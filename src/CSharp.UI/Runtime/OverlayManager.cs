namespace CSharp.UI;

/// <summary>单活动 overlay entry：锚、popup 子树与环境/焦点快照。</summary>
/// <remarks>
/// <see cref="AnchorNode"/> 是锚的实际 view 子树根节点（<c>Popup</c> 的 Anchor 子节点、
/// <c>Select</c> 的 trigger 节点）：定位矩形与锚子树豁免共用该节点的 Frame。
/// <see cref="PopupNode"/> 是 <see cref="PopupSurfaceView"/> 的节点（挂载后才有值）。
/// </remarks>
internal sealed record ActiveOverlayEntry(
    ViewNode AnchorNode,
    ViewNode? PopupNode,
    ViewNode? SavedFocus,
    double MaxHeight,
    ViewNode? Owner,
    double Margin,
    PopupPlacement Preference);

/// <summary>
/// 每个 owner（<c>Popup</c>/<c>Select</c>）的 open-request 观察状态：安全阶段每轮拉取时更新。
/// </summary>
/// <remarks>
/// <see cref="Requested"/> 是上一次观察到的 open-request（binding 值 / Select 内部 flag），用于识别
/// false→true 边沿。<see cref="AwaitingOpenEdge"/> 在内部关闭后置位：owner 的 binding 即使恒为 true
/// 也不能重开，必须观察到新的 false→true 边沿才会清除并允许再次打开。挂载/卸载只影响该节点的状态
/// （卸载即移除，新节点是独立状态），已打开的 entry 不会再被重复 <c>Show</c>。
/// </remarks>
internal sealed class OverlayOwnerState
{
    /// <summary>上一次观察到的 open-request（binding 值 / Select 的 open flag）。</summary>
    internal bool Requested { get; set; }

    /// <summary>已内部关闭，等待 owner 的 false→true 边沿后才允许再次打开。</summary>
    internal bool AwaitingOpenEdge { get; set; }
}

/// <summary>
/// 单窗口 overlay 管理器：单活动 entry 的挂载/关闭规则/弹层测量与定位。
/// </summary>
/// <remarks>
/// <para>
/// 打开/关闭请求只登记，由 <see cref="UiRuntime.ProcessPendingWork"/> 在布局之后的安全阶段消费
/// （Measure/Arrange/Emit 内不 reconcile、不卸载、不写 binding）；消费改变子树时由运行时再布局到稳定。
/// </para>
/// <para>
/// 消费纪律：同一安全阶段内的多次请求按“最后一次请求”收敛（打开取代更早的关闭，反之亦然）；
/// 关闭请求带挂载代际，只对请求时存在的 entry 生效——请求之后发生的新挂载/关闭会让它过时丢弃，
/// 因此旧 popup 内容在 OnUnmount 中的关闭不会误关随后挂载的新 entry。每次实际消费都计入单轮预算
/// （<see cref="MaxConsumptionRoundsPerPass"/>）：popup 内容在挂载/卸载回调中反复请求开合会在预算耗尽时
/// 报告诊断并抛出，由运行时丢弃挂起请求后恢复，不做无界 drain。
/// </para>
/// <para>
/// 资格门（每轮消费时校验）：锚已挂载、未禁用、<c>anchorVisible</c> 非空、<c>popupBounds</c> 非空、窗口激活。
/// 门不满足时已经打开的 entry 关闭；新的打开请求被拒绝（不产 entry）。资格判定只读 <c>anchorVisible</c>，
/// <c>popupBounds</c> 是独立矩形（不与锚祖先 clip 相交）。
/// </para>
/// <para>
/// owner 驱动（<see cref="IOverlayOwner"/>）：<c>Popup</c>/<c>Select</c> 在 <c>OnMount</c> 注册，安全阶段按
/// 注册顺序（主树声明序）拉取开合意图——只读当前描述与绑定，不创建视图；owner 描述参数/content 变化
/// 延后到消费阶段，仅引用/值真的不同时才 reconcile 或失效一次（不产生空闲帧的重复工作）。
/// 内部关闭按 <see cref="OverlayCloseReason"/> 通知 owner 写回一次；<see cref="OverlayCloseReason.OwnerRequest"/>
/// 不回写。已有 entry 的 owner 再次观察 true 不会重复 Show；未实现 <see cref="IOverlayOwner"/> 的 owner
/// （直接 <see cref="Show"/> 的 internal 入口）仍只记录 await-edge 骨架。
/// </para>
/// </remarks>
internal sealed class OverlayManager
{
    private const double DefaultMargin = 4;

    /// <summary>
    /// 单轮 <see cref="UiRuntime.ProcessPendingWork"/> 内 overlay 安全阶段的最大消费次数（与响应式事务预算同一纪律）。
    /// </summary>
    /// <remarks>
    /// 每次实际消费都会改变子树并触发重新布局。popup 内容的 OnMount/OnUnmount 反复请求打开/关闭会让
    /// “消费→布局→再消费”无限循环；超过上限即报告运行时诊断并抛出异常，由运行时丢弃挂起请求后恢复。
    /// 正常收敛（打开一次、关闭一次、挤占一次）远低于该上限。
    /// </remarks>
    internal const int MaxConsumptionRoundsPerPass = 16;

