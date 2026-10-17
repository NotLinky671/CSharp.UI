using System.Runtime.InteropServices;
using System.Text;
using global::OpenTK.Graphics.OpenGL4;
using global::OpenTK.Windowing.Common;
using global::OpenTK.Windowing.Desktop;

namespace CSharp.UI.ImeProbe;

/// <summary>
/// <see cref="ImeProbeWindow.SetNativeImeMode"/> 的结果：只认回读值，不认调用返回值。
/// </summary>
/// <param name="Applied">open 与 conversion 两项都按回读确认落地。</param>
/// <param name="OpenApplied">ImmSetOpenStatus(true) 之后回读 open 仍为 true。</param>
/// <param name="ConversionApplied">回读的 conversion 同时含有请求的 CMODE_NATIVE|CMODE_ROMAN。</param>
/// <param name="Himc">被设置的目标上下文句柄（操作对象，不是用户全局设置）。</param>
/// <param name="ReadbackNative">回读值里 CMODE_NATIVE 是否为真。</param>
/// <param name="Reason">未落地时的原因说明。</param>
internal sealed record NativeModeOutcome(
    bool Applied,
    bool OpenApplied,
    bool ConversionApplied,
    string Himc,
    bool ReadbackNative,
    string Reason);

/// <summary>
/// 专用测试窗口：创建自己的 GLFW/OpenTK 窗口，只对该窗口子类化窗口过程并接 IMM32 宽接口。
/// </summary>
/// <remarks>
/// <para>
/// 所有消息处理都在创建窗口的主线程上（GLFW 事件泵 <c>GLFW.PollEvents</c> 所在线程），
/// 日志中的 <c>tid</c> 可核对这一点。窗口过程内的任何托管异常都被吞掉并计数，绝不让异常穿回原生栈。
/// </para>
/// <para>
/// 该类型不修改生产框架：只操作自己创建的 HWND，销毁前恢复原窗口过程。
/// </para>
/// </remarks>
internal sealed class ImeProbeWindow : IDisposable
{
    private const uint IMR_COMPOSITIONWINDOW = 0x0001;
    private const uint IMR_CANDIDATEWINDOW = 0x0002;
    private const uint IMR_QUERYCHARPOSITION = 0x0006;
    private const uint IMR_DOCUMENTFEED = 0x0007;
    private const uint IMR_RECONVERTSTRING = 0x0004;
    private const int MaxTextInputs = 64;

    /// <summary>恢复失败时把委托钉住，避免仍被原生代码引用的函数指针被 GC 回收。</summary>
    private static readonly List<NativeMethods.WndProc> PinnedProcs = [];

    /// <summary>在原生回调里登记的截图请求；真正的抓屏在消息返回且画面更新后执行。</summary>
    private readonly List<(string Tag, int DelayMs)> _deferredCaptures = [];
    private bool _deferredCaptureRunning;

    private readonly ProbeLog _log;
    private readonly string _label;
    private readonly string? _captureDirectory;
    private readonly List<string> _resultSegments = [];
    private readonly List<string> _textInputs = [];
    private readonly List<string> _compositionUpdates = [];
    private readonly List<string> _preeditStrings = [];
    private readonly List<CommitSnapshot> _commitSnapshots = [];
    private readonly NativeWindow _window;
    private readonly IntPtr _hwnd;
    private NativeMethods.WndProc _wndProcDelegate = null!;
    private IntPtr _originalWndProc;
    private bool _subclassInstalled;
    private bool _disposed;
    private int _callbackExceptionCount;
    private int _skippedMessageCount;
    private int _compositionId;
    private bool _composing;
    private bool _resultSeenInComposition;
    private int _compositionUpdateCount;
    private int _commitCount;
    private int _cancelCount;
    private int _imeCharMessageCount;
    private int _charMessageCount;
    private int _unicharMessageCount;
    private int _textInputCount;
    private int _keyDownCount;
    private int _keyRepeatCount;
    private int _candidateRequestCount;
    private int _captureCount;
    private string _imeDescription = string.Empty;
    private string _imeFileName = string.Empty;
    private bool _resultAndPreeditSameMessage;
    private int _resultFlagCommitCount;
    private int _snapshotOnlyResultCount;
    private int _anchorSetOkCount;
    private int _anchorGetOkCount;
    private int _lastCandidateCount;
    private string _faultInjectMode = "none";
    private uint _faultInjectMessage;
    private bool _restoreFailedForFaultInjection;
    private int _pumpExceptionCount;
    private readonly List<string> _restoreOutcomes = [];
    private uint _originalConversion;
    private uint _originalSentence;
    private bool _originalOpenStatus;
    private bool _originalStateCaptured;

    internal ImeProbeWindow(
        ProbeLog log,
        string label,
        string title,
        int width,
        int height,
        bool visible,
        bool subclass = true,
        string? captureDirectory = null)
    {
        _log = log;
        _label = label;
        _captureDirectory = captureDirectory;

        var settings = new NativeWindowSettings
        {
            Title = title,
            ClientSize = new global::OpenTK.Mathematics.Vector2i(width, height),
            APIVersion = new Version(3, 3),
            Profile = ContextProfile.Core,
            Flags = ContextFlags.Default,
            Vsync = VSyncMode.Off,
            IsEventDriven = false,
            StartVisible = visible,
            StartFocused = visible,
            WindowBorder = WindowBorder.Resizable,
            NumberOfSamples = 0,
            DepthBits = 0,
            StencilBits = 0,
        };

        _window = new NativeWindow(settings);
        Window = _window;
        _window.MakeCurrent();

        unsafe
        {
            _hwnd = NativeMethods.GlfwGetWin32Window((IntPtr)_window.WindowPtr);
        }

        if (_hwnd == IntPtr.Zero)
        {
            _window.Dispose();
            throw new InvalidOperationException("glfwGetWin32Window 返回空句柄，无法继续。");
        }

        HookGlfwEvents();

        _log.Record("probe", "probe-window", "window-created", new Dictionary<string, object?>
        {
            ["label"] = _label,
            ["hwnd"] = $"0x{_hwnd.ToInt64():X}",
            ["class_name"] = NativeMethods.GetWindowClassName(_hwnd),
            ["client_size"] = DescribeClientSize(),
            ["window_thread_id"] = NativeMethods.GetWindowThreadProcessId(_hwnd, out var ownerPid),
            ["window_pid"] = ownerPid,
            ["unit"] = "window-content-pixels",
        });

        if (subclass)
        {
            InstallSubclass();
            AssociateDefaultImeContext("construction");
        }
    }

    internal NativeWindow Window { get; }

    internal IntPtr Handle => _hwnd;

    internal string Label => _label;

    /// <summary>本窗口使用的结构化日志；供窗口过程之外（事件泵）执行延迟的身份查询写入。</summary>
    internal ProbeLog Log => _log;

    /// <summary>最后一条已写出日志的序号；用于把“登记时刻”和“查询时刻”分开记录。</summary>
    internal long LastLoggedSequence => _log.LastSequence;

    internal int CallbackExceptionCount => _callbackExceptionCount;

    internal IReadOnlyList<string> ResultSegments => _resultSegments;

    internal IReadOnlyList<string> TextInputs => _textInputs;

    internal IReadOnlyList<string> CompositionUpdates => _compositionUpdates;

    /// <summary>
    /// 最近一次组合会话里逐条读到的非空预编辑串（例如 n → ni → ni'h → ni'ha）。
    /// 只反映“当前/最后一段”会话；逐结果归因请用 <see cref="CommitSnapshots"/>。
    /// </summary>
    internal IReadOnlyList<string> PreeditStrings => _preeditStrings;

    /// <summary>
    /// 每次新 <c>GCS_RESULTSTR</c> 结果在记录时刻冻结的快照（结果串 + 所属 compositionId + 该结果所属
    /// 组合会话的预编辑历史）。分两段提交时，第一段的历史不会被第二段的 start 清空影响。
    /// </summary>
    internal IReadOnlyList<CommitSnapshot> CommitSnapshots => _commitSnapshots;

    internal int CompositionUpdateCount => _compositionUpdateCount;

    internal int CommitCount => _commitCount;

    internal int CancelCount => _cancelCount;

    internal int ImeCharMessageCount => _imeCharMessageCount;

    internal int CharMessageCount => _charMessageCount;

    internal int UnicharMessageCount => _unicharMessageCount;

    internal int TextInputCount => _textInputCount;

    internal int KeyDownCount => _keyDownCount;

    internal int KeyRepeatCount => _keyRepeatCount;

    internal int CandidateRequestCount => _candidateRequestCount;

    internal int SkippedMessageCount => _skippedMessageCount;

    internal int CaptureCount => _captureCount;

    internal bool IsComposing => _composing;

    internal int CompositionId => _compositionId;

    /// <summary>最近一次从 IME 上下文读到的输入法描述（ImmGetDescriptionW）。</summary>
    internal string CurrentImeDescription => _imeDescription;

    /// <summary>最近一次从 IME 上下文读到的输入法文件名（ImmGetIMEFileNameW）。</summary>
    internal string CurrentImeFileName => _imeFileName;

    /// <summary>是否观察到同一条 WM_IME_COMPOSITION 同时携带结果串与预编辑串。</summary>
    internal bool ResultAndPreeditSameMessage => _resultAndPreeditSameMessage;

