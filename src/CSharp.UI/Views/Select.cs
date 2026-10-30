namespace CSharp.UI;

/// <summary>下拉选项：值、显示标签与是否禁用。</summary>
/// <param name="Value">提交到绑定的值；同一 <see cref="Select{T}"/> 内按 <see cref="EqualityComparer{T}.Default"/> 唯一。</param>
/// <param name="Label">显示标签；null 回退为 <c>Value?.ToString() ?? ""</c>（空串合法）。</param>
/// <param name="Disabled">该项不可选（跳过键盘/指针激活），但被选中时仍正常回显。</param>
public sealed record SelectItem<T>(T Value, string? Label = null, bool Disabled = false)
{
    /// <summary>实际显示文本：显式标签优先，否则值字符串，null 值显示空串。</summary>
    internal string DisplayLabel => Label ?? (Value is null ? string.Empty : Value.ToString() ?? string.Empty);
}

/// <summary>
/// 单选下拉控件：<see cref="Binding{T}"/> 双向当前值，选项列表在根级 overlay 弹层中展示，
/// 支持限高内部滚动、完整键盘导航与选后关闭；焦点始终留在 trigger。
/// </summary>
/// <remarks>
/// <para>
/// 打开/关闭与公开 <see cref="Popup"/> 共用同一 overlay 生命周期（同一个 owner 拉取状态机＋关闭原因），
/// 不是另一套 popup 系统：trigger 节点既是锚（定位与锚豁免）也是 SavedFocus。
/// </para>
/// <para>
/// 提交纪律：绑定只在“选后路径”写入恰好一次（pointer Up 落在同一项 / Enter 或 Space 提交 active 项），
/// 打开/关闭/Esc/Tab/外点/失活都不写。items 内的 <see cref="SelectItem{T}.Value"/> 必须唯一，
/// 重复会抛 <see cref="ArgumentException"/>（不静默取第一个）。
/// </para>
/// </remarks>
public sealed class Select<T> : View, IOverlayOwner
{
    private double _maxPopupHeight = 240;

    /// <summary>由当前值绑定、选项列表与可选占位文本创建。</summary>
    /// <param name="value">当前值绑定（外部写入即时回显）。</param>
    /// <param name="items">选项列表（按值唯一；null 值合法）。</param>
    /// <param name="placeholder">当前值不在选项内时显示的占位文本。</param>
    public Select(Binding<T> value, IReadOnlyList<SelectItem<T>> items, string? placeholder = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(items);

        var copy = new SelectItem<T>[items.Count];
        var seen = new HashSet<T>(EqualityComparer<T>.Default);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i] ?? throw new ArgumentNullException(nameof(items), "选项不能为 null。");
            if (!seen.Add(item.Value))
            {
                throw new ArgumentException(
                    $"选项值重复：{DescribeValue(item.Value)}。同一 Select 内的 Value 必须唯一。",
                    nameof(items));
            }

