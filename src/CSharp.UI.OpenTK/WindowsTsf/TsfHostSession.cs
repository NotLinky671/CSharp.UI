using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CSharp.UI.OpenTK.WindowsTsf;

/// <summary>
/// 正式 OpenTK 宿主的 Windows TSF 会话（TI-07 P3b）。
/// </summary>
/// <remarks>
/// <para>
/// <b>线程模型：</b>所有成员只在创建窗口的 UI 主线程上调用。本类型<b>不</b>创建线程、
/// <b>不</b>迁移窗口、<b>不</b>改动 GLFW 主线程检查。STA 由应用入口 <c>[STAThread]</c> 提供；
/// 本类型只通过 <see cref="StaComApartmentLease"/> 取得<b>当前线程</b>的引用，绝不把 MTA 转成 STA。
/// </para>
/// <para>
/// <b>所有权与顺序：</b>成功路径按
/// COM 租约 → <c>TF_CreateThreadMgr</c> → <c>Activate</c> → 文档管理器 → 上下文 → store 关联窗口 →
/// owner/sink → 编辑完成 sink 的顺序建立；失败路径<b>逆序</b>释放已取得的每一份资源，
/// 因此调用方可以直接降级到既有 IMM 路径，不会留下半初始化的 COM 状态。
/// </para>
/// <para>
/// <b>单一写入来源：</b>同一 HWND 只允许一个组合写入来源。本类型在建立前要求
/// <see cref="HostWriterRegistry"/> 成功占用该 HWND，释放时归还；失败时不占用。
/// </para>
/// <para>
/// <b>回调只捕获：</b>所有 COM 回调经 <see cref="NativeCompositionOwnerSink"/> 只入队不可变事实；
/// 应用（含选区）由宿主在安全阶段经 <see cref="DrainSafePhase"/> 执行。
/// </para>
/// <para>
/// <b>候选通路：</b>本类型只拥有 <see cref="ITfUIElementSink"/> 的注册/撤销与元素生命周期
/// （<see cref="TsfUiElementSink"/>）；候选内容读取、不可变快照与 OpenGL 绘制由 sink/overlay
/// 与 <see cref="TsfInputBridge"/> 完成，本类型不排版、不绘制、不读候选文本。
/// </para>
/// </remarks>
internal sealed class TsfHostSession : IDisposable
{
    private readonly AcpTextStore _store;
    private readonly NativeCompositionOwner _owner;
    private readonly NativeCompositionOwnerSink _sink;
    private readonly ITfThreadMgr _threadMgr;
    private readonly ITfDocumentMgr _documentMgr;
    private readonly ITfContext _context;
    private readonly StaComApartmentLease _lease;
    private readonly IntPtr _hwnd;
    private readonly int _ownerThreadId;
    private readonly uint _clientId;

    /// <summary>本会话持有的写入来源租约（唯一身份；只有本实例能释放）。</summary>
    /// <remarks>
    /// <b>必须在构造函数中赋值：</b>只把它作为参数接收而不保存，会让
    /// <see cref="Dispose"/> 上的 <c>HostWriterRegistry.Release</c> 永远拿到 <c>null</c>，
    /// 于是窗口的写入来源占用永久泄漏、同一窗口再也无法建立第二个会话。
    /// </remarks>
    private readonly HostWriterLease? _writerLease;

    /// <summary>安全阶段发布的 ACP 几何缓存（只读查询，不触碰客户端）。</summary>
    private readonly HostAcpGeometryProvider _geometryProvider;

    /// <summary>候选 UIElement sink（Advise 成对撤销；元素 RCW 由其自身管理）。</summary>
    private readonly TsfUiElementSink? _uiElementSink;

    /// <summary>候选 overlay（sink 发布快照，几何由 bridge 发布）。</summary>
    private readonly CandidateOverlay? _overlay;

    private readonly uint _activeFlags;

    /// <summary>本会话是否真正成功 Advise 了 UIElement sink。</summary>
    private readonly bool _uiElementAdvised;

    private bool _focused;

    /// <summary>是否已把本 context 压入文档管理器栈（必须与 Pop 配对）。</summary>
    private bool _pushed;

    /// <summary>Activate 是否<b>真正成功</b>（决定 Dispose 是否 Deactivate）。</summary>
    private bool _activated;

    private int _focusAlreadyAppliedCount;
    private int _focusFailureCount;
    private int _pushFailureCount;
    private int _teardownPopFailureCount;
    private int _blurFailureCount;

    /// <summary>因焦点属于另一个文档管理器而拒绝代为撤销的次数（诊断）。</summary>
    private int _foreignFocusRejectCount;

    private int _ownerThreadRejectCount;
    private int _unadviseFailureCount;

    /// <summary>
    /// <b>失活取消栅栏（P4）</b>：会话建立后对宿主 HWND 链入只读窗口过程，
    /// 在 <c>WM_ACTIVATE(WA_INACTIVE)</c> 到达时刻向 owner 登记取消意图（较低来源 ticket）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 窗口失活的原生边界是 <c>WM_ACTIVATE(WA_INACTIVE)</c>，它<b>先于</b> TIP 因焦点丢失而捕获的
    /// End/EditCompleted 终结事实（实测 deactivate-trace：seq165 先于 t14/t15）。此刻登记意图，
    /// 下一次真实安全阶段 <see cref="NativeCompositionOwner.ExecutePendingCancels"/> 先于事实应用执行
    /// <c>TerminateComposition</c>，使失活触发的终结事实不再提交预编辑（plan:118"窗口失活先取消"）。
    /// </para>
    /// <para>
    /// <b>只登记、不终止：</b>窗口过程内不得请求写锁/COM 终止，仅登记不可变意图与 ticket；
    /// <b>不消费消息：</b>所有消息一律转发 <c>CallWindowProc</c>。边界<b>之前</b>已应用的独立结果不受影响；
    /// 意图晚于 native capture 时按既有 ticket 序如实保留（不插队）。
    /// </para>
    /// </remarks>
    private static readonly List<Win32Ime.WndProc> PinnedDeactivationProcs = [];
    private Win32Ime.WndProc? _deactivationProcDelegate;
    private IntPtr _deactivationPrevProc;
    private bool _deactivationHookInstalled;
    private int _deactivationIntentCount;
    private int _deactivationHookFailureCount;
    private bool _deactivationFenced;
    /// <summary>
    /// 失焦时切入的<b>会话自有空文档管理器</b>（parking DM）：按需创建，保持到 Dispose。
    /// </summary>
    /// <remarks>
    /// <b>为什么必须有它：</b>官方 <c>ITfThreadMgr::SetFocus</c> 明确 <c>pdimFocus</c> 不能为 NULL；
    /// 实测 <c>SetFocus(null)</c> 返回 <c>S_OK</c> 却<b>不改变</b>焦点（焦点仍是自身）。
    /// 因此失焦必须切到一个自有、无 context/store 的文档管理器，不能指望 NULL。
    /// </remarks>
    private ITfDocumentMgr? _parkingDocumentMgr;

    /// <summary>parking 文档管理器创建失败次数（诊断）。</summary>
    private int _parkingCreateFailureCount;

    /// <summary>已归还写入来源租约（只在<b>确实</b>归还成功后置位）。</summary>
    private int _writerLeaseReleased;

    /// <summary>卸载时 <c>Pop(TF_POPF_ALL)</c> 的结果；0 表示从未执行。</summary>
    private int _teardownPopHResult;

    /// <summary>
    /// 上一次真实 <c>SetFocus</c>（取得焦点）的 HRESULT 及其“是否调用过”标志。
    /// </summary>
    /// <remarks><c>0</c> 是合法成功值 <c>S_OK</c>，因此不能只看数值判断“从未调用”。</remarks>
    private bool _hasFocusHResult;
    private int _lastFocusHResult;

