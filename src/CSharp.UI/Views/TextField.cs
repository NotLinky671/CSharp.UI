using System.Globalization;

namespace CSharp.UI;

/// <summary>
/// 单行文本输入框：绑定字符串，支持已提交 Unicode 文本、字素边界光标移动、选择、剪贴板与横向滚动。
/// </summary>
/// <remarks>
/// <para>
/// 光标与选择状态保存在运行时节点上，因此组件重新构建后保持不变；
/// 外部绑定文本变化或窗口尺寸变化时，选择会被限制到有效的字素边界。
/// </para>
/// <para>
/// 首版不实现完整 IME 组合文本与候选窗定位：只接受宿主提交的文本（例如中文输入法上屏后的结果）
/// 与剪贴板粘贴，不承诺组合期间的中间状态。
/// </para>
/// </remarks>
public sealed class TextField : View
{
    /// <summary>由字符串绑定创建。</summary>
    public TextField(Binding<string> text, string? placeholder = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        Text = text;
        Placeholder = placeholder;
    }

    /// <summary>由状态创建。</summary>
    public TextField(State<string> state, string? placeholder = null)
        : this(state.ToBinding(), placeholder)
    {
    }

    /// <summary>字符串绑定。</summary>
    public Binding<string> Text { get; }

    /// <summary>空文本时显示的占位提示。</summary>
    public string? Placeholder { get; }

    /// <summary>显式宽度；null 表示使用默认宽度或填满可用宽度。</summary>
    public double? Width { get; init; }

    /// <summary>控件高度。</summary>
    public double Height { get; init; } = 32;

    /// <summary>水平内边距。</summary>
    public double HorizontalPadding { get; init; } = 10;

    /// <summary>圆角半径。</summary>
    public double CornerRadius { get; init; } = 6;

    /// <summary>最大字符数（UTF-16 长度）；默认不限制。</summary>
    public int MaxLength { get; init; } = int.MaxValue;

    /// <summary>光标闪烁周期的一半（亮/灭各持续该时长）。</summary>
    public TimeSpan BlinkInterval { get; init; } = TimeSpan.FromMilliseconds(530);

    /// <inheritdoc />
    public override string DebugName => $"TextField({Placeholder ?? "text"})";

    /// <inheritdoc />
    public override bool IsFocusable => true;

    /// <summary>返回替换绑定的副本（协调更新时使用新绑定）。</summary>
    public TextField With(Binding<string> text) => new(text, Placeholder)
    {
        Width = Width,
        Height = Height,
        HorizontalPadding = HorizontalPadding,
        CornerRadius = CornerRadius,
        MaxLength = MaxLength,
        BlinkInterval = BlinkInterval,
    };

    internal override void Measure(ViewNode node, LayoutConstraints constraints, LayoutContext context)
    {
        var text = Text.Read(node) ?? string.Empty;
        var font = node.Environment.Font ?? FontDescription.Default;
        var layout = TextPainter.Layout(context, text, font, double.PositiveInfinity, context.PixelScale);
        node.ViewCache = new TextFieldCache(text, font, layout);
        var width = Width ?? (constraints.IsWidthTight
            ? constraints.MaxWidth
            : Math.Clamp(layout.Size.Width + (HorizontalPadding * 2), 140, constraints.HasBoundedWidth ? constraints.MaxWidth : double.PositiveInfinity));
        node.DesiredSize = constraints.Constrain(new Size(width, Height));
    }

