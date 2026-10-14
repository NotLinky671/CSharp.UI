namespace CSharp.UI;

/// <summary>
/// 视图描述基类：不可变、可重用的 UI 声明。
/// </summary>
/// <remarks>
/// <para>
/// 视图描述与运行时节点（<see cref="ViewNode"/>）分离：描述可以重复声明与复用，
/// 节点由协调器（reconciler）创建、复用与卸载，并保存布局结果、交互状态与组件状态。
/// </para>
/// <para>
/// 应用通过组合内建视图、链式修饰符与自定义 <see cref="Component"/> 构建界面；
/// 不继承本类实现自定义绘制（首版范围）。
/// </para>
/// </remarks>
public abstract class View
{
    /// <summary>子视图描述。不展开 <see cref="KeyedView"/> 与 <c>ForEach</c>，展开由协调器完成。</summary>
    internal virtual IReadOnlyList<View> Children => [];

    /// <summary>用于诊断与测试的可读名称。</summary>
    public virtual string DebugName => GetType().Name;

    /// <summary>该视图是否可以作为 Tab 焦点目标。</summary>
    public virtual bool IsFocusable => false;

    /// <summary>
    /// 由修饰符决定的环境覆盖。框架在测量前调用，子级环境由父级环境与自身覆盖合成。
    /// </summary>
    internal virtual EnvironmentValues ResolveEnvironment(ViewNode node, EnvironmentValues parent) => parent;

    /// <summary>
    /// 测量阶段：根据父级约束计算期望尺寸并写入 <see cref="ViewNode.DesiredSize"/>。
    /// 容器实现需通过 <see cref="LayoutContext.Measure"/> 测量子节点。
    /// </summary>
    internal virtual void Measure(ViewNode node, LayoutConstraints constraints, LayoutContext context)
        => node.DesiredSize = Size.Zero;

    /// <summary>
    /// 排列阶段：把节点的最终矩形（绝对逻辑坐标）写入 <see cref="ViewNode.Frame"/>，
    /// 并通过 <see cref="LayoutContext.Arrange"/> 排列子节点。
    /// </summary>
    internal virtual void Arrange(ViewNode node, Rect bounds, LayoutContext context) => node.Frame = bounds;

    /// <summary>绘制命令生成阶段：按 背景 → 自身 → 边框 → 子项 的顺序向上下文追加命令。</summary>
    internal virtual void Emit(ViewNode node, DrawContext context)
    {
        foreach (var child in node.Children)
        {
            context.EmitChild(child);
        }
    }

    /// <summary>节点挂载后调用一次；用于申请资源或订阅。</summary>
    internal virtual void OnMount(ViewNode node)
    {
    }

    /// <summary>同身份节点被新描述更新后调用；用于替换属性与事件处理器。</summary>
    internal virtual void OnUpdate(ViewNode node, View previous)
    {
    }

    /// <summary>节点卸载前调用一次；用于释放资源、停止订阅与清理交互状态。</summary>
    internal virtual void OnUnmount(ViewNode node)
    {
    }

    /// <summary>
    /// 需要定时唤醒重绘时返回下一次唤醒的相对延迟（例如 TextField 光标闪烁）；
    /// 返回 null 表示不需要定时唤醒。
    /// </summary>
    internal virtual TimeSpan? GetNextWakeup(ViewNode node, TimeSpan now) => null;

    /// <summary>该节点当前是否参与命中测试（不可见或被裁剪的内容不参与）。</summary>
    internal virtual bool IsHitTestVisible(ViewNode node) => true;

    /// <summary>节点自身是否命中指定点（默认使用排列矩形）。</summary>
    internal virtual bool HitTestSelf(ViewNode node, Point position) => node.Frame.Contains(position);

    /// <summary>指针按下；返回 true 表示已处理并阻止继续冒泡。</summary>
    internal virtual bool OnPointerDown(InputContext context, ViewNode node, PointerEvent e) => false;

    /// <summary>指针移动（含按下拖动）。</summary>
    internal virtual bool OnPointerMove(InputContext context, ViewNode node, PointerEvent e) => false;