    /// <summary>
    /// 上一次真实失焦操作的 HRESULT 及其“是否尝试过”标志。
    /// </summary>
    /// <remarks><c>0</c> 是合法成功值 <c>S_OK</c>，因此不能只看数值判断“从未尝试”。</remarks>
    private bool _hasBlurHResult;
    private int _lastBlurHResult;

    private bool _disposed;
    private int _wrongThreadDisposeCount;
    private int _drainCount;
    private int _deferredDrainCount;

    private TsfHostSession(
        AcpTextStore store,
        NativeCompositionOwner owner,
        NativeCompositionOwnerSink sink,
        ITfThreadMgr threadMgr,
        ITfDocumentMgr documentMgr,
        ITfContext context,
        StaComApartmentLease lease,
        IntPtr hwnd,
        uint clientId,
        HostWriterLease? writerLease,
        HostAcpGeometryProvider geometryProvider,
        TsfUiElementSink? uiElementSink,
        CandidateOverlay? overlay,
        uint activeFlags,
        bool uiElementAdvised)
    {
        _store = store;
        _owner = owner;
        _sink = sink;
        _threadMgr = threadMgr;
        _documentMgr = documentMgr;
        _context = context;
        _lease = lease;
        _hwnd = hwnd;
        _clientId = clientId;
        _ownerThreadId = Environment.CurrentManagedThreadId;
        // 唯一写入来源租约必须真正持有，否则 Dispose 无法归还它。
        _writerLease = writerLease;
        _geometryProvider = geometryProvider;
        _uiElementSink = uiElementSink;
        _overlay = overlay;
        _activeFlags = activeFlags;
        _uiElementAdvised = uiElementAdvised;
    }

    /// <summary>ACP 几何缓存提供方（bridge 在安全阶段发布）。</summary>
    internal HostAcpGeometryProvider GeometryProvider => _geometryProvider;

    /// <summary>候选 UIElement sink（未接线时为 null）。</summary>
    internal TsfUiElementSink? UiElementSink => _uiElementSink;

    /// <summary>真实 <c>GetActiveFlags</c> 值（诊断）。</summary>
    internal uint ActiveFlags => _activeFlags;

    /// <summary>激活方式（诊断；只有 <c>ActivateEx</c> 这一条路径）。</summary>
    internal string ActivationMode => "ActivateEx(TF_TMAE_UIELEMENTENABLEDONLY)";

    /// <summary>UIElement sink 是否成对注册成功。</summary>
    internal bool UiElementAdvised => _uiElementAdvised;

    /// <summary>自绘候选通路是否真实可用（激活 + flags + sink + overlay 同时成立）。</summary>
    internal bool CandidatePresentationAvailable
        => _uiElementAdvised
            && _overlay is not null
            && (_activeFlags & TsfFlags.TF_TMF_ACTIVATED) != 0
            && (_activeFlags & TsfFlags.TF_TMAE_UIELEMENTENABLEDONLY) != 0;

    /// <summary>该会话拥有的原生窗口句柄。</summary>
    internal IntPtr WindowHandle => _hwnd;

    /// <summary>创建该会话的线程 id（所有成员都必须在该线程上调用）。</summary>
    internal int OwnerThreadId => _ownerThreadId;

    /// <summary>TSF 客户端 id（<c>Activate</c> 返回）。</summary>
    internal uint ClientId => _clientId;

    /// <summary>产品 store（宿主权威文档与原生视图）。</summary>
    internal AcpTextStore Store => _store;

    /// <summary>初始化实际使用的权威快照；核心版本域与 ACP 原生文档修订独立。</summary>
    internal TextInputAuthoritativeSnapshot InitialSnapshot { get; private set; } = null!;

    /// <summary>组合 owner（事实分类与安全阶段应用）。</summary>
    internal NativeCompositionOwner Owner => _owner;

    /// <summary>COM 回调适配层。</summary>
    internal NativeCompositionOwnerSink Sink => _sink;

    /// <summary>当前线程 COM 套间租约。</summary>
    internal StaComApartmentLease Lease => _lease;
    /// <summary>
    /// TSF 键盘管理器（<c>ITfKeystrokeMgr</c>），用于官方 <c>TestKeyDown→KeyDown</c> 判定。
    /// </summary>
    /// <remarks>
    /// 经 <c>ITfThreadMgr</c> 的 QI 取得；不可用时为 null（此时桥按“未消费”处理，按键交给应用）。
    /// 只在所有者线程访问。
    /// </remarks>
    internal ITfKeystrokeMgr? KeystrokeMgr { get; private set; }

    /// <summary>是否已把该会话的文档设为 TSF 焦点。</summary>
    internal bool IsFocused => _focused;

    /// <summary>安全阶段排空次数（诊断）。</summary>
    internal int DrainCount => _drainCount;

    /// <summary>因不安全而被延后的排空次数（诊断）。</summary>
    internal int DeferredDrainCount => _deferredDrainCount;

    /// <summary>跨线程 Dispose 被拒绝的次数（诊断）。</summary>
    internal int WrongThreadDisposeCount => _wrongThreadDisposeCount;

    /// <summary>是否已释放。</summary>
    internal bool IsDisposed => _disposed;

    /// <summary>幂等命中次数：已聚焦时再次 <see cref="Focus"/>，不重复 Push/SetFocus（诊断）。</summary>
    internal int FocusAlreadyAppliedCount => _focusAlreadyAppliedCount;

    /// <summary>Push 失败次数（诊断；已压栈时绝不重复 Push）。</summary>
    internal int PushFailureCount => _pushFailureCount;

    /// <summary>真实 <c>SetFocus</c>（取得焦点/撤销焦点）失败次数（诊断）。</summary>
    internal int FocusFailureCount => _focusFailureCount;

    /// <summary>失焦失败次数（诊断；失败时 <see cref="IsFocused"/> 保持 true）。</summary>
    internal int BlurFailureCount => _blurFailureCount;

    /// <summary>
    /// 因焦点在<b>别的文档管理器</b>而拒绝代为撤销失焦的次数（诊断）。
    /// </summary>
    /// <remarks>这是“旧会话不得清掉另一会话焦点”的实际保护计数（只作用于 <see cref="Blur"/>）。</remarks>
    internal int ForeignFocusRejectCount => _foreignFocusRejectCount;

    /// <summary>因非所有者线程而被拒绝的 Focus/Blur 次数（诊断）。</summary>
    internal int OwnerThreadRejectCount => _ownerThreadRejectCount;

    /// <summary>失焦用 parking 文档管理器创建失败次数（诊断）。</summary>
    internal int ParkingCreateFailureCount => _parkingCreateFailureCount;

    /// <summary>parking 文档管理器是否已创建（诊断；用于核对 Dispose 前已释放）。</summary>
    internal bool HasParkingDocumentMgr => _parkingDocumentMgr is not null;

    /// <summary>卸载时 <c>ITfTextEditSink::UnadviseSink</c> 失败次数（诊断）。</summary>
    internal int UnadviseFailureCount => _unadviseFailureCount;

    /// <summary>卸载时 <c>Pop(TF_POPF_ALL)</c> 失败次数（诊断）。</summary>
    internal int TeardownPopFailureCount => _teardownPopFailureCount;

    /// <summary>
    /// 卸载时 <c>Pop(TF_POPF_ALL)</c> 的<b>真实</b> HRESULT（0 表示本会话从未执行过 Pop）。
    /// </summary>
    /// <remarks>只记录真实返回值；不使用“没有异常”代替成功。</remarks>
    internal int TeardownPopHResult => _teardownPopHResult;