            copy[i] = item;
        }

        Value = value;
        Items = Array.AsReadOnly(copy);
        Placeholder = placeholder;
    }

    /// <summary>当前值绑定。</summary>
    public Binding<T> Value { get; }

    /// <summary>选项列表（构造时快照，按值唯一）。</summary>
    public IReadOnlyList<SelectItem<T>> Items { get; }

    /// <summary>当前值不在选项内时显示的占位文本。</summary>
    public string? Placeholder { get; }

    /// <summary>弹层高度上限（有限正数，默认 240）。</summary>
    public double MaxPopupHeight
    {
        get => _maxPopupHeight;
        init
        {
            if (!double.IsFinite(value) || value <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(MaxPopupHeight), value, "弹层高度上限必须是有限正数。");
            }

            _maxPopupHeight = value;
        }
    }

    /// <inheritdoc />
    public override string DebugName => "Select";

    /// <inheritdoc />
    public override bool IsFocusable => true;

    // 触发器视觉常量（逻辑像素）。
    internal const double HorizontalPadding = 12;
    internal const double VerticalPadding = 6;
    internal const double CornerRadius = 6;
    internal const double MinHeight = 32;
    internal const double ArrowExtent = 18;
    internal const double RowHeight = 28;
    internal const double RowPadding = 10;
    internal const double PanelInset = 2;
    internal const double PanelRadius = 6;
    private const double PopupMargin = 4;

    internal override IReadOnlyList<View> Children => [];

    internal override void OnMount(ViewNode node)
    {
        StateOf(node);
        node.Runtime.Overlay.RegisterOwner(node);
    }

    internal override void Measure(ViewNode node, LayoutConstraints constraints, LayoutContext context)
    {
        var state = StateOf(node);
        state.Bind(node);

        var text = DisplayText(node);
        var font = node.Environment.Font ?? FontDescription.Default;
        var available = constraints.HasBoundedWidth
            ? Math.Max(0, constraints.MaxWidth - (HorizontalPadding * 2) - ArrowExtent)
            : double.PositiveInfinity;
        var layout = TextPainter.Layout(
            context, text, font, available, context.PixelScale,
            TextWrapping.NoWrap, TextTrimming.Ellipsis);
        node.ViewCache = layout;

        node.DesiredSize = new Size(
            layout.Size.Width + (HorizontalPadding * 2) + ArrowExtent,
            Math.Max(MinHeight, layout.Size.Height + (VerticalPadding * 2)));
    }

    internal override void Arrange(ViewNode node, Rect bounds, LayoutContext context)
    {
        node.Frame = bounds;

        // 弹层最小宽度跟随 trigger 实际宽度：变化时让已打开的 popup 内容重新测量（每轮至多一次）。
        var state = StateOf(node);
        if (Math.Abs(state.TriggerWidth - bounds.Width) > 0.001)
        {
            state.TriggerWidth = bounds.Width;
            node.Runtime.Overlay.InvalidateOpenPopupMeasure();
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
        var fill = disabled
            ? theme.DisabledBackground
            : node.IsPressed
                ? theme.ControlPressedBackground
                : node.IsHovered ? theme.ControlHoverBackground : theme.ControlBackground;
        var stroke = node.IsFocused ? theme.FocusRing : theme.Border;

        context.Commands.Add(new RectangleCommand(
            node.Frame, fill, CornerRadius, stroke, node.IsFocused ? 2 : 1, node.Environment.Opacity));

        var isPlaceholder = FindItem(Value.Read(node)) is null;
        var text = DisplayText(node);
        var font = node.Environment.Font ?? FontDescription.Default;
        var labelWidth = Math.Max(0, node.Frame.Width - (HorizontalPadding * 2) - ArrowExtent);
        var labelRect = new Rect(
            node.Frame.X + HorizontalPadding,
            node.Frame.Y,
            labelWidth,
            node.Frame.Height);

        var request = TextPainter.CreateRequest(
            text, font, labelWidth, context.PixelScale,
            TextWrapping.NoWrap, TextTrimming.Ellipsis);
        var layout = context.TextService.Layout(request);
        var color = disabled
            ? theme.DisabledForeground
            : isPlaceholder ? theme.MutedForeground : theme.Foreground;

        var restore = context.CurrentClip;
        if (context.PushClip(labelRect))
        {
            TextPainter.Paint(context, node, layout, color, labelRect);
            context.PopClip(restore);
        }

        DrawArrow(context, node, node.Frame, disabled ? theme.DisabledForeground : theme.MutedForeground);
    }

    internal override bool OnPointerDown(InputContext context, ViewNode node, PointerEvent e)
    {
        if (node.IsEffectivelyDisabled)
        {
            return false;
        }

        context.Runtime.SetFocus(node);
        return true;
    }

    internal override bool OnPointerUp(InputContext context, ViewNode node, PointerEvent e)
    {
        if (node.IsEffectivelyDisabled || !node.IsPressed)
        {
            return false;
        }

        if (!node.Frame.Contains(e.Position))
        {
            return true;
        }

        var state = StateOf(node);
        if (state.Open)
        {
            // 再点 trigger：关闭（不写值）。
            state.Open = false;
            state.ClearPressed();
            node.InvalidateRender();
            return true;
        }

        // Up-activation：Down 只聚焦/按下，Up 同点才打开。
        return TryOpen(node, state, forward: true);
    }

    internal override bool OnKeyDown(InputContext context, ViewNode node, KeyEvent e)
    {
        if (node.IsEffectivelyDisabled)
        {
            return false;
        }

        // 带命令修饰符的组合键不归控件处理（不吞全局快捷键）；Alt+Down 不在约定范围，按普通 Down 处理。
        if (e.Modifiers.HasFlag(KeyModifiers.Control) || e.Modifiers.HasFlag(KeyModifiers.Meta))
        {
            return false;
        }

        var state = StateOf(node);
        switch (e.Key)
        {
            case UiKey.Enter or UiKey.Space:
                // 长按重复不得“打开后立即提交”：重复按键一律消费但不动作。
                if (e.Repeat)
                {
                    return true;
                }

                if (state.Open)
                {
                    return CommitActive(node, state);
                }

                return TryOpen(node, state, forward: true);

            case UiKey.Down or UiKey.Up:
                if (state.Open)
                {
                    MoveActive(state, e.Key == UiKey.Down ? 1 : -1);
                    state.PendingEnsureVisible = true;
                    node.InvalidateRender();
                    return true;
                }

                return TryOpen(node, state, forward: e.Key == UiKey.Down);

            case UiKey.Home or UiKey.End:
                if (!state.Open)
                {
                    return false;
                }

                if (MoveActiveToEdge(state, first: e.Key == UiKey.Home))
                {
                    state.PendingEnsureVisible = true;
                    node.InvalidateRender();
                }

                return true;

            case UiKey.Tab:
                if (!state.Open)
                {
                    return false;
                }

                // 关闭弹层但不消费：主树 Tab 遍历继续，焦点移向 trigger 的下一项。
                state.Open = false;
                node.InvalidateRender();
                return false;

            case UiKey.Escape:
                // 交给 overlay 的统一 Esc 路径（focused 路由已先行；本控件不自处理）。
                return false;
        }

        return false;
    }

    /// <inheritdoc />
    bool IOverlayOwner.TryGetOpenRequest(ViewNode node, out OverlayOpenRequest request)
    {
        request = default;

        var state = StateOf(node);
        state.Bind(node);

        // 读取走绑定：登记来源 State 依赖；值变化只触发一次安全阶段失效（标签回显）。
        var value = Value.Read(node);
        state.SyncValue(node, value);

        if (!state.Open)
        {
            return false;
        }

        if (!CanOpen(node))
        {
            state.Open = false;
            return false;
        }

        state.SyncItems(this, node);

        // 首次打开时弹层内容刚挂载、尚未布局；items 变化时内容也刚重建（旧 ScrollNode 仍 mounted 且 Frame 有效，
        // 但行位置属于旧内容）。EnsureActiveVisible 只在“当前内容代际已真实 Arrange”后才兑现，
        // 否则 pending 留到后续安全阶段重试（有界，且只在键盘/列表变化时触发）。
        if (state.PendingEnsureVisible && state.EnsureActiveVisible())
        {
            state.PendingEnsureVisible = false;
        }

        request = new OverlayOpenRequest(
            node,
            state.Content!,
            PopupMargin,
            MaxPopupHeight,
            PopupPlacement.Auto);
        return true;
    }

    /// <inheritdoc />
    void IOverlayOwner.OnOverlayClosed(ViewNode node, OverlayCloseReason reason)
    {
        // manager 侧关闭（外点/Esc/失活/锚失效/挤占/卸载）：同步清 open flag，不留 stale open。
        var state = StateOf(node);
        state.Open = false;
        state.ClearPressed();
        node.InvalidateRender();
    }

    // ---------------------------------------------------------------- 内部协作

    internal SelectState<T> StateOf(ViewNode node)
    {
        if (node.InteractionState is SelectState<T> existing)
        {
            return existing;
        }

        var created = new SelectState<T>();
        created.Bind(node);
        node.InteractionState = created;
        return created;
    }

    internal string DisplayText(ViewNode node)
    {
        var item = FindItem(Value.Read(node));
        return item is null ? Placeholder ?? string.Empty : item.DisplayLabel;
    }

    internal SelectItem<T>? FindItem(T value)
    {
        var comparer = EqualityComparer<T>.Default;
        foreach (var item in Items)
        {
            if (comparer.Equals(item.Value, value))
            {
                return item;
            }
        }

        return null;
    }

    internal bool TryOpen(ViewNode node, SelectState<T> state, bool forward)
    {
        if (!CanOpen(node))
        {
            return false;
        }

        var selected = FindItem(Value.Read(node));
        if (selected is { Disabled: false })
        {
            state.SetActive(selected.Value);
        }
        else if (!SelectFirstEnabled(state, forward))
        {
            return false;
        }

        state.Open = true;
        state.PendingEnsureVisible = true;
        node.InvalidateRender();
        return true;
    }

    internal bool CanOpen(ViewNode node)
    {
        if (node.IsEffectivelyDisabled || Items.Count == 0)
        {
            return false;
        }

        foreach (var item in Items)
        {
            if (!item.Disabled)
            {
                return true;
            }
        }

        return false;
    }

    internal bool CommitActive(ViewNode node, SelectState<T> state)
    {
        if (!state.HasActive)
        {
            return false;
        }

        state.Commit(state.ActiveValue, node);
        return true;
    }

    internal void MoveActive(SelectState<T> state, int direction)
    {
        var items = Items;
        if (items.Count == 0)
        {
            return;
        }

        var current = state.HasActive ? IndexOfValue(items, state.ActiveValue) : -1;
        for (var step = 1; step <= items.Count; step++)
        {
            var index = current < 0
                ? (direction > 0 ? step - 1 : items.Count - step)
                : current + (direction * step);
            if (index < 0 || index >= items.Count)
            {
                return; // 两端停，不环绕。
            }

            if (!items[index].Disabled)
            {
                state.SetActive(items[index].Value);
                return;
            }
        }
    }

    internal bool SelectFirstEnabled(SelectState<T> state, bool forward)
    {
        var items = Items;
        if (forward)
        {
            for (var i = 0; i < items.Count; i++)
            {
                if (!items[i].Disabled)
                {
                    state.SetActive(items[i].Value);
                    return true;
                }
            }
        }
        else
        {
            for (var i = items.Count - 1; i >= 0; i--)
            {
                if (!items[i].Disabled)
                {
                    state.SetActive(items[i].Value);
                    return true;
                }
            }
        }

        return false;
    }

    private bool MoveActiveToEdge(SelectState<T> state, bool first)
        => SelectFirstEnabled(state, forward: first);

    internal int IndexOfValue(IReadOnlyList<SelectItem<T>> items, T value)
    {
        var comparer = EqualityComparer<T>.Default;
        for (var i = 0; i < items.Count; i++)
        {
            if (comparer.Equals(items[i].Value, value))
            {
                return i;
            }
        }

        return -1;
    }

    internal static void DrawArrow(DrawContext context, ViewNode node, Rect frame, Color color)
    {
        // 无旋转图元：用 4 层递减宽度的细矩形拼出下拉箭头，主题色一致、不依赖字体字形。
        // 透明度继承节点环境（.Opacity(0) 的 Select 不得残留实心箭头）。
        var opacity = node.Environment.Opacity;
        const double size = 8;
        var originX = frame.Right - HorizontalPadding - size;
        var originY = frame.Y + ((frame.Height - size) / 2);
        for (var i = 0; i < 4; i++)
        {
            var width = size - (i * 2);
            context.Commands.Add(new RectangleCommand(
                new Rect(originX + i, originY + (i * 2), width, 2),
                color,
                0,
                null,
                0,
                opacity));
        }
    }

    private static string DescribeValue(T value)
        => value is null ? "null" : value.ToString() ?? value.GetType().Name;
}

