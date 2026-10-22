using System.Runtime.InteropServices;

namespace CSharp.UI.OpenTK;

/// <summary>
/// Windows IME 桥（TI-07）：对宿主自有 HWND 子类化并接 IMM32 宽接口，
/// 作为 <see cref="ITextInputService"/> 注入运行时。
/// </summary>
/// <remarks>
/// <para>
/// 所有原生消息处理都在创建窗口的 UI 线程上：回调里当场把 IMM 数据复制成托管快照，
/// 经 <see cref="ImeEventTranslator"/> 生成组合事件批次后同步应用——不重建组件、不排版、不递归泵事件；
/// 阻止默认重复插入所需的“已处理”结果同步返回。
/// </para>
/// <para>
/// 委托生命周期：子类化委托由本实例持有；<c>WM_NCDESTROY</c> 与 <see cref="Dispose"/> 都会恢复原过程，
/// 恢复失败时把委托钉住保活并计数，绝不把仍被原生引用的函数指针交给 GC。
/// </para>
/// </remarks>
internal sealed class WindowsImeBridge : IWindowsInputBridge
{
    /// <summary>恢复失败时钉住的委托（进程级保活，防 GC 回收仍被引用的函数指针）。</summary>
    private static readonly List<Win32Ime.WndProc> PinnedProcs = [];

    private readonly IntPtr _hwnd;
    private readonly IImeInterop _ime;
    private readonly OpenTkUiDispatcher _dispatcher;
    private readonly Func<(int Width, int Height)> _clientSize;
    private readonly ImeEventTranslator _translator = new();

    private Win32Ime.WndProc? _wndProcDelegate;
    private IntPtr _originalWndProc;
    private UiRuntime? _runtime;
    private ITextInputClient? _activeClient;
    private bool _subclassInstalled;
    private bool _subclassRestored;
    private bool _disposed;
    private WindowsTsf.HostWriterLease? _writerLease;

    private bool _installAttempted;
    private string? _installError;
    private int _callbackExceptionCount;
    private int _restoreFailureCount;
    private double _pointerScaleX = 1.0;
    private double _pointerScaleY = 1.0;

    /// <summary>当前按键消息的身份 token/IME 消费标记（仅在本窗口的 Key 消息链式调用期间有效）。</summary>
    private object? _currentKeyToken;
    private bool _currentKeyConsumed;

    /// <summary>
    /// 本桥已发布到平台的最后一次几何快照（逻辑坐标）与其客户区换算结果。
    /// 换算输入（DPI/指针比例/客户区）失效后按缓存重新发布；客户区三元组逐位相等时不重复唤醒平台。
    /// </summary>
    private TextInputGeometry? _lastLogicalGeometry;
    private (int X, int Y)? _lastPublishedPos;
    private (int L, int T, int R, int B)? _lastPublishedExclude;
    private (int X, int Y, int L, int T, int R, int B)? _lastPublishedComposition;
    private bool _anchorRevoked;

    /// <summary>
    /// 原生层面是否仍有未结束的组合（STARTCOMPOSITION..ENDCOMPOSITION/无标志取消）。
    /// 与客户端 IsComposing 无关：核心先本地取消再到安全阶段调用 RequestCancel，
    /// 此时客户端已不组合但原生组合仍在——原生待取消状态必须独立跟踪（R2）。
    /// </summary>
    private bool _nativeComposing;

    /// <summary>
    /// 当前原生组合的归属客户端：即收到 STARTCOMPOSITION 时绑定的客户端（按消息链归属，不按文本）。
    /// 同一段原生组合的后续 COMPOSITION/END 消息始终路由给它——提交回调内换焦点后它可能已释放，
    /// 旧段消息仍投递给它、由会话归属校验安全丢弃，绝不重定向到新客户端（R5）。
    /// END/无标志取消/新 START 时按平台序列更新归属。
    /// 退休（<see cref="Dispose"/>）时清空：退休后不再有组合归属。
    /// </summary>
    private ITextInputClient? _compositionOwnerClient;

    /// <summary>
    /// 原生组合是否已绑定归属：START 时即使没有任何客户端也置位——
    /// 无归属开始的组合不允许把消息转移给随后新聚焦的客户端；END/无标志取消时解除。
    /// </summary>
    private bool _compositionBound;

    /// <summary>
    /// 最近一次有效锚点（客户区像素）；供 IMR_CANDIDATEWINDOW 应答与诊断。
    /// 退休（<see cref="Dispose"/>）时一并清除：退休后本桥不得再以“仍持有有效锚点”的身份应答。
    /// </summary>
    private (int X, int Y)? _lastAnchorClient;
    private int _geometryPublishCount;
    private int _geometryRevokeCount;

    /// <summary>
    /// 最近一次带 GCS_COMPSTR 读到的原生组合串（R17）：IMR_QUERYCHARPOSITION 的 dwCharPos
    /// 是相对它的 UTF-16 偏移，应答前用它核对几何快照的预编辑身份；不匹配一律拒绝。
    /// </summary>
    private string? _nativeCompositionString;

    /// <summary>创建 IME 桥。hwnd 为零或互操作不可用时按“仅已提交文本”降级。</summary>
    internal WindowsImeBridge(
        IntPtr hwnd,
        IImeInterop ime,
        OpenTkUiDispatcher dispatcher,
        Func<(int Width, int Height)> clientSize)
    {
        _hwnd = hwnd;

        // R9/R8：**必须**先取得写入来源租约；冲突时拒绝安装（不写、不抢别人的窗口）。
        if (WindowsTsf.HostWriterRegistry.TryAcquire(
                hwnd, WindowsTsf.HostWriterKind.Imm, out var writerLease, out var conflictReason))
        {
            _writerLease = writerLease;
            _ime = ime;
            _dispatcher = dispatcher;
            _clientSize = clientSize;
            ClientToScreen = DefaultClientToScreen;
            return;
        }

        // 冲突：不安装，保持不可用并如实记录原因（宿主据此回退/报错）。
        _installAttempted = true;
        _installError = conflictReason ?? "该窗口已有其它输入写入来源。";
        _ime = ime;
        _dispatcher = dispatcher;
        _clientSize = clientSize;
        ClientToScreen = DefaultClientToScreen;
    }