    /// <summary>最近一次真实 <c>SetFocus</c>（取得焦点）的 HRESULT；<c>0</c> 可能是 <c>S_OK</c>。</summary>
    internal int LastFocusHResult => _lastFocusHResult;

    /// <summary>本会话是否<b>调用过</b>取得焦点的 <c>SetFocus</c>（区分“未调用”与 <c>S_OK</c>）。</summary>
    internal bool HasFocusHResult => _hasFocusHResult;

    /// <summary>最近一次真实失焦操作的 HRESULT；<c>0</c> 可能是 <c>S_OK</c>。</summary>
    internal int LastBlurHResult => _lastBlurHResult;

    /// <summary>本会话是否<b>尝试过</b>失焦操作（区分“未尝试”与 <c>S_OK</c>）。</summary>
    internal bool HasBlurHResult => _hasBlurHResult;

    /// <summary>
    /// 写入来源租约是否<b>确实</b>已归还（诊断；只由 <see cref="Dispose"/> 的成功归还置位）。
    /// </summary>
    /// <remarks>
    /// 这是判断“同一 HWND 能否建立第二个会话”的前置事实：未归还时登记表仍由本会话占用。
    /// </remarks>
    internal bool WriterLeaseReleased => Volatile.Read(ref _writerLeaseReleased) != 0;

