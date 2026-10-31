namespace CSharp.UI;

/// <summary>侧边导航项：值、显示标签与是否禁用。</summary>
/// <param name="Value">提交到绑定的值；同一 <see cref="Sidebar{T}"/> 内按 <see cref="EqualityComparer{T}.Default"/> 唯一。</param>
/// <param name="Label">显示标签；null 回退为 <c>Value?.ToString() ?? ""</c>（空串合法）。</param>
/// <param name="Disabled">该项不可选（跳过键盘/指针激活与提交）。</param>
public sealed record SidebarItem<T>(T Value, string? Label = null, bool Disabled = false)
{
    /// <summary>实际显示文本：显式标签优先，否则值字符串，null 值显示空串。</summary>
    internal string DisplayLabel => Label ?? (Value is null ? string.Empty : Value.ToString() ?? string.Empty);
}

/// <summary>
/// 纵向单选导航列表：<see cref="Binding{T}"/> 双向当前值，常驻主树（不走 popup/overlay）。
/// </summary>
/// <remarks>
/// <para>
/// 提交纪律与 <see cref="Select{T}"/> 同源：绑定只在提交路径写一次（pointer Up 落在同一行 /
/// Enter 或 Space 提交 active 行），同值重选仍写一次；导航、焦点进出、外部绑定变化、items
/// 替换都不写。items 内的 <see cref="SidebarItem{T}.Value"/> 必须唯一，重复抛
/// <see cref="ArgumentException"/>（不静默取第一个）；当前值不在 items 内时无高亮、不自动纠正。
/// </para>
/// <para>
/// <see cref="Width"/> 是<b>自然偏好宽度</b>而非硬宽：父约束更窄时收缩，可被
/// <c>Frame</c>/<c>Flex</c>/<c>FillWidth</c> 正常覆盖。超高内容滚动交给调用方
/// <see cref="ScrollView"/>：仅键盘 active 移动（Up/Down/Home/End）经
/// <see cref="UiRuntime.RequestEnsureVisible"/> 请求把 active 行带进最近祖先 ScrollView
/// 视口；用户滚轮/纯平移不触发，不产生回弹。
/// </para>
/// <para>
/// 单 focus owner：本控件可聚焦，选项行非 focusable、不入 Tab 序（与 <see cref="Select{T}"/> 同纪律）。
/// </para>
/// </remarks>
public sealed class Sidebar<T> : View
{
    private double _width = 200;

    /// <summary>由当前值绑定、项集合与可选 header 文本创建。</summary>
    /// <param name="value">当前值绑定（外部写入即时回显）。</param>
    /// <param name="items">导航项（按值唯一；null 值合法；构造时快照）。</param>
    /// <param name="header">顶部非选中、非导航的标题文本。</param>
    public Sidebar(Binding<T> value, IReadOnlyList<SidebarItem<T>> items, string? header = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(items);

        var copy = new SidebarItem<T>[items.Count];
        var seen = new HashSet<T>(EqualityComparer<T>.Default);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i] ?? throw new ArgumentNullException(nameof(items), "导航项不能为 null。");
            if (!seen.Add(item.Value))
            {
                throw new ArgumentException(
                    $"导航值重复：{DescribeValue(item.Value)}。同一 Sidebar 内的 Value 必须唯一。",
                    nameof(items));
            }