    internal override void Emit(ViewNode node, DrawContext context)
    {
        if (node.Frame.IsEmpty)
        {
            return;
        }

        var theme = node.Environment.ResolvedTheme;
        var disabled = node.IsEffectivelyDisabled;
        var state = GetState(node);
        var layout = GetLayout(node, context.TextService);
        var text = layout.Text;

        // 背景与边框。
        var background = disabled
            ? theme.DisabledBackground
            : node.IsFocused ? theme.Surface : theme.ControlBackground;
        var stroke = disabled ? theme.Border : node.IsFocused ? theme.FocusRing : theme.Border;
        context.Commands.Add(new RectangleCommand(
            node.Frame,
            background,
            CornerRadius,
            stroke,
            node.IsFocused ? 2 : 1,
            node.Environment.Opacity));

        var inner = InnerRect(node.Frame);
        var opacity = node.Environment.Opacity;

        if (string.IsNullOrEmpty(text) && !string.IsNullOrEmpty(Placeholder) && !state.HasSelection)
        {
            var font = node.Environment.Font ?? FontDescription.Default;
            var placeholder = TextPainter.Layout(context, node, Placeholder, font, double.PositiveInfinity, context.PixelScale);
            TextPainter.Paint(context, node, placeholder, theme.MutedForeground, inner, TextAlignment.Left, 0.85);
        }

        var restore = context.CurrentClip;
        if (!context.PushClip(inner))
        {
            return;
        }

        var originX = inner.X - state.ScrollX;
        var originY = inner.Y;

        // 选择高亮。
        if (state.HasSelection && node.IsFocused)
        {
            var selection = layout.GetRangeBounds(state.SelectionStart, state.SelectionEnd);
            if (selection.Width > 0 || selection.Height > 0)
            {
                context.Commands.Add(new RectangleCommand(
                    new Rect(originX + selection.X, originY + selection.Y, Math.Max(selection.Width, 1), selection.Height),
                    theme.SelectionBackground,
                    2,
                    null,
                    0,
                    opacity));
            }
        }

        var textColor = disabled
            ? theme.DisabledForeground
            : theme.Foreground;
        var textBounds = new Rect(originX, originY, layout.Size.Width, inner.Height);
        TextPainter.Paint(context, node, layout, textColor, textBounds, TextAlignment.Left);

        // 光标。
        if (node.IsFocused && IsCaretVisible(node, state))
        {
            var caretIndex = layout.SnapToCaretStop(Math.Clamp(state.Caret, 0, text.Length));
            var caret = layout.GetCaretBounds(caretIndex, 1.5);
            context.Commands.Add(new RectangleCommand(
                new Rect(originX + caret.X, originY + caret.Y, caret.Width, caret.Height),
                textColor,
                0,
                null,
                0,
                opacity));
        }

        context.PopClip(restore);
    }

    internal override bool OnPointerDown(InputContext context, ViewNode node, PointerEvent e)
    {
        if (node.IsEffectivelyDisabled)
        {
            return false;
        }

        var state = GetState(node);
        context.Runtime.SetFocus(node);
        state.IsDragging = true;
        state.Caret = IndexFromPosition(node, e.Position);
        state.Anchor = state.Caret;
        state.BlinkEpoch = node.Runtime.Clock.Now;
        EnsureCaretVisible(node, state);
        context.MarkHandled();
        return true;
    }

    internal override bool OnPointerMove(InputContext context, ViewNode node, PointerEvent e)
    {
        var state = GetState(node);
        if (!state.IsDragging)
        {
            return false;
        }

        state.Caret = IndexFromPosition(node, e.Position);
        EnsureCaretVisible(node, state);
        context.MarkHandled();
        return true;
    }

    internal override bool OnPointerUp(InputContext context, ViewNode node, PointerEvent e)
    {
        var state = GetState(node);
        if (!state.IsDragging)
        {
            return false;
        }

        state.IsDragging = false;
        state.Caret = IndexFromPosition(node, e.Position);
        return true;
    }

    internal override bool OnPointerCancel(InputContext context, ViewNode node)
    {
        GetState(node).IsDragging = false;
        return false;
    }

    internal override bool OnPointerEnter(InputContext context, ViewNode node, PointerEvent e)
    {
        context.Runtime.InvalidateRender();
        return false;
    }

    internal override bool OnPointerLeave(InputContext context, ViewNode node, PointerEvent e)
    {
        context.Runtime.InvalidateRender();
        return false;
    }

    internal override void OnFocusChanged(InputContext context, ViewNode node, bool focused)
    {
        var state = GetState(node);
        state.BlinkEpoch = node.Runtime.Clock.Now;
        state.IsDragging = false;
        if (focused)
        {
            var text = Text.Read(node) ?? string.Empty;
            state.Caret = ClampIndex(node, state.Caret, text);
            state.Anchor = ClampIndex(node, state.Anchor, text);
            EnsureCaretVisible(node, state);
        }
        else
        {
            // 失去焦点后停止闪烁，并取消选择（首版不与其它控件共享选择）。
            state.Anchor = state.Caret;
        }

        context.Runtime.InvalidateRender();
    }