/// <summary>
/// 单个 <see cref="Select{T}"/> 节点持有的交互状态：open flag、active/pressed 值、popup 内容与滚动锚点。
/// </summary>
/// <remarks>
/// 按<b>值</b>记录 active/pressed（不是列表下标）：items 被替换/重排后按值重定位，不会把按下/高亮落到
/// 另一项上；状态挂在节点上，描述重建（同 key/类型）不会丢 open flag/active。
/// </remarks>
internal sealed class SelectState<T>
{
    internal ViewNode? Node { get; private set; }

    internal bool Open { get; set; }

    internal bool HasActive { get; private set; }

    internal T ActiveValue { get; private set; } = default!;

    internal bool HasPressed { get; private set; }

    internal T PressedValue { get; private set; } = default!;

    internal bool PendingEnsureVisible { get; set; }

    internal double TriggerWidth { get; set; }

    internal SelectPopupContent<T>? Content { get; private set; }

    internal ViewNode? ScrollNode { get; set; }

    /// <summary>弹层内容代际：每次 items 变化重建内容时递增。</summary>
    internal int ContentGeneration { get; private set; }

    /// <summary>已真实 Arrange 的内容代际（旧内容/新内容尚未布局时不得据此兑现 ensure-visible）。</summary>
    internal int ScrollReadyGeneration { get; set; } = -1;