    /// <summary>
    /// 在真实宿主上建立 TSF 会话（失败时完整逆序回退，不留半初始化状态）。
    /// </summary>
    /// <param name="hwnd">自有窗口句柄（非 0，属于当前线程）。</param>
    /// <param name="client">当前文本输入客户端（真实 TextField 客户端）。</param>
    /// <param name="isCurrentOwner">该客户端是否仍是当前归属（用于会话失效判定）。</param>
    /// <param name="initialText">初始权威文本（用于初始化 store 视图）。</param>
    /// <param name="session">成功时返回建立的会话（调用方负责 <see cref="Dispose"/>）；失败时为 null。</param>
    /// <returns>显式结果；失败时已释放所有已取得资源且未占用写入来源。</returns>
    /// <remarks>
    /// <b>为什么把创建集中在这里：</b>失败回退必须逆序且完整，分散在调用方容易漏释放。
    /// 本方法保证“要么完全建立，要么完全回到调用前”。
    /// </remarks>
    internal static TsfHostBridgeResult TryCreate(
        IntPtr hwnd,
        ITextInputClient client,
        Func<ITextInputClient, bool> isCurrentOwner,
        string initialText,
        CandidateOverlay overlay,
        Action requestRedraw,
        out TsfHostSession? session)
    {
        session = null;
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(isCurrentOwner);
        ArgumentNullException.ThrowIfNull(overlay);
        ArgumentNullException.ThrowIfNull(requestRedraw);

        if (!OperatingSystem.IsWindows())
        {
            return TsfHostBridgeResult.Failed(
                TsfHostBridgeFailure.PlatformNotSupported,
                "非 Windows 平台：不调用 ole32/msctf。");
        }

        if (hwnd == IntPtr.Zero)
        {
            return TsfHostBridgeResult.Failed(
                TsfHostBridgeFailure.NoWindowHandle,
                "拿不到自有窗口句柄。");
        }

        // 单一写入来源：同一 HWND 只允许一个组合写入者。
        if (!HostWriterRegistry.TryAcquire(hwnd, HostWriterKind.Tsf, out var writerLease, out var conflictReason))
        {
            return TsfHostBridgeResult.Failed(
                TsfHostBridgeFailure.WriterConflict,
                conflictReason ?? "该窗口已有其它输入来源在写。");
        }

        StaComApartmentLease? lease = null;
        ITfThreadMgr? threadMgr = null;
        ITfDocumentMgr? documentMgr = null;
        ITfContext? context = null;
        AcpTextStore? store = null;
        NativeCompositionOwner? owner = null;
        NativeCompositionOwnerSink? sink = null;
        HostAcpGeometryProvider? geometryProvider = null;
        TsfUiElementSink? uiElementSink = null;
        var transferred = false;
        var activated = false;
        var activeFlags = 0u;
        var uiElementAdvised = false;

        try
        {
            // 1) 当前线程 STA 租约：MTA 已占用时如实失败（绝不“转换”套间）。
            lease = StaComApartmentLease.Acquire();
            if (lease.Status == StaComApartmentStatus.ChangedMode)
            {
                return Fail(TsfHostBridgeFailure.ApartmentNotSta,
                    "当前线程已被 MTA 占用（RPC_E_CHANGED_MODE）；需要 [STAThread] 入口。");
            }

            if (lease.Status is not (StaComApartmentStatus.Initialized or StaComApartmentStatus.AlreadyInitialized))
            {
                return Fail(TsfHostBridgeFailure.ApartmentInitFailed,
                    $"CoInitializeEx(STA) 失败：{lease.Status}。");
            }

            // 2) TSF 线程管理器。
            var createHr = TsfNative.TF_CreateThreadMgr(out threadMgr);
            if (createHr < 0 || threadMgr is null)
            {
                return Fail(TsfHostBridgeFailure.ThreadMgrUnavailable,
                    $"TF_CreateThreadMgr hr=0x{createHr:X8}。");
            }

            // 3) 单次 UI-less 激活：只走 ITfThreadMgrEx::ActivateEx，绝不在普通 Activate 后叠加第二次。
            if (threadMgr is not ITfThreadMgrEx threadMgrEx)
            {
                return Fail(TsfHostBridgeFailure.UiElementUnavailable,
                    "QI ITfThreadMgrEx 失败：无法请求 UIElement-only 激活（未调用 Activate）。");
            }

            var activateHr = threadMgrEx.ActivateEx(out var clientId,
                TsfFlags.TF_TMAE_UIELEMENTENABLEDONLY);
            if (activateHr < 0)
            {
                return Fail(TsfHostBridgeFailure.ActivateFailed,
                    $"ActivateEx(UIELEMENTENABLEDONLY) hr=0x{activateHr:X8}。");
            }

            // ActivateEx 一旦原生成功即拥有激活所有权：即使 flags 校验失败，清理链也必须 Deactivate 一次。
            activated = true;

            var flagsHr = threadMgrEx.GetActiveFlags(out activeFlags);
            if (flagsHr < 0
                || (activeFlags & TsfFlags.TF_TMF_ACTIVATED) == 0
                || (activeFlags & TsfFlags.TF_TMAE_UIELEMENTENABLEDONLY) == 0)
            {
                return Fail(TsfHostBridgeFailure.UiElementUnavailable,
                    $"GetActiveFlags hr=0x{flagsHr:X8} flags=0x{activeFlags:X8}："
                    + "缺少 ACTIVATED 或 UIELEMENTENABLEDONLY。");
            }

            // 4) 文档管理器 + 上下文（CreateContext 时 TSF 会对 store QI 组合 owner sink）。
            var dmHr = threadMgr.CreateDocumentMgr(out documentMgr);
            if (dmHr < 0 || documentMgr is null)
            {
                return Fail(TsfHostBridgeFailure.DocumentContextFailed,
                    $"CreateDocumentMgr hr=0x{dmHr:X8}。");
            }

            geometryProvider = new HostAcpGeometryProvider();
            store = new AcpTextStore(geometryProvider);
            store.ResetAuthoritativeText(initialText);
            store.AttachWindow(hwnd);

            var compositionSession = new TextInputCompositionSession(client, isCurrentOwner);
            owner = new NativeCompositionOwner(store, compositionSession, client.Session.SessionId);
            // 失活段只读状态捕获：capture 时刻冻结自有 hwnd 的真实前台/焦点状态
            // （GetFocus/GetForegroundWindow），作为物理失活 vs 独立提交的归因判据。
            owner.CaptureDeactivationProbe = () => CaptureDeactivationState(hwnd);
            sink = new NativeCompositionOwnerSink(owner, store);

            // 官方注册方式：store 必须在 CreateContext 之前暴露组合 owner sink。
            store.CompositionObserver = sink;

            var ctxHr = documentMgr.CreateContext(clientId, 0, store, out context, out _);
            if (ctxHr < 0 || context is null)
            {
                return Fail(TsfHostBridgeFailure.DocumentContextFailed,
                    $"CreateContext hr=0x{ctxHr:X8}。");
            }

            // 5) 编辑完成 sink：必须真正注册成功。
            var adviseHr = sink.AdviseEditSink(context);
            if (adviseHr < 0 || !sink.EditSinkAdvised)
            {
                return Fail(TsfHostBridgeFailure.EditSinkAdviseFailed,
                    $"ITfTextEditSink.AdviseSink hr=0x{adviseHr:X8}。");
            }

            // 6) 初始权威同步：必须使用<b>真实会话快照</b>（真实文本、有向选区、版本、epoch）。
            // 硬编码 0/0、版本 1 会把已有内容中间/反向选区的输入框重置到文档开头，
            // 且 epoch/version 也不是权威回读值。同步失败必须如实失败，不能返回 Success。
            var initialSnapshot = compositionSession.Snapshot;
            if (!store.ApplyAuthoritativeUpdate(initialSnapshot, store.NativeGeneration))
            {
                return Fail(TsfHostBridgeFailure.InitialSyncFailed,
                    $"初始权威同步被拒绝（版本 v{initialSnapshot.Committed.Version}，"
                    + $"选区 {initialSnapshot.Committed.Selection}）。");
            }

            // 7) UIElement 候选 sink：管理器/源 QI 或 Advise 失败都使整个 TSF 会话失败（回退 IMM），
            //    绝不出现“已抑制系统候选但无法自绘”。cookie 0 合法，以显式 advised 标志判定。
            var uiElementMgr = threadMgr as ITfUIElementMgr;
            var uiElementSource = uiElementMgr as ITfSource;
            if (uiElementMgr is null || uiElementSource is null)
            {
                return Fail(TsfHostBridgeFailure.UiElementUnavailable,
                    "QI ITfUIElementMgr/ITfSource 失败：无法注册候选 sink。");
            }

            uiElementSink = new TsfUiElementSink(
                uiElementMgr,
                uiElementSource,
                client.Session.SessionId,
                () => isCurrentOwner(client),
                snapshot => overlay.SetSnapshot(snapshot),
                requestRedraw);

            var uiAdviseHr = uiElementSink.Advise();
            if (uiAdviseHr < 0 || !uiElementSink.IsAdvised)
            {
                return Fail(TsfHostBridgeFailure.UiElementUnavailable,
                    $"ITfUIElementSink.AdviseSink hr=0x{uiAdviseHr:X8} cookie=0x{uiElementSink.Cookie:X8}。");
            }

            uiElementAdvised = true;

            var created = new TsfHostSession(
                store, owner, sink, threadMgr, documentMgr, context, lease, hwnd, clientId, writerLease,
                geometryProvider, uiElementSink, overlay, activeFlags, uiElementAdvised);

            // 成功 Activate 必须<b>转移</b>到实例上：只记在局部变量里会让实例的
            // <see cref="_activated"/> 永远为 false，随后 Dispose 不会 Deactivate，
            // 线程管理器停留在激活状态（TSF 文本服务继续挂在已释放的上下文上）。
            created.InitialSnapshot = initialSnapshot;
            created._activated = true;

            // 键盘管理器：经 QI 取得（失败不阻断会话，桥会按“未消费”把按键交给应用）。
            created.KeystrokeMgr = TryQueryKeystrokeMgr(threadMgr);

            // owner 侧终止原生组合需要本会话的真实 context（借用，不额外持有引用）：
            // 宿主取消必须经 ITfContextOwnerCompositionServices::TerminateComposition，而不是直接取消核心客户端。
            created._owner.BindNativeContext(context);

            // 失活取消栅栏：对宿主 HWND 链入只读窗口过程，WM_ACTIVATE(WA_INACTIVE) 时刻登记
            // owner 取消意图（ticket 先于失活触发的 End/EditCompleted 捕获）。安装失败只计数、不阻断会话。
            created.InstallDeactivationHook();

            session = created;

            // 成功：所有权已转移给 created，禁止 finally 释放。
            // <b>必须清空局部引用</b>：否则 finally 的 ReleaseCom 会在所有权转移之后再减少
            // 这些 RCW 的引用计数，使 created 持有的接口引用失效。
            transferred = true;
            lease = null;
            threadMgr = null;
            documentMgr = null;
            context = null;
            store = null;
            owner = null;
            sink = null;
            writerLease = null;
            geometryProvider = null;
            uiElementSink = null;

            return TsfHostBridgeResult.Success;

            TsfHostBridgeResult Fail(TsfHostBridgeFailure failure, string reason)
                => TsfHostBridgeResult.Failed(failure, reason);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return TsfHostBridgeResult.Failed(
                TsfHostBridgeFailure.DocumentContextFailed,
                $"建立 TSF 会话时异常（{ex.GetType().Name}）。");
        }
        finally
        {
            // 逆序释放：任何未转移出去的东西都必须在这里回收。
            // UIElement sink 最晚创建，最先撤销；元素 RCW 与候选快照由它自行释放/清除。
            if (uiElementSink is not null)
            {
                try
                {
                    uiElementSink.Unadvise();
                }
                catch (Exception)
                {
                }

                uiElementSink.Dispose();
            }

            geometryProvider?.Clear();
            sink?.Dispose();
            owner?.Dispose();
            store?.Dispose();
            ReleaseCom(context);
            ReleaseCom(documentMgr);
            if (threadMgr is not null)
            {
                // 只配对实际成功的 Activate；失败路径不得动到并未持有的激活引用。
                if (activated)
                {
                    try
                    {
                        threadMgr.Deactivate();
                    }
                    catch (Exception)
                    {
                    }
                }

                ReleaseCom(threadMgr);
            }

            lease?.Dispose();

            // 只有**未转移**（失败/异常）路径才归还写入来源；成功路径由实例 Dispose 归还。
            // 成功时局部 writerLease 已被清空，因此这里不会误放走实例持有的租约。
            if (!transferred)
            {
                HostWriterRegistry.Release(writerLease);
            }
        }
    }

