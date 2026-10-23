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

            // 布局阶段可判定 Disabled：禁用是取消接缝。取消只在安全阶段通知平台（不在 Measure/Arrange 内
            // 调用外部服务），并按所有者结束会话，不会结束别的新会话。
            if (node.IsEffectivelyDisabled && state.Composing is not null)
            {
                CancelCompositionForHostAction(node, state);
                state.RequestDeferredEndInputSession();
            }

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

        // 记录编辑/命中使用的“文档排版”与绘制使用的“显示串排版”是两份缓存（TI-06）：
        // 预编辑只进入显示串，绝不写 Binding，也不能污染文档排版（否则命中与编辑范围会按显示文本计算）。
        var layout = GetLayout(node, context.TextService);

        // 纯重绘入口也要观测真实绑定值：外部写入可能只请求重绘（没有描述重建、没有重新布局）。
        // 复用本次文档排版结果，不递归 GetLayout，也不在这里失效布局。
        SyncEditor(node, state, invalidate: false, knownLayout: layout);
        var display = GetDisplayLayout(node, context.TextService);
        var text = display.Text;

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

        // 组合期间保证的是组合 caret（显示串坐标）可见：长预编辑会横向滚动，而不是把组合 caret 画到内容区外。
        EnsureCaretVisible(node, state, display.Layout, inner);

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

        // 文字纹理在内容区内垂直居中，因此光标、选区、预编辑下划线与点击命中都必须从同一个文本原点出发；
        // 直接用 inner.Y 会让光标随输入框变高/字号变大而向上错位。
        // 该原点再统一吸附到物理像素：文字纹理、光标、选区、下划线与点击命中共用同一变换（见 TextPainter.ResolveOrigin）。
        // 同一函数也供最终布局后的候选锚点发布使用，避免“绘制位置”与“发布几何”各算一套。
        var textBounds = DisplayTextBounds(display, inner, state);
        var origin = TextPainter.ResolveOrigin(textBounds, display.Layout.Size, TextAlignment.Left, display.Layout.PixelScale);
        var originX = origin.X;
        var originY = origin.Y;

        // 选择高亮只在已提交文本上绘制：组合期间原选区被预编辑整体替代，显示的是“前缀 + 预编辑 + 后缀”，
        // 再画文档选区会与替换后的显示不一致。
        if (!display.Composing && state.HasSelection && node.IsFocused)
        {
            var selection = display.Layout.GetRangeBounds(state.SelectionStart, state.SelectionEnd);
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
        // 保证纹理、光标、选区与预编辑装饰使用同一坐标约定。
        TextPainter.Paint(context, node, display.Layout, textColor, textBounds, TextAlignment.Left);

        if (display.Composing)
        {
            DrawCompositionDecorations(context, node, state, display, theme, origin, inner, opacity);
        }
        else if (node.IsFocused && IsCaretVisible(node, state))
        {
            // 普通已提交 caret：位置与“发布给候选锚点的几何”共用同一计算（CaretRectAt）。
            var caretRect = ClampCaretToInner(CaretRectAt(display, state, origin), inner);
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

    /// <summary>
    /// 绘制预编辑装饰：每段下划线 + 活动段加粗强调色 + 组合 caret（显示串坐标）。
    /// </summary>
    /// <remarks>
    /// 活动段是包含组合 caret 的分段（caret 恰好落在分段边界时取其后一段；caret 在末尾时取最后一段）；
    /// 没有分段信息时整段预编辑视为一个活动段。所有装饰与文字纹理共用 <paramref name="origin"/>，
    /// 因此相对位置不受 DPI、Padding 或横向滚动影响。
    /// </remarks>
    private void DrawCompositionDecorations(
        DrawContext context,
        ViewNode node,
        TextFieldState state,
        TextFieldDisplayLayout display,
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

            var bounds = display.Layout.GetRangeBounds(start, end);
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

        if (!node.IsFocused || !IsCaretVisible(node, state))
        {
            return;
        }

        // 组合 caret 的 UTF-16 偏移可能不在字素边界上：按排版可用 caret 规则吸附显示位置，
        // 但绝不改写组合状态里的原生 caret 偏移。
        var caretRect = ClampCaretToInner(CaretRectAt(display, state, origin), inner);
        context.Commands.Add(new RectangleCommand(caretRect, theme.Accent, 0, null, 0, opacity));
    }

    /// <summary>显示串在内容区内的目标矩形（横向滚动 + 垂直居中）；文字纹理、装饰与命中共用。</summary>
    private static Rect DisplayTextBounds(TextFieldDisplayLayout display, Rect inner, TextFieldState state)
        => new(
            inner.X - state.ScrollX,
            TextPainter.VerticalOrigin(inner, display.Layout.Size),
            display.Layout.Size.Width,
            display.Layout.Size.Height);

    /// <summary>
    /// 按当前显示串解析有效 caret 的未裁剪矩形：组合期间是组合 caret（显示串坐标），
    /// 否则是已提交文档 caret。位置只由排版原点 + 排版 caret 决定，不受绘制端夹取影响。
    /// </summary>
    private static Rect CaretRectAt(TextFieldDisplayLayout display, TextFieldState state, Point origin)
    {
        var caretIndex = display.Composing
            ? display.CaretDisplay
            : display.Layout.SnapToCaretStop(Math.Clamp(state.Caret, 0, display.Text.Length));
        var caret = display.Layout.GetCaretBounds(caretIndex, 1.5);
        return new Rect(origin.X + caret.X, origin.Y + caret.Y, caret.Width, caret.Height);
    }

    /// <summary>
    /// 绘制端把 caret 夹回内容区，避免末端吸附/最大滚动造成不足 1 像素的整条不可见；
    /// 发布几何使用未夹取的 <see cref="CaretRectAt"/> 与真实裁剪求交，绝不用夹取后的位置掩盖文本错位。
    /// </summary>
    private static Rect ClampCaretToInner(Rect caretRect, Rect inner)
        => caretRect.Width <= inner.Width
            ? caretRect with { X = Math.Clamp(caretRect.X, inner.Left, inner.Right - caretRect.Width) }
            : caretRect;

    /// <summary>返回包含组合 caret 的活动分段索引；caret 落在分段间隙时取其后一段，末尾取最后一段。</summary>
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

    internal override bool OnPointerDown(InputContext context, ViewNode node, PointerEvent e)
    {
        if (node.IsEffectivelyDisabled)
        {
            return false;
        }

        var state = GetState(node);
        context.Runtime.SetFocus(node);
        SyncEditor(node, state, invalidate: true);

        // 鼠标重新定位选区属于取消接缝：先取消未提交组合，再处理点击。
        // 命中测试必须随后基于<b>正式文档</b>（GetLayout）而不是组合显示串：取消后再算 caret，
        // 保证“显示替换”与“点击意图”使用同一份已提交文本；取消本身不会写绑定。
        CancelCompositionForHostAction(node, state);

        // 长预编辑可能留下很大的横向滚动：取消后先按正式文档与恢复后的选区收敛 ScrollX，
        // 再计算命中；否则 PointerDown 会用显示串的旧滚动把光标落到错误位置。
        EnsureCaretVisible(node, state);

        // 指针/选区操作（即使随后移回原处）都是分组边界。
        state.History.BreakMerge();

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
        state.History.BreakMerge();
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
        state.History.BreakMerge();
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

        // 焦点变化（进入/离开）是分组边界：不清空历史，但后续键入不得与失焦前的键入合并。
        state.History.BreakMerge();

        if (focused)
        {
            // 焦点变化是安全入口：先按真实绑定值同步会话（外部写入可能已发生），再限制选区。
            SyncEditor(node, state, invalidate: true);
            var text = Text.Read(node) ?? string.Empty;
            state.Caret = ClampIndex(node, state.Caret, text);
            state.Anchor = ClampIndex(node, state.Anchor, text);
            EnsureCaretVisible(node, state);

            // 焦点获得后才建立文本输入会话；没有服务/仅提交文本时保持既有路径（会话为空操作）。
            // 每次新会话都分配新 epoch，并创建新客户端（已释放的旧客户端绝不会被复用）。
            state.InputSession.Start(node, epoch => state.RequireComposing(node, epoch));
        }
        else
        {
            // 失去焦点：先取消未提交组合，再撤销会话归属（先失效、后通知服务），最后收敛选区。
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

        // 组合期间的“已提交字符”通道不做写入：该输入的归属是输入法消息，由组合结果独占落地。
        // 结束组合是取消接缝，随后仍走既有的已提交文本路径（不重放组合批次）。
        if (state.Composing?.IsComposing == true)
        {
            CancelCompositionForHostAction(node, state);
            context.MarkHandled();
            return true;
        }

        // 单行净化先行：全部被过滤（例如只有换行/制表符）时不产生事务，也不写绑定。
        var inserted = TextInputEvent.SanitizeSingleLine(e.Text);
        if (inserted.Length == 0)
        {
            context.MarkHandled();
            return true;
        }

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
    /// <remarks>
    /// <b>历史记录（TI-05）：</b>只有真实事务精确落地（回读与请求候选一致、节点仍挂载、来源/MaxLength 未变）时
    /// 才记录增量事务；规范化、setter 明确拒绝、写/读异常与外部变化都清理不可信历史，绝不记录错误逆操作。
    /// 容量拒绝与真正的空操作既不新增历史，也不凭空清空 redo。
    /// </remarks>
    private void ApplyEdit(ViewNode node, TextFieldState state, EditOperation operation)
    {
        // 事务期间协调更新是延迟的，node.View 通常仍是本实例；仍按当前视图读取绑定，避免使用过时的描述。
        if (node.View is not TextField field)
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
            // setter 已改写或回读失败：按会话当前真实文本恢复选区/布局，但不掩盖原异常。
            RecoverAfterTransactionFailure(node, state);
            state.History.Clear();
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
            // 事务期间换源/换配置：历史上下文已不可信。
            state.History.Clear();
            SyncEditor(node, state, invalidate: true);
            return;
        }

        // 事务读取点观测到外部文本/来源/MaxLength 变化：旧历史上下文失效（即使范围仍恰好有效）。
        if (state.Editor.LastTransactionSawExternalChange)
        {
            state.History.Clear();
        }

        if (!result.Changed)
        {
            // 拒绝 / 未变化：不做投机选区提交，但把会话与选区对齐到真实回读值（外部变化可能已发生）。
            // setter 明确拒绝写入后回读不可信，必须清理历史；容量拒绝与空操作则保留历史与 redo。
            if (state.Editor.LastTransaction == TextEditTransactionOutcome.SetterRefused)
            {
                state.History.Clear();
            }

            SyncEditor(node, state, invalidate: false);
            return;
        }

        // 回读值才是唯一事实来源：会话已按实际文本吸附光标，这里沿用其结果快照。
        state.Caret = result.After.Selection.Active;
        state.Anchor = result.After.Selection.Anchor;

        if (state.Editor.LastTransaction == TextEditTransactionOutcome.Applied)
        {
            state.History.Record(TextEditHistory.FromTransaction(result, operation, beforeSelection), node.Runtime.Clock.Now);
        }
        else
        {
            // 规范化等非精确落地：不记录基于“请求替换”的错误逆操作。
            state.History.Clear();
        }

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
        string text;
        try
        {
            text = knownLayout?.Text ?? binding.Read(node) ?? string.Empty;
        }
        catch
        {
            // 真实读取失败：历史上下文已不可信，两栈必须实际失效（异常沿用既有契约继续上抛）。
            state.History.Clear();
            throw;
        }

        if (state.Editor.IsOwnPendingWrite(text) && state.Editor.MatchesContext(binding.Identity, field.MaxLength))
        {
            // 本次读取正是自己的事务写入、且来源与配置未变：不能当成外部变化清空版本或移动选区。
            return;
        }

        var sync = state.Editor.Sync(binding.Identity, text, field.MaxLength);
        if (!sync.HasExternalChange)
        {
            // 同值回读是真正的空操作：不推进版本、不清历史，也不打断合并。
            return;
        }

        // 外部文本 / 来源 / MaxLength 真正变化：历史上下文失效，清空两栈（同值回读不会走到这里）。
        state.History.Clear();

        // 外部文本 / 来源 / MaxLength 真正变化：先取消未提交组合（预编辑绝不写绑定），再收敛选区。
        // 只在真实外部变化时取消；同节点同绑定重建、Flow/Adaptive 重排不会走到这里。
        state.Composing?.InvalidateForExternalChange();

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
        try
        {
            return HandleKeyDown(context, node, state, e);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 命令路径中的真实读取/事务异常（getter、事务回读等）都让历史不可信；
            // 异常沿用既有契约继续上抛，不能被历史清理掩盖。
            state.History.Clear();
            throw;
        }
    }

    private bool HandleKeyDown(InputContext context, ViewNode node, TextFieldState state, KeyEvent e)
    {
        SyncEditor(node, state, invalidate: false);

        // 输入法显式声明消费的同一次原生按键始终优先：带命令修饰符（Ctrl/Command+Z 等）也不能执行编辑命令，
        // 覆盖“组合在同一原生按键内结束、随后仍进入 KeyDown”。该判定只认显式 token，不把“正在组合”算作已消费。
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
            state.BlinkEpoch = node.Runtime.Clock.Now;
            ExecuteCommand(node, state, resolved);
            return true;
        }

        // 非命令键：组合期间方向键/Backspace/Delete/空格/Enter/Escape 归输入法所有；Tab 的消费结果同样在这里决定。
        // 未被输入法消费的 Undo/Redo/Cut/Paste/SelectAll 已经在上面按命令处理（先取消预编辑再执行）。
        if (state.Composing?.IsComposing == true)
        {
            state.BlinkEpoch = node.Runtime.Clock.Now;
            context.MarkHandled();
            return true;
        }

        var text = Text.Read(node) ?? string.Empty;
        var extend = e.Modifiers.HasFlag(KeyModifiers.Shift);
        state.BlinkEpoch = node.Runtime.Clock.Now;

        switch (e.Key)
        {
            case UiKey.Left:
                CancelCompositionForHostAction(node, state);
                state.History.BreakMerge();
                MoveCaret(node, state, text, backward: true, extend);
                return true;
            case UiKey.Right:
                CancelCompositionForHostAction(node, state);
                state.History.BreakMerge();
                MoveCaret(node, state, text, backward: false, extend);
                return true;
            case UiKey.Home:
                CancelCompositionForHostAction(node, state);
                state.History.BreakMerge();
                SetCaret(node, state, 0, extend);
                return true;
            case UiKey.End:
                CancelCompositionForHostAction(node, state);
                state.History.BreakMerge();
                SetCaret(node, state, text.Length, extend);
                return true;
            case UiKey.Backspace:
                CancelCompositionForHostAction(node, state);
                text = Text.Read(node) ?? string.Empty;
                if (!state.HasSelection && state.Caret == 0)
                {
                    return true;
                }

                DeleteSelectionOrGrapheme(node, state, text, backward: true, TextEditKind.Backspace);
                return true;
            case UiKey.Delete:
                CancelCompositionForHostAction(node, state);
                text = Text.Read(node) ?? string.Empty;
                if (!state.HasSelection && state.Caret >= text.Length)
                {
                    return true;
                }

                DeleteSelectionOrGrapheme(node, state, text, backward: false, TextEditKind.Delete);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// 执行统一内部编辑命令：Undo/Redo 先取消预编辑（空栈也一样），剪贴板命令与全选先取消预编辑，
    /// 复制只读取已提交选区且不取消组合。所有命令都是合并分组边界。
    /// </summary>
    private void ExecuteCommand(ViewNode node, TextFieldState state, TextEditCommand command)
    {
        switch (command)
        {
            case TextEditCommand.Copy:
                // 复制只读取已提交选区：组合期间不复制预编辑，也不因此取消组合；但仍构成分组边界。
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

    /// <summary>
    /// 撤销/重做一次历史事务：与普通编辑走同一条 TI-03 原子替换通道（写一次绑定、真实回读），
    /// 只有精确确认成功后才移动栈并恢复存档的方向选区；重放本身不记录新历史。
    /// </summary>
    private void ReplayHistory(ViewNode node, TextFieldState state, bool undo)
    {
        var history = state.History;

        // 空栈：不调用 setter、不推进版本、不制造事务（预编辑已在调用前取消）。
        if (undo ? !history.CanUndo : !history.CanRedo)
        {
            return;
        }

        if (node.View is not TextField field)
        {
            return;
        }

        var binding = field.Text;
        var entry = undo ? history.UndoPeek! : history.RedoPeek!;

        // 文档不再符合历史上下文时拒绝旧记录并清理（增量记录无法安全重放）。
        string current;
        try
        {
            current = binding.Read(node) ?? string.Empty;
        }
        catch
        {
            history.Clear();
            state.Editor.MarkRequiresResync();
            return;
        }

        var expected = undo ? entry.Inserted : entry.Deleted;
        if (!MatchesHistoryContext(current, entry.ReplacementStart, expected))
        {
            history.Clear();
            return;
        }

        var range = undo
            ? new TextRange(entry.ReplacementStart, entry.ReplacementStart + entry.Inserted.Length)
            : new TextRange(entry.ReplacementStart, entry.ReplacementStart + entry.Deleted.Length);

        if (!EditOperation.TryCreate(
                range,
                undo ? entry.Deleted : entry.Inserted,
                undo ? TextEditKind.Undo : TextEditKind.Redo,
                TextEditReason.History,
                scrubSingleLine: false,
                out var operation,
                out _))
        {
            history.Clear();
            return;
        }

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
            RecoverAfterTransactionFailure(node, state);
            history.Clear();
            throw;
        }

        if (!node.IsMounted || node.View is not TextField mounted)
        {
            return;
        }

        if (!ReferenceEquals(mounted.Text.Identity, binding.Identity) || mounted.MaxLength != field.MaxLength)
        {
            // 事务期间换源/换配置：旧事务不得移动栈，也不得把恢复选区写进新客户端。
            history.Clear();
            SyncEditor(node, state, invalidate: true);
            return;
        }

        if (state.Editor.LastTransactionSawExternalChange)
        {
            // 重放读取点观测到外部变化：历史上下文失效，拒绝旧记录并清理。
            history.Clear();
            return;
        }

        if (!result.Changed)
        {
            // 拒绝 / 未变化：不假装撤销成功，栈保持原位；setter 明确拒绝写入时历史已不可信。
            if (state.Editor.LastTransaction == TextEditTransactionOutcome.SetterRefused)
            {
                history.Clear();
            }

            return;
        }

        if (state.Editor.LastTransaction != TextEditTransactionOutcome.Applied)
        {
            // 规范化/写后读错等：不假装成功，清理不可信历史并按实际回读收敛选区。
            history.Clear();
            state.Caret = result.After.Selection.Active;
            state.Anchor = result.After.Selection.Anchor;
            node.Runtime.InvalidateRender();
            EnsureCaretVisible(node, state);
            return;
        }

        // 精确确认成功后才移动栈；版本由事务单调推进，绝不回退到历史版本。
        if (undo)
        {
            history.CommitUndo();
        }
        else
        {
            history.CommitRedo();
        }

        var restored = undo ? entry.SelectionBefore : entry.SelectionAfter;
        var actual = result.After.Text;
        state.Anchor = TextGraphemeBoundaries.SnapToBoundary(actual, Math.Clamp(restored.Anchor, 0, actual.Length));
        state.Caret = TextGraphemeBoundaries.SnapToBoundary(actual, Math.Clamp(restored.Active, 0, actual.Length));
        node.Runtime.InvalidateRender();
        EnsureCaretVisible(node, state);
    }

    /// <summary>当前文档在历史起点处是否仍含有该事务执行后的片段（增量上下文校验）。</summary>
    private static bool MatchesHistoryContext(string text, int start, string expected)
    {
        if (start < 0 || start > text.Length || expected.Length > text.Length - start)
        {
            return false;
        }

        return expected.Length == 0
            || string.CompareOrdinal(text, start, expected, 0, expected.Length) == 0;
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
        // SyncEditor 只在真实外部变化时取消已失效的组合（同节点同绑定重建、Flow/Adaptive 重排不取消）。
        SyncEditor(node, state, invalidate: false);
    }

    internal override void OnUnmount(ViewNode node)
    {
        if (node.InteractionState is not TextFieldState state)
        {
            return;
        }

        // 真正卸载：先在本地取消未提交段，再按所有者撤销文本输入归属（先失效、后通知服务），
        // 最后清空组合客户端。旧节点卸载不会结束已经属于另一个节点的新会话。
        CancelCompositionForHostAction(node, state);
        state.EndInputSession();
        state.ClearComposing();
    }

    internal override bool IsLayoutEquivalent(View other) => false;

    /// <summary>
    /// 发布几何前的安全同步：观测真实绑定值，外部写入会立即取消未提交组合并请求换 epoch。
    /// 只读取绑定与本地会话；安全阶段允许建立/读取文本排版缓存以收敛选区，但不写 Binding、不重建、
    /// 不调用平台服务，也不在 GetState/原生组合回调内执行。
    /// </summary>
    internal override void PrepareTextInputGeometry(ViewNode node)
    {
        if (node.InteractionState is TextFieldState state)
        {
            SyncEditor(node, state, invalidate: false);
        }
    }

    /// <summary>
    /// 最终布局后的安全阶段：发布逻辑像素的 caret / 组合 / 排除几何（TI-06）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 几何来自显示串的真实排版原点（已吸附物理像素）与真实 caret，包含 TextField 的 <see cref="TextFieldState.ScrollX"/>、
    /// 祖先 <see cref="ScrollView"/> 滚动（<see cref="ViewNode.Frame"/> 是绝对坐标）、Padding/Frame、祖先裁剪与自身内容区裁剪。
    /// 完全被裁出、不可见或没有有效文本输入会话时发布 <see cref="TextInputGeometry.Unavailable"/>，
    /// 由客户端按同值去重，避免每帧重复唤醒平台。
    /// </para>
    /// <para>
    /// 本方法只在 <see cref="UiRuntime.ProcessPendingWork"/> 的安全阶段被调用：不重建组件、不改变布局树、
    /// 不递归生成帧，也不在 Build/Measure/Emit 或原生回调中执行。发布前允许读取/建立当前显示串的排版缓存并
    /// 收敛 <see cref="TextFieldState.ScrollX"/>，保证纯重绘发现外部短文本后，同一帧的锚点与 Emit 实际绘制一致。
    /// </para>
    /// </remarks>
    internal override void PublishTextInputGeometry(ViewNode node)
    {
        if (node.InteractionState is not TextFieldState state)
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
            // 外部文本变短（尤其从长预编辑滚动恢复）时，上一帧的 ScrollX 可能仍很大。
            // 发布前先按当前正式显示串收敛滚动；这一步在安全阶段执行、只读布局缓存，不等待 Emit 修正。
            EnsureCaretVisible(node, state);
            geometry = ComputeInputGeometry(node, state, client, out charGeometry);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 几何发布是候选锚点的辅助元数据：绑定/文本服务的真实读取失败不得让整个安全阶段中断。
            // 撤销锚点并把失败写入脱敏诊断，用户体验（候选窗隐藏）优先于把异常抛给宿主循环。
            node.Runtime.ReportDiagnostic($"发布文本输入几何失败（节点 {node.ShortDebugPath()}）：{ex.GetType().Name}");
            geometry = TextInputGeometry.Unavailable;
            charGeometry = null;
        }

        // 逐字符几何快照与锚点同帧同源更新（R17）：平台位置查询只读该快照；
        // 几何不可用时快照为 null，客户端立即拒绝应答，绝不回落旧位置。
        client.UpdateCompositionCharGeometry(charGeometry);
        client.PublishGeometry(geometry);
    }

    /// <summary>从最终布局、显示串排版、滚动偏移与裁剪链计算候选锚点几何。</summary>
    private TextInputGeometry ComputeInputGeometry(
        ViewNode node,
        TextFieldState state,
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

        var textBounds = DisplayTextBounds(display, inner, state);
        var origin = TextPainter.ResolveOrigin(textBounds, display.Layout.Size, TextAlignment.Left, display.Layout.PixelScale);

        // 与绘制共用同一 caret 解析：位置来自真实排版原点 + 排版 caret，不使用绘制端夹取后的矢量线。
        var caretRect = CaretRectAt(display, state, origin);

        // 组合矩形覆盖预编辑整段（显示串坐标），用于宿主换算候选锚点。
        var compositionRect = display.Composing && display.PreeditLength > 0
            ? Offset(display.Layout.GetRangeBounds(display.PreeditStart, display.PreeditStart + display.PreeditLength), origin)
            : Rect.Empty;

        // 完全可见时返回原始矩形（保留与绘制端逐字段相同的坐标）；部分裁出才求交。
        // 记录结构使用精确 double 相等，不能因 Intersect 的加减舍入让“发布几何 == 绘制 caret”失真。
        var visibleCaret = clip.Contains(caretRect) ? caretRect : caretRect.Intersect(clip);
        if (visibleCaret.IsEmpty || visibleCaret.Height <= 0)
        {
            // caret 完全被裁出/隐藏：撤销锚点，不把候选定位到窗口原点。
            return TextInputGeometry.Unavailable;
        }

        // 逐字符几何快照（R17）：与本帧发布的锚点同源——同一份显示串排版、同一原点、同一有效裁剪，
        // 平台按它逐偏移应答，原生回调不再访问活动状态。三个身份键（epoch/预编辑/文档版本）
        // 由客户端在提供时核对，任一变化即拒绝。
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
        };

        var visibleComposition = compositionRect.IsEmpty ? Rect.Empty : compositionRect.Intersect(clip);
        return new TextInputGeometry(visibleCaret, visibleComposition, visibleCaret.Union(visibleComposition));
    }

    /// <summary>
    /// 节点内容区的有效裁剪矩形：窗口内容区 ∩ 所有祖先的 <see cref="View.GetChildClip"/> ∩ TextField 自身内容区。
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


    /// <summary>取消未提交组合的宿主接缝（幂等）：无组合或没有会话时是无操作。</summary>
    private static void CancelCompositionForHostAction(ViewNode node, TextFieldState state)
    {
        if (state.Composing?.IsComposing != true)
        {
            return;
        }

        state.Composing.CancelComposition();
    }

    /// <summary>
    /// 组合提交前的真实文档同步：读取实际绑定值，识别外部写入 / 来源替换 / MaxLength 变化。
    /// </summary>
    /// <param name="invalidated">是否检测到外部变化（未提交组合因此失效，需要换 epoch）。</param>
    /// <remarks>
    /// 实时组合回调里也会调用：只用核心字素规则夹取选区，不做排版、不递归处理待办、不调用任何平台服务。
    /// </remarks>
    internal void SyncCompositionDocument(ViewNode node, TextFieldState state, out bool invalidated)
    {
        invalidated = false;
        if (node.View is not TextField textField)
        {
            return;
        }

        var binding = textField.Text;
        string text;
        try
        {
            text = binding.Read(node) ?? string.Empty;
        }
        catch
        {
            // 组合回调内的真实读取失败：历史不可信；清理是核心内存操作，异常由外层组合接线收敛。
            state.History.Clear();
            throw;
        }

        if (state.Editor.IsOwnPendingWrite(text) && state.Editor.MatchesContext(binding.Identity, textField.MaxLength))
        {
            return;
        }

        var sync = state.Editor.Sync(binding.Identity, text, textField.MaxLength);
        if (!sync.HasExternalChange)
        {
            return;
        }

        invalidated = true;

        // 组合回调内的历史清理只是核心内存操作：不排版、不调用平台服务。
        state.History.Clear();
        state.Caret = TextGraphemeBoundaries.SnapToBoundary(text, state.Caret);
        state.Anchor = TextGraphemeBoundaries.SnapToBoundary(text, state.Anchor);
    }

    /// <summary>
    /// 把一段组合结果作为<b>一次</b>真实原子事务提交，并处理事务期间的来源/挂载/配置变化。
    /// </summary>
    /// <returns>真实事务结果；节点已换源、换配置或卸载时返回“拒绝”，绝不把旧事务写到新客户端。</returns>
    internal EditResult ApplyCompositionCommit(ViewNode node, TextFieldState state, TextRange range, string result)
    {
        if (node.View is not TextField textField)
        {
            return RejectedCommit("节点已不再是本输入框；组合结果不写入。");
        }

        var binding = textField.Text;
        var identity = binding.Identity;
        var maxLength = textField.MaxLength;

        EditOperation operation;
        if (result.Length == 0)
        {
            operation = EditOperation.Delete(range, TextEditKind.CompositionResult, TextEditReason.UserInput);
        }
        else if (!EditOperation.TryCreate(
                     range,
                     result,
                     TextEditKind.CompositionResult,
                     TextEditReason.UserInput,
                     scrubSingleLine: true,
                     out operation,
                     out var createError))
        {
            return RejectedCommit(createError ?? "组合结果未通过编辑操作校验。");
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
            // setter / getter 抛错：按真实回读恢复会话，不伪造成功，也不掩盖原异常。
            // 组合回调内不做文本排版：只用核心字素规则限制选区并标记待布局。
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

        // 事务结束时重新核对当前挂载来源与配置：setter 期间可能已同步换源/换 MaxLength/卸载。
        if (!node.IsMounted || node.View is not TextField mounted)
        {
            state.Editor.MarkRequiresResync();
            state.History.Clear();
            return RejectedCommit("事务期间输入框已卸载；组合结果不写入。");
        }

        if (!ReferenceEquals(mounted.Text.Identity, identity) || mounted.MaxLength != maxLength)
        {
            // 事务期间换源/换配置：按新来源真实回读重同步会话（核心字素规则，不做排版），旧事务结果绝不写进新来源。
            state.History.Clear();
            SyncCompositionContext(node, state);
            return RejectedCommit("事务期间绑定来源或 MaxLength 已变化；旧组合结果不写入新客户端。");
        }

        if (state.Editor.LastTransactionSawExternalChange)
        {
            // 事务读取点观测到外部变化：旧历史上下文失效（清理只是核心内存操作）。
            state.History.Clear();
        }

        if (edit.Changed)
        {
            state.Caret = edit.After.Selection.Active;
            state.Anchor = edit.After.Selection.Anchor;

            if (state.Editor.LastTransaction == TextEditTransactionOutcome.Applied)
            {
                // 一次真实组合 Result 只记录一项；分段结果各自独立。记录只是核心内存操作，不做排版/平台调用。
                state.History.Record(TextEditHistory.FromTransaction(edit, operation, beforeSelection), node.Runtime.Clock.Now);
            }
            else
            {
                // 规范化等非精确落地：不记录错误逆操作。
                state.History.Clear();
            }

            // 原生组合回调内不做排版/几何计算：只标记布局失效，由下一次安全布局保证 caret 可见。
            node.Runtime.InvalidateLayout(node);
        }
        else
        {
            // 拒绝 / 未变化：只用核心字素规则把会话与选区对齐到真实文本，不做排版。
            // setter 明确拒绝写入时历史不可信；容量拒绝/空操作不凭空清空 redo。
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

    /// <summary>
    /// 组合回调内的布局无关同步：读取真实绑定、识别外部文本/来源/MaxLength 变化并让组合立即失效，
    /// 只按核心字素规则夹取选区，<b>绝不</b>调用 <c>GetLayout</c>/<c>TextService.Layout</c>。
    /// </summary>
    private void SyncCompositionContext(ViewNode node, TextFieldState state)
    {
        if (node.View is not TextField field)
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
            // 事务之外的组合同步读取失败：历史不可信；清理是核心内存操作，不调用布局/平台服务。
            state.History.Clear();
            state.Editor.MarkRequiresResync();
            node.Runtime.InvalidateRender();
            return;
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

        // 外部变化发生在组合回调内：历史上下文失效；清理是核心内存操作，不排版、不调用平台服务。
        state.History.Clear();
        state.Composing?.InvalidateForExternalChange();
        SnapSelectionToText(state, state.Editor.Text);
        node.Runtime.InvalidateLayout(node);
    }

    /// <summary>只按核心字素规则（StringInfo，不要求排版器）把光标与选择限制到真实文本。</summary>
    private static void SnapSelectionToText(TextFieldState state, string text)
    {
        var length = text.Length;
        state.Caret = TextGraphemeBoundaries.SnapToBoundary(text, Math.Clamp(state.Caret, 0, length));
        state.Anchor = TextGraphemeBoundaries.SnapToBoundary(text, Math.Clamp(state.Anchor, 0, length));
    }

    private static EditResult RejectedCommit(string reason)
        => new(TextEditStatus.Rejected, TextSnapshot.Empty, TextSnapshot.Empty, string.Empty, 0, reason);

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

    /// <summary>
    /// 解析当前显示串及其真实排版（TI-06）：组合期间是“已提交前缀 + 预编辑 + 已提交后缀”，
    /// 否则就是已提交文本。显示串排版与 <see cref="GetLayout"/> 的文档排版分开缓存，绝不互相污染。
    /// </summary>
    /// <remarks>
    /// 显示串按<b>当前真实绑定文本</b> + 活动替换区构造，而不是复用组合状态里的旧文档快照；这样外部写入在
    /// 安全入口被发现并取消组合后，显示串不会短暂拼出过期内容。本方法只读取会话/排版缓存，不写绑定、不改组合状态。
    /// </remarks>
    private TextFieldDisplayLayout GetDisplayLayout(ViewNode node, ITextService textService)
    {
        var text = Text.Read(node) ?? string.Empty;
        var font = node.Environment.Font ?? FontDescription.Default;
        var state = node.InteractionState as TextFieldState;
        if (state?.Composing is not { IsComposing: true } client
            || client.Composition.Active is not { } anchor)
        {
            return new TextFieldDisplayLayout(text, GetLayout(node, textService), Composing: false, 0, 0, 0, []);
        }

        var preedit = anchor.Preedit;
        var start = Math.Clamp(anchor.Replacement.Start, 0, text.Length);
        var end = Math.Clamp(anchor.Replacement.End, start, text.Length);
        var displayText = string.Concat(text.AsSpan(0, start), preedit, text.AsSpan(end));
        var pixelScale = node.Runtime.PixelScale;

        TextLayoutResult layout;
        if (state.DisplayLayoutCache is { } cache
            && cache.Text == displayText
            && cache.PixelScale.Equals(pixelScale)
            && Equals(cache.Font, font))
        {
            layout = cache.Layout;
        }
        else
        {
            layout = textService.Layout(TextPainter.CreateRequest(displayText, font, double.PositiveInfinity, pixelScale));
            state.DisplayLayoutCache = new TextFieldDisplayCache(displayText, font, pixelScale, layout);
        }

        var preeditCaret = Math.Clamp(anchor.Caret, 0, preedit.Length);
        return new TextFieldDisplayLayout(
            displayText,
            layout,
            Composing: true,
            start,
            preedit.Length,
            start + preeditCaret,
            anchor.Segments);
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
    /// 保证有效 caret 在内容区可见：只调整 <see cref="TextFieldState.ScrollX"/>，不写绑定、不改光标与选区。
    /// 可复用调用方已有的排版结果与内容区矩形，供排列与纯重绘观测路径共用而不递归重排。
    /// </summary>
    /// <remarks>
    /// 组合与已提交 caret 使用<b>同一可见性预算</b>：使用当前显示串/文档的真实排版 caret 位置，
    /// 并计入 caret 宽度与不足 1 物理像素的吸附余量。这样真实排版原点 + caret 矩形完整落在内容区内，
    /// 绘制与发布的几何一致；不依赖事后夹取矢量 caret，也不把文本末端强行贴死在右边界。
    /// </remarks>
    private void EnsureCaretVisible(ViewNode node, TextFieldState state, TextLayoutResult? knownLayout = null, Rect? knownInner = null)
    {
        var inner = knownInner ?? InnerRect(node.Frame);
        if (inner.Width <= 0)
        {
            return;
        }

        TextFieldDisplayLayout? display = state.Composing?.IsComposing == true
            ? GetDisplayLayout(node, node.Runtime.Services.TextService)
            : null;
        var layout = display?.Layout ?? knownLayout ?? GetLayout(node, node.Runtime.Services.TextService);
        var caret = display is { Composing: true } composing
            ? Math.Clamp(composing.CaretDisplay, 0, composing.Text.Length)
            : layout.SnapToCaretStop(Math.Clamp(state.Caret, 0, layout.Text.Length));
        var caretX = layout.GetCaretX(caret);

        // 末尾 caret 的右边缘也必须可见；物理像素吸附可能再平移不足 1 像素。
        const double CaretWidth = 1.5;
        var pixelScale = layout.PixelScale <= 0 ? 1.0 : layout.PixelScale;
        var snapMargin = 1.0 / pixelScale;
        var maxScroll = Math.Max(0, layout.Size.Width + CaretWidth + snapMargin - inner.Width);

        if (caretX + CaretWidth - state.ScrollX > inner.Width)
        {
            state.ScrollX = caretX + CaretWidth - inner.Width + snapMargin;
        }
        else if (caretX - state.ScrollX < 0)
        {
            state.ScrollX = Math.Max(0, caretX - snapMargin);
        }

        state.ScrollX = Math.Clamp(state.ScrollX, 0, maxScroll);
    }

    private static TextFieldState GetState(ViewNode node)
    {
        if (node.InteractionState is TextFieldState state)
        {
            return state;
        }

        state = new TextFieldState(node.Runtime);
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

/// <summary>
/// TextField 的显示串视图（TI-06）：显示文本、其真实排版、预编辑在显示串中的范围与组合 caret。
/// </summary>
/// <param name="Text">显示串（组合期间为“已提交前缀 + 预编辑 + 已提交后缀”，否则为已提交文本）。</param>
/// <param name="Layout">显示串的真实排版结果；与文档排版分开缓存。</param>
/// <param name="Composing">是否正在组合。</param>
/// <param name="PreeditStart">预编辑在显示串中的起始 UTF-16 偏移。</param>
/// <param name="PreeditLength">预编辑长度（UTF-16）。</param>
/// <param name="CaretDisplay">组合 caret 在显示串中的 UTF-16 偏移；未组合时为 0。</param>
/// <param name="Segments">预编辑分段（相对预编辑串的 UTF-16 范围）。</param>
internal readonly record struct TextFieldDisplayLayout(
    string Text,
    TextLayoutResult Layout,
    bool Composing,
    int PreeditStart,
    int PreeditLength,
    int CaretDisplay,
    IReadOnlyList<TextRange> Segments);

/// <summary>
/// 显示串排版缓存：只按“显示文本 + 字体 + DPI”复用排版；caret/分段每次从组合状态实时解析。
/// </summary>
internal sealed class TextFieldDisplayCache(string text, FontDescription font, double pixelScale, TextLayoutResult layout)
{
    public string Text { get; } = text;

    public FontDescription Font { get; } = font;

    public double PixelScale { get; } = pixelScale;

    public TextLayoutResult Layout { get; } = layout;
}

/// <summary>TextField 的运行时编辑状态（保存在节点上，组件重建后保持）。</summary>
internal sealed class TextFieldState : ITextEditSessionOwner
{
    /// <summary>原子编辑会话：确认文本、文档版本、来源身份与失效接缝（TI-03）。</summary>
    public TextEditSession Editor { get; } = new();

    /// <summary>显示串排版缓存（TI-06）；与 <see cref="ViewNode.ViewCache"/> 中的文档排版严格分开。</summary>
    public TextFieldDisplayCache? DisplayLayoutCache { get; set; }

    /// <summary>每节点有界撤销/重做历史（TI-05）：与运行时节点同寿命。</summary>
    public TextEditHistory History { get; }

    /// <summary>文本输入会话接线（运行时唯一实例）：绑定/解绑焦点客户端并收敛取消（TI-04）。</summary>
    public TextInputServiceSession InputSession { get; }

    /// <summary>
    /// 本节点当前原生按键的身份 token：原生桥（TI-07）在一次按键的 KeyDown 与组合结束之间传同一个实例；
    /// 无原生信息时使用节点自己的稳定实例，因此“结束键归属”判定不依赖时间窗或文本内容。
    /// </summary>
    public object NativeKeyToken { get; } = new();

    /// <summary>
    /// 组合客户端（惰性创建）：只有真实焦点会话建立后才存在，卸载时清空，旧实例由接线标记为已撤销。
    /// </summary>
    public TextCompositionClient? Composing { get; private set; }

    public TextFieldState(UiRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        InputSession = runtime.TextInputSession;
        History = new TextEditHistory(runtime.Services.HistoryCapacity);
    }

    /// <summary>取得组合客户端；会话使用新 epoch 时创建新客户端，绝不复用已被释放的旧实例。</summary>
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

    /// <summary>
    /// 结束本状态上的焦点文本输入会话：只有本节点仍是当前所有者时才结束（先失效、后通知服务）。
    /// </summary>
    /// <remarks>
    /// 已发布锚点的撤销由会话接线在“客户端失效之后”发出（见 <see cref="TextInputServiceSession"/>），
    /// 不能先从本状态调用平台，否则服务同步回调仍能写入尚未失效的旧客户端。
    /// </remarks>
    /// <returns>确实结束了会话时返回 true；没有会话或已属于其他节点时为 false（幂等）。</returns>
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

    /// <summary>
    /// 撤销归属前的本地取消：恢复活动段开始前的选区（含方向）并清空未提交段。
    /// 必须在客户端被标记为已释放之前调用。
    /// </summary>
    public void CancelCompositionForRelease()
    {
        if (Composing is { IsComposing: true } client)
        {
            client.CancelComposition(restoreSelection: true);
        }
    }

    /// <summary>
    /// 声明一次原生按键的归属（TI-07 接缝）；返回控件是否应在本次 KeyDown 中放弃该按键。
    /// </summary>
    public bool TrackOriginatingKey(object nativeKeyToken, bool consumedByInputMethod)
        => Composing?.TrackOriginatingKey(nativeKeyToken, consumedByInputMethod) ?? false;

    /// <summary>当前按键是否已被输入法消费、控件不得再执行编辑命令。</summary>
    public bool IsConsumedOriginatingKey() => IsConsumedOriginatingKey(NativeKeyToken);

    /// <summary>指定 token 的按键是否已被输入法消费。</summary>
    public bool IsConsumedOriginatingKey(object nativeKeyToken)
        => Composing?.ShouldOwnInputKey(nativeKeyToken) == TextInputKeyDecision.Consume;

    /// <summary>
    /// 指定 token 的按键是否被<b>显式</b>声明为由输入法消费（一次性）。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="IsConsumedOriginatingKey(object)"/> 不同：这里不把“当前正在组合”当作消费，
    /// 因此组合期间未被输入法声明的 Undo/Redo/Cut/Paste/SelectAll 仍可先取消预编辑再执行。
    /// 该判定覆盖“组合在同一原生按键内结束、随后仍进入 KeyDown”的显式 token。
    /// </remarks>
    public bool IsExplicitlyConsumedKey(object nativeKeyToken)
        => Composing?.IsExplicitlyConsumedKey(nativeKeyToken) ?? false;

    /// <summary>光标位置的 UTF-16 索引（始终位于字素边界）。</summary>
    public int Caret { get; set; }

    /// <summary>选择锚点；与 <see cref="Caret"/> 相同表示无选择。</summary>
    public int Anchor { get; set; }

    /// <summary>水平滚动偏移（逻辑像素）。</summary>
    public double ScrollX { get; set; }

    /// <summary>垂直滚动偏移（逻辑像素）；TextField 单行恒为 0。</summary>
    public double ScrollY => 0;

    /// <inheritdoc cref="ITextEditSessionOwner.CaretAffinity"/>
    /// <remarks>单行 TextField 无共享 offset 歧义，恒为 <see cref="TextAffinity.Unspecified"/>。</remarks>
    public TextAffinity CaretAffinity => TextAffinity.Unspecified;

    /// <inheritdoc cref="ITextEditSessionOwner.SyncCompositionDocument"/>
    public bool SyncCompositionDocument(ViewNode node)
    {
        if (node.View is not TextField field)
        {
            return false;
        }

        field.SyncCompositionDocument(node, this, out var invalidated);
        return invalidated;
    }

    /// <inheritdoc cref="ITextEditSessionOwner.ApplyCompositionCommit"/>
    public EditResult ApplyCompositionCommit(ViewNode node, TextRange range, string result)
        => node.View is TextField field
            ? field.ApplyCompositionCommit(node, this, range, result)
            : default;

    /// <inheritdoc cref="ITextEditSessionOwner.MaxLength"/>
    public int MaxLength(ViewNode node)
        => node.View is TextField field ? field.MaxLength : int.MaxValue;

    /// <inheritdoc cref="ITextEditSessionOwner.TextIdentity"/>
    public object TextIdentity(ViewNode node)
        => node.View is TextField field ? field.Text.Identity : this;

    /// <inheritdoc cref="ITextEditSessionOwner.IsTextEditNode"/>
    public bool IsTextEditNode(ViewNode node)
        => node.IsMounted && node.View is TextField;

    /// <inheritdoc cref="ITextEditSessionOwner.IsEditableNode"/>
    public bool IsEditableNode(ViewNode node)
        => node.IsMounted && !node.IsEffectivelyDisabled && node.View is TextField;
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
