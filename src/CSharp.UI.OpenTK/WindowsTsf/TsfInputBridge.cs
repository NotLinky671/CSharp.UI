using System.Runtime.InteropServices;

namespace CSharp.UI.OpenTK.WindowsTsf;

/// <summary>
/// 生产宿主的 <b>TSF 输入桥</b>（TI-07 P3b）：把 <see cref="TsfHostSession"/> 接到
/// <see cref="IWindowsInputBridge"/> 契约上，使宿主接线与 IMM 桥完全同构；并负责候选快照到
/// <see cref="CandidateOverlay"/> 的同帧呈现、ACP 只读几何缓存/布局通知与默认 IMM 回退边界。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么实现同一个契约：</b>宿主（<see cref="OpenTkHost"/>）与输入适配器只依赖
/// <see cref="IWindowsInputBridge"/>；TSF 桥实现它之后，宿主不需要为新后端改接线，
/// 也不会出现两套并行写入来源。
/// </para>
/// <para>
/// <b>单一写入来源：</b>实例由宿主创建；创建前宿主必须先释放 IMM 桥（产品负责切换），
/// 本类型再经 <see cref="HostWriterRegistry"/> 占用该 HWND。失败时如实返回原因，宿主回退到 IMM。
/// </para>
/// <para>
/// <b>按键归属：</b>钩子回调只询问 <see cref="TsfKeyRouting"/> 的结论；
/// <see cref="CurrentKeyToken"/> 与 <see cref="CurrentKeyConsumedByIme"/> 按“本次原生按键”设置，
/// 不使用“上一次是否吞掉”的全局标志。
/// </para>
/// <para>
/// <b>COM 回调只捕获：</b>事实由 <see cref="NativeCompositionOwnerSink"/> 入队；
/// 应用只发生在宿主安全阶段经 <see cref="DrainSafePhase"/>。
/// </para>
/// </remarks>
internal sealed class TsfInputBridge : IWindowsInputBridge
{
    private readonly TsfHostSession _session;
    private readonly TsfMessageHook _hook;
    private readonly HostInputCoordinator _coordinator;
    private readonly IntPtr _hwnd;
    private readonly int _ownerThreadId;
    private readonly CandidateOverlay _overlay;
    private readonly Func<ITextInputClient, bool> _isCurrentOwner;

    private UiRuntime? _runtime;
    private ITextInputClient? _activeClient;
    private bool _layoutChangePending;
    private int _geometryPublishCount;
    private int _geometryRejectedCount;
    private int _geometryClearedCount;
    private int _layoutNotificationCount;
    private object? _currentKeyToken;
    private bool _currentKeyConsumed;
    private bool _disposed;
    private int _safeDrainCount;
    private int _deferredDrainCount;

    /// <summary>原生事实排空进行中：期间不得改写权威基线（排空是唯一应用点）。</summary>
    private bool _draining;

    /// <summary>Host wake/retry callback (injected by the router while this bridge is active).</summary>
    private Action? _hostSafePhaseSignal;

    /// <summary>Number of real safe-boundary notifications delivered to the host (diagnostics).</summary>
    private int _hostSignalNotificationCount;

    /// <summary>Number of sessions successfully created and focused (Push + SetFocus).</summary>
    private int _focusedCount;

    private TsfInputBridge(TsfHostSession session, HostInputCoordinator coordinator,
        CandidateOverlay overlay, Func<ITextInputClient, bool> isCurrentOwner)
    {
        _session = session;
        _coordinator = coordinator;
        _hwnd = session.WindowHandle;
        _ownerThreadId = session.OwnerThreadId;
        _overlay = overlay;
        _isCurrentOwner = isCurrentOwner;

        // 钩子只判定“该次按键是否被 TSF 消费”，并据此改写 MSG（消费必须改写，不能只 return 1）。
        _hook = new TsfMessageHook(_hwnd, (uint)GetCurrentThreadId(), IsKeyConsumedByTsf);
    }

    /// <summary>该桥拥有的原生窗口句柄。</summary>
    internal IntPtr WindowHandle => _hwnd;

    /// <summary>底层 TSF 会话（验收工具读取状态用）。</summary>
    internal TsfHostSession Session => _session;

    /// <summary>按键拦截钩子（验收工具读取计数用）。</summary>
    internal TsfMessageHook Hook => _hook;