    /// <summary>按 GCS_RESULTSTR 标志计入的提交次数。</summary>
    internal int ResultFlagCommitCount => _resultFlagCommitCount;

    /// <summary>是否已经处理过第一条真实合成更新（用于“首次真实组合”现场只读身份佐证）。</summary>
    internal bool FirstCompositionUpdateSeen { get; private set; }

    /// <summary>无结果标志但上下文里仍有结果串数据的次数（只作快照）。</summary>
    internal int SnapshotOnlyResultCount => _snapshotOnlyResultCount;

    /// <summary>最近一次组合更新里从 IMM 候选列表读到的候选数量（只读快照，不是“候选窗已显示”的证明）。</summary>
    internal int CurrentCandidateCount => _lastCandidateCount;

    /// <summary>ImmSetCandidateWindow 成功次数。</summary>
    internal int AnchorSetOkCount => _anchorSetOkCount;

    /// <summary>ImmGetCandidateWindow 成功次数。</summary>
    internal int AnchorGetOkCount => _anchorGetOkCount;

    /// <summary>子类化当前是否仍然安装。</summary>
    internal bool SubclassInstalled => _subclassInstalled;

    /// <summary>非预期的消息泵/渲染异常次数；不为 0 时相关场景不得判 PASS。</summary>
    internal int UnexpectedPumpExceptionCount => _pumpExceptionCount;

    /// <summary>是否已在消息路径（WM_NCDESTROY）或 Dispose 中恢复原窗口过程。</summary>
    internal bool SubclassRestored { get; private set; }

    /// <summary>每次恢复尝试的“触发点:结果”序列，用于断言恢复判据（ok / failed / window-gone）。</summary>
    internal string RestoreOutcomes => string.Join(";", _restoreOutcomes);

    /// <summary>写入日志用的窗口客户端尺寸描述。</summary>
    internal string DescribeClientSize()    {
        var client = _window.ClientSize;
        var framebuffer = _window.FramebufferSize;
        return $"{client.X}x{client.Y} (framebuffer {framebuffer.X}x{framebuffer.Y})";
    }

    /// <summary>当前逻辑 caret 的客户区坐标（固定公式，不读取用户输入）。</summary>
    internal (int X, int Y) CaretClientPoint
    {
        get
        {
            var client = _window.ClientSize;
            var x = Math.Clamp(client.X / 5 + 40, 8, Math.Max(9, client.X - 8));
            var y = Math.Max(client.Y / 2, 8);
            return (x, y);
        }
    }

    internal uint WindowDpi => NativeMethods.GetDpiForWindow(_hwnd);

    /// <summary>记录当前输入法上下文状态（输入法身份、开关状态、转换模式、候选数）。</summary>
    internal void LogCurrentImeState(string trigger)
    {
        var himc = NativeMethods.ImmGetContext(_hwnd);
        try
        {
            var (description, fileName) = ReadImeIdentity(himc);
            _imeDescription = description;
            _imeFileName = fileName;
            var (conversion, sentence) = ReadConversionStatus(himc);
            var open = himc != IntPtr.Zero && NativeMethods.ImmGetOpenStatus(himc);
            var candidates = ReadCandidates(himc);
            var defaultImeWindow = NativeMethods.ImmGetDefaultIMEWnd(_hwnd);
            _log.Record("imm32", "imm32-api", "ime-state", new Dictionary<string, object?>
            {
                ["label"] = _label,
                ["trigger"] = trigger,
                ["himc"] = himc == IntPtr.Zero ? "(null)" : $"0x{himc.ToInt64():X}",
                ["ime_description"] = description,
                ["ime_file_name"] = fileName,
                ["open_status"] = open,
                ["conversion"] = $"0x{conversion:X8}",
                ["sentence"] = $"0x{sentence:X8}",
                ["cmode_native"] = (conversion & NativeMethods.IME_CMODE_NATIVE) != 0,
                ["cmode_roman"] = (conversion & NativeMethods.IME_CMODE_ROMAN) != 0,
                ["cmode_fullshape"] = (conversion & NativeMethods.IME_CMODE_FULLSHAPE) != 0,
                ["candidate_count"] = candidates.Count,
                ["default_ime_window"] = defaultImeWindow == IntPtr.Zero ? "(null: no IME UI window for this hwnd)" : $"0x{defaultImeWindow.ToInt64():X}",
                ["unit"] = "IMM32 context state",
            });
        }
        finally
        {
            if (himc != IntPtr.Zero)
            {
                NativeMethods.ImmReleaseContext(_hwnd, himc);
            }
        }
    }

    /// <summary>
    /// 为窗口显式创建并关联一个 IME 上下文（部分 IME 接入要求窗口拥有自己的上下文，
    /// 而不是系统默认上下文）。只影响本窗口。
    /// </summary>
    internal void AssociateOwnImeContext()
    {
        var created = NativeMethods.ImmCreateContext();
        var previous = created == IntPtr.Zero
            ? IntPtr.Zero
            : NativeMethods.ImmAssociateContext(_hwnd, created);
        var current = NativeMethods.ImmGetContext(_hwnd);
        if (current != IntPtr.Zero)
        {
            NativeMethods.ImmReleaseContext(_hwnd, current);
        }

        _log.Record("imm32", "imm32-api", "associate-own-context", new Dictionary<string, object?>
        {
            ["label"] = _label,
            ["created_context"] = created == IntPtr.Zero ? "(null)" : $"0x{created.ToInt64():X}",
            ["previous_context"] = previous == IntPtr.Zero ? "(null)" : $"0x{previous.ToInt64():X}",
            ["current_context"] = current == IntPtr.Zero ? "(null)" : $"0x{current.ToInt64():X}",
            ["scope"] = "this window only",
        });
    }

    /// <summary>记录原始输入法状态，供探针结束时按窗口上下文恢复。</summary>
    internal void CaptureImeState()
    {
        var himc = NativeMethods.ImmGetContext(_hwnd);
        if (himc == IntPtr.Zero)
        {
            return;
        }

        try
        {
            if (NativeMethods.ImmGetConversionStatus(himc, out var conversion, out var sentence))
            {
                _originalConversion = conversion;
                _originalSentence = sentence;
                _originalOpenStatus = NativeMethods.ImmGetOpenStatus(himc);
                _originalStateCaptured = true;
            }
        }
        finally
        {
            NativeMethods.ImmReleaseContext(_hwnd, himc);
        }
    }

    /// <summary>
    /// 把本窗口上下文的输入法切到中文（native）模式并打开，仅影响本窗口的 IME 上下文。
    /// </summary>
    /// <remarks>
    /// 这里不把 API 调用本身当作成功：设置之后立即回读 open/conversion，并把“请求值 vs 回读值”
    /// 作为返回值交给调用方判定。异步的 profile/上下文切换可能覆盖刚写入的值，因此只有回读匹配才算落地。
    /// </remarks>
    internal NativeModeOutcome SetNativeImeMode()
    {
        var himc = NativeMethods.ImmGetContext(_hwnd);
        if (himc == IntPtr.Zero)
        {
            _log.Record("imm32", "imm32-api", "set-native-mode-skipped", new Dictionary<string, object?>
            {
                ["label"] = _label,
                ["reason"] = "ImmGetContext returned NULL",
            });
            return new NativeModeOutcome(false, false, false, "(null himc)", false, "ImmGetContext returned NULL");
        }

        try
        {
            var openOk = NativeMethods.ImmSetOpenStatus(himc, true);
            var conversion = NativeMethods.IME_CMODE_NATIVE | NativeMethods.IME_CMODE_ROMAN;
            var conversionOk = NativeMethods.ImmSetConversionStatus(himc, conversion, 0);
            var statusOk = NativeMethods.ImmGetConversionStatus(himc, out var readback, out var sentenceReadback);
            var openReadback = NativeMethods.ImmGetOpenStatus(himc);
            var nativeReadback = (readback & NativeMethods.IME_CMODE_NATIVE) != 0;
            var openApplied = openOk && openReadback;
            var conversionApplied = conversionOk && statusOk && (readback & conversion) == conversion;
            var applied = openApplied && conversionApplied;
            var reason = applied
                ? "open=true and CMODE_NATIVE|CMODE_ROMAN were both read back from this HIMC"
                : $"readback mismatch: ImmSetOpenStatus={openOk}/readback_open={openReadback}, "
                    + $"ImmSetConversionStatus={conversionOk}/readback=0x{readback:X8}";
            _log.Record("imm32", "imm32-api", "set-native-mode", new Dictionary<string, object?>
            {
                ["label"] = _label,
                ["himc"] = $"0x{himc.ToInt64():X}",
                ["ImmSetOpenStatus_ok"] = openOk,
                ["ImmSetConversionStatus_ok"] = conversionOk,
                ["requested_conversion"] = $"0x{conversion:X8}",
                ["readback_conversion"] = $"0x{readback:X8}",
                ["readback_sentence"] = $"0x{sentenceReadback:X8}",
                ["readback_cmode_native"] = nativeReadback,
                ["readback_open_status"] = openReadback,
                ["apply_readback_verified"] = applied,
                ["apply_failure_reason"] = applied ? string.Empty : reason,
                ["scope"] = "this window's IMM32 context only; the user's global input method default is untouched",
                ["note"] = "the set calls are NOT treated as success; only the matching readback is",
            });

            return new NativeModeOutcome(
                applied, openApplied, conversionApplied, $"0x{himc.ToInt64():X}", nativeReadback, reason);
        }
        finally
        {
            NativeMethods.ImmReleaseContext(_hwnd, himc);
        }
    }

