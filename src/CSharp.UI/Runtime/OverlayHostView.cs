namespace CSharp.UI;

/// <summary>
/// 根层中的 overlay 宿主：活动 popup 子树的父节点。
/// </summary>
/// <remarks>
/// <para>
/// 子项由 <see cref="OverlayManager"/> 在安全阶段显式协调（<see cref="Children"/> 恒为空，
/// 不依赖描述更新自动挂载）。无活动 entry 且无吞并中的手势时整支不可见，
/// 下方用户树原样接收 wheel/hover。
/// </para>
/// <para>
/// 命中纪律：<see cref="IsHitTestVisible"/> 由 manager 条件化（有 entry 或吞并手势期）；
/// 自身命中恒由 <see cref="HitTestSelf"/> 拒绝——popup 子节点正常命中，
/// 宿主 Frame 本身不制造命中死区。
/// </para>
/// </remarks>
internal sealed class OverlayHostView : View
{
    private readonly OverlayManager _manager;

    internal OverlayHostView(OverlayManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        _manager = manager;
    }

    /// <inheritdoc />
    public override string DebugName => "OverlayHost";

    internal override IReadOnlyList<View> Children => [];

    /// <summary>是否存在活动 popup entry（有 entry 时宿主必须每轮重测）。</summary>
    internal bool HasActiveEntry => _manager.HasActiveEntry;

    internal override bool IsHitTestVisible(ViewNode node) => _manager.IsHostInteractive;

    internal override bool HitTestSelf(ViewNode node, Point position) => false;

    internal override void Measure(ViewNode node, LayoutConstraints constraints, LayoutContext context)
    {
        // 宿主不参与主布局尺寸：无 entry 时 0；有 entry 时只测量 popup 子树（见 manager）。
        node.DesiredSize = constraints.Constrain(Size.Zero);
        _manager.MeasureEntry(context);
    }

    internal override void Arrange(ViewNode node, Rect bounds, LayoutContext context)
    {
        node.Frame = bounds;
        _manager.ArrangeEntry(context);
    }
}