    private bool _hasLastValue;
    private T _lastValue = default!;
    private IReadOnlyList<SelectItem<T>>? _items;

    internal void Bind(ViewNode node) => Node = node;

    /// <summary>安全阶段观察到的当前值：变化时让 trigger 重新测量（标签宽度可能变化）。</summary>
    internal void SyncValue(ViewNode node, T value)
    {
        if (_hasLastValue && EqualityComparer<T>.Default.Equals(_lastValue, value))
        {
            return;
        }

        _hasLastValue = true;
        _lastValue = value;
        node.InvalidateLayout();
    }

    /// <summary>items 引用变化时重建弹层内容并把 active 按值重定位（移除/禁用 → 首个可用项）。</summary>
    internal void SyncItems(Select<T> owner, ViewNode node)
    {
        if (Content is not null && ReferenceEquals(_items, owner.Items))
        {
            return;
        }

        _items = owner.Items;
        ContentGeneration++;
        Content = new SelectPopupContent<T>(this, owner.Items, ContentGeneration);

        // 列表变化后合法 active 也可能换位（例如移到末项）：重新确保一次可见（行已可见时为 no-op）。
        PendingEnsureVisible = true;

        if (!HasActive)
        {
            return;
        }

        var index = owner.IndexOfValue(owner.Items, ActiveValue);
        if (index >= 0 && !owner.Items[index].Disabled)
        {
            return;
        }

        HasActive = false;
        owner.SelectFirstEnabled(this, forward: true);
    }

