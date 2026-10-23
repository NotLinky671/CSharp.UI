namespace CSharp.UI;

/// <summary>
/// 多行文本编辑控件（TE-03）：可聚焦、可滚动、支持换行/选区/撤销重做的 TextField 多行对应物。
/// </summary>
/// <remarks>
/// <para>
/// 与 <see cref="TextField"/> 的区别：内容是真实多行文档（<c>\n</c> 分段 + 按内容区宽软折行），
/// 高度固定为 <see cref="MinLines"/> 行视口（超高内容内部纵向滚动，不按全文增高）；
/// caret 是二维位置（行 + 列，跨行导航保留期望 X 与 <see cref="TextAffinity"/>），
/// 滚动是纵向 <see cref="TextEditorState.ScrollY"/>。
/// </para>
/// <para>
/// 编辑协议与 <see cref="TextField"/> 共享同一通道：原子事务走 <see cref="TextEditSession.Apply"/>，
/// 历史走 <see cref="TextEditHistory"/>，命令分发走 <see cref="TextEditCommandResolver"/>，
/// 粘贴/已提交输入经 <see cref="TextInputEvent.SanitizeMultiLine"/> 归一化。
/// IME 组合经共享的 <see cref="TextCompositionClient"/> 接入（TE-04）：预编辑只存在于显示串，
/// 结果经 <see cref="ApplyCompositionCommit"/> 一次真实事务落地；焦点/失活/卸载/外部变化/
/// 编辑命令/鼠标改选区的“先取消组合”接缝与 TextField 一致。
/// </para>
/// </remarks>
public sealed class TextEditor : View
{
    /// <summary>未显式指定宽度时的首选最小宽度（逻辑像素）；父级可用宽度更小时以父级为准。</summary>
    private const double DefaultMinimumWidth = 140;

    /// <summary>拖选越界时自动滚动的固定步进（一行/tick）；是调度常量，不是性能承诺。</summary>
    private static readonly TimeSpan DragScrollTick = TimeSpan.FromMilliseconds(80);

    /// <summary>空选区时行末窄 marker 的绘制宽（几何返回 0，绘制层涂成可见窄块）。</summary>
    private const double SelectionLineEndMarkerWidth = 2;

    /// <summary>由字符串绑定创建。</summary>
    public TextEditor(Binding<string> text, string? placeholder = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        Text = text;
        Placeholder = placeholder;
    }

    /// <summary>由状态创建。</summary>
    public TextEditor(State<string> state, string? placeholder = null)
        : this(state.ToBinding(), placeholder)
    {
    }

    /// <summary>字符串绑定。</summary>
    public Binding<string> Text { get; }

    /// <summary>空文本时显示的占位提示。</summary>
    public string? Placeholder { get; }

    /// <summary>显式宽度；null 表示使用默认宽度或填满可用宽度。</summary>
    public double? Width
    {
        get => _width;
        init
        {
            if (value is { } w)
            {
                ValidateFiniteNonNegative(w, nameof(Width));
            }
            _width = value;
        }
    }

    /// <summary>显式高度；null 表示按 <see cref="MinLines"/> 行 + 内边距计算视口高度。</summary>
    public double? Height
    {
        get => _height;
        init
        {
            if (value is { } h)
            {
                ValidateFiniteNonNegative(h, nameof(Height));
            }
            _height = value;
        }
    }

    /// <summary>水平内边距。</summary>
    public double HorizontalPadding
    {
        get => _horizontalPadding;
        init
        {
            ValidateFiniteNonNegative(value, nameof(HorizontalPadding));
            _horizontalPadding = value;
        }
    }

    /// <summary>垂直内边距。</summary>
    public double VerticalPadding
    {
        get => _verticalPadding;
        init
        {
            ValidateFiniteNonNegative(value, nameof(VerticalPadding));
            _verticalPadding = value;
        }
    }

    /// <summary>圆角半径。</summary>
    public double CornerRadius
    {
        get => _cornerRadius;
        init
        {
            ValidateFiniteNonNegative(value, nameof(CornerRadius));
            _cornerRadius = value;
        }
    }