    /// <summary>指针释放。</summary>
    internal virtual bool OnPointerUp(InputContext context, ViewNode node, PointerEvent e) => false;

    /// <summary>指针进入。</summary>
    internal virtual bool OnPointerEnter(InputContext context, ViewNode node, PointerEvent e) => false;

    /// <summary>指针离开。</summary>
    internal virtual bool OnPointerLeave(InputContext context, ViewNode node, PointerEvent e) => false;

    /// <summary>指针捕获被取消（窗口失焦、节点卸载）；控件应中止拖动或按下状态而不触发点击。</summary>
    internal virtual bool OnPointerCancel(InputContext context, ViewNode node) => false;

    /// <summary>滚轮。</summary>
    internal virtual bool OnWheel(InputContext context, ViewNode node, WheelEvent e) => false;

    /// <summary>键盘按下。</summary>
    internal virtual bool OnKeyDown(InputContext context, ViewNode node, KeyEvent e) => false;

    /// <summary>键盘释放。</summary>
    internal virtual bool OnKeyUp(InputContext context, ViewNode node, KeyEvent e) => false;

    /// <summary>已提交文本输入。</summary>
    internal virtual bool OnTextInput(InputContext context, ViewNode node, TextInputEvent e) => false;

    /// <summary>焦点获得或失去。</summary>
    internal virtual void OnFocusChanged(InputContext context, ViewNode node, bool focused)
    {
    }

    /// <summary>视图自身的布局相关属性是否等价（不含子项，也不含只影响绘制的属性，例如纯颜色）。</summary>
    internal virtual bool LayoutPropertiesEquivalent(View other) => true;