    /// <summary>
    /// 把该会话的文档设为 TSF 焦点（窗口获得焦点时调用）。
    /// </summary>
    /// <returns>真实 HRESULT；失败时不改变已持有的栈状态，可安全重试。</returns>
    /// <remarks>
    /// <para>
    /// <b>Push 必须与 Pop 配对</b>：<c>ITfDocumentMgr::Push</c> 的 context 栈最多两个
    /// （官方 <c>TF_E_STACKFULL</c>），重复 Push 同一 context 会占用栈并破坏后续 Push/Pop 平衡。
    /// 因此用 <see cref="_pushed"/> 跟踪，已压栈时<b>不</b>再次 Push。
    /// </para>
    /// <para>
    /// <b>为什么不禁用“切换焦点”</b>：<c>SetFocus</c> 是按线程的全局状态，
    /// 同一线程上聚焦另一个文档管理器是<b>合法且必需</b>的操作（例如焦点在同一线程的输入框之间迁移）。
    /// 因此这里不因“焦点当前在别的文档管理器上”而拒绝，直接按官方语义把本会话文档设为焦点。
    /// 保护另一会话不被<b>清掉</b>焦点的职责在 <see cref="Blur"/>，不在 Focus。
    /// </para>
    /// <para><b>幂等</b>：已聚焦时再次调用直接返回 S_OK，不重复 Push/SetFocus。</para>
    /// <para>
    /// <b>只记录真实 HRESULT</b>：所有原生方法都保留 <c>PreserveSig</c>，负值就是失败，
    /// 绝不把“没有抛异常”当成成功。
    /// </para>
    /// </remarks>
    internal int Focus()
    {
        if (!CanUse)
        {
            return TsfHResult.E_FAIL;
        }

        if (Environment.CurrentManagedThreadId != _ownerThreadId)
        {
            _ownerThreadRejectCount++;
            return TsfHResult.RPC_E_WRONG_THREAD;
        }

        // 已聚焦：幂等成功，不重复 Push/SetFocus。
        if (_focused)
        {
            _focusAlreadyAppliedCount++;
            return TsfHResult.S_OK;
        }

        // 主 context 在会话存续期内**始终保持压栈**（只在 teardown 用 TF_POPF_ALL 移除）。
        // 因此这里只在首次需要时 Push 一次。
        if (!_pushed)
        {
            var pushHr = _documentMgr.Push(_context);
            if (pushHr < 0)
            {
                _pushFailureCount++;
                return pushHr;
            }

            _pushed = true;
        }

        var focusHr = _threadMgr.SetFocus(_documentMgr);
        _lastFocusHResult = focusHr;
        _hasFocusHResult = true;
        if (focusHr < 0)
        {
            // SetFocus 失败：不弹栈（主 context 仍应保持压栈），只如实报告失败并允许重试。
            _focusFailureCount++;
            _focused = false;
            return focusHr;
        }

        _focused = true;
        return focusHr;
    }

    /// <summary>撤销 TSF 焦点（失活/失焦时调用）。</summary>
    /// <returns>真实 HRESULT；失败时 <see cref="IsFocused"/> 保持 true（可安全重试）。</returns>
    /// <remarks>
    /// <para>
    /// <b>不弹主 context</b>：官方 <c>ITfDocumentMgr::Pop</c> 说明，无 <c>TF_POPF_ALL</c> 且栈上只有
    /// 一个 context 时返回 <c>E_FAIL</c>；primary context 只能在文档 uninitialize 时用
    /// <c>TF_POPF_ALL</c> 移除。因此 Blur 只撤销焦点，context 保持压栈；再次 Focus 不会重复 Push。
    /// </para>
    /// <para>
    /// <b>切到自有空文档管理器，而不是 SetFocus(NULL)</b>：官方 <c>ITfThreadMgr::SetFocus</c> 明确
    /// <c>pdimFocus</c> 不能为 NULL；实测 <c>SetFocus(null)</c> 返回 <c>S_OK</c> 却不改变焦点。
    /// 因此失焦切到<a cref="_parkingDocumentMgr">会话自有的空文档管理器</a>（无 context/store），
    /// 并读回确认焦点确实离开了自身——绝不因为返回 S_OK 就宣称已失焦。
    /// </para>
    /// <para>
    /// <b>只撤销自己的焦点</b>：先 <c>GetFocus</c> 确认焦点在本会话（主或 parking）文档管理器上；
    /// 若焦点已经在<b>别人的</b>文档管理器上（例如同一线程上更新的会话），本方法<b>不动</b>它，
    /// 只计数并返回 S_OK。这是“旧 session 不得清掉另一 session 焦点”的实际保护。
    /// </para>
    /// <para><b>幂等</b>：已失焦或已释放时直接返回 S_OK，不触碰 COM。</para>
    /// </remarks>
    internal int Blur()
    {
        if (_disposed)
        {
            return TsfHResult.S_OK;
        }

        if (Environment.CurrentManagedThreadId != _ownerThreadId)
        {
            _ownerThreadRejectCount++;
            return TsfHResult.RPC_E_WRONG_THREAD;
        }

        return BlurCore();
    }

    /// <summary>Blur 的实际实现（Dispose 在标记 <see cref="_disposed"/> 之前调用）。</summary>
    private int BlurCore()
    {
        if (!_focused)
        {
            return TsfHResult.S_OK;
        }

        var ownership = ReadThreadFocusOwnership();
        if (ownership is null)
        {
            _blurFailureCount++;
            _lastBlurHResult = TsfHResult.E_FAIL;
            _hasBlurHResult = true;
            return TsfHResult.E_FAIL;
        }

        if (ownership == TsfThreadFocusOwnership.Foreign)
        {
            // 焦点已被别的文档管理器取得：绝不代为撤销，只计数并如实返回成功（无事可做）。
            _foreignFocusRejectCount++;
            return TsfHResult.S_OK;
        }

        // 焦点已在本会话的 parking 文档管理器上：失焦状态已成立。
        if (ownership == TsfThreadFocusOwnership.OwnParking)
        {
            _focused = false;
            _lastBlurHResult = TsfHResult.S_OK;
            _hasBlurHResult = true;
            return TsfHResult.S_OK;
        }

        var parking = EnsureParkingDocumentMgr();
        if (parking is null)
        {
            _blurFailureCount++;
            _lastBlurHResult = TsfHResult.E_FAIL;
            _hasBlurHResult = true;
            return TsfHResult.E_FAIL;
        }

        var hr = _threadMgr.SetFocus(parking);
        _lastBlurHResult = hr;
        _hasBlurHResult = true;
        if (hr < 0)
        {
            // 失败：焦点可能仍在自身上——如实保留状态，不谎称已失焦。
            _blurFailureCount++;
            _focusFailureCount++;
            return hr;
        }

        // 只有读回确认焦点确实不再属于自身，才更新状态。
        var after = ReadThreadFocusOwnership();
        if (after == TsfThreadFocusOwnership.Own)
        {
            _blurFailureCount++;
            _lastBlurHResult = TsfHResult.E_FAIL;
            return TsfHResult.E_FAIL;
        }

        _focused = false;
        return hr;
    }

    /// <summary>
    /// 对宿主 HWND 链入只读失活观察窗口过程（会话生命周期内有效）。
    /// </summary>
    /// <remarks>
    /// <para><b>失败不阻断：</b>安装失败只计数，会话照常工作（退回“Focus action 交付时才登记意图”的旧路径）。</para>
    /// <para><b>生命周期随会话：</b>安装于 <see cref="TryCreate"/> 成功后，复原于 <see cref="Dispose"/>；
    /// 复原失败把委托钉入 <see cref="PinnedDeactivationProcs"/> 保活，绝不丢悬空函数指针。</para>
    /// </remarks>
    private void InstallDeactivationHook()
    {
        if (_deactivationHookInstalled || _hwnd == IntPtr.Zero || !OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            _deactivationProcDelegate = OnDeactivationWndProc;
            var newProc = Marshal.GetFunctionPointerForDelegate(_deactivationProcDelegate);
            var previous = Win32Ime.SetWindowProc(_hwnd, newProc);
            var error = Marshal.GetLastWin32Error();
            if (previous == IntPtr.Zero && error != 0)
            {
                _deactivationProcDelegate = null;
                _deactivationHookFailureCount++;
                return;
            }

            _deactivationPrevProc = previous;
            _deactivationHookInstalled = true;
        }
        catch (Exception)
        {
            _deactivationProcDelegate = null;
            _deactivationHookFailureCount++;
        }
    }