    /// <summary>
    /// <b>仅验收用</b>：确定性合成消费策略 <c>(message, wParam) → 是否消费</c>。
    /// </summary>
    /// <remarks>生产为 null，完全由真实 TSF <c>TestKeyDown→KeyDown</c> 判定。</remarks>
    internal Func<uint, uint, IntPtr, bool>? ConsumptionPolicyForTesting { get; set; }

    /// <summary>
    /// 宿主“正在处理重建/布局/帧”的真实状态查询（由宿主注入；null 表示未知→按 false）。
    /// </summary>
    /// <remarks>不得硬编码 false：真实 apply 期间的重入必须被识别为不安全。</remarks>
    internal Func<bool>? HostWorkGuard { get; set; }

    /// <summary>
    /// <b>仅测试用</b>：强制“不安全”以验证延后与重放（生产为 null）。
    /// </summary>
    internal bool ForceUnsafeForTesting { get; set; }

    /// <summary>有序协调器。</summary>
    internal HostInputCoordinator Coordinator => _coordinator;

    /// <summary>是否成功安装钩子。</summary>
    internal bool HookInstalled => _hook.Installed;

    /// <summary>安全阶段排空次数。</summary>
    internal int SafeDrainCount => _safeDrainCount;

    /// <summary>因不安全而延后的排空次数。</summary>
    internal int DeferredDrainCount => _deferredDrainCount;
    /// <summary>Real safe-boundary notifications delivered to the host (diagnostics; bounded, not polling).</summary>
    internal int HostSignalNotificationCount => Volatile.Read(ref _hostSignalNotificationCount);

    /// <summary>1 when the real TSF session was created and focused, otherwise 0.</summary>
    internal int FocusedCount => _focusedCount;

    /// <summary>
    /// <b>Host wiring</b>: binds the host's single wake/retry callback.
    /// </summary>
    /// <param name="signal">Host callback; invoked once after a real safe boundary exits.</param>
    /// <remarks>Injected by the router when it takes over this bridge; unbound on retire/dispose.</remarks>
    internal void AttachHostSafePhaseSignal(Action signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        _hostSafePhaseSignal = signal;
    }

    /// <summary><b>Host wiring</b>: unbinds the host callback (idempotent).</summary>
    internal void DetachHostSafePhaseSignal() => _hostSafePhaseSignal = null;

    /// <summary>
    /// Real safe-boundary exit: exactly one host notification (no recursion, no dispatch, no document write).
    /// </summary>
    private void NotifyHostSafePhaseExited()
    {
        var signal = _hostSafePhaseSignal;
        if (signal is null || _disposed)
        {
            return;
        }

        Interlocked.Increment(ref _hostSignalNotificationCount);
        signal();
    }

    /// <inheritdoc />
    public object? CurrentKeyToken => _currentKeyToken;

    /// <inheritdoc />
    public bool CurrentKeyConsumedByIme => _currentKeyConsumed;

    /// <summary>几何发布被身份/安全阶段拒绝的次数（诊断）。</summary>
    internal int GeometryRejectedCount => _geometryRejectedCount;

    /// <summary>成功发布几何缓存的次数（诊断）。</summary>
    internal int GeometryPublishCount => _geometryPublishCount;

    /// <summary>几何被立即清除的次数（Move/Resize/DPI/epoch/版本失效）。</summary>
    internal int GeometryClearedCount => _geometryClearedCount;

    /// <summary>真实安全阶段发出的布局通知次数（诊断）。</summary>
    internal int LayoutNotificationCount => _layoutNotificationCount;

    internal CandidateOverlay Overlay => _overlay;

    /// <summary>
    /// 是否仍有需要真实安全阶段处理的待办（原生事实/终止/取消/布局通知）。
    /// </summary>
    /// <remarks>宿主的恢复唤醒只据此请求一次，不周期轮询。</remarks>
    internal bool HasPendingSafeWork
        => _layoutChangePending || _session.Owner.HasPendingSafeWork;

    /// <inheritdoc />
    public TextInputCapabilities Capabilities
    {
        get
        {
            var candidate = _session.CandidatePresentationAvailable;
            return new TextInputCapabilities(
                committedText: true,
                composition: true,
                candidateAnchor: candidate,
                reason: candidate
                    ? "TSF UI-less：ActivateEx(UIELEMENTENABLEDONLY) + flags + UIElement sink 已就绪。"
                    : "TSF 组合已接入；自绘候选通路不可用（能力/激活/flags/sink 未同时成立）。");
        }
    }