    /// <summary>
    /// 描述树在布局上是否等价。为 true 时协调器只标记重绘，不重新测量与排列。
    /// 默认实现比较类型、自身布局属性与子项结构；纯视觉变化（颜色、透明度）返回 true。
    /// </summary>
    internal virtual bool IsLayoutEquivalent(View other)
    {
        if (other is null || GetType() != other.GetType() || !LayoutPropertiesEquivalent(other))
        {
            return false;
        }

        var mine = Children;
        var theirs = other.Children;
        if (mine.Count != theirs.Count)
        {
            return false;
        }

        for (var i = 0; i < mine.Count; i++)
        {
            if (!mine[i].IsLayoutEquivalent(theirs[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>节点的绘制矩形是否包含指定矩形范围（用于裁剪剔除）。</summary>
    internal virtual Rect GetClipBounds(ViewNode node) => node.Frame;

    /// <summary>子项的被裁剪区域；返回非 null 表示该视图把子项裁剪到指定矩形（ScrollView、Clip）。</summary>
    internal virtual Rect? GetChildClip(ViewNode node) => null;

    /// <summary>在堆叠容器主轴上吸收剩余空间的权重；0 表示不吸收。</summary>
    internal virtual double FlexGrow(ViewNode node, bool horizontal) => 0;

    /// <summary>返回用于调试的简短描述。</summary>
    public override string ToString() => DebugName;
}

/// <summary>
/// 协调器的子项描述：展开 <see cref="KeyedView"/> 与 <c>ForEach</c> 后的单个同级条目。
/// </summary>
internal readonly record struct ChildDescriptor(View View, object? Key, bool HasKey)
{
    /// <summary>用于诊断的身份文本。</summary>
    public string Describe() => HasKey ? $"{View.DebugName}(key: {Key})" : View.DebugName;
}

/// <summary>
/// <c>Key</c> 修饰符：为同级节点提供稳定身份，使增删重排后节点与其附加状态仍属于原项目。
/// </summary>
public sealed class KeyedView : View
{
    /// <summary>由键与内部视图创建。</summary>
    public KeyedView(string key, View child)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(child);
        Key = key;
        Child = child;
    }

    /// <summary>稳定身份键。</summary>
    public string Key { get; init; }

    /// <summary>被包装的视图。</summary>
    public View Child { get; init; }

    /// <inheritdoc />
    public override string DebugName => $"Keyed({Key})";

    internal override bool LayoutPropertiesEquivalent(View other) => other is KeyedView keyed && keyed.Key == Key;
}

/// <summary>把值、键与其视图组合成一个条目，供 <c>ForEach</c> 使用。</summary>
/// <typeparam name="T">项目类型。</typeparam>
public readonly record struct ForEachItem<T>(T Value, object Key, View View);

/// <summary>
/// 动态集合视图的非泛型基类；协调器据此展开为多个同级节点。
/// </summary>
public abstract class ForEachView : View
{
    /// <summary>展开为子项描述。<paramref name="prefixKey"/> 来自外层 <see cref="KeyedView"/>。</summary>
    internal abstract void Expand(object? prefixKey, bool hasPrefixKey, List<ChildDescriptor> output);

    // 动态集合的结构由项目与键决定，保守地要求重新布局。
    internal sealed override bool IsLayoutEquivalent(View other) => false;
}

/// <summary>
/// 带稳定键的动态集合视图：<c>new ForEach&lt;T&gt;(items, item =&gt; view, item =&gt; item.Id)</c>。
/// </summary>
/// <typeparam name="T">项目类型。</typeparam>
/// <remarks>
/// 未提供键选择器时使用序号作为键；序号键在插入或删除后不能保持身份，需要稳定身份时应显式提供键。
/// </remarks>
public sealed class ForEach<T> : ForEachView
{
    /// <summary>由项目集合、构建函数与可选键选择器创建。</summary>
    public ForEach(IEnumerable<T> items, Func<T, int, View> builder, Func<T, int, object>? keySelector = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(builder);
        Items = items;
        Builder = builder;
        KeySelector = keySelector;
    }

    /// <summary>由项目集合与构建函数创建（序号键）。</summary>
    public ForEach(IEnumerable<T> items, Func<T, View> builder)
        : this(items, (item, _) => builder(item), null)
    {
    }

    /// <summary>由项目集合、构建函数与单参数键选择器创建（写法 <c>new ForEach&lt;T&gt;(items, item =&gt; view, item =&gt; item.Id)</c>）。</summary>
    public ForEach(IEnumerable<T> items, Func<T, View> builder, Func<T, object> keySelector)
        : this(items, (item, _) => builder(item), (item, _) => keySelector(item))
    {
    }

    /// <summary>项目集合。</summary>
    public IEnumerable<T> Items { get; init; }

    /// <summary>项目到视图的构建函数，第二个参数为序号。</summary>
    public Func<T, int, View> Builder { get; init; }

    /// <summary>项目到稳定键的选择器；null 时使用序号。</summary>
    public Func<T, int, object>? KeySelector { get; init; }

    /// <inheritdoc />
    public override string DebugName => $"ForEach<{typeof(T).Name}>";

    /// <inheritdoc />
    internal override void Expand(object? prefixKey, bool hasPrefixKey, List<ChildDescriptor> output)
    {
        var index = 0;
        foreach (var item in Items)
        {
            var view = Builder(item, index) ?? throw new InvalidOperationException($"ForEach<{typeof(T).Name}> 在序号 {index} 处返回了 null 视图。");

            // 展开项目自带的 Key（写法：new Item(...).Key(item.Id)）。
            object? explicitKey = null;
            while (view is KeyedView keyed)
            {
                explicitKey ??= keyed.Key;
                view = keyed.Child;
            }

            var localKey = explicitKey ?? KeySelector?.Invoke(item, index) ?? index;
            var key = hasPrefixKey ? (prefixKey, localKey) : localKey;

            if (view is ForEachView nested)
            {
                nested.Expand(key, true, output);
            }
            else
            {
                output.Add(new ChildDescriptor(view, key, true));
            }

            index++;
        }
    }
}

/// <summary>视图与修饰符的链式扩展。</summary>
public static class ViewExtensions
{
    /// <summary>为同级节点提供稳定身份。</summary>
    public static View Key(this View view, string key)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return new KeyedView(key, view);
    }
}