    internal override bool OnTextInput(InputContext context, ViewNode node, TextInputEvent e)
    {
        if (!node.IsFocused || node.IsEffectivelyDisabled)
        {
            return false;
        }

        var inserted = TextInputEvent.SanitizeSingleLine(e.Text);
        if (inserted.Length == 0)
        {
            return true;
        }

        var state = GetState(node);
        var text = Text.Read(node) ?? string.Empty;
        if (state.HasSelection)
        {
            text = ReplaceSelection(state, text, inserted);
        }
        else
        {
            var caret = ClampIndex(node, state.Caret, text);
            if (text.Length + inserted.Length > MaxLength)
            {
                var allowed = Math.Max(0, MaxLength - text.Length);
                inserted = inserted[..Math.Min(allowed, inserted.Length)];
            }

            text = string.Concat(text.AsSpan(0, caret), inserted, text.AsSpan(caret));
            state.Caret = caret + inserted.Length;
            state.Anchor = state.Caret;
        }

        Text.Value = text;
        state.BlinkEpoch = node.Runtime.Clock.Now;
        EnsureCaretVisible(node, state);
        context.MarkHandled();
        return true;
    }

    internal override bool OnKeyDown(InputContext context, ViewNode node, KeyEvent e)
    {
        if (!node.IsFocused || node.IsEffectivelyDisabled)
        {
            return false;
        }

        var state = GetState(node);
        var text = Text.Read(node) ?? string.Empty;
        var extend = e.Modifiers.HasFlag(KeyModifiers.Shift);
        var command = e.IsCommandPressed;
        state.BlinkEpoch = node.Runtime.Clock.Now;

        switch (e.Key)
        {
            case UiKey.Left:
                MoveCaret(node, state, text, backward: true, extend);
                return true;
            case UiKey.Right:
                MoveCaret(node, state, text, backward: false, extend);
                return true;
            case UiKey.Home:
                SetCaret(node, state, 0, extend);
                return true;
            case UiKey.End:
                SetCaret(node, state, text.Length, extend);
                return true;
            case UiKey.Backspace:
                if (!state.HasSelection && state.Caret == 0)
                {
                    return true;
                }

                DeleteSelectionOrGrapheme(node, state, text, backward: true);
                return true;
            case UiKey.Delete:
                if (!state.HasSelection && state.Caret >= text.Length)
                {
                    return true;
                }

                DeleteSelectionOrGrapheme(node, state, text, backward: false);
                return true;
            case UiKey.A when command:
                state.Anchor = 0;
                state.Caret = text.Length;
                context.MarkHandled();
                return true;
            case UiKey.C when command:
                CopySelection(node, state, text, cut: false);
                return true;
            case UiKey.X when command:
                CopySelection(node, state, text, cut: true);
                return true;
            case UiKey.V when command:
                PasteFromClipboard(node, state, text);
                return true;
            default:
                return false;
        }
    }

    internal override TimeSpan? GetNextWakeup(ViewNode node, TimeSpan now)
    {
        if (!node.IsFocused)
        {
            return null;
        }

        var period = BlinkInterval.Ticks * 2;
        if (period <= 0)
        {
            return null;
        }

        var elapsed = now.Ticks - GetState(node).BlinkEpoch.Ticks;
        var phase = ((elapsed % period) + period) % period;
        return TimeSpan.FromTicks(period - phase);
    }

    internal override void OnMount(ViewNode node)
    {
        var state = GetState(node);
        state.BlinkEpoch = node.Runtime.Clock.Now;
    }

    internal override void OnUpdate(ViewNode node, View previous)
    {
        // 外部绑定变化后把光标与选择限制到有效范围（字素吸附在绘制/交互时完成）。
        if (node.InteractionState is not TextFieldState state)
        {
            return;
        }

        var text = Text.Read(node) ?? string.Empty;
        state.Caret = Math.Clamp(state.Caret, 0, text.Length);
        state.Anchor = Math.Clamp(state.Anchor, 0, text.Length);
    }

    internal override bool IsLayoutEquivalent(View other) => false;

    private bool IsCaretVisible(ViewNode node, TextFieldState state)
    {
        var period = BlinkInterval.Ticks * 2;
        if (period <= 0 || !node.IsFocused)
        {
            return false;
        }

        var elapsed = node.Runtime.Clock.Now.Ticks - state.BlinkEpoch.Ticks;
        var phase = ((elapsed % period) + period) % period;
        return phase < BlinkInterval.Ticks;
    }