    /// <inheritdoc />
    public ITextInputClient? ActiveClient => _activeClient;

    /// <summary>
    /// 真实组合身份（诊断/验收用只读视图；不属于公开输入契约）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="HostCompositionIdentity.SessionId"/> 是当前客户端固化的焦点 epoch，
    /// <see cref="HostCompositionIdentity.CompositionId"/> 是核心组合状态机的真实组合 ID；
    /// 只有在 <see cref="HostCompositionIdentity.IsComposing"/> 为 true 时组合 ID 才表示活动组合。
    /// </para>
    /// <para>
    /// <b>不依赖几何缓存：</b>裁出/滚动导致 ACP 几何为 null 时，本属性仍可读出同一组合身份，
    /// 供真实场景证明“隐藏/恢复前后仍是同一组合”。无活动客户端或客户端不提供身份接缝时为 null。
    /// </para>
    /// </remarks>
    internal HostCompositionIdentity? CompositionIdentity
        => _activeClient is ICompositionIdentitySource source
            ? new HostCompositionIdentity(source.Epoch, source.CompositionId, source.IsComposing)
            : null;

    /// <summary>当前客户端的真实组合身份（诊断/验收）。</summary>
    internal readonly record struct HostCompositionIdentity(int SessionId, int CompositionId, bool IsComposing);

    /// <summary>
    /// 建立 TSF 输入桥（失败时返回原因，调用方回退到 IMM）。
    /// </summary>
    /// <param name="hwnd">自有窗口句柄。</param>
    /// <param name="client">真实 TextField 客户端（宿主自己的 runtime 聚焦的那个）。</param>
    /// <param name="isCurrentOwner">该客户端是否仍是当前归属。</param>
    /// <param name="initialText">初始权威文本。</param>
    /// <param name="bridge">成功时返回桥实例。</param>
    /// <param name="reason">失败原因（脱敏）。</param>
    internal static bool TryCreate(
        IntPtr hwnd,
        ITextInputClient client,
        Func<ITextInputClient, bool> isCurrentOwner,
        string initialText,
        CandidateOverlay overlay,
        Action requestRedraw,
        out TsfInputBridge? bridge,
        out string? reason)
    {
        bridge = null;
        reason = null;
        ArgumentNullException.ThrowIfNull(overlay);
        ArgumentNullException.ThrowIfNull(requestRedraw);

        var created = TsfHostSession.TryCreate(hwnd, client, isCurrentOwner, initialText,
            overlay, requestRedraw, out var session);
        if (!created.Succeeded || session is null)
        {
            reason = created.Reason;
            return false;
        }

        var coordinator = new HostInputCoordinator();
        var result = new TsfInputBridge(session, coordinator, overlay, isCurrentOwner)
        {
            _activeClient = client,
        };

        if (!result._hook.Install())
        {
            var code = result._hook.InstallFailureCode;
            result.Dispose();
            reason = $"安装 WH_GETMESSAGE 失败（Win32={code}）。";
            return false;
        }

        // Real focus: the TSF session must push its primary context onto the document manager and
        // become the thread focus. Without it, TestKeyDown/KeyDown and document writes act on an
        // unfocused context, so real input never arrives. A failure here must fully Dispose
        // (hook/session/writer lease) and fall back — never leave half-initialized COM behind.
        var focusHr = result._session.Focus();
        if (focusHr < 0)
        {
            result.Dispose();
            reason = $"TSF 会话聚焦失败（HRESULT=0x{focusHr:X8}）。";
            return false;
        }

        result._focusedCount++;
        bridge = result;
        return true;
    }