            copy[i] = item;
        }

        Value = value;
        Items = Array.AsReadOnly(copy);
        Header = header;
        _children = [new SidebarBody<T>(this)];
    }

    /// <summary>当前值绑定。</summary>
    public Binding<T> Value { get; }

    /// <summary>导航项（构造时快照，按值唯一）。</summary>
    public IReadOnlyList<SidebarItem<T>> Items { get; }

    /// <summary>可选标题文本；非选中行、不参与导航/提交。</summary>
    public string? Header { get; }

    /// <summary>
    /// 自然偏好宽度（逻辑像素，默认 200）：measure 期望宽 min(<see cref="Width"/>, 约束)；
    /// 不是硬宽，父级可收缩、<c>Frame</c>/<c>Flex</c>/<c>FillWidth</c> 可覆盖。
    /// 必须是有限<b>正</b>数（0 不是合法偏好；父约束 0 仍可量出 0 宽）。
    /// </summary>
    public double Width
    {
        get => _width;
        init
        {
            if (!double.IsFinite(value) || value <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(Width), value, "Width 必须是有限正数。");
            }

            _width = value;
        }
    }

    /// <inheritdoc />
    public override string DebugName => "Sidebar";

    /// <inheritdoc />
    public override bool IsFocusable => true;

    // 行视觉常量（逻辑像素）。
    internal const double RowMinHeight = 32;
    internal const double RowHorizontalPadding = 12;
    internal const double RowVerticalPadding = 6;
    internal const double HeaderHorizontalPadding = 12;
    internal const double HeaderVerticalPadding = 8;
    internal const double ActiveIndicatorWidth = 3;
    internal const double ActiveCornerRadius = 4;

    private readonly View[] _children;

    internal override IReadOnlyList<View> Children => _children;

    internal override void Measure(ViewNode node, LayoutConstraints constraints, LayoutContext context)
    {
        var child = node.ChildList[0];
        context.Measure(child, constraints);
        node.DesiredSize = new Size(
            constraints.HasBoundedWidth ? Math.Min(Width, Math.Max(0, constraints.MaxWidth)) : Width,
            child.DesiredSize.Height);
    }

    internal override void Arrange(ViewNode node, Rect bounds, LayoutContext context)
    {
        node.Frame = bounds;
        context.Arrange(node.ChildList[0], bounds);
    }

    internal override LayoutFeatures GetLayoutFeatures(ViewNode node, LayoutAxis mainAxis)
    {
        var inner = node.ChildList.Count > 0
            ? node.ChildList[0].GetLayoutFeatures(mainAxis)
            : LayoutFeatures.FromNatural(node.DesiredSize);
        // 宽度以声明的偏好为自然基准（即使父约束截断 Desired 也保留偏好声明）；
        // 高度透传 body（header + 行高之和）。
        return new LayoutFeatures(
            inner.Horizontal with { NaturalSize = Width },
            inner.Vertical);
    }

    internal override void Emit(ViewNode node, DrawContext context)
    {
        if (node.Frame.IsEmpty)
        {
            return;
        }

        var theme = node.Environment.ResolvedTheme;
        context.Commands.Add(new RectangleCommand(
            node.Frame, theme.Surface, 0, theme.Border, 1, node.Environment.Opacity));

        foreach (var child in node.ChildList)
        {
            context.EmitChild(child);
        }

        // owner 级焦点环画在行内容之上（行自身非 focusable）。
        if (node.IsFocused && !node.IsEffectivelyDisabled)
        {
            context.Commands.Add(new RectangleCommand(
                node.Frame, Color.Transparent, 0, theme.FocusRing, 2, node.Environment.Opacity));
        }
    }

    internal override void OnUpdate(ViewNode node, View previous)
    {
        // items 引用替换（重排/删除/禁用翻转/新绑定）后：active/pressed 按值在
        // 新 items 上重新定位；不合法（移除/禁用）则丢 active，不自动改选不写值。
        if (previous is Sidebar<T> prev && !ReferenceEquals(prev.Items, Items))
        {
            var state = StateOf(node);
            state.SyncActive(this);
            state.SyncPressed(this);
        }
    }

    internal override bool LayoutPropertiesEquivalent(View other)
        => other is Sidebar<T> sidebar
            && sidebar.Width == Width
            && sidebar.Header == Header
            && ReferenceEquals(sidebar.Items, Items);

    // ---------------------------------------------------------------- 键盘

    internal override bool OnKeyDown(InputContext context, ViewNode node, KeyEvent e)
    {
        if (node.IsEffectivelyDisabled)
        {
            return false;
        }

        // 带命令/替换修饰符的组合键不归控件（不吞全局快捷键、IME 候选导航）。
        if (e.Modifiers.HasFlag(KeyModifiers.Control)
            || e.Modifiers.HasFlag(KeyModifiers.Meta)
            || e.Modifiers.HasFlag(KeyModifiers.Alt))
        {
            return false;
        }

        var state = StateOf(node);
        switch (e.Key)
        {
            case UiKey.Enter or UiKey.Space:
                // 长按重复不得重复提交：重复按键一律消费但不动作。
                if (e.Repeat)
                {
                    return true;
                }

                // 无 active 时先按当前绑定值初始化：仅当绑定值是有效 enabled 项才置 active，
                // 否则保持未选（未知/禁用值不纠正、不强选首项）；之后提交——同值重选仍写一次。
                EnsureActiveInitialized(node, state);
                return CommitActive(node, state);

            case UiKey.Down or UiKey.Up:
                EnsureActiveInitialized(node, state);
                MoveActive(node, state, e.Key == UiKey.Down ? 1 : -1);
                return true;

            case UiKey.Home or UiKey.End:
                EnsureActiveInitialized(node, state);
                MoveActiveToEdge(node, state, first: e.Key == UiKey.Home);
                return true;

            case UiKey.Tab:
                // 不消费：正常焦点遍历离开/进入本控件。
                return false;
        }

        return false;
    }

    internal override bool OnPointerDown(InputContext context, ViewNode node, PointerEvent e)
    {
        if (node.IsEffectivelyDisabled)
        {
            return false;
        }

        // 空白区（非行）按下：聚焦 owner、吞事件——不冒泡成"空白按下清焦"，也不写值。
        context.Runtime.SetFocus(node);
        context.MarkHandled();
        return true;
    }

    // ---------------------------------------------------------------- 协作

    internal SidebarState<T> StateOf(ViewNode node)
    {
        if (node.InteractionState is SidebarState<T> existing)
        {
            return existing;
        }

        var created = new SidebarState<T>();
        created.Bind(node);
        node.InteractionState = created;
        return created;
    }

    internal SidebarItem<T>? FindItem(T value)
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

    /// <summary>首次键盘交互前播种 active：当前绑定值若是有效 enabled 项则用它（与 Select 打开同纪律）；
    /// 未知值/disabled 不强选（MoveActive/Edge 会按方向取端点）。</summary>
    private void EnsureActiveInitialized(ViewNode node, SidebarState<T> state)
    {
        if (state.HasActive)
        {
            return;
        }

        var current = FindItem(Value.Read(node));
        if (current is { Disabled: false })
        {
            state.SetActive(current.Value);
        }
    }

    internal bool CommitActive(ViewNode node, SidebarState<T> state)
    {
        if (!state.HasActive)
        {
            return false;
        }

        state.Commit(state.ActiveValue);
        return true;
    }

    /// <summary>键盘 active 移动（跳过 disabled、两端停不环绕）；只 move 不写，随后请求 ensure。</summary>
    internal void MoveActive(ViewNode node, SidebarState<T> state, int direction)
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
                SetActiveAndEnsure(node, state, items[index].Value);
                return;
            }
        }
    }

    internal void MoveActiveToEdge(ViewNode node, SidebarState<T> state, bool first)
    {
        var items = Items;
        if (first)
        {
            for (var i = 0; i < items.Count; i++)
            {
                if (!items[i].Disabled)
                {
                    SetActiveAndEnsure(node, state, items[i].Value);
                    return;
                }
            }
        }
        else
        {
            for (var i = items.Count - 1; i >= 0; i--)
            {
                if (!items[i].Disabled)
                {
                    SetActiveAndEnsure(node, state, items[i].Value);
                    return;
                }
            }
        }
    }
    /// <summary>键盘路径的 active 变更：设置 active + 请求把该行带进最近祖先 ScrollView 视口。</summary>
    /// <remarks>请求携带 owner 节点（不是行节点）：消费端经 <see cref="ResolveEnsureVisibleTarget"/>
    /// 在布局收敛后按当前描述+已挂载子树解析最新行——items 重排/替换后仍按值命中正确行。</remarks>
    private void SetActiveAndEnsure(ViewNode node, SidebarState<T> state, T value)
    {
        state.SetActive(value);
        node.InvalidateRender();
        node.Runtime.RequestEnsureVisible(node);
    }

    /// <summary>键盘 ensure 目标：active 值对应的当前行节点；active 已被移除/禁用、owner 失焦/失效时为 null（消费一次即 drop，不持续重试）。</summary>
    internal override ViewNode? ResolveEnsureVisibleTarget(ViewNode node)
    {
        if (node.IsEffectivelyDisabled || !node.IsFocused)
        {
            return null;
        }

        var state = node.InteractionState as SidebarState<T>;
        if (state is not { HasActive: true })
        {
            return null;
        }

        // 目标行仍是"当前描述里的 enabled active 项"才兑现——items 被替换/禁用翻转后
        // 旧 active 可能已失配，此时自然 drop（不滚动到错误的行）。
        var item = FindItem(state.ActiveValue);
        return item is { Disabled: false } ? FindRow(node, item.Value) : null;
    }

    internal int IndexOfValue(IReadOnlyList<SidebarItem<T>> items, T value)
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

    /// <summary>在 owner 子树内定位承载 <paramref name="value"/> 的行节点（ensure 用；未 reconcile 时为 null）。</summary>
    internal ViewNode? FindRow(ViewNode node, T value)
    {
        foreach (var child in node.ChildList)
        {
            if (child.View is SidebarRowView<T> row && EqualityComparer<T>.Default.Equals(row.Item.Value, value))
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

    private static string DescribeValue(T value)
        => value is null ? "null" : value.ToString() ?? value.GetType().Name;
}

/// <summary>
/// 单个 <see cref="Sidebar{T}"/> 节点持有的交互状态：active/pressed 值。
/// </summary>
/// <remarks>
/// 按<b>值</b>记录 active/pressed（不是列表下标）：items 被替换/重排/禁用翻转后按值重定位，
/// 不会把按下/高亮落到另一项上；状态挂在节点上，描述重建（同 key/类型）不丢 active。
/// </remarks>
internal sealed class SidebarState<T>
{
    internal ViewNode? Node { get; private set; }

    internal bool HasActive { get; private set; }

    internal T ActiveValue { get; private set; } = default!;

    internal bool HasPressed { get; private set; }

    internal T PressedValue { get; private set; } = default!;

    internal void Bind(ViewNode node) => Node = node;

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

    /// <summary>提交：写<b>当前描述</b>的绑定恰好一次；导航/焦点/外部变化不调本路径。</summary>
    /// <remarks>
    /// 行视图可能跨越父组件重建：提交必须经 <see cref="Node"/> 取当前 <see cref="Sidebar{T}"/>，
    /// 用最新的 Value 绑定写入——不能闭包捕获旧描述/旧 Binding 实例。
    /// </remarks>
    internal void Commit(T value)
    {
        if (Node is not { } owner
            || owner.IsEffectivelyDisabled
            || owner.View is not Sidebar<T> sidebar)
        {
            return;
        }

        sidebar.Value.Value = value;
        ClearPressed();
        owner.InvalidateRender();
    }

    /// <summary>items 替换后按值清理非法 active（移除/禁用 → 丢 active，不自动改选）。</summary>
    internal void SyncActive(Sidebar<T> owner)
    {
        if (!HasActive)
        {
            return;
        }

        var index = owner.IndexOfValue(owner.Items, ActiveValue);
        if (index < 0 || owner.Items[index].Disabled)
        {
            HasActive = false;
        }
    }

    /// <summary>items 替换后按值清理非法 pressed（同上纪律）。</summary>
    internal void SyncPressed(Sidebar<T> owner)
    {
        if (!HasPressed)
        {
            return;
        }

        var index = owner.IndexOfValue(owner.Items, PressedValue);
        if (index < 0 || owner.Items[index].Disabled)
        {
            HasPressed = false;
        }
    }
}

/// <summary>
/// body：header 文本 + 按值 key 的 <see cref="ForEach{T}"/> 行列表。
/// 把 header 与 ForEach 作为真实子节点交给协调器展开/保身份；body 只负责纵向堆叠排列。
/// </summary>
internal sealed class SidebarBody<T> : View
{
    private readonly Sidebar<T> _owner;

    /// <summary>
    /// null 值的 ForEach key 哨兵：非 null 对象，避免 <c>null → index 回退</c> 与真实数字 key 碰撞
    /// （例如 <c>Sidebar&lt;int?&gt;</c> 的 <c>[null, 0]</c>：null 用哨兵、0 用真实值 0，不再双 key 0）。
    /// </summary>
    private static readonly object NullKey = new();

    internal SidebarBody(Sidebar<T> owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _owner = owner;

        var items = owner.Items;
        var list = new List<View>(items.Count + (owner.Header is not null ? 1 : 0));
        if (owner.Header is { } header)
        {
            list.Add(new SidebarHeader<T>(header));
        }

        // ForEach 作为真实子节点：协调器展开为同级行节点并按 key 保持身份。
        // keySelector 必须返回非 null 对象（null 会走 index 回退并与真实值碰撞）；
        // 不用 ToString 作 key——不同 Value 可能同 ToString。
        list.Add(new ForEach<SidebarItem<T>>(
            items,
            (item, _) => new SidebarRowView<T>(item),
            (item, _) => (object)(item.Value ?? NullKey)));

        _children = [.. list];
    }

    private readonly View[] _children;

    internal override IReadOnlyList<View> Children => _children;

    public override string DebugName => "SidebarBody";

    internal override void Measure(ViewNode node, LayoutConstraints constraints, LayoutContext context)
    {
        var width = constraints.MaxWidth;
        double total = 0;
        foreach (var child in node.ChildList)
        {
            context.Measure(child, new LayoutConstraints(0, Math.Max(0, width), 0, double.PositiveInfinity));
            total += child.DesiredSize.Height;
        }

        node.DesiredSize = new Size(
            constraints.HasBoundedWidth ? Math.Min(width, Math.Max(0, constraints.MaxWidth)) : width,
            total);
    }

    internal override void Arrange(ViewNode node, Rect bounds, LayoutContext context)
    {
        node.Frame = bounds;
        var y = bounds.Y;
        foreach (var child in node.ChildList)
        {
            var h = child.DesiredSize.Height;
            context.Arrange(child, new Rect(bounds.X, y, bounds.Width, h));
            y += h;
        }
    }
}

/// <summary>顶部标题：非选中、非导航行（muted 文本 + 分隔线）。</summary>
internal sealed class SidebarHeader<T> : View
{
    internal SidebarHeader(string text)
    {
        Text = text;
        _children = [];
    }

    internal string Text { get; }

    private readonly View[] _children;

    internal override IReadOnlyList<View> Children => _children;

    public override string DebugName => "SidebarHeader";

    internal override void Measure(ViewNode node, LayoutConstraints constraints, LayoutContext context)
    {
        var font = node.Environment.Font ?? FontDescription.Default;
        var available = constraints.HasBoundedWidth
            ? Math.Max(0, constraints.MaxWidth - (Sidebar<T>.HeaderHorizontalPadding * 2))
            : double.PositiveInfinity;
        var layout = TextPainter.Layout(
            context, Text, font, available, context.PixelScale,
            TextWrapping.NoWrap, TextTrimming.Ellipsis);
        node.ViewCache = layout;
        node.DesiredSize = new Size(
            layout.Size.Width + (Sidebar<T>.HeaderHorizontalPadding * 2),
            layout.Size.Height + (Sidebar<T>.HeaderVerticalPadding * 2));
    }

    internal override void Emit(ViewNode node, DrawContext context)
    {
        if (node.Frame.IsEmpty)
        {
            return;
        }

        var theme = node.Environment.ResolvedTheme;
        var textRect = new Rect(
            node.Frame.X + Sidebar<T>.HeaderHorizontalPadding,
            node.Frame.Y,
            Math.Max(0, node.Frame.Width - (Sidebar<T>.HeaderHorizontalPadding * 2)),
            Math.Max(0, node.Frame.Height - 1));
        var request = TextPainter.CreateRequest(
            Text, node.Environment.Font ?? FontDescription.Default, textRect.Width, context.PixelScale,
            TextWrapping.NoWrap, TextTrimming.Ellipsis);
        var layout = context.TextService.Layout(request);
        var restore = context.CurrentClip;
        if (context.PushClip(node.Frame))
        {
            TextPainter.Paint(context, node, layout, theme.MutedForeground, textRect);
            context.PopClip(restore);
        }

        // 底部分隔线。
        context.Commands.Add(new RectangleCommand(
            new Rect(node.Frame.X, node.Frame.Bottom - 1, node.Frame.Width, 1),
            theme.Border, 0, null, 0, node.Environment.Opacity));
    }
}

/// <summary>单个选项行：行特有的指针交互与绘制（非 focusable、不进入 Tab 序列、不抢 owner 焦点）。</summary>
internal sealed class SidebarRowView<T> : View
{
    internal SidebarRowView(SidebarItem<T> item)
    {
        ArgumentNullException.ThrowIfNull(item);
        Item = item;
    }

    internal SidebarItem<T> Item { get; }

    public override string DebugName => "SidebarRow";

    internal override IReadOnlyList<View> Children => [];

    // 行挂在 body 下：body.Parent 即 owner 节点（ForEach 展开后行是 body 的直接子节点）。
    private static ViewNode? OwnerNode(ViewNode node)
    {
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            if (current.View is Sidebar<T>)
            {
                return current;
            }
        }

        return null;
    }

    internal override void Measure(ViewNode node, LayoutConstraints constraints, LayoutContext context)
    {
        var font = node.Environment.Font ?? FontDescription.Default;
        var available = constraints.HasBoundedWidth
            ? Math.Max(0, constraints.MaxWidth - (Sidebar<T>.RowHorizontalPadding * 2))
            : double.PositiveInfinity;
        var layout = TextPainter.Layout(
            context, Item.DisplayLabel, font, available, context.PixelScale,
            TextWrapping.NoWrap, TextTrimming.Ellipsis);
        node.ViewCache = layout;
        node.DesiredSize = new Size(
            layout.Size.Width + (Sidebar<T>.RowHorizontalPadding * 2),
            Math.Max(Sidebar<T>.RowMinHeight, layout.Size.Height + (Sidebar<T>.RowVerticalPadding * 2)));
    }

    internal override void Emit(ViewNode node, DrawContext context)
    {
        if (node.Frame.IsEmpty)
        {
            return;
        }

        var theme = node.Environment.ResolvedTheme;
        var disabled = Item.Disabled || node.IsEffectivelyDisabled;
        var owner = OwnerNode(node);
        var sidebar = owner?.View as Sidebar<T>;
        var state = owner is null || sidebar is null ? null : sidebar.StateOf(owner);
        var selected = sidebar is not null
            && EqualityComparer<T>.Default.Equals(Item.Value, sidebar.Value.Read(node));
        var active = state is { HasActive: true } && EqualityComparer<T>.Default.Equals(state.ActiveValue, Item.Value);
        var pressed = state is { HasPressed: true } && EqualityComparer<T>.Default.Equals(state.PressedValue, Item.Value);

        // 行底色：selected > pressed > active > hover > 透明。
        Color? rowFill = null;
        if (!disabled)
        {
            if (selected)
            {
                rowFill = theme.SelectionBackground;
            }
            else if (pressed)
            {
                rowFill = theme.ControlPressedBackground;
            }
            else if (active || node.IsHovered)
            {
                rowFill = theme.ControlHoverBackground;
            }
        }

        if (rowFill is { } fill)
        {
            var radius = selected ? Sidebar<T>.ActiveCornerRadius : 0.0;
            context.Commands.Add(new RectangleCommand(
                node.Frame, fill, radius, null, 0, node.Environment.Opacity));
        }

        // active/selected 左缘指示条。
        if ((active || selected) && !disabled)
        {
            context.Commands.Add(new RectangleCommand(
                new Rect(node.Frame.X, node.Frame.Y + 4, Sidebar<T>.ActiveIndicatorWidth, Math.Max(0, node.Frame.Height - 8)),
                theme.Accent, 1, null, 0, node.Environment.Opacity));
        }

        var textRect = new Rect(
            node.Frame.X + Sidebar<T>.RowHorizontalPadding,
            node.Frame.Y,
            Math.Max(0, node.Frame.Width - (Sidebar<T>.RowHorizontalPadding * 2)),
            node.Frame.Height);
        var request = TextPainter.CreateRequest(
            Item.DisplayLabel, node.Environment.Font ?? FontDescription.Default, textRect.Width, context.PixelScale,
            TextWrapping.NoWrap, TextTrimming.Ellipsis);
        var layout = context.TextService.Layout(request);
        var color = disabled
            ? theme.DisabledForeground
            : selected ? theme.Accent : theme.Foreground;

        var restore = context.CurrentClip;
        if (context.PushClip(node.Frame))
        {
            TextPainter.Paint(context, node, layout, color, textRect);
            context.PopClip(restore);
        }
    }

    internal override bool OnPointerEnter(InputContext context, ViewNode node, PointerEvent e)
    {
        if (Item.Disabled || node.IsEffectivelyDisabled || OwnerNode(node) is not { } owner
            || owner.View is not Sidebar<T> sidebar)
        {
            return false;
        }

        var state = sidebar.StateOf(owner);
        if (!state.HasActive || !EqualityComparer<T>.Default.Equals(state.ActiveValue, Item.Value))
        {
            state.SetActive(Item.Value);
            node.InvalidateRender();
        }

        return false;
    }

    internal override bool OnPointerDown(InputContext context, ViewNode node, PointerEvent e)
    {
        if (Item.Disabled || node.IsEffectivelyDisabled || OwnerNode(node) is not { } owner
            || owner.View is not Sidebar<T> sidebar)
        {
            return false;
        }

        var state = sidebar.StateOf(owner);
        state.SetActive(Item.Value);
        state.SetPressed(Item.Value);
        context.Runtime.SetFocus(owner);
        node.InvalidateRender();
        context.MarkHandled();
        return true;
    }

    internal override bool OnPointerUp(InputContext context, ViewNode node, PointerEvent e)
    {
        if (OwnerNode(node) is not { } owner || owner.View is not Sidebar<T> sidebar)
        {
            return false;
        }

        var state = sidebar.StateOf(owner);
        if (Item.Disabled)
        {
            state.ClearPressed();
            return true;
        }

        if (!state.HasPressed || !EqualityComparer<T>.Default.Equals(state.PressedValue, Item.Value))
        {
            state.ClearPressed();
            return false;
        }

        state.ClearPressed();
        if (node.Frame.Contains(e.Position))
        {
            state.Commit(Item.Value);
        }

        context.MarkHandled();
        return true;
    }

    internal override bool OnPointerCancel(InputContext context, ViewNode node)
    {
        if (OwnerNode(node) is { } owner && owner.View is Sidebar<T> sidebar)
        {
            sidebar.StateOf(owner).ClearPressed();
        }

        return false;
    }
}
