using System.Runtime.InteropServices;

namespace CSharp.UI.OpenTK.WindowsTsf;

/// <summary>
/// 生产 ACP 文本存储（TI-07 P2b）：<see cref="ITextStoreACP"/> 的实现，单视图、UTF-16 码元文本、
/// 单一原生选区、正确锁协议、暂存编辑批次与按 mask 门控的通知。
/// </summary>
/// <remarks>
/// <para>
/// <b>权威边界：</b>本 store 持有的 <see cref="AcpTextDocument"/> 是<b>原生 ACP 文本视图</b>，
/// 不是 <see cref="TextFieldComposition"/> 的权威文档。TSF 在写锁内的写入只进入
/// <see cref="Staging"/>（<see cref="AcpStagedEdit"/> 批次），<b>绝不</b>写 Binding、绝不推断结果/取消。
/// 权威文本由应用通过 <see cref="ApplyAuthoritativeUpdate"/> 显式推入，或用
/// <see cref="ReadAuthoritativeDocument"/> 读取。
/// </para>
/// <para>
/// <b>不竞争权威编辑：</b>本类型不引用 <see cref="ITextInputClient"/>，没有写绑定路径，也不排版。
/// </para>
/// <para>
/// <b>异常边界：</b>每个 COM 入口整体 try/catch，托管异常不穿过原生边界（映射为 <c>E_FAIL</c>）。
/// </para>
/// <para>
/// <b>线程：</b>所有成员只在拥有它的 UI 线程调用；通知与锁授予都在该线程同步完成，
/// 不使用线程池/计时器。延迟通知与锁续排通过注入的 <see cref="UiDispatcher"/> 回到同一线程。
/// </para>
/// </remarks>
internal sealed class AcpTextStore : ITextStoreACP, ITfContextOwnerCompositionSink, IDisposable
{
    /// <summary>锁续排允许的最大嵌套深度（内联 dispatcher 递归保护）。</summary>
    private const int MaxContinuationDepth = 8;

    private readonly object _stateGate = new();
    private readonly AcpTextDocument _document = new();
    private readonly TextStoreLockManager _locks;
    private readonly int _ownerThreadId;

    /// <summary>跨线程被拒绝的 COM 调用次数（测试与诊断核对“零 sink 调用”）。</summary>
    private int _crossThreadRejected;

    private ITextStoreACPSink? _sink;
    private object? _sinkPunk;
    private uint _sinkMask;

    private readonly uint _viewCookie = 1;
    private IntPtr _hwnd;

    private readonly List<DeferredNotification> _deferred = [];
    private bool _flushing;
    private bool _flushScheduled;
    private bool _continuationScheduled;
    private bool _disposed;
    private int _notificationDepth;

    /// <summary>
    /// 外层 <c>RequestLock</c> 的嵌套深度（安全阶段判定用）。
    /// </summary>
    /// <remarks>
    /// <b>为什么不能只看 <c>LockType==0</c>：</b>同步授予会在 <c>RequestLock</c> 返回<b>之前</b>释放锁，
    /// 因此 `LockType==0` 仍处于 <c>RequestLock</c> 调用栈内。官方要求应用通知必须发生在
    /// <c>RequestLock</c> <b>真正返回之后</b>，所以必须显式跟踪请求栈深度。
    /// </remarks>
    private int _requestDepth;

    /// <summary>
    /// 锁续排的嵌套深度。内联 dispatcher 会在 <see cref="ContinuePendingLocks"/> 栈内再次调度，
    /// 因此必须用有界深度拒绝递归，并把 pending 留给后续安全阶段（绝不无限递归）。
    /// </summary>
    private int _continuationDepth;

    /// <summary>
    /// dispatcher 调用的嵌套深度。内联 dispatcher（<c>action =&gt; action()</c>）会在调度点同步执行，
    /// 若被调用的 Flush/Continue 再次调度就会无限递归；因此任何嵌套调度一律只保留 pending。
    /// </summary>
    private int _dispatchDepth;

    /// <summary>
    /// 是否仍处于 <c>RequestLock</c> 方法体作用域内（含尾部调度）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么不能用 <c>_requestDepth</c>：</b>同步授予会在 <c>RequestLock</c> 返回前释放锁，
    /// 深度也在 <c>finally</c> 里归零；但“调度 dispatcher”与“真正回调 sink”是两个不同时刻。
    /// </para>
    /// <para>
    /// <b>规则：</b>作用域内<b>允许</b>调用 dispatcher（排队型 dispatcher 需要拿到回调），
    /// 但<b>禁止</b>真正执行 sink 通知或锁续排——内联 dispatcher（<c>action =&gt; action()</c>）
    /// 因此会在作用域内被拒绝并保留 pending，等 <c>RequestLock</c> 返回后再由安全阶段执行。
    /// </para>
    /// </remarks>
    private int _requestScopeActive;

    /// <summary>
    /// sink 连接代数：每次安装/卸载 sink 递增（仅更新 mask 不递增）。
    /// </summary>
    /// <remarks>
    /// 一次应用更新可能产生多条通知（文本 + 选区）。若 sink 在文本通知内被卸载/替换，
    /// 后续的选区通知<b>不得</b>发给新连接——那会把上一连接的尾部通知泄漏给替换后的 sink。
    /// 通知序列因此在开始时捕获代数，发送前逐条校验。
    /// </remarks>
    private long _sinkGeneration;

    /// <summary>
    /// 由 sink 的 QI 取得的 <c>ITextStoreACPServices</c>（官方用法）。
    /// </summary>
    /// <remarks>
    /// 只用于构造 ACP range（<c>CreateRange</c>）；不持有 sink 生命周期，卸载时一并释放。
    /// </remarks>
    private ITextStoreAcpServices? _services;

    /// <summary>原生代数（暂存/几何失效隔离用）。</summary>
    private long _nativeGeneration;

    private enum NotificationKind
    {
        TextChange,
        SelectionChange,
        LayoutChange,
    }

    private readonly record struct DeferredNotification(
        NotificationKind Kind, uint Flags, AcpTextChange Change);

    internal AcpTextStore(IAcpGeometryProvider? geometry = null)
    {
        Geometry = geometry ?? UnboundGeometryProvider.Instance;
        Staging = new AcpStagingBuffer();
        _locks = new TextStoreLockManager(GrantSinkLock);

        // R2：捕获所属 UI 线程（构造线程）。所有 COM 入口必须在该线程上调用。
        _ownerThreadId = Environment.CurrentManagedThreadId;
    }

    /// <summary>所属 UI 线程的托管线程 ID（构造线程）。</summary>
    internal int OwnerThreadId => _ownerThreadId;

    /// <summary>当前调用是否在所属 UI 线程上。</summary>
    internal bool IsOnOwnerThread => Environment.CurrentManagedThreadId == _ownerThreadId;

    /// <summary>外层 <c>RequestLock</c> 嵌套深度（0 表示不在请求栈内）。</summary>
    internal int RequestDepth => Volatile.Read(ref _requestDepth);

    /// <summary>
    /// 是否处于“安全阶段”：所属 UI 线程、不在 <c>RequestLock</c> 调用栈内、未持锁、不在通知回调内。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 应用侧修改（<see cref="ApplyAuthoritativeUpdate"/> / <see cref="ResetAuthoritativeText"/>）与
    /// sink 通知<b>只</b>允许在此状态下发生。
    /// </para>
    /// <para>
    /// <b>关键：</b>同步授予会在 <c>RequestLock</c> 返回前释放锁，因此 <c>LockType==0</c> 不足以证明
    /// 已离开请求栈——必须同时要求 <see cref="RequestDepth"/> 为 0。
    /// </para>
    /// </remarks>
    internal bool IsSafePhase
        => IsOnOwnerThread
            && Volatile.Read(ref _requestDepth) == 0
            && Volatile.Read(ref _requestScopeActive) == 0
            && _locks.LockType == 0
            && Volatile.Read(ref _notificationDepth) == 0;

    /// <summary>跨线程被拒绝的 COM 调用次数。</summary>
    internal int CrossThreadRejectedCount => Volatile.Read(ref _crossThreadRejected);

    /// <summary>只读几何提供方（默认未绑定）。</summary>
    internal IAcpGeometryProvider Geometry { get; }