    private readonly UiRuntime _runtime;
    private readonly OverlayHostView _hostView;
    private readonly Dictionary<ViewNode, OverlayOwnerState> _ownerStates = new(ReferenceEqualityComparer.Instance);

    // owner 注册表（注册顺序 = 主树声明序）：只在 mount/unmount 时增删，拉取循环不增删。
    private readonly List<ViewNode> _ownerOrder = [];
    private readonly HashSet<ViewNode> _ownerRegistered = new(ReferenceEqualityComparer.Instance);

    private ViewNode? _hostNode;
    private ActiveOverlayEntry? _active;
    private PendingShow? _pendingShow;
    private bool _pendingClose;
    private OverlayCloseReason _pendingCloseReason = OverlayCloseReason.OwnerRequest;
    private OwnerRefresh? _pendingRefresh;
    private bool _popupMeasureInvalid;

    // overlay 宿主子项：surface（活动 entry）与 dismiss-target（外点吞并手势期）。
    // 两者用稳定键区分，增删任意一方都不会误 remount 另一方（surface 的 content 状态必须保留）。
    private const string SurfaceChildKey = "overlay:surface";
    private const string DismissChildKey = "overlay:dismiss";

    private PopupSurfaceView? _surfaceView;
    private OverlayDismissTargetView? _dismissTargetView;
    private ViewNode? _dismissTargetNode;
    private bool _swallowGestureActive;

    // 布局期发现无有效定位空间（bounds 空/锚空/算法返回 null）：Arrange 只排空矩形，
    // 真正的 unmount 留给安全阶段消费，避免在 Measure/Arrange 内 reconcile。
    private bool _geometryInvalid;

    // 挂载/卸载代际：关闭请求只对“请求时存在的 entry”生效。请求后若已发生新的挂载或关闭，
    // 该请求已过时（否则旧 popup 内容在卸载中的 Close 会误关刚挂载的新 entry）。
    private long _mountEpoch;
    private long _closeEpoch;

    // 挂载协调期标记：popup 内容节点在 OnMount 时尚未链接到父节点（协调器先 OnMount 再挂父），
    // 期间出现未挂接的锚即“内容自身请求嵌套打开”。
    private bool _mounting;

    private long _budgetPassId;
    private int _roundsInPass;

    internal OverlayManager(UiRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _runtime = runtime;
        _hostView = new OverlayHostView(this);
    }

    private readonly record struct PendingShow(
        ViewNode Anchor,
        View Content,
        double MaxHeight,
        ViewNode? Owner,
        double Margin,
        PopupPlacement Preference);

    /// <summary>拉取阶段发现 owner 描述参数变化：延后到消费阶段应用（不在拉取循环内 reconcile）。</summary>
    private readonly record struct OwnerRefresh(ViewNode Owner, OverlayOpenRequest Request);

    /// <summary>overlay 宿主的固定描述（作为根层第二个子项挂载一次）。</summary>
    internal OverlayHostView HostView => _hostView;

    /// <summary>是否存在活动 entry。</summary>
    internal bool HasActiveEntry => _active is not null;

    /// <summary>当前活动 entry；无 entry 时为 null。</summary>
    internal ActiveOverlayEntry? ActiveEntry => _active;

    /// <summary>宿主是否参与命中测试：有 entry 或处于外点吞并手势期。</summary>
    internal bool IsHostInteractive => _active is not null || _swallowGestureActive;

    /// <summary>是否处于外点吞并手势期（期间宿主保持可命中以承载 dismiss-target）。</summary>
    internal bool SwallowGestureActive => _swallowGestureActive;

    /// <summary>当前吞并手势的 dismiss-target 节点（无吞并时为 null）。</summary>
    internal ViewNode? DismissTargetNode => _dismissTargetNode;

    /// <summary>节点是否属于吞并手势的 dismiss-target（滚轮命中需跳过它）。</summary>
    internal bool IsDismissTarget(ViewNode node)
        => _dismissTargetNode is { } target && IsSelfOrDescendant(target, node);

    /// <summary>打开请求：登记后由安全阶段消费。嵌套请求（锚位于当前 popup 子树内）直接拒绝。</summary>
    /// <param name="anchor">锚的实际 view 子树根节点（定位与锚豁免共用其 Frame）。</param>
    /// <param name="content">popup 内容描述。</param>
    /// <param name="maxHeight">popup 高度上限（正数或 +∞）。</param>
    /// <param name="owner">可选 owner 节点（<c>Popup</c>/<c>Select</c>），用于 open-request 状态记录。</param>
    /// <param name="margin">popup 与视口边缘的间距（有限、非负）。</param>
    /// <param name="preference">首选方向；Auto 可翻转，Above/Below 强制该侧并按可用高度截断。</param>
    /// <returns>请求是否被接受（嵌套请求返回 false，外层保持打开）。</returns>
    internal bool Show(
        ViewNode anchor,
        View content,
        double maxHeight = double.PositiveInfinity,
        ViewNode? owner = null,
        double margin = DefaultMargin,
        PopupPlacement preference = PopupPlacement.Auto)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(content);
        if (!double.IsFinite(maxHeight) && maxHeight != double.PositiveInfinity)
        {
            throw new ArgumentOutOfRangeException(nameof(maxHeight), "popup 高度上限必须是正数或正无穷。");
        }