    internal void SetActive(T value)
    {
        HasActive = true;
        ActiveValue = value;
    }

    internal void SetPressed(T value)
    {
        HasPressed = true;
        PressedValue = value;
    }

    internal void ClearPressed() => HasPressed = false;

    /// <summary>提交：写<b>当前描述</b>的绑定恰好一次、关闭弹层、焦点留在 trigger。</summary>
    /// <remarks>
    /// 行视图长时间存活，可能跨越父组件重建：提交必须经 <c>ownerNode.View</c> 取当前 <see cref="Select{T}"/>，
    /// 用最新的 Value 绑定写入——不能闭包捕获旧描述/旧 Binding 实例（否则 UI 已刷新却仍调用旧 setter）。
    /// </remarks>
    internal void Commit(T value, ViewNode node)
    {
        if (Node is not { } selectNode
            || selectNode.IsEffectivelyDisabled
            || selectNode.View is not Select<T> owner)
        {
            return;
        }

        owner.Value.Value = value;
        Open = false;
        ClearPressed();
        PendingEnsureVisible = false;
        node.Runtime.SetFocus(selectNode);
        selectNode.InvalidateLayout();
    }

    /// <summary>internal ensure-visible：active 行移出可视区时调整 popup 内部滚动（安全阶段调用）。</summary>
    /// <returns>是否已兑现（目标行与视口都可用，包含“本来就在可视区”）；false 表示尚未布局完成，稍后重试。</returns>
    internal bool EnsureActiveVisible()
    {
        if (!HasActive)
        {
            return true;
        }

        if (ScrollNode is not { IsMounted: true } scroll || scroll.View is not ScrollView)
        {
            return false;
        }

        // 当前 ScrollNode 必须属于“本次内容代际且已 Arrange”：否则行位置还是旧内容的，不能据此清 pending。
        if (ScrollReadyGeneration != ContentGeneration)
        {
            return false;
        }

        var viewport = scroll.Frame;
        if (viewport.IsEmpty)
        {
            return false;
        }

        var row = FindRow(scroll, ActiveValue);
        if (row is null || row.Frame.IsEmpty)
        {
            return false;
        }

        double delta = 0;
        if (row.Frame.Bottom > viewport.Bottom)
        {
            delta = row.Frame.Bottom - viewport.Bottom;
        }
        else if (row.Frame.Y < viewport.Y)
        {
            delta = row.Frame.Y - viewport.Y;
        }

        if (Math.Abs(delta) >= 0.5)
        {
            scroll.ScrollOffsetY += delta;
            scroll.InvalidateLayout();
        }

        return true;
    }