    /// <summary>
    /// 组合 owner 观察器（真实 <c>ITfContextOwnerCompositionSink</c> 委托目标）。
    /// </summary>
    /// <remarks>
    /// <b>官方注册方式：</b><c>CreateContext</c> 的 owner 对象必须<b>自身</b>暴露
    /// <see cref="ITfContextOwnerCompositionSink"/>；TSF 在创建上下文时对该对象 QI 并安装组合 sink。
    /// 事后用 <c>ITfSource::AdviseSink</c> 注册该 IID 会返回 <c>TS_E_NOOBJECT</c>（本批实测确认）。
    /// 因此必须在 <c>CreateContext</c> <b>之前</b>接好本属性。
    /// </remarks>
    internal ITfContextOwnerCompositionSink? CompositionObserver { get; set; }

    /// <inheritdoc />
    public int OnStartComposition(ITfCompositionView pComposition, out int pfOk)
    {
        pfOk = 1;
        var observer = CompositionObserver;
        if (observer is null)
        {
            return TsfHResult.S_OK;
        }

        try
        {
            return observer.OnStartComposition(pComposition, out pfOk);
        }
        catch (Exception)
        {
            pfOk = 0;
            return TsfHResult.E_FAIL;
        }
    }

    /// <inheritdoc />
    public int OnUpdateComposition(ITfCompositionView pComposition, IntPtr pRangeNew)
    {
        var observer = CompositionObserver;
        if (observer is null)
        {
            return TsfHResult.S_OK;
        }

        try
        {
            return observer.OnUpdateComposition(pComposition, pRangeNew);
        }
        catch (Exception)
        {
            return TsfHResult.E_FAIL;
        }
    }

    /// <inheritdoc />
    public int OnEndComposition(ITfCompositionView pComposition)
    {
        var observer = CompositionObserver;
        if (observer is null)
        {
            return TsfHResult.S_OK;
        }

        try
        {
            return observer.OnEndComposition(pComposition);
        }
        catch (Exception)
        {
            return TsfHResult.E_FAIL;
        }
    }

    /// <summary>暂存编辑批次（TSF 写锁内的原生写入；不是权威）。</summary>
    internal AcpStagingBuffer Staging { get; }

    /// <summary>锁管理器（协议测试可直接观察）。</summary>
    internal TextStoreLockManager Locks => _locks;

    /// <summary>原生 ACP 文本视图（单视图）。</summary>
    internal AcpTextDocument Document => _document;

    /// <summary>
    /// 延迟通知与锁续排的调度器：必须把动作投递到 <c>RequestLock</c> 返回之后的同一线程安全阶段。
    /// </summary>
    /// <remarks>
    /// 未注入时（纯托管协议测试）保留 pending，由调用方在同一线程显式调用
    /// <see cref="FlushDeferredNotifications"/> / <see cref="ContinuePendingLocks"/>。
    /// <b>绝不</b>创建线程池或计时器跨线程回调 COM sink。
    /// </remarks>
    internal Action<Action>? UiDispatcher { get; set; }

    /// <summary>当前视图 cookie（单视图）。</summary>
    internal uint ViewCookie => _viewCookie;

    /// <summary>原生代数（每次失效/重置递增）。</summary>
    internal long NativeGeneration
    {
        get
        {
            lock (_stateGate)
            {
                return _nativeGeneration;
            }
        }
    }

    /// <summary>当前 sink 掩码。</summary>
    internal uint SinkMask
    {
        get
        {
            lock (_stateGate)
            {
                return _sinkMask;
            }
        }
    }

    /// <summary>是否已安装 sink。</summary>
    internal bool HasSink
    {
        get
        {
            lock (_stateGate)
            {
                return _sink is not null;
            }
        }
    }

    /// <summary>是否正在向 sink 发送通知的同步栈内（重入诊断与保护用）。</summary>
    internal int NotificationDepth => _notificationDepth;

    /// <summary>是否已释放。</summary>
    internal bool IsDisposed => _disposed;

    /// <summary>绑定真实窗口句柄（<c>GetWnd</c> 与显示表面查询用）。</summary>
    /// <summary>
    /// 绑定真实窗口句柄（<c>GetWnd</c> 与显示表面查询用）。
    /// </summary>
    /// <remarks><b>R4：</b>该入口不经 Guard，必须自带线程/释放保护；拒绝时保持原值。</remarks>
    /// <returns>确实更新了句柄时 true；跨线程/已释放时 false。</returns>
    internal bool AttachWindow(IntPtr hwnd)
    {
        if (!IsOnOwnerThread)
        {
            Interlocked.Increment(ref _crossThreadRejected);
            return false;
        }

        lock (_stateGate)
        {
            if (_disposed)
            {
                return false;
            }

            _hwnd = hwnd;
            return true;
        }
    }

    /// <summary>
    /// 以权威文档重建 ACP 文本视图（应用侧外部更新；选区归零）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这是<b>应用→原生</b>方向的权威同步：<b>只</b>允许在所属 UI 线程且<b>未持锁</b>时调用
    /// （官方禁止在 <c>RequestLock</c> 内修改文档）。持锁/跨线程/已释放时拒绝并保持原状。
    /// </para>
    /// <para>本方法只更新原生视图，不写绑定；通知由调用方按需通过
    /// <see cref="NotifyTextChange"/> 发出（或使用 <see cref="ApplyAuthoritativeUpdate"/> 自动通知）。</para>
    /// </remarks>
    /// <returns>确实重置了视图时 true；非安全阶段/跨线程/已释放时 false。</returns>
    internal bool ResetAuthoritativeText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (!IsOnOwnerThread)
        {
            Interlocked.Increment(ref _crossThreadRejected);
            return false;
        }

        // R5：与 ApplyAuthoritativeUpdate 等价的安全阶段保护
        // （RequestLock 栈内 / 通知回调内 / 持锁一律拒绝）。
        if (!IsSafePhase)
        {
            return false;
        }