    /// <summary>绑定运行时（结构后调用）：按键归属声明与脱敏诊断的落脚点。</summary>
    internal void AttachRuntime(UiRuntime runtime) => _runtime = runtime;

    /// <summary>诊断/验收追踪：每次关键消息与应用结果写一行（不含用户原文的元数据）。</summary>
    internal Action<string>? Trace { get; set; }

    /// <summary>当前正在分发的按键身份 token；非按键派发期间为 null。</summary>
    internal object? CurrentKeyToken => _currentKeyToken;

    /// <summary>当前按键是否已被输入法消费（VK_PROCESSKEY）。</summary>
    internal bool CurrentKeyConsumedByIme => _currentKeyConsumed;

    /// <summary>子类化是否仍安装。</summary>
    internal bool SubclassInstalled => _subclassInstalled;

    /// <summary>原过程是否已恢复（WM_NCDESTROY 或 Dispose）。</summary>
    internal bool SubclassRestored => _subclassRestored;

    /// <summary>原生回调内吞掉的托管异常计数；非 0 必须进入诊断与验收记录。</summary>
    internal int CallbackExceptionCount => _callbackExceptionCount;

    /// <summary>原过程恢复失败次数。</summary>
    internal int RestoreFailureCount => _restoreFailureCount;

    /// <summary>子类化安装失败原因；成功或尚未安装时为 null。</summary>
    internal string? InstallError => _installError;

    /// <summary>累计发布的有效锚点次数。</summary>
    internal int GeometryPublishCount => _geometryPublishCount;

    /// <summary>累计撤销锚点次数。</summary>
    internal int GeometryRevokeCount => _geometryRevokeCount;

    /// <summary>最近一次发布的锚点（客户区像素）。</summary>
    internal (int X, int Y)? LastAnchorClient => _lastAnchorClient;

    /// <summary>
    /// 客户区像素 → 屏幕坐标换算（R17 字符几何应答用）：默认调用 user32 ClientToScreen 作用于本窗口，
    /// 多显示器下保留负屏幕坐标；测试可注入替身。返回 null 表示窗口无效/换算失败——查询如实拒绝。
    /// </summary>
    internal Func<int, int, (int X, int Y)?> ClientToScreen { get; set; }

    private (int X, int Y)? DefaultClientToScreen(int x, int y)
        => Win32Ime.TryClientToScreen(_hwnd, x, y, out var sx, out var sy) ? (sx, sy) : null;

    /// <summary>从 GLFW 窗口取回 Win32 句柄；非 Windows 或查询失败返回 Zero。</summary>
    internal static IntPtr GetWindowHandle(global::OpenTK.Windowing.Desktop.NativeWindow window)
    {
        if (!OperatingSystem.IsWindows())
        {
            return IntPtr.Zero;
        }

        try
        {
            unsafe
            {
                return Win32Ime.GlfwGetWin32Window((IntPtr)window.WindowPtr);
            }
        }
        catch (Exception)
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>安装窗口子类化；失败只记录诊断并降级（仍可按仅提交文本运行），不抛出。</summary>
    internal void InstallSubclass()
    {
        if (_installAttempted || _disposed || _hwnd == IntPtr.Zero)
        {
            return;
        }

        _installAttempted = true;
        try
        {
            _wndProcDelegate = OnWndProc;
            var newProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate);
            var previous = Win32Ime.SetWindowProc(_hwnd, newProc);
            var error = Marshal.GetLastWin32Error();
            if (previous == IntPtr.Zero && error != 0)
            {
                _installError = $"SetWindowLongPtr(GWLP_WNDPROC) 失败，Win32 错误 {error}。";
                // 子类化未安装：委托从未被原生引用，可以安全释放。
                _wndProcDelegate = null;
                return;
            }

            _originalWndProc = previous;
            _subclassInstalled = true;

            // 重新安装即重新生效：此前的“已恢复”标记不再成立（同一实例再次安装后的如实状态）。
            _subclassRestored = false;
        }
        catch (Exception ex)
        {
            // 异常发生在安装完成之前：原生侧从未拿到函数指针，委托可以安全释放（R10）。
            _installError = $"安装 IME 子类化失败（{ex.GetType().Name}：{ex.Message}）。";
            _wndProcDelegate = null;
            return;
        }

        // 子类化已经安装：此后的任何失败都不得清空委托——原生回调仍指向它，
        // 必须先恢复原过程；恢复失败由 RestoreSubclass 钉住保活（R10）。
        try
        {
            // 尽早确保本窗口有输入上下文：运行时在 StartSession 之前先查 Capabilities，
            // 若此时 HIMC 尚未关联，能力会被降级为“仅已提交文本”，组合通道永远走不到。
            var himc = _ime.EnsureContext();
            _ime.ReleaseContext(himc);
        }
        catch (Exception ex)
        {
            _installError = $"安装后初始化输入上下文失败（{ex.GetType().Name}：{ex.Message}）。";
            RestoreSubclass("install-context-failure");
        }
    }

