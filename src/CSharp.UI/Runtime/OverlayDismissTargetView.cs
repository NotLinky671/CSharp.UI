namespace CSharp.UI;

/// <summary>
/// 外点吞并期间的临时全屏命中目标：接管被吞并手势的 Down/Move/Up/Cancel，使下层用户树收不到任何事件。
/// </summary>
/// <remarks>
/// <para>
/// 由 <see cref="OverlayManager"/> 在检测到“外点按下”后挂到 overlay 宿主下，一直保持挂载到手势结束
/// （Up/Cancel、失活或目标失效）；期间即使 popup entry 已关闭也不卸载——关闭弹层不得把吞并目标一起清掉。
/// </para>
/// <para>
/// 不绘制任何内容。命中自身恒为 true，Frame 由 manager 在挂载时立即设为视口矩形（初始 Down 之前还没有
/// 经过一轮布局，空 Frame 会导致 HitTest 漏点）。滚轮不属于吞并范围，由 router 在命中时跳过本节点。
/// </para>
/// </remarks>
internal sealed class OverlayDismissTargetView : View
{
    /// <inheritdoc />
    public override string DebugName => "OverlayDismissTarget";

    internal override IReadOnlyList<View> Children => [];

    internal override void Measure(ViewNode node, LayoutConstraints constraints, LayoutContext context)
        => node.DesiredSize = Size.Zero;

    internal override void Arrange(ViewNode node, Rect bounds, LayoutContext context)
        => node.Frame = bounds;

    internal override bool IsHitTestVisible(ViewNode node) => true;

    internal override bool HitTestSelf(ViewNode node, Point position) => true;

    internal override bool WantsPointerCapture(ViewNode node, PointerEvent e) => true;

    internal override bool OnPointerDown(InputContext context, ViewNode node, PointerEvent e)
    {
        context.MarkHandled();
        return true;
    }

    internal override bool OnPointerMove(InputContext context, ViewNode node, PointerEvent e)
    {
        context.MarkHandled();
        return true;
    }

    internal override bool OnPointerUp(InputContext context, ViewNode node, PointerEvent e)
    {
        context.MarkHandled();
        return true;
    }

    internal override bool OnPointerCancel(InputContext context, ViewNode node)
    {
        context.MarkHandled();
        return true;
    }
}