    private TextLayoutResult GetLayout(ViewNode node, ITextService textService)
    {
        var text = Text.Read(node) ?? string.Empty;
        var font = node.Environment.Font ?? FontDescription.Default;
        if (node.ViewCache is TextFieldCache cache && cache.Text == text && Equals(cache.Font, font))
        {
            return cache.Layout;
        }

        var request = TextPainter.CreateRequest(text, font, double.PositiveInfinity, node.Runtime.PixelScale);
        var layout = textService.Layout(request);
        node.ViewCache = new TextFieldCache(text, font, layout);
        return layout;
    }

    private Rect InnerRect(Rect frame)
    {
        var inner = frame.Deflate(new Thickness(HorizontalPadding, 0));
        // 避免描边与文本重叠。
        return inner.Deflate(new Thickness(0, 2));
    }

    private int IndexFromPosition(ViewNode node, Point position)
    {
        var layout = GetLayout(node, node.Runtime.Services.TextService);
        var inner = InnerRect(node.Frame);
        var state = GetState(node);
        var local = new Point(position.X - inner.X + state.ScrollX, position.Y - inner.Y);
        return layout.SnapToCaretStop(layout.HitTestIndex(local));
    }

    private int ClampIndex(ViewNode node, int index, string text)
    {
        var clamped = Math.Clamp(index, 0, text.Length);
        var layout = GetLayout(node, node.Runtime.Services.TextService);
        return layout.Text == text ? layout.SnapToCaretStop(clamped) : clamped;
    }

    private void MoveCaret(ViewNode node, TextFieldState state, string text, bool backward, bool extend)
    {
        var layout = GetLayout(node, node.Runtime.Services.TextService);
        var current = layout.SnapToCaretStop(Math.Clamp(state.Caret, 0, text.Length));
        var stops = layout.CaretStops;
        var position = TextLayoutResult.FindStop(stops, current);
        var index = position >= 0 ? position : Math.Max(0, ~position - 1);
        var next = backward ? Math.Max(0, index - 1) : Math.Min(stops.Count - 1, index + 1);
        SetCaret(node, state, stops[next], extend, layout);
    }

    private void SetCaret(ViewNode node, TextFieldState state, int index, bool extend, TextLayoutResult? layout = null)
    {
        layout ??= GetLayout(node, node.Runtime.Services.TextService);
        var snapped = layout.SnapToCaretStop(index);
        state.Caret = snapped;
        if (!extend)
        {
            state.Anchor = snapped;
        }

        EnsureCaretVisible(node, state);
    }

    private void DeleteSelectionOrGrapheme(ViewNode node, TextFieldState state, string text, bool backward)
    {
        var layout = GetLayout(node, node.Runtime.Services.TextService);
        string next;
        if (state.HasSelection)
        {
            var start = layout.SnapToCaretStop(state.SelectionStart);
            var end = layout.SnapToCaretStop(state.SelectionEnd, roundUp: true);
            next = string.Concat(text.AsSpan(0, start), text.AsSpan(end));
            state.Caret = start;
            state.Anchor = start;
        }
        else
        {
            var caret = layout.SnapToCaretStop(Math.Clamp(state.Caret, 0, text.Length));
            if (backward)
            {
                var previous = PreviousBoundary(layout, caret);
                next = string.Concat(text.AsSpan(0, previous), text.AsSpan(caret));
                state.Caret = previous;
            }
            else
            {
                var following = NextBoundary(layout, caret);
                next = string.Concat(text.AsSpan(0, caret), text.AsSpan(following));
            }

            state.Anchor = state.Caret;
        }

        Text.Value = next;
        node.Runtime.InvalidateRender();
    }

    private static int PreviousBoundary(TextLayoutResult layout, int index)
    {
        for (var i = layout.CaretStops.Count - 1; i >= 0; i--)
        {
            if (layout.CaretStops[i] < index)
            {
                return layout.CaretStops[i];
            }
        }

        return 0;
    }

    private static int NextBoundary(TextLayoutResult layout, int index)
    {
        foreach (var stop in layout.CaretStops)
        {
            if (stop > index)
            {
                return stop;
            }
        }

        return layout.Text.Length;
    }