    /// <summary>
    /// 更新“窗口内容区坐标 → 逻辑像素”换算系数（与输入适配器同源）。
    /// 换算输入变化即全部已发布锚点失效：按缓存的逻辑几何立即重发（R7），
    /// 由发布路径自己的客户区同值比较保证不产生无意义平台调用。
    /// </summary>
    internal void UpdatePointerTransform(double scaleX, double scaleY)
    {
        var x = scaleX > 0 && double.IsFinite(scaleX) ? scaleX : 1.0;
        var y = scaleY > 0 && double.IsFinite(scaleY) ? scaleY : 1.0;
        if (x == _pointerScaleX && y == _pointerScaleY)
        {
            return;
        }

        _pointerScaleX = x;
        _pointerScaleY = y;
        RepublishCachedGeometry("transform");
    }

    /// <summary>
    /// 窗口位置/尺寸/可见性等使已发布锚点失效时的重发入口（宿主回调调用）。
    /// 失效意味着平台侧状态不再可信：先清掉已发布平台值缓存，使同值重发真正到达 IMM（R7/R13），
    /// 再按缓存逻辑几何重新发布；不逐帧强刷——只在宿主回调明确失效时调用。
    /// </summary>
    internal void NotifyGeometryInvalidated()
    {
        _lastPublishedPos = null;
        _lastPublishedExclude = null;
        _lastPublishedComposition = null;
        _anchorRevoked = false;
        RepublishCachedGeometry("host-change");
    }

    /// <summary>按缓存的最后一次逻辑几何重发锚点（含不可用=重新撤销语义）；无缓存时不产生平台调用。</summary>
    private void RepublishCachedGeometry(string reason)
    {
        if (_lastLogicalGeometry is { } geometry)
        {
            SafeTrace($"anchor-republish reason={reason}");
            PublishGeometry(geometry);
        }
    }

    /// <summary>
    /// 读取自有输入上下文的开启状态与转换模式（验收工具先保存原状态再切换中英文路径）。
    /// 只作用于本窗口 HIMC，不改用户全局/系统输入设置。无上下文时返回 false。
    /// </summary>
    internal bool TryGetNativeInputState(out bool open, out uint conversion, out uint sentence)
    {
        open = false;
        conversion = 0;
        sentence = 0;
        var himc = _ime.GetContext();
        if (himc == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            return _ime.GetNativeInputState(himc, out open, out conversion, out sentence);
        }
        finally
        {
            _ime.ReleaseContext(himc);
        }
    }

    /// <summary>设置自有输入上下文的开启状态与转换模式；调用方负责恢复保存的原状态。</summary>
    internal bool TrySetNativeInputState(bool open, uint conversion, uint sentence)
    {
        var himc = _ime.GetContext();
        if (himc == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            return _ime.SetNativeInputState(himc, open, conversion, sentence);
        }
        finally
        {
            _ime.ReleaseContext(himc);
        }
    }

    // ------------------------------------------------------------------ IWindowsInputBridge

    // 契约成员由现有 internal 成员实现：显式实现让宿主经接口依赖同一份状态，
    // 同时保留内部成员（验收工具/测试）的既有签名与可见性，不新增产品公开 API。

    /// <inheritdoc />
    object? IWindowsInputBridge.CurrentKeyToken => _currentKeyToken;

    /// <inheritdoc />
    bool IWindowsInputBridge.CurrentKeyConsumedByIme => _currentKeyConsumed;

    /// <inheritdoc />
    void IWindowsInputBridge.AttachRuntime(UiRuntime runtime) => AttachRuntime(runtime);

    /// <inheritdoc />
    void IWindowsInputBridge.NotifyGeometryInvalidated() => NotifyGeometryInvalidated();

    /// <inheritdoc />
    void IWindowsInputBridge.UpdatePointerTransform(double scaleX, double scaleY)
        => UpdatePointerTransform(scaleX, scaleY);

    // ------------------------------------------------------------------ ITextInputService

    /// <summary>测试钩子：把桥标记为“子类化已安装”（真实安装由 <see cref="InstallSubclass"/> 完成）。</summary>
    internal void SimulateInstalled()
    {
        _subclassInstalled = true;
        _subclassRestored = false;
    }

    /// <inheritdoc />
    public TextInputCapabilities Capabilities
    {
        get
        {
            if (_disposed || !_subclassInstalled)
            {
                return TextInputCapabilities.CommittedTextOnly;
            }

            var himc = _ime.GetContext();
            try
            {
                return himc != IntPtr.Zero
                    ? TextInputCapabilities.Full
                    : new TextInputCapabilities(true, reason: "本窗口没有可用的 IMM32 输入上下文。");
            }
            finally
            {
                _ime.ReleaseContext(himc);
            }
        }
    }

    /// <inheritdoc />
    public ITextInputClient? ActiveClient => _activeClient;

    /// <inheritdoc />
    public bool StartSession(ITextInputClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (_disposed || !_subclassInstalled)
        {
            return false;
        }

        // 窗口没有输入上下文时关联系统默认上下文（只影响本窗口，不动用户全局设置）。
        var himc = _ime.EnsureContext();
        if (himc == IntPtr.Zero)
        {
            Report("IME 会话未建立：窗口没有可用的输入上下文。");
            return false;
        }

        _ime.ReleaseContext(himc);

        // EnsureContext 是原生调用：它返回后必须重新核对退休标志，绝不把新客户端挂到已退休的桥上。
        if (_disposed)
        {
            return false;
        }

        _activeClient = client;
        _nativeCompositionString = null;
        _translator.BeginSession(client.Session.SessionId);
        SafeTrace($"session-start epoch={client.Session.SessionId}");
        return true;
    }

    /// <inheritdoc />
    public bool EndSession()
    {
        if (_disposed || _activeClient is null)
        {
            return false;
        }

        _activeClient = null;
        _nativeCompositionString = null;
        _translator.EndSession();

        // 先撤销客户端归属，再通知平台取消仍存在的原生组合（R2/“先失效、后通知”边界）；
        // 平台随后发送的无标志 COMPOSITION + ENDCOMPOSITION 到达时本桥已无客户端，安全忽略。
        if (_nativeComposing)
        {
            NotifyNativeCancel();
        }

        SafeTrace("session-end");
        return true;
    }

