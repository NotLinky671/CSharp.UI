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
    /// <summary>未显式指定宽度时的首选最小宽度（逻辑像素）；父级可用宽度更小时以父级为准。</summary>
    private const double DefaultMinimumWidth = 140;

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

        // 默认最小宽度不得突破父级上限：把 140 与可用宽度取较小值，保证 Clamp 的上下限始终合法。
        var available = constraints.HasBoundedWidth ? Math.Max(0, constraints.MaxWidth) : double.PositiveInfinity;
        var preferred = layout.Size.Width + (HorizontalPadding * 2);
        var minimum = Math.Min(DefaultMinimumWidth, available);
        var width = Width ?? (constraints.IsWidthTight
            ? constraints.MaxWidth
            : Math.Clamp(preferred, minimum, available));
        node.DesiredSize = constraints.Constrain(new Size(width, Height));
    }

    internal override void Arrange(ViewNode node, Rect bounds, LayoutContext context)
    {
        node.Frame = bounds;

        // 排列阶段真实观测绑定值：外部写入可能在没有任何描述重建的情况下改变文本，
        // 因此这里（以及其它安全入口）都要把会话与选区同步到实际回读值。
        // 只读绑定并调整节点内部状态，不写 Binding、不产生外部副作用。
        if (node.InteractionState is TextFieldState state)
        {
            SyncEditor(node, state, invalidate: false);

            // 弹性分配、窗口缩放或断点切换都会改变字段宽度：每次排列后重新保证光标可见，
            // 否则旧的 ScrollX 会把光标留在可视区之外（绘制阶段的夹取只是最后防线，不能掩盖错位）。
            // 只调整内部滚动偏移，不写绑定值、不改光标与选区。
            if (node.IsFocused)
            {
                EnsureCaretVisible(node, state);
            }
        }
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

        // 纯重绘入口也要观测真实绑定值：外部写入可能只请求重绘（没有描述重建、没有重新布局）。
        // 复用本次排版结果，不递归 GetLayout，也不在这里失效布局。
        SyncEditor(node, state, invalidate: false, knownLayout: layout);

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

        // 纯重绘观测可能刚刚同步了外部写入的新文档：立刻用同一排版与内容区把横向滚动夹回/保证光标可见，
        // 否则旧 ScrollX 会把缩短后的文字整个移出可见区域（不递归 GetLayout、不失效布局）。
        EnsureCaretVisible(node, state, layout, inner);

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

        // 文字纹理在内容区内垂直居中，因此光标、选区与点击命中都必须从同一个文本原点出发；
        // 直接用 inner.Y 会让光标随输入框变高/字号变大而向上错位。
        // 该原点再统一吸附到物理像素：文字纹理、光标、选区与点击命中共用同一变换（见 TextPainter.ResolveOrigin）。
        var textBounds = new Rect(
            inner.X - state.ScrollX,
            TextPainter.VerticalOrigin(inner, layout.Size),
            layout.Size.Width,
            layout.Size.Height);
        var origin = TextPainter.ResolveOrigin(textBounds, layout.Size, TextAlignment.Left, layout.PixelScale);
        var originX = origin.X;
        var originY = origin.Y;

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

        // 传入已居中的文本原点（高度取排版高度，Paint 的垂直居中偏移因此为 0），
        // 保证纹理、光标与选区使用同一坐标约定。
        TextPainter.Paint(context, node, layout, textColor, textBounds, TextAlignment.Left);

        // 光标。
        if (node.IsFocused && IsCaretVisible(node, state))
        {
            var caretIndex = layout.SnapToCaretStop(Math.Clamp(state.Caret, 0, text.Length));
            var caret = layout.GetCaretBounds(caretIndex, 1.5);
            var caretRect = new Rect(originX + caret.X, originY + caret.Y, caret.Width, caret.Height);

            // 共用吸附后的文本原点；光标是矢量矩形而不是纹理，不需要纹素对齐。
            // 文本末尾 + 最大横向滚动时，吸附可能把光标推到内容区外不足 1 像素而整条不可见，
            // 因此把光标夹取回内容区，保证它始终可见；末端偏移包含光标宽度和原点吸附量，
            // 内部未被裁剪的光标仍与文字原点及排版位置精确一致。
            if (caretRect.Width <= inner.Width)
            {
                caretRect = caretRect with { X = Math.Clamp(caretRect.X, inner.Left, inner.Right - caretRect.Width) };
            }

            context.Commands.Add(new RectangleCommand(
                caretRect,
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
        SyncEditor(node, state, invalidate: true);
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

        SyncEditor(node, state, invalidate: true);
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
        SyncEditor(node, state, invalidate: true);
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
            // 焦点变化是安全入口：先按真实绑定值同步会话（外部写入可能已发生），再限制选区。
            SyncEditor(node, state, invalidate: true);
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

        // 单行净化先行：全部被过滤（例如只有换行/制表符）时不产生事务，也不写绑定。
        var inserted = TextInputEvent.SanitizeSingleLine(e.Text);
        if (inserted.Length == 0)
        {
            context.MarkHandled();
            return true;
        }

        var state = GetState(node);
        SyncEditor(node, state, invalidate: false);
        var range = CurrentEditRange(node, state);
        var operation = new EditOperation(range, inserted, TextEditKind.Typing, TextEditReason.UserInput);
        ApplyEdit(node, state, operation);
        state.BlinkEpoch = node.Runtime.Clock.Now;
        context.MarkHandled();
        return true;
    }

    /// <summary>
    /// 当前编辑范围：有选区时用吸附到字素边界的选择范围（保留方向语义，逆序选择也取规范范围），
    /// 否则用折叠光标位置的空范围。容量 / 整字素截断由编辑会话在真实文本上完成。
    /// </summary>
    private TextRange CurrentEditRange(ViewNode node, TextFieldState state)
    {
        if (state.HasSelection)
        {
            var layout = GetLayout(node, node.Runtime.Services.TextService);
            return SelectionRange(layout, state);
        }

        var text = Text.Read(node) ?? string.Empty;
        return TextRange.FromLength(ClampIndex(node, state.Caret, text), 0);
    }

    private static TextRange SelectionRange(TextLayoutResult layout, TextFieldState state)
    {
        // 起点向上吸附、终点向下吸附：范围只会包含完整字素，不会吞掉选区外的半个字素。
        var start = layout.SnapToCaretStop(Math.Clamp(state.SelectionStart, 0, layout.Text.Length));
        var end = layout.SnapToCaretStop(Math.Clamp(state.SelectionEnd, 0, layout.Text.Length), roundUp: true);
        return new TextRange(start, end);
    }

    /// <summary>
    /// 通过统一编辑会话执行一次原子替换：写一次绑定、回读确认，只有实际改变文档时才提交选区与滚动。
    /// setter 拒绝、容量整体拒绝或回读与操作前一致时都不移动选区、不推进版本。
    /// </summary>
    private void ApplyEdit(ViewNode node, TextFieldState state, EditOperation operation)
    {
        // 事务期间协调更新是延迟的，node.View 通常仍是本实例；仍按当前视图读取绑定，避免使用过时的描述。
        if (node.View is not TextField field)
        {
            return;
        }

        var binding = field.Text;

        EditResult result;
        try
        {
            result = state.Editor.Apply(
                binding.Identity,
                field.MaxLength,
                operation,
                new TextSelection(state.Anchor, state.Caret),
                () => binding.Read(node) ?? string.Empty,
                value => binding.Value = value);
        }
        catch
        {
            // setter 已改写或回读失败：按会话当前真实文本恢复选区/布局，但不掩盖原异常。
            RecoverAfterTransactionFailure(node, state);
            throw;
        }

        // 事务结束时重新核对当前挂载视图：setter 期间可能已同步重建、换用新来源/新 MaxLength，
        // 甚至整个节点已不再是本输入框。旧事务不得把结果与选区串写到已经不存在的新客户端。
        if (!node.IsMounted || node.View is not TextField mounted)
        {
            return;
        }

        if (!ReferenceEquals(mounted.Text.Identity, binding.Identity) || mounted.MaxLength != field.MaxLength)
        {
            SyncEditor(node, state, invalidate: true);
            return;
        }

        if (!result.Changed)
        {
            // 拒绝 / 未变化：不做投机选区提交，但把会话与选区对齐到真实回读值（外部变化可能已发生）。
            SyncEditor(node, state, invalidate: false);
            return;
        }

        // 回读值才是唯一事实来源：会话已按实际文本吸附光标，这里沿用其结果快照。
        state.Caret = result.After.Selection.Active;
        state.Anchor = result.After.Selection.Anchor;
        node.Runtime.InvalidateRender();
        EnsureCaretVisible(node, state);
    }

    /// <summary>
    /// 事务抛错后的恢复：按真实回读文本同步会话并限制选区，使布局/渲染失效。
    /// 次生读取异常只标记 <see cref="TextEditSession.RequiresResync"/>，绝不覆盖原始异常。
    /// </summary>
    private void RecoverAfterTransactionFailure(ViewNode node, TextFieldState state)
    {
        if (node.View is not TextField field)
        {
            // 节点已不再是本输入框：没有可读来源，只标记待重同步并请求重绘。
            state.Editor.MarkRequiresResync();
            node.Runtime.InvalidateRender();
            return;
        }

        try
        {
            var text = field.Text.Read(node) ?? string.Empty;
            state.Editor.Sync(field.Text.Identity, text, field.MaxLength);
            ClampSelectionToText(node, state, text);
            node.Runtime.InvalidateLayout(node);
        }
        catch
        {
            state.Editor.MarkRequiresResync();
            node.Runtime.InvalidateRender();
        }
    }

    /// <summary>
    /// 安全入口（挂载/描述更新/排列/绘制/焦点/指针/编辑）统一调用：读取真实绑定值，
    /// 识别自身确认与外部文本/来源/MaxLength 变化，必要时把选区吸附到真实文本。
    /// 不写 Binding、不产生外部副作用；已提供排版结果时直接复用，避免递归重排。
    /// </summary>
    private void SyncEditor(ViewNode node, TextFieldState state, bool invalidate, TextLayoutResult? knownLayout = null)
    {
        if (node.View is not TextField field)
        {
            return;
        }

        var binding = field.Text;
        var text = knownLayout?.Text ?? binding.Read(node) ?? string.Empty;

        if (state.Editor.IsOwnPendingWrite(text) && state.Editor.MatchesContext(binding.Identity, field.MaxLength))
        {
            // 本次读取正是自己的事务写入、且来源与配置未变：不能当成外部变化清空版本或移动选区。
            return;
        }

        var sync = state.Editor.Sync(binding.Identity, text, field.MaxLength);
        if (!sync.HasExternalChange)
        {
            return;
        }

        knownLayout ??= GetLayout(node, node.Runtime.Services.TextService);
        state.Caret = knownLayout.SnapToCaretStop(Math.Clamp(state.Caret, 0, text.Length));
        state.Anchor = knownLayout.SnapToCaretStop(Math.Clamp(state.Anchor, 0, text.Length));
        if (invalidate)
        {
            node.Runtime.InvalidateLayout(node);
        }
    }

    /// <summary>把光标与选择限制到真实文本并吸附到字素边界（不写绑定）。</summary>
    private void ClampSelectionToText(ViewNode node, TextFieldState state, string text)
    {
        var layout = GetLayout(node, node.Runtime.Services.TextService);
        state.Caret = layout.SnapToCaretStop(Math.Clamp(state.Caret, 0, text.Length));
        state.Anchor = layout.SnapToCaretStop(Math.Clamp(state.Anchor, 0, text.Length));
    }

    internal override bool OnKeyDown(InputContext context, ViewNode node, KeyEvent e)
    {
        if (!node.IsFocused || node.IsEffectivelyDisabled)
        {
            return false;
        }

        var state = GetState(node);
        SyncEditor(node, state, invalidate: false);
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

                DeleteSelectionOrGrapheme(node, state, text, backward: true, TextEditKind.Backspace);
                return true;
            case UiKey.Delete:
                if (!state.HasSelection && state.Caret >= text.Length)
                {
                    return true;
                }

                DeleteSelectionOrGrapheme(node, state, text, backward: false, TextEditKind.Delete);
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

        // 返回距离下一个“亮/灭切换边界”的时间：0—半周期为亮，半周期—整周期为灭。
        // 用整周期计算会让宿主在灭灯边界不重绘，光标就不会闪烁。
        var remaining = phase < BlinkInterval.Ticks
            ? BlinkInterval.Ticks - phase
            : period - phase;
        return TimeSpan.FromTicks(Math.Max(1, remaining));
    }

    internal override void OnMount(ViewNode node)
    {
        var state = GetState(node);
        state.BlinkEpoch = node.Runtime.Clock.Now;

        // 建立首次确认快照：绑定来源身份、已提交文本与 MaxLength 的基线。
        SyncEditor(node, state, invalidate: false);
    }

    internal override void OnUpdate(ViewNode node, View previous)
    {
        if (node.InteractionState is not TextFieldState state)
        {
            return;
        }

        // 外部文本 / 来源替换 / MaxLength 变化：同步会话并把选区限制到新文本。
        // 同来源同配置、且文本正是本事务正在写入的候选时，SyncEditor 会识别为自身确认并跳过。
        SyncEditor(node, state, invalidate: false);
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

        // 命中坐标与绘制共用同一文本原点（垂直居中 + 横向滚动偏移，并同样吸附到物理像素）。
        var textBounds = new Rect(
            inner.X - state.ScrollX,
            TextPainter.VerticalOrigin(inner, layout.Size),
            layout.Size.Width,
            layout.Size.Height);
        var origin = TextPainter.ResolveOrigin(textBounds, layout.Size, TextAlignment.Left, layout.PixelScale);
        var local = new Point(position.X - origin.X, position.Y - origin.Y);
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

    private void DeleteSelectionOrGrapheme(ViewNode node, TextFieldState state, string text, bool backward, TextEditKind kind)
    {
        var layout = GetLayout(node, node.Runtime.Services.TextService);
        TextRange range;
        if (state.HasSelection)
        {
            range = SelectionRange(layout, state);
        }
        else
        {
            var caret = layout.SnapToCaretStop(Math.Clamp(state.Caret, 0, text.Length));
            range = backward
                ? new TextRange(PreviousBoundary(layout, caret), caret)
                : new TextRange(caret, NextBoundary(layout, caret));
        }

        ApplyEdit(node, state, EditOperation.Delete(range, kind, TextEditReason.UserInput));
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

    private void CopySelection(ViewNode node, TextFieldState state, string text, bool cut)
    {
        if (!state.HasSelection)
        {
            return;
        }

        var layout = GetLayout(node, node.Runtime.Services.TextService);
        var range = SelectionRange(layout, state).Clamp(text.Length);
        var selected = text[range.Start..range.End];
        var clipboard = node.Runtime.Services.Clipboard;
        if (clipboard is not null && selected.Length > 0)
        {
            clipboard.SetText(selected);
        }

        if (cut && selected.Length > 0)
        {
            ApplyEdit(node, state, EditOperation.Delete(range, TextEditKind.Cut, TextEditReason.Clipboard));
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

        // 单行规则过滤后为空时不产生事务（既有行为：不写绑定）。
        if (TextInputEvent.SanitizeSingleLine(pasted).Length == 0)
        {
            return;
        }

        var range = CurrentEditRange(node, state);
        if (!EditOperation.TryCreate(
                range,
                pasted,
                TextEditKind.Paste,
                TextEditReason.Clipboard,
                scrubSingleLine: true,
                out var operation,
                out _))
        {
            return;
        }

        ApplyEdit(node, state, operation);
    }

    /// <summary>
    /// 保证光标在内容区可见：只调整 <see cref="TextFieldState.ScrollX"/>，不写绑定、不改光标与选区。
    /// 可复用调用方已有的排版结果与内容区矩形，供排列与纯重绘观测路径共用而不递归重排。
    /// </summary>
    private void EnsureCaretVisible(ViewNode node, TextFieldState state, TextLayoutResult? knownLayout = null, Rect? knownInner = null)
    {
        var layout = knownLayout ?? GetLayout(node, node.Runtime.Services.TextService);
        var inner = knownInner ?? InnerRect(node.Frame);
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
    /// <summary>原子编辑会话：确认文本、文档版本、来源身份与失效接缝（TI-03）。</summary>
    public TextEditSession Editor { get; } = new();

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