        if (maxHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxHeight), "popup 高度上限必须为正。");
        }

        if (!double.IsFinite(margin) || margin < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(margin), "popup 边距必须是有限非负数。");
        }

        if (preference is not (PopupPlacement.Auto or PopupPlacement.Above or PopupPlacement.Below))
        {
            throw new ArgumentOutOfRangeException(nameof(preference));
        }

        if (IsNestedAnchor(anchor))
        {
            _runtime.ReportDiagnostic(
                $"嵌套 popup 不受支持：锚 {anchor.ShortDebugPath()} 位于当前 popup 子树内；打开请求已拒绝，外层 popup 保持打开。");
            return false;
        }

        // 同一安全阶段内的请求按“最后一次请求”收敛：打开取代更早的关闭意图。
        _pendingShow = new PendingShow(anchor, content, maxHeight, owner, margin, preference);
        _pendingClose = false;
        if (owner is not null)
        {
            TrackOwner(owner);
        }

        _runtime.InvalidateRender();
        return true;
    }

    /// <summary>关闭当前 entry（若有）；在安全阶段消费。</summary>
    /// <param name="reason">
    /// 关闭原因：<see cref="OverlayCloseReason.OwnerRequest"/> 表示 owner 已自己写 false（不回写），
    /// 其余原因会在关闭时通知 owner 写回一次。
    /// </param>
    /// <remarks>
    /// 与打开一样只登记意图：同一安全阶段内后到的关闭取代更早的打开（最后一次请求胜），且只作用于
    /// 请求时存在的 entry——请求之后已经发生的挂载/关闭会让它成为过时请求被丢弃。
    /// </remarks>
    internal void Close(OverlayCloseReason reason = OverlayCloseReason.OwnerRequest)
    {
        _pendingShow = null;
        _pendingClose = true;
        _pendingCloseReason = reason;
        _closeEpoch = _mountEpoch;
        _runtime.InvalidateRender();
    }

    /// <summary>关闭全部 entry（单槽，语义同 <see cref="Close"/>）。</summary>
    internal void CloseAll(OverlayCloseReason reason = OverlayCloseReason.OwnerRequest) => Close(reason);

    /// <summary>窗口失活通知点：关闭当前 entry（owner 同步写回一次，不改主树焦点），并结束进行中的吞并手势。</summary>
    internal void OnWindowDeactivated()
    {
        Close(OverlayCloseReason.WindowDeactivated);
        EndSwallowGesture();
    }

    /// <summary>owner 注册（<c>Popup</c>/<c>Select</c> 在 <c>OnMount</c> 调用）：登记后由安全阶段拉取。</summary>
    /// <remarks>
    /// 注册只记录节点，不触发挂载：初次根层挂载时 overlay host 尚未 <see cref="AttachHost"/>，
    /// 真正的打开发生在随后一轮布局之后的安全阶段。
    /// </remarks>
    internal void RegisterOwner(ViewNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!_ownerRegistered.Add(node))
        {
            return;
        }

        TrackOwner(node);
        _ownerOrder.Add(node);
    }

    /// <summary>运行时异常恢复：丢弃尚未消费的打开/关闭请求（已挂载的 entry 保持由下一轮资格校验处理）。</summary>
    internal void DropPendingRequests()
    {
        _pendingShow = null;
        _pendingClose = false;
        _pendingCloseReason = OverlayCloseReason.OwnerRequest;
        _pendingRefresh = null;
        _popupMeasureInvalid = false;
        _roundsInPass = 0;
    }

    /// <summary>节点卸载钩子：锚/popup 子树被外部卸载时请求关闭；owner 状态随节点移除。</summary>
    internal void OnNodeUnmounting(ViewNode node)
    {
        if (_active is { } active)
        {
            var inPopup = active.PopupNode is { } popup && IsSelfOrDescendant(node, popup);
            if (inPopup || IsSelfOrDescendant(node, active.AnchorNode))
            {
                // owner 一起卸载（含其祖先子树被卸载）→ Unmounted；仅锚失效 → AnchorIneligible。
                var ownerUnmounting = active.Owner is { } owner && (owner.IsUnmounting || ReferenceEquals(owner, node));
                Close(ownerUnmounting ? OverlayCloseReason.Unmounted : OverlayCloseReason.AnchorIneligible);
            }
        }

        _ownerStates.Remove(node);
        if (_ownerRegistered.Remove(node))
        {
            _ownerOrder.Remove(node);
        }
    }

    /// <summary>owner 是否在等待 false→true 边沿（内部关闭过）。</summary>
    internal bool IsOwnerAwaitingOpenEdge(ViewNode owner)
        => owner is not null && _ownerStates.TryGetValue(owner, out var state) && state.AwaitingOpenEdge;

    /// <summary>
    /// 安全阶段消费：先拉取 owner 的开合意图，再关闭不再满足资格门的 entry、执行挂起的关闭、最后执行挂起的打开。
    /// </summary>
    /// <param name="passId">当前 <see cref="UiRuntime.ProcessPendingWork"/> 轮次标识（消费预算按轮计）。</param>
    /// <returns>是否改变了节点树（调用方需要重新布局到稳定）。</returns>
    /// <exception cref="InvalidOperationException">
    /// 单轮消费次数超过 <see cref="MaxConsumptionRoundsPerPass"/>：popup 内容在挂载/卸载回调中反复请求
    /// 打开或关闭，安全阶段无法收敛。异常前已报告运行时诊断；运行时随后丢弃挂起请求，已挂载的 entry 保持稳定。
    /// </exception>
    internal bool ConsumePending(long passId)
    {
        if (_budgetPassId != passId)
        {
            _budgetPassId = passId;
            _roundsInPass = 0;
        }

        // 拉取只登记意图/延后刷新，不改变节点树；owner 尚未注册或描述未就绪时自然跳过。
        // owner 拉取可能在安全阶段做必要的失效（Select 标签宽度/ensure-visible）：用布局序号变化
        // 作为“需要再布局”的信号，保证同一轮 ProcessPendingWork 内收敛而不是拖到下一帧。
        var layoutSerial = _runtime.LayoutSerial;
        PollOwners();
        var swallowEnded = ValidateSwallowGesture();
        var ownerInvalidated = _runtime.LayoutSerial != layoutSerial;

        // 只有“完全没有工作”才提前返回：owner 在拉取阶段请求的失效同样是一次真实进度，
        // 必须经过下面的单轮消费预算，不能被 early return 绕过 P-01 的防循环守护。
        if (!HasPendingWork && !swallowEnded && !ownerInvalidated)
        {
            return false;
        }

        if (++_roundsInPass > MaxConsumptionRoundsPerPass)
        {
            var message =
                $"检测到 overlay 循环：单轮更新内 overlay 安全阶段已消费 {_roundsInPass - 1} 次" +
                $"（上限 {MaxConsumptionRoundsPerPass}）。popup 内容的 OnMount/OnUnmount 反复请求打开或关闭、" +
                "或 owner 每轮都请求布局失效（例如绑定 getter 每次都返回不同值）会无限切换 entry/布局；" +
                "请让弹层打开状态与 owner 值收敛（例如只在用户交互中请求打开，或让资格变化有序停止）。";
            _runtime.ReportDiagnostic(message);
            throw new InvalidOperationException(message);
        }

        var changed = swallowEnded | ApplyOwnerRefresh();

        if (_popupMeasureInvalid)
        {
            _popupMeasureInvalid = false;
            if (_active is { PopupNode: { IsMounted: true } popupNode })
            {
                // 在这里失效才会成为本轮的真实进度（Arrange 内失效会被 RunLayout 末尾清零）。
                popupNode.InvalidateLayout();
                changed = true;
            }
        }

        if (_geometryInvalid)
        {
            _geometryInvalid = false;
            if (_active is not null)
            {
                CloseEntry(OverlayCloseReason.AnchorIneligible);
                changed = true;
            }
        }

        if (_pendingClose)
        {
            _pendingClose = false;
            var reason = _pendingCloseReason;
            _pendingCloseReason = OverlayCloseReason.OwnerRequest;

            // 过时请求（请求之后已发生新的挂载/关闭）丢弃：旧内容卸载中的关闭不作用于新 entry。
            if (_active is not null && _closeEpoch == _mountEpoch)
            {
                CloseEntry(reason);
                changed = true;
            }
        }

        if (_active is { } active && !IsEntryEligible(active))
        {
            CloseEntry(OverlayCloseReason.AnchorIneligible);
            changed = true;
        }

        if (_pendingShow is { } show)
        {
            _pendingShow = null;
            if (IsNestedAnchor(show.Anchor))
            {
                // 请求登记之后 popup 子树才挂接完成（内容 OnMount 期）——此处兜底拒绝，外层保持打开。
                _runtime.ReportDiagnostic(
                    $"嵌套 popup 不受支持：锚 {show.Anchor.ShortDebugPath()} 位于当前 popup 子树内；打开请求已拒绝，外层 popup 保持打开。");
                if (show.Owner is { IsMounted: true } rejectedOwner)
                {
                    TrackOwner(rejectedOwner).AwaitingOpenEdge = true;
                }
            }
            else if (IsShowEligible(show))
            {
                if (_active is not null)
                {
                    CloseEntry(OverlayCloseReason.Displaced);
                    changed = true;
                }

                // 挤占关闭可能连带卸载/禁用目标锚：挂载前重新校验资格，绝不把已卸载锚的 popup 挂上去。
                if (IsShowEligible(show) && Mount(show))
                {
                    changed = true;
                }
            }
        }

        return changed || ownerInvalidated || _runtime.LayoutSerial != layoutSerial;
    }

    /// <summary>拉取每个已注册 owner 的开合意图（注册顺序 = 主树声明序，后者胜）。</summary>
    /// <remarks>
    /// 只读 + 登记 pending：<see cref="IOverlayOwner.TryGetOpenRequest"/> 不得创建视图或 reconcile；
    /// 描述参数变化的 reconcile 延后到 <see cref="ApplyOwnerRefresh"/>，因此本循环内注册表不会被修改。
    /// </remarks>
    private void PollOwners()
    {
        for (var i = 0; i < _ownerOrder.Count; i++)
        {
            var node = _ownerOrder[i];
            if (!node.IsMounted || !_ownerStates.TryGetValue(node, out var state))
            {
                continue;
            }

            if (node.View is not IOverlayOwner owner)
            {
                continue;
            }

            PollOwner(node, state, owner);
        }
    }

    private void PollOwner(ViewNode node, OverlayOwnerState state, IOverlayOwner owner)
    {
        var requested = owner.TryGetOpenRequest(node, out var request);
        var rising = requested && !state.Requested;
        state.Requested = requested;

        if (!requested)
        {
            if (IsEntryOwnedBy(node))
            {
                RequestClose(node, OverlayCloseReason.OwnerRequest);
            }

            return;
        }

        if (IsEntryOwnedBy(node))
        {
            // 只有描述引用/参数真的变化时才登记刷新：稳定打开状态不产生任何安全阶段工作。
            if (NeedsRefresh(node, request))
            {
                _pendingRefresh = new OwnerRefresh(node, request);
            }

            return;
        }

        if (state.AwaitingOpenEdge)
        {
            // binding / flag 恒为打开：保持关、不写、不打开，只有新的 false→true 边沿才允许重开。
            if (!rising)
            {
                return;
            }

            state.AwaitingOpenEdge = false;
        }

        RequestShow(node, state, request);
    }

    private void RequestShow(ViewNode owner, OverlayOwnerState state, in OverlayOpenRequest request)
    {
        if (IsNestedAnchor(request.Anchor))
        {
            // 嵌套打开不受支持：拒绝并只诊断一次（置 await-edge 抑制每轮重复请求），外层保持打开。
            _runtime.ReportDiagnostic(
                $"嵌套 popup 不受支持：锚 {request.Anchor.ShortDebugPath()} 位于当前 popup 子树内；打开请求已拒绝，外层 popup 保持打开。");
            state.AwaitingOpenEdge = true;
            return;
        }

        if (_pendingShow is { Owner: { } pendingOwner } && !ReferenceEquals(pendingOwner, owner))
        {
            // 同一安全阶段内后注册的 owner 胜：前一个请求被挤占，按内部关闭同步一次（写 false + 待边沿）。
            NotifyOwnerClosed(pendingOwner, OverlayCloseReason.Displaced);
            if (pendingOwner.IsMounted)
            {
                TrackOwner(pendingOwner).AwaitingOpenEdge = true;
            }
        }

        _pendingShow = new PendingShow(
            request.Anchor,
            request.Content,
            request.MaxHeight,
            owner,
            request.Margin,
            request.Preference);
        _pendingClose = false;
    }

    private void RequestClose(ViewNode owner, OverlayCloseReason reason)
    {
        if (_active is not { } entry || !ReferenceEquals(entry.Owner, owner))
        {
            return;
        }

        _pendingShow = null;
        _pendingClose = true;
        _pendingCloseReason = reason;
        _closeEpoch = _mountEpoch;
    }

    /// <summary>应用拉取阶段记录的 owner 描述变化：仅引用/参数真的不同时才 reconcile 或失效一次。</summary>
    private bool ApplyOwnerRefresh()
    {
        if (_pendingRefresh is not { } refresh)
        {
            return false;
        }

        _pendingRefresh = null;

        if (_active is not { } entry
            || !ReferenceEquals(entry.Owner, refresh.Owner)
            || entry.PopupNode is not { IsMounted: true } popupNode)
        {
            return false;
        }

        var request = refresh.Request;
        var surface = popupNode.View as PopupSurfaceView;
        var anchorChanged = !ReferenceEquals(entry.AnchorNode, request.Anchor);
        var contentChanged = surface is null || !ReferenceEquals(surface.Content, request.Content);

        var updated = entry with
        {
            AnchorNode = request.Anchor,
            Margin = request.Margin,
            MaxHeight = request.MaxHeight,
            Preference = request.Preference,
        };

        if (anchorChanged || contentChanged)
        {
            // 替换 surface 节点自身的描述（不是它的子项）：surface 节点身份保持，
            // 新描述的内容子树按 key/类型 reconcile（UseState/InteractionState 不丢），
            // 不是 unmount/remount 整个 overlay 子树。
            var refreshed = new PopupSurfaceView(request.Anchor, request.Content);
            Reconciler.UpdateNode(
                popupNode,
                new ChildDescriptor(refreshed, SurfaceChildKey, true));
            _surfaceView = refreshed;
            _active = updated;
            return true;
        }

        if (entry.Margin == request.Margin
            && entry.MaxHeight == request.MaxHeight
            && entry.Preference == request.Preference)
        {
            return false;
        }

        _active = updated;
        _hostNode?.InvalidateLayout();
        return true;
    }

    private bool IsEntryOwnedBy(ViewNode owner)
        => _active is { Owner: { } activeOwner } && ReferenceEquals(activeOwner, owner);

    /// <summary>活动 entry 是否真的需要按 owner 当前描述刷新（引用或参数变化）。</summary>
    private bool NeedsRefresh(ViewNode owner, in OverlayOpenRequest request)
    {
        if (_active is not { } entry
            || !ReferenceEquals(entry.Owner, owner)
            || entry.PopupNode is not { } popupNode)
        {
            return false;
        }

        if (!ReferenceEquals(entry.AnchorNode, request.Anchor)
            || entry.Margin != request.Margin
            || entry.MaxHeight != request.MaxHeight
            || entry.Preference != request.Preference)
        {
            return true;
        }

        return popupNode.View is not PopupSurfaceView surface
               || !ReferenceEquals(surface.Content, request.Content);
    }

    private void NotifyOwnerClosed(ViewNode owner, OverlayCloseReason reason)
    {
        if (owner.View is IOverlayOwner view)
        {
            view.OnOverlayClosed(owner, reason);
        }
    }

    /// <summary>是否存在需要在安全阶段处理的工作（挂起请求、延后的 owner 刷新或不再满足资格门的 entry）。</summary>
    private bool HasPendingWork
        => _pendingShow is not null
           || _pendingClose
           || _popupMeasureInvalid
           || _pendingRefresh is not null
           || _geometryInvalid
           || (_active is { } active && !IsEntryEligible(active));

    /// <summary>overlay 测量阶段：按 <c>popupBounds</c> 约束测量活动 popup 子树（锚环境变化时强制重测）。</summary>
    internal void MeasureEntry(LayoutContext context)
    {
        if (_active is not { } entry || entry.PopupNode is not { IsMounted: true } surface)
        {
            return;
        }

        var bounds = PopupBounds(entry.Margin);
        if (bounds.IsEmpty)
        {
            return;
        }

        var constraints = LayoutConstraints.Loose(bounds.Width, bounds.Height);

        // 锚的环境（Theme/Disabled/Font）每轮由主树测量更新；表面节点缓存了旧环境时，
        // 即使约束未变也必须重新测量，才能把新环境传播给 popup 内容。
        if (!Equals(surface.Environment, entry.AnchorNode.Environment))
        {
            context.MeasureFresh(surface, constraints);
        }
        else
        {
            context.Measure(surface, constraints);
        }
    }

    /// <summary>overlay 排列阶段（主树已排列）：placement 定位并排列活动 popup 子树。</summary>
    internal void ArrangeEntry(LayoutContext context)
    {
        if (_active is not { } entry || entry.PopupNode is not { IsMounted: true } surface)
        {
            return;
        }

        var bounds = PopupBounds(entry.Margin);
        var anchorFrame = entry.AnchorNode.Frame;
        if (bounds.IsEmpty || anchorFrame.IsEmpty)
        {
            context.Arrange(surface, Rect.Empty);
            _geometryInvalid = true;
            return;
        }

        var desired = surface.DesiredSize;
        var resolved = PopupPlacementCalculator.Resolve(
            anchorFrame,
            desired,
            bounds,
            entry.Preference,
            entry.MaxHeight);

        if (resolved is not { } frame)
        {
            // 无有效空间：只排空矩形，关闭意图由安全阶段消费（不在 Arrange 内 unmount）。
            context.Arrange(surface, Rect.Empty);
            _geometryInvalid = true;
            return;
        }

        // 阶段 3：最终尺寸与已测 DesiredSize 不同时，最多一次按 (w,h) 上限重测，
        // 让内部 ScrollView 拿到真实有限高（thumb/可视区正确）。
        if (Math.Abs(frame.Width - desired.Width) > 0.0001 || Math.Abs(frame.Height - desired.Height) > 0.0001)
        {
            context.MeasureFresh(surface, LayoutConstraints.Loose(frame.Width, frame.Height));
        }

        _geometryInvalid = false;
        context.Arrange(surface, frame);
    }

    private bool Mount(PendingShow show)
    {
        if (_hostNode is not { IsMounted: true } host)
        {
            return false;
        }

        var surface = new PopupSurfaceView(show.Anchor, show.Content);

        // 挂载开始即推进代际：内容 OnMount 中的关闭请求指向本次 entry（代际匹配），
        // 而更早的挂载/关闭请求会自然过时。
        _mountEpoch++;
        _mounting = true;
        try
        {
            _surfaceView = surface;
            ReconcileHostChildren();
        }
        finally
        {
            _mounting = false;
        }

        var surfaceNode = FindHostChild(surface);
        if (surfaceNode is null)
        {
            _surfaceView = null;
            return false;
        }

        _active = new ActiveOverlayEntry(
            show.Anchor,
            surfaceNode,
            _runtime.FocusedNode,
            show.MaxHeight,
            show.Owner,
            show.Margin,
            show.Preference);
        _geometryInvalid = false;
        return true;
    }

    private void CloseEntry(OverlayCloseReason reason)
    {
        var entry = _active;
        _active = null;
        _geometryInvalid = false;

        // 关闭期间到达的关闭请求由本次关闭满足（代际失配），不会误关随后挂载的新 entry。
        _mountEpoch++;

        if (entry?.Owner is { } owner)
        {
            // owner 主动关闭不回写；其余内部关闭恰好通知一次（owner 写回 false 或无操作）。
            if (reason != OverlayCloseReason.OwnerRequest)
            {
                NotifyOwnerClosed(owner, reason);
            }

            if (owner.IsMounted)
            {
                TrackOwner(owner).AwaitingOpenEdge = true;
            }
        }

        _surfaceView = null;

        // 只移除 surface；吞并手势期的 dismiss-target 必须保持挂载到 Up/Cancel（关闭弹层不等于结束吞并）。
        ReconcileHostChildren();
    }

    private bool IsEntryEligible(ActiveOverlayEntry entry)
        => entry.PopupNode is { IsMounted: true }
           && IsAnchorEligible(entry.AnchorNode, entry.Margin);

    private bool IsShowEligible(PendingShow show)
        => IsAnchorEligible(show.Anchor, show.Margin);

    private bool IsAnchorEligible(ViewNode anchor, double margin)
    {
        if (!anchor.IsMounted || anchor.IsEffectivelyDisabled || !_runtime.IsWindowActive)
        {
            return false;
        }

        return !PopupBounds(margin).IsEmpty && IsAnchorVisible(anchor);
    }

    /// <summary>锚可见资格：锚 Frame ∩ 视口 ∩ 各祖先 <see cref="View.GetChildClip"/>；只用于关/开判定。</summary>
    private bool IsAnchorVisible(ViewNode anchor)
    {
        var viewport = _runtime.ViewportSize;
        var rect = anchor.Frame.Intersect(new Rect(0, 0, viewport.Width, viewport.Height));
        for (var current = anchor.Parent; current is not null && !rect.IsEmpty; current = current.Parent)
        {
            if (current.View.GetChildClip(current) is { } clip)
            {
                rect = rect.Intersect(clip);
            }
        }

        return !rect.IsEmpty;
    }

    /// <summary>popup 可用边界：视口内缩 Margin（不与锚祖先 clip 相交）。</summary>
    private Rect PopupBounds(double margin)
    {
        var viewport = _runtime.ViewportSize;
        return new Rect(
            margin,
            margin,
            Math.Max(0, viewport.Width - (2 * margin)),
            Math.Max(0, viewport.Height - (2 * margin)));
    }

    private OverlayOwnerState TrackOwner(ViewNode owner)
    {
        if (!_ownerStates.TryGetValue(owner, out var state))
        {
            state = new OverlayOwnerState();
            _ownerStates.Add(owner, state);
        }

        return state;
    }

    /// <summary>
    /// 外点按下判定（Down 分发前调用）：命中在 popup 子树内或当前锚子树内 → 正常分发；
    /// 否则（且 owner 允许）登记 OutsidePress 关闭并挂载全屏 dismiss-target，由它吞并整个手势。
    /// </summary>
    /// <param name="hit">本次 Down 在**当前**结构下的命中节点（可为 null：空白外点）。</param>
    /// <returns>是否进入吞并：调用方随后按新结构重新命中并分发，事件只落在 dismiss-target 上。</returns>
    internal bool TryBeginOutsidePress(ViewNode? hit)
    {
        if (_swallowGestureActive || _active is not { } entry || !ClosesOnOutsidePress(entry))
        {
            return false;
        }

        if (IsInPopup(entry, hit) || IsInAnchor(entry, hit))
        {
            return false;
        }

        // 关闭请求先登记（安全阶段消费，写回 false 恰好一次），但 entry 仍挂载到消费时；
        // dismiss-target 立即挂载：本次 Down 就必须有可命中的有限 Frame。
        Close(OverlayCloseReason.OutsidePress);
        BeginSwallowGesture();
        return true;
    }

    /// <summary>Esc 关闭（焦点路由未消费时调用）：请求 Escape 关闭并给出恢复目标。</summary>
    /// <param name="restoreFocus">
    /// 关闭后应恢复的焦点：SavedFocus（仍可用且不在即将卸载的 popup 子树内）→ 锚子树首个可用 focusable → null。
    /// </param>
    /// <returns>是否存在可关闭的 entry 且 owner 允许 Esc 关闭。</returns>
    internal bool TryRequestEscapeClose(out ViewNode? restoreFocus)
    {
        restoreFocus = null;
        if (_active is not { } entry || !ClosesOnEscape(entry))
        {
            return false;
        }

        restoreFocus = ResolveEscapeRestoreFocus(entry);
        Close(OverlayCloseReason.Escape);
        return true;
    }

    /// <summary>
    /// 当前活动 entry 关闭后的焦点恢复目标（Esc 与 P-05 选后关闭共用同一规则）：
    /// SavedFocus（仍可用且不在即将卸载的 popup 子树内）→ 锚子树首个可用 focusable → null。
    /// </summary>
    /// <param name="restoreFocus">恢复目标；无活动 entry 时为 null。</param>
    /// <returns>当前是否存在活动 entry。</returns>
    internal bool TryResolveRestoreFocus(out ViewNode? restoreFocus)
    {
        if (_active is not { } entry)
        {
            restoreFocus = null;
            return false;
        }

        restoreFocus = ResolveEscapeRestoreFocus(entry);
        return true;
    }

    private void BeginSwallowGesture()
    {
        if (_swallowGestureActive)
        {
            return;
        }

        _swallowGestureActive = true;
        var target = new OverlayDismissTargetView();
        _dismissTargetView = target;
        ReconcileHostChildren();

        var targetNode = FindHostChild(target);
        if (targetNode is null)
        {
            // 宿主不可用（窗口尚未挂载等极端情况）：不留下“不可见的输入拦截者”。
            _swallowGestureActive = false;
            _dismissTargetView = null;
            return;
        }

        _dismissTargetNode = targetNode;

        // 初始 Down 早于下一次布局：先给视口矩形，保证 HitTest 立即命中；后续布局仍由 Arrange 覆盖。
        var viewport = _runtime.ViewportSize;
        targetNode.Frame = new Rect(0, 0, viewport.Width, viewport.Height);
    }

    /// <summary>
    /// 已打开的 popup 内容需要重新测量（如 Select 的 trigger 宽度变化导致最小宽度跟随）。
    /// </summary>
    /// <remarks>
    /// 只在安全阶段真正失效：调用方可能在 Arrange 内（<c>RunLayout</c> 末尾会清 <c>_layoutDirty</c>，
    /// 当场 InvalidateLayout 会被这次布局末尾抹掉）。因此这里只登记请求，由
    /// <see cref="ConsumePending"/> 消费（计入单轮预算）→ 返回进度 → 同一轮 ProcessPendingWork 内再布局。
    /// </remarks>
    internal void InvalidateOpenPopupMeasure() => _popupMeasureInvalid = true;

    /// <summary>结束吞并手势（Up/Cancel/失活/目标失效）：卸载 dismiss-target 并清除状态。</summary>
    internal void EndSwallowGesture()
    {
        if (!_swallowGestureActive && _dismissTargetView is null)
        {
            return;
        }

        _swallowGestureActive = false;
        _dismissTargetView = null;
        _dismissTargetNode = null;
        ReconcileHostChildren();
    }

    /// <summary>安全阶段校验：吞并目标已失效（被卸载/宿主销毁）时结束吞并，不残留不可见输入拦截。</summary>
    /// <returns>是否因此结束了吞并（调用方需要重新布局）。</returns>
    private bool ValidateSwallowGesture()
    {
        if (!_swallowGestureActive || _dismissTargetNode is { IsMounted: true })
        {
            return false;
        }

        EndSwallowGesture();
        return true;
    }

    /// <summary>按“surface?/dismiss-target?”重建宿主子项：两者用稳定键区分，互不误 remount。</summary>
    private void ReconcileHostChildren()
    {
        if (_hostNode is not { IsMounted: true } host)
        {
            return;
        }

        // 每次结构变化一份不可变快照（最多 2 项，空用 []）：一次性分配，
        // 协调期间即使回调触发 EndSwallow/失活也不会被枚举中的可变缓冲影响。
        var surface = _surfaceView is { } surfaceView
            ? new ChildDescriptor(surfaceView, SurfaceChildKey, true)
            : default;
        var target = _dismissTargetView is { } dismiss
            ? new ChildDescriptor(dismiss, DismissChildKey, true)
            : default;

        IReadOnlyList<ChildDescriptor> descriptors = (_surfaceView, _dismissTargetView) switch
        {
            (null, null) => [],
            (not null, null) => [surface],
            (null, not null) => [target],
            _ => [surface, target],
        };

        Reconciler.ReconcileDescriptors(host, descriptors);
    }

    private ViewNode? FindHostChild(View view)
    {
        if (_hostNode is not { } host)
        {
            return null;
        }

        foreach (var child in host.ChildList)
        {
            if (ReferenceEquals(child.View, view))
            {
                return child;
            }
        }

        return null;
    }

    private static bool ClosesOnOutsidePress(ActiveOverlayEntry entry)
        => entry.Owner?.View is not Popup popup || popup.CloseOnOutsidePress;

    private static bool ClosesOnEscape(ActiveOverlayEntry entry)
        => entry.Owner?.View is not Popup popup || popup.CloseOnEscape;

    private static bool IsInPopup(ActiveOverlayEntry entry, ViewNode? node)
        => node is not null && entry.PopupNode is { } popup && IsSelfOrDescendant(popup, node);

    private static bool IsInAnchor(ActiveOverlayEntry entry, ViewNode? node)
        => node is not null && IsSelfOrDescendant(entry.AnchorNode, node);

    /// <summary>Esc 关闭前的焦点恢复目标：SavedFocus 可用且不在即将卸载的 popup 子树内，否则锚子树首个 focusable。</summary>
    private ViewNode? ResolveEscapeRestoreFocus(ActiveOverlayEntry entry)
    {
        if (entry.SavedFocus is { } saved && IsUsableFocus(saved) && !IsInPopup(entry, saved))
        {
            return saved;
        }

        return FirstFocusable(entry.AnchorNode);
    }

    private static bool IsUsableFocus(ViewNode node)
        => node.IsMounted && !node.IsEffectivelyDisabled && node.View.IsFocusable;

    private static ViewNode? FirstFocusable(ViewNode root)
    {
        if (!root.IsMounted)
        {
            return null;
        }

        if (IsUsableFocus(root))
        {
            return root;
        }

        foreach (var child in root.ChildList)
        {
            if (FirstFocusable(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>运行时根层挂载后绑定宿主节点（只调用一次）。</summary>
    internal void AttachHost(ViewNode host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _hostNode = host;
    }

    /// <summary>锚是否位于 popup 子树内（嵌套打开请求）。</summary>
    /// <remarks>
    /// 两种情况都算嵌套：锚已在活动 popup 子树内；或锚尚未链接到父级而当前正协调 popup 子树
    /// （协调器先调用 OnMount 再挂父链接，内容在 OnMount 中请求打开自身必然处于该状态）。
    /// </remarks>
    private bool IsNestedAnchor(ViewNode anchor)
    {
        if (_active?.PopupNode is { } popup && IsSelfOrDescendant(popup, anchor))
        {
            return true;
        }

        return _mounting && anchor.Parent is null;
    }

    private static bool IsSelfOrDescendant(ViewNode ancestor, ViewNode node)
    {
        for (var current = node; current is not null; current = current.Parent)
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
        }

        return false;
    }
}