    /// <inheritdoc />
    public TextInputApplyResult ApplyComposition(IReadOnlyList<CompositionEvent> events)
    {
        if (_disposed)
        {
            return TextInputApplyResult.Rejected(
                TextSnapshot.Empty,
                "IME 桥已退休；组合事件未执行。");
        }

        var client = _activeClient;
        if (client is null)
        {
            return TextInputApplyResult.Rejected(
                TextSnapshot.Empty,
                "IME 桥没有活动客户端；组合事件未执行。");
        }

        // 客户端经运行时接线验证归属；这里只转发当前绑定的客户端。
        return client.ApplyComposition(events);
    }

    /// <inheritdoc />
    public bool RequestCancel()
    {
        // 已退休：本桥不再是该窗口的写入来源，取消必须由新 writer 负责，不能替它写原生组合。
        if (_disposed)
        {
            return false;
        }

        // 判定看原生组合是否仍在进行，而不是客户端本地状态：核心先本地取消预编辑，
        // 到安全阶段调用本方法时 ActiveClient.IsComposing 已是 false（R2）。
        if (!_nativeComposing)
        {
            return false;
        }

        var issued = NotifyNativeCancel();
        SafeTrace($"cancel-request issued={issued}");
        return issued;
    }

    /// <summary>本窗口当前是否持有输入上下文（不创建）：组合通道是否由本桥接管的判定依据。</summary>
    private bool HasNativeContext()
    {
        var himc = _ime.GetContext();
        try
        {
            return himc != IntPtr.Zero;
        }
        finally
        {
            _ime.ReleaseContext(himc);
        }
    }

    /// <summary>向平台发送组合取消通知；无上下文或调用失败时返回 false。</summary>
    private bool NotifyNativeCancel()
    {
        var himc = _ime.GetContext();
        if (himc == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            return _ime.NotifyCancelComposition(himc);
        }
        finally
        {
            _ime.ReleaseContext(himc);
        }
    }

    /// <inheritdoc />
    public bool PublishGeometry(TextInputGeometry geometry)
    {
        // 退休后不再写平台锚点：迟到几何发布既不缓存也不应答（迟到 WM_IME_REQUEST 同样零动作）。
        if (_disposed)
        {
            return false;
        }

        // 缓存逻辑几何供换算失效后重发（R7）；不可用状态同样入缓存（撤销语义本身可重复发布）。
        _lastLogicalGeometry = geometry;
        var himc = _ime.GetContext();
        if (himc == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            if (!geometry.HasCaret)
            {
                // 撤销锚点：用“整个客户区都是排除区”声明，让系统候选窗避开本窗口，
                // 而不是把候选定位到窗口原点或停在旧 caret 位置。已撤销状态不重复调用平台。
                _lastAnchorClient = null;
                _lastPublishedPos = null;
                _lastPublishedExclude = null;
                _lastPublishedComposition = null;
                if (_anchorRevoked)
                {
                    return true;
                }

                var (width, height) = _clientSize();
                var revoked = width > 0 && height > 0
                    && _ime.SetCandidateExclude(himc, 0, 0, width, height);
                _anchorRevoked = revoked;
                _geometryRevokeCount++;
                SafeTrace($"anchor-revoked ok={revoked}");
                return revoked;
            }

            var caret = ToClient(geometry.CaretBounds);
            var exclusion = geometry.ExclusionBounds.IsEmpty ? caret : ToClient(geometry.ExclusionBounds);
            var composition = geometry.HasComposition ? ToClient(geometry.CompositionBounds) : caret;
            var pos = (X: caret.Left, Y: caret.Bottom);
            var excl = (L: exclusion.Left, T: exclusion.Top, R: exclusion.Right, B: exclusion.Bottom);
            var comp = (X: caret.Left, Y: caret.Bottom,
                L: composition.Left, T: composition.Top, R: composition.Right, B: composition.Bottom);

            // 同值不重复唤醒：逐位比较上次已发布的客户区三元组（位置/排除区/组合窗分别比较）。
            var setPos = _lastPublishedPos != pos && _ime.SetCandidatePosition(himc, pos.X, pos.Y);
            var setExcl = _lastPublishedExclude != excl
                && _ime.SetCandidateExclude(himc, excl.L, excl.T, excl.R, excl.B);
            var setComp = _lastPublishedComposition != comp
                && _ime.SetCompositionWindow(himc, comp.X, comp.Y, comp.L, comp.T, comp.R, comp.B);
            if (setPos)
            {
                _lastPublishedPos = pos;
            }

            if (setExcl)
            {
                _lastPublishedExclude = excl;
            }

            if (setComp)
            {
                _lastPublishedComposition = comp;
            }

            if (setPos || setExcl || setComp)
            {
                _lastAnchorClient = pos;
                _anchorRevoked = false;
                _geometryPublishCount++;
            }

            SafeTrace(
                $"anchor caret={caret.Left},{caret.Top},{caret.Right},{caret.Bottom} " +
                $"excl={excl.L},{excl.T},{excl.R},{excl.B} pos={setPos} excl={setExcl} comp={setComp}");
            return true;
        }
        finally
        {
            _ime.ReleaseContext(himc);
        }
    }

    // ------------------------------------------------------------------ WndProc

    private IntPtr OnWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        var isKeyMessage = msg
            is Win32Ime.WM_KEYDOWN or Win32Ime.WM_SYSKEYDOWN or Win32Ime.WM_KEYUP or Win32Ime.WM_SYSKEYUP;
        var chainLParam = lParam;