    /// <summary>把本窗口上下文恢复到探针启动时记录的状态（尽力而为）。</summary>
    internal void RestoreImeState()
    {
        if (!_originalStateCaptured || _disposed)
        {
            return;
        }

        var himc = NativeMethods.ImmGetContext(_hwnd);
        if (himc == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var conversionOk = NativeMethods.ImmSetConversionStatus(himc, _originalConversion, _originalSentence);
            var openOk = NativeMethods.ImmSetOpenStatus(himc, _originalOpenStatus);
            _log.Record("imm32", "imm32-api", "restore-native-mode", new Dictionary<string, object?>
            {
                ["label"] = _label,
                ["restored_conversion"] = $"0x{_originalConversion:X8}",
                ["restored_open_status"] = _originalOpenStatus,
                ["ImmSetConversionStatus_ok"] = conversionOk,
                ["ImmSetOpenStatus_ok"] = openOk,
            });
        }
        finally
        {
            NativeMethods.ImmReleaseContext(_hwnd, himc);
        }
    }


    /// <summary>安装窗口过程子类化；失败抛出，所有后续消息先经过本探针。</summary>
    internal void InstallSubclass()
    {
        if (_subclassInstalled || _disposed)
        {
            return;
        }

        _wndProcDelegate = OnWndProc;
        var newProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate);
        var previous = NativeMethods.SetWindowProc(_hwnd, newProc);
        var error = Marshal.GetLastWin32Error();
        if (previous == IntPtr.Zero && error != 0)
        {
            _log.Record("wndproc", "hwnd-subclass", "install-failed", new Dictionary<string, object?>
            {
                ["hwnd"] = $"0x{_hwnd.ToInt64():X}",
                ["win32_error"] = error,
            });
            throw new InvalidOperationException($"SetWindowLongPtr(GWLP_WNDPROC) 失败，Win32 错误 {error}。");
        }

