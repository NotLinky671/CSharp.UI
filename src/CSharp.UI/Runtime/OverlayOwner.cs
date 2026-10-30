namespace CSharp.UI;

/// <summary>
/// owner（公开 <c>Popup</c>、<c>Select</c>）与 overlay manager 之间的最小契约：
/// 每轮安全阶段由 manager 拉取开合意图，内部关闭时回调 owner 同步自身状态。
/// </summary>
/// <remarks>
/// <para>
/// 拉取必须只读且轻量：读当前节点描述与绑定（<c>Binding.Read(node)</c> 负责登记来源状态依赖），
/// 不创建视图、不 reconcile、不写状态。状态机与挂载/卸载全部由 manager 在安全阶段完成。
/// </para>
/// <para>
/// 回调次数纪律：<see cref="OverlayCloseReason.OwnerRequest"/> 不回写（owner 已经写 false）；
/// 其余内部关闭每个 entry 恰好回调一次，由 owner 把公开状态写回一次 false（或无操作）。
/// </para>
/// </remarks>
internal interface IOverlayOwner
{
    /// <summary>owner 当前是否有打开意图；为 true 时给出本次打开所需的完整请求。</summary>
    /// <param name="node">owner 自己的节点（描述被重建时同节点、新视图实例）。</param>
    /// <param name="request">打开请求（锚节点、内容描述、边距、高度上限、方向偏好）。</param>
    bool TryGetOpenRequest(ViewNode node, out OverlayOpenRequest request);

    /// <summary>manager 侧内部关闭（非 owner 主动）：owner 同步自己的状态。</summary>
    void OnOverlayClosed(ViewNode node, OverlayCloseReason reason);
}

/// <summary>owner 请求打开时所需的最小参数集合（每轮从当前描述读取）。</summary>
internal readonly record struct OverlayOpenRequest(
    ViewNode Anchor,
    View Content,
    double Margin,
    double MaxHeight,
    PopupPlacement Preference);

/// <summary>overlay 关闭原因：决定是否回写 owner 状态、以及 P-04 的焦点恢复行为。</summary>
internal enum OverlayCloseReason
{
    /// <summary>owner 自己把公开状态置为关闭（manager 不回写）。</summary>
    OwnerRequest,

    /// <summary>外点（P-04）。</summary>
    OutsidePress,

    /// <summary>Esc（P-04）。</summary>
    Escape,

    /// <summary>锚不可用：锚卸载、滚出、禁用、无可用几何、窗口失活之外的资格门失败。</summary>
    AnchorIneligible,

    /// <summary>窗口失活。</summary>
    WindowDeactivated,

    /// <summary>另一个 owner 的打开请求挤占。</summary>
    Displaced,

    /// <summary>owner/popup 子树被卸载。</summary>
    Unmounted,
}
