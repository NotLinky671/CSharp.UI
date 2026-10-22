namespace CSharp.UI.OpenTK.WindowsTsf;

/// <summary>
/// 正式宿主<b>稳定</b>的 Windows 文本输入服务/路由器（TI-07 P3b R7）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么必须是稳定实例：</b>宿主在构造 <see cref="UiRuntime"/> 时把本对象放进
/// <c>UiRuntimeServices.TextInputService</c>，输入适配器也持有同一个对象。若像上一版那样
/// 事后替换宿主字段，运行时与适配器仍指向<b>已释放的旧 IMM 桥</b>，后续
/// <c>StartSession</c>/<c>Geometry</c>/<c>Cancel</c> 全部打到死对象上。
/// </para>
/// <para>
/// <b>后端如何切换：</b>本对象自身是唯一对外身份；内部按<b>真实焦点 epoch</b>在
/// “IMM 子桥”与“TSF 会话”之间切换。切换只改内部委托，<b>不</b>替换对外实例，
/// 因此运行时/适配器/已发布的几何都始终指向正确对象。
/// </para>
/// <para>
/// <b>延迟建立：</b>TSF 会话在<b>第一次真实 <c>StartSession(client)</c></b> 时按该客户端的
/// 真实快照惰性建立，因此不需要“构造期先找到一个已聚焦的客户端”。
/// </para>
/// <para>
/// <b>退役真实资源：</b><c>EndSession</c>/取消/epoch 变化都会退役<b>实际的</b>
/// owner/store/context/hook，而不是只改一个 <c>ActiveClient</c> 字段。
/// </para>
/// <para>
/// <b>失败回退：</b>TSF 建立失败时通过内部 IMM 子桥继续工作，并把准确原因写入
/// <see cref="BackendReason"/>；回退路径完成完整解绑，不残留半初始化 COM。
/// </para>
/// <para>
/// <b>能力报告：</b>按<b>真实后端实例</b>报告：惰性握手期只声明组合能力；TSF 会话建立后按真实
/// UI-less 状态报告候选锚点，IMM 路径报告该实例的实际能力。不返回无原因、未经验证的 Full。
/// </para>
/// </remarks>
internal sealed class WindowsTextInputRouter : IWindowsInputBridge
{
    private readonly object _gate = new();
    /// <summary>当前可用的 IMM 子桥（TSF 退役后可能已被替换为新实例）。</summary>
    private IWindowsInputBridge? _immSubBridge;

    /// <summary>IMM 工厂：TSF 失败/退役后创建**新的**可用 IMM，而不是复用已释放实例。</summary>
    private readonly Func<IWindowsInputBridge?>? _immFactory;

    /// <summary>
    /// The host's <b>single stable</b> ordered input queue, owned by this router for the whole host
    /// lifetime. A backend switch must never replace it and never clear it.
    /// </summary>
    /// <remarks>
    /// A TSF bridge owns a coordinator for its own lifecycle and clears it on Dispose. If the host's
    /// deferred-input queue lived there, retiring an epoch would drop still-undelivered input
    /// (e.g. a captured click / character Y queued before the switch). Only the <b>event source</b>
    /// follows the backend; the queue identity does not.
    /// </remarks>
    private readonly HostInputCoordinator _queueCoordinator = new();

    /// <summary>
    /// Coordinator whose real safe-boundary exits are forwarded to the host: the stable queue on the
    /// default IMM path, or the <b>real TSF bridge</b>'s coordinator after takeover.
    /// </summary>
    private HostInputCoordinator _eventSourceCoordinator;

    private TsfInputBridge? _tsfBridge;
    private CandidateOverlay _candidateOverlay = new();
    private ITextInputClient? _activeClient;
    private UiRuntime? _runtime;
    private bool _disposed;
    private bool _immRetired;

    private string _backendReason = "IMM（默认后端）。";
    private int _tsfCreateCount;
    private int _tsfRetireCount;
    private int _tsfCreateFailureCount;
    private int _epochSwitchCount;

    /// <summary>Single stable delegate used for every safe-boundary subscription (migratable, no per-subscriber leak).</summary>
    private readonly Action _eventSourceHandler;