    /// <summary>复原失活观察窗口过程；只在所属线程调用（<see cref="Dispose"/> 已保证）。</summary>
    private void RestoreDeactivationHook()
    {
        if (!_deactivationHookInstalled)
        {
            return;
        }

        var restored = false;
        try
        {
            // 链校验：只有当前窗口过程仍是本委托时才能安全弹回本层；
            // 若外侧又被其他子类化覆盖，直接改写会摘掉别人的层——如实计数并钉住委托保活。
            var current = Win32Ime.GetWindowProc(_hwnd);
            var ownProc = _deactivationProcDelegate is null
                ? IntPtr.Zero
                : Marshal.GetFunctionPointerForDelegate(_deactivationProcDelegate);
            if (ownProc != IntPtr.Zero && current == ownProc)
            {
                Win32Ime.SetWindowProc(_hwnd, _deactivationPrevProc);
                restored = Win32Ime.GetWindowProc(_hwnd) == _deactivationPrevProc;
            }
        }
        catch (Exception)
        {
            restored = false;
        }

        if (restored)
        {
            _deactivationHookInstalled = false;
            _deactivationProcDelegate = null;
            _deactivationPrevProc = IntPtr.Zero;
            return;
        }

        // 恢复失败：原生仍可能回调 → 钉住委托保活，保留安装标记以如实反映“仍被引用”。
        _deactivationHookFailureCount++;
        if (_deactivationProcDelegate is not null)
        {
            PinnedDeactivationProcs.Add(_deactivationProcDelegate);
            _deactivationProcDelegate = null;
        }
    }