    /// <summary>
    /// 钩子判定回调：该次按键是否被 TSF <b>确实</b>消费。
    /// </summary>
    /// <remarks>
    /// 必须走官方顺序（TestKeyDown → 必要时 KeyDown），且只把“两次都确认”的结果当作消费。
    /// 回调内不排版、不生成帧、不写文档。
    /// </remarks>
    private bool IsKeyConsumedByTsf(IntPtr hwnd, uint message, uint wParam, IntPtr lParam)
    {
        if (_disposed || hwnd != _hwnd)
        {
            return false;
        }

        // 每次真实按键生成一个独立 token（同一按键链内复用，跨按键不复用）。
        _currentKeyToken = new object();

        // 验收可注入确定性“合成消费策略”：真实 TSF 在没有 key event sink 时
        // 会报告 eaten=false，无法用它构造“被消费”的确定性样例。
        // 生产默认没有该策略，完全由真实 TSF 判定。
        if (ConsumptionPolicyForTesting is { } policy)
        {
            var syntheticConsumed = policy(message, wParam, lParam);
            _currentKeyConsumed = syntheticConsumed;
            return syntheticConsumed;
        }

        var keystrokeMgr = _session.KeystrokeMgr;
        if (keystrokeMgr is null)
        {
            _currentKeyConsumed = false;
            return false;
        }

        var isUp = TsfMessageHookPolicy.IsKeyUpMessage(message);
        var wParamPtr = (IntPtr)wParam;

        // 真实 lParam 直接来自 MSG：保留 scan code、repeat count、extended/previous/transition 位。
        // 绝不用 wParam 冒充 scan code 或重新拼装（那会丢掉 keyup/重复信息）。

        int testHr;
        int testEaten;
        try
        {
            testHr = isUp
                ? keystrokeMgr.TestKeyUp(wParamPtr, lParam, out testEaten)
                : keystrokeMgr.TestKeyDown(wParamPtr, lParam, out testEaten);
        }
        catch (Exception)
        {
            _currentKeyConsumed = false;
            return false;
        }

        int? applyHr = null;
        var applyEaten = false;

        // 官方：只有 TestKeyDown/Up 返回 S_OK 且 eaten 才调用 KeyDown/Up。
        if (testHr == TsfKeyRouting.S_OK && testEaten != 0)
        {
            try
            {
                if (isUp)
                {
                    var hr = keystrokeMgr.KeyUp(wParamPtr, lParam, out var eatenUp);
                    applyHr = hr;
                    applyEaten = hr == TsfKeyRouting.S_OK && eatenUp != 0;
                }
                else
                {
                    var hr = keystrokeMgr.KeyDown(wParamPtr, lParam, out var eatenDown);
                    applyHr = hr;
                    applyEaten = hr == TsfKeyRouting.S_OK && eatenDown != 0;
                }
            }
            catch (Exception)
            {
                applyHr = null;
                applyEaten = false;
            }
        }

        var consumed = TsfKeyRouting.Decide(testHr, testEaten != 0, applyHr, applyEaten)
            == TsfKeyRoutingDecision.Consumed;

        _currentKeyConsumed = consumed;
        return consumed;
    }

    /// <summary>
    /// 在安全阶段应用已捕获事实；不安全时延后（不越过旧事实交付后续输入）。
    /// </summary>
    /// <returns>本次是否真正进入了安全阶段。</returns>
    internal bool DrainSafePhase()
    {
        if (_disposed)
        {
            return false;
        }

        // 测试接缝：强制不安全（验证延后/重放路径）。
        if (ForceUnsafeForTesting)
        {
            _deferredDrainCount++;
            return false;
        }

        var attempt = _coordinator.TryBeginSafePhase(
            isOnOwnerThread: Environment.CurrentManagedThreadId == _ownerThreadId,
            storeSafe: _session.Store.IsSafePhase,
            // Real host-work state: unsafe while rebuilding/laying out and while a frame is being
            // built/submitted. Never hardcode false — reentrancy during a real apply must be seen.
            hostWorking: HostWorkGuard?.Invoke() ?? false);

        if (!attempt.Applied)
        {
            _deferredDrainCount++;
            return false;
        }

        var enteredSafePhase = false;
        _draining = true;
        try
        {
            _session.DrainSafePhase();
            _safeDrainCount++;
            enteredSafePhase = true;
            return true;
        }
        finally
        {
            _draining = false;
            // The real safe-boundary exit event is raised inside ExitSafePhase (the one natural
            // moment at which deferred input may become deliverable).
            _coordinator.ExitSafePhase();

            // After leaving this safe phase, notify the host once when facts were actually applied:
            // the host then schedules exactly one bounded retry (never periodic polling).
            if (enteredSafePhase)
            {
                // 布局通知只在这里（真实安全阶段、锁外）发送；PublishGeometry 本身绝不触发通知。
                NotifyLayoutChangedIfPending();

                try
                {
                    NotifyHostSafePhaseExited();
                }
                catch (Exception)
                {
                    // A throwing host callback must not cross the host loop boundary nor change
                    // the already-completed drain result.
                }
            }
        }
    }