    /// <summary>Event-source coordinator currently subscribed for host notification (null when unbound).</summary>
    private HostInputCoordinator? _signalCoordinator;

    /// <summary>Host wake callback injected via <see cref="AttachHostSafePhaseSignal"/>.</summary>
    private Action? _hostSafePhaseSignal;

    /// <summary>Number of real safe-boundary notifications delivered to the host (diagnostics).</summary>
    private int _hostSignalNotificationCount;

    /// <summary>Guards the forwarding accessor against re-entering itself.</summary>
    private bool _forwardingQueueEvent;

    /// <summary>
    /// 创建路由器。
    /// </summary>
    /// <param name="immSubBridge">默认 IMM 子桥（可为 null：无 IMM 时仅已提交文本）。</param>
    /// <param name="enableTsf">是否允许在真实客户端出现时建立 TSF 会话（内部测试开关）。</param>
    internal WindowsTextInputRouter(IWindowsInputBridge? immSubBridge, bool enableTsf)
        : this(immSubBridge, enableTsf, immFactory: null)
    {
    }

    /// <summary>
    /// 创建路由器（可注入 IMM 工厂以支持“失败后重建新 IMM”）。
    /// </summary>
    /// <param name="immSubBridge">初始 IMM 子桥（可为 null）。</param>
    /// <param name="enableTsf">是否允许建立 TSF。</param>
    /// <param name="immFactory">
    /// IMM 重建工厂：TSF 退役或失败后必须得到**新的可用实例**，
    /// 绝不能把已释放的旧 IMM 交回运行时。
    /// </param>
    internal WindowsTextInputRouter(
        IWindowsInputBridge? immSubBridge,
        bool enableTsf,
        Func<IWindowsInputBridge?>? immFactory)
    {
        _immSubBridge = immSubBridge;
        TsfEnabled = enableTsf;
        _immFactory = immFactory;
        _eventSourceCoordinator = _queueCoordinator;
        _eventSourceHandler = OnEventSourceSafePhaseExited;
        _queueCoordinator.SafePhaseExited += _eventSourceHandler;
    }

    /// <summary>是否允许建立 TSF 会话。</summary>
    internal bool TsfEnabled { get; }

    /// <summary>当前活动后端（诊断用；不代表已声明 Full 能力）。</summary>
    internal HostInputBackendKind ActiveBackend
        => _tsfBridge is not null ? HostInputBackendKind.Tsf : HostInputBackendKind.Imm;

    /// <summary>后端选择/回退的准确原因（脱敏）。</summary>
    internal string BackendReason
    {
        get
        {
            lock (_gate)
            {
                return _backendReason;
            }
        }
    }

    /// <summary>当前 TSF 桥（未启用/未建立时为 null）。</summary>
    internal TsfInputBridge? TsfBridge => _tsfBridge;

    /// <summary>候选 overlay（宿主创建后立即注入；同一实例跨 epoch 复用）。</summary>
    internal CandidateOverlay CandidateOverlay => _candidateOverlay;

