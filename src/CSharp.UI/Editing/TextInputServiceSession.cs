namespace CSharp.UI;

/// <summary>
/// 运行时统一分配文本输入会话时返回的焦点租约。
/// </summary>
/// <remarks>
/// <para>
/// <b>撤销先于通知：</b>宿主结束一段焦点归属时先调用 <see cref="Revoke"/>，使旧 epoch 与旧客户端立刻失效，
/// 之后才通知输入服务（<see cref="ITextInputService.EndSession"/> / <see cref="ITextInputService.RequestCancel"/>）。
/// 服务若同步回调到已撤销的租约，回调只会得到显式拒绝，不会写入新客户端——这覆盖了“服务同步回调”与
/// “延迟到达的旧取消”两类串写。
/// </para>
/// <para>租约只能由所属 <see cref="UiRuntime"/> 创建；应用通过运行时分配，不自行构造 epoch。</para>
/// </remarks>
internal sealed class TextInputFocusLease
{
    internal TextInputFocusLease(UiRuntime runtime, int epoch)
    {
        Runtime = runtime;
        Epoch = epoch;
    }

    /// <summary>分配该租约的运行时。</summary>
    internal UiRuntime Runtime { get; }

    /// <summary>焦点 epoch（作为 <see cref="TextInputSession.SessionId"/> 使用），在同一运行时内唯一。</summary>
    internal int Epoch { get; }

    /// <summary>租约是否仍然有效；被撤销后为 false，之后到达的事件与回调都不得写入。</summary>
    internal bool IsValid { get; private set; } = true;

    /// <summary>撤销租约：使旧 epoch/旧客户端立即失效（幂等）。</summary>
    internal void Revoke() => IsValid = false;
}

/// <summary>
/// 运行时与 <see cref="ITextInputService"/> 之间的会话接线（TI-04）：绑定/解绑客户端、收敛取消、
/// 让服务回调始终落到当前有效客户端。
/// </summary>
/// <remarks>
/// <para>
/// 同一个运行时只有一个接线实例，epoch 由 <see cref="UiRuntime.AcquireTextInputLease"/> 统一分配并单调递增；
/// 运行时只保留<b>当前</b>一个归属，不做租约/历史列表，因此焦点切换不会无界增长。
/// </para>
/// <para>
/// <b>重入安全：</b>Start/End 都用代际号保护。服务在 StartSession/EndSession 内同步切换焦点、卸载或再开会话时，
/// 内部已经建立的新会话不会被外层调用的收尾逻辑覆盖；服务抛出的异常在本地收敛为失败并记录脱敏诊断。
/// </para>
/// <para>
/// <b>旧归属保护：</b>提供方（TextField）在失焦/卸载/禁用/窗口失活时按<b>所有者</b>结束自己的会话，
/// 旧节点卸载不会结束已经属于另一个节点的新会话。
/// </para>
/// </remarks>
internal sealed class TextInputServiceSession
{
    private readonly UiRuntime _runtime;
    private TextFieldCompositionClient? _owner;
    private TextInputFocusLease? _lease;
    private Func<int, TextFieldCompositionClient>? _factory;
    private ViewNode? _ownerNode;
    private int _generation;
    private bool _serviceSessionStarted;
    private bool _cancelPending;
    private TextFieldCompositionClient? _cancelPendingOwner;
    private int _cancelPendingGeneration;
    private bool _flushing;
    private TextFieldCompositionClient? _pendingEndOwner;
    internal TextInputServiceSession(UiRuntime runtime) => _runtime = runtime;

    /// <summary>实际生效的文本输入服务（null 归一化为 <see cref="NoTextInputService"/>）。</summary>
    internal ITextInputService Service => TextInputForwarder.EffectiveService(_runtime.Services.TextInputService);

    /// <summary>当前焦点租约；没有会话时为 null。</summary>
    internal TextInputFocusLease? Lease => _lease;

    /// <summary>服务是否接受了当前客户端（用于避免对没有会话的服务发出 End）。</summary>
    internal bool ServiceSessionStarted => _serviceSessionStarted;

    /// <summary>当前焦点会话身份；没有会话时为 <c>default</c>。</summary>
    internal TextInputSession Session => _lease is { } lease ? new TextInputSession(lease.Epoch) : default;