    /// <summary>普通核心输入完成后通知 owner 同步权威基线（仅在真实安全阶段且宿主不忙时）。</summary>
    /// <remarks>
    /// <para>
    /// <b>真实前置：</b>必须在所属线程、<b>未</b>处于宿主工作（重建/布局/帧）、store 处于安全阶段且没有
    /// 正在进行的原生排空时才同步；否则保持基线不动，由下一次安全阶段重试，绝不越过守卫写入。
    /// </para>
    /// <para><b>不抛异常：</b>宿主回调边界上任何异常都被吞掉，不改变已完成的输入交付结论。</para>
    /// </remarks>
    public void NotifyCommittedBaselineChanged()
    {
        if (_disposed || Environment.CurrentManagedThreadId != _ownerThreadId)
        {
            return;
        }

        if (HostWorkGuard?.Invoke() ?? false)
        {
            return;
        }

        // 原生事实排空期间不得改写权威基线（排空是唯一应用点）。
        if (_draining)
        {
            return;
        }

        try
        {
            _session.Owner.SynchronizeCommittedBaseline();
        }
        catch (Exception)
        {
            // 宿主回调路径不允许托管异常越界。
        }
    }

    /// <inheritdoc />
    public void AttachRuntime(UiRuntime runtime)
    {
        _runtime = runtime;
        if (!_disposed)
        {
            // 运行时就绪即发布真实显示表面：GetScreenExt 不等待文本布局。
            RefreshSurface();
        }
    }

    /// <inheritdoc />
    public void NotifyGeometryInvalidated()
    {
        if (_disposed || Environment.CurrentManagedThreadId != _ownerThreadId)
        {
            return;
        }

        // 窗口移动/resize/DPI：立即撤销文本几何与候选放置；下一安全布局由 TextField 重发。
        // 显示表面属于窗口本身，这里重新查询并发布（文本失效不影响 GetScreenExt）。
        _session.GeometryProvider.Clear();
        RefreshSurface();
        _overlay.SetGeometry(ClientLogicalBounds(), null);
        _geometryClearedCount++;
        _layoutChangePending = true;

        // 纯窗口移动不改变逻辑几何：客户端同值去重会吞掉平台必需的真实重发，
        // 因此重置其“已发布”状态（不动当前逻辑几何/身份），保证下一安全阶段确实重发（R7/P4）。
        (_activeClient as IGeometryPublishInvalidationSink)?.InvalidatePublishedGeometry();
    }

    /// <summary>最小化/隐藏：显示表面发布零矩形 + S_OK；文本几何撤销。</summary>
    internal void NotifySurfaceUnavailable()
    {
        if (_disposed || Environment.CurrentManagedThreadId != _ownerThreadId)
        {
            return;
        }

        _session.GeometryProvider.Clear();
        _session.GeometryProvider.PublishSurfaceHidden();
        _overlay.SetGeometry(ClientLogicalBounds(), null);
        _geometryClearedCount++;
        _layoutChangePending = true;

        // 与 NotifyGeometryInvalidated 同理：恢复后同值重发不能被客户端去重吞掉。
        (_activeClient as IGeometryPublishInvalidationSink)?.InvalidatePublishedGeometry();
    }

    /// <summary>建桥后立即发布真实显示表面：GetScreenExt 不等待文本布局。</summary>
    internal void InitializeSurface() => RefreshSurface();

    /// <summary>重新查询窗口客户区屏幕原点并发布表面；查询失败如实失败，不伪造零矩形。</summary>
    private void RefreshSurface()
    {
        var runtime = _runtime;
        var viewport = runtime?.ViewportSize ?? Size.Zero;
        if (runtime is null || viewport.IsEmpty)
        {
            _session.GeometryProvider.PublishSurfaceHidden();
            return;
        }

        if (!TryGetClientScreenOrigin(out var originX, out var originY))
        {
            _session.GeometryProvider.PublishSurfaceFailure(TsfHResult.E_FAIL);
            return;
        }

        var scale = runtime.PixelScale;
        if (!double.IsFinite(scale) || scale <= 0)
        {
            _session.GeometryProvider.PublishSurfaceFailure(TsfHResult.E_FAIL);
            return;
        }

        _session.GeometryProvider.PublishSurface(TsfRect.FromLtrb(
            originX, originY,
            originX + LogicalToPixels(viewport.Width, scale),
            originY + LogicalToPixels(viewport.Height, scale)));
    }

    /// <inheritdoc />
    public void UpdatePointerTransform(double scaleX, double scaleY)
    {
        // 指针换算属于输入适配器；P3a 不需要额外处理。
        _ = scaleX;
        _ = scaleY;
    }