    /// <summary>
    /// 注入候选 overlay。<b>必须</b>在 TSF 会话惰性建立之前调用（否则新建会话拿不到呈现目标）。
    /// </summary>
    internal void AttachCandidateOverlay(CandidateOverlay overlay)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        _candidateOverlay = overlay;
    }

    /// <summary>帧末候选入口：活动 TSF 桥先复核身份再绘制，未建立时绘制空 overlay。</summary>
    internal void DrawCandidates(DrawContext context)
    {
        var tsf = _tsfBridge;
        if (tsf is not null)
        {
            tsf.DrawCandidates(context);
            return;
        }

        _candidateOverlay.Draw(context);
    }

    /// <summary>是否仍有需要真实安全阶段处理的待办（宿主据此单次唤醒，不轮询）。</summary>
    internal bool HasPendingSafeWork => _tsfBridge?.HasPendingSafeWork ?? false;

    /// <summary>窗口最小化/不可见：显示表面发布零矩形 + S_OK，文本几何撤销。</summary>
    internal void NotifySurfaceUnavailable() => _tsfBridge?.NotifySurfaceUnavailable();

    /// <summary>TSF 会话建立次数。</summary>
    internal int TsfCreateCount => _tsfCreateCount;

    /// <summary>TSF 会话退役次数。</summary>
    internal int TsfRetireCount => _tsfRetireCount;

    /// <summary>TSF 建立失败次数。</summary>
    internal int TsfCreateFailureCount => _tsfCreateFailureCount;

    /// <summary>因 epoch 变化而切换后端的次数。</summary>
    internal int EpochSwitchCount => _epochSwitchCount;

    /// <summary>
    /// <b>仅测试用</b>：强制“不安全”，用于验证延后与 FIFO 重放（生产为 false）。
    /// </summary>
    internal bool ForceUnsafeForTesting { get; set; }

    /// <summary>
    /// 宿主<b>唯一且稳定</b>的有序输入队列（安全阶段/延后队列）。
    /// </summary>
    /// <remarks>
    /// <b>身份不随后端变化：</b>真实 TSF 桥拥有自己的协调器并在 <c>Dispose</c> 时 <c>Clear()</c>。
    /// 若把宿主队列放在那里，epoch 退役会丢掉尚未交付的输入（例如切换前已入队的 pointerB 点击 / 字符 y）。
    /// 因此本属性在整个宿主生命周期内恒为同一个实例，只由本路由器在最终释放时清理。
    /// </remarks>
    internal HostInputCoordinator Coordinator => _queueCoordinator;

    /// <summary>
    /// 真实安全边界退出事件：转发<b>稳定队列</b>与<b>当前事件源</b>（真实 TSF 桥协调器）的退出。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么需要转发：</b>宿主只持有本稳定路由器。真实 apply 退出发生在 TSF 桥<b>自己</b>的
    /// 协调器上，若只订阅 <see cref="Coordinator"/>，宿主的单次唤醒永远不会收到（R10 断链）。
    /// </para>
    /// <para>
    /// <b>为什么稳定队列本身也要订阅：</b>默认 IMM / 回退路径下没有 TSF 桥，此时事件源就是稳定队列，
    /// 直接订阅本事件（或调用 <c>Coordinator.ExitSafePhase()</c>）仍必须能触发一次宿主通知。
    /// </para>
    /// <para>
    /// <b>不替换队列：</b>切换后端只更换事件源订阅；稳定队列实例与其内容始终保持不变。
    /// </para>
    /// </remarks>
    internal event Action? SafePhaseExited;

    /// <summary>Number of real safe-boundary notifications delivered to the host (diagnostics).</summary>
    internal int HostSignalNotificationCount => Volatile.Read(ref _hostSignalNotificationCount);

    /// <summary>IMM sub-bridge already retired (after TSF takeover).</summary>
    internal bool ImmRetired => _immRetired;

    /// <summary>
    /// <b>Host wiring</b>: subscribe once on this stable router and receive real safe-boundary exits
    /// on whichever backend is currently active.
    /// </summary>
    /// <param name="signal">Host single wake/retry callback (invoked synchronously on the boundary thread).</param>
    /// <remarks>
    /// <para><b>Pairing:</b> pairs with <see cref="DetachHostSafePhaseSignal"/>; re-attaching replaces the old callback.</para>
    /// <para>
    /// <b>Only the event source migrates:</b> a backend switch rebinds the source subscription and
    /// <b>never</b> replaces or clears the stable <see cref="Coordinator"/> queue, so queued input
    /// survives focus changes, fallback and epoch retirement in its original order.
    /// </para>
    /// <para>
    /// <b>Bounded catch-up:</b> if the new source already sits inside a safe-phase apply, that exit has
    /// already happened and this subscription will never observe it — notify exactly once, no polling.
    /// </para>
    /// </remarks>
    internal void AttachHostSafePhaseSignal(Action signal)
    {
        ArgumentNullException.ThrowIfNull(signal);

        lock (_gate)
        {
            _hostSafePhaseSignal = signal;
            OnEventSourceChangedLocked(_eventSourceCoordinator);
        }
    }

    /// <summary>
    /// <b>Host wiring</b>: unbind the host callback (host loop exit / dispose). Idempotent.
    /// </summary>
    /// <remarks>After unbinding, no late notification from any coordinator can reach the host.</remarks>
    internal void DetachHostSafePhaseSignal()
    {
        lock (_gate)
        {
            _hostSafePhaseSignal = null;
            UnbindEventSourceLocked();
        }
    }

    private bool _tsfAttempted;

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <b>如实报告：</b>优先现存<b>真实</b> <c>_tsfBridge.Capabilities</c>，其次回退到 IMM 实例的<b>实际</b>
    /// 能力（实例可能只有已提交文本）；都没有时才是已提交文本。
    /// </para>
    /// <para>
    /// <b>保留惰性 TSF 启动握手：</b>尚未建立 TSF 时若声明 <c>composition=false</c>，
    /// 核心 <c>TextInputServiceSession.Start</c> 会在调用 <c>StartSession</c> 之前直接放弃，
    /// TSF 就永远没有建立机会。因此“本路由仍会尝试建立 TSF”时必须保留组合能力。
    /// </para>
    /// <para>
    /// <b>不因读能力而建后端：</b>这里只读现存实例，绝不惰性创建 TSF/IMM，也不更换后端。
    /// </para>
    /// </remarks>
    public TextInputCapabilities Capabilities
    {
        get
        {
            TsfInputBridge? tsf;
            IWindowsInputBridge? imm;
            bool disposed;
            bool canAttemptTsf;
            lock (_gate)
            {
                tsf = _tsfBridge;
                imm = _immSubBridge;
                disposed = _disposed;
                canAttemptTsf = !_disposed && TsfEnabled && !_tsfAttempted;
            }

            if (disposed)
            {
                // 已释放：本实例不再接受会话，也不代表任何组合通道。
                return TextInputCapabilities.CommittedTextOnly;
            }

            if (tsf is not null)
            {
                try
                {
                    return tsf.Capabilities;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // 子桥能力查询失败（TI-08）：只报告诚实保守快照 + 脱敏 Reason，
                    // 不切换/释放/重试真实后端，也不与仍活动的 TSF 形成双写入。
                    return new TextInputCapabilities(
                        committedText: true,
                        reason: $"TSF 桥能力查询失败（{ex.GetType().Name}）；按仅已提交文本报告。");
                }
            }

            TextInputCapabilities? actualImm;
            try
            {
                actualImm = imm?.Capabilities;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // IMM 子桥能力查询失败：如实回退到仅已提交文本，不造 Full、不影响真实后端。
                actualImm = new TextInputCapabilities(
                    committedText: true,
                    reason: $"IMM 桥能力查询失败（{ex.GetType().Name}）；按仅已提交文本报告。");
            }
            if (actualImm is { } existing && (existing.Composition || !canAttemptTsf))
                return existing;

            if (canAttemptTsf)
            {
                // 尚未惰性建立 TSF：保留组合能力作为真实启动握手（见 remarks）。
                return new TextInputCapabilities(
                    committedText: true,
                    composition: true,
                    candidateAnchor: false,
                    reason: "TSF 尚未建立（首次 StartSession 时惰性建立）；候选锚点由真实会话建立后按 UI-less 状态报告。");
            }

            // 不启用 TSF：只能如实报告现存 IMM 的实际能力；没有 IMM 时只有已提交文本。
            return actualImm ?? TextInputCapabilities.CommittedTextOnly;
        }
    }

    /// <inheritdoc />
    public ITextInputClient? ActiveClient => _activeClient;

    /// <inheritdoc />
    public object? CurrentKeyToken
        // 只有当前原生按键链内有效；不缓存到后续 GLFW 事件。
        => _tsfBridge?.CurrentKeyToken ?? _immSubBridge?.CurrentKeyToken;

    /// <inheritdoc />
    public bool CurrentKeyConsumedByIme
        => _tsfBridge?.CurrentKeyConsumedByIme ?? _immSubBridge?.CurrentKeyConsumedByIme ?? false;

    /// <inheritdoc />
    public void AttachRuntime(UiRuntime runtime)
    {
        _runtime = runtime;
        _immSubBridge?.AttachRuntime(runtime);
        _tsfBridge?.AttachRuntime(runtime);
    }

    /// <summary>
    /// 建立一次真实客户端归属（这是 TSF 惰性建立的入口）。
    /// </summary>
    /// <param name="client">真实文本输入客户端。</param>
    /// <returns>是否接受了该归属。</returns>
    /// <remarks>
    /// <para>
    /// 每次真实 <c>StartSession</c> 都代表一次新的焦点 epoch：若已有 TSF 会话且客户端<b>不同</b>，
    /// 先退役旧会话（真实 owner/store/context/hook），再按新客户端快照建立新会话。
    /// </para>
    /// <para>
    /// 建立失败不阻断输入：回退到 IMM 子桥并记录准确原因。
    /// </para>
    /// </remarks>
    public bool StartSession(ITextInputClient client)
    {
        ArgumentNullException.ThrowIfNull(client);

        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            var sameClient = ReferenceEquals(_activeClient, client);
            if (sameClient && _tsfBridge is not null)
            {
                // 同一归属重复 StartSession：幂等，不重建会话。
                return true;
            }

            if (!sameClient && _tsfBridge is not null)
            {
                // 客户端替换 = 新 epoch：必须退役真实旧会话。
                RetireTsfLocked("客户端替换（epoch 变化）。");
                _epochSwitchCount++;
            }

            _activeClient = client;
            if (TsfEnabled) _tsfAttempted = true;
        }

        var accepted = !TsfEnabled
            ? StartImmSession(client, "TSF 未启用（内部开关）；使用 IMM。")
            : TryStartTsf(client);

        if (!accepted)
        {
            lock (_gate)
            {
                // 只有本次归属仍未改变时才清空：绝不覆盖重入时已建立的新归属。
                if (ReferenceEquals(_activeClient, client))
                {
                    _activeClient = null;
                    if (_tsfBridge is null)
                    {
                        _backendReason = $"会话未被接受（{_backendReason}）；当前没有活动文本输入归属。";
                    }
                }
            }
        }

        return accepted;
    }

    /// <summary>
    /// 在没有 TSF 的无回退分支上启动 IMM 会话，并记录<b>准确</b>的失败原因。
    /// </summary>
    /// <remarks>
    /// <b>绝不虚报接受：</b>没有可用 IMM 时返回 <c>false</c>（旧实现用 <c>?? true</c> 把“没有后端”
    /// 报成“已接受”），失败原因写入 <see cref="BackendReason"/>。
    /// </remarks>
    private bool StartImmSession(ITextInputClient client, string reasonWhenNoBackend)
    {
        var imm = EnsureImm();
        if (imm is null)
        {
            lock (_gate)
            {
                _backendReason = reasonWhenNoBackend + " 没有可用的 IMM 输入后端，会话未被接受。";
            }

            return false;
        }

        var accepted = imm.StartSession(client);
        lock (_gate)
        {
            _backendReason = accepted
                ? "IMM 会话已建立。"
                : $"IMM 拒绝建立会话（{reasonWhenNoBackend}）。";
        }

        return accepted;
    }

    /// <summary>按真实客户端快照惰性建立 TSF 会话。</summary>
    private bool TryStartTsf(ITextInputClient client)
    {
        var hwnd = _tsfBridge?.WindowHandle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero)
        {
            hwnd = ResolveHostWindowHandle();
        }

        if (hwnd == IntPtr.Zero)
        {
            lock (_gate)
            {
                _backendReason = "TSF 未启用：拿不到自有窗口句柄；使用 IMM。";
            }

            return StartImmSession(client, "TSF 不可用（没有自有窗口句柄）。");
        }

        // 真实快照：文本/版本/epoch 全部来自客户端回读，绝不硬编码。
        var snapshot = client.Document;

        // 先退役 IMM 写入来源（由产品完成），否则 TSF 会被“一窗口一个 writer”正确拒绝。
        // 这是产品的生命周期切换，不是绕过冲突：IMM 真正停止后再由 TSF 接管。
        RetireImm();

        // 真实 TSF 桥使用**它自己**的协调器；必须在同一个 gate 内接管桥并完成宿主订阅搬迁，
        // 否则宿主会一直订阅路由器协调器，真实 apply 退出永远通知不到宿主（R10）。
        TsfInputBridge? created = null;
        string? failureReason = null;
        var acquired = false;
        lock (_gate)
        {
            if (_disposed)
            {
                failureReason = "路由器已释放。";
            }
            else if (TsfInputBridge.TryCreate(
                         hwnd,
                         client,
                         c => ReferenceEquals(c, _activeClient),
                         snapshot.Text,
                         _candidateOverlay,
                         () => _runtime?.InvalidateRender(),
                         out created,
                         out failureReason)
                     && created is not null)
            {
                _tsfBridge = created;
                _tsfCreateCount++;
                _backendReason = "TSF 会话已建立（真实客户端归属）。";
                BindEventSourceCoordinatorLocked(created.Coordinator);
                acquired = true;
            }

            if (!acquired)
            {
                _tsfCreateFailureCount++;
                _backendReason = $"TSF 未启用（{failureReason ?? "未知原因"}）；已回退 IMM。";
            }
        }

        if (acquired && created is not null)
        {
            if (_runtime is { } runtime)
            {
                created.AttachRuntime(runtime);
                created.InitializeSurface();
            }

            created.HostWorkGuard = _hostWorkGuard;
            return true;
        }

        if (created is not null)
        {
            // 与释放竞争：本次建立的真实会话与租约必须立即成对归还，绝不泄漏 TSF 资源。
            created.Dispose();
        }

        return StartImmSession(client, $"TSF 建立失败（{failureReason ?? "未知原因"}）。");
    }

    /// <summary>解析宿主窗口句柄（由宿主在构造时注入，避免工具猜）。</summary>
    private IntPtr _hostWindowHandle;

    /// <summary>
    /// 注入宿主“正在处理重建/布局/帧”的真实状态查询（避免硬编码 hostWorking=false）。
    /// </summary>
    internal void AttachHostWorkGuard(Func<bool> guard)
    {
        _hostWorkGuard = guard;
        if (_tsfBridge is { } bridge)
        {
            bridge.HostWorkGuard = guard;
        }
    }

    private Func<bool>? _hostWorkGuard;

    /// <summary>注入宿主窗口句柄（宿主在创建路由器后立即调用一次）。</summary>
    internal void AttachHostWindow(IntPtr hwnd) => _hostWindowHandle = hwnd;

    /// <summary>
    /// <b>仅测试用</b>：把<b>事件源</b>换成给定的 TSF 协调器，走与真实 TSF 接管<b>完全同一</b>条订阅路径。
    /// </summary>
    /// <param name="coordinator">替代事件源（&lt;c&gt;null&lt;/c&gt; 表示回到稳定队列自身）。</param>
    /// <remarks>
    /// 生产代码不会调用它；它只用于在<b>没有真实 COM/TSF</b> 的环境里驱动真实的订阅搬迁路径。
    /// 稳定队列（<see cref="Coordinator"/>）<b>不</b>因它而改变。
    /// </remarks>
    internal void AttachTsfCoordinatorForTesting(HostInputCoordinator? coordinator)
    {
        lock (_gate)
        {
            BindEventSourceCoordinatorLocked(coordinator ?? _queueCoordinator);
        }
    }

    /// <summary>切换事件源并搬迁订阅（<b>不</b>替换稳定队列；必须在 <see cref="_gate"/> 内调用）。</summary>
    private void BindEventSourceCoordinatorLocked(HostInputCoordinator coordinator)
    {
        _eventSourceCoordinator = coordinator;
        OnEventSourceChangedLocked(coordinator);
    }

    /// <summary>按当前事件源状态（重新）挂接宿主/路由器级通知。</summary>
    private void OnEventSourceChangedLocked(HostInputCoordinator coordinator)
    {
        UnbindEventSourceLocked();

        // 稳定队列自身的退出必须始终转发（默认 IMM / 回退路径没有 TSF 桥）。
        if (!ReferenceEquals(coordinator, _queueCoordinator))
        {
            coordinator.SafePhaseExited += OnEventSourceSafePhaseExited;
            _signalCoordinator = coordinator;
        }

        var signal = _hostSafePhaseSignal;
        if (signal is null)
        {
            return;
        }

        // 绑定瞬间若该事件源已处于安全阶段应用内部，其退出事件已经发生，本次订阅不会再收到；
        // 只补发一次通知（有界），绝不周期轮询。
        if (coordinator.IsInsideApply)
        {
            signal();
        }
    }

    /// <summary>解除当前事件源上的订阅（幂等；稳定队列自身的转发始终保留）。</summary>
    private void UnbindEventSourceLocked()
    {
        if (_signalCoordinator is { } bound)
        {
            bound.SafePhaseExited -= OnEventSourceSafePhaseExited;
            _signalCoordinator = null;
        }
    }

    /// <summary>
    /// 真实安全边界退出：把事件源的退出通知宿主一次；路由器级订阅由稳定委托在匿名源上直接触发。
    /// </summary>
    /// <remarks>只做通知（不递归、不投递、不改文档）；由 <see cref="_forwardingQueueEvent"/> 防重入。</remarks>
    private void OnEventSourceSafePhaseExited()
    {
        Action? signal;
        Action? observers;
        lock (_gate)
        {
            if (_disposed || _forwardingQueueEvent)
            {
                return;
            }

            _forwardingQueueEvent = true;
            signal = _hostSafePhaseSignal;
            observers = SafePhaseExited;
            if (signal is not null)
            {
                Interlocked.Increment(ref _hostSignalNotificationCount);
            }
        }

        try
        {
            signal?.Invoke();
            observers?.Invoke();
        }
        finally
        {
            lock (_gate)
            {
                _forwardingQueueEvent = false;
            }
        }
    }

    private IntPtr ResolveHostWindowHandle() => _hostWindowHandle;

    /// <summary>退役 TSF 会话：真实释放 owner/store/context/hook，并搬迁宿主订阅（必须在 <see cref="_gate"/> 内调用）。</summary>
    private void RetireTsfLocked(string reason)
    {
        var bridge = _tsfBridge;
        if (bridge is null)
        {
            return;
        }
        _tsfBridge = null;
        _tsfRetireCount++;
        _backendReason = $"TSF 会话已退役（{reason}）；使用 IMM。";

        // 先把订阅从真实 TSF 协调器上摘除（成对解绑），再释放会话：
        // 释放后的旧 epoch 协调器不可能再唤醒宿主。
        // 注意：只搬迁订阅，稳定队列（Coordinator）与其未交付内容保持不变。
        BindEventSourceCoordinatorLocked(_queueCoordinator);

        // 在 <see cref="_gate"/> 内释放：Dispose 会触碰 COM 与协调器，但所有调用方都在锁内，
        // 顺序由本方法保证，因此不再有“锁外释放”的额外语义。
        bridge.Dispose();
    }

    /// <summary>
    /// 取得可用的 IMM 子桥；若旧的已释放则通过工厂重建新实例。
    /// </summary>
    /// <remarks>绝不把已 Dispose 的旧 IMM 交回运行时（会导致后续 Start/Geometry 打到死对象）。</remarks>
    private IWindowsInputBridge? EnsureImm()
    {
        if (!_immRetired && _immSubBridge is not null)
        {
            return _immSubBridge;
        }

        // 需要新实例：优先工厂，其次允许复用尚未退役的实例。
        if (_immFactory is { } factory)
        {
            var fresh = factory();
            if (fresh is not null)
            {
                _immSubBridge = fresh;
                _immRetired = false;
                if (_runtime is { } runtime)
                {
                    fresh.AttachRuntime(runtime);
                }

                return fresh;
            }
        }

        return null;
    }

    /// <summary>退役 IMM 子桥（TSF 接管后不得两个来源同时写）。</summary>
    private void RetireImm()
    {
        if (_immRetired)
        {
            return;
        }

        _immRetired = true;

        // 真实卸载 IMM（撤销子类化并归还写入来源），由产品完成。
        _immSubBridge?.Dispose();
    }

    /// <inheritdoc />
    public bool EndSession()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            _activeClient = null;
            RetireTsfLocked("会话结束。");
        }

        return EnsureImm()?.EndSession() ?? true;
    }

    /// <inheritdoc />
    public TextInputApplyResult ApplyComposition(IReadOnlyList<CompositionEvent> events)
    {
        var tsf = _tsfBridge;
        if (tsf is not null)
        {
            return tsf.ApplyComposition(events);
        }

        return EnsureImm()?.ApplyComposition(events)
            ?? TextInputApplyResult.Rejected(TextSnapshot.Empty, "没有活动输入后端。");
    }

    /// <inheritdoc />
    public bool RequestCancel()
    {
        // 取消必须走 owner/core action 序列，而不是直接 CancelComposition 造成镜像失步。
        var tsf = _tsfBridge;
        if (tsf is not null)
        {
            return tsf.RequestCancel();
        }

        return EnsureImm()?.RequestCancel() ?? false;
    }

    public void NotifyCommittedBaselineChanged() => _tsfBridge?.NotifyCommittedBaselineChanged();

    /// <inheritdoc />
    public bool PublishGeometry(TextInputGeometry geometry)
    {
        var tsf = _tsfBridge;
        if (tsf is not null)
        {
            return tsf.PublishGeometry(geometry);
        }

        return EnsureImm()?.PublishGeometry(geometry) ?? false;
    }

    /// <inheritdoc />
    public void NotifyGeometryInvalidated()
    {
        _tsfBridge?.NotifyGeometryInvalidated();
        _immSubBridge?.NotifyGeometryInvalidated();
    }

    /// <inheritdoc />
    public void UpdatePointerTransform(double scaleX, double scaleY)
    {
        _tsfBridge?.UpdatePointerTransform(scaleX, scaleY);
        _immSubBridge?.UpdatePointerTransform(scaleX, scaleY);
    }

    /// <summary>在安全阶段应用已捕获事实（真实后端）。</summary>
    /// <returns>true 表示可以继续交付本条输入；false 表示应延后。</returns>
    /// <remarks>
    /// <para>
    /// <b>默认 IMM / 未聚焦 / TSF 失败回退必须直接放行：</b>上一版在没有 TSF 桥时返回 <c>false</c>，
    /// 于是每个普通输入都被压进延后队列而<b>永不重放</b>——默认路径停止交付（R9 已独立复现）。
    /// 没有 TSF 就没有“待应用的原生事实”，因此这里必须返回 true。
    /// </para>
    /// <para>有 TSF 时按其真实 store 安全状态判定；不安全才返回 false 让调用方延后。</para>
    /// </remarks>
    internal bool DrainSafePhase()
    {
        // 测试接缝：强制不安全（验证延后/重放，无需真实 TSF 会话）。
        if (ForceUnsafeForTesting)
        {
            return false;
        }

        var tsf = _tsfBridge;

        // 没有 TSF：没有待应用事实，直接放行（默认 IMM 路径的关键）。
        if (tsf is null)
        {
            return true;
        }

        return tsf.DrainSafePhase();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            // 先解绑宿主回调并摘除事件源订阅：释放后的任何迟到通知都不能再唤醒已释放的宿主。
            _hostSafePhaseSignal = null;
            UnbindEventSourceLocked();

            RetireTsfLocked("路由器释放。");

            // 只有最终释放才清理稳定队列（后端切换绝不清理它）。
            _queueCoordinator.Clear();
            _candidateOverlay.Clear();
        }

        _immSubBridge?.Dispose();
    }

    /// <summary>只输出身份与计数，不含用户内容。</summary>
    public override string ToString()
        => $"input-router backend={ActiveBackend} tsfEnabled={TsfEnabled} "
            + $"created={_tsfCreateCount} retired={_tsfRetireCount} failures={_tsfCreateFailureCount} "
            + $"epochSwitches={_epochSwitchCount} immRetired={_immRetired}";
}

/// <summary>当前活动输入后端（诊断用）。</summary>
internal enum HostInputBackendKind
{
    /// <summary>IMM32 路径（默认）。</summary>
    Imm = 0,

    /// <summary>TSF 会话路径。</summary>
    Tsf = 1,
}