    /// <summary>当前绑定到服务的组合客户端；没有会话时为 null。</summary>
    internal TextFieldCompositionClient? ActiveClient => _lease is { IsValid: true } ? _owner : null;

    /// <summary>该客户端是否是当前有效归属。</summary>
    /// <remarks>外部绑定/来源变化会立即置位 <see cref="TextFieldCompositionClient.EpochInvalidationRequested"/>，
    /// 因此旧 epoch 的客户端在安全阶段换会话之前就已经不再是有效归属。</remarks>
    internal bool IsCurrentOwner(TextFieldCompositionClient client)
        => _lease is { IsValid: true }
            && ReferenceEquals(_owner, client)
            && !client.IsReleased
            && !client.EpochInvalidationRequested;

    /// <summary>
    /// 开始一个焦点会话并绑定客户端。
    /// </summary>
    /// <param name="node">归属节点（用于在安全阶段判断是否仍可恢复）。</param>
    /// <param name="clientFactory">按分配的 epoch 创建组合客户端；只有服务确实接受会话时才会保留其结果。</param>
    /// <returns>确实建立了会话时返回 true。</returns>
    internal bool Start(ViewNode node, Func<int, TextFieldCompositionClient> clientFactory)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(clientFactory);

        // 已有归属：先按所有者结束（先失效、后通知），绝不共享同一个 epoch。
        if (_owner is not null)
        {
            EndForOwner(_owner);
        }

        if (_owner is not null)
        {
            // 服务在结束旧会话的回调里重入并建立了新会话：外层不得覆盖它。
            return false;
        }

        // 窗口失活期间允许逻辑焦点更新，但不得启动文本输入会话；重新激活时由 ResumeTextInput 建立。
        if (!_runtime.IsWindowActive)
        {
            return false;
        }

        var service = Service;
        bool canCompose;
        try
        {
            canCompose = service.Capabilities.Composition;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 能力查询失败（TI-08）：异常不穿透 SetFocus；收敛为“无组合能力”并记录脱敏诊断，
            // 不造 Full、不重试、不影响已提交字符通道。
            ReportServiceFailure("Capabilities", ex);
            return false;
        }

        if (!canCompose)
        {
            return false;
        }

        var lease = _runtime.AcquireTextInputLease();
        var generation = ++_generation;
        TextFieldCompositionClient client;
        try
        {
            client = clientFactory(lease.Epoch);
        }
        catch
        {
            lease.Revoke();
            return false;
        }

        _lease = lease;
        _owner = client;
        _ownerNode = node;
        _factory = clientFactory;
        _serviceSessionStarted = false;

        // 新会话取代旧归属的延迟通知：旧的取消/结束请求不再适用。
        _cancelPending = false;
        _cancelPendingOwner = null;
        _pendingEndOwner = null;

        bool accepted;
        try
        {
            accepted = service.StartSession(client);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ReportServiceFailure("StartSession", ex);
            accepted = false;
        }

        if (_generation != generation || !ReferenceEquals(_owner, client))
        {
            // 服务在 StartSession 内重入并创建了新的会话：不得覆盖，也不得撤销新租约。
            client.IsReleased = true;
            return false;
        }

        if (!accepted)
        {
            client.IsReleased = true;
            lease.Revoke();
            _owner = null;
            _lease = null;
            _ownerNode = null;
            _factory = null;
            _serviceSessionStarted = false;
            return false;
        }