    /// <inheritdoc />
    public TextInputApplyResult ApplyComposition(IReadOnlyList<CompositionEvent> events)
        => _activeClient?.ApplyComposition(events)
            ?? TextInputApplyResult.Rejected(TextSnapshot.Empty, "没有活动客户端。");

    /// <inheritdoc />
    public bool StartSession(ITextInputClient client)
    {
        _activeClient = client;
        return true;
    }

    /// <inheritdoc />
    public bool EndSession()
    {
        _activeClient = null;
        return true;
    }

    /// <summary>
    /// 宿主取消请求：经 owner 登记取消意图，并在<b>真实安全阶段</b>执行原生终止 + 核心取消。
    /// </summary>
    /// <returns>取消是否已真实执行；不安全时已排队（返回 false，由下一次安全阶段执行）。</returns>
    /// <remarks>
    /// <para>
    /// <b>绝不直接 <c>client.CancelComposition()</c>：</b>那只清核心镜像，原生 TSF 组合仍存活，
    /// 随后的真实 <c>OnEndComposition</c>/完成编辑会按残留预编辑分类，可能把旧预编辑写回绑定。
    /// </para>
    /// <para>
    /// <b>前置与延后：</b>必须在所属线程、未释放、宿主不忙（<see cref="HostWorkGuard"/>）、
    /// 未在原生排空中且协调器能进入安全阶段时才执行；否则 owner 只保留取消意图（有界），
    /// 由 <see cref="DrainSafePhase"/> 在下一个真实安全阶段执行。不做任何定时轮询。
    /// </para>
    /// <para>
    /// <b>epoch/Dispose：</b>已释放时返回 false，不把取消投给新 client；取消意图绑定登记时的 epoch。
    /// </para>
    /// </remarks>
    public bool RequestCancel()
    {
        if (_disposed || Environment.CurrentManagedThreadId != _ownerThreadId)
        {
            return false;
        }

        // 宿主正在重建/布局/帧：此刻写原生与核心都不安全，只登记意图（绝不立即执行）。
        if (HostWorkGuard?.Invoke() ?? false)
        {
            _ = _session.Owner.RequestCancel(executeNow: false);
            return false;
        }

        // 正在排空原生事实：不得在其中重入终止/取消（原生终止会同步触发终结回调）。
        if (_draining)
        {
            _ = _session.Owner.RequestCancel(executeNow: false);
            return false;
        }

        // 登记意图（较低 ticket），再尝试在安全阶段执行。
        if (_session.Owner.RequestCancel())
        {
            return true;
        }

        // 未执行：可能是不安全（已排队）或被拒绝。只有确实进入安全阶段才立即重试一次（有界，不轮询）。
        if (_session.Owner.PendingCancelCount == 0)
        {
            return false;
        }

        var attempt = _coordinator.TryBeginSafePhase(
            isOnOwnerThread: Environment.CurrentManagedThreadId == _ownerThreadId,
            storeSafe: _session.Store.IsSafePhase,
            hostWorking: HostWorkGuard?.Invoke() ?? false);

        if (!attempt.Applied)
        {
            _deferredDrainCount++;
            return false;
        }

        try
        {
            return _session.Owner.ExecutePendingCancels();
        }
        finally
        {
            _coordinator.ExitSafePhase();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <b>只在真实安全阶段发布：</b>此处只写不可变缓存（同 UI 线程），<b>不</b>排版、不建帧、
    /// 不发通知。身份核对失败时立即清空缓存并隐藏几何；绝不发布近似坐标。
    /// </para>
    /// <para>
    /// <b>布局通知延后：</b>仅当几何代次真实变化时标记 pending；真正的
    /// <c>OnLayoutChange</c> 由 <see cref="DrainSafePhase"/> 在安全阶段、
    /// <see cref="NotifyLayoutChangedIfPending"/> 中发送（store 在发送时按当前 sink 与 mask 门控；
    /// 连接撤销/释放会清空延迟队列，不会把旧连接排队的项送给新连接）。
    /// </para>
    /// </remarks>
    public bool PublishGeometry(TextInputGeometry geometry)
    {
        if (_disposed || Environment.CurrentManagedThreadId != _ownerThreadId)
        {
            return false;
        }

        if (!_session.Store.IsSafePhase)
        {
            _geometryRejectedCount++;
            return false;
        }

        var client = _activeClient;
        if (client is null || !_isCurrentOwner(client))
        {
            ClearGeometry(markLayoutPending: false);
            _geometryRejectedCount++;
            return false;
        }

        var runtime = _runtime;
        var authoritative = _session.Owner.Snapshot;
        var charSnapshot = (client as ICompositionCharGeometrySource)?.CompositionCharGeometry;
        var storeDocument = _session.Store.ReadAuthoritativeDocument();

        if (runtime is null
            || charSnapshot is null
            || (!geometry.HasCaret && !geometry.HasComposition))
        {
            // 只有“从可见变为不可用”才排队布局通知；否则每次安全阶段重发 Unavailable 都会
            // 重新置 pending，和恢复唤醒形成自激通知循环。
            ClearGeometry(markLayoutPending: _session.GeometryProvider.Current is not null);
            _geometryRejectedCount++;
            return false;
        }

        var valid = charSnapshot.Epoch == client.Session.SessionId
            && charSnapshot.DocumentVersion == authoritative.Committed.Version
            && charSnapshot.DocumentVersion == client.Document.Version
            && string.Equals(charSnapshot.Preedit, authoritative.Preedit, StringComparison.Ordinal)
            // 完整串身份，不是长度比较。
            && string.Equals(charSnapshot.Layout.Text, authoritative.DisplayText, StringComparison.Ordinal)
            && string.Equals(storeDocument.Text, authoritative.DisplayText, StringComparison.Ordinal)
            && charSnapshot.LayoutSerial == runtime.LayoutSerial
            && double.IsFinite(charSnapshot.ScrollX)
            && charSnapshot.ScrollX >= 0
            && double.IsFinite(runtime.PixelScale)
            && runtime.PixelScale > 0;

        var viewport = runtime.ViewportSize;
        var clientBounds = new Rect(0, 0, viewport.Width, viewport.Height);
        if (!valid || clientBounds.IsEmpty
            || !TryGetClientScreenOrigin(out var originX, out var originY))
        {
            ClearGeometry(markLayoutPending: _session.GeometryProvider.Current is not null);
            _geometryRejectedCount++;
            return false;
        }

        var scale = runtime.PixelScale;
        var surface = TsfRect.FromLtrb(originX, originY,
            originX + LogicalToPixels(clientBounds.Width, scale),
            originY + LogicalToPixels(clientBounds.Height, scale));

        var cache = new HostGeometryCache(
            geometry,
            charSnapshot,
            authoritative.DisplayText,
            authoritative.Preedit,
            charSnapshot.Epoch,
            authoritative.Committed.Version,
            storeDocument.Version,
            _session.Store.NativeGeneration,
            authoritative.MirrorRevision,
            authoritative.NativeRevision,
            charSnapshot.LayoutSerial,
            charSnapshot.ScrollX,
            surface,
            scale);

        var previous = _session.GeometryProvider.Current;
        _session.GeometryProvider.Publish(cache);
        _session.GeometryProvider.PublishSurface(surface);
        _overlay.SetGeometry(clientBounds,
            geometry.HasCaret ? geometry.CaretBounds : geometry.CompositionBounds);
        _geometryPublishCount++;

        if (!SameGeometryGeneration(previous, cache))
        {
            _layoutChangePending = true;
        }

        return true;
    }

    /// <summary>
    /// 帧末同帧入口：绘制前复核缓存身份；epoch/版本一旦失效立即清几何，绝不等到下一帧。
    /// </summary>
    internal void DrawCandidates(DrawContext context)
    {
        if (_disposed || _session.IsDisposed)
        {
            _overlay.Clear();
            return;
        }

        var client = _activeClient;
        if (client is null || !_isCurrentOwner(client))
        {
            _overlay.SetGeometry(ClientLogicalBounds(), null);
            _session.GeometryProvider.Clear();
            _overlay.Draw(context);
            return;
        }

        // 同帧绘制前只读复核：缓存、当前 char 快照、布局代次/滚动与 store 身份任一失效都立即清几何。
        // 只比较引用/标量，不排版、不重建、不触碰原生回调。
        var runtime = _runtime;
        var authoritative = _session.Owner.Snapshot;
        var cache = _session.GeometryProvider.Current;
        var currentChar = (client as ICompositionCharGeometrySource)?.CompositionCharGeometry;
        var storeDocument = _session.Store.ReadAuthoritativeDocument();
        var current = runtime is not null
            && cache is not null
            && currentChar is not null
            && currentChar.Epoch == client.Session.SessionId
            && cache.Epoch == client.Session.SessionId
            && currentChar.DocumentVersion == client.Document.Version
            && cache.CoreDocumentVersion == client.Document.Version
            && currentChar.LayoutSerial == runtime.LayoutSerial
            && currentChar.ScrollX.Equals(cache.ScrollX)
            && storeDocument.Version == cache.StoreDocumentVersion
            && _session.Store.NativeGeneration == cache.NativeGeneration
            && string.Equals(currentChar.Layout.Text, cache.DisplayText, StringComparison.Ordinal)
            && string.Equals(storeDocument.Text, cache.DisplayText, StringComparison.Ordinal)
            && string.Equals(cache.Preedit, authoritative.Preedit, StringComparison.Ordinal)
            && string.Equals(cache.DisplayText, authoritative.DisplayText, StringComparison.Ordinal);

        if (!current)
        {
            // 反复失效（缓存已空）不重复排队通知；只在从可见变为失效时通知一次。
            ClearGeometry(markLayoutPending: _session.GeometryProvider.Current is not null);
        }

        _overlay.Draw(context);
    }

    /// <summary>安全阶段发送待处理的布局通知；不在安全阶段时保留待办，不轮询。</summary>
    internal bool NotifyLayoutChangedIfPending()
    {
        if (!_layoutChangePending || _disposed)
        {
            return false;
        }

        if (!_session.Store.IsSafePhase)
        {
            return false;
        }

        var accepted = _session.Store.NotifyLayoutChange();
        _layoutChangePending = false;
        if (accepted)
        {
            _layoutNotificationCount++;
        }

        return accepted;
    }

    /// <summary>立即撤销几何缓存与候选放置（失焦/epoch/窗口几何失效）。</summary>
    internal void ClearGeometry(bool markLayoutPending)
    {
        _session.GeometryProvider.Clear();
        _overlay.SetGeometry(ClientLogicalBounds(), null);
        _geometryClearedCount++;
        if (markLayoutPending && !_disposed)
        {
            _layoutChangePending = true;
        }
    }

    /// <summary>完整撤销候选呈现（释放/卸载）：快照与几何都清。</summary>
    internal void ClearPresentation()
    {
        _session.GeometryProvider.Clear();
        _session.GeometryProvider.ClearSurface();
        _overlay.Clear();
        _layoutChangePending = false;
    }

    private Rect ClientLogicalBounds()
    {
        var viewport = _runtime?.ViewportSize ?? Size.Zero;
        return new Rect(0, 0, viewport.Width, viewport.Height);
    }

    private bool TryGetClientScreenOrigin(out int x, out int y)
    {
        // 窗口客户区原点 → 屏幕物理像素；失败绝不伪造 (0,0)。
        if (Win32Ime.TryClientToScreen(_hwnd, 0, 0, out x, out y))
        {
            return true;
        }

        x = 0;
        y = 0;
        return false;
    }

    private static int LogicalToPixels(double logical, double scale)
    {
        var value = Math.Round(logical * scale, MidpointRounding.AwayFromZero);
        return value >= int.MaxValue ? int.MaxValue
            : value <= int.MinValue ? int.MinValue
            : (int)value;
    }

    private static bool SameGeometryGeneration(HostGeometryCache? previous, HostGeometryCache current)
        => previous is not null
            && previous.Epoch == current.Epoch
            && previous.CoreDocumentVersion == current.CoreDocumentVersion
            && previous.StoreDocumentVersion == current.StoreDocumentVersion
            && previous.NativeGeneration == current.NativeGeneration
            && previous.LayoutSerial == current.LayoutSerial
            && string.Equals(previous.DisplayText, current.DisplayText, StringComparison.Ordinal)
            && previous.Logical.CaretBounds == current.Logical.CaretBounds
            && previous.Logical.CompositionBounds == current.Logical.CompositionBounds;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // 先撤销宿主可见呈现与几何（元素 RCW/快照由会话的 sink 释放），再退役 COM。
        ClearPresentation();

        _hook.Dispose();
        _coordinator.Clear();
        _session.Dispose();
    }

    /// <summary>只输出计数，不含用户内容。</summary>
    public override string ToString()
        => $"tsf-input-bridge hook={_hook.Installed} drains={_safeDrainCount} "
            + $"deferred={_deferredDrainCount}";

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint GetCurrentThreadId();
}
