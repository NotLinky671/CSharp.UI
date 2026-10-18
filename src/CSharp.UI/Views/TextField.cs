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

        // 鼠标重新定位选区属于取消接缝：先取消未提交组合，再处理点击。
        CancelCompositionForHostAction(node, state);

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
        SyncEditor(node, state, invalidate: false);
        var command = e.IsCommandPressed;

        // 组合期间方向键/Backspace/Delete/空格/Enter/Escape 归输入法所有；同一原生按键内刚结束的组合
        // 也不能再进入编辑命令（不能只检查此刻是否 Composing）。Tab 的消费结果同样在这里决定。
        // 命令快捷键（Ctrl/Command + C/X/A/V 等）不属于输入法编辑键：不在此吞掉，交给下面的既有命令分支。
        if (!command)
        {
            if (state.Composing?.IsComposing == true)
            {
                state.BlinkEpoch = node.Runtime.Clock.Now;
                context.MarkHandled();
                return true;
            }

            if (state.IsConsumedOriginatingKey(node.Runtime.KeyOriginToken ?? state.NativeKeyToken))
            {
                // 组合在同一原生按键内结束：该按键已由输入法消费，控件不再执行编辑命令，也不做焦点遍历。
                state.BlinkEpoch = node.Runtime.Clock.Now;
                context.MarkHandled();
                return true;
            }
        }

        var text = Text.Read(node) ?? string.Empty;
        var extend = e.Modifiers.HasFlag(KeyModifiers.Shift);
        state.BlinkEpoch = node.Runtime.Clock.Now;

        switch (e.Key)
        {
            case UiKey.Left:
                CancelCompositionForHostAction(node, state);
                MoveCaret(node, state, text, backward: true, extend);
                return true;
            case UiKey.Right:
                CancelCompositionForHostAction(node, state);
                MoveCaret(node, state, text, backward: false, extend);
                return true;
            case UiKey.Home:
                CancelCompositionForHostAction(node, state);
                SetCaret(node, state, 0, extend);
                return true;
            case UiKey.End:
                CancelCompositionForHostAction(node, state);
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
            case UiKey.A when command:
                CancelCompositionForHostAction(node, state);
                state.Anchor = 0;
                state.Caret = text.Length;
                context.MarkHandled();
                return true;
            case UiKey.C when command:
                // 复制只读取已提交选区：组合期间不复制预编辑，也不因此取消组合。
                CopySelection(node, state, text, cut: false);
                return true;
            case UiKey.X when command:
                CancelCompositionForHostAction(node, state);
                CopySelection(node, state, text, cut: true);
                return true;
            case UiKey.V when command:
                CancelCompositionForHostAction(node, state);
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
        var text = binding.Read(node) ?? string.Empty;
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
        try
        {
            edit = state.Editor.Apply(
                identity,
                maxLength,
                operation,
                new TextSelection(state.Anchor, state.Caret),
                () => binding.Read(node) ?? string.Empty,
                value => binding.Value = value);
        }
        catch
        {
            // setter / getter 抛错：按真实回读恢复会话，不伪造成功，也不掩盖原异常。
            // 组合回调内不做文本排版：只用核心字素规则限制选区并标记待布局。
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
            return RejectedCommit("事务期间输入框已卸载；组合结果不写入。");
        }

        if (!ReferenceEquals(mounted.Text.Identity, identity) || mounted.MaxLength != maxLength)
        {
            // 事务期间换源/换配置：按新来源真实回读重同步会话（核心字素规则，不做排版），旧事务结果绝不写进新来源。
            SyncCompositionContext(node, state);
            return RejectedCommit("事务期间绑定来源或 MaxLength 已变化；旧组合结果不写入新客户端。");
        }

        if (edit.Changed)
        {
            state.Caret = edit.After.Selection.Active;
            state.Anchor = edit.After.Selection.Anchor;

            // 原生组合回调内不做排版/几何计算：只标记布局失效，由下一次安全布局保证 caret 可见。
            node.Runtime.InvalidateLayout(node);
        }
        else
        {
            // 拒绝 / 未变化：只用核心字素规则把会话与选区对齐到真实文本，不做排版。
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

/// <summary>TextField 的运行时编辑状态（保存在节点上，组件重建后保持）。</summary>
internal sealed class TextFieldState
{
    /// <summary>原子编辑会话：确认文本、文档版本、来源身份与失效接缝（TI-03）。</summary>
    public TextEditSession Editor { get; } = new();

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
    public TextFieldCompositionClient? Composing { get; private set; }

    public TextFieldState(UiRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        InputSession = runtime.TextInputSession;
    }

    /// <summary>取得组合客户端；会话使用新 epoch 时创建新客户端，绝不复用已被释放的旧实例。</summary>
    public TextFieldCompositionClient RequireComposing(ViewNode node, int epoch)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (Composing is { IsReleased: false } existing)
        {
            return existing;
        }

        Composing = new TextFieldCompositionClient(node, this, epoch);
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