        _originalWndProc = previous;
        _subclassInstalled = true;
        _log.Record("wndproc", "hwnd-subclass", "install-ok", new Dictionary<string, object?>
        {
            ["label"] = _label,
            ["hwnd"] = $"0x{_hwnd.ToInt64():X}",
            ["original_wndproc"] = $"0x{_originalWndProc.ToInt64():X}",
            ["delegate"] = "ImeProbeWindow.OnWndProc",
            ["unit"] = "pointer",
        });
    }

    /// <summary>恢复原窗口过程；已恢复或窗口已销毁时为幂等空操作。</summary>
    internal void UninstallSubclass() => RestoreSubclass("dispose");

    private void RestoreSubclass(string trigger)
    {
        if (!_subclassInstalled)
        {
            return;
        }

        try
        {
            if (!NativeMethods.IsWindow(_hwnd))
            {
                // 窗口已销毁：没有可恢复的目标，视为已不处于安装状态，但保留委托引用直到进程结束。
                _subclassInstalled = false;
                _restoreOutcomes.Add($"{trigger}:window-gone");
                SafeLog("wndproc", "hwnd-subclass", "uninstall-window-gone", new Dictionary<string, object?>
                {
                    ["label"] = _label,
                    ["trigger"] = trigger,
                });
                return;
            }

            if (_restoreFailedForFaultInjection)
            {
                // 故障注入：完全不执行恢复动作，模拟“恢复无效”。委托必须继续保活。
                _subclassInstalled = true;
                SubclassRestored = false;
                _restoreOutcomes.Add($"{trigger}:failed");
                SafeLog("wndproc", "hwnd-subclass", "uninstall-failed", new Dictionary<string, object?>
                {
                    ["label"] = _label,
                    ["trigger"] = trigger,
                    ["fault_injection"] = true,
                    ["delegate_kept_alive"] = true,
                });
                return;
            }

            var previous = NativeMethods.SetWindowProc(_hwnd, _originalWndProc);
            var error = Marshal.GetLastWin32Error();
            var current = NativeMethods.GetWindowProc(_hwnd);
            var verified = current == _originalWndProc
                && previous == Marshal.GetFunctionPointerForDelegate(_wndProcDelegate);

            if (verified)
            {
                SubclassRestored = true;
                _subclassInstalled = false;
                _restoreOutcomes.Add($"{trigger}:ok");
                SafeLog("wndproc", "hwnd-subclass", "uninstall-ok", new Dictionary<string, object?>
                {
                    ["label"] = _label,
                    ["trigger"] = trigger,
                    ["hwnd"] = $"0x{_hwnd.ToInt64():X}",
                    ["restored"] = $"0x{_originalWndProc.ToInt64():X}",
                    ["previous_proc"] = $"0x{previous.ToInt64():X}",
                    ["current_proc_after"] = $"0x{current.ToInt64():X}",
                    ["verified"] = true,
                    ["callback_exceptions"] = _callbackExceptionCount,
                });
                return;
            }

            // 恢复失败：不能把仍然被原生代码引用的委托交给 GC，也不能虚报清理成功。
            _subclassInstalled = true;
            SubclassRestored = false;
            _restoreOutcomes.Add($"{trigger}:failed");
            PinnedProcs.Add(_wndProcDelegate);
            SafeLog("wndproc", "hwnd-subclass", "uninstall-failed", new Dictionary<string, object?>
            {
                ["label"] = _label,
                ["trigger"] = trigger,
                ["hwnd"] = $"0x{_hwnd.ToInt64():X}",
                ["expected"] = $"0x{_originalWndProc.ToInt64():X}",
                ["previous_proc"] = $"0x{previous.ToInt64():X}",
                ["current_proc_after"] = $"0x{current.ToInt64():X}",
                ["win32_error"] = error,
                ["delegate_kept_alive"] = true,
                ["fault_injection"] = _restoreFailedForFaultInjection,
            });
        }
        catch (Exception ex)
        {
            _subclassInstalled = true;
            SubclassRestored = false;
            _restoreOutcomes.Add($"{trigger}:exception");
            SafeLog("wndproc", "hwnd-subclass", "uninstall-failed", new Dictionary<string, object?>
            {
                ["label"] = _label,
                ["trigger"] = trigger,
                ["exception"] = $"{ex.GetType().Name}: {ex.Message}",
                ["delegate_kept_alive"] = true,
            });
        }
    }

    /// <summary>记录事件泵/渲染异常；只有明确处于销毁路径时才允许忽略。</summary>
    internal void LogPumpException(string source, Exception exception, bool expected)
    {
        _pumpExceptionCount += expected ? 0 : 1;
        SafeLog("probe", "event-pump", "pump-exception", new Dictionary<string, object?>
        {
            ["label"] = _label,
            ["source"] = source,
            ["exception"] = $"{exception.GetType().Name}: {exception.Message}",
            ["expected_during_teardown"] = expected,
            ["unexpected_count"] = _pumpExceptionCount,
        });
    }

    /// <summary>日志写入失败也不能影响原生回调边界。</summary>
    private void SafeLog(string stage, string source, string kind, IDictionary<string, object?> fields)
    {
        try
        {
            _log.Record(stage, source, kind, fields);
        }
        catch (Exception)
        {
            // 故意忽略：日志失败不改变控制流。
        }
    }

    /// <summary>让一次指定的消息在托管回调内抛异常，用于验证异常不穿回原生栈（合成边界测试）。</summary>
    /// <param name="mode">none / message / log / restore。</param>
    /// <param name="message">mode=message 时要抛异常的消息号。</param>
    internal void EnableFaultInjection(string mode, uint message)
    {
        _faultInjectMode = mode;
        _faultInjectMessage = message;
        if (mode == "restore")
        {
            _restoreFailedForFaultInjection = true;
        }

        _log.Record("wndproc", "synthetic", "fault-injection-armed", new Dictionary<string, object?>
        {
            ["mode"] = mode,
            ["message"] = mode == "message" ? NativeMethods.MessageName(message) : "(n/a)",
            ["note"] = "synthetic boundary test; not a real IME path",
        });
    }

    internal void HookGlfwEvents()
    {
        _window.KeyDown += e =>
        {
            _keyDownCount++;
            if (e.IsRepeat)
            {
                _keyRepeatCount++;
            }

            _log.Record("glfw", "glfw-key-callback", "key-down", new Dictionary<string, object?>
            {
                ["key"] = e.Key.ToString(),
                ["is_repeat"] = e.IsRepeat,
                ["modifiers"] = e.Modifiers.ToString(),
                ["text_input_count_so_far"] = _textInputCount,
            });
        };

        _window.KeyUp += e => _log.Record("glfw", "glfw-key-callback", "key-up", new Dictionary<string, object?>
        {
            ["key"] = e.Key.ToString(),
            ["modifiers"] = e.Modifiers.ToString(),
        });

        _window.TextInput += e =>
        {
            _textInputCount++;
            var text = e.AsString;
            if (_textInputs.Count < MaxTextInputs)
            {
                _textInputs.Add(text);
            }

            _log.Record("glfw", "glfw-char-callback", "text-input", new Dictionary<string, object?>
            {
                ["unicode_int"] = e.Unicode,
                ["unicode_hex"] = $"U+{e.Unicode:X}",
                ["text"] = text,
                ["utf16"] = ImeFormat.EscapeUtf16(text),
                ["utf16_units"] = ImeFormat.CodeUnitCount(text),
                ["code_points"] = ImeFormat.CodePointCount(text),
                ["unit"] = "UTF-32 code point (OpenTK TextInputEventArgs.Unicode is int)",
            });
        };

        _window.FocusedChanged += e =>
        {
            _log.Record("glfw", "glfw-focus-callback", "focus-changed", new Dictionary<string, object?>
            {
                ["focused"] = e.IsFocused,
                ["composing"] = _composing,
            });

            if (!e.IsFocused && _composing)
            {
                RequestCapture("focus-lost-while-composing", 0);
            }
        };

        _window.Resize += e => _log.Record("glfw", "glfw-resize-callback", "resize", new Dictionary<string, object?>
        {
            ["client_size"] = DescribeClientSize(),
            ["caret_client"] = $"{CaretClientPoint.X},{CaretClientPoint.Y}",
            ["unit"] = "window-content-pixels",
        });

        _window.Move += e =>
        {
            NativeMethods.GetWindowRect(_hwnd, out var rect);
            _log.Record("glfw", "glfw-move-callback", "move", new Dictionary<string, object?>
            {
                ["window_rect"] = $"{rect.Left},{rect.Top},{rect.Right},{rect.Bottom}",
                ["unit"] = "screen-pixels",
            });
        };
    }

    /// <summary>按触发点请求一次系统候选窗定位，并读回 IME 报告的位置。</summary>
    internal void RequestCandidateAnchor(string trigger) => RequestCandidateAnchorAtPoint(trigger, CaretClientPoint);

    /// <summary>
    /// 在指定的<b>客户区</b>坐标发布候选/组合锚点，并读回 IME 报告的位置。
    /// </summary>
    /// <remarks>
    /// 只用来自有窗口内部的坐标：<see cref="PublishCandidateAnchor"/> 会记录窗口矩形，调用方必须核对
    /// 两次发布的窗口矩形完全相同，才能把“候选位置随锚点变化”归因于锚点而不是窗口移动。
    /// </remarks>
    internal void RequestCandidateAnchorAtPoint(string trigger, (int X, int Y) clientPoint)
    {
        var himc = NativeMethods.ImmGetContext(_hwnd);
        if (himc == IntPtr.Zero)
        {
            _log.Record("imm32", "imm32-api", "candidate-anchor-skipped", new Dictionary<string, object?>
            {
                ["trigger"] = trigger,
                ["reason"] = "ImmGetContext returned NULL",
            });
            return;
        }

        try
        {
            PublishCandidateAnchor(himc, trigger, clientPoint);
        }
        finally
        {
            NativeMethods.ImmReleaseContext(_hwnd, himc);
        }
    }

    /// <summary>自有窗口内部的候选锚点：客户区左上偏内 (a) 与中间偏右下 (b)，都在窗口矩形之内。</summary>
    internal (int X, int Y) InWindowAnchorPoint(int which)
    {
        var client = _window.ClientSize;
        var width = Math.Max(40, client.X);
        var height = Math.Max(40, client.Y);
        return which == 0
            ? (Math.Clamp(width / 6, 8, width - 8), Math.Clamp(height / 4, 8, height - 8))
            : (Math.Clamp(width * 2 / 3, 8, width - 8), Math.Clamp(height * 3 / 4, 8, height - 8));
    }

    /// <summary>
    /// 只读诊断：把当前 IMM 上下文的“中/英”相关状态（open + conversion mode）与 TSF 报告的 profile
    /// 身份放在同一时刻比较。只读，不改变任何状态。
    /// </summary>
    /// <remarks>
    /// 用途：当真实输入法只产生英文字流时，需要判断到底是 TSF 侧的中文 profile 没生效，还是 IMM 上下文
    /// 仍处于非 native 模式 —— 两者是不同的问题，不能只凭“看到英文字母”下结论。
    /// </remarks>
    internal (bool Ok, bool Open, uint Conversion, bool Native, bool Roman, string Himc) ReadImmModeDiagnostics()
    {
        var himc = NativeMethods.ImmGetContext(_hwnd);
        if (himc == IntPtr.Zero)
        {
            return (false, false, 0, false, false, "(null)");
        }

        try
        {
            var open = NativeMethods.ImmGetOpenStatus(himc);
            var ok = NativeMethods.ImmGetConversionStatus(himc, out var conversion, out _);
            return (
                ok,
                open,
                conversion,
                (conversion & NativeMethods.IME_CMODE_NATIVE) != 0,
                (conversion & NativeMethods.IME_CMODE_ROMAN) != 0,
                $"0x{himc.ToInt64():X}");
        }
        finally
        {
            NativeMethods.ImmReleaseContext(_hwnd, himc);
        }
    }

    /// <summary>当前窗口在屏幕坐标下的矩形（用于核对两次锚点发布的窗口矩形是否一致）。</summary>
    internal string CurrentWindowRect()
    {
        NativeMethods.GetWindowRect(_hwnd, out var rect);
        return $"{rect.Left},{rect.Top},{rect.Right},{rect.Bottom}";
    }

    /// <summary>渲染一帧：纯色背景 + caret 标记（用 scissor+clear，不需要 shader）。</summary>
    internal void RenderFrame()
    {
        var framebuffer = _window.FramebufferSize;
        var client = _window.ClientSize;
        if (framebuffer.X <= 0 || framebuffer.Y <= 0)
        {
            return;
        }

        GL.Viewport(0, 0, framebuffer.X, framebuffer.Y);
        GL.ClearColor(0.10f, 0.12f, 0.16f, 1f);
        GL.Clear(ClearBufferMask.ColorBufferBit);

        var (caretX, caretY) = CaretClientPoint;
        var scaleX = client.X > 0 ? (float)framebuffer.X / client.X : 1f;
        var scaleY = client.Y > 0 ? (float)framebuffer.Y / client.Y : 1f;
        var width = Math.Max(2, (int)(2 * scaleX));
        var height = Math.Max(6, (int)(20 * scaleY));
        var glX = (int)(caretX * scaleX);
        var glY = framebuffer.Y - (int)(caretY * scaleY) - height;

        GL.Enable(EnableCap.ScissorTest);
        GL.Scissor(glX, glY, width, height);
        GL.ClearColor(0.95f, 0.80f, 0.20f, 1f);
        GL.Clear(ClearBufferMask.ColorBufferBit);
        GL.Disable(EnableCap.ScissorTest);

        _window.Context.SwapBuffers();
    }

    /// <summary>
    /// 在原生回调（WndProc / GLFW 回调）内登记一次截图请求。回调只入队、不休眠、不抓屏，
    /// 由事件泵在消息返回且 RenderFrame 之后调用 <see cref="FlushDeferredCaptures"/> 执行。
    /// </summary>
    internal void RequestCapture(string tag, int delayMs = 0)
    {
        if (_captureDirectory is null || _deferredCaptures.Count >= 16)
        {
            return;
        }

        _deferredCaptures.Add((tag, Math.Max(0, delayMs)));
    }

    /// <summary>在消息循环返回、画面更新后执行已登记的截图（允许短暂 settle，不阻塞原生回调）。</summary>
    internal void FlushDeferredCaptures()
    {
        if (_captureDirectory is null || _deferredCaptures.Count == 0 || _deferredCaptureRunning)
        {
            return;
        }

        _deferredCaptureRunning = true;
        try
        {
            foreach (var (tag, delay) in _deferredCaptures)
            {
                CaptureScreenshot(tag, delay);
            }

            _deferredCaptures.Clear();
        }
        finally
        {
            _deferredCaptureRunning = false;
        }
    }

    /// <summary>
    /// 抓取自有窗口范围的证据截图：只含自有测试窗口矩形，且窗口必须可见。
    /// 不提供任意 margin 扩张，也绝不抓取整个桌面；窗口外候选需要证据时必须先确证其归属与边界。
    /// </summary>
    internal void CaptureScreenshot(string tag, int delayMs = 0)
    {
        if (_captureDirectory is null)
        {
            return;
        }

        if (delayMs > 0)
        {
            Thread.Sleep(delayMs);
        }

        var name = $"{DateTime.Now:HHmmss}-{_label}-{++_captureCount:D2}-{tag}.png";
        var path = Path.Combine(_captureDirectory, name);
        var visible = NativeMethods.IsWindowVisible(_hwnd);
        var foreground = NativeMethods.GetForegroundWindow() == _hwnd;
        // 记录截图时窗口自身的屏幕矩形：两次锚点截图必须矩形一致，才能把差异归因于锚点而非窗口移动。
        NativeMethods.GetWindowRect(_hwnd, out var windowRect);
        var result = ScreenCapture.CaptureWindowRegion(_hwnd, path);
        if (result is { } region)
        {
            _log.Record("capture", "gdi-bitblt", "screenshot-saved", new Dictionary<string, object?>
            {
                ["tag"] = tag,
                ["file"] = region.Path,
                ["screen_rect"] = $"{region.X},{region.Y},{region.Width},{region.Height}",
                ["window_rect_screen"] = $"{windowRect.Left},{windowRect.Top},{windowRect.Right},{windowRect.Bottom}",
                ["window_visible"] = visible,
                ["window_foreground"] = foreground,
                ["scope"] = "own visible window rect only; no arbitrary margin and never the whole desktop",
                ["unit"] = "screen-pixels",
            });
        }
        else
        {
            _log.Record("capture", "gdi-bitblt", "screenshot-failed", new Dictionary<string, object?>
            {
                ["tag"] = tag,
                ["file"] = path,
                ["window_visible"] = visible,
                ["window_foreground"] = foreground,
                ["reason"] = "window not visible, no window rect, or GDI capture failed",
            });
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        // 先销毁窗口（消息路径仍装着子类化过程，可记录 WM_IME_*/WM_DESTROY/WM_NCDESTROY 与恢复动作），
        // 再执行幂等恢复作为兜底。
        try
        {
            _window.Dispose();
        }
        catch (Exception ex)
        {
            _log.Record("probe", "probe-window", "dispose-failed", new Dictionary<string, object?>
            {
                ["exception"] = $"{ex.GetType().Name}: {ex.Message}",
            });
        }

        UninstallSubclass();
        // 恢复失败时委托仍可能被原生代码引用：这里无条件保活到进程结束，不释放。
        GC.KeepAlive(_wndProcDelegate);
    }

    private IntPtr OnWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        // 托管异常绝不穿回原生栈：捕获、计数、尽量记录，然后继续链式调用。
        // 该边界不依赖日志写入成功：所有日志都走 SafeLog。
        try
        {
            if (_faultInjectMode == "log" && _faultInjectMessage != 0 && msg == _faultInjectMessage)
            {
                // 故障注入：下一条日志写入失败（验证日志失败也不穿回原生回调）。
                _faultInjectMessage = 0;
                _log.ThrowOnNextWrite = true;
            }

            if (_faultInjectMessage != 0 && msg == _faultInjectMessage)
            {
                _faultInjectMessage = 0;
                throw new InvalidOperationException($"fault injection for synthetic message {NativeMethods.MessageName(msg)}");
            }

            var overridden = HandleMessage(hwnd, msg, wParam, lParam);
            if (overridden is { } value)
            {
                return value;
            }
        }
        catch (Exception ex)
        {
            _callbackExceptionCount++;
            SafeLog("wndproc", "hwnd-subclass", "callback-exception", new Dictionary<string, object?>
            {
                ["message"] = NativeMethods.MessageName(msg),
                ["message_hex"] = $"0x{msg:X4}",
                ["exception"] = $"{ex.GetType().Name}: {ex.Message}",
                ["count"] = _callbackExceptionCount,
                ["note"] = "swallowed inside the native callback boundary",
            });
        }

        try
        {
            return NativeMethods.CallWindowProc(_originalWndProc, hwnd, msg, wParam, lParam);
        }
        catch (Exception ex)
        {
            _callbackExceptionCount++;
            SafeLog("wndproc", "hwnd-subclass", "chain-failed", new Dictionary<string, object?>
            {
                ["message"] = NativeMethods.MessageName(msg),
                ["exception"] = $"{ex.GetType().Name}: {ex.Message}",
                ["note"] = "returned a safe default instead of crossing the native boundary",
            });
            return IntPtr.Zero;
        }
    }

    /// <summary>处理一条消息；返回非 null 表示覆盖链式调用的返回值。</summary>
    private IntPtr? HandleMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case NativeMethods.WM_IME_STARTCOMPOSITION:
                _composing = true;
                _compositionId++;
                LogMessage(msg, wParam, lParam);
                OnCompositionStart();
                return null;

            case NativeMethods.WM_IME_COMPOSITION:
                _compositionUpdateCount++;
                LogMessage(msg, wParam, lParam);
                OnCompositionUpdate(lParam);
                return null;

            case NativeMethods.WM_IME_ENDCOMPOSITION:
                LogMessage(msg, wParam, lParam);
                OnCompositionEnd();
                return null;

            case NativeMethods.WM_IME_NOTIFY:
                LogMessage(msg, wParam, lParam);
                OnImeNotify(wParam, lParam);
                return null;

            case NativeMethods.WM_IME_SETCONTEXT:
                LogMessage(msg, wParam, lParam);
                OnImeSetContext(wParam);
                return null;

            case NativeMethods.WM_IME_REQUEST:
                LogMessage(msg, wParam, lParam);
                return OnImeRequest(wParam, lParam);

            case NativeMethods.WM_IME_CHAR:
                _imeCharMessageCount++;
                LogMessage(msg, wParam, lParam, extra: new Dictionary<string, object?>
                {
                    ["code_unit"] = wParam.ToInt32(),
                    ["utf16"] = ImeFormat.EscapeUtf16(((char)wParam.ToInt32()).ToString()),
                    ["note"] = "WM_IME_CHAR passes to DefWindowProc which may post WM_CHAR (duplicate path check)",
                });
                return null;

            case NativeMethods.WM_CHAR:
            case NativeMethods.WM_SYSCHAR:
                _charMessageCount++;
                LogMessage(msg, wParam, lParam, extra: new Dictionary<string, object?>
                {
                    ["code_unit"] = wParam.ToInt32(),
                    ["code_unit_hex"] = $"0x{wParam.ToInt32():X4}",
                    ["is_high_surrogate"] = wParam.ToInt32() is >= 0xD800 and <= 0xDBFF,
                    ["is_low_surrogate"] = wParam.ToInt32() is >= 0xDC00 and <= 0xDFFF,
                    ["unit"] = "UTF-16 code unit",
                });
                return null;

            case NativeMethods.WM_UNICHAR:
                _unicharMessageCount++;
                LogMessage(msg, wParam, lParam, extra: new Dictionary<string, object?>
                {
                    ["value"] = wParam.ToInt32(),
                    ["unit"] = "UTF-32 code point",
                });
                return null;

            case NativeMethods.WM_KEYDOWN:
            case NativeMethods.WM_SYSKEYDOWN:
            case NativeMethods.WM_KEYUP:
            case NativeMethods.WM_SYSKEYUP:
                LogMessage(msg, wParam, lParam, extra: new Dictionary<string, object?>
                {
                    ["virtual_key"] = $"0x{wParam.ToInt32():X2}",
                    ["virtual_key_name"] = VirtualKeyName(wParam.ToInt32()),
                    ["repeat_bit"] = (lParam.ToInt64() & (1L << 30)) != 0,
                    ["extended_bit"] = (lParam.ToInt64() & (1L << 24)) != 0,
                    ["note"] = wParam.ToInt32() == 0xE5 ? "VK_PROCESSKEY: IME consumed this key" : string.Empty,
                });
                return null;

            case NativeMethods.WM_INPUTLANGCHANGE:
                LogMessage(msg, wParam, lParam);
                LogInputLanguage("WM_INPUTLANGCHANGE");
                return null;

            case NativeMethods.WM_SETFOCUS:
            case NativeMethods.WM_KILLFOCUS:
            case NativeMethods.WM_ACTIVATE:
            case NativeMethods.WM_ACTIVATEAPP:
            case NativeMethods.WM_CLOSE:
            case NativeMethods.WM_DESTROY:
            case NativeMethods.WM_SIZE:
            case NativeMethods.WM_MOVE:
                LogMessage(msg, wParam, lParam);
                return null;

            case NativeMethods.WM_NCDESTROY:
                LogMessage(msg, wParam, lParam);
                // 标准做法：窗口销毁时在消息路径内恢复原窗口过程，之后 Dispose 的恢复成为幂等空操作。
                RestoreSubclass("WM_NCDESTROY");
                return null;

            default:
                _skippedMessageCount++;
                return null;
        }
    }

    private void LogMessage(uint msg, IntPtr wParam, IntPtr lParam, IDictionary<string, object?>? extra = null)
    {
        var fields = new Dictionary<string, object?>
        {
            ["label"] = _label,
            ["hwnd"] = $"0x{_hwnd.ToInt64():X}",
            ["msg"] = NativeMethods.MessageName(msg),
            ["msg_hex"] = $"0x{msg:X4}",
            ["wparam"] = $"0x{wParam.ToInt64():X}",
            ["lparam"] = $"0x{lParam.ToInt64():X}",
            ["native_tid"] = NativeMethods.GetWindowThreadProcessId(_hwnd, out var pid),
            ["window_pid"] = pid,
            ["unit"] = "win32-message",
        };

        if (msg == NativeMethods.WM_IME_COMPOSITION)
        {
            var flags = NativeMethods.GcsFlags(lParam);
            fields["gcs_flags"] = flags.Count == 0 ? "(none)" : string.Join("|", flags);
            fields["gcs_flags_hex"] = $"0x{lParam.ToInt64():X}";
        }
        else if (msg == NativeMethods.WM_IME_NOTIFY)
        {
            fields["imn"] = NativeMethods.ImnName(wParam);
            fields["imn_lparam"] = $"0x{lParam.ToInt64():X}";
        }
        else if (msg == NativeMethods.WM_IME_SETCONTEXT)
        {
            fields["fSet"] = wParam.ToInt64() != 0;
            fields["isc_flags"] = $"0x{lParam.ToInt64():X}";
            fields["isc_showuicompositionwindow"] = (lParam.ToInt64() & 0x1) != 0;
            fields["isc_showuicandidatewindow"] = (lParam.ToInt64() & 0x2) != 0;
            fields["isc_showuistatuswindow"] = (lParam.ToInt64() & 0x4) != 0;
        }

        if (extra is not null)
        {
            foreach (var pair in extra)
            {
                fields[pair.Key] = pair.Value;
            }
        }

        _log.Record("wndproc", "hwnd-subclass", "message", fields);
    }

    private void OnCompositionStart()
    {
        _resultSeenInComposition = false;
        // 新组合会话开始：清空预编辑历史，保证提交内容的归因只看本会话（不混入旧会话残留）。
        _preeditStrings.Clear();
        // 原生回调内只登记（连同当时的 seq / compositionId）：真正的只读 GetActiveProfile 查询
        // 在消息返回、窗口过程退出后由事件泵执行，日志会把“登记时刻”和“查询时刻”分开写。
        IdentityProbe.RequestPending("first-composition", _log.LastSequence, _compositionId);
        var himc = NativeMethods.ImmGetContext(_hwnd);
        try
        {
            var (description, fileName) = ReadImeIdentity(himc);
            _imeDescription = description;
            _imeFileName = fileName;
            var (conversion, sentence) = ReadConversionStatus(himc);
            _log.Record("imm32", "imm32-api", "composition-start", new Dictionary<string, object?>
            {
                ["label"] = _label,
                ["composition_id"] = _compositionId,
                ["himc"] = $"0x{himc.ToInt64():X}",
                ["ime_description"] = description,
                ["ime_file_name"] = fileName,
                ["conversion"] = $"0x{conversion:X8}",
                ["sentence"] = $"0x{sentence:X8}",
                ["cmode_native"] = (conversion & NativeMethods.IME_CMODE_NATIVE) != 0,
                ["cmode_alphanumeric"] = (conversion & NativeMethods.IME_CMODE_LANGUAGE) == 0,
                ["caret_client"] = $"{CaretClientPoint.X},{CaretClientPoint.Y}",
                ["window_dpi"] = WindowDpi,
            });

            PublishCandidateAnchor(himc, "composition-start");
        }
        finally
        {
            if (himc != IntPtr.Zero)
            {
                NativeMethods.ImmReleaseContext(_hwnd, himc);
            }
        }

        // 原生回调内只登记截图请求；真正的抓屏在消息返回、画面更新后由事件泵执行，绝不在 WndProc 里休眠。
        RequestCapture("composition-start", 120);    }

    private void OnCompositionUpdate(IntPtr lParam)
    {
        var himc = NativeMethods.ImmGetContext(_hwnd);
        if (himc == IntPtr.Zero)
        {
            _log.Record("imm32", "imm32-api", "composition-update-no-context", new Dictionary<string, object?>
            {
                ["composition_id"] = _compositionId,
            });
            return;
        }

        try
        {
            var flags = (ulong)lParam.ToInt64();
            if (!FirstCompositionUpdateSeen)
            {
                FirstCompositionUpdateSeen = true;
                // 首次真实合成更新：回调内登记（带当时的 seq / compositionId），查询在消息过程之外执行。
                IdentityProbe.RequestPending("first-composition-update", _log.LastSequence, _compositionId);
            }

            var compFlagSet = (flags & NativeMethods.GCS_COMPSTR) != 0;
            var resultFlagSet = (flags & NativeMethods.GCS_RESULTSTR) != 0;
            var compStr = ReadString(himc, NativeMethods.GCS_COMPSTR, out var compBytes);
            var resultStr = ReadString(himc, NativeMethods.GCS_RESULTSTR, out var resultBytes);
            var compAttrs = ReadBytes(himc, NativeMethods.GCS_COMPATTR);
            var compClauses = ReadDwords(himc, NativeMethods.GCS_COMPCLAUSE);
            var resultClauses = ReadDwords(himc, NativeMethods.GCS_RESULTCLAUSE);
            var cursorReturn = NativeMethods.ImmGetCompositionStringW(himc, NativeMethods.GCS_CURSORPOS, null, 0);
            var candidates = ReadCandidates(himc);
            _lastCandidateCount = candidates.Count;

            // 提交计数只由“本条消息的 GCS_RESULTSTR 标志 + 本条查询到的非空结果串”决定。
            // 无标志但查询仍有数据时只作快照，避免把上下文里保留的旧结果/分段状态重复计入。
            var isNewResult = CompositionObservation.IsNewResult(resultFlagSet, resultStr);
            var isSnapshotOnly = CompositionObservation.IsSnapshotOnly(resultFlagSet, resultStr);
            if (CompositionObservation.HasResultAndPreedit(resultFlagSet, resultStr, compFlagSet, compStr))
            {
                // 同一条消息里同时出现“已提交结果 + 后续预编辑”：TI-04 状态机必须按顺序处理。
                _resultAndPreeditSameMessage = true;
            }

            if (isNewResult)
            {
                _commitCount++;
                _resultFlagCommitCount++;
                _resultSeenInComposition = true;
                _resultSegments.Add(resultStr!);
                // 关键：为**这条结果**冻结它所属组合会话的预编辑历史快照，并**立即清空当前段历史**。
                // 复制只防止过去被改写；同一段 message 若还带“结果 + 后续预编辑”，后续 compStr 属于下一段，
                // 而下一段不一定有新的 WM_IME_STARTCOMPOSITION，所以必须在这里重置，
                // 否则上一段（例如 nihao）会污染下一段结果的快照。
                var frozen = CommitSnapshotSlicer.FreezeResultAndResetHistory(
                    resultStr!, _compositionId, _preeditStrings);
                if (_commitSnapshots.Count < MaxTextInputs)
                {
                    // 日志集合满时只丢弃这条快照记录；历史重置已在上一步必然发生，污染防护不依赖它。
                    _commitSnapshots.Add(frozen);
                }

                // 同上：回调内只登记，查询在消息过程之外执行，且日志分别记录登记时刻与查询时刻。
                IdentityProbe.RequestPending(
                    $"result:{ImeFormat.EscapeUtf16(resultStr!)}", _log.LastSequence, _compositionId);
            }
            else if (isSnapshotOnly)
            {
                _snapshotOnlyResultCount++;
            }

            var entry =
                $"#{_compositionUpdateCount} flags={string.Join("|", NativeMethods.GcsFlags(lParam))} " +
                $"comp={ImeFormat.EscapeUtf16(compStr)} result={ImeFormat.EscapeUtf16(resultStr)} cursor={cursorReturn}";
            if (_compositionUpdates.Count < MaxTextInputs)
            {
                _compositionUpdates.Add(entry);
            }

            if (compFlagSet && !string.IsNullOrEmpty(compStr) && _preeditStrings.Count < MaxTextInputs)
            {
                // 本会话的预编辑历史（用于把 results 归因到本场景的输入）。
                _preeditStrings.Add(compStr);
            }

            var fields = new Dictionary<string, object?>
            {
                ["label"] = _label,
                ["composition_id"] = _compositionId,
                ["update_index"] = _compositionUpdateCount,
                ["flags"] = string.Join("|", NativeMethods.GcsFlags(lParam)),
                ["result_flag_set"] = resultFlagSet,
                ["comp_flag_set"] = compFlagSet,
                ["counted_as_new_result"] = isNewResult,
                ["snapshot_only_result"] = isSnapshotOnly,
                ["comp_str"] = compStr,
                ["comp_str_utf16"] = ImeFormat.EscapeUtf16(compStr),
                ["comp_str_utf16_units"] = ImeFormat.CodeUnitCount(compStr),
                ["comp_str_bytes_reported"] = compBytes,
                ["comp_attr_hex"] = ImeFormat.ToHex(compAttrs),
                ["comp_attr_bytes"] = compAttrs?.Length ?? 0,
                ["comp_clause_dwords"] = ImeFormat.ToHex(compClauses),
                ["comp_clause_count"] = compClauses?.Length ?? 0,
                ["cursor_pos_return"] = cursorReturn,
                ["cursor_pos_unit"] = "MSDN: GCS_CURSORPOS return value is the cursor position (not a byte size)",
                ["result_str"] = resultStr,
                ["result_str_utf16"] = ImeFormat.EscapeUtf16(resultStr),
                ["result_str_utf16_units"] = ImeFormat.CodeUnitCount(resultStr),
                ["result_str_bytes_reported"] = resultBytes,
                ["result_clause_dwords"] = ImeFormat.ToHex(resultClauses),
                ["candidate_count"] = candidates.Count,
                ["candidates"] = string.Join(" | ", candidates.Select(c => $"{(c.IsSelected ? "*" : " ")}{c.Index}:{c.Text}")),
                ["unit"] = "UTF-16 code units unless stated otherwise",
                ["composing"] = _composing,
            };

            _log.Record("imm32", "imm32-api", "composition-update", fields);

            if (candidates.Count > 0 && _candidateRequestCount < 32)
            {
                PublishCandidateAnchor(himc, $"candidate-open({candidates.Count})");
            }
        }
        finally
        {
            NativeMethods.ImmReleaseContext(_hwnd, himc);
        }

        RequestCapture(((ulong)lParam.ToInt64() & NativeMethods.GCS_RESULTSTR) != 0
            ? "composition-update-result"
            : "composition-update", 120);
    }

    private void OnCompositionEnd()
    {
        _composing = false;
        if (!_resultSeenInComposition)
        {
            _cancelCount++;
        }

        _log.Record("imm32", "imm32-api", "composition-end", new Dictionary<string, object?>
        {
            ["label"] = _label,
            ["composition_id"] = _compositionId,
            ["updates"] = _compositionUpdateCount,
            ["commits"] = _commitCount,
            ["result_seen_in_composition"] = _resultSeenInComposition,
            ["ended_without_result"] = !_resultSeenInComposition,
            ["cancellations"] = _cancelCount,
            ["result_segments"] = string.Join("|", _resultSegments.Select(ImeFormat.EscapeUtf16)),
        });
        RequestCapture("composition-end", 120);
    }

    private void OnImeNotify(IntPtr wParam, IntPtr lParam)
    {
        var command = (ulong)wParam.ToInt64();
        var fields = new Dictionary<string, object?>
        {
            ["label"] = _label,
            ["composition_id"] = _compositionId,
            ["command"] = NativeMethods.ImnName(wParam),
            ["lparam"] = $"0x{lParam.ToInt64():X}",
            ["composing"] = _composing,
        };

        if (command is NativeMethods.IMN_OPENCANDIDATE or NativeMethods.IMN_CHANGECANDIDATE)
        {
            var himc = NativeMethods.ImmGetContext(_hwnd);
            try
            {
                var candidates = ReadCandidates(himc);
                fields["candidate_count"] = candidates.Count;
                fields["candidates"] = string.Join(" | ", candidates.Select(c => $"{(c.IsSelected ? "*" : " ")}{c.Index}:{c.Text}"));
                fields["unit"] = "UTF-16 code units";
                if (candidates.Count > 0 && command == NativeMethods.IMN_OPENCANDIDATE)
                {
                    PublishCandidateAnchor(himc, "imn-opencandidate");
                    RequestCapture("candidate-open", 150);
                }
            }
            finally
            {
                if (himc != IntPtr.Zero)
                {
                    NativeMethods.ImmReleaseContext(_hwnd, himc);
                }
            }
        }
        else if (command == NativeMethods.IMN_CLOSECANDIDATE)
        {
            RequestCapture("candidate-close", 0);
        }
        else if (command == NativeMethods.IMN_SETCANDIDATEPOS)
        {
            // lParam 是候选列表索引（每个 bit 对应一个列表）。
            fields["candidate_list_mask"] = $"0x{lParam.ToInt64():X}";
        }

        _log.Record("imm32", "imm32-api", "ime-notify", fields);
    }

    private void OnImeSetContext(IntPtr wParam)
    {
        var hasFocus = wParam.ToInt64() != 0;
        if (hasFocus)
        {
            AssociateDefaultImeContext("WM_IME_SETCONTEXT");
        }

        var himc = NativeMethods.ImmGetContext(_hwnd);
        try
        {
            if (himc == IntPtr.Zero)
            {
                _log.Record("imm32", "imm32-api", "set-context", new Dictionary<string, object?>
                {
                    ["label"] = _label,
                    ["focused"] = hasFocus,
                    ["himc"] = "(null)",
                });
                return;
            }

            var (description, fileName) = ReadImeIdentity(himc);
            _imeDescription = description;
            _imeFileName = fileName;
            _log.Record("imm32", "imm32-api", "set-context", new Dictionary<string, object?>
            {
                ["label"] = _label,
                ["focused"] = hasFocus,
                ["himc"] = $"0x{himc.ToInt64():X}",
                ["ime_description"] = description,
                ["ime_file_name"] = fileName,
            });
        }
        finally
        {
            if (himc != IntPtr.Zero)
            {
                NativeMethods.ImmReleaseContext(_hwnd, himc);
            }
        }
    }

    /// <summary>处理 WM_IME_REQUEST；返回非 null 表示已处理（覆盖链式返回值）。</summary>
    private IntPtr? OnImeRequest(IntPtr wParam, IntPtr lParam)
    {
        var request = (ulong)wParam.ToInt64();
        var fields = new Dictionary<string, object?>
        {
            ["label"] = _label,
            ["request"] = request switch
            {
                IMR_COMPOSITIONWINDOW => "IMR_COMPOSITIONWINDOW",
                IMR_CANDIDATEWINDOW => "IMR_CANDIDATEWINDOW",
                IMR_RECONVERTSTRING => "IMR_RECONVERTSTRING",
                IMR_QUERYCHARPOSITION => "IMR_QUERYCHARPOSITION",
                IMR_DOCUMENTFEED => "IMR_DOCUMENTFEED",
                _ => $"IMR_0x{request:X4}",
            },
            ["lparam"] = $"0x{lParam.ToInt64():X}",
            ["handled"] = false,
        };

        if (request == IMR_CANDIDATEWINDOW && lParam != IntPtr.Zero)
        {
            var form = BuildCandidateForm();
            Marshal.StructureToPtr(form, lParam, fDeleteOld: false);
            fields["handled"] = true;
            fields["requested_client"] = $"{form.ptCurrentPos.X},{form.ptCurrentPos.Y}";
            fields["requested_style"] = $"0x{form.dwStyle:X}";
            fields["unit"] = "client px (per MSDN CANDIDATEFORM.ptCurrentPos)";
            _log.Record("imm32", "imm32-api", "ime-request", fields);
            return new IntPtr(1);
        }

        if (request == IMR_COMPOSITIONWINDOW && lParam != IntPtr.Zero)
        {
            var (caretX, caretY) = CaretClientPoint;
            var form = new NativeMethods.COMPOSITIONFORM
            {
                dwStyle = NativeMethods.CFS_POINT,
                ptCurrentPos = new NativeMethods.POINT { X = caretX, Y = caretY },
            };
            Marshal.StructureToPtr(form, lParam, fDeleteOld: false);
            fields["handled"] = true;
            fields["requested_client"] = $"{caretX},{caretY}";
            fields["unit"] = "client px";
            _log.Record("imm32", "imm32-api", "ime-request", fields);
            return new IntPtr(1);
        }

        if (request == IMR_QUERYCHARPOSITION)
        {
            // 更正：IMR_QUERYCHARPOSITION 不是“重转换”查询，而是**组合串字符坐标**查询
            // （输入法要问“第 N 个组合字符在屏幕上哪里”，用于定位候选/组合窗）。
            // 当前探针使用的是系统默认组合 UI（WM_IME_SETCONTEXT 未屏蔽默认 UI），坐标由默认 UI 自己处理，
            // 因此这里未处理并转给默认过程是安全的；但这**不构成**“系统候选锚点不可用”的证据。
            // 本轮不实现自绘组合串所需的 dwSize/dwCharPos/屏幕坐标/行高/文档矩形几何，也不伪造通用字符几何。
            fields["note"] = "composition character coordinates are served by the default IME UI; "
                + "not implemented in this probe and it does not by itself invalidate candidate anchoring";
            fields["is_reconversion_request"] = false;
            fields["request_semantics"] = "IMR_QUERYCHARPOSITION asks for composition-string character geometry (screen position of a character)";
        }
        else if (request is IMR_RECONVERTSTRING or IMR_DOCUMENTFEED)
        {
            fields["note"] = "reconversion not implemented in TI-01 probe";
        }

        _log.Record("imm32", "imm32-api", "ime-request", fields);
        return null;
    }

    /// <summary>按客户端坐标发布候选窗锚点，并读回 IME 报告的位置。</summary>
    private void PublishCandidateAnchor(IntPtr himc, string trigger, (int X, int Y)? anchorPoint = null)
    {
        _candidateRequestCount++;
        var (caretX, caretY) = anchorPoint ?? CaretClientPoint;
        var form = BuildCandidateForm(caretX, caretY);

        var setOk = NativeMethods.ImmSetCandidateWindow(himc, ref form);
        var setError = Marshal.GetLastWin32Error();
        if (setOk)
        {
            _anchorSetOkCount++;
        }

        // CANDIDATEFORM.dwStyle 是互斥取值：一次调用只能表达“候选位置”(CFS_CANDIDATEPOS) 或
        // “排除区域”(CFS_EXCLUDE) 之一，不能把两者按位或（MSDN CANDIDATEFORM.dwStyle）。
        // 需要排除区域时单独发一次 CFS_EXCLUDE，绝不把非法组合当作候选能力结论。
        var excludeForm = form;
        excludeForm.dwStyle = NativeMethods.CFS_EXCLUDE;
        excludeForm.rcArea = new NativeMethods.RECT
        {
            Left = caretX,
            Top = caretY,
            Right = caretX + 2,
            Bottom = caretY + 24,
        };
        var setExcludeOk = NativeMethods.ImmSetCandidateWindow(himc, ref excludeForm);

        var getOk = NativeMethods.ImmGetCandidateWindow(himc, 0, out var actual);
        if (getOk)
        {
            _anchorGetOkCount++;
        }

        var compositionForm = new NativeMethods.COMPOSITIONFORM
        {
            dwStyle = NativeMethods.CFS_POINT,
            ptCurrentPos = new NativeMethods.POINT { X = caretX, Y = caretY },
        };
        var setCompositionOk = NativeMethods.ImmSetCompositionWindow(himc, ref compositionForm);

        var clientPoint = new NativeMethods.POINT { X = caretX, Y = caretY };
        NativeMethods.ClientToScreen(_hwnd, ref clientPoint);
        var windowRect = default(NativeMethods.RECT);
        NativeMethods.GetWindowRect(_hwnd, out windowRect);
        var screenPoint = new NativeMethods.POINT { X = caretX, Y = caretY };
        NativeMethods.ClientToScreen(_hwnd, ref screenPoint);

        _log.Record("imm32", "imm32-api", "candidate-anchor", new Dictionary<string, object?>
        {
            ["label"] = _label,
            ["trigger"] = trigger,
            ["request_index"] = _candidateRequestCount,
            ["requested_client"] = $"{caretX},{caretY}",
            ["requested_client_unit"] = "client px (MSDN CANDIDATEFORM.ptCurrentPos)",
            ["requested_style"] = $"0x{form.dwStyle:X}",
            ["requested_screen"] = $"{screenPoint.X},{screenPoint.Y}",
            ["requested_screen_unit"] = "screen px (same point via ClientToScreen)",
            ["window_rect_screen"] = $"{windowRect.Left},{windowRect.Top},{windowRect.Right},{windowRect.Bottom}",
            ["window_dpi"] = WindowDpi,
            ["screen_size"] = $"{NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN)}x{NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN)}",
            ["set_candidate_ok"] = setOk,
            ["set_candidate_win32_error"] = setError,
            ["set_candidate_exclude_ok"] = setExcludeOk,
            ["set_composition_ok"] = setCompositionOk,
            ["get_candidate_ok"] = getOk,
            ["ime_reported_style"] = getOk ? $"0x{actual.dwStyle:X}" : "(unavailable)",
            ["ime_reported_client_or_screen"] = getOk ? $"{actual.ptCurrentPos.X},{actual.ptCurrentPos.Y}" : "(unavailable)",
            ["ime_reported_rcarea"] = getOk ? $"{actual.rcArea.Left},{actual.rcArea.Top},{actual.rcArea.Right},{actual.rcArea.Bottom}" : "(unavailable)",
            ["note"] = "ImmGetCandidateWindow returns the IME's own report, not a foreign-window read",
        });
    }

    private NativeMethods.CANDIDATEFORM BuildCandidateForm()
    {
        var (caretX, caretY) = CaretClientPoint;
        return BuildCandidateForm(caretX, caretY);
    }

    private static NativeMethods.CANDIDATEFORM BuildCandidateForm(int caretX, int caretY)
        => new()
        {
            dwIndex = 0,
            dwStyle = NativeMethods.CFS_CANDIDATEPOS,
            ptCurrentPos = new NativeMethods.POINT { X = caretX, Y = caretY },
        };

    private void AssociateDefaultImeContext(string trigger)
    {
        var before = NativeMethods.ImmGetContext(_hwnd);
        if (before != IntPtr.Zero)
        {
            NativeMethods.ImmReleaseContext(_hwnd, before);
            return;
        }

        var assigned = NativeMethods.ImmAssociateContextEx(_hwnd, IntPtr.Zero, NativeMethods.IACE_DEFAULT);
        var after = NativeMethods.ImmGetContext(_hwnd);
        if (after != IntPtr.Zero)
        {
            NativeMethods.ImmReleaseContext(_hwnd, after);
        }

        _log.Record("imm32", "imm32-api", "associate-default-context", new Dictionary<string, object?>
        {
            ["label"] = _label,
            ["trigger"] = trigger,
            ["ImmAssociateContextEx_ok"] = assigned,
            ["context_after"] = after == IntPtr.Zero ? "(null)" : $"0x{after.ToInt64():X}",
        });
    }

    private void LogInputLanguage(string trigger)
    {
        var threadId = NativeMethods.GetWindowThreadProcessId(_hwnd, out _);
        var hkl = NativeMethods.GetKeyboardLayout(threadId);
        _log.Record("imm32", "imm32-api", "input-language", new Dictionary<string, object?>
        {
            ["label"] = _label,
            ["trigger"] = trigger,
            ["window_thread_id"] = threadId,
            ["hkl"] = $"0x{hkl.ToInt64():X8}",
            ["ImmIsIME"] = NativeMethods.ImmIsIME(hkl),
            ["unit"] = "HKL",
        });
    }

    internal void LogCurrentInputLanguage(string trigger) => LogInputLanguage(trigger);

    /// <summary>
    /// 只读诊断：读取当前窗口 IMM 上下文的输入法身份（描述 + 文件名）。
    /// 只用于佐证，不做任何状态变更；相对身份仍是 TSF <c>GetActiveProfile</c> 的报告。
    /// </summary>
    internal (string Description, string FileName) ReadImeIdentityForDiagnostics()
    {
        var himc = NativeMethods.ImmGetContext(_hwnd);
        try
        {
            return ReadImeIdentity(himc);
        }
        finally
        {
            if (himc != IntPtr.Zero)
            {
                NativeMethods.ImmReleaseContext(_hwnd, himc);
            }
        }
    }

    private (string Description, string FileName) ReadImeIdentity(IntPtr himc)
    {
        if (himc == IntPtr.Zero)
        {
            return ("(no context)", "(no context)");
        }

        var description = new char[256];
        var descriptionLength = NativeMethods.ImmGetDescriptionW(himc, description, (uint)description.Length);
        var fileName = new char[512];
        var fileNameLength = NativeMethods.ImmGetIMEFileNameW(himc, fileName, (uint)fileName.Length);
        return (
            descriptionLength > 0 ? new string(description, 0, (int)Math.Min(descriptionLength, (uint)description.Length)) : string.Empty,
            fileNameLength > 0 ? new string(fileName, 0, (int)Math.Min(fileNameLength, (uint)fileName.Length)) : string.Empty);
    }

    private (uint Conversion, uint Sentence) ReadConversionStatus(IntPtr himc)
        => himc != IntPtr.Zero && NativeMethods.ImmGetConversionStatus(himc, out var conversion, out var sentence)
            ? (conversion, sentence)
            : (0, 0);

    private List<CandidateListParser.Candidate> ReadCandidates(IntPtr himc)
    {
        if (himc == IntPtr.Zero)
        {
            return [];
        }

        var size = NativeMethods.ImmGetCandidateListW(himc, 0, null, 0);
        if (size == 0)
        {
            return [];
        }

        var buffer = new byte[size];
        var written = NativeMethods.ImmGetCandidateListW(himc, 0, buffer, size);
        if (written == 0)
        {
            return [];
        }

        return CandidateListParser.Parse(buffer, 12);
    }

    private static string? ReadString(IntPtr himc, uint index, out int reportedBytes)
    {
        reportedBytes = NativeMethods.ImmGetCompositionStringW(himc, index, null, 0);
        if (reportedBytes <= 0)
        {
            return null;
        }

        var buffer = new byte[reportedBytes];
        var written = NativeMethods.ImmGetCompositionStringW(himc, index, buffer, reportedBytes);
        if (written <= 0)
        {
            return null;
        }

        return Encoding.Unicode.GetString(buffer, 0, written);
    }

    private static byte[]? ReadBytes(IntPtr himc, uint index)
    {
        var reportedBytes = NativeMethods.ImmGetCompositionStringW(himc, index, null, 0);
        if (reportedBytes <= 0)
        {
            return null;
        }

        var buffer = new byte[reportedBytes];
        var written = NativeMethods.ImmGetCompositionStringW(himc, index, buffer, reportedBytes);
        if (written <= 0)
        {
            return null;
        }

        var result = new byte[written];
        Array.Copy(buffer, result, written);
        return result;
    }

    private static uint[]? ReadDwords(IntPtr himc, uint index)
    {
        var bytes = ReadBytes(himc, index);
        if (bytes is null || bytes.Length < 4)
        {
            return null;
        }

        var count = bytes.Length / 4;
        var result = new uint[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = BitConverter.ToUInt32(bytes, i * 4);
        }

        return result;
    }

    private static string VirtualKeyName(int vk)
    {
        if (vk >= 0x30 && vk <= 0x39)
        {
            return ((char)vk).ToString();
        }

        if (vk >= 0x41 && vk <= 0x5A)
        {
            return ((char)vk).ToString();
        }

        return vk switch
        {
            0x08 => "VK_BACK",
            0x09 => "VK_TAB",
            0x0D => "VK_RETURN",
            0x10 => "VK_SHIFT",
            0x11 => "VK_CONTROL",
            0x12 => "VK_MENU",
            0x13 => "VK_PAUSE",
            0x14 => "VK_CAPITAL",
            0x1B => "VK_ESCAPE",
            0x20 => "VK_SPACE",
            0x21 => "VK_PRIOR",
            0x22 => "VK_NEXT",
            0x23 => "VK_END",
            0x24 => "VK_HOME",
            0x25 => "VK_LEFT",
            0x26 => "VK_UP",
            0x27 => "VK_RIGHT",
            0x28 => "VK_DOWN",
            0x2E => "VK_DELETE",
            0xE5 => "VK_PROCESSKEY",
            _ => $"VK_0x{vk:X2}",
        };
    }
}