        lock (_stateGate)
        {
            if (_disposed)
            {
                return false;
            }

            _document.Reset(text);
            _nativeGeneration++;
            Staging.AdvanceGeneration();
            _deferred.Clear();
            return true;
        }
    }

    // -----------------------------------------------------------------------
    // 权威快照的显式外部更新（防陈旧代数）
    // -----------------------------------------------------------------------

    /// <summary>
    /// 用一份<b>权威</b>快照重建原生 ACP 视图（R4/R5），并在成功后按 mask 通知。
    /// </summary>
    /// <param name="snapshot">核心确认的权威快照（来自 P2a 的真实回读）。</param>
    /// <param name="expectedNativeGeneration">
    /// 调用方捕获的原生代数；与当前不一致时<b>拒绝</b>（陈旧会话不得覆盖新会话）。
    /// </param>
    /// <returns>确实应用了更新时 true；代数不符、非安全阶段、已释放或无效时 false。</returns>
    /// <remarks>
    /// <para>
    /// <b>R4 完整快照：</b>组合中时原生视图必须呈现<b>显示镜像</b>（<c>前缀 + 预编辑 + 后缀</c>）并把原生
    /// caret 映射到组合内位置；非组合时才用已提交文本与真实选区方向。绝不静默丢弃活动预编辑。
    /// </para>
    /// <para>
    /// <b>R5 安全阶段：</b>应用更新<b>不得</b>在 <c>RequestLock</c> 调用栈内或通知回调内修改文档
    /// （官方禁止在 <c>RequestLock</c> 内改文档或发 <c>OnTextChange</c>）。仅当
    /// <see cref="IsSafePhase"/> 为真时才应用；否则<b>拒绝</b>并保持原状，由调用方在
    /// <c>RequestLock</c> 真正返回后的安全阶段重试。
    /// </para>
    /// <para>
    /// <b>通知：</b>真实变化才按当前 mask 产生文本/选区通知（零变化不重复通知）；
    /// 通知与更新是同一次同步操作，且在安全阶段发送。
    /// </para>
    /// <para><b>不写绑定、不推断组合结局。</b></para>
    /// </remarks>
    internal bool ApplyAuthoritativeUpdate(TextInputAuthoritativeSnapshot snapshot, long expectedNativeGeneration)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!IsOnOwnerThread)
        {
            Interlocked.Increment(ref _crossThreadRejected);
            return false;
        }

        // R5：只有安全阶段才允许应用修改（RequestLock 栈内 / 通知回调内 / 持锁一律拒绝）。
        if (!IsSafePhase)
        {
            return false;
        }

        TextInputApplyResultChange? notification = null;
        lock (_stateGate)
        {
            if (_disposed || _nativeGeneration != expectedNativeGeneration)
            {
                return false;
            }

            // R4：组合中用显示镜像；否则用已提交文本。
            var composing = snapshot.IsComposing;
            var targetText = composing ? snapshot.DisplayText : snapshot.Committed.Text;

            // 组合内 caret 映射到显示串位置；非组合用真实选区（保留方向）。
            TextSelection targetSelection;
            if (composing)
            {
                var preeditCaret = _snapshotPreeditCaretFallback(snapshot);
                var caret = Math.Clamp(snapshot.DisplayPreeditStart + preeditCaret, 0, targetText.Length);
                targetSelection = TextSelection.Caret(caret);
            }
            else
            {
                var committed = snapshot.Committed.Selection;
                var max = targetText.Length;
                targetSelection = new TextSelection(
                    Math.Clamp(committed.Anchor, 0, max),
                    Math.Clamp(committed.Active, 0, max));
            }

            var textChanged = _document.Text != targetText;

            // R5：零变化判定必须比较<b>规范化范围</b>与<b>方向</b>。
            // _document.Selection 是规范化后的 (min,max)，直接与 Anchor/Active 比较会把
            // “反向选区再次设置”误判为变化，从而错误推进代数并发通知。
            var targetStart = Math.Min(targetSelection.Anchor, targetSelection.Active);
            var targetEnd = Math.Max(targetSelection.Anchor, targetSelection.Active);
            var targetBackward = targetSelection.Active < targetSelection.Anchor;
            var selectionChanged = _document.Selection != (targetStart, targetEnd)
                || _document.IsSelectionBackward != targetBackward;

            if (!textChanged && !selectionChanged)
            {
                // 零变化：不重置、不推进代数、不通知。
                return true;
            }

            var previousLength = _document.Length;
            if (textChanged)
            {
                _document.Reset(targetText);
            }

            // 必须用带方向的 TextSelection 重载：int 重载会丢失反向语义。
            _document.TrySetSelection(targetSelection);

            // 权威更新推进代数：此前收集的暂存批次不再适用于新状态。
            _nativeGeneration++;
            Staging.AdvanceGeneration();

            if (textChanged)
            {
                // 应用侧修改：以整段替换通知（TSF 需要知道文档已变）。
                notification = new TextInputApplyResultChange(
                    new AcpTextChange(0, previousLength, targetText.Length, _document.Version),
                    selectionChanged);
            }
            else if (selectionChanged)
            {
                notification = new TextInputApplyResultChange(default, true);
            }
        }

        // R5：通知与更新属于同一次同步操作，且在锁外发送（此时锁类型必为 0）。
        if (notification is { } pending)
        {
            // 捕获本次通知序列所属的 sink 连接代数：若文本通知内 sink 被卸载/替换，
            // 后续选区通知不得发给新连接（避免把旧连接的尾部通知泄漏给替换后的 sink）。
            long generation;
            lock (_stateGate)
            {
                generation = _sinkGeneration;
            }

            if (pending.TextChanged)
            {
                NotifyTextChange(0, pending.Change);
            }

            if (pending.SelectionChanged && IsSameSinkGeneration(generation))
            {
                NotifySelectionChange();
            }
        }

        return true;
    }

    /// <summary>该 sink 连接代数是否仍是当前连接（未被卸载/替换）。</summary>
    private bool IsSameSinkGeneration(long generation)
    {
        lock (_stateGate)
        {
            return _sinkGeneration == generation && _sink is not null;
        }
    }

    /// <summary>取快照的组合内 caret；无预编辑时退回预编辑终点。</summary>
    private static int _snapshotPreeditCaretFallback(TextInputAuthoritativeSnapshot snapshot)
    {
        var caret = snapshot.PreeditCaret;
        var length = snapshot.Preedit.Length;
        return caret < 0 ? length : Math.Clamp(caret, 0, length);
    }

    /// <summary>一次权威更新的待发送通知（文本 + 选区）。</summary>
    private readonly record struct TextInputApplyResultChange(AcpTextChange Change, bool SelectionChanged)
    {
        public bool TextChanged => Change.Version != 0;
    }

    /// <summary>读取当前原生 ACP 文本视图（供 owner 观察器对照；不含任何绑定写入）。</summary>
    /// <remarks>
    /// 端点已规范化（<c>start &lt;= end</c>），<b>不</b>携带方向。需要方向时用
    /// <see cref="ReadDirectedSelection"/>——反向选择与“caret 在左端”在规范化范围里无法区分。
    /// </remarks>
    internal (string Text, int SelectionStart, int SelectionEnd, long Version) ReadAuthoritativeDocument()
    {
        lock (_stateGate)
        {
            var (start, end) = _document.Selection;
            return (_document.Text, start, end, _document.Version);
        }
    }

    /// <summary>
    /// 读取当前原生选区的<b>有向</b>形态（锚点/活动端，保留反向）。
    /// </summary>
    /// <remarks>
    /// 原生完成事务必须冻结这个值：<see cref="ReadAuthoritativeDocument"/> 只给排序后的端点，
    /// 用它替代会丢掉方向（反向选中 3→1 与 caret 在 1 不可区分）。
    /// </remarks>
    internal TextSelection ReadDirectedSelection()
    {
        lock (_stateGate)
        {
            return _document.DirectedSelection;
        }
    }

    // -----------------------------------------------------------------------
    // ITextStoreACP
    // -----------------------------------------------------------------------

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <b>R3（官方契约）：</b>以<b>同一 COM identity</b> 再次注册是“更新 dwMask”的请求，必须成功
    /// （S_OK）且<b>不</b>额外 AddRef；不同 sink 对象在旧 sink 移除前失败。
    /// </para>
    /// <para>更新 mask 后，后续通知与已排队的延迟通知都使用新 mask。</para>
    /// </remarks>
    public int AdviseSink(Guid riid, object punk, uint dwMask)
        => Guard(nameof(AdviseSink), () =>
        {
            if (riid != TsfIids.TextStoreAcpSink)
            {
                return TsfHResult.TS_E_NOINTERFACE;
            }

            if (punk is not ITextStoreACPSink sink)
            {
                return TsfHResult.E_NOINTERFACE;
            }

            lock (_stateGate)
            {
                if (_disposed)
                {
                    return TsfHResult.E_FAIL;
                }

                // 同一 identity：只更新 mask，不额外持有引用（官方：不要 AddRef）。
                if (_sink is not null && SameComIdentity(punk, _sinkPunk))
                {
                    _sinkMask = dwMask;
                    return TsfHResult.S_OK;
                }

                // 不同 sink：单连接点，旧 sink 移除前失败。
                if (_sink is not null)
                {
                    return TsfHResult.CONNECT_E_ADVISELIMIT;
                }

                _sink = sink;
                _sinkPunk = punk;
                _sinkMask = dwMask;
                _sinkGeneration++;
                _locks.HasSink = true;

                // 官方：通过 sink 的 QI 取得 ITextStoreACPServices（用于 CreateRange 等）。
                TryCaptureServicesLocked(punk);
            }

            return TsfHResult.S_OK;
        });

    /// <inheritdoc />
    /// <remarks>
    /// <b>R3（官方契约）：</b>没有活动连接时返回 <see cref="TsfHResult.CONNECT_E_NOCONNECTION"/>；
    /// identity 不匹配时返回 <see cref="TsfHResult.E_INVALIDARG"/>（官方未规定该情形，按“指定 sink 无效”处理）。
    /// </remarks>
    public int UnadviseSink(object punk)
        => Guard(nameof(UnadviseSink), () =>
        {
            bool removed;
            bool hadConnection;
            lock (_stateGate)
            {
                hadConnection = _sink is not null;
                removed = hadConnection && SameComIdentity(punk, _sinkPunk);
                if (removed)
                {
                    // 先撤销引用与排队工作，再返回：之后绝不再回调旧 sink。
                    _sink = null;
                    _sinkPunk = null;
                    _sinkMask = 0;
                    _sinkGeneration++;
                    _locks.HasSink = false;
                    _deferred.Clear();
                    _flushScheduled = false;
                    _continuationScheduled = false;
                }
            }

            if (removed)
            {
                _locks.ClearPending();
                ReleaseServices();
                return TsfHResult.S_OK;
            }

            // 没有活动 sink：官方 CONNECT_E_NOCONNECTION。
            return hadConnection ? TsfHResult.E_INVALIDARG : TsfHResult.CONNECT_E_NOCONNECTION;
        });

    /// <inheritdoc />
    public int RequestLock(uint dwLockFlags, out int phrSession)
    {
        var session = 0;
        int hr;
        phrSession = TsfHResult.E_FAIL;

        // 作用域覆盖<b>整个方法体，含尾部调度</b>：调度可以调用 dispatcher（排队型 dispatcher 需要
        // 拿到回调入队），但真正的 sink 执行要求作用域已退出。于是：
        //   · 内联 dispatcher 在尾部同步执行时仍被拒绝（通知不在 RequestLock 栈内）；
        //   · 排队型 dispatcher 的回调在方法返回后执行，能正常发送。
        Interlocked.Increment(ref _requestScopeActive);
        try
        {
            Interlocked.Increment(ref _requestDepth);
            try
            {
                hr = Guard(nameof(RequestLock), () => _locks.RequestLock(dwLockFlags, out session));
                phrSession = session;
            }
            catch (Exception)
            {
                phrSession = TsfHResult.E_FAIL;
                hr = TsfHResult.E_FAIL;
            }
            finally
            {
                Interlocked.Decrement(ref _requestDepth);
            }

            if (hr >= 0)
            {
                ScheduleFlush();
                if (_locks.PendingCount > 0)
                {
                    ScheduleLockContinuation();
                }
            }

            return hr;
        }
        finally
        {
            Interlocked.Decrement(ref _requestScopeActive);
        }
    }

    /// <inheritdoc />
    public int GetStatus(out TsStatus pdcs)
    {
        // R4：本入口不经 Guard，必须自带同样的线程/释放保护，且输出先初始化。
        pdcs = default;
        if (!IsOnOwnerThread)
        {
            Interlocked.Increment(ref _crossThreadRejected);
            return TsfHResult.E_FAIL;
        }

        if (_disposed)
        {
            // 释放后不再对外声明任何状态。
            return TsfHResult.E_FAIL;
        }

        // 非只读、非加载；静态位：单选区、无 region。
        pdcs = new TsStatus { DynamicFlags = 0, StaticFlags = 0 };
        return TsfHResult.S_OK;
    }

    /// <inheritdoc />
    public int QueryInsert(int acpTestStart, int acpTestEnd, uint cch,
        out int pacpResultStart, out int pacpResultEnd)
    {
        var start = 0;
        var end = 0;
        var hr = Guard(nameof(QueryInsert), () =>
        {
            var lockHr = RequireRead();
            if (lockHr < 0)
            {
                return lockHr;
            }

            // 溢出与边界：cch 超出 int 范围或范围非法都拒绝。
            if (!_document.IsValidRange(acpTestStart, acpTestEnd) || cch > int.MaxValue)
            {
                return TsfHResult.TS_E_INVALIDPOS;
            }

            // 单行输入不支持内嵌对象：插入点即测试范围。
            start = acpTestStart;
            end = acpTestEnd;
            return TsfHResult.S_OK;
        });

        pacpResultStart = start;
        pacpResultEnd = end;
        return hr;
    }

    /// <inheritdoc />
    public int GetSelection(uint ulIndex, uint ulCount, IntPtr pSelection, out uint pcFetched)
    {
        var fetched = 0u;
        var hr = Guard(nameof(GetSelection), () =>
        {
            var lockHr = RequireRead();
            if (lockHr < 0)
            {
                return lockHr;
            }

            if (ulCount == 0)
            {
                return TsfHResult.S_OK;
            }

            // 单视图单选区：只有索引 0 或 TS_DEFAULT_SELECTION 有效。
            if (ulIndex != 0 && ulIndex != TsfFlags.TS_DEFAULT_SELECTION)
            {
                return TsfHResult.S_OK;
            }

            var (start, end) = _document.Selection;
            var selection = new TsSelectionAcp
            {
                AcpStart = start,
                AcpEnd = end,
                Style = new TsSelectionStyle
                {
                    // 方向由 TS_AE_START/END 表达（R4：不得把反向选区压成正向）。
                    ActiveSelEnd = start == end
                        ? TsfFlags.TS_AE_NONE
                        : _document.IsSelectionBackward ? TsfFlags.TS_AE_START : TsfFlags.TS_AE_END,
                    InterimChar = 0,
                },
            };

            if (pSelection != IntPtr.Zero)
            {
                Marshal.StructureToPtr(selection, pSelection, false);
                fetched = 1;
            }

            return TsfHResult.S_OK;
        });

        pcFetched = fetched;
        return hr;
    }

    /// <inheritdoc />
    public int SetSelection(uint ulCount, IntPtr pSelection)
        => Guard(nameof(SetSelection), () =>
        {
            var lockHr = RequireWrite();
            if (lockHr < 0)
            {
                return lockHr;
            }

            if (ulCount != 1 || pSelection == IntPtr.Zero)
            {
                return TsfHResult.E_INVALIDARG;
            }

            var selection = Marshal.PtrToStructure<TsSelectionAcp>(pSelection);
            lock (_stateGate)
            {
                // 真实方向来自 ACP 的 ActiveSelEnd：TS_AE_START 表示活动端在起点（反向选择）。
                // 只传 AcpStart/AcpEnd 会把方向丢掉，反向选中 3→1 会退化成 caret 在 1。
                var directed = selection.Style.ActiveSelEnd == TsfFlags.TS_AE_START
                    ? new TextSelection(selection.AcpEnd, selection.AcpStart)
                    : new TextSelection(selection.AcpStart, selection.AcpEnd);

                if (!_document.TrySetSelection(directed))
                {
                    return TsfHResult.TS_E_INVALIDPOS;
                }

                // 响应 SetSelection 时不回送 OnSelectionChange（官方约定）。
                return TsfHResult.S_OK;
            }
        });

    /// <inheritdoc />
    public int GetText(int acpStart, int acpEnd, IntPtr pchPlain, uint cchPlainReq,
        out uint pcchPlainRet, IntPtr prgRunInfo, uint cRunInfoReq,
        out uint pcRunInfoRet, out int pacpNext)
    {
        var plainRet = 0u;
        var runRet = 0u;
        var next = acpStart;
        var hr = Guard(nameof(GetText), () =>
        {
            var lockHr = RequireRead();
            if (lockHr < 0)
            {
                return lockHr;
            }

            // 非零容量配空缓冲区：明确拒绝，不报告“写入了不存在的缓冲区”。
            if (cchPlainReq > 0 && pchPlain == IntPtr.Zero)
            {
                return TsfHResult.E_INVALIDARG;
            }

            if (cRunInfoReq > 0 && prgRunInfo == IntPtr.Zero)
            {
                return TsfHResult.E_INVALIDARG;
            }

            // acpEnd == -1 表示读到文档末尾（官方语义）。
            var end = acpEnd == -1 ? _document.Length : acpEnd;
            if (!_document.IsValidRange(acpStart, end))
            {
                return TsfHResult.TS_E_INVALIDPOS;
            }

            var available = end - acpStart;
            var plainCount = cchPlainReq == 0
                ? 0
                : (int)Math.Min(cchPlainReq, (uint)available);
            if (plainCount > 0)
            {
                var chars = _document.GetText(acpStart, acpStart + plainCount).ToCharArray();
                Marshal.Copy(chars, 0, pchPlain, plainCount);
            }

            plainRet = (uint)plainCount;

            // run-only 调用（pchPlain=NULL, cchPlainReq=0）必须覆盖请求范围并推进 pacpNext，
            // 否则调用方无法扫描文档。
            var runChars = cchPlainReq == 0 ? available : plainCount;
            if (cRunInfoReq > 0 && runChars > 0)
            {
                var run = new TsRunInfo { Count = (uint)runChars, Type = TsRunType.Plain };
                Marshal.StructureToPtr(run, prgRunInfo, false);
                runRet = 1;
            }

            next = acpStart + runChars;
            return TsfHResult.S_OK;
        });

        pcchPlainRet = plainRet;
        pcRunInfoRet = runRet;
        pacpNext = next;
        return hr;
    }

    /// <inheritdoc />
    public int SetText(uint dwFlags, int acpStart, int acpEnd, IntPtr pchText, uint cch,
        out TsTextChange pChange)
    {
        var change = default(TsTextChange);
        var hr = Guard(nameof(SetText), () =>
        {
            var lockHr = RequireWrite();
            if (lockHr < 0)
            {
                return lockHr;
            }

            if (cch > int.MaxValue || (cch > 0 && pchText == IntPtr.Zero))
            {
                return TsfHResult.E_INVALIDARG;
            }

            var text = cch == 0 ? string.Empty : Marshal.PtrToStringUni(pchText, (int)cch) ?? string.Empty;
            lock (_stateGate)
            {
                var correction = (dwFlags & TsfFlags.TS_ST_CORRECTION) != 0;
                var result = ApplyTsfChangeLocked(acpStart, acpEnd, text, correction);
                if (result >= 0)
                {
                    change = new TsTextChange
                    {
                        AcpStart = acpStart,
                        AcpOldEnd = acpEnd,
                        AcpNewEnd = acpStart + text.Length,
                    };
                }

                return result;
            }
        });

        pChange = change;
        return hr;
    }

    /// <inheritdoc />
    public int GetFormattedText(int acpStart, int acpEnd, out IntPtr ppDataObject)
    {
        ppDataObject = IntPtr.Zero;
        return TsfHResult.E_NOTIMPL;
    }

    /// <inheritdoc />
    public int GetEmbedded(int acpPos, IntPtr rguidService, IntPtr riid, out IntPtr ppunk)
    {
        ppunk = IntPtr.Zero;
        return TsfHResult.TS_E_NOOBJECT;
    }

    /// <inheritdoc />
    public int QueryInsertEmbedded(IntPtr pguidService, IntPtr pFormatEtc, out int pfInsertable)
    {
        // 显式报告“不支持内嵌”：S_OK + false（不是 E_NOTIMPL）。
        pfInsertable = 0;
        return TsfHResult.S_OK;
    }

    /// <inheritdoc />
    public int InsertEmbedded(uint dwFlags, int acpStart, int acpEnd, object pDataObject,
        out TsTextChange pChange)
    {
        pChange = default;
        return TsfHResult.E_NOTIMPL;
    }

    /// <inheritdoc />
    public int InsertTextAtSelection(uint dwFlags, IntPtr pchText, uint cch,
        IntPtr pacpStart, IntPtr pacpEnd, IntPtr pChange)
        => Guard(nameof(InsertTextAtSelection), () =>
        {
            var lockHr = RequireWrite();
            if (lockHr < 0)
            {
                return lockHr;
            }

            if (cch > int.MaxValue || (cch > 0 && pchText == IntPtr.Zero))
            {
                return TsfHResult.E_INVALIDARG;
            }

            lock (_stateGate)
            {
                var (start, end) = _document.Selection;

                // 查询模式：只回报将插入的位置，不修改文本。
                if ((dwFlags & TsfFlags.TS_IAS_QUERYONLY) != 0)
                {
                    WriteOptionalInt32(pacpStart, start);
                    WriteOptionalInt32(pacpEnd, end);
                    return TsfHResult.S_OK;
                }

                var text = cch == 0 ? string.Empty : Marshal.PtrToStringUni(pchText, (int)cch) ?? string.Empty;
                var hr = ApplyTsfChangeLocked(start, end, text, correction: false);
                if (hr < 0)
                {
                    return hr;
                }

                // 插入后把选区移到插入文本末尾，后续预编辑/普通字符才不会反向堆叠。
                // 这是响应 InsertTextAtSelection 的内部选区调整，不回送 OnSelectionChange。
                _document.TrySetSelection(start + text.Length, start + text.Length);

                WriteOptionalInt32(pacpStart, start);
                WriteOptionalInt32(pacpEnd, start + text.Length);
                if (pChange != IntPtr.Zero)
                {
                    var change = new TsTextChange
                    {
                        AcpStart = start,
                        AcpOldEnd = end,
                        AcpNewEnd = start + text.Length,
                    };
                    Marshal.StructureToPtr(change, pChange, false);
                }

                return TsfHResult.S_OK;
            }
        });

    /// <inheritdoc />
    public int InsertEmbeddedAtSelection(uint dwFlags, object pDataObject, IntPtr pacpStart,
        IntPtr pacpEnd, IntPtr pChange)
        => TsfHResult.E_NOTIMPL;

    /// <inheritdoc />
    public int RequestSupportedAttrs(uint dwFlags, uint cFilterAttrs, IntPtr paFilterAttrs)
        => TsfHResult.E_NOTIMPL;

    /// <inheritdoc />
    public int RequestAttrsAtPosition(int acpPos, uint cFilterAttrs, IntPtr paFilterAttrs, uint dwFlags)
        => TsfHResult.E_NOTIMPL;

    /// <inheritdoc />
    public int RequestAttrsTransitioningAtPosition(int acpPos, uint cFilterAttrs,
        IntPtr paFilterAttrs, uint dwFlags)
        => TsfHResult.E_NOTIMPL;

    /// <inheritdoc />
    public int FindNextAttrTransition(int acpStart, int acpHalt, uint cFilterAttrs,
        IntPtr paFilterAttrs, uint dwFlags, out int pacpNext, out int pfFound, out int plFoundOffset)
    {
        pacpNext = 0;
        pfFound = 0;
        plFoundOffset = 0;
        return TsfHResult.E_NOTIMPL;
    }

    /// <inheritdoc />
    public int RetrieveRequestedAttrs(uint ulCount, IntPtr paAttrVals, out uint pcFetched)
    {
        pcFetched = 0;
        return TsfHResult.S_OK;
    }

    /// <inheritdoc />
    public int GetEndACP(out int pacp)
    {
        var end = 0;
        var hr = Guard(nameof(GetEndACP), () =>
        {
            var lockHr = RequireRead();
            if (lockHr < 0)
            {
                return lockHr;
            }

            end = _document.Length;
            return TsfHResult.S_OK;
        });

        pacp = end;
        return hr;
    }

    /// <inheritdoc />
    public int GetActiveView(out uint pvcView)
    {
        // R4：自带线程/释放保护；输出先初始化（拒绝时为 0）。
        pvcView = 0;
        if (!IsOnOwnerThread)
        {
            Interlocked.Increment(ref _crossThreadRejected);
            return TsfHResult.E_FAIL;
        }

        if (_disposed)
        {
            return TsfHResult.E_FAIL;
        }

        pvcView = _viewCookie;
        return TsfHResult.S_OK;
    }

    /// <inheritdoc />
    public int GetACPFromPoint(uint vcView, IntPtr ptScreen, uint dwFlags, out int pacp)
    {
        var result = 0;
        var hr = Guard(nameof(GetACPFromPoint), () =>
        {
            var lockHr = RequireRead();
            if (lockHr < 0)
            {
                return lockHr;
            }

            if (vcView != _viewCookie)
            {
                return TsfHResult.TF_E_INVALIDVIEW;
            }

            if (ptScreen == IntPtr.Zero)
            {
                return TsfHResult.TS_E_INVALIDPOINT;
            }

            // 当前不实现命中测试（字符几何已用于 GetTextExt，但反向命中映射尚未验收）：
            // 如实报告无布局，绝不猜测位置。
            return TsfHResult.TS_E_NOLAYOUT;
        });

        pacp = result;
        return hr;
    }

    /// <inheritdoc />
    public int GetTextExt(uint vcView, int acpStart, int acpEnd, out TsfRect prc, out int pfClipped)
    {
        var rect = default(TsfRect);
        var clipped = 0;
        var hr = Guard(nameof(GetTextExt), () =>
        {
            // GetTextExt 需要有效读锁（与 GetScreenExt 不同）。
            var lockHr = RequireRead();
            if (lockHr < 0)
            {
                return lockHr;
            }

            if (vcView != _viewCookie)
            {
                return TsfHResult.TF_E_INVALIDVIEW;
            }

            var end = acpEnd == -1 ? _document.Length : acpEnd;
            if (!_document.IsValidRange(acpStart, end) || end < acpStart)
            {
                return TsfHResult.TS_E_INVALIDPOS;
            }

            // 零长度范围是<b>合法的 caret 查询</b>（TSF 用 acpStart==acpEnd 定位插入点/候选锚点）。
            // 已验收原型明确支持该路径（AcpLayout.TryGetRangeRect 的 CaretRectUnclipped 分支），
            // 因此这里不拒绝、也不伪造零矩形：交给几何提供方按 caret 语义返回真实矩形。
            // 注意：SDK 中<b>没有</b> TS_E_INVALIDARG 常量；官方页面的 start==end 限制不改变
            // 已验收的兼容行为，本批保留支持。

            // 同一读锁内冻结只读身份：provider 只比较它和不可变缓存，不访问活动客户端。
            var identity = new AcpGeometryQueryIdentity(
                _nativeGeneration, _document.Version, _document.Text);
            if (!Geometry.TryGetTextRect(acpStart, end, in identity,
                    out var providerRect, out var providerClipped, out var knownEmpty))
            {
                // 未绑定/失效：显式无布局，绝不返回零矩形冒充成功。
                return TsfHResult.TS_E_NOLAYOUT;
            }

            if (knownEmpty)
            {
                // 官方语义：已布局但完全不可见（含最小化）→ S_OK + 全零 RECT + clipped=1。
                rect = default;
                clipped = 1;
                return TsfHResult.S_OK;
            }

            // 提供方给出的已是<b>屏幕物理像素</b>（含窗口屏幕原点）；store 原样返回，不二次换算。
            rect = providerRect;
            clipped = providerClipped ? 1 : 0;
            return TsfHResult.S_OK;
        });

        prc = rect;
        pfClipped = clipped;
        return hr;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <b>与 GetTextExt 的区别：</b>显示表面查询<b>不要求</b>读锁，也<b>不依赖</b>文本布局版本
    /// （已验证：官方样例 <c>GetScreenExt</c> 明确注明 no lock is necessary）。非法 view 返回
    /// <c>E_INVALIDARG</c>（不是 <c>TF_E_INVALIDVIEW</c>），输出已清零。
    /// </remarks>
    public int GetScreenExt(uint vcView, out TsfRect prc)
    {
        var rect = default(TsfRect);
        var hr = Guard(nameof(GetScreenExt), () =>
        {
            if (vcView != _viewCookie)
            {
                return TsfHResult.E_INVALIDARG;
            }

            if (!Geometry.TryGetScreenRect(out var surface, out var surfaceHr))
            {
                // 未绑定：显式不可用（零矩形 + 负 HRESULT），不用旧坐标伪装。
                return surfaceHr < 0 ? surfaceHr : TsfHResult.TS_E_NOLAYOUT;
            }

            if (surfaceHr < 0)
            {
                return surfaceHr;
            }

            rect = surface;
            return TsfHResult.S_OK;
        });

        prc = rect;
        return hr;
    }

    /// <inheritdoc />
    public int GetWnd(uint vcView, out IntPtr phwnd)
    {
        // R4：自带线程/释放保护；输出先初始化。
        phwnd = IntPtr.Zero;
        if (!IsOnOwnerThread)
        {
            Interlocked.Increment(ref _crossThreadRejected);
            return TsfHResult.E_FAIL;
        }

        if (_disposed)
        {
            return TsfHResult.E_FAIL;
        }

        if (vcView != _viewCookie)
        {
            phwnd = IntPtr.Zero;
            return TsfHResult.TF_E_INVALIDVIEW;
        }

        lock (_stateGate)
        {
            phwnd = _hwnd;
        }

        return TsfHResult.S_OK;
    }

    // -----------------------------------------------------------------------
    // 内部：TSF 写入（只暂存，不写绑定）
    // -----------------------------------------------------------------------

    /// <summary>
    /// 应用一次 TSF 写锁内的文本写入：更新原生 ACP 视图并记录<b>暂存</b>批次。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>不写 Binding、不推断结果/取消</b>：本方法只更新原生视图并记录事实。结果/取消分类属于下一批
    /// owner 观察器职责。
    /// </para>
    /// <para>
    /// <b>不回送自身通知</b>：TSF 发起的写入由 TSF 自己知道，绝不向同一 sink 回送 <c>OnTextChange</c>
    /// （官方约定；否则会造成回声/重复插入）。
    /// </para>
    /// </remarks>
    private int ApplyTsfChangeLocked(int start, int end, string text, bool correction)
    {
        if (!_document.TryReplace(start, end, text, AcpEditOrigin.Tsf, out var change))
        {
            return TsfHResult.TS_E_INVALIDPOS;
        }

        var kind = change.NewEnd > change.OldEnd || change.NewEnd > start
            ? AcpStagedEditKind.Insert
            : AcpStagedEditKind.Delete;
        var inserted = change.NewEnd > change.Start
            ? _document.GetText(change.Start, change.NewEnd)
            : string.Empty;

        // 只进入暂存：绝不写 Binding、绝不推断提交/取消。
        Staging.Record(kind, change, inserted, correction);

        // TSF 来源的写入不回送 OnTextChange（TSF 已知）。
        return TsfHResult.S_OK;
    }

    // -----------------------------------------------------------------------
    // 内部：通知（mask 门控 + 延迟到锁外安全阶段）
    // -----------------------------------------------------------------------

    /// <summary>
    /// 应用侧（权威）文本变化后按 mask 通知 sink。
    /// </summary>
    /// <param name="flags">通知标志（<c>TS_TC_CORRECTION</c> 等）。</param>
    /// <param name="change">真实变更。</param>
    /// <remarks>
    /// 持锁时入队，释放后由 <see cref="FlushDeferredNotifications"/> 在安全阶段发送；
    /// 已卸载 sink 时丢弃，绝不回调旧 sink。
    /// </remarks>
    internal void NotifyTextChange(uint flags, AcpTextChange change)
    {
        // 审计：跨线程不得发 sink 通知（不触碰 sink、不排队）。
        if (!IsOnOwnerThread)
        {
            Interlocked.Increment(ref _crossThreadRejected);
            return;
        }

        lock (_stateGate)
        {
            if (_sink is null || (_sinkMask & TsfFlags.TS_AS_TEXT_CHANGE) == 0)
            {
                return;
            }

            if (_locks.LockType != 0)
            {
                // 持锁时不能回送 sink 通知（会重入并拿到 TS_E_NOLOCK）：延迟到锁外。
                _deferred.Add(new DeferredNotification(NotificationKind.TextChange, flags, change));
                return;
            }

            SendTextChangeLocked(flags, change);
        }
    }

    /// <summary>应用侧选区变化后按 mask 通知 sink（同样延迟到锁外）。</summary>
    internal void NotifySelectionChange()
    {
        // 审计：跨线程不得发 sink 通知。
        if (!IsOnOwnerThread)
        {
            Interlocked.Increment(ref _crossThreadRejected);
            return;
        }

        lock (_stateGate)
        {
            if (_sink is null || (_sinkMask & TsfFlags.TS_AS_SEL_CHANGE) == 0)
            {
                return;
            }

            if (_locks.LockType != 0)
            {
                _deferred.Add(new DeferredNotification(NotificationKind.SelectionChange, 0, default));
                return;
            }

            SendSelectionChangeLocked();
        }
    }

    /// <summary>
    /// 几何代次真正变化后通知 sink（<c>TS_AS_LAYOUT_CHANGE</c> / <c>TS_LC_CHANGE</c>）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>只从安全阶段调用：</b>持锁或未处于安全阶段时只入队，由现有锁外/safe-phase flush 发送；
    /// 绝不在这里排版、重建或生成帧，也不借调度器做周期轮询。
    /// </para>
    /// <para>
    /// <b>mask 与连接生命周期：</b>延迟项不单独携带 sink 代次；<c>UnadviseSink</c>/<c>Dispose</c> 会清空整个
    /// 延迟队列，且已有连接时不允许换新 sink（<c>CONNECT_E_ADVISELIMIT</c>），因此 flush 时队列项必然
    /// 属于当前连接。发送仍按当次锁内的 sink 与 <c>TS_AS_LAYOUT_CHANGE</c> mask 门控；已撤销/未订阅时丢弃，
    /// 绝不回调旧 sink，也不把旧连接排队的项送给替换后的 sink。
    /// </para>
    /// </remarks>
    /// <returns>是否已发送或已排队（调用方据此清除“待通知”标记）。</returns>
    internal bool NotifyLayoutChange()
    {
        if (!IsOnOwnerThread)
        {
            Interlocked.Increment(ref _crossThreadRejected);
            return false;
        }

        lock (_stateGate)
        {
            if (_sink is null || (_sinkMask & TsfFlags.TS_AS_LAYOUT_CHANGE) == 0)
            {
                return false;
            }

            if (_locks.LockType != 0 || !IsSafePhase || Volatile.Read(ref _notificationDepth) > 0)
            {
                _deferred.Add(new DeferredNotification(NotificationKind.LayoutChange, 0, default));
                ScheduleFlush();
                return true;
            }

            SendLayoutChangeLocked();
            return true;
        }
    }

    private void SendLayoutChangeLocked()
    {
        var sink = _sink;
        if (sink is null || (_sinkMask & TsfFlags.TS_AS_LAYOUT_CHANGE) == 0)
        {
            return;
        }

        _notificationDepth++;
        try
        {
            sink.OnLayoutChange(TsfFlags.TS_LC_CHANGE, _viewCookie);
        }
        catch (Exception)
        {
            // sink 抛异常不得穿过 COM 边界。
        }
        finally
        {
            _notificationDepth--;
        }
    }

    private void SendTextChangeLocked(uint flags, in AcpTextChange change)
    {
        var sink = _sink;
        if (sink is null || (_sinkMask & TsfFlags.TS_AS_TEXT_CHANGE) == 0)
        {
            return;
        }

        var native = new TsTextChange
        {
            AcpStart = change.Start,
            AcpOldEnd = change.OldEnd,
            AcpNewEnd = change.NewEnd,
        };

        _notificationDepth++;
        try
        {
            sink.OnTextChange(flags, ref native);
        }
        catch (Exception)
        {
            // sink 抛异常不得穿过 COM 边界。
        }
        finally
        {
            _notificationDepth--;
        }
    }

    private void SendSelectionChangeLocked()
    {
        var sink = _sink;
        if (sink is null || (_sinkMask & TsfFlags.TS_AS_SEL_CHANGE) == 0)
        {
            return;
        }

        _notificationDepth++;
        try
        {
            sink.OnSelectionChange();
        }
        catch (Exception)
        {
        }
        finally
        {
            _notificationDepth--;
        }
    }

    /// <summary>锁释放后按 mask 发送延迟通知；持锁或正在发送时不做任何事。</summary>
    /// <remarks>
    /// <b>R2：</b>非所属 UI 线程调用直接返回（<b>不</b>回调 sink、不跨套间），
    /// 因此错误的 dispatcher 无法把 COM 通知带离原线程。
    /// </remarks>
    internal void FlushDeferredNotifications()
    {
        if (!IsOnOwnerThread)
        {
            Interlocked.Increment(ref _crossThreadRejected);
            return;
        }

        lock (_stateGate)
        {
            _flushScheduled = false;
        }

        // R5：只有真正的安全阶段才发送。仍在 RequestLock 作用域内（内联 dispatcher）或已持锁时
        // 保留 pending，绝不回调 sink。
        if (!IsSafePhase)
        {
            return;
        }

        if (_flushing)
        {
            return;
        }

        _flushing = true;
        try
        {
            while (true)
            {
                DeferredNotification item;
                lock (_stateGate)
                {
                    if (_deferred.Count == 0 || !IsSafePhase || _sink is null)
                    {
                        return;
                    }

                    item = _deferred[0];
                    _deferred.RemoveAt(0);

                    if (item.Kind == NotificationKind.TextChange)
                    {
                        SendTextChangeLocked(item.Flags, item.Change);
                    }
                    else if (item.Kind == NotificationKind.SelectionChange)
                    {
                        SendSelectionChangeLocked();
                    }
                    else
                    {
                        SendLayoutChangeLocked();
                    }
                }
            }
        }
        finally
        {
            _flushing = false;

            bool remaining;
            lock (_stateGate)
            {
                remaining = _deferred.Count > 0;
            }

            if (remaining)
            {
                ScheduleFlush();
            }
        }
    }

    /// <summary>
    /// 把 flush 投递到 <c>RequestLock</c> 真正返回之后的安全阶段（同一线程）。
    /// </summary>
    /// <remarks>无 dispatcher 时保留 pending，由调用方在同一线程显式 flush；绝不跨线程回调 COM sink。</remarks>
    /// <summary>
    /// 调度延迟通知到安全阶段。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>R5 内联 dispatcher：</b>若 <see cref="UiDispatcher"/> 同步内联执行（例如
    /// <c>action =&gt; action()</c>），则执行点仍在 <c>RequestLock</c> 栈内。此时
    /// <see cref="FlushDeferredNotifications"/> 会因非安全阶段而拒绝并<b>再次</b>调用本方法——
    /// 因此这里必须在调度前就判定安全阶段：不安全时<b>只保留 pending</b>、不调用 dispatcher，
    /// 从而既不会递归、也不会丢工作；调用方随后在安全阶段显式 Flush 即可。
    /// </para>
    /// <para>无 dispatcher 时同样只保留 pending（绝不创建线程池/计时器）。</para>
    /// </remarks>
    private void ScheduleFlush()
    {
        lock (_stateGate)
        {
            if (_deferred.Count == 0 || _flushScheduled || _disposed)
            {
                return;
            }

            // 已在调度栈内（内联 dispatcher 递归）：只保留 pending，绝不再调度。
            if (Volatile.Read(ref _dispatchDepth) > 0)
            {
                return;
            }

            // 持锁或正在通知内：只保留 pending（这两个状态即使排队也无法安全发送）。
            if (_locks.LockType != 0 || Volatile.Read(ref _notificationDepth) > 0)
            {
                return;
            }

            _flushScheduled = true;
        }

        var dispatcher = UiDispatcher;
        if (dispatcher is null)
        {
            lock (_stateGate)
            {
                _flushScheduled = false;
            }

            return;
        }

        try
        {
            Interlocked.Increment(ref _dispatchDepth);
            try
            {
                dispatcher(FlushDeferredNotifications);
            }
            finally
            {
                Interlocked.Decrement(ref _dispatchDepth);
            }
        }
        catch (Exception)
        {
            lock (_stateGate)
            {
                _flushScheduled = false;
            }
        }
    }

    private void ScheduleLockContinuation()
    {
        lock (_stateGate)
        {
            if (_continuationScheduled || _locks.PendingCount == 0 || _disposed)
            {
                return;
            }

            // 已在调度栈内（内联 dispatcher 递归）：只保留 pending，绝不再调度。
            if (Volatile.Read(ref _dispatchDepth) > 0)
            {
                return;
            }

            // 仍在外层 RequestLock 作用域内（含尾部调度）：不调度续排，留给后续安全阶段。
            // 这是阻止内联 dispatcher 递归的关键——否则尾部调度会同步进入 ContinuePendingLocks，
            // 而它又调度下一次授予，形成无界递归。
            if (Volatile.Read(ref _requestScopeActive) != 0)
            {
                return;
            }

            _continuationScheduled = true;
        }

        var dispatcher = UiDispatcher;
        if (dispatcher is null)
        {
            lock (_stateGate)
            {
                _continuationScheduled = false;
            }

            return;
        }

        try
        {
            Interlocked.Increment(ref _dispatchDepth);
            try
            {
                dispatcher(ContinuePendingLocks);
            }
            finally
            {
                Interlocked.Decrement(ref _dispatchDepth);
            }
        }
        catch (Exception)
        {
            lock (_stateGate)
            {
                _continuationScheduled = false;
            }
        }
    }

    /// <summary>在锁外安全阶段继续授予已承诺的 pending 读/写升级。</summary>
    /// <remarks><b>R2：</b>非所属 UI 线程调用直接返回，绝不跨线程授予锁或回调 sink。</remarks>
    internal void ContinuePendingLocks()
    {
        if (!IsOnOwnerThread)
        {
            Interlocked.Increment(ref _crossThreadRejected);
            return;
        }

        lock (_stateGate)
        {
            _continuationScheduled = false;
        }

        // 安全阶段要求：仍在外层 RequestLock 作用域内（含其尾部调度）时绝不执行锁续排。
        // 这使内联 dispatcher 的同步递归立即停止，pending 保留给后续安全阶段。
        if (Volatile.Read(ref _requestScopeActive) != 0)
        {
            return;
        }

        // 有界：已在续排栈内（内联 dispatcher 递归）时直接返回，pending 留给后续安全阶段。
        if (Interlocked.Increment(ref _continuationDepth) > MaxContinuationDepth)
        {
            Interlocked.Decrement(ref _continuationDepth);
            return;
        }

        try
        {
            if (_disposed || _locks.LockType != 0)
            {
                ScheduleLockContinuation();
                return;
            }

            if (_locks.DrainPending())
            {
                ScheduleLockContinuation();
            }
        }
        finally
        {
            Interlocked.Decrement(ref _continuationDepth);
        }
    }

    /// <summary>丢弃尚未发送的通知与已排队的锁请求（Unadvise/释放路径）。</summary>
    internal void CancelPendingNotifications()
    {
        lock (_stateGate)
        {
            _deferred.Clear();
            _flushScheduled = false;
            _continuationScheduled = false;
        }

        _locks.ClearPending();
    }

    // -----------------------------------------------------------------------
    // 内部：锁授予
    // -----------------------------------------------------------------------

    private int GrantSinkLock(uint flags)
    {
        ITextStoreACPSink? sink;
        lock (_stateGate)
        {
            sink = _sink;
        }

        if (sink is null)
        {
            return TsfHResult.E_FAIL;
        }

        try
        {
            return sink.OnLockGranted(flags);
        }
        catch (Exception)
        {
            return TsfHResult.E_FAIL;
        }
    }

    // -----------------------------------------------------------------------
    // 内部：辅助
    // -----------------------------------------------------------------------

    private int RequireRead() => _locks.IsLocked(TsfFlags.TS_LF_READ) ? TsfHResult.S_OK : TsfHResult.TS_E_NOLOCK;

    private int RequireWrite() => _locks.IsWriteLocked ? TsfHResult.S_OK : TsfHResult.TS_E_NOLOCK;

    /// <summary>
    /// COM 入口统一守卫（R2）：所属 UI 线程校验 + 释放校验 + 异常边界。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>线程归属：</b>ACP store 属于创建它的 UI 线程（STA）。跨线程调用一律在<b>触碰任何状态或 sink 之前</b>
    /// 返回 <see cref="TsfHResult.E_FAIL"/>：锁住状态对象不能替代 STA 线程归属，且跨线程回调 sink
    /// 会脱离原套间。计数 <see cref="CrossThreadRejectedCount"/> 供测试与诊断核对“零 sink 调用”。
    /// </para>
    /// <para><b>异常边界：</b>托管异常绝不穿过原生边界。</para>
    /// </remarks>
    private int Guard(string method, Func<int> body)
    {
        _ = method;

        if (!IsOnOwnerThread)
        {
            // 跨线程：不读状态、不回调 sink、不排队。
            Interlocked.Increment(ref _crossThreadRejected);
            return TsfHResult.E_FAIL;
        }

        if (_disposed)
        {
            // 释放后不再触碰核心/状态：所有入口显式失败。
            return TsfHResult.E_FAIL;
        }

        try
        {
            return body();
        }
        catch (Exception)
        {
            // 托管异常绝不穿过原生边界。
            return TsfHResult.E_FAIL;
        }
    }

    private static void WriteOptionalInt32(IntPtr target, int value)
    {
        if (target != IntPtr.Zero)
        {
            Marshal.WriteInt32(target, value);
        }
    }

    /// <summary>COM 身份比较：引用相等或同一 IUnknown 指针（不释放不属于自己的引用）。</summary>
    /// <summary>通过 sink 的 QI 取得 <c>ITextStoreACPServices</c>（失败不致命，只记录缺失）。</summary>
    private void TryCaptureServicesLocked(object? punk)
    {
        if (punk is null || !OperatingSystem.IsWindows())
        {
            return;
        }

        var unknown = IntPtr.Zero;
        var servicesPtr = IntPtr.Zero;
        try
        {
            unknown = Marshal.GetIUnknownForObject(punk);
            var iid = TsfOwnerIids.TextStoreAcpServicesIid;
            var hr = Marshal.QueryInterface(unknown, in iid, out servicesPtr);
            if (hr >= 0 && servicesPtr != IntPtr.Zero)
            {
                _services = (ITextStoreAcpServices)Marshal.GetObjectForIUnknown(servicesPtr);
            }
        }
        catch (Exception)
        {
            // 服务不可用不影响协议主路径。
        }
        finally
        {
            if (servicesPtr != IntPtr.Zero)
            {
                Marshal.Release(servicesPtr);
            }

            if (unknown != IntPtr.Zero)
            {
                Marshal.Release(unknown);
            }
        }
    }

    /// <summary>
    /// 用 <c>ITextStoreACPServices::CreateRange</c> 构造 ACP range；服务不可用时返回 null。
    /// </summary>
    /// <remarks>供组合建立路径使用；返回的 range 由调用方释放。</remarks>
    /// <returns>原始 <c>ITfRangeACP</c> 指针（调用方负责 Release）；服务不可用时返回 <see cref="IntPtr.Zero"/>。</returns>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal IntPtr CreateAcpRangePointer(int acpStart, int acpEnd)
    {
        ITextStoreAcpServices? services;
        lock (_stateGate)
        {
            services = _services;
        }

        if (services is null)
        {
            return IntPtr.Zero;
        }

        try
        {
            if (services.CreateRange(acpStart, acpEnd, out var range) < 0 || range is null)
            {
                return IntPtr.Zero;
            }

            return Marshal.GetIUnknownForObject(range);
        }
        catch (Exception)
        {
            return IntPtr.Zero;
        }
    }

    private static bool SameComIdentity(object? a, object? b)
    {
        if (a is null || b is null)
        {
            return false;
        }

        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        IntPtr left = IntPtr.Zero;
        IntPtr right = IntPtr.Zero;
        try
        {
            left = Marshal.GetIUnknownForObject(a);
            right = Marshal.GetIUnknownForObject(b);
            return left == right;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            // 只释放自己刚取得的两个临时引用；绝不对借用/共享 RCW 调 FinalReleaseComObject。
            if (left != IntPtr.Zero)
            {
                Marshal.Release(left);
            }

            if (right != IntPtr.Zero)
            {
                Marshal.Release(right);
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// 释放只清理<b>自己拥有</b>的状态：撤销 sink 引用、丢弃排队工作、推进代数使旧暂存失效。
    /// 不对 sink 调用任何方法，也不 <c>FinalReleaseComObject</c> 借用/共享的 RCW。
    /// </remarks>
    public void Dispose()
    {
        // R4：跨线程释放被拒绝，但<b>不</b>破坏拥有线程的释放能力（不标记 _disposed）。
        // 跨线程清空 COM 引用会脱离原套间，因此只计数并返回，由拥有线程完成真正的清理。
        if (!IsOnOwnerThread)
        {
            Interlocked.Increment(ref _crossThreadRejected);
            return;
        }

        lock (_stateGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _sink = null;
            _sinkPunk = null;
            _sinkMask = 0;
            _deferred.Clear();
            _flushScheduled = false;
            _continuationScheduled = false;
            _nativeGeneration++;
            Staging.AdvanceGeneration();
        }

        _locks.HasSink = false;
        _locks.ClearPending();
        ReleaseServices();
    }

    /// <summary>释放自己取得的 <c>ITextStoreACPServices</c>（不碰借用对象）。</summary>
    private void ReleaseServices()
    {
        ITextStoreAcpServices? services;
        lock (_stateGate)
        {
            services = _services;
            _services = null;
        }

        if (services is not null && OperatingSystem.IsWindows())
        {
            try
            {
                Marshal.ReleaseComObject(services);
            }
            catch (Exception)
            {
            }
        }
    }
}