    private string ReplaceSelection(TextFieldState state, string text, string inserted)
    {
        var start = Math.Clamp(state.SelectionStart, 0, text.Length);
        var end = Math.Clamp(state.SelectionEnd, 0, text.Length);
        var result = string.Concat(text.AsSpan(0, start), inserted, text.AsSpan(end));
        if (result.Length > MaxLength)
        {
            result = result[..MaxLength];
        }

        state.Caret = Math.Min(start + inserted.Length, result.Length);
        state.Anchor = state.Caret;
        return result;
    }

    private void CopySelection(ViewNode node, TextFieldState state, string text, bool cut)
    {
        if (!state.HasSelection)
        {
            return;
        }

        var clipboard = node.Runtime.Services.Clipboard;
        var start = Math.Clamp(state.SelectionStart, 0, text.Length);
        var end = Math.Clamp(state.SelectionEnd, 0, text.Length);
        var selected = text[start..end];
        if (clipboard is not null && selected.Length > 0)
        {
            clipboard.SetText(selected);
        }

        if (cut && selected.Length > 0)
        {
            var next = string.Concat(text.AsSpan(0, start), text.AsSpan(end));
            Text.Value = next;
            state.Caret = start;
            state.Anchor = start;
        }
    }

    private void PasteFromClipboard(ViewNode node, TextFieldState state, string text)
    {
        var clipboard = node.Runtime.Services.Clipboard;
        var pasted = clipboard?.GetText();
        if (string.IsNullOrEmpty(pasted))
        {
            return;
        }

        pasted = TextInputEvent.SanitizeSingleLine(pasted);
        if (pasted.Length == 0)
        {
            return;
        }

        if (state.HasSelection)
        {
            Text.Value = ReplaceSelection(state, text, pasted);
        }
        else
        {
            var caret = Math.Clamp(state.Caret, 0, text.Length);
            var next = string.Concat(text.AsSpan(0, caret), pasted, text.AsSpan(caret));
            if (next.Length > MaxLength)
            {
                next = next[..MaxLength];
            }

            state.Caret = Math.Min(caret + pasted.Length, next.Length);
            state.Anchor = state.Caret;
            Text.Value = next;
        }

        EnsureCaretVisible(node, state);
    }

    private void EnsureCaretVisible(ViewNode node, TextFieldState state)
    {
        var layout = GetLayout(node, node.Runtime.Services.TextService);
        var inner = InnerRect(node.Frame);
        if (inner.Width <= 0)
        {
            return;
        }

        var caret = layout.SnapToCaretStop(Math.Clamp(state.Caret, 0, layout.Text.Length));
        var caretX = layout.GetCaretX(caret);
        var maxScroll = Math.Max(0, layout.Size.Width - inner.Width);

        if (caretX - state.ScrollX > inner.Width)
        {
            state.ScrollX = caretX - inner.Width;
        }
        else if (caretX - state.ScrollX < 0)
        {
            state.ScrollX = caretX;
        }

        state.ScrollX = Math.Clamp(state.ScrollX, 0, maxScroll);
    }

    private static TextFieldState GetState(ViewNode node)
    {
        if (node.InteractionState is TextFieldState state)
        {
            return state;
        }

        state = new TextFieldState();
        node.InteractionState = state;
        return state;
    }

    private sealed class TextFieldCache(string text, FontDescription font, TextLayoutResult layout)
    {
        public string Text { get; } = text;

        public FontDescription Font { get; } = font;

        public TextLayoutResult Layout { get; } = layout;
    }
}

/// <summary>TextField 的运行时编辑状态（保存在节点上，组件重建后保持）。</summary>
internal sealed class TextFieldState
{
    /// <summary>光标位置的 UTF-16 索引（始终位于字素边界）。</summary>
    public int Caret { get; set; }

    /// <summary>选择锚点；与 <see cref="Caret"/> 相同表示无选择。</summary>
    public int Anchor { get; set; }

    /// <summary>水平滚动偏移（逻辑像素）。</summary>
    public double ScrollX { get; set; }

    /// <summary>光标闪烁计时起点。</summary>
    public TimeSpan BlinkEpoch { get; set; }

    /// <summary>是否正在用鼠标拖选。</summary>
    public bool IsDragging { get; set; }

    /// <summary>选择起点。</summary>
    public int SelectionStart => Math.Min(Caret, Anchor);

    /// <summary>选择终点（不含）。</summary>
    public int SelectionEnd => Math.Max(Caret, Anchor);

    /// <summary>是否存在选择。</summary>
    public bool HasSelection => Caret != Anchor;
}