        _serviceSessionStarted = true;
        return true;
    }

    /// <summary>结束当前会话（先失效、后通知）；没有会话时返回 false（幂等）。</summary>
    internal bool End()
    {
        var owner = _owner;
        if (owner is null)
        {
            return false;
        }

        return EndForOwner(owner);
    }

    /// <summary>
    /// 仅当该客户端仍是当前所有者时才结束会话；旧节点卸载/失焦不会结束别的新会话。
    /// </summary>
    internal bool EndForOwner(TextFieldCompositionClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (!ReferenceEquals(_owner, client))
        {
            client.IsReleased = true;
            return false;
        }

        return EndCore();
    }

    private bool EndCore()
    {
        // 绑定本次结束要通知平台取消的那段归属：意图必须绑定旧 owner 与旧代际，
        // 同步回调重入新建会话后不再适用新段，也不会因本方法清状态而静默丢失。
        var cancelOwner = _cancelPending ? _cancelPendingOwner : null;
        var cancelGeneration = _cancelPending ? _cancelPendingGeneration : -1;

        var lease = _lease;
        var started = _serviceSessionStarted;
        var generation = ++_generation;
        var owner = _owner;

        // 先撤销归属并使客户端失效，再通知服务：服务的同步回调（包括撤销锚点的回调）不会写入任何新会话。
        // 几何撤销也必须遵守这个顺序：否则 PublishGeometry(Unavailable) 的同步 Result 仍能写入旧客户端。
        var retractGeometry = owner?.HasPublishedGeometry == true;
        if (owner is not null)
        {
            owner.IsReleased = true;
            owner.MarkGeometryRevoked();
        }

        _owner = null;
        _lease = null;
        _ownerNode = null;
        _factory = null;
        _serviceSessionStarted = false;
        _cancelPending = false;
        _cancelPendingOwner = null;
        lease?.Revoke();

        if (!started)
        {
            return false;
        }

        // “先取消、后结束”边界：归属已失效后才把<b>登记的</b>原生取消请求交给服务（plan：
        // 先切断旧事件归属，再通知平台，防同步回调串写）。窗口失活/失焦的取消→结束紧邻序列
        // 必经此处；错过则 TIP 从未收到 TerminateComposition，Deactivate 后自行决定提交/放弃。
        // 此刻 owner 已释放/租约已撤销：服务在 RequestCancel 内同步投递迟到 Result 只会得到显式拒绝，
        // 重入新建会话由代际号保护，并且新会话不会收到绑定旧归属的取消。
        if (cancelOwner is not null
            && ReferenceEquals(cancelOwner, owner)
            && cancelGeneration == generation - 1
            && !owner!.EpochInvalidationRequested)
        {
            try
            {
                Service.RequestCancel();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                ReportServiceFailure("RequestCancel", ex);
            }

            // 取消回调同步重入并建立了新会话：不再对服务发旧会话的几何撤销/EndSession。
            if (_generation != generation || _owner is not null)
            {
                return true;
            }
        }

        if (retractGeometry)
        {
            try
            {
                Service.PublishGeometry(TextInputGeometry.Unavailable);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                ReportServiceFailure("PublishGeometry", ex);
            }

            // 撤销回调同步重入并建立了新会话：由代际号保护，不再向服务发送旧会话的 EndSession。
            if (_generation != generation || _owner is not null)
            {
                return true;
            }
        }

        try
        {
            Service.EndSession();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ReportServiceFailure("EndSession", ex);
        }

        // 服务在 EndSession 内重入创建的新会话由代际号保护：这里不做任何清理。
        return true;
    }

    /// <summary>请求在安全阶段按所有者结束会话（布局/绘制阶段不能直接调用平台服务）。</summary>
    internal void RequestEndForOwner(TextFieldCompositionClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (!client.IsReleased)
        {
            _pendingEndOwner = client;
        }
    }

    /// <summary>请求向平台撤销当前未提交组合；实际服务调用延迟到安全阶段。</summary>
    /// <remarks>
    /// 调用方只在确实取消了未提交段时请求；组合状态此刻可能已经是本地取消后的 false，
    /// 但平台侧仍可能持有旧预编辑，因此只要求存在有效归属。请求绑定<b>当时的归属与代际</b>，
    /// 安全阶段发现归属已换（例如期间新开了一段组合）时不再发出，绝不取消新的段。
    /// </remarks>
    internal void RequestNativeCancel()
    {
        if (_owner is { IsReleased: false, EpochInvalidationRequested: false } owner)
        {
            _cancelPending = true;
            _cancelPendingOwner = owner;
            _cancelPendingGeneration = _generation;
        }
    }

    /// <summary>
    /// 在安全阶段（不在 Measure/Arrange/Emit 内）消费延迟的平台通知：外部变化换新 epoch，或通知平台取消组合。
    /// </summary>
    internal void FlushPendingNotifications()
    {
        if (_flushing)
        {
            return;
        }

        _flushing = true;
        try
        {
            if (_pendingEndOwner is { } deferred)
            {
                _pendingEndOwner = null;
                EndForOwner(deferred);
            }

            if (_owner is null)
            {
                _cancelPending = false;
                _cancelPendingOwner = null;
                return;
            }

            if (_owner.EpochInvalidationRequested)
            {
                RearmFor(owner: _owner, node: _ownerNode, factory: _factory);
                return;
            }

            if (_cancelPending)
            {
                var pendingOwner = _cancelPendingOwner;
                var pendingGeneration = _cancelPendingGeneration;
                _cancelPending = false;
                _cancelPendingOwner = null;

                // 只对“请求取消时的那一个归属”发出：期间换了客户端/代际（例如服务重入新建会话）时丢弃，
                // 绝不把延迟取消发给新开始的组合。
                if (pendingOwner is not null
                    && ReferenceEquals(_owner, pendingOwner)
                    && _generation == pendingGeneration
                    && !pendingOwner.IsReleased
                    && !pendingOwner.EpochInvalidationRequested)
                {
                    try
                    {
                        // 服务可能在 RequestCancel 内同步回调客户端；组合已在本地取消，回调是幂等空操作。
                        Service.RequestCancel();
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        ReportServiceFailure("RequestCancel", ex);
                    }
                }
            }
        }
        finally
        {
            _flushing = false;
        }
    }

    /// <summary>外部内容/来源变化后换新 epoch：结束旧会话，并在节点仍可输入时开始新会话。</summary>
    /// <remarks>
    /// 结束旧会话会同步通知服务，服务可能在回调里切换焦点/卸载/再开会话。因此<b>回调之后</b>必须重新校验
    /// 当前归属与真实焦点，不能只依赖回调前算出的 resumable 布尔值。
    /// </remarks>
    private void RearmFor(TextFieldCompositionClient owner, ViewNode? node, Func<int, TextFieldCompositionClient>? factory)
    {
        if (!ReferenceEquals(_owner, owner) || node is null || factory is null)
        {
            return;
        }

        EndForOwner(owner);

        // 服务在 EndSession 内重入并建立了新会话：外层不得覆盖它。
        if (_owner is not null)
        {
            return;
        }

        // 回调之后重新校验真实归属与焦点（不能沿用回调前的判断）。
        if (!node.IsMounted
            || !node.IsFocused
            || node.IsEffectivelyDisabled
            || node.View is not TextField
            || !ReferenceEquals(_runtime.FocusedNode, node))
        {
            return;
        }

        Start(node, factory);
    }

    /// <summary>服务把组合批次交给框架：只处理当前有效归属，租约已撤销或没有归属时显式拒绝。</summary>
    internal TextInputApplyResult Apply(IReadOnlyList<CompositionEvent> events)
    {
        if (events is null || events.Count == 0)
        {
            return TextInputApplyResult.NotHandled(TextSnapshot.Empty, "组合事件批次为空；没有需要应用的内容。");
        }

        if (_lease is not { IsValid: true } || _owner is null || _owner.IsReleased || _owner.EpochInvalidationRequested)
        {
            return TextInputApplyResult.Rejected(
                TextSnapshot.Empty,
                "焦点文本输入归属已撤销或不存在；事件不写入，也不重投到当前焦点。");
        }

        return _owner.ApplyValidated(events);
    }

    /// <summary>修正服务把事件投递给旧客户端实例的情况：只投递给当前有效归属。</summary>
    internal TextInputApplyResult ApplyFromClient(TextFieldCompositionClient caller, IReadOnlyList<CompositionEvent> events)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (!IsCurrentOwner(caller))
        {
            return TextInputApplyResult.Rejected(
                caller.LastReliableSnapshot,
                "该客户端实例的焦点归属已撤销；事件不写入，也不重投到当前焦点。");
        }

        return caller.ApplyValidated(events);
    }

    /// <summary>
    /// 在安全阶段发布当前客户端 caret/组合/排除几何（TI-06）；只接受当前有效归属。
    /// </summary>
    /// <remarks>
    /// 平台调用封装在这里，服务抛错在本地收敛为失败并记录脱敏诊断，不会越过布局后的安全阶段。
    /// 同值去重由 <see cref="TextFieldCompositionClient"/> 负责。
    /// </remarks>
    internal bool PublishGeometry(TextFieldCompositionClient caller, TextInputGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (!IsCurrentOwner(caller) || !_serviceSessionStarted)
        {
            return false;
        }

        try
        {
            return Service.PublishGeometry(geometry);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ReportServiceFailure("PublishGeometry", ex);
            return false;
        }
    }

    private void ReportServiceFailure(string operation, Exception exception)
        => _runtime.ReportDiagnostic($"文本输入服务 {operation} 失败：{exception.GetType().Name}");
}