        // 托管异常绝不穿回原生栈：吞掉、计数、尽量记录，然后继续链式调用。
        try
        {
            var overridden = HandleMessage(hwnd, msg, wParam, lParam);
            if (overridden is { } value)
            {
                return value;
            }

            if (msg == Win32Ime.WM_IME_SETCONTEXT && MaskedSetContextLParam is { } masked)
            {
                chainLParam = (IntPtr)masked;
            }
        }
        catch (Exception ex)
        {
            _callbackExceptionCount++;
            try
            {
                Report($"IME 消息 {msg:X4} 处理异常：{ex.GetType().Name}");
                SafeTrace($"callback-exception msg=0x{msg:X4} ex={ex.GetType().Name}");
            }
            catch (Exception)
            {
                // 诊断写入本身失败时忽略。
            }
        }

        try
        {
            return Win32Ime.CallWindowProc(_originalWndProc, hwnd, msg, wParam, chainLParam);
        }
        catch (Exception)
        {
            _callbackExceptionCount++;
            return IntPtr.Zero;
        }
        finally
        {
            // 按键 token 只对“本条消息链内嵌套的 GLFW 分发”有效；链返回后立即清除。
            if (isKeyMessage)
            {
                EndKeyDispatch();
            }

            MaskedSetContextLParam = null;
        }
    }

    /// <summary>
    /// 处理一条窗口消息；返回非 null 表示覆盖链式调用的返回值（吞掉该消息）。
    /// 测试可在无窗口环境下直接调用。
    /// </summary>
    internal IntPtr? HandleMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        // 窗口过程只在本窗口所属线程（UI 线程）上运行；防御性检查避免任何意外线程触碰运行时状态。
        if (!_dispatcher.IsOnUiThread)
        {
            return null;
        }

        // 退休（Dispose）后：本桥不再是该窗口的写入来源，迟到到达的组合/开始/结果/几何/按键回调
        // 一律零动作（不改状态、不写核心），原样交给链式默认过程。
        // WM_NCDESTROY 例外：它是窗口销毁清理路径，仍须继续尝试恢复原过程（不破坏清理）。
        if (_disposed && msg != Win32Ime.WM_NCDESTROY)
        {
            return null;
        }

        switch (msg)
        {
            case Win32Ime.WM_IME_STARTCOMPOSITION:
                _nativeComposing = true;
                _compositionBound = true;
                _compositionOwnerClient = _activeClient;
                _nativeCompositionString = null;
                if (_compositionOwnerClient is { } startClient)
                {
                    ApplyBatch(startClient, _translator.OnStartComposition(startClient), "ime-start");
                }

                return null;

            case Win32Ime.WM_IME_COMPOSITION:
                // 本桥接管的消息不进入默认链：链式默认处理（DefWindowProc）会把 GCS_RESULTSTR
                // 的结果再生成 WM_IME_CHAR 回声造成二次提交。本桥已按冻结契约自行提交结果，
                // 因此处理过（有归属客户端）的消息直接结束，不进默认结果生成链（R1）。
                return OnCompositionMessage(lParam.ToInt64()) ? IntPtr.Zero : null;

            case Win32Ime.WM_IME_ENDCOMPOSITION:
                _nativeComposing = false;
                _nativeCompositionString = null;
                {
                    // 结束消息同样属于该段原生组合的归属客户端（已释放则安全丢弃）。
                    var endClient = _compositionBound ? _compositionOwnerClient : _activeClient;
                    _compositionOwnerClient = null;
                    _compositionBound = false;
                    if (endClient is not null)
                    {
                        ApplyBatch(endClient, _translator.OnEndComposition(endClient), "ime-end");
                    }
                }

                return null;

            case Win32Ime.WM_IME_SETCONTEXT:
                // 框架自绘预编辑：屏蔽系统组合窗（0x80000000 高位），保留全部候选窗位（R8）。
                if (wParam.ToInt64() != 0)
                {
                    MaskedSetContextLParam = lParam.ToInt64() & ~Win32Ime.ISC_SHOWUICOMPOSITIONWINDOW;
                }

                return null;

            case Win32Ime.WM_IME_REQUEST:
                return OnImeRequest(wParam, lParam);

            case Win32Ime.WM_IME_CHAR:
                // WM_IME_CHAR 按消息类别即是“IME 转换结果字符”——它不是普通键入通道。
                // 本桥只有在真正接管组合通道（子类化已装且 HIMC 存在）时才按类吞掉其回声；
                // 无上下文（CommittedTextOnly 降级）时它就是唯一的结果通道，必须链式放行，
                // 让默认过程/GLFW 把已提交字符交给文本通道（R1 + committed-only 保留）。
                if (_subclassInstalled && HasNativeContext())
                {
                    SafeTrace("ime-char-swallowed (result echo channel owned by bridge)");
                    return IntPtr.Zero;
                }

                return null;

            // WM_CHAR/WM_SYSCHAR/WM_UNICHAR：普通字符与 UTF-32 字符通道不属于本桥的所有权——
            // 永远不吞（真实键入、DefWindowProc 翻译、外部工具 SendMessage 都照常放行）。
            // 若 IME 不投 WM_IME_CHAR 而直接投 WM_CHAR 的结果回声，依赖“结果消息不进默认链”
            // 从源头阻止其生成，而不是在这里按值匹配（R1）。

            case Win32Ime.WM_KEYDOWN:
            case Win32Ime.WM_SYSKEYDOWN:
            case Win32Ime.WM_KEYUP:
            case Win32Ime.WM_SYSKEYUP:
                BeginKeyDispatch(wParam);
                return null;

            case Win32Ime.WM_NCDESTROY:
                RestoreSubclass("ncdestroy");
                return null;

            default:
                return null;
        }
    }

    /// <summary>WM_IME_SETCONTEXT 屏蔽组合窗后要传给原过程的实际 lParam；未修改时为 null。</summary>
    internal long? MaskedSetContextLParam { get; private set; }

    /// <summary>WM_IME_COMPOSITION：当场读取 IMM 数据，生成并应用事件批次；返回本桥是否已接管该消息。</summary>
    private bool OnCompositionMessage(long flags)
    {
        // 归属按原生组合段（START 时绑定的客户端），不按“此刻的活动客户端”：
        // 提交回调换焦点后到达的旧段延迟消息仍发给原归属（会话层安全丢弃），不写入新客户端（R5）；
        // START 时就没有客户端的组合保持无归属——不转移给随后新聚焦的客户端。
        var client = _compositionBound ? _compositionOwnerClient : _activeClient;
        if (client is null)
        {
            return false;
        }

        var himc = _ime.GetContext();
        if (himc == IntPtr.Zero)
        {
            return false;
        }

        ImmCompositionSnapshot data;
        try
        {
            data = _ime.ReadComposition(himc, flags);
        }
        finally
        {
            _ime.ReleaseContext(himc);
        }

        // 无 STARTCOMPOSITION 的隐式组合（真实 IME 存在此序列）：本条带任何组合数据时把
        // 归属绑到处理它的客户端并标记原生组合进行中——随后的平台取消/延迟结果仍走同一归属，
        // 换焦点后旧段结果不会泄漏给新客户端（R2/R11 的隐式路径）。
        if (!_nativeComposing && (flags & Win32Ime.GcsAnyMask) != 0)
        {
            _nativeComposing = true;
            _compositionBound = true;
            _compositionOwnerClient = client;
        }

        // 本条声明组合串时记录原生原文：字符位置查询的 dwCharPos 是相对它的 UTF-16 偏移（R17）。
        if ((flags & Win32Ime.GCS_COMPSTR) != 0)
        {
            _nativeCompositionString = data.CompositionString;
        }

        // lParam 完全不含 GCS_* 标志才表示平台取消（R3/R6）：原生组合状态到此结束；
        // 事件侧的 Cancel 由翻译器按活动段身份产出，组合归属一并解除。
        if ((flags & Win32Ime.GcsAnyMask) == 0)
        {
            _nativeComposing = false;
            _compositionOwnerClient = null;
            _compositionBound = false;
            _nativeCompositionString = null;
        }

        SafeTrace(
            $"ime-composition flags=0x{flags:X} compLen={data.CompositionString?.Length ?? 0} " +
            $"resultLen={data.ResultString?.Length ?? 0} cursor={data.CursorPosition}");

        var batch = _translator.OnComposition(client, data);
        ApplyBatch(client, batch, "ime-composition");
        return true;
    }

    /// <summary>应用一个事件批次，并按真实回读结果重锚后续段。</summary>
    private void ApplyBatch(ITextInputClient client, List<CompositionEvent> events, string tag)
    {
        if (events.Count == 0)
        {
            return;
        }

        var result = client.ApplyComposition(events);

        // R5：setter 可能在提交回调内同步换焦点/卸载/换源——回调返回后重新校验归属，
        // 客户端已不在归属时绝不把旧批次的版本/替换区回写到新会话的翻译器状态。
        if (ReferenceEquals(_activeClient, client))
        {
            _translator.NoteApplied(result);
        }

        SafeTrace($"{tag} applied={result}");
    }

    /// <summary>按键消息进入时登记身份与 IME 消费标记；链式调用内嵌套的 GLFW 分发可读到。</summary>
    internal void BeginKeyDispatch(IntPtr wParam)
    {
        // 已退休：不再登记按键归属，也不通知运行时（退休后一切输入归属由新 writer 负责）。
        if (_disposed)
        {
            return;
        }

        var token = new object();
        var consumed = wParam.ToInt32() == Win32Ime.VK_PROCESSKEY;
        _currentKeyToken = token;
        _currentKeyConsumed = consumed;
        _runtime?.NotifyCompositionKeyOrigin(token, consumed);
    }

    /// <summary>按键消息处理完毕后清理当前身份（链式调用返回后）。</summary>
    internal void EndKeyDispatch()
    {
        _currentKeyToken = null;
        _currentKeyConsumed = false;
    }



    /// <summary>应答 WM_IME_REQUEST：按已发布锚点填候选/组合窗位置；不支持的请求放行。</summary>
    private IntPtr? OnImeRequest(IntPtr wParam, IntPtr lParam)
    {
        var request = (ulong)wParam.ToInt64();
        // 诊断 trace（R16）：如实记录到达本窗口的 WM_IME_REQUEST 请求号（含未处理的
        // IMR_QUERYCHARPOSITION 等），用于确认微软拼音实际向宿主查询什么；不改变应答语义。
        SafeTrace($"ime-request id=0x{request:X4} lparam=0x{lParam.ToInt64():X}");

        // 字符位置查询必须先识别：自绘几何没有有效数据时要返回明确 FALSE 并结束本消息——
        // 本桥已识别的请求绝不落回默认过程，否则默认链会按窗口原点/旧 caret 补出错误坐标。
        if (request == Win32Ime.IMR_QUERYCHARPOSITION)
        {
            return TryAnswerQueryCharPosition(lParam);
        }

        if (lParam == IntPtr.Zero || _lastAnchorClient is not { } anchor)
        {
            return null;
        }

        if (request == Win32Ime.IMR_CANDIDATEWINDOW)
        {
            var form = new Win32Ime.CANDIDATEFORM
            {
                dwIndex = 0,
                dwStyle = Win32Ime.CFS_CANDIDATEPOS,
                ptCurrentPos = new Win32Ime.POINT { X = anchor.X, Y = anchor.Y },
            };
            try
            {
                Marshal.StructureToPtr(form, lParam, fDeleteOld: false);
                return new IntPtr(1);
            }
            catch (Exception)
            {
                return null;
            }
        }

        if (request == Win32Ime.IMR_COMPOSITIONWINDOW)
        {
            var form = new Win32Ime.COMPOSITIONFORM
            {
                dwStyle = Win32Ime.CFS_POINT,
                ptCurrentPos = new Win32Ime.POINT { X = anchor.X, Y = anchor.Y },
            };
            try
            {
                Marshal.StructureToPtr(form, lParam, fDeleteOld: false);
                return new IntPtr(1);
            }
            catch (Exception)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// 应答 IMR_QUERYCHARPOSITION（R17）：自绘组合串时按 IMECHARPOSITION 官方语义逐字符回填——
    /// dwCharPos 是组合串 UTF-16 偏移，pt 为该字符屏幕坐标，cLineHeight 物理行高，
    /// rcDocument 为可编辑可见区域的屏幕矩形（不是整个客户区）。
    /// </summary>
    /// <remarks>
    /// 应答数据只来自安全阶段捕获的 <see cref="CompositionCharGeometrySnapshot"/>（最终显示排版 +
    /// 原点 + 有效裁剪）；本回调只读快照，绝不触发 Measure/Arrange/Build 或重新排版。
    /// 结构无效、快照缺失/过期（预编辑或文档版本已变化、归属撤销）、偏移越界或完全裁出都如实
    /// 返回 FALSE，不回落到旧锚点或窗口原点。客户区换算与锚点同系数，负屏幕坐标原样保留。
    /// </remarks>
    private IntPtr TryAnswerQueryCharPosition(IntPtr lParam)
    {
        try
        {
            // 已识别的查询一切无效输入都返回明确 FALSE（IntPtr.Zero），绝不返回 null 落回默认链。
            if (lParam == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            // dwSize/dwCharPos 是调用方输入字段：先验证声明的结构大小，再读偏移——
            // 不对任意原生地址做越界探测，只按调用方声明的合法缓冲区读写。
            var charPosSize = Marshal.SizeOf<Win32Ime.IMECHARPOSITION>();
            var declaredSize = Marshal.ReadInt32(lParam, 0);
            if (declaredSize < charPosSize)
            {
                return IntPtr.Zero;
            }

            var charPos = unchecked((uint)Marshal.ReadInt32(lParam, 4));

            // 锚点已撤销（裁出/隐藏/几何失效）时没有可用几何：明确拒绝，不回落旧点。
            if (_lastAnchorClient is null)
            {
                return IntPtr.Zero;
            }

            // 查询归属与组合消息一致：已绑定段只应答其归属客户端的几何（旧段消息不转给新客户端）。
            var client = _compositionBound ? _compositionOwnerClient : _activeClient;
            if (client is not ICompositionCharGeometrySource source
                || source.CompositionCharGeometry is not { } snapshot
                || !string.Equals(snapshot.Preedit, _nativeCompositionString ?? string.Empty, StringComparison.Ordinal)
                || snapshot.EditableClip.IsEmpty
                || !snapshot.TryResolveCharPosition(charPos, out var charX, out var charY, out var lineHeight))
            {
                return IntPtr.Zero;
            }

            var doc = ToClient(snapshot.EditableClip);
            if (ClientToScreen((int)Math.Floor(charX / _pointerScaleX), (int)Math.Floor(charY / _pointerScaleY)) is not { } pt
                || ClientToScreen(doc.Left, doc.Top) is not { } docOrigin
                || ClientToScreen(doc.Right, doc.Bottom) is not { } docExtent)
            {
                return IntPtr.Zero;
            }

            var answer = new Win32Ime.IMECHARPOSITION
            {
                dwSize = unchecked((uint)declaredSize),
                dwCharPos = charPos,
                pt = new Win32Ime.POINT { X = pt.X, Y = pt.Y },
                cLineHeight = (uint)Math.Max(1, (int)Math.Ceiling(lineHeight / _pointerScaleY)),
                rcDocument = new Win32Ime.RECT
                {
                    Left = docOrigin.X,
                    Top = docOrigin.Y,
                    Right = docExtent.X,
                    Bottom = docExtent.Y,
                },
            };
            Marshal.StructureToPtr(answer, lParam, fDeleteOld: false);
            SafeTrace($"ime-char-pos offset={charPos} pt={pt.X},{pt.Y}");
            return new IntPtr(1);
        }
        catch (Exception)
        {
            // 合法缓冲区验证之外的任何意外一律如实失败：不假成功，也不让异常越过原生回调边界。
            return IntPtr.Zero;
        }
    }

    /// <summary>恢复原窗口过程；已恢复或窗口已销毁时幂等。</summary>
    /// <remarks>
    /// <para>
    /// <b>成功判定以恢复后的实际窗口过程为准：</b>只有确实“当前过程是原过程”才解除安装；
    /// 否则如实保留“仍安装”、计数并钉住委托保活——绝不因恢复调用未抛异常就虚报窗口已空闲。
    /// </para>
    /// <para>
    /// <b>恢复失败保留重试所需状态：</b>已钉住的委托根不重复累积（同一委托只钉一次），
    /// 原过程指针不清空，<see cref="Dispose"/> 可由所属线程重复调用继续重试。
    /// </para>
    /// <para><b>只清理本窗口：</b>全部原生调用只作用于本桥的 HWND，绝不跨窗口操作。</para>
    /// </remarks>
    internal void RestoreSubclass(string trigger)
    {
        if (!_subclassInstalled)
        {
            return;
        }

        try
        {
            if (!Win32Ime.IsWindow(_hwnd))
            {
                // 窗口已销毁：原生过程不再存在，本桥确定不再是该窗口的 writer。
                _subclassInstalled = false;
                _subclassRestored = true;
                ReleasePinnedProc();
                SafeTrace($"uninstall-skipped-window-destroyed trigger={trigger}");
                return;
            }

            var current = Win32Ime.GetWindowProc(_hwnd);
            if (current != _originalWndProc)
            {
                var ownProc = _wndProcDelegate is null ? IntPtr.Zero : Marshal.GetFunctionPointerForDelegate(_wndProcDelegate);
                if (ownProc == IntPtr.Zero || current != ownProc)
                {
                    // 外部后来安装的子类化仍可能链式调用本桥，不能覆盖它或撤销保活/租约。
                    RecordRestoreFailure(trigger);
                    return;
                }
                Win32Ime.SetWindowProc(_hwnd, _originalWndProc);
                current = Win32Ime.GetWindowProc(_hwnd);
            }
            if (current == _originalWndProc)
            {
                _subclassInstalled = false;
                _subclassRestored = true;
                ReleasePinnedProc();
                SafeTrace($"uninstall-ok trigger={trigger}");
                return;
            }

            RecordRestoreFailure(trigger);
        }
        catch (Exception)
        {
            RecordRestoreFailure(trigger);
        }
    }

    /// <summary>如实记录一次恢复失败：保留安装状态、计数、钉住委托根（同一委托只钉一次）。</summary>
    private void RecordRestoreFailure(string trigger)
    {
        _subclassInstalled = true;
        _restoreFailureCount++;
        if (_wndProcDelegate is { } proc)
        {
            lock (PinnedProcs)
            {
                if (!PinnedProcs.Contains(proc)) PinnedProcs.Add(proc);
            }
        }

        SafeTrace($"uninstall-failed trigger={trigger} failures={_restoreFailureCount}");
        Report($"IME 子类化恢复原过程失败（{trigger}）。");
    }

    private void ReleasePinnedProc()
    {
        if (_wndProcDelegate is { } proc)
        {
            lock (PinnedProcs) PinnedProcs.Remove(proc);
        }
    }

    /// <summary>把逻辑矩形换算为窗口客户区像素（向下/上取整保证覆盖）。</summary>
    private (int Left, int Top, int Right, int Bottom) ToClient(Rect rect)
        => (
            (int)Math.Floor(rect.X / _pointerScaleX),
            (int)Math.Floor(rect.Y / _pointerScaleY),
            (int)Math.Ceiling(rect.Right / _pointerScaleX),
            (int)Math.Ceiling(rect.Bottom / _pointerScaleY));

    /// <summary>写追踪行；监听器异常被吞掉，绝不能让日志失败把已处理的消息落回默认路径（R1）。</summary>
    private void SafeTrace(string line)
    {
        try
        {
            Trace?.Invoke(line);
        }
        catch (Exception)
        {
            // 追踪失败不影响消息路径。
        }
    }

    private void Report(string message)
    {
        try
        {
            _runtime?.ReportDiagnostic(message);
        }
        catch (Exception)
        {
            // 诊断失败不影响消息路径。
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <b>只在所属 UI 线程释放：</b>非所属线程调用被拒绝且<b>不产生任何副作用</b>
    /// （不置退休标志、不撤销客户端、不恢复原过程、不归还租约），活动桥继续按原样工作；
    /// 清理留给所属线程（再次 <see cref="Dispose"/>）。
    /// </para>
    /// <para>
    /// <b>顺序：</b>先标退休禁止再写（<see cref="HandleMessage"/> 对迟到回调零动作，
    /// <c>WM_NCDESTROY</c> 除外，销毁清理照常尝试恢复），再撤销活动客户端、原生组合归属与已发布锚点，
    /// 然后恢复原窗口过程；<b>只有确认不再安装或窗口已销毁</b>才归还写入来源租约。
    /// </para>
    /// <para>
    /// <b>恢复失败不假成功、可重试：</b>失败时按既有 <see cref="PinnedProcs"/> 钉住委托保活，
    /// <b>保留租约</b>阻止第二个 writer 接管（原生回调仍可能到达本桥，它仍是实际 writer），
    /// 由所属线程再次调用本方法重试恢复。
    /// </para>
    /// </remarks>
    public void Dispose()
    {
        if (!_dispatcher.IsOnUiThread)
        {
            // 跨线程释放：拒绝且无副作用，不触碰任何状态。
            return;
        }

        // 退休标志先置位：此后 HandleMessage 不再消费/应答任何迟到 IMM 消息（WM_NCDESTROY 除外）。
        _disposed = true;

        // 撤销活动客户端与原生组合归属：迟到消息即使进入处理路径也无客户端可写（幂等）。
        _activeClient = null;
        _compositionOwnerClient = null;
        _compositionBound = false;
        _nativeComposing = false;
        _nativeCompositionString = null;

        // 已发布锚点同步失效：退休后即使迟到 WM_IME_REQUEST 进入清理路径，也无锚点可应答。
        _lastAnchorClient = null;

        // 恢复原过程（幂等；已恢复或窗口已销毁时不重复调用原生）。
        RestoreSubclass("dispose");

        // 只有确认原有过程已恢复（不再安装）或窗口已销毁，本桥才不再是该窗口的 writer；
        // 恢复失败时子类化仍然安装，必须保留租约阻止第二 writer。
        if (!_subclassInstalled && WindowsTsf.HostWriterRegistry.Release(_writerLease))
        {
            _writerLease = null;
        }

        // 恢复失败时委托仍可能被原生代码引用：无条件保活到进程结束。
        GC.KeepAlive(_wndProcDelegate);
    }

    /// <summary>测试专用：本桥是否仍持有写入来源租约（恢复失败保留租约的语义核对）。</summary>
    internal bool HoldsWriterLease => _writerLease is not null;
}
