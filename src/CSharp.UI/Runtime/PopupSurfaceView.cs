namespace CSharp.UI;

/// <summary>
/// 单个活动 popup 的宿主视图：popup 内容的实际父节点，挂在 overlay host 下。
/// </summary>
/// <remarks>
/// <para>
/// 环境：<see cref="ResolveEnvironment"/> 返回锚节点的<b>当前</b> <see cref="ViewNode.Environment"/>
/// （每轮布局读取现值，不做快照），因此锚局部 Theme/Disabled/Font 作用域对 popup 内容生效，
/// 锚环境变化在下一轮布局自然随动。
/// </para>
/// <para>
/// 裁剪：popup 内容裁剪到本节点的最终 Frame（<see cref="GetChildClip"/> 约束命中测试，
/// <see cref="Emit"/> 生成绘制裁剪命令），超限内容不会外溢到锚或视口之外。
/// </para>
/// </remarks>
internal sealed class PopupSurfaceView : View
{
    private readonly View[] _children;

    internal PopupSurfaceView(ViewNode anchor, View content)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(content);
        Anchor = anchor;
        Content = content;

        // portal 内容挂在一个内部 Component scope 下：让内容里的控件通过 Binding.Read 补登记的来源状态
        // 依赖能找到 Component 祖先（否则“绑定保存在字段、Build 不直接读值”的内容在 popup 内会丢依赖，
        // 外部状态变化不再请求重绘）。scope 仍是普通布局透传，不改变 public API / 焦点序 / owner 边沿。
        _children = [new PopupContentScope(content)];
    }

    /// <summary>定位与环境来源的锚节点。</summary>
    internal ViewNode Anchor { get; }

    /// <summary>popup 内容描述。</summary>
    internal View Content { get; }

    /// <inheritdoc />
    public override string DebugName => "PopupSurface";

    internal override IReadOnlyList<View> Children => _children;

    internal override EnvironmentValues ResolveEnvironment(ViewNode node, EnvironmentValues parent)
        => Anchor.IsMounted ? Anchor.Environment : parent;

    internal override void Measure(ViewNode node, LayoutConstraints constraints, LayoutContext context)
    {
        double width = 0;
        double height = 0;
        foreach (var child in node.ChildList)
        {
            // 必须在每轮真正访问内容 scope：缓存命中只会刷新该节点自身的 Environment、不访问子级，
            // 而 popup 内容的环境来自锚（局部 Theme/Font/Disabled）。surface 每轮已被强制重测，
            // 这里再强制访问 scope 才能把新环境传播到内容节点（内容子树自身仍走正常缓存）。
            context.MeasureFresh(child, constraints);
            width = Math.Max(width, child.DesiredSize.Width);
            height = Math.Max(height, child.DesiredSize.Height);
        }

        node.DesiredSize = constraints.Constrain(new Size(
            Math.Max(width, constraints.MinWidth),
            Math.Max(height, constraints.MinHeight)));
    }

    internal override void Arrange(ViewNode node, Rect bounds, LayoutContext context)
    {
        node.Frame = bounds;
        foreach (var child in node.ChildList)
        {
            context.Arrange(child, bounds);
        }
    }

    internal override Rect? GetChildClip(ViewNode node) => node.Frame;

    internal override void Emit(ViewNode node, DrawContext context)
    {
        if (node.Frame.IsEmpty)
        {
            return;
        }

        var restore = context.CurrentClip;
        if (!context.PushClip(node.Frame))
        {
            return;
        }

        try
        {
            foreach (var child in node.Children)
            {
                context.EmitChild(child);
            }
        }
        finally
        {
            context.PopClip(restore);
        }
    }
}

/// <summary>
/// popup 内容的内部 Component scope：仅用于给 portal 内容提供 Component 依赖归属
/// （<see cref="UiRuntime.RegisterBindingDependency"/> 沿父链寻找最近 Component）。
/// </summary>
/// <remarks>
/// 布局/绘制仍是单子项透传；内容描述保持原对象，节点身份与内部组件状态按原有协调规则保留。
/// </remarks>
internal sealed class PopupContentScope : Component
{
    internal PopupContentScope(View content)
    {
        ArgumentNullException.ThrowIfNull(content);
        Content = content;
    }

    /// <summary>原始 popup 内容描述（由 surface 负责 clip/环境）。</summary>
    internal View Content { get; }

    public override string DebugName => "PopupContent";

    protected override View Build() => Content;
}