    /// <summary>最小行数（决定视口高度，非文本行数上限）。</summary>
    public int MinLines
    {
        get => _minLines;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, 0);
            _minLines = value;
        }
    }

    /// <summary>最大字符数（UTF-16 长度）；默认不限制。</summary>
    public int MaxLength { get; init; } = int.MaxValue;

    /// <summary>光标闪烁周期的一半（亮/灭各持续该时长）。</summary>
    public TimeSpan BlinkInterval { get; init; } = TimeSpan.FromMilliseconds(530);

    private double? _width;
    private double? _height;
    private double _horizontalPadding = 10;
    private double _verticalPadding = 6;
    private double _cornerRadius = 6;
    private int _minLines = 3;

    /// <inheritdoc />
    public override string DebugName => $"TextEditor({Placeholder ?? "text"})";

    /// <inheritdoc />
    public override bool IsFocusable => true;

    /// <summary>返回替换绑定的副本（协调更新时使用新绑定）。</summary>
    public TextEditor With(Binding<string> text) => new(text, Placeholder)
    {
        Width = Width,
        Height = Height,
        HorizontalPadding = HorizontalPadding,
        VerticalPadding = VerticalPadding,
        CornerRadius = CornerRadius,
        MaxLength = MaxLength,
        BlinkInterval = BlinkInterval,
        MinLines = MinLines,
    };

    private static void ValidateFiniteNonNegative(double value, string name)
    {
        if (!double.IsFinite(value) || value < 0)
        {
            throw new ArgumentOutOfRangeException(name, value, $"{name} 必须是有限非负数。");
        }
    }

    internal override void Measure(ViewNode node, LayoutConstraints constraints, LayoutContext context)
    {
        var text = Text.Read(node) ?? string.Empty;
        var font = node.Environment.Font ?? FontDescription.Default;

        // 排版宽 = 最终 content 宽 = frameWidth − 2×HorizontalPadding（唯一来源）：
        //  - Width 显式 → frame = Width；
        //  - 父级 tight → frame = MaxWidth；
        //  - 其余 → frame = 默认下限（140 或更小，视父级上限），与下方 DesiredSize 同一公式。
        var available = constraints.HasBoundedWidth ? Math.Max(0, constraints.MaxWidth) : double.PositiveInfinity;
        var minimum = Math.Min(DefaultMinimumWidth, available);
        var frameWidth = Width ?? (constraints.IsWidthTight ? constraints.MaxWidth : minimum);
        var wrapWidth = Math.Max(1, frameWidth - (HorizontalPadding * 2));

        // Editing=true：硬换行分段 + 尾随空段 + caretsByLine —— 同一份排版供给 Measure/Arrange/Emit/命中。
        var request = TextPainter.CreateRequest(
            text, font, wrapWidth, context.PixelScale,
            wrapping: TextWrapping.Wrap, maxLines: int.MaxValue, lineHeight: 0) with { Editing = true };
        var layout = context.TextService.Layout(request);

        // 缓存供 Arrange/Paint 复用;Measure 得到的就是此刻文档的权威排版。
        node.ViewCache = new TextEditorMeasureCache(text, font, context.PixelScale, wrapWidth, layout);

        var width = Width ?? (constraints.IsWidthTight
            ? constraints.MaxWidth
            : Math.Clamp(layout.Size.Width + (HorizontalPadding * 2), minimum, available));

        // 高度统一：先按 preferredHeight（MinLines 行 + 内边距）再由父级夹取；
        // 父级 Frame(height:)/Flex 强制分配的最终 bounds 由 Arrange 接收，这里只给期望。
        var lineHeight = Math.Max(1, layout.Lines.Count > 0 ? layout.Lines[0].Bounds.Height : font.Size * 1.4);
        var preferredHeight = Height ?? (MinLines * lineHeight + (VerticalPadding * 2));
        node.DesiredSize = constraints.Constrain(new Size(width, preferredHeight));
    }

    internal override void Arrange(ViewNode node, Rect bounds, LayoutContext context)
    {
        node.Frame = bounds;

        if (node.InteractionState is TextEditorState state)
        {
            SyncEditor(node, state, invalidate: false);

            // 宽度变化触发 reflow：排版缓存按内容区宽键控，宽度改变时下次排版自然取新宽度。
            // 此处只保证 caret 在新宽度下仍可见；具体的排版由排版缓存惰性重建。
            if (node.IsEffectivelyDisabled && state.Composing is not null)
            {
                CancelCompositionForHostAction(node, state);
                state.RequestDeferredEndInputSession();
            }

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

        // 文档排版与显示串排版严格分开：预编辑只进显示串，绝不写绑定。
        var layout = GetLayout(node, context.TextService);
        SyncEditor(node, state, invalidate: false, knownLayout: layout);
        var display = GetDisplayLayout(node, context.TextService);

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
        if (inner.IsEmpty)
        {
            return;
        }

        // caret 可见性只在键盘/编辑/pointer/Arrange 入口收敛 —— 纯重绘帧不 ensure，
        // 否则用户滚轮把 caret 滚出视口后下一帧会被拉回（计划 §3.8：用户滚不 ensure）。

        if (string.IsNullOrEmpty(display.Text) && !string.IsNullOrEmpty(Placeholder) && !state.HasSelection)
        {
            var font = node.Environment.Font ?? FontDescription.Default;
            // 占位符与 TextField 一致：不换行，超宽由 inner 裁剪。
            var placeholder = TextPainter.Layout(context, node, Placeholder, font, double.PositiveInfinity, context.PixelScale);
            TextPainter.Paint(context, node, placeholder, theme.MutedForeground, inner, TextAlignment.Left, 0.85);
        }

        var restore = context.CurrentClip;
        if (!context.PushClip(inner))
        {
            return;
        }

        // 文本原点 = 文本块左上角（含 ScrollY 与物理像素吸附）；文本、选区、caret、预编辑装饰共用。
        // 编辑器首行必须顶对齐（区别于 TextField 的垂直居中）：bounds 高取 max(inner,layout)
        // 时垂直居中公式会把偏短的文本下推，因此统一取 textBounds 左上角吸附。
        var origin = TextOrigin(display, inner, state);
        if (state.HasSelection && node.IsFocused && !display.Composing)
        {
            // 逐行选区矩形：内容区真实 X + 行末零宽 marker（涂成 SelectionLineEndMarkerWidth 可见窄块）。
            var rects = display.Layout.GetRangeRects(state.SelectionStart, state.SelectionEnd);
            foreach (var rect in rects)
            {
                var painted = rect.Width <= 0
                    ? new Rect(origin.X + rect.X, origin.Y + rect.Y, SelectionLineEndMarkerWidth, rect.Height)
                    : new Rect(origin.X + rect.X, origin.Y + rect.Y, Math.Max(rect.Width, 1), rect.Height);
                context.Commands.Add(new RectangleCommand(
                    painted,
                    theme.SelectionBackground,
                    2,
                    null,
                    0,
                    opacity));
            }
        }

        var textColor = disabled ? theme.DisabledForeground : theme.Foreground;
        // Paint 内部会对 bounds 垂直居中——传与排版同高的 bounds 使居中偏移恒为 0，保持顶部对齐。
        TextPainter.Paint(context, node, display.Layout, textColor,
            new Rect(origin, display.Layout.Size), TextAlignment.Left);

        if (display.Composing)
        {
            DrawCompositionDecorations(context, node, state, display, theme, origin, inner, opacity);
        }
        else if (node.IsFocused && IsCaretVisible(node, state))
        {
            var caretRect = ClampCaretToInner(CaretRectAt(display, state, origin), inner);
            context.Commands.Add(new RectangleCommand(caretRect, textColor, 0, null, 0, opacity));
        }

        context.PopClip(restore);
    }

    /// <summary>绘制预编辑装饰（每段下划线 + 活动段强调 + 组合 caret）；TE-04 接 IME 后生效。</summary>
    private void DrawCompositionDecorations(
        DrawContext context,
        ViewNode node,
        TextEditorState state,
        TextEditorDisplayLayout display,
        Theme theme,
        Point origin,
        Rect inner,
        double opacity)
    {
        var segments = display.Segments.Count > 0
            ? display.Segments
            : (IReadOnlyList<TextRange>)[new TextRange(0, display.PreeditLength)];
        var caretInPreedit = Math.Clamp(display.CaretDisplay - display.PreeditStart, 0, display.PreeditLength);
        var active = ActiveSegment(segments, caretInPreedit);

        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            var start = display.PreeditStart + Math.Clamp(segment.Start, 0, display.PreeditLength);
            var end = display.PreeditStart + Math.Clamp(segment.End, 0, display.PreeditLength);
            if (end <= start)
            {
                continue;
            }

            // 逐行真实边界（不 union 填空）：跨行段的下行段绘制下行自己的下划线。
            var rects = display.Layout.GetRangeRects(start, end);
            foreach (var bounds in rects)
            {
                if (bounds.Width <= 0)
                {
                    continue;
                }

                var thickness = i == active ? 2.0 : 1.0;
                var fill = i == active ? theme.Accent : theme.MutedForeground;
                context.Commands.Add(new RectangleCommand(
                    new Rect(origin.X + bounds.X, origin.Y + bounds.Bottom - thickness, bounds.Width, thickness),
                    fill,
                    0,
                    null,
                    0,
                    opacity));
            }
        }

        if (!node.IsFocused || !IsCaretVisible(node, state))
        {
            return;
        }

        var caretRect = ClampCaretToInner(CaretRectAt(display, state, origin), inner);
        context.Commands.Add(new RectangleCommand(caretRect, theme.Accent, 0, null, 0, opacity));
    }

    /// <summary>显示串在内容区内的目标矩形（垂直滚动 + 顶部对齐）；文字、装饰与命中共用。</summary>
    private static Rect DisplayTextBounds(TextEditorDisplayLayout display, Rect inner, TextEditorState state)
        => new(
            inner.X,
            inner.Y - state.ScrollY,
            inner.Width,
            Math.Max(inner.Height, display.Layout.Size.Height));

    /// <summary>文本绘制/命中共用的吸附原点：textBounds 左上角（顶部对齐）吸附到物理像素。</summary>
    private static Point TextOrigin(TextEditorDisplayLayout display, Rect inner, TextEditorState state)
    {
        var bounds = DisplayTextBounds(display, inner, state);
        return TextPainter.SnapToPhysicalPixel(new Point(bounds.X, bounds.Y), display.Layout.PixelScale);
    }

    /// <summary>按当前显示串解析有效 caret 的未裁剪矩形；位置只由排版原点 + caret 决定。</summary>
    private static Rect CaretRectAt(TextEditorDisplayLayout display, TextEditorState state, Point origin)
    {
        var caretIndex = display.Composing
            ? display.CaretDisplay
            : display.Layout.SnapToCaretStop(Math.Clamp(state.Caret, 0, display.Text.Length));
        var caret = display.Layout.GetCaretBounds(caretIndex, state.CaretAffinity, 1.5);
        return new Rect(origin.X + caret.X, origin.Y + caret.Y, caret.Width, caret.Height);
    }

    /// <summary>绘制端把 caret 夹回内容区，避免不足 1 像素的整条不可见；发布几何用未夹取值。</summary>
    private static Rect ClampCaretToInner(Rect caretRect, Rect inner)
    {
        var x = caretRect.Width <= inner.Width
            ? Math.Clamp(caretRect.X, inner.Left, inner.Right - caretRect.Width)
            : caretRect.X;
        var y = caretRect.Height <= inner.Height
            ? Math.Clamp(caretRect.Y, inner.Top, inner.Bottom - caretRect.Height)
            : caretRect.Y;
        return new Rect(x, y, caretRect.Width, caretRect.Height);
    }

    private static int ActiveSegment(IReadOnlyList<TextRange> segments, int caret)
    {
        for (var i = 0; i < segments.Count; i++)
        {
            if (caret < segments[i].End)
            {
                return i;
            }
        }

        return segments.Count - 1;
    }

    // ------------------------------------------------------------------ 指针

    internal override bool OnPointerDown(InputContext context, ViewNode node, PointerEvent e)
    {
        if (node.IsEffectivelyDisabled)
        {
            return false;
        }

        var state = GetState(node);
        context.Runtime.SetFocus(node);
        SyncEditor(node, state, invalidate: true);

        // 鼠标改选区是取消接缝：先取消未提交组合，再按正式文档命中。
        CancelCompositionForHostAction(node, state);

        state.History.BreakMerge();
        state.PreferredX = null;

        state.IsDragging = true;
        state.DragPointer = e.Position;
        state.LastDragScrollTick = null;
        SetCaretFromPointer(node, state, e.Position, extend: false);
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

        state.DragPointer = e.Position;
        SyncEditor(node, state, invalidate: true);
        state.History.BreakMerge();
        state.PreferredX = null;
        SetCaretFromPointer(node, state, e.Position, extend: true);
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
        state.DragPointer = null;
        SyncEditor(node, state, invalidate: true);
        state.History.BreakMerge();
        SetCaretFromPointer(node, state, e.Position, extend: true);
        EnsureCaretVisible(node, state);
        return true;
    }

    internal override bool OnPointerCancel(InputContext context, ViewNode node)
    {
        var state = GetState(node);
        state.IsDragging = false;
        state.DragPointer = null;
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

    /// <summary>按指针位置定位 caret（affinity 精确行归属）；拖选时保持 anchor 不动。</summary>
    private void SetCaretFromPointer(ViewNode node, TextEditorState state, Point position, bool extend)
    {
        var layout = GetLayout(node, node.Runtime.Services.TextService);
        var inner = InnerRect(node.Frame);
        if (inner.IsEmpty)
        {
            state.Caret = 0;
            if (!extend)
            {
                state.Anchor = 0;
            }
            state.CaretAffinity = TextAffinity.Unspecified;
            return;
        }

        var display = GetDisplayLayout(node, node.Runtime.Services.TextService);
        var origin = TextOrigin(display, inner, state);
        var local = new Point(position.X - origin.X, position.Y - origin.Y);
        var stop = layout.HitTestIndex(local, out var affinity);
        state.Caret = stop;
        state.CaretAffinity = affinity;
        if (!extend)
        {
            state.Anchor = stop;
        }
    }

    // ------------------------------------------------------------------ 焦点 / 文本 / 键

    internal override void OnFocusChanged(InputContext context, ViewNode node, bool focused)
    {
        var state = GetState(node);
        state.BlinkEpoch = node.Runtime.Clock.Now;
        state.IsDragging = false;
        state.DragPointer = null;
        state.PreferredX = null;

        state.History.BreakMerge();

        if (focused)
        {
            SyncEditor(node, state, invalidate: true);
            var text = Text.Read(node) ?? string.Empty;
            var layout = GetLayout(node, node.Runtime.Services.TextService);
            state.Caret = layout.SnapToCaretStop(Math.Clamp(state.Caret, 0, text.Length));
            state.Anchor = layout.SnapToCaretStop(Math.Clamp(state.Anchor, 0, text.Length));
            EnsureCaretVisible(node, state);

            // 焦点获得后才建立文本输入会话；组合客户端由会话工厂按需创建。
            state.InputSession.Start(node, epoch => state.RequireComposing(node, epoch));
        }
        else
        {
            CancelCompositionForHostAction(node, state);
            state.EndInputSession();
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

        var state = GetState(node);
        SyncEditor(node, state, invalidate: false);

        // 组合期间的“已提交字符”通道不做写入：归属是输入法消息，由组合结果独占落地。
        if (state.Composing?.IsComposing == true)
        {
            CancelCompositionForHostAction(node, state);
            context.MarkHandled();
            return true;
        }

        var inserted = TextInputEvent.SanitizeMultiLine(e.Text);
        if (inserted.Length == 0)
        {
            context.MarkHandled();
            return true;
        }

        state.History.BreakMerge();
        state.PreferredX = null;
        ApplyEdit(node, state, EditOperation.TryCreate(
            CurrentEditRange(node, state),
            inserted,
            TextEditKind.Typing,
            TextEditReason.UserInput,
            scrubSingleLine: false,
            out var operation,
            out _)
            ? operation
            : default);
        state.BlinkEpoch = node.Runtime.Clock.Now;
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
        try
        {
            return HandleKeyDown(context, node, state, e);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            state.History.Clear();
            throw;
        }
    }

    private bool HandleKeyDown(InputContext context, ViewNode node, TextEditorState state, KeyEvent e)
    {
        SyncEditor(node, state, invalidate: false);

        // 输入法显式消费的同一次原生按键优先（TI-07 接缝）。
        var keyToken = node.Runtime.KeyOriginToken ?? state.NativeKeyToken;
        if (state.IsExplicitlyConsumedKey(keyToken))
        {
            state.BlinkEpoch = node.Runtime.Clock.Now;
            context.MarkHandled();
            return true;
        }

        var command = TextEditCommandResolver.Resolve(e, node.Runtime.EditPlatform);
        if (command is { } resolved)
        {
            ExecuteCommand(node, state, resolved);
            state.BlinkEpoch = node.Runtime.Clock.Now;
            context.MarkHandled();
            return true;
        }

        // 非命令键：组合期间方向键/翻页/Backspace/Delete/空格/Enter/Escape 归输入法所有；
        // 未被输入法消费的 Undo/Redo/Cut/Paste/SelectAll 已在上面按命令处理（先取消预编辑再执行）。
        if (state.Composing?.IsComposing == true)
        {
            state.BlinkEpoch = node.Runtime.Clock.Now;
            context.MarkHandled();
            return true;
        }

        var text = Text.Read(node) ?? string.Empty;
        var extend = e.Modifiers.HasFlag(KeyModifiers.Shift);
        var handled = e.Key switch
        {
            UiKey.Left => MoveCaretHorizontal(node, state, text, backward: true, extend),
            UiKey.Right => MoveCaretHorizontal(node, state, text, backward: false, extend),
            UiKey.Up => MoveCaretVertical(node, state, text, up: true, extend),
            UiKey.Down => MoveCaretVertical(node, state, text, up: false, extend),
            UiKey.Home => MoveCaretToLineEdge(node, state, text, home: true, extend, wholeDocument: e.IsCommandPressed),
            UiKey.End => MoveCaretToLineEdge(node, state, text, home: false, extend, wholeDocument: e.IsCommandPressed),
            UiKey.PageUp => MoveCaretPage(node, state, text, up: true, extend),
            UiKey.PageDown => MoveCaretPage(node, state, text, up: false, extend),
            UiKey.Enter => InsertNewline(node, state),
            UiKey.Backspace => DeleteSelectionOrGrapheme(node, state, text, backward: true, TextEditKind.Backspace),
            UiKey.Delete => DeleteSelectionOrGrapheme(node, state, text, backward: false, TextEditKind.Delete),
            _ => false,
        };

        if (handled)
        {
            state.BlinkEpoch = node.Runtime.Clock.Now;
            context.MarkHandled();
        }

        return handled;
    }

    private bool MoveCaretHorizontal(ViewNode node, TextEditorState state, string text, bool backward, bool extend)
    {
        state.History.BreakMerge();
        state.PreferredX = null;
        var layout = GetLayout(node, node.Runtime.Services.TextService);
        var current = layout.SnapToCaretStop(Math.Clamp(state.Caret, 0, text.Length));
        var stops = layout.CaretStops;
        var position = TextLayoutResult.FindStop(stops, current);
        var index = position >= 0 ? position : Math.Max(0, ~position - 1);
        var next = backward ? Math.Max(0, index - 1) : Math.Min(stops.Count - 1, index + 1);
        var stop = stops[next];
        // 左右来的方向决定 affinity：左来→LineEnd，右来→LineStart（跨行边界歧义消除）。
        state.CaretAffinity = backward ? TextAffinity.LineEnd : TextAffinity.LineStart;
        SetCaret(node, state, stop, extend);
        return true;
    }

    private bool MoveCaretVertical(ViewNode node, TextEditorState state, string text, bool up, bool extend)
    {
        state.History.BreakMerge();
        var layout = GetLayout(node, node.Runtime.Services.TextService);
        var caret = layout.SnapToCaretStop(Math.Clamp(state.Caret, 0, text.Length));

        // 上下行用 affinity 归属定行；期望 X 只在本组上下移动里保留。
        var line = layout.LineIndexForIndex(caret, state.CaretAffinity);
        var targetX = state.PreferredX ??= layout.GetCaretX(caret, state.CaretAffinity);

        var targetLine = up ? line - 1 : line + 1;
        int stop;
        TextAffinity affinity;
        if (targetLine < 0)
        {
            stop = 0;
            affinity = TextAffinity.Unspecified;
        }
        else if (targetLine >= layout.Lines.Count)
        {
            stop = text.Length;
            affinity = TextAffinity.Unspecified;
        }
        else
        {
            stop = layout.HitTestLine(targetLine, targetX, out affinity);
        }

        state.CaretAffinity = affinity;
        SetCaret(node, state, stop, extend);
        return true;
    }

    private bool MoveCaretToLineEdge(ViewNode node, TextEditorState state, string text, bool home, bool extend, bool wholeDocument)
    {
        state.History.BreakMerge();
        state.PreferredX = null;
        var layout = GetLayout(node, node.Runtime.Services.TextService);
        int stop;
        if (wholeDocument)
        {
            stop = home ? 0 : text.Length;
        }
        else
        {
            var caret = layout.SnapToCaretStop(Math.Clamp(state.Caret, 0, text.Length));
            var line = layout.LineIndexForIndex(caret, state.CaretAffinity);
            stop = home ? layout.LineStartIndex(line) : layout.LineEndIndex(line);
        }

        // Home/End 不是跨行导航：清 PreferredX，按行边界归属 affinity。
        state.CaretAffinity = home ? TextAffinity.LineStart : TextAffinity.LineEnd;
        SetCaret(node, state, stop, extend);
        return true;
    }

    private bool MoveCaretPage(ViewNode node, TextEditorState state, string text, bool up, bool extend)
    {
        state.History.BreakMerge();
        var layout = GetLayout(node, node.Runtime.Services.TextService);
        var inner = InnerRect(node.Frame);
        var lineHeight = layout.Lines.Count > 0 ? layout.Lines[0].Bounds.Height : Math.Max(1, (node.Environment.Font?.Size ?? 14) * 1.4);
        var pageLines = Math.Max(1, (int)Math.Floor(inner.Height / lineHeight));

        var caret = layout.SnapToCaretStop(Math.Clamp(state.Caret, 0, text.Length));
        var line = layout.LineIndexForIndex(caret, state.CaretAffinity);
        var targetX = state.PreferredX ??= layout.GetCaretX(caret, state.CaretAffinity);

        var targetLine = Math.Clamp(up ? line - pageLines : line + pageLines, 0, layout.Lines.Count - 1);
        var stop = layout.HitTestLine(targetLine, targetX, out var affinity);
        state.CaretAffinity = affinity;
        SetCaret(node, state, stop, extend);
        return true;
    }

    private bool InsertNewline(ViewNode node, TextEditorState state)
    {
        // 换行是新逻辑行起点，不归并前序字符：插入前后显式 BreakMerge。
        CancelCompositionForHostAction(node, state);
        state.History.BreakMerge();
        state.PreferredX = null;
        ApplyEdit(node, state, new EditOperation(
            CurrentEditRange(node, state),
            "\n",
            TextEditKind.Typing,
            TextEditReason.UserInput,
            scrubSingleLine: false));
        state.History.BreakMerge();
        return true;
    }

    private bool DeleteSelectionOrGrapheme(ViewNode node, TextEditorState state, string text, bool backward, TextEditKind kind)
    {
        state.History.BreakMerge();
        state.PreferredX = null;
        var layout = GetLayout(node, node.Runtime.Services.TextService);
        TextRange range;
        if (state.HasSelection)
        {
            range = SelectionRange(layout, state);
        }
        else
        {
            var caret = layout.SnapToCaretStop(Math.Clamp(state.Caret, 0, text.Length));
            var boundary = backward ? PreviousBoundary(layout, caret) : NextBoundary(layout, caret);
            range = backward ? new TextRange(boundary, caret) : new TextRange(caret, boundary);
        }

        if (range.IsEmpty)
        {
            return true;
        }

        ApplyEdit(node, state, EditOperation.Delete(range, kind, TextEditReason.UserInput));
        return true;
    }

    private void SetCaret(ViewNode node, TextEditorState state, int index, bool extend)
    {
        var layout = GetLayout(node, node.Runtime.Services.TextService);
        var snapped = layout.SnapToCaretStop(index);
        state.Caret = snapped;
        if (!extend)
        {
            state.Anchor = snapped;
        }

        EnsureCaretVisible(node, state);
    }

    // ------------------------------------------------------------------ 编辑命令 / 剪贴板

    private void ExecuteCommand(ViewNode node, TextEditorState state, TextEditCommand command)
    {
        switch (command)
        {
            case TextEditCommand.Copy:
                // 复制只读已提交选区，不取消组合；仍是分组边界。
                state.History.BreakMerge();
                CopySelection(node, state, Text.Read(node) ?? string.Empty, cut: false);
                break;
            case TextEditCommand.Cut:
                CancelCompositionForHostAction(node, state);
                state.History.BreakMerge();
                CopySelection(node, state, Text.Read(node) ?? string.Empty, cut: true);
                break;
            case TextEditCommand.Paste:
                CancelCompositionForHostAction(node, state);
                state.History.BreakMerge();
                PasteFromClipboard(node, state, Text.Read(node) ?? string.Empty);
                break;
            case TextEditCommand.SelectAll:
                CancelCompositionForHostAction(node, state);
                state.History.BreakMerge();
                var text = Text.Read(node) ?? string.Empty;
                state.Anchor = 0;
                state.Caret = text.Length;
                break;
            case TextEditCommand.Undo:
                CancelCompositionForHostAction(node, state);
                state.History.BreakMerge();
                ReplayHistory(node, state, undo: true);
                break;
            case TextEditCommand.Redo:
                CancelCompositionForHostAction(node, state);
                state.History.BreakMerge();
                ReplayHistory(node, state, undo: false);
                break;
        }
    }

    private void CopySelection(ViewNode node, TextEditorState state, string text, bool cut)
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

    private void PasteFromClipboard(ViewNode node, TextEditorState state, string text)
    {
        var clipboard = node.Runtime.Services.Clipboard;
        var pasted = clipboard?.GetText();
        if (string.IsNullOrEmpty(pasted))
        {
            return;
        }

        if (TextInputEvent.SanitizeMultiLine(pasted).Length == 0)
        {
            return;
        }

        var range = CurrentEditRange(node, state);
        if (!EditOperation.TryCreate(
                range,
                pasted,
                TextEditKind.Paste,
                TextEditReason.Clipboard,
                scrubSingleLine: false,
                out var operation,
                out _,
                normalizeLineEndings: true))
        {
            return;
        }

        ApplyEdit(node, state, operation);
    }

    private void ReplayHistory(ViewNode node, TextEditorState state, bool undo)
    {
        var history = state.History;
        if (undo ? !history.CanUndo : !history.CanRedo)
        {
            return;
        }

        if (node.View is not TextEditor field)
        {
            return;
        }

        var binding = field.Text;
        var entry = undo ? history.UndoPeek! : history.RedoPeek!;

        string current;
        try
        {
            current = binding.Read(node) ?? string.Empty;
        }
        catch
        {
            history.Clear();
            state.Editor.MarkRequiresResync();
            throw;
        }

        // 撤销把当时的 inserted 区段替换回 deleted，重做把 deleted 换回 inserted；
        // 文档必须符合记录的上下文（起点 + 期望区段文本匹配）才可安全重放。
        var expected = undo ? entry.Inserted : entry.Deleted;
        var replacement = undo ? entry.Deleted : entry.Inserted;
        var range = new TextRange(entry.ReplacementStart, entry.ReplacementStart + expected.Length);
        var matches = range.IsWithin(current.Length)
            && string.CompareOrdinal(current, range.Start, expected, 0, expected.Length) == 0;
        if (!matches)
        {
            history.Clear();
            return;
        }

        if (!EditOperation.TryCreate(
                range,
                replacement,
                undo ? TextEditKind.Undo : TextEditKind.Redo,
                TextEditReason.History,
                scrubSingleLine: false,
                out var operation,
                out _))
        {
            history.Clear();
            return;
        }

        var expectedSelection = undo ? entry.SelectionBefore : entry.SelectionAfter;
        var beforeSelection = undo ? entry.SelectionAfter : entry.SelectionBefore;

        EditResult result;
        try
        {
            result = state.Editor.Apply(
                binding.Identity,
                field.MaxLength,
                operation,
                beforeSelection,
                () => binding.Read(node) ?? string.Empty,
                value => binding.Value = value);
        }
        catch
        {
            history.Clear();
            RecoverAfterTransactionFailure(node, state);
            throw;
        }

        if (!node.IsMounted || node.View is not TextEditor mounted
            || !ReferenceEquals(mounted.Text.Identity, binding.Identity)
            || mounted.MaxLength != field.MaxLength
            || !result.Changed
            || state.Editor.LastTransaction != TextEditTransactionOutcome.Applied)
        {
            history.Clear();
            SyncEditor(node, state, invalidate: true);
            return;
        }

        if (undo)
        {
            history.CommitUndo();
        }
        else
        {
            history.CommitRedo();
        }

        state.Caret = Math.Clamp(expectedSelection.Active, 0, result.After.Text.Length);
        state.Anchor = Math.Clamp(expectedSelection.Anchor, 0, result.After.Text.Length);
        state.PreferredX = null;
        node.Runtime.InvalidateRender();
        EnsureCaretVisible(node, state);
    }

    // ------------------------------------------------------------------ 原子事务

    /// <summary>通过统一编辑会话执行一次原子替换；setter 拒绝/容量拒绝/外部变化都不移动选区。</summary>
    private void ApplyEdit(ViewNode node, TextEditorState state, EditOperation operation)
    {
        if (node.View is not TextEditor field)
        {
            return;
        }

        var binding = field.Text;
        var beforeSelection = new TextSelection(state.Anchor, state.Caret);

        EditResult result;
        try
        {
            result = state.Editor.Apply(
                binding.Identity,
                field.MaxLength,
                operation,
                beforeSelection,
                () => binding.Read(node) ?? string.Empty,
                value => binding.Value = value);
        }
        catch
        {
            RecoverAfterTransactionFailure(node, state);
            state.History.Clear();
            throw;
        }

        if (!node.IsMounted || node.View is not TextEditor mounted)
        {
            return;
        }

        if (!ReferenceEquals(mounted.Text.Identity, binding.Identity) || mounted.MaxLength != field.MaxLength)
        {
            state.History.Clear();
            SyncEditor(node, state, invalidate: true);
            return;
        }

        if (state.Editor.LastTransactionSawExternalChange)
        {
            state.History.Clear();
        }

        if (!result.Changed)
        {
            if (state.Editor.LastTransaction == TextEditTransactionOutcome.SetterRefused)
            {
                state.History.Clear();
            }

            SyncEditor(node, state, invalidate: false);
            return;
        }

        state.Caret = result.After.Selection.Active;
        state.Anchor = result.After.Selection.Anchor;

        if (state.Editor.LastTransaction == TextEditTransactionOutcome.Applied)
        {
            state.History.Record(TextEditHistory.FromTransaction(result, operation, beforeSelection), node.Runtime.Clock.Now);
        }
        else
        {
            state.History.Clear();
        }

        node.Runtime.InvalidateRender();
        EnsureCaretVisible(node, state);
    }


    private void RecoverAfterTransactionFailure(ViewNode node, TextEditorState state)
    {
        if (node.View is not TextEditor field)
        {
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

    /// <summary>安全入口统一同步：读真实绑定、识别自身确认与外部变化、按真实文本夹选区。</summary>
    private void SyncEditor(ViewNode node, TextEditorState state, bool invalidate, TextLayoutResult? knownLayout = null)
    {
        if (node.View is not TextEditor field)
        {
            return;
        }

        var binding = field.Text;
        string text;
        try
        {
            text = binding.Read(node) ?? string.Empty;
        }
        catch
        {
            state.History.Clear();
            throw;
        }

        // 自身刚确认的值与来源一致：不是外部变化，直接返回（同节点同绑定重建/纯重布局不触发）。
        if (state.Editor.IsOwnPendingWrite(text) && state.Editor.MatchesContext(binding.Identity, field.MaxLength))
        {
            return;
        }

        var sync = state.Editor.Sync(binding.Identity, text, field.MaxLength);
        if (!sync.HasExternalChange)
        {
            return;
        }

        // 外部文本/来源/MaxLength 真正变化：历史上下文失效；取消未提交组合（预编辑绝不写绑定）。
        state.History.Clear();
        state.Composing?.InvalidateForExternalChange();

        knownLayout ??= GetLayout(node, node.Runtime.Services.TextService);
        state.Caret = knownLayout.SnapToCaretStop(Math.Clamp(state.Caret, 0, text.Length));
        state.Anchor = knownLayout.SnapToCaretStop(Math.Clamp(state.Anchor, 0, text.Length));
        if (invalidate)
        {
            node.Runtime.InvalidateLayout(node);
        }
    }

    private void ClampSelectionToText(ViewNode node, TextEditorState state, string text)
    {
        var layout = GetLayout(node, node.Runtime.Services.TextService);
        state.Caret = layout.SnapToCaretStop(Math.Clamp(state.Caret, 0, text.Length));
        state.Anchor = layout.SnapToCaretStop(Math.Clamp(state.Anchor, 0, text.Length));
    }

    private TextRange CurrentEditRange(ViewNode node, TextEditorState state)
    {
        if (state.HasSelection)
        {
            var layout = GetLayout(node, node.Runtime.Services.TextService);
            return SelectionRange(layout, state);
        }

        var text = Text.Read(node) ?? string.Empty;
        return TextRange.FromLength(ClampIndex(node, state.Caret, text), 0);
    }

    private static TextRange SelectionRange(TextLayoutResult layout, TextEditorState state)
    {
        var start = layout.SnapToCaretStop(Math.Clamp(state.SelectionStart, 0, layout.Text.Length));
        var end = layout.SnapToCaretStop(Math.Clamp(state.SelectionEnd, 0, layout.Text.Length), roundUp: true);
        return new TextRange(start, end);
    }

    private int ClampIndex(ViewNode node, int index, string text)
    {
        var clamped = Math.Clamp(index, 0, text.Length);
        var layout = GetLayout(node, node.Runtime.Services.TextService);
        return layout.Text == text ? layout.SnapToCaretStop(clamped) : clamped;
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

    // ------------------------------------------------------------------ 组合提交接缝（TE-04 生效）

    /// <summary>组合回调内的绑定同步：读真实文本、识别外部变化并让组合立即失效（不排版、不调平台）。</summary>
    internal void SyncCompositionDocument(ViewNode node, TextEditorState state, out bool invalidated)
    {
        invalidated = false;
        if (node.View is not TextEditor field)
        {
            return;
        }

        var binding = field.Text;
        string text;
        try
        {
            text = binding.Read(node) ?? string.Empty;
        }
        catch
        {
            state.History.Clear();
            throw;
        }

        if (state.Editor.IsOwnPendingWrite(text) && state.Editor.MatchesContext(binding.Identity, field.MaxLength))
        {
            return;
        }

        var sync = state.Editor.Sync(binding.Identity, text, field.MaxLength);
        if (!sync.HasExternalChange)
        {
            return;
        }

        invalidated = true;
        state.History.Clear();
        state.Caret = TextGraphemeBoundaries.SnapToBoundary(text, state.Caret);
        state.Anchor = TextGraphemeBoundaries.SnapToBoundary(text, state.Anchor);
    }

    /// <summary>把一段组合结果作为一次真实原子事务提交（与 TextField 同语义，TE-04 生效）。</summary>
    internal EditResult ApplyCompositionCommit(ViewNode node, TextEditorState state, TextRange range, string result)
    {
        if (node.View is not TextEditor field)
        {
            return default;
        }

        var binding = field.Text;
        var identity = binding.Identity;
        var maxLength = field.MaxLength;

        if (!EditOperation.TryCreate(
                range,
                result,
                TextEditKind.CompositionResult,
                TextEditReason.UserInput,
                scrubSingleLine: false,
                out var operation,
                out var createError,
                normalizeLineEndings: true))
        {
            return RejectedCommit($"组合结果未通过编辑操作校验：{createError}");
        }

        EditResult edit;
        var beforeSelection = new TextSelection(state.Anchor, state.Caret);
        try
        {
            edit = state.Editor.Apply(
                identity,
                maxLength,
                operation,
                beforeSelection,
                () => binding.Read(node) ?? string.Empty,
                value => binding.Value = value);
        }
        catch
        {
            state.History.Clear();
            try
            {
                state.Editor.Sync(identity, binding.Read(node) ?? string.Empty, maxLength);
                SnapSelectionToText(state, state.Editor.Text);
                node.Runtime.InvalidateLayout(node);
            }
            catch
            {
                state.Editor.MarkRequiresResync();
                node.Runtime.InvalidateRender();
            }

            throw;
        }

        if (!node.IsMounted || node.View is not TextEditor mounted)
        {
            state.Editor.MarkRequiresResync();
            state.History.Clear();
            return RejectedCommit("事务期间输入框已卸载；组合结果不写入。");
        }

        if (!ReferenceEquals(mounted.Text.Identity, identity) || mounted.MaxLength != maxLength)
        {
            state.History.Clear();
            SyncCompositionContext(node, state);
            return RejectedCommit("事务期间绑定来源或 MaxLength 已变化；旧组合结果不写入新客户端。");
        }

        if (state.Editor.LastTransactionSawExternalChange)
        {
            state.History.Clear();
        }

        if (edit.Changed)
        {
            state.Caret = edit.After.Selection.Active;
            state.Anchor = edit.After.Selection.Anchor;

            if (state.Editor.LastTransaction == TextEditTransactionOutcome.Applied)
            {
                state.History.Record(TextEditHistory.FromTransaction(edit, operation, beforeSelection), node.Runtime.Clock.Now);
            }
            else
            {
                state.History.Clear();
            }

            node.Runtime.InvalidateLayout(node);
        }
        else
        {
            if (state.Editor.LastTransaction == TextEditTransactionOutcome.SetterRefused)
            {
                state.History.Clear();
            }

            SyncCompositionContext(node, state);
            SnapSelectionToText(state, state.Editor.Text);
            node.Runtime.InvalidateLayout(node);
        }

        return edit;
    }

    private void SyncCompositionContext(ViewNode node, TextEditorState state)
    {
        if (node.View is not TextEditor field)
        {
            return;
        }

        var binding = field.Text;
        string text;
        try
        {
            text = binding.Read(node) ?? string.Empty;
        }
        catch
        {
            state.History.Clear();
            state.Editor.MarkRequiresResync();
            return;
        }

        var sync = state.Editor.Sync(binding.Identity, text, field.MaxLength);
        if (!sync.HasExternalChange)
        {
            return;
        }

        state.History.Clear();
        state.Composing?.InvalidateForExternalChange();
        SnapSelectionToText(state, state.Editor.Text);
        node.Runtime.InvalidateLayout(node);
    }

    private static void SnapSelectionToText(TextEditorState state, string text)
    {
        var length = text.Length;
        state.Caret = TextGraphemeBoundaries.SnapToBoundary(text, Math.Clamp(state.Caret, 0, length));
        state.Anchor = TextGraphemeBoundaries.SnapToBoundary(text, Math.Clamp(state.Anchor, 0, length));
    }

    private static EditResult RejectedCommit(string reason)
        => new(TextEditStatus.Rejected, TextSnapshot.Empty, TextSnapshot.Empty, string.Empty, 0, reason);

    // ------------------------------------------------------------------ 组合接缝（TE-04 生效）

    private static void CancelCompositionForHostAction(ViewNode node, TextEditorState state)
    {
        if (state.Composing?.IsComposing != true)
        {
            return;
        }

        state.Composing.CancelComposition();
    }

    // ------------------------------------------------------------------ caret 可见性 / 滚动

    /// <summary>二维 caret 可见性：只调 ScrollY，不写绑定、不改光标与选区。组合期间按显示串的组合 caret 定位（含预编辑行）。</summary>
    private void EnsureCaretVisible(ViewNode node, TextEditorState state)
    {
        var inner = InnerRect(node.Frame);
        if (inner.Width <= 0 || inner.Height <= 0)
        {
            return;
        }

        // 组合期间必须按显示串排版与组合 caret 定位：预编辑可能把 caret 推到已提交文档之外，
        // 用已提交 caret 会把候选窗锚定到错误行。
        var display = GetDisplayLayout(node, node.Runtime.Services.TextService);
        var layout = display.Layout;
        var caretIndex = display.Composing
            ? Math.Clamp(display.CaretDisplay, 0, display.Text.Length)
            : layout.SnapToCaretStop(Math.Clamp(state.Caret, 0, layout.Text.Length));
        var line = layout.LineIndexForIndex(caretIndex, state.CaretAffinity);
        var lineBounds = layout.Lines[Math.Clamp(line, 0, layout.Lines.Count - 1)].Bounds;

        // caret 矩形完整落在内容区内：顶/底各不足 1 物理像素的吸附余量。
        var pixelScale = layout.PixelScale <= 0 ? 1.0 : layout.PixelScale;
        var snapMargin = 1.0 / pixelScale;

        var caretTop = lineBounds.Top;
        var caretBottom = lineBounds.Bottom;

        var maxScroll = MaxScrollY(layout, inner);
        if (caretBottom - state.ScrollY > inner.Height)
        {
            state.ScrollY = caretBottom - inner.Height + snapMargin;
        }
        else if (caretTop - state.ScrollY < 0)
        {
            state.ScrollY = Math.Max(0, caretTop - snapMargin);
        }

        state.ScrollY = Math.Clamp(state.ScrollY, 0, maxScroll);
    }

    /// <summary>可滚上限：内容高 − 可视高（不额外加 caret 高）。</summary>
    private static double MaxScrollY(TextLayoutResult layout, Rect inner)
        => Math.Max(0, layout.Size.Height - inner.Height);

    internal override bool OnWheel(InputContext context, ViewNode node, WheelEvent e)
    {
        if (node.IsEffectivelyDisabled)
        {
            return false;
        }

        var inner = InnerRect(node.Frame);
        var layout = GetLayout(node, node.Runtime.Services.TextService);
        var state = GetState(node);
        var maxScroll = MaxScrollY(layout, inner);
        if (maxScroll <= 0)
        {
            return false;
        }

        var axis = e.DeltaY;
        if (Math.Abs(axis) < 0.0001)
        {
            return false;
        }

        double distance;
        if (e.Mode == WheelDeltaMode.Pixels)
        {
            distance = axis;
        }
        else
        {
            var stepDistance = context.Runtime.Services.WheelScrollSettings.Settings.StepDistance(horizontal: false, inner.Height);
            distance = stepDistance * axis;
        }

        if (distance == 0 || !double.IsFinite(distance))
        {
            return false;
        }

        var next = Math.Clamp(state.ScrollY - distance, 0, maxScroll);
        if (Math.Abs(next - state.ScrollY) < 0.0001)
        {
            return false;
        }

        // 用户滚只改滚动，不改 caret、不 ensure（caret 可离视口）。
        state.ScrollY = next;
        node.Runtime.InvalidateRender();
        context.MarkHandled();
        return true;
    }

    // ------------------------------------------------------------------ 唤醒 / 时钟

    internal override TimeSpan? GetNextWakeup(ViewNode node, TimeSpan now)
    {
        if (!node.IsFocused)
        {
            return null;
        }

        var state = GetState(node);

        // 拖选越界自动滚动：返回固定 tick（一行/tick）；指针静止越界期间持续滚动，
        // 释放/取消/失焦/卸载/禁用即停。
        if (state.IsDragging && state.DragPointer is { } pointer)
        {
            var inner = InnerRect(node.Frame);
            if (inner.Height > 0 && (pointer.Y < inner.Top || pointer.Y > inner.Bottom))
            {
                return DragScrollTick;
            }
        }

        var period = BlinkInterval.Ticks * 2;
        if (period <= 0)
        {
            return null;
        }

        var elapsed = now.Ticks - state.BlinkEpoch.Ticks;
        var phase = ((elapsed % period) + period) % period;
        var remaining = phase < BlinkInterval.Ticks
            ? BlinkInterval.Ticks - phase
            : period - phase;
        return TimeSpan.FromTicks(Math.Max(1, remaining));
    }

    /// <summary>时钟推进：拖选越界时按固定步进推进 ScrollY（安全阶段，不写绑定、不递归布局）。</summary>
    internal override void OnClockTick(ViewNode node, TimeSpan now)
    {
        var state = node.InteractionState as TextEditorState;
        if (state is null || !node.IsFocused || !state.IsDragging || state.DragPointer is not { } pointer)
        {
            return;
        }

        var inner = InnerRect(node.Frame);
        if (inner.Height <= 0)
        {
            return;
        }

        // Due-gate：host 每轮 UI 循环都会调 OnClockTick（不只 wakeup 到期时），
        // 必须自限 ≤ 1 行 / DragScrollTick，否则任意事件都加速滚动、wakeup 失去节奏意义。
        if (state.LastDragScrollTick is { } lastTick && now - lastTick < DragScrollTick)
        {
            return;
        }

        var layout = GetLayout(node, node.Runtime.Services.TextService);
        var maxScroll = MaxScrollY(layout, inner);
        if (maxScroll <= 0)
        {
            return;
        }

        var lineHeight = layout.Lines.Count > 0 ? layout.Lines[0].Bounds.Height : Math.Max(1, (node.Environment.Font?.Size ?? 14) * 1.4);
        var delta = pointer.Y < inner.Top
            ? -Math.Min(lineHeight, Math.Abs(pointer.Y - inner.Top))
            : pointer.Y > inner.Bottom
                ? Math.Min(lineHeight, Math.Abs(pointer.Y - inner.Bottom))
                : 0;
        if (delta == 0)
        {
            return;
        }

        var next = Math.Clamp(state.ScrollY + delta, 0, maxScroll);
        if (Math.Abs(next - state.ScrollY) < 0.0001)
        {
            return;
        }

        state.ScrollY = next;
        state.LastDragScrollTick = now;

        // 拖选滚动时 caret 跟随指针所在行移动（推进到 pointer 所在行）。
        SyncEditor(node, state, invalidate: false);
        var display = GetDisplayLayout(node, node.Runtime.Services.TextService);
        var origin = TextOrigin(display, inner, state);
        var local = new Point(pointer.X - origin.X, pointer.Y - origin.Y);
        var stop = layout.HitTestIndex(local, out var affinity);
        state.Caret = stop;
        state.CaretAffinity = affinity;

        node.Runtime.InvalidateRender();
    }

    // ------------------------------------------------------------------ 排版缓存

    private TextLayoutResult GetLayout(ViewNode node, ITextService textService)
    {
        var text = Text.Read(node) ?? string.Empty;
        var font = node.Environment.Font ?? FontDescription.Default;
        var innerWidth = InnerRect(node.Frame).Width;
        var key = new TextEditorLayoutKey(text, font, node.Runtime.PixelScale, innerWidth);
        if (node.ViewCache is TextEditorMeasureCache cache && cache.Key.Equals(key))
        {
            return cache.Layout;
        }

        var layout = textService.Layout(TextPainter.CreateRequest(
            text, font, innerWidth, node.Runtime.PixelScale,
            wrapping: TextWrapping.Wrap, maxLines: int.MaxValue, lineHeight: 0) with { Editing = true });
        node.ViewCache = new TextEditorMeasureCache(text, font, node.Runtime.PixelScale, innerWidth, layout);
        return layout;
    }

    /// <summary>解析当前显示串及其真实排版；组合期间是“已提交前缀 + 预编辑 + 已提交后缀”。</summary>
    private TextEditorDisplayLayout GetDisplayLayout(ViewNode node, ITextService textService)
    {
        var text = Text.Read(node) ?? string.Empty;
        var font = node.Environment.Font ?? FontDescription.Default;
        var state = node.InteractionState as TextEditorState;
        var innerWidth = InnerRect(node.Frame).Width;
        if (state?.Composing is not { IsComposing: true } client
            || client.Composition.Active is not { } anchor)
        {
            return new TextEditorDisplayLayout(text, GetLayout(node, textService), Composing: false, 0, 0, 0, []);
        }

        var preedit = anchor.Preedit;
        var start = Math.Clamp(anchor.Replacement.Start, 0, text.Length);
        var end = Math.Clamp(anchor.Replacement.End, start, text.Length);
        var displayText = string.Concat(text.AsSpan(0, start), preedit, text.AsSpan(end));
        var pixelScale = node.Runtime.PixelScale;

        TextLayoutResult layout;
        if (state.DisplayLayoutCache is { } cache
            && cache.Text == displayText
            && cache.Font.Equals(font)
            && cache.PixelScale.Equals(pixelScale)
            && cache.InnerWidth.Equals(innerWidth))
        {
            layout = cache.Layout;
        }
        else
        {
            layout = textService.Layout(TextPainter.CreateRequest(
                displayText, font, innerWidth, pixelScale,
                wrapping: TextWrapping.Wrap, maxLines: int.MaxValue, lineHeight: 0) with { Editing = true });
            state.DisplayLayoutCache = new TextEditorDisplayCache(displayText, font, pixelScale, innerWidth, layout);
        }

        var preeditCaret = Math.Clamp(anchor.Caret, 0, preedit.Length);
        return new TextEditorDisplayLayout(
            displayText,
            layout,
            Composing: true,
            start,
            preedit.Length,
            start + preeditCaret,
            anchor.Segments);
    }

    private Rect InnerRect(Rect frame)
        => frame.Deflate(new Thickness(HorizontalPadding, VerticalPadding));

    private bool IsCaretVisible(ViewNode node, TextEditorState state)
    {
        var period = BlinkInterval.Ticks * 2;
        if (period <= 0)
        {
            return true;
        }

        var elapsed = node.Runtime.Clock.Now.Ticks - state.BlinkEpoch.Ticks;
        var phase = ((elapsed % period) + period) % period;
        return phase < BlinkInterval.Ticks;
    }

    private static TextEditorState GetState(ViewNode node)
    {
        if (node.InteractionState is TextEditorState state)
        {
            return state;
        }

        state = new TextEditorState(node.Runtime);
        node.InteractionState = state;
        return state;
    }

    internal override void OnMount(ViewNode node)
    {
        var state = GetState(node);
        state.BlinkEpoch = node.Runtime.Clock.Now;
        SyncEditor(node, state, invalidate: false);
    }

    internal override void OnUpdate(ViewNode node, View previous)
    {
        if (node.InteractionState is not TextEditorState state)
        {
            return;
        }

        // 同节点同绑定/纯重布局不触发取消；真实来源/文本/MaxLength 变化由 SyncEditor 识别。
        SyncEditor(node, state, invalidate: false);
    }

    internal override void OnUnmount(ViewNode node)
    {
        if (node.InteractionState is not TextEditorState state)
        {
            return;
        }

        // 真正卸载：先在本地取消未提交段，再按所有者撤销文本输入归属（先失效、后通知服务），
        // 最后清空组合客户端。旧节点卸载不会结束已经属于另一个节点的新会话。
        CancelCompositionForHostAction(node, state);
        state.EndInputSession();
        state.ClearComposing();
        state.IsDragging = false;
        state.DragPointer = null;
    }

    internal override void PrepareTextInputGeometry(ViewNode node)
    {
        if (node.InteractionState is TextEditorState state)
        {
            SyncEditor(node, state, invalidate: false);
        }
    }

    /// <summary>
    /// 最终布局后的安全阶段：发布逻辑像素的 caret / 组合 / 排除几何（TE-04）。
    /// </summary>
    /// <remarks>
    /// 几何来自显示串的真实排版原点（已吸附物理像素）与真实 caret，包含 <see cref="TextEditorState.ScrollY"/>、
    /// 祖先 <see cref="ScrollView"/> 滚动（<see cref="ViewNode.Frame"/> 是绝对坐标）、Padding/Frame、祖先裁剪与
    /// 自身内容区裁剪。完全被裁出、不可见或没有有效文本输入会话时发布
    /// <see cref="TextInputGeometry.Unavailable"/>，由客户端按同值去重，避免每帧重复唤醒平台。
    /// 本方法只在 <see cref="UiRuntime.ProcessPendingWork"/> 的安全阶段被调用：不排版、不重建组件、不递归生成帧。
    /// </remarks>
    internal override void PublishTextInputGeometry(ViewNode node)
    {
        if (node.InteractionState is not TextEditorState state)
        {
            return;
        }

        if (state.Composing is not { } client
            || !ReferenceEquals(state.InputSession.ActiveClient, client))
        {
            return;
        }

        TextInputGeometry geometry;
        CompositionCharGeometrySnapshot? charGeometry;
        try
        {
            // 外部文本变短时上一帧的 ScrollY 可能仍很大：先收敛越界滚动。
            // 用户滚允许 caret 离视口，不能无条件 ensure（会把刚滚走的内容拉回，与 OnWheel 打架）；
            // 组合期间除外——组合 caret 必须保持可见以锚定候选窗。
            var inner = InnerRect(node.Frame);
            var layout = GetLayout(node, node.Runtime.Services.TextService);
            state.ScrollY = Math.Clamp(state.ScrollY, 0, MaxScrollY(layout, inner));
            if (client.IsComposing)
            {
                EnsureCaretVisible(node, state);
            }

            geometry = ComputeInputGeometry(node, state, client, out charGeometry);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            node.Runtime.ReportDiagnostic($"发布文本输入几何失败（节点 {node.ShortDebugPath()}）：{ex.GetType().Name}");
            geometry = TextInputGeometry.Unavailable;
            charGeometry = null;
        }

        // 逐字符几何快照与锚点同帧同源更新（R17）：平台位置查询只读该快照；
        // 几何不可用时快照为 null，客户端立即拒绝应答，绝不回落旧位置。
        client.UpdateCompositionCharGeometry(charGeometry);
        client.PublishGeometry(geometry);
    }

    /// <summary>从最终布局、显示串排版、滚动偏移与裁剪链计算候选锚点几何（多行版本）。</summary>
    private TextInputGeometry ComputeInputGeometry(
        ViewNode node,
        TextEditorState state,
        TextCompositionClient client,
        out CompositionCharGeometrySnapshot? charGeometry)
    {
        charGeometry = null;
        if (!node.IsMounted
            || !node.IsFocused
            || node.IsEffectivelyDisabled
            || node.Environment.Opacity <= 0
            || node.Frame.IsEmpty)
        {
            return TextInputGeometry.Unavailable;
        }

        var inner = InnerRect(node.Frame);
        if (inner.IsEmpty)
        {
            return TextInputGeometry.Unavailable;
        }

        var display = GetDisplayLayout(node, node.Runtime.Services.TextService);
        var clip = EffectiveClip(node, inner);
        if (clip.IsEmpty)
        {
            return TextInputGeometry.Unavailable;
        }

        // 与 Emit 同一原点（TextOrigin：textBounds 左上角吸附，顶部对齐）。不能用
        // TextPainter.ResolveOrigin——它在 contentHeight < inner 时垂直居中，会把发布锚点
        // 相对绘制位置下推 (inner.Height − layout.Height)/2。
        var origin = TextOrigin(display, inner, state);

        // 与绘制共用同一 caret 解析：位置来自真实排版原点 + 排版 caret + affinity，不使用绘制端夹取后的矢量线。
        var caretRect = CaretRectAt(display, state, origin);

        // 组合矩形对外是单个 Rect（TextInputGeometry 契约），只能取逐行边界的包围盒；
        // 真正的逐行不填空绘制在 Emit 的段下划线与选区（GetRangeRects）。
        Rect compositionRect = Rect.Empty;
        if (display.Composing && display.PreeditLength > 0)
        {
            var rects = display.Layout.GetRangeRects(display.PreeditStart, display.PreeditStart + display.PreeditLength);
            foreach (var rect in rects)
            {
                var shifted = Offset(rect, origin);
                compositionRect = compositionRect.IsEmpty ? shifted : compositionRect.Union(shifted);
            }
        }

        var visibleCaret = clip.Contains(caretRect) ? caretRect : caretRect.Intersect(clip);
        if (visibleCaret.IsEmpty || visibleCaret.Height <= 0)
        {
            return TextInputGeometry.Unavailable;
        }

        var preedit = display.Composing
            && display.PreeditStart >= 0
            && display.PreeditStart + display.PreeditLength <= display.Text.Length
                ? display.Text.Substring(display.PreeditStart, display.PreeditLength)
                : string.Empty;
        charGeometry = new CompositionCharGeometrySnapshot
        {
            Layout = display.Layout,
            Preedit = preedit,
            PreeditStart = preedit.Length > 0 ? display.PreeditStart : 0,
            CaretIndex = display.Composing
                ? Math.Clamp(display.CaretDisplay, 0, display.Text.Length)
                : display.Layout.SnapToCaretStop(Math.Clamp(state.Caret, 0, display.Text.Length)),
            Origin = origin,
            EditableClip = clip,
            Epoch = client.Epoch,
            DocumentVersion = state.Editor.Version,
            LayoutSerial = node.Runtime.LayoutSerial,
            ScrollX = state.ScrollX,
            ScrollY = state.ScrollY,
            Affinity = state.CaretAffinity,
        };

        var visibleComposition = compositionRect.IsEmpty ? Rect.Empty : compositionRect.Intersect(clip);
        return new TextInputGeometry(visibleCaret, visibleComposition, visibleCaret.Union(visibleComposition));
    }

    /// <summary>
    /// 节点内容区的有效裁剪矩形：窗口内容区 ∩ 所有祖先的 <see cref="View.GetChildClip"/> ∩ 自身内容区。
    /// </summary>
    private static Rect EffectiveClip(ViewNode node, Rect inner)
    {
        var viewport = node.Runtime.ViewportSize;
        var clip = new Rect(0, 0, viewport.Width, viewport.Height);
        if (clip.IsEmpty)
        {
            return Rect.Empty;
        }

        for (var ancestor = node.Parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (ancestor.View.GetChildClip(ancestor) is { } childClip)
            {
                clip = clip.Intersect(childClip);
                if (clip.IsEmpty)
                {
                    return Rect.Empty;
                }
            }
        }

        return clip.Intersect(inner);
    }

    private static Rect Offset(Rect bounds, Point origin)
        => new(origin.X + bounds.X, origin.Y + bounds.Y, bounds.Width, bounds.Height);

    internal override bool IsLayoutEquivalent(View other) => false;

    // ------------------------------------------------------------------ 缓存 / 显示布局

    /// <summary>文档排版缓存键（值类型，比较零分配）。</summary>
    internal readonly record struct TextEditorLayoutKey(string Text, FontDescription Font, double PixelScale, double InnerWidth);

    internal sealed class TextEditorMeasureCache(string text, FontDescription font, double pixelScale, double innerWidth, TextLayoutResult layout)
    {
        public string Text { get; } = text;

        public FontDescription Font { get; } = font;

        public double PixelScale { get; } = pixelScale;

        public double InnerWidth { get; } = innerWidth;

        public TextLayoutResult Layout { get; } = layout;

        public TextEditorLayoutKey Key => new(Text, Font, PixelScale, InnerWidth);
    }

    internal sealed class TextEditorDisplayCache(string text, FontDescription font, double pixelScale, double innerWidth, TextLayoutResult layout)
    {
        public string Text { get; } = text;
        public FontDescription Font { get; } = font;
        public double PixelScale { get; } = pixelScale;
        public double InnerWidth { get; } = innerWidth;
        public TextLayoutResult Layout { get; } = layout;
    }
}

/// <summary>
/// TextEditor 的显示串视图（TE-03/TE-04）：显示文本、其真实排版、预编辑范围与组合 caret。
/// </summary>
internal readonly record struct TextEditorDisplayLayout(
    string Text,
    TextLayoutResult Layout,
    bool Composing,
    int PreeditStart,
    int PreeditLength,
    int CaretDisplay,
    IReadOnlyList<TextRange> Segments);

/// <summary>TextEditor 的运行时编辑状态（保存在节点上，组件重建后保持）。</summary>
internal sealed class TextEditorState : ITextEditSessionOwner
{
    /// <summary>原子编辑会话（TI-03）：确认文本、文档版本、来源身份与失效接缝。</summary>
    public TextEditSession Editor { get; } = new();

    /// <summary>显示串排版缓存（TE-04 生效；与文档排版分开）。</summary>
    public TextEditor.TextEditorDisplayCache? DisplayLayoutCache { get; set; }

    /// <summary>有界撤销/重做历史（TI-05）：与运行时节点同寿命。</summary>
    public TextEditHistory History { get; }

    /// <summary>文本输入会话接线（运行时唯一实例）：绑定/解绑焦点客户端并收敛取消（TI-04）。</summary>
    public TextInputServiceSession InputSession { get; }

    /// <summary>
    /// 本节点当前原生按键的身份 token（TI-07 接缝）；无原生信息时使用稳定实例，
    /// “结束键归属”判定不依赖时间窗或文本内容。
    /// </summary>
    public object NativeKeyToken { get; } = new();

    /// <summary>组合客户端（惰性创建）；只有真实焦点会话建立后才存在，卸载时清空。</summary>
    public TextCompositionClient? Composing { get; private set; }

    /// <summary>光标位置的 UTF-16 索引（始终位于字素边界）。</summary>
    public int Caret { get; set; }

    /// <summary>选择锚点；与 <see cref="Caret"/> 相同表示无选择。</summary>
    public int Anchor { get; set; }

    /// <summary>横向滚动偏移（逻辑像素）；TextEditor 无横向滚动，恒为 0。</summary>
    public double ScrollX => 0;

    /// <summary>纵向滚动偏移（逻辑像素）。</summary>
    public double ScrollY { get; set; }

    /// <summary>上下行导航时保留的期望 X（列位置）；非上下 caret 变化清空。</summary>
    public double? PreferredX { get; set; }

    /// <summary>当前 caret 的跨行归属（<see cref="TextAffinity"/>）；点击/导航实时更新。</summary>
    public TextAffinity CaretAffinity { get; set; }

    /// <summary>是否正在用指针拖选。</summary>
    public bool IsDragging { get; set; }

    /// <summary>拖选期间最近一次指针位置（越界检测与边缘滚动方向）。</summary>
    public Point? DragPointer { get; set; }

    /// <summary>最近一次边缘滚动 tick 的时钟值（≤1 行/80ms 的 due-gate；null = 尚未 tick，立即滚）。</summary>
    public TimeSpan? LastDragScrollTick { get; set; }

    /// <summary>光标闪烁计时起点。</summary>
    public TimeSpan BlinkEpoch { get; set; }

    /// <summary>选择起点。</summary>
    public int SelectionStart => Math.Min(Caret, Anchor);

    /// <summary>选择终点（不含）。</summary>
    public int SelectionEnd => Math.Max(Caret, Anchor);

    /// <summary>是否存在选择。</summary>
    public bool HasSelection => Caret != Anchor;

    public TextEditorState(UiRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        InputSession = runtime.TextInputSession;
        History = new TextEditHistory(runtime.Services.HistoryCapacity);
    }

    /// <summary>取得组合客户端；新 epoch 创建新客户端，绝不复用已释放的旧实例。</summary>
    public TextCompositionClient RequireComposing(ViewNode node, int epoch)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (Composing is { IsReleased: false } existing)
        {
            return existing;
        }

        Composing = new TextCompositionClient(node, this, epoch);
        return Composing;
    }

    /// <summary>清空组合客户端引用（节点卸载时调用；旧实例已由接线条目释放）。</summary>
    public void ClearComposing()
    {
        Composing?.ResetKeyOwnership();
        Composing = null;
    }

    /// <summary>结束本状态上的焦点文本输入会话（仅当本节点仍是当前所有者；幂等）。</summary>
    public bool EndInputSession()
        => Composing is { } client && InputSession.EndForOwner(client);

    /// <summary>在安全阶段结束本节点会话（布局/绘制阶段不得直接调用平台服务）。</summary>
    public void RequestDeferredEndInputSession()
    {
        if (Composing is { IsReleased: false } client)
        {
            InputSession.RequestEndForOwner(client);
        }
    }

    /// <summary>撤销归属前的本地取消：恢复活动段开始前的选区并清空未提交段。</summary>
    public void CancelCompositionForRelease()
    {
        if (Composing is { IsComposing: true } client)
        {
            client.CancelComposition(restoreSelection: true);
        }
    }

    /// <summary>声明一次原生按键的归属（TI-07 接缝）；返回控件是否应在本次 KeyDown 中放弃该按键。</summary>
    public bool TrackOriginatingKey(object nativeKeyToken, bool consumedByInputMethod)
        => Composing?.TrackOriginatingKey(nativeKeyToken, consumedByInputMethod) ?? false;

    /// <summary>指定 token 的按键是否已被输入法显式消费（一次性）。</summary>
    public bool IsExplicitlyConsumedKey(object nativeKeyToken)
        => Composing?.IsExplicitlyConsumedKey(nativeKeyToken) ?? false;

    /// <inheritdoc cref="ITextEditSessionOwner.SyncCompositionDocument"/>
    public bool SyncCompositionDocument(ViewNode node)
    {
        if (node.View is not TextEditor field)
        {
            return false;
        }

        field.SyncCompositionDocument(node, this, out var invalidated);
        return invalidated;
    }

    /// <inheritdoc cref="ITextEditSessionOwner.ApplyCompositionCommit"/>
    public EditResult ApplyCompositionCommit(ViewNode node, TextRange range, string result)
        => node.View is TextEditor field
            ? field.ApplyCompositionCommit(node, this, range, result)
            : default;

    /// <inheritdoc cref="ITextEditSessionOwner.MaxLength"/>
    public int MaxLength(ViewNode node)
        => node.View is TextEditor field ? field.MaxLength : int.MaxValue;

    /// <inheritdoc cref="ITextEditSessionOwner.TextIdentity"/>
    public object TextIdentity(ViewNode node)
        => node.View is TextEditor field ? field.Text.Identity : this;

    /// <inheritdoc cref="ITextEditSessionOwner.IsTextEditNode"/>
    public bool IsTextEditNode(ViewNode node)
        => node.IsMounted && node.View is TextEditor;

    /// <inheritdoc cref="ITextEditSessionOwner.IsEditableNode"/>
    public bool IsEditableNode(ViewNode node)
        => node.IsMounted && !node.IsEffectivelyDisabled && node.View is TextEditor;
}