    /// <summary>
    /// 只读失活观察窗口过程：<c>WM_ACTIVATE(WA_INACTIVE)</c> 与 <c>WM_KILLFOCUS</c> 到达时刻
    /// 向 owner 登记取消意图；所有消息一律转发 <see cref="Win32Ime.CallWindowProc"/>，绝不消费。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 两条消息都是真实失活边界且先后次序<b>不固定</b>：窗口被其他窗口激活时 WM_ACTIVATE 先到、
    /// KILLFOCUS 随后（deactivate-trace）；最小化时 KILLFOCUS 先行、携 <c>WA_MINIMIZED</c> 的
    /// WM_ACTIVATE 滞后约 15ms（minimize-trace）。任一先到即登记意图，ticket 即排在失活触发的
    /// TIP 终结事实捕获之前；重复登记按有界意图队列幂等处理。
    /// </para>
    /// <para>
    /// 只登记不可变意图（较低来源 ticket），<b>不</b>在此请求写锁、终止 COM、写核心或排版；
    /// 原生终止由下一次真实安全阶段 <see cref="NativeCompositionOwner.ExecutePendingCancels"/> 执行。
    /// </para>
    /// <para><b>异常绝不越过原生边界：</b>观察失败只计数，消息照常转发。</para>
    /// </remarks>
    private IntPtr OnDeactivationWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            // 真实失活确认边界：仅 KILLFOCUS / WM_ACTIVATE(WA_INACTIVE)——
            // 程序内 SC_MINIMIZE/SHOWWINDOW(0) 是<b>请求</b>而非失活事实
            // （用户撤销最小化时不得误 fence 活动窗口），且 minimize-trace3 证实
            // 该路径根本不下发 SC_MINIMIZE/SHOWWINDOW（TIP 经 TSF focus 通道先 capture）。
            // 意图 ticket 可能晚于失活批 fact——失活归因在 suppression 侧按
            // <c>CapturedDeactivated</c>（capture 时刻 GetFocus/前台取证冻结）判定，不依赖 ticket 序。
            var deactivating =
                (msg == Win32Ime.WM_ACTIVATE && (uint)(ushort)(nuint)wParam == 0)
                || msg == Win32Ime.WM_KILLFOCUS;
            if (deactivating && !_deactivationFenced)
            {
                _ = _owner.RequestCancel(executeNow: false, deactivationFence: true);
                _deactivationFenced = true;
                _deactivationIntentCount++;
            }
        }
        catch (Exception)
        {
            // 观察失败只计数，消息照常转发。
            _deactivationHookFailureCount++;
        }

        return Win32Ime.CallWindowProc(_deactivationPrevProc, hwnd, msg, wParam, lParam);
    }

    /// <summary>
    /// 失活段只读判据：capture 时刻自有窗口是否已离开前台焦点。
    /// <c>GetFocus</c>/<c>GetForegroundWindow</c> 只读查询自有 hwnd，不触碰全局状态。
    /// </summary>
    /// <remarks>
    /// <c>deactivated = !focusIsHwnd || !fgIsHwnd</c>（任一为假即失活段）——两个信号互补、
    /// 分别覆盖不同的泵外 capture 顺序：hide（<c>ShowWindow(SW_HIDE)</c>）路径 SHOWWINDOW/
    /// NCACTIVATE 先到，TIP 同步 capture 时 <c>KILLFOCUS</c> 尚未分派，<c>GetFocus</c> 仍 hwnd
    /// 但前台已离开（<c>fgIsHwnd=false</c>）；minimize 路径 TIP 经 TSF focus 通道在 KILLFOCUS
    /// 前 capture，<c>GetFocus=0</c> 但前台可能仍 hwnd（<c>fgIsHwnd=true</c>）。正常提交两值
    /// 同为 true（anchor-probe 取证），不冲突。
    /// <para>
    /// <b>宿主契约：</b>正式 OpenTK 宿主是单一顶层窗口（first release single window）；
    /// <c>WM_ACTIVATE(WA_INACTIVE)/KILLFOCUS</c> 的失活语义本就按"本顶层窗失去激活/焦点"
    /// 建模。因此<c>fg != hwnd</c>（含 <c>fg==0</c> 与前台被同进程辅助窗/其他顶层接管）
    /// 在此契约下均为失活事实，不做 child/同进程辅助窗特判；若将来引入多窗口宿主，
    /// 此判据需重审。
    /// </para>
    /// </remarks>
    private static bool? CaptureDeactivationState(IntPtr hwnd)
    {
        var focus = Win32Ime.GetFocus();
        var foreground = Win32Ime.GetForegroundWindow();

        return focus != hwnd || foreground != hwnd;
    }

    /// <summary>
    /// 取得（按需创建）失焦用的自有空文档管理器；失败返回 <c>null</c> 并计数。
    /// </summary>
    /// <remarks>
    /// parking 文档管理器只有 TSF 文档管理器语义，<b>没有</b> context、store 或 sink，
    /// 因此不会成为写入来源，也不会参与组合。整个会话存续期内复用同一实例。
    /// </remarks>
    private ITfDocumentMgr? EnsureParkingDocumentMgr()
    {
        if (_parkingDocumentMgr is not null)
        {
            return _parkingDocumentMgr;
        }

        ITfDocumentMgr? created = null;
        try
        {
            var hr = _threadMgr.CreateDocumentMgr(out created);
            if (hr < 0 || created is null)
            {
                _parkingCreateFailureCount++;
                return null;
            }

            _parkingDocumentMgr = created;
            return created;
        }
        catch (Exception)
        {
            _parkingCreateFailureCount++;
            return null;
        }
    }

    /// <summary>
    /// 只读判定当前线程的 TSF 焦点归属（诊断/验收用）。
    /// </summary>
    /// <returns>焦点归属；<c>null</c> 表示已释放、非拥有线程或查询失败。</returns>
    /// <remarks>
    /// <b>纯只读：</b>不计数、不改状态，也不调用 <c>SetFocus</c>/<c>AssociateFocus</c>；
    /// 对 <c>GetFocus</c> 取得的引用成对释放。已释放后不再触碰 COM。
    /// </remarks>
    internal TsfThreadFocusOwnership? ThreadFocusOwnership
    {
        get
        {
            if (_disposed || Environment.CurrentManagedThreadId != _ownerThreadId)
            {
                return null;
            }

            return ReadThreadFocusOwnership();
        }
    }

    /// <summary>
    /// 只读读取当前线程的 TSF 焦点归属；失败/无法判定时返回 <c>null</c>。
    /// </summary>
    /// <remarks>
    /// <b>纯只读：</b>不计数、不改状态；对 <c>GetFocus</c> 取得的引用成对释放。
    /// </remarks>
    private TsfThreadFocusOwnership? ReadThreadFocusOwnership()
    {
        ITfDocumentMgr? focused = null;
        try
        {
            var hr = _threadMgr.GetFocus(out focused);
            if (hr < 0)
            {
                return null;
            }

            if (focused is null)
            {
                // 官方：没有文档管理器有焦点时返回 S_FALSE 且指针为 NULL。
                return TsfThreadFocusOwnership.None;
            }

            if (ComIdentity.Same(focused, _documentMgr) is { } own)
            {
                return own ? TsfThreadFocusOwnership.Own : ClassifyAgainstParking(focused);
            }

            return null;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            ReleaseCom(focused);
        }
    }

    /// <summary>把“不属于主文档管理器”的焦点归到 parking 或 Foreign。</summary>
    private TsfThreadFocusOwnership ClassifyAgainstParking(object focused)
    {
        if (_parkingDocumentMgr is null)
        {
            return TsfThreadFocusOwnership.Foreign;
        }

        return ComIdentity.Same(focused, _parkingDocumentMgr) is true
            ? TsfThreadFocusOwnership.OwnParking
            : TsfThreadFocusOwnership.Foreign;
    }

    /// <summary>
    /// 读取文档栈顶 context（验收用于证明主 context 在 Focus 周期内保持压栈）。
    /// </summary>
    /// <param name="top">栈顶 context；失败时可能为 null。</param>
    /// <returns>HRESULT。</returns>
    internal int GetTopContext(out ITfContext? top)
    {
        top = null;
        if (_disposed)
        {
            return TsfHResult.E_FAIL;
        }

        return _documentMgr.GetTop(out top);
    }

    /// <summary>本会话的 TSF context（验收用于核对栈顶身份）。</summary>
    internal ITfContext Context => _context;

    /// <summary>
    /// 当前是否仍有主 context 压栈（诊断）。
    /// </summary>
    /// <remarks>
    /// 卸载时只有 <c>Pop(TF_POPF_ALL)</c> 得到 <c>S_OK</c> 或 <c>E_FAIL</c>（栈已空）
    /// 才会置回 false；其它失败如实保留 true，不无条件清掉。
    /// </remarks>
    internal bool IsPushed => _pushed;
    /// <summary>
    /// 在<b>安全阶段</b>排空已捕获的原生事实（唯一应用点）。
    /// </summary>
    /// <returns>排空结果；不安全时返回 <see cref="NativeCompositionDrainResult.Deferred"/> 并计数。</returns>
    internal NativeCompositionDrainResult DrainSafePhase()
    {
        if (_disposed)
        {
            return NativeCompositionDrainResult.Empty;
        }

        // 安全阶段判定由 owner/store 自己负责：不安全时返回 Deferred 并保留 pending。
        var wasSafe = _store.IsSafePhase;

        // 宿主取消意图按真实到达 ticket 分界执行：只消费早于首条待决事实的意图
        // （原生终止会同步触发 OnEndComposition，必须先完成“终止 + 核心取消”再应用其后的终结事实）。
        // 失活栅栏之前已确认的独立结果（ticket 更早的事实）不被误拦；更晚意图由 drain 内分界消费。
        // 不在安全阶段时 owner 会保留意图并计数，绝不假装已经取消。
        _owner.ExecutePendingCancelsUpTo(_owner.NextPendingFactTicket);

        var result = _owner.DrainAtSafePhase();
        if (wasSafe)
        {
            _drainCount++;
        }
        else
        {
            _deferredDrainCount++;
        }

        return result;
    }

    /// <summary>当前是否可以在本线程使用该会话。</summary>
    private bool CanUse
        => !_disposed && Environment.CurrentManagedThreadId == _ownerThreadId;

    /// <summary>
    /// 释放会话：撤销焦点 → 撤销 sink → 会话对象 → 停用线程管理器 → 弹出文档栈 → 释放租约 → 归还写入来源。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>跨线程释放被拒绝</b>：不修改任何状态、不触碰 COM，让所有者线程仍能正确清理。
    /// 反复跨线程调用只累计 <see cref="WrongThreadDisposeCount"/>，不消耗任何资源。
    /// </para>
    /// <para>
    /// <b>顺序固定：</b>焦点必须在文档栈被 Pop 之前撤销（Pop 会终止文本服务 UI）；
    /// Pop 必须在释放 context/documentMgr 的 RCW <b>之前</b>执行（此时引用仍有效）。
    /// 线程管理器停用（<c>Deactivate</c>）只按激活所有权调用一次，先于 Pop，不依赖栈状态。
    /// </para>
    /// <para>
    /// <b>在拥有线程上一次完成：</b>释放路径不做任何延期/重试调度——被调用时就把清理做完，
    /// 因此不存在“静默延期但没有人再调用”的滞留窗口。非拥有线程的调用仍然被拒绝。
    /// </para>
    /// <para>
    /// <b>失败不谎报：</b>Pop 的真实 HRESULT 记录在 <see cref="TeardownPopHResult"/>；
    /// 只有在“栈已空 / 已弹空”这类明确结论下才认为主 context 不再压栈。
    /// </para>
    /// </remarks>
    public void Dispose()
    {
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
        {
            _wrongThreadDisposeCount++;
            return;
        }

        if (_disposed)
        {
            return;
        }

        // 先真正撤销焦点（Blur 仍要求 !_disposed），再标记释放。
        // 上一版先置 _disposed=true 让 Blur 直接返回，于是 Dispose 从未撤销 TSF 文档焦点。
        // 这里显式调用 BlurCore（不受 _disposed 守卫影响），把失焦切到自有 parking 文档管理器。
        if (Environment.CurrentManagedThreadId == _ownerThreadId)
        {
            BlurCore();
        }

        // 失活栅栏先摘除：此后到达的 WM_ACTIVATE 不再向已退役 owner 登记意图；
        // 恢复失败钉住委托保活（与 WindowsImeBridge 同一模式），绝不丢悬空函数指针。
        RestoreDeactivationHook();

        _disposed = true;

        // 0) 候选通路：先成对撤销 UIElement sink（释放元素 RCW），再清几何与 overlay；
        //    Deactivate 之前系统候选的接管与本地呈现状态都已完全撤销。
        if (_uiElementSink is not null)
        {
            try
            {
                // 失败（真实错误，不是已断开）保留 advised 标记：Dispose 前只重试一次。
                if (_uiElementSink.Unadvise() < 0)
                {
                    _uiElementSink.Unadvise();
                }
            }
            catch (Exception)
            {
            }

            _uiElementSink.Dispose();
        }

        _geometryProvider.Clear();
        _geometryProvider.ClearSurface();
        _overlay?.Clear();

        // 1) 编辑完成连接：成对撤销（幂等；未建立时为 S_OK）。
        var unadviseHr = TsfHResult.S_OK;
        try
        {
            unadviseHr = _sink.UnadviseEditSink();
        }
        catch (Exception)
        {
            unadviseHr = TsfHResult.E_FAIL;
        }

        if (unadviseHr < 0)
        {
            _unadviseFailureCount++;
        }

        // 2) 会话对象（必须先于 store 释放：它们持有 store/context 的引用）。
        _sink.Dispose();
        _owner.Dispose();
        _store.Dispose();

        // 3) 释放失焦用的自有 parking 文档管理器（在 Deactivate 与 threadMgr 释放之前，
        //    因为它是由 threadMgr 创建的原生对象；逆序释放，绝不泄漏）。
        var parking = _parkingDocumentMgr;
        _parkingDocumentMgr = null;
        ReleaseCom(parking);

        // 4) 只配对**实际成功**的 Activate。
        if (_activated)
        {
            var deactivateHr = TsfHResult.S_OK;
            try
            {
                deactivateHr = _threadMgr.Deactivate();
            }
            catch (Exception)
            {
                deactivateHr = TsfHResult.E_FAIL;
            }

            // 失败如实保留 _activated（下一次 Dispose 可重试），但不重复计数。
            if (deactivateHr >= 0)
            {
                _activated = false;
            }
        }

        // 4) 真正的会话卸载：主 context 只能用 TF_POPF_ALL 移除。
        //    官方：只有单个 primary context 时 Pop(0) 返回 E_FAIL；TF_POPF_ALL 只能在文档 uninitialize 时用。
        if (_pushed)
        {
            var popHr = TsfHResult.E_FAIL;
            try
            {
                popHr = _documentMgr.Pop(TfPopFlagsAll);
            }
            catch (Exception)
            {
                popHr = TsfHResult.E_FAIL;
            }

            _teardownPopHResult = popHr;

            // E_FAIL 表示栈本来就是空的（官方：the stack is empty）——这同样说明主 context 已不在栈上。
            // 其它失败必须如实保留 _pushed=true，让状态反映真实情况而不是无条件清掉。
            if (popHr >= 0 || popHr == TsfHResult.E_FAIL)
            {
                _pushed = false;
            }
            else
            {
                _teardownPopFailureCount++;
            }
        }

        // 5) 释放原生引用（一次计数释放，绝不对可能共享的 RCW 做 FinalReleaseComObject）。
        //    顺序仍与建立相反：KeyStrokeMgr 是 threadMgr 上的独立 QI，先放开它再放开 threadMgr。
        var keystrokeMgr = KeystrokeMgr;
        KeystrokeMgr = null;
        ReleaseCom(keystrokeMgr);
        ReleaseCom(_threadMgr);

        // Pop 未能确认成功时 _pushed 保持 true（见上），文档管理器引用照常释放以避免泄漏；
        // 真实结论保留在 _pushed / TeardownPopHResult / TeardownPopFailureCount 供审计读取。
        ReleaseCom(_context);
        ReleaseCom(_documentMgr);

        // 6) 当前线程 COM 引用与写入来源。
        _lease.Dispose();

        if (HostWriterRegistry.Release(_writerLease))
        {
            Volatile.Write(ref _writerLeaseReleased, 1);
        }
    }

    /// <summary>
    /// 只输出身份与计数，不含用户内容。
    /// </summary>
    public override string ToString()
        => $"tsf-session hwnd=0x{_hwnd:X} thread={_ownerThreadId} client={_clientId} "
            + $"focused={_focused} pushed={_pushed} drains={_drainCount} deferred={_deferredDrainCount}";

    /// <summary>
    /// 从线程管理器 QI 取得 <c>ITfKeystrokeMgr</c>（失败返回 null，不抛异常）。
    /// </summary>
    /// <remarks>
    /// <c>ITfKeystrokeMgr</c> 是<b>独立 IID</b>（不是 <c>ITfThreadMgr</c> 的槽位），
    /// 因此必须用 QI 取得，不能按接口顺序强转。
    /// </remarks>
    private static ITfKeystrokeMgr? TryQueryKeystrokeMgr(ITfThreadMgr threadMgr)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var unknown = IntPtr.Zero;
        var ksPtr = IntPtr.Zero;
        try
        {
            unknown = Marshal.GetIUnknownForObject(threadMgr);
            var iid = TsfIids.KeystrokeMgr;
            if (Marshal.QueryInterface(unknown, in iid, out ksPtr) < 0 || ksPtr == IntPtr.Zero)
            {
                return null;
            }

            return (ITfKeystrokeMgr)Marshal.GetObjectForIUnknown(ksPtr);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (ksPtr != IntPtr.Zero)
            {
                Marshal.Release(ksPtr);
            }

            if (unknown != IntPtr.Zero)
            {
                Marshal.Release(unknown);
            }
        }
    }

    /// <summary><c>TF_POPF_ALL</c>：移除整个 context 栈（仅在文档 uninitialize/会话卸载时使用）。</summary>
    private const uint TfPopFlagsAll = 0x1;

    /// <summary>
    /// 释放本会话持有的一个 COM 引用：<b>恰好一次</b>计数释放本实例拥有的 RCW。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么是 <c>Marshal.ReleaseComObject</c>：</b>它正是“释放本实例持有的那份 RCW 引用”。
    /// 用 <c>GetIUnknownForObject</c> + <c>Marshal.Release</c> 只是 AddRef 后立即 Release，
    /// 净效果为零，本实例的 RCW 引用会一直留着——那是真实泄漏，不能作为释放手段。
    /// </para>
    /// <para>
    /// <b>为什么不是 <c>FinalReleaseComObject</c>：</b>它把引用计数直接清零，
    /// 若同一原生对象还被别的字段/对象使用就会提前失效。这里只减自己这一次。
    /// </para>
    /// <para>释放失败不抛出：调用方已进入卸载路径，不能因为清理失败破坏后续清理。</para>
    /// </remarks>
    private static void ReleaseCom(object? value)
    {
        if (value is null || !OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            Marshal.ReleaseComObject(value);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>原生 COM 身份比较（同一接口指针）。</summary>
    /// <remarks>
    /// 只用真实 <c>IUnknown</c> 指针比较，不依赖托管对象引用相等：
    /// 两次 <c>GetFocus</c>/字段读取可能得到不同的 RCW，但指向同一原生对象。
    /// 任一侧取不到身份时返回 <c>null</c>，调用方按“无法判定”失败关闭。
    /// </remarks>
    private static class ComIdentity
    {
        internal static bool? Same(object left, object right)
        {
            if (!OperatingSystem.IsWindows())
            {
                return null;
            }

            var leftPtr = IntPtr.Zero;
            var rightPtr = IntPtr.Zero;
            try
            {
                leftPtr = Marshal.GetIUnknownForObject(left);
                rightPtr = Marshal.GetIUnknownForObject(right);
                return leftPtr == rightPtr;
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                if (leftPtr != IntPtr.Zero)
                {
                    Marshal.Release(leftPtr);
                }

                if (rightPtr != IntPtr.Zero)
                {
                    Marshal.Release(rightPtr);
                }
            }
        }
    }
}

/// <summary>
/// 当前线程 TSF 文档焦点的归属（TI-07 P3a 只读诊断）。
/// </summary>
/// <remarks>
/// 必须区分“无焦点”和“焦点被别的文档管理器持有”：前者可以安全取得焦点，
/// 后者禁止代为撤销（否则会清掉另一个真实会话的焦点）。
/// </remarks>
internal enum TsfThreadFocusOwnership
{
    /// <summary>焦点确属本会话的文档管理器。</summary>
    Own = 0,

    /// <summary>当前没有文档管理器持有焦点（官方 <c>GetFocus</c> 返回 <c>S_FALSE</c>）。</summary>
    None = 1,

    /// <summary>焦点被<b>别的</b>文档管理器持有。</summary>
    Foreign = 2,

    /// <summary>焦点在本会话失焦用的自有空（parking）文档管理器上，即本会话处于失焦状态。</summary>
    OwnParking = 3,
}