    private static ViewNode? FindRow(ViewNode parent, T value)
    {
        foreach (var child in parent.ChildList)
        {
            if (child.View is SelectOptionView<T> row && EqualityComparer<T>.Default.Equals(row.Item.Value, value))
            {
                return child;
            }

            if (FindRow(child, value) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}

/// <summary>弹层内容：内部 ScrollView + 选项行（宽至少 trigger 宽，最高由 placement/MaxPopupHeight 限制）。</summary>
internal sealed class SelectPopupContent<T> : View
{
    private readonly View[] _children;

    internal SelectPopupContent(SelectState<T> state, IReadOnlyList<SelectItem<T>> items, int generation)
    {
        State = state;
        Items = items;
        Generation = generation;
        // ForEach 直接作为 ScrollView 内容会展开成多个同级子项，而 ScrollView 只测量/排列第一个子项；
        // 因此用 VStack 作为唯一内容根（行仍按值 key 稳定复用）。
        Scroll = new ScrollView(
            new VStack(new ForEach<SelectItem<T>>(
                items,
                (item, _) => new SelectOptionView<T>(state, item),
                (item, _) => (object?)item.Value!)),
            ScrollAxis.Vertical)
        {
            ScrollbarStyle = ScrollbarStyle.Standard,
        };
        _children = [Scroll];
    }

    internal SelectState<T> State { get; }

    internal IReadOnlyList<SelectItem<T>> Items { get; }

    internal int Generation { get; }

    internal ScrollView Scroll { get; }

    public override string DebugName => "SelectPopup";

    internal override IReadOnlyList<View> Children => _children;

    internal override void Measure(ViewNode node, LayoutConstraints constraints, LayoutContext context)
    {
        State.ScrollNode = node.ChildList[0];
        State.ScrollReadyGeneration = Generation;

        // 面板内边距：内容在真实 inset 内测量/排列/裁剪（不是只画一层背景矩形）。
        var inner = constraints.Deflate(new Thickness(Select<T>.PanelInset));
        context.Measure(node.ChildList[0], inner);

        var desired = node.ChildList[0].DesiredSize;
        var inset = Select<T>.PanelInset * 2;
        var minWidth = constraints.HasBoundedWidth
            ? Math.Min(State.TriggerWidth, constraints.MaxWidth)
            : State.TriggerWidth;
        node.DesiredSize = new Size(
            Math.Max(desired.Width + inset, minWidth),
            desired.Height + inset);
    }

    internal override void Arrange(ViewNode node, Rect bounds, LayoutContext context)
    {
        node.Frame = bounds;
        State.ScrollNode = node.ChildList[0];
        State.ScrollReadyGeneration = Generation;
        context.Arrange(node.ChildList[0], bounds.Deflate(new Thickness(Select<T>.PanelInset)));
    }

    /// <summary>子项裁剪到面板 inset 内：滚动条/行背景不会压到边框与圆角外侧。</summary>
    internal override Rect? GetChildClip(ViewNode node)
        => node.Frame.IsEmpty ? null : node.Frame.Deflate(new Thickness(Select<T>.PanelInset));

    internal override void Emit(ViewNode node, DrawContext context)
    {
        if (node.Frame.IsEmpty)
        {
            return;
        }

        // 不透明面板：主题 Surface + Border + 控件族圆角（不硬编码颜色，也不改通用 PopupSurface 默认）。
        var theme = node.Environment.ResolvedTheme;
        context.Commands.Add(new RectangleCommand(
            node.Frame,
            theme.Surface,
            Select<T>.PanelRadius,
            theme.Border,
            1,
            node.Environment.Opacity));

        var restore = context.CurrentClip;
        if (context.PushClip(node.Frame.Deflate(new Thickness(Select<T>.PanelInset))))
        {
            foreach (var child in node.Children)
            {
                context.EmitChild(child);
            }

            context.PopClip(restore);
        }
    }

    internal override bool OnPointerDown(InputContext context, ViewNode node, PointerEvent e)
    {
        // 弹层内未命中行（空白/禁用行区域）的按下也要吞掉：
        // 否则“空白按下清焦”会把焦点从 trigger 抢走，后续键盘提交就丢了。
        context.MarkHandled();
        return true;
    }
}

/// <summary>单个选项行：行特有的指针交互与绘制（非 focusable、不进入 Tab 序列、不抢 trigger 焦点）。</summary>
internal sealed class SelectOptionView<T> : View
{
    private readonly SelectState<T> _state;

    internal SelectOptionView(SelectState<T> state, SelectItem<T> item)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(item);
        _state = state;
        Item = item;
    }

    internal SelectItem<T> Item { get; }

    public override string DebugName => "SelectOption";

    internal override IReadOnlyList<View> Children => [];

    internal override void Measure(ViewNode node, LayoutConstraints constraints, LayoutContext context)
    {
        var font = node.Environment.Font ?? FontDescription.Default;
        var available = constraints.HasBoundedWidth
            ? Math.Max(0, constraints.MaxWidth - (Select<T>.RowPadding * 2))
            : double.PositiveInfinity;
        var layout = TextPainter.Layout(
            context, Item.DisplayLabel, font, available, context.PixelScale,
            TextWrapping.NoWrap, TextTrimming.Ellipsis);
        node.ViewCache = layout;

        node.DesiredSize = new Size(
            layout.Size.Width + (Select<T>.RowPadding * 2),
            Math.Max(Select<T>.RowHeight, layout.Size.Height + 8));
    }

    internal override void Emit(ViewNode node, DrawContext context)
    {
        if (node.Frame.IsEmpty)
        {
            return;
        }

        var theme = node.Environment.ResolvedTheme;
        var disabled = Item.Disabled || node.IsEffectivelyDisabled;
        var active = _state.HasActive && EqualityComparer<T>.Default.Equals(_state.ActiveValue, Item.Value);

        if (active)
        {
            context.Commands.Add(new RectangleCommand(
                node.Frame, theme.SelectionBackground, 0, null, 0, node.Environment.Opacity));
        }

        var font = node.Environment.Font ?? FontDescription.Default;
        var textRect = new Rect(
            node.Frame.X + Select<T>.RowPadding,
            node.Frame.Y,
            Math.Max(0, node.Frame.Width - (Select<T>.RowPadding * 2)),
            node.Frame.Height);
        var request = TextPainter.CreateRequest(
            Item.DisplayLabel, font, textRect.Width, context.PixelScale,
            TextWrapping.NoWrap, TextTrimming.Ellipsis);
        var layout = context.TextService.Layout(request);
        var color = disabled ? theme.DisabledForeground : theme.Foreground;

        var restore = context.CurrentClip;
        if (context.PushClip(node.Frame))
        {
            TextPainter.Paint(context, node, layout, color, textRect);
            context.PopClip(restore);
        }
    }

    internal override bool OnPointerEnter(InputContext context, ViewNode node, PointerEvent e)
    {
        // 行 hover 更新 active（跳过 disabled），只影响绘制。
        if (Item.Disabled || node.IsEffectivelyDisabled)
        {
            return false;
        }

        if (!_state.HasActive || !EqualityComparer<T>.Default.Equals(_state.ActiveValue, Item.Value))
        {
            _state.SetActive(Item.Value);
            node.InvalidateRender();
        }

        return false;
    }

    internal override bool OnPointerDown(InputContext context, ViewNode node, PointerEvent e)
    {
        if (Item.Disabled || node.IsEffectivelyDisabled)
        {
            return false;
        }

        _state.SetActive(Item.Value);
        _state.SetPressed(Item.Value);
        node.InvalidateRender();
        context.MarkHandled();
        return true;
    }

    internal override bool OnPointerUp(InputContext context, ViewNode node, PointerEvent e)
    {
        if (Item.Disabled)
        {
            _state.ClearPressed();
            return true;
        }

        if (!_state.HasPressed || !EqualityComparer<T>.Default.Equals(_state.PressedValue, Item.Value))
        {
            _state.ClearPressed();
            return false;
        }

        _state.ClearPressed();
        if (node.Frame.Contains(e.Position))
        {
            _state.Commit(Item.Value, node);
        }

        context.MarkHandled();
        return true;
    }

    internal override bool OnPointerCancel(InputContext context, ViewNode node)
    {
        _state.ClearPressed();
        return false;
    }
}
