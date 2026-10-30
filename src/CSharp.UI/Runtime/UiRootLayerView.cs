namespace CSharp.UI;

/// <summary>
/// 运行时固定的根层描述：把用户根视图与 overlay host 放在同一层叠容器中。
/// </summary>
/// <remarks>
/// <para>
/// 根层只由 <see cref="UiRuntime"/> 构造一次并挂载一次（不 double-mount）：用户根视图经
/// <see cref="Reconciler.NormalizeRootView"/> 按既有根语义归一化（<c>ForEach</c>/多条目仍包一层隐式容器），
/// overlay host 固定为第二个子项。两者都按 ZStack 语义占用同一矩形，后者绘制与命中在上。
/// </para>
/// <para>
/// 命中纪律：<see cref="IsHitTestVisible"/> 恒为 true——<c>InputRouter</c> 沿该标记遍历，父级返回 false 会剪掉整棵子树；
/// 自身命中由显式 <see cref="HitTestSelf"/> 返回 false 拒绝（默认实现会命中自身 Frame，不能用）。
/// </para>
/// </remarks>
internal sealed class UiRootLayerView : View
{
    private readonly View[] _children;

    internal UiRootLayerView(View userRoot, OverlayHostView overlayHost)
    {
        ArgumentNullException.ThrowIfNull(overlayHost);
        UserRoot = Reconciler.NormalizeRootView(userRoot);
        OverlayHost = overlayHost;
        _children = [UserRoot, OverlayHost];
    }

    /// <summary>归一化后的用户根视图描述（首个布局子项）。</summary>
    internal View UserRoot { get; }

    /// <summary>overlay 宿主描述（第二个布局子项）。</summary>
    internal OverlayHostView OverlayHost { get; }

    /// <inheritdoc />
    public override string DebugName => "RootLayer";

    internal override IReadOnlyList<View> Children => _children;

    internal override void Measure(ViewNode node, LayoutConstraints constraints, LayoutContext context)
    {
        double width = 0;
        double height = 0;
        foreach (var child in node.ChildList)
        {
            // 主树先测量。overlay 宿主只在有活动 entry 时强制重测：popup 尺寸/锚环境可能在主树测量后
            // 变化，且宿主不是用户树失效的祖先，缓存复用会跳过 popup 的测量阶段。无 entry 时宿主
            // 尺寸恒为 0、子树为空，沿用既有测量缓存即可，不为关闭状态付每轮测量代价。
            if (ReferenceEquals(child.View, OverlayHost) && OverlayHost.HasActiveEntry)
            {
                context.MeasureFresh(child, constraints);
            }
            else
            {
                context.Measure(child, constraints);
            }

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

        // 先排列用户树，再排列 overlay 宿主：placement 需要锚的最终 Frame。
        foreach (var child in node.ChildList)
        {
            context.Arrange(child, bounds);
        }
    }

    // 父级高度可见：根层自身不接受命中，空白区行为与原单根布局一致。
    internal override bool IsHitTestVisible(ViewNode node) => true;

    internal override bool HitTestSelf(ViewNode node, Point position) => false;
}
