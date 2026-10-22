using System.Runtime.InteropServices;
using System.Text;
using CSharp.UI.ImeProbe;

namespace CSharp.UI.NativeEditReference;

/// <summary>一次消息观测：在子类过程里记录，绝不吞掉原生默认处理。</summary>
internal sealed class NativeEditObservationLog
{
    private readonly ProbeLog _log;

    internal NativeEditObservationLog(ProbeLog log) => _log = log;

    internal int CompositionStart { get; private set; }

    internal int CompositionUpdate { get; private set; }

    internal int CompositionEnd { get; private set; }

    /// <summary>只统计**非空**的 GCS_RESULTSTR 通知；查询到的旧值不计入。</summary>
    internal int ResultStringCount { get; private set; }

    internal int CompositionStringCount { get; private set; }

    internal int CharCount { get; private set; }

    internal int EnChangeCount { get; private set; }

    internal int FocusChangeCount { get; private set; }

    internal string LastCompositionString { get; private set; } = string.Empty;

    internal string LastResultString { get; private set; } = string.Empty;

    /// <summary>非空未提交串的最新值（每场景基线后重算）。</summary>
    internal string LastNonEmptyComposition { get; private set; } = string.Empty;

    internal void Record(string kind, IDictionary<string, object?>? fields)
    {
        try
        {
            _log.Record("ime", "native-edit", kind, fields);
        }
        catch (Exception)
        {
            // 日志失败不得穿越原生边界。
        }
    }

    internal void OnImeMessage(uint msg, IntPtr lParam, string phase, IntPtr edit)
    {
        switch (msg)
        {
            case NativeEditNative.WM_IME_STARTCOMPOSITION:
                CompositionStart++;
                Record("wm-ime-start-composition", new Dictionary<string, object?> { ["phase"] = phase });
                CaptureStrings(edit, phase, "start", countResults: false);
                break;
            case NativeEditNative.WM_IME_ENDCOMPOSITION:
                CompositionEnd++;
                Record("wm-ime-end-composition", new Dictionary<string, object?> { ["phase"] = phase });
                break;
            case NativeEditNative.WM_IME_COMPOSITION:
                CompositionUpdate++;
                var flags = (uint)lParam.ToInt64();
                var hasResult = (flags & NativeEditNative.GCS_RESULTSTR) != 0;
                Record("wm-ime-composition", new Dictionary<string, object?>
                {
                    ["phase"] = phase,
                    ["flags"] = $"0x{flags:X}",
                    ["has_compstr"] = (flags & NativeEditNative.GCS_COMPSTR) != 0,
                    ["has_resultstr"] = hasResult,
                });
                if ((flags & NativeEditNative.GCS_COMPSTR) != 0 || hasResult)
                {
                    // 只有本次通知确实带 GCS_RESULTSTR 才可能计入提交；结果串必须非空才算。
                    CaptureStrings(edit, phase, "composition", countResults: hasResult);
                }

                break;
        }
    }

    internal void OnChar(string phase)
    {
        CharCount++;
        Record("wm-char", new Dictionary<string, object?> { ["phase"] = phase });
    }

    internal void OnEnChange(string phase, bool fromOwnEdit)
    {
        EnChangeCount++;
        Record("wm-command-en-change", new Dictionary<string, object?>
        {
            ["phase"] = phase,
            ["from_own_edit"] = fromOwnEdit,
            ["note"] = "preedit EN_CHANGE is NOT a Binding commit",
        });
    }

    internal void OnFocusChange(int notify, string phase)
    {
        FocusChangeCount++;
        Record(notify == NativeEditNative.EN_SETFOCUS
                ? "en-setfocus"
                : "en-killfocus",
            new Dictionary<string, object?> { ["phase"] = phase });
    }

    /// <summary>开始新场景：所有计数与字符串事实归零，避免跨场景累计。</summary>
    internal void ResetForScenario()
    {
        CompositionStart = 0;
        CompositionUpdate = 0;
        CompositionEnd = 0;
        ResultStringCount = 0;
        CompositionStringCount = 0;
        CharCount = 0;
        EnChangeCount = 0;
        FocusChangeCount = 0;
        LastCompositionString = string.Empty;
        LastResultString = string.Empty;
        LastNonEmptyComposition = string.Empty;
    }

    /// <summary>一次**实时**组合查询结果：成功才可用，空串/缺少上下文/查询失败一律不可用。</summary>
    internal readonly record struct CompositionQuery(bool Ok, string Text, string? Failure)
    {
        /// <summary>可用 = 查询成功且拿到非空未提交串。</summary>
        internal bool Usable => Ok && Text.Length > 0;

        internal string Describe() => Ok
            ? $"ok len={Text.Length} text='{ImeFormat.EscapeUtf16(Text)}'"
            : $"unavailable({Failure})";
    }

    /// <summary>
    /// 实时读取当前未提交组合串。**不复用任何历史缓存**：每次调用都重新取 IMM 上下文，
    /// 空串、上下文缺失或查询失败都返回不可用。
    /// </summary>
    internal CompositionQuery QueryComposition(IntPtr editHwnd, string phase)
    {
        var hIMC = NativeEditNative.ImmGetContext(editHwnd);
        if (hIMC == IntPtr.Zero)
        {
            Record("imm-context-missing", new Dictionary<string, object?>
            {
                ["phase"] = phase,
                ["reason"] = "query-composition",
            });
            return new CompositionQuery(false, string.Empty, "imm-context-missing");
        }

        try
        {
            var composition = ReadString(hIMC, NativeEditNative.GCS_COMPSTR);
            Record("imm-composition-query", new Dictionary<string, object?>
            {
                ["phase"] = phase,
                ["composition"] = ImeFormat.EscapeUtf16(composition ?? "(none)"),
                ["composition_cch"] = composition?.Length,
                ["usable"] = composition is { Length: > 0 },
            });
            return composition is { Length: > 0 }
                ? new CompositionQuery(true, composition, null)
                : new CompositionQuery(false, composition ?? string.Empty, "empty-or-missing-compstr");
        }
        finally
        {
            if (!NativeEditNative.ImmReleaseContext(editHwnd, hIMC))
            {
                Record("imm-release-failed", new Dictionary<string, object?> { ["phase"] = phase });
            }
        }
    }

    /// <summary>在 EDIT 上下文内读取未提交串与结果串（IMM 上下文成对释放）。</summary>
    internal void CaptureStrings(IntPtr editHwnd, string phase, string reason, bool countResults = false)
    {
        var hIMC = NativeEditNative.ImmGetContext(editHwnd);
        if (hIMC == IntPtr.Zero)
        {
            Record("imm-context-missing", new Dictionary<string, object?>
            {
                ["phase"] = phase,
                ["reason"] = reason,
            });
            return;
        }

        try
        {
            var composition = ReadString(hIMC, NativeEditNative.GCS_COMPSTR);
            var result = ReadString(hIMC, NativeEditNative.GCS_RESULTSTR);
            if (composition is not null)
            {
                CompositionStringCount++;
                LastCompositionString = composition;
                if (composition.Length > 0)
                {
                    LastNonEmptyComposition = composition;
                }
            }

            // 只有“本次确实是 GCS_RESULTSTR 通知且结果串非空”才计为一次提交结果。
            if (countResults && !string.IsNullOrEmpty(result))
            {
                ResultStringCount++;
                LastResultString = result;
            }

            Record("imm-strings", new Dictionary<string, object?>
            {
                ["phase"] = phase,
                ["reason"] = reason,
                ["counts_as_result"] = countResults && !string.IsNullOrEmpty(result),
                ["composition"] = ImeFormat.EscapeUtf16(composition ?? "(none)"),
                ["composition_cch"] = composition?.Length,
                ["result"] = ImeFormat.EscapeUtf16(result ?? "(none)"),
                ["result_cch"] = result?.Length,
            });
        }
        finally
        {
            // 成对释放：失败必须可见，不能静默。
            if (!NativeEditNative.ImmReleaseContext(editHwnd, hIMC))
            {
                Record("imm-release-failed", new Dictionary<string, object?> { ["phase"] = phase });
            }
        }
    }

    private string? ReadString(IntPtr hIMC, uint index)
    {
        var length = NativeEditNative.ImmGetCompositionStringW(hIMC, index, IntPtr.Zero, 0);
        if (length <= 0)
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            var copied = NativeEditNative.ImmGetCompositionStringW(hIMC, index, buffer, length);
            return copied <= 0 ? null : Marshal.PtrToStringUni(buffer, copied / 2);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}

/// <summary>
/// 真实多行 EDIT 宿主：父窗口 + 固定 viewport（WS_CLIPCHILDREN）+ 其内 EDIT 子窗口。
/// 输入走 EDIT 原生默认处理；我们只用 SetWindowSubclass 观测，观测后一律调用
/// <c>DefSubclassProc</c>，绝不改变默认行为。
/// </summary>
internal sealed class NativeEditHost : IDisposable
{
    private const string ParentClass = "NativeEditRefParent";
    private const string EditClass = "EDIT";

    private const int ViewportX = 16;
    private const int ViewportY = 96;
    private const int ViewportWidth = 560;
    private const int ViewportHeight = 180;
    private const int EditHeight = 420;

    private readonly ProbeLog _log;
    private readonly NativeEditObservationLog _observations;
    private readonly NativeEditNative.WndProcDelegate _parentProc;
    private readonly NativeEditNative.SubclassProcDelegate _editSubclassProc;
    private readonly NativeEditNative.SubclassProcDelegate _viewportSubclassProc;
    private IntPtr _parent;
    private IntPtr _viewport;
    private IntPtr _edit;
    private IntPtr _classAtom;
    private bool _classRegistered;
    private bool _subclassed;
    private bool _viewportSubclassed;
    private bool _disposed;

    internal NativeEditHost(ProbeLog log, NativeEditObservationLog observations)
    {
        _log = log;
        _observations = observations;
        _parentProc = ParentWndProc;
        _editSubclassProc = EditSubclassProc;
        _viewportSubclassProc = ViewportSubclassProc;
    }

    internal IntPtr Parent => _parent;

    internal IntPtr Viewport => _viewport;

    internal IntPtr Edit => _edit;

    internal bool IsCreated { get; private set; }

    internal bool EditSubclassed => _subclassed;

    internal bool ViewportSubclassed => _viewportSubclassed;

    /// <summary>是否调用过 ShowWindow（隐藏 smoke 必须为 false）。</summary>
    internal bool WasShown { get; private set; }

    /// <summary>当前消息相位（baseline/before/after/restore），用于把观测归段。</summary>
    internal string Phase { get; set; } = "init";

    internal bool Create(int width, int height, bool visible)
    {
        var instance = NativeEditNative.GetModuleHandleW(null);
        var className = Marshal.StringToHGlobalUni(ParentClass);
        try
        {
            var wc = new NativeEditNative.WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<NativeEditNative.WNDCLASSEXW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_parentProc),
                hInstance = instance,
                lpszClassName = className,
            };
            var atom = NativeEditNative.RegisterClassExW(ref wc);
            if (atom == 0)
            {
                _log.Record("host", "native-edit", "register-class-failed",
                    new Dictionary<string, object?> { ["win32_error"] = Marshal.GetLastWin32Error() });
                return false;
            }

            _classAtom = new IntPtr(atom);
            _classRegistered = true;

            var style = visible
                ? NativeEditNative.WS_OVERLAPPEDWINDOW | NativeEditNative.WS_CLIPCHILDREN
                : NativeEditNative.WS_POPUP | NativeEditNative.WS_CLIPCHILDREN;

            _parent = NativeEditNative.CreateWindowExW(
                0, _classAtom, "CSharp.UI native EDIT reference — click this window",
                style, 80, 80, width, height, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            if (_parent == IntPtr.Zero)
            {
                _log.Record("host", "native-edit", "create-parent-failed",
                    new Dictionary<string, object?> { ["win32_error"] = Marshal.GetLastWin32Error() });
                return false;
            }

            _viewport = NativeEditNative.CreateWindowExW(
                0, "STATIC", string.Empty,
                NativeEditNative.WS_CHILD | NativeEditNative.WS_VISIBLE | NativeEditNative.WS_CLIPCHILDREN,
                ViewportX, ViewportY, ViewportWidth, ViewportHeight,
                _parent, IntPtr.Zero, instance, IntPtr.Zero);
            if (_viewport == IntPtr.Zero)
            {
                _log.Record("host", "native-edit", "create-viewport-failed",
                    new Dictionary<string, object?> { ["win32_error"] = Marshal.GetLastWin32Error() });
                return false;
            }

            _edit = NativeEditNative.CreateWindowExW(
                0, EditClass, string.Empty,
                NativeEditNative.WS_CHILD | NativeEditNative.WS_VISIBLE | NativeEditNative.WS_VSCROLL
                | NativeEditNative.ES_MULTILINE | NativeEditNative.ES_AUTOVSCROLL
                | NativeEditNative.ES_NOHIDESEL,
                0, 0, ViewportWidth, EditHeight,
                _viewport, IntPtr.Zero, instance, IntPtr.Zero);
            if (_edit == IntPtr.Zero)
            {
                _log.Record("host", "native-edit", "create-edit-failed",
                    new Dictionary<string, object?> { ["win32_error"] = Marshal.GetLastWin32Error() });
                return false;
            }

            // 观测子类化：SetWindowSubclass 不改变窗口过程链的所有权；回调末尾调用 DefSubclassProc。
            _subclassed = NativeEditNative.SetWindowSubclass(_edit,
                Marshal.GetFunctionPointerForDelegate(_editSubclassProc), EditSubclassId, IntPtr.Zero);
            if (!_subclassed)
            {
                _log.Record("host", "native-edit", "subclass-failed",
                    new Dictionary<string, object?> { ["win32_error"] = Marshal.GetLastWin32Error() });
                return false;
            }

            // EDIT 的 EN_CHANGE/焦点通知以 WM_COMMAND 发给直接父窗口（viewport），必须在此观测。
            _viewportSubclassed = NativeEditNative.SetWindowSubclass(_viewport,
                Marshal.GetFunctionPointerForDelegate(_viewportSubclassProc), ViewportSubclassId,
                IntPtr.Zero);
            if (!_viewportSubclassed)
            {
                _log.Record("host", "native-edit", "viewport-subclass-failed",
                    new Dictionary<string, object?> { ["win32_error"] = Marshal.GetLastWin32Error() });
                return false;
            }

            IsCreated = true;
            _log.Record("host", "native-edit", "created", new Dictionary<string, object?>
            {
                ["parent"] = $"0x{_parent.ToInt64():X}",
                ["viewport"] = $"0x{_viewport.ToInt64():X}",
                ["edit"] = $"0x{_edit.ToInt64():X}",
                ["visible"] = visible,
                ["edit_subclassed"] = _subclassed,
                ["viewport_subclassed"] = _viewportSubclassed,
                ["dpi"] = NativeMethods.GetDpiForWindow(_parent),
                ["viewport_client_screen"] = ClientScreen(_viewport).ToString(),
                ["parent_window_rect"] = WindowRect(_parent).ToString(),
                ["edit_window_rect"] = WindowRect(_edit).ToString(),
            });
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(className);
        }
    }

    private static readonly IntPtr EditSubclassId = new(0x4E45_4449);
    private static readonly IntPtr ViewportSubclassId = new(0x4E45_5650);

    internal void Show()
    {
        WasShown = true;
        NativeMethods.ShowWindow(_parent, (int)NativeEditNative.SW_SHOW);
    }

    /// <summary>
    /// 普通一次前台请求。初始子控件焦点是独立的一步（<see cref="TryInitialChildFocusOnce"/>），
    /// 不 TOPMOST、不 AttachThreadInput、不对任何外部窗口 SetFocus。
    /// </summary>
    internal void RequestForeground()
    {
        if (!IsCreated)
        {
            return;
        }

        var ok = NativeMethods.SetForegroundWindow(_parent);
        _log.Record("host", "native-edit", "foreground-request", new Dictionary<string, object?>
        {
            ["set_foreground_returned"] = ok,
            ["foreground"] = $"0x{NativeMethods.GetForegroundWindow().ToInt64():X}",
            ["focus"] = $"0x{NativeMethods.GetFocus().ToInt64():X}",
            ["initial_focus_attempted"] = false,
            ["note"] = "ordinary foreground request only; initial child focus is a separate step",
        });
    }

    /// <summary>
    /// 初始化阶段的一次性自有子控件聚焦。
    /// <para>
    /// 前置条件全部满足才执行：当前前台**确为自有 Parent**、Parent/EDIT 均存在、
    /// 二者同属本进程且同属本 UI 线程。任一不满足即**不**调用 SetFocus、也不重夺前台，
    /// 由调用方继续等待用户主动点击。
    /// </para>
    /// <para>
    /// 这是普通应用把焦点放进自己子控件的初始化行为，不是强抢外部前台，也不是场景运行期改变焦点。
    /// 每个宿主实例最多执行一次（<paramref name="alreadyAttempted"/> 由调用方持有），绝不循环强设。
    /// </para>
    /// </summary>
    internal InitialFocusOutcome TryInitialChildFocusOnce(ref bool alreadyAttempted)
    {
        if (!IsCreated)
        {
            return new InitialFocusOutcome(false, "host-not-created", false);
        }

        if (alreadyAttempted)
        {
            return new InitialFocusOutcome(false, "already-attempted", false);
        }

        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground != _parent)
        {
            // Windows 拒绝前台请求或前台已不是自有窗口：绝不 SetFocus、绝不重夺。
            return new InitialFocusOutcome(false,
                $"foreground-not-own(0x{foreground.ToInt64():X})", false);
        }

        if (_parent == IntPtr.Zero || _edit == IntPtr.Zero
            || !NativeMethods.IsWindow(_parent) || !NativeMethods.IsWindow(_edit))
        {
            return new InitialFocusOutcome(false, "window-or-edit-missing", false);
        }

        var parentThread = NativeMethods.GetWindowThreadProcessId(_parent, out var parentPid);
        var editThread = NativeMethods.GetWindowThreadProcessId(_edit, out var editPid);
        var processId = Environment.ProcessId;
        if (parentPid != processId || editPid != processId || parentThread != editThread)
        {
            return new InitialFocusOutcome(false,
                $"identity-mismatch(parent_pid={parentPid},edit_pid={editPid},own={processId},"
                + $"parent_thread={parentThread},edit_thread={editThread})", false);
        }

        // 前置条件已核验：这是唯一一次普通的自有子控件 SetFocus。
        alreadyAttempted = true;
        NativeMethods.SetFocus(_edit);
        var focus = NativeEditNative.GetFocus();
        var ok = focus == _edit;
        _log.Record("host", "native-edit", "initial-own-child-focus", new Dictionary<string, object?>
        {
            ["foreground"] = $"0x{foreground.ToInt64():X}",
            ["parent"] = $"0x{_parent.ToInt64():X}",
            ["edit"] = $"0x{_edit.ToInt64():X}",
            ["focus_after"] = $"0x{focus.ToInt64():X}",
            ["focus_is_edit"] = ok,
            ["same_process"] = true,
            ["same_ui_thread"] = parentThread == editThread,
            ["source"] = "initial-own-child-focus",
            ["not_user_click"] = true,
            ["note"] = "ordinary initial child focus inside our own window; not an external foreground grab",
        });

        return new InitialFocusOutcome(ok, ok ? "focused" : $"focus-not-edit(0x{focus.ToInt64():X})", true);
    }

    /// <summary>初始化聚焦结果：<paramref name="Attempted"/> 为 true 表示确实调用过 SetFocus。</summary>
    internal readonly record struct InitialFocusOutcome(bool Ok, string Detail, bool Attempted);

    internal NativeRect ClientScreen(IntPtr hwnd)
    {
        if (!NativeMethods.GetClientRect(hwnd, out var client))
        {
            return default;
        }

        var origin = new NativeMethods.POINT { X = 0, Y = 0 };
        if (!NativeMethods.ClientToScreen(hwnd, ref origin))
        {
            return default;
        }

        return new NativeRect(origin.X, origin.Y, origin.X + client.Width, origin.Y + client.Height);
    }

    internal NativeRect WindowRect(IntPtr hwnd)
        => NativeMethods.GetWindowRect(hwnd, out var rect) ? NativeRect.FromClient(rect) : default;

    internal bool SetEditText(string text)
    {
        NativeEditNative.SendMessageW(_edit, NativeEditNative.WM_SETTEXT, IntPtr.Zero, text);
        return string.Equals(GetEditText(), text, StringComparison.Ordinal);
    }

    internal string GetEditText()
    {
        var length = (int)NativeEditNative.SendMessageW(_edit, NativeEditNative.WM_GETTEXTLENGTH,
            IntPtr.Zero, IntPtr.Zero);
        if (length <= 0)
        {
            return string.Empty;
        }

        var raw = Marshal.AllocHGlobal((length + 1) * 2);
        try
        {
            var copied = (int)NativeEditNative.SendMessageW(_edit, NativeEditNative.WM_GETTEXT,
                new IntPtr(length + 1), raw);
            return copied <= 0 ? string.Empty : Marshal.PtrToStringUni(raw, copied) ?? string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(raw);
        }
    }

    internal int LineCount => (int)NativeEditNative.SendMessageW(_edit, NativeEditNative.EM_GETLINECOUNT,
        IntPtr.Zero, IntPtr.Zero);

    internal int FirstVisibleLine => (int)NativeEditNative.SendMessageW(_edit,
        NativeEditNative.EM_GETFIRSTVISIBLELINE, IntPtr.Zero, IntPtr.Zero);

    internal int LineIndex(int line) => (int)NativeEditNative.SendMessageW(_edit,
        NativeEditNative.EM_LINEINDEX, new IntPtr(line), IntPtr.Zero);

    /// <summary>
    /// EM_GETSEL：wParam/lParam 分别是**起止字符位置的地址**（可为 NULL 表示不需要该项）。
    /// 这里两者都传真实地址，绝不读未初始化的缓冲。
    /// 官方：https://learn.microsoft.com/en-us/windows/win32/controls/em-getsel
    /// </summary>
    internal NativeQuery<(int Start, int End)> GetSelection()
    {
        var raw = Marshal.AllocHGlobal(8);
        try
        {
            Marshal.WriteInt32(raw, 0, -1);
            Marshal.WriteInt32(raw, 4, -1);
            var startPtr = raw;
            var endPtr = IntPtr.Add(raw, 4);
            var result = NativeEditNative.SendMessageW(_edit, NativeEditNative.EM_GETSEL, startPtr, endPtr);
            _ = result;
            var start = Marshal.ReadInt32(raw, 0);
            var end = Marshal.ReadInt32(raw, 4);
            if (start < 0 || end < 0)
            {
                return new NativeQuery<(int, int)>(unchecked((int)0x80004005), default,
                    $"em-getsel-not-filled(start={start},end={end})");
            }

            return new NativeQuery<(int, int)>(0, (start, end), null);
        }
        catch (Exception ex)
        {
            return new NativeQuery<(int, int)>(unchecked((int)0x80004005), default,
                $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Marshal.FreeHGlobal(raw);
        }
    }

    internal void SetSelection(int start, int end)
        => NativeEditNative.SendMessageW(_edit, NativeEditNative.EM_SETSEL, new IntPtr(start),
            new IntPtr(end));

    /// <summary>
    /// EM_POSFROMCHAR（标准 EDIT，非 RichEdit）：wParam=字符索引，lParam=0，
    /// 返回值低 16 位为 x、高 16 位为 y（各自为**有符号** short）；失败返回 -1。
    /// 官方：https://learn.microsoft.com/en-us/windows/win32/controls/em-posfromchar
    /// </summary>
    internal NativeQuery<NativeMethods.POINT> PosFromChar(int index)
    {
        try
        {
            var result = NativeEditNative.SendMessageW(_edit, NativeEditNative.EM_POSFROMCHAR,
                new IntPtr(index), IntPtr.Zero);
            var packed = result.ToInt64();
            if (EditPosFromChar.IsFailure(packed))
            {
                return new NativeQuery<NativeMethods.POINT>(unchecked((int)0x80004005), default,
                    "em-posfromchar-failed");
            }

            // 低 16 位 x、高 16 位 y，均为有符号 16 位（保留负坐标）。
            var (x, y) = EditPosFromChar.Unpack(packed);
            return new NativeQuery<NativeMethods.POINT>(0, new NativeMethods.POINT { X = x, Y = y }, null);
        }
        catch (Exception ex)
        {
            return new NativeQuery<NativeMethods.POINT>(unchecked((int)0x80004005), default,
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>把 EDIT 客户区坐标（EM_POSFROMCHAR 结果）转成屏幕坐标。</summary>
    internal NativeQuery<NativeMethods.POINT> CharPointToScreen(int index)
    {
        var local = PosFromChar(index);
        if (!local.Ok)
        {
            return local;
        }

        var point = local.Value;
        if (!NativeMethods.ClientToScreen(_edit, ref point))
        {
            return new NativeQuery<NativeMethods.POINT>(unchecked((int)0x80004005), default,
                "client-to-screen-failed");
        }

        return new NativeQuery<NativeMethods.POINT>(0, point, null);
    }

    internal int LinesScroll(int dxLines, int dyLines)
        => (int)NativeEditNative.SendMessageW(_edit, NativeEditNative.EM_LINESCROLL, new IntPtr(dxLines),
            new IntPtr(dyLines));

    /// <summary>把首可见行设置为指定值（用于基线确定目标行落在 viewport 内）。</summary>
    internal int SetFirstVisibleLine(int line)
    {
        var current = FirstVisibleLine;
        return LinesScroll(0, line - current);
    }

    /// <summary>发送无参数的 EDIT 命令（例如 EM_SCROLLCARET）。</summary>
    internal int SendCommand(uint command)
        => (int)NativeEditNative.SendMessageW(_edit, command, IntPtr.Zero, IntPtr.Zero);

    /// <summary>
    /// 字符所在行的屏幕矩形：用该行首字符的 x/y 与下一行首字符的 y 差推导行高。
    /// 只用于判断“该行是否仍在 viewport 内”，不替代任何 TSF 几何契约。
    /// </summary>
    internal NativeQuery<NativeRect> RowRectForChar(int index)
    {
        var point = CharPointToScreen(index);
        if (!point.Ok)
        {
            return new NativeQuery<NativeRect>(unchecked((int)0x80004005), default, point.Failure);
        }

        var line = (int)NativeEditNative.SendMessageW(_edit, NativeEditNative.EM_LINEFROMCHAR,
            new IntPtr(index), IntPtr.Zero);
        var nextLineStart = LineIndex(line + 1);
        var height = 20;
        if (nextLineStart > 0)
        {
            var next = CharPointToScreen(nextLineStart);
            if (next.Ok && next.Value.Y > point.Value.Y)
            {
                height = next.Value.Y - point.Value.Y;
            }
        }

        var left = point.Value.X;
        var rect = new NativeRect(left, point.Value.Y, left + 1, point.Value.Y + height);
        return new NativeQuery<NativeRect>(0, rect, null);
    }

    /// <summary>原生 caret：GUIThreadInfo.hwndCaret 必须是自己 EDIT，否则如实报失败。</summary>
    internal NativeQuery<NativeMethods.POINT> NativeCaret()
    {
        try
        {
            var info = new NativeMethods.GUITHREADINFO
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.GUITHREADINFO>(),
            };
            var thread = NativeMethods.GetWindowThreadProcessId(_edit, out _);
            if (!NativeMethods.GetGUIThreadInfo(thread, ref info))
            {
                return new NativeQuery<NativeMethods.POINT>(unchecked((int)0x80004005), default,
                    "get-gui-thread-info-failed");
            }

            if (info.hwndCaret != _edit)
            {
                return new NativeQuery<NativeMethods.POINT>(unchecked((int)0x80004005), default,
                    $"native-caret-hwnd-mismatch(0x{info.hwndCaret.ToInt64():X})");
            }

            var rect = info.rcCaret;
            return new NativeQuery<NativeMethods.POINT>(0,
                new NativeMethods.POINT { X = rect.Left, Y = rect.Top }, null);
        }
        catch (Exception ex)
        {
            return new NativeQuery<NativeMethods.POINT>(unchecked((int)0x80004005), default,
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>把 EDIT 子窗口移动到指定的父客户区坐标（不动父/viewport）。</summary>
    internal bool MoveEditChild(int parentClientX, int parentClientY)
    {
        var ok = NativeMethods.SetWindowPos(_edit, IntPtr.Zero, parentClientX, parentClientY, 0, 0,
            NativeEditNative.SWP_NOSIZE | NativeEditNative.SWP_NOZORDER | NativeEditNative.SWP_NOACTIVATE);
        _log.Record("action", "native-edit", "move-edit-child", new Dictionary<string, object?>
        {
            ["parent_client"] = $"({parentClientX},{parentClientY})",
            ["ok"] = ok,
            ["check_action"] = "setwindowpos-child",
            ["edit_window_rect"] = WindowRect(_edit).ToString(),
            ["viewport_client_screen"] = ClientScreen(_viewport).ToString(),
        });
        return ok;
    }

    /// <summary>
    /// 按**真实父窗口**（viewport）客户坐标相对移动 EDIT。
    /// 从 EDIT 当前窗口屏幕矩形反推其父客户坐标，再按增量平移，
    /// 因此位移精确等于 (dx,dy) 物理像素，与父窗口位置/边框无关。
    /// </summary>
    internal NativeQuery<NativeMethods.POINT> MoveEditChildBy(int dx, int dy)
    {
        var current = WindowRect(_edit);
        if (current.IsEmpty)
        {
            return new NativeQuery<NativeMethods.POINT>(unchecked((int)0x80004005), default,
                "edit-window-rect-unavailable");
        }

        // 当前 EDIT 左上角在 viewport（其真实父）客户坐标中的位置。
        var origin = new NativeMethods.POINT { X = current.Left, Y = current.Top };
        if (!NativeMethods.ScreenToClient(_viewport, ref origin))
        {
            return new NativeQuery<NativeMethods.POINT>(unchecked((int)0x80004005), default,
                "screen-to-client-failed");
        }

        var targetX = origin.X + dx;
        var targetY = origin.Y + dy;
        var parentBefore = WindowRect(_parent);
        var viewportBefore = WindowRect(_viewport);
        var editBefore = WindowRect(_edit);

        var ok = NativeMethods.SetWindowPos(_edit, IntPtr.Zero, targetX, targetY, 0, 0,
            NativeEditNative.SWP_NOSIZE | NativeEditNative.SWP_NOZORDER | NativeEditNative.SWP_NOACTIVATE);
        var win32Error = ok ? 0 : Marshal.GetLastWin32Error();

        var editAfter = WindowRect(_edit);
        var actualDx = editAfter.Left - editBefore.Left;
        var actualDy = editAfter.Top - editBefore.Top;

        _log.Record("action", "native-edit", "move-edit-child", new Dictionary<string, object?>
        {
            ["check_action"] = "setwindowpos-child",
            ["requested_delta"] = $"({dx},{dy})",
            ["parent_client_target"] = $"({targetX},{targetY})",
            ["set_window_pos_ok"] = ok,
            ["win32_error"] = win32Error,
            ["edit_before"] = editBefore.ToString(),
            ["edit_after"] = editAfter.ToString(),
            ["actual_delta"] = $"({actualDx},{actualDy})",
            ["parent_unchanged"] = parentBefore == WindowRect(_parent),
            ["viewport_unchanged"] = viewportBefore == WindowRect(_viewport),
        });

        if (!ok)
        {
            return new NativeQuery<NativeMethods.POINT>(unchecked((int)0x80004005), default,
                $"set-window-pos-failed(win32_error={win32Error})");
        }

        if (actualDx != dx || actualDy != dy)
        {
            return new NativeQuery<NativeMethods.POINT>(unchecked((int)0x80004005),
                new NativeMethods.POINT { X = actualDx, Y = actualDy },
                $"unexpected-delta(expected=({dx},{dy}),actual=({actualDx},{actualDy}))");
        }

        if (WindowRect(_parent) != parentBefore || WindowRect(_viewport) != viewportBefore)
        {
            return new NativeQuery<NativeMethods.POINT>(unchecked((int)0x80004005),
                new NativeMethods.POINT { X = actualDx, Y = actualDy },
                "parent-or-viewport-geometry-changed");
        }

        return new NativeQuery<NativeMethods.POINT>(0,
            new NativeMethods.POINT { X = actualDx, Y = actualDy }, null);
    }

    /// <summary>
    /// R28：只改**自有** EDIT 子窗口的宽度/高度，原点不动。
    /// 使用 <c>SWP_NOMOVE|SWP_NOZORDER|SWP_NOACTIVATE</c>：
    /// 不隐藏/显示窗口、不改样式、不激活、不动候选 HWND。
    /// 返回值附带真实 win32 错误与动作后的窗口/客户矩形，供审计核对。
    /// </summary>
    internal NativeQuery<NativeRect> ResizeEditChild(int width, int height, string checkAction)
    {
        // 判定标准：HWND 必须仍是我们的（IsWindow + GetWindowRect 调用成功），
        // 而不是“窗口矩形非空”。collapse 有意把高度设为 0，此时 GetWindowRect 仍返回
        // 真实矩形（Top==Bottom）；把零面积当成不可用会让恢复路径永远无法执行。
        if (!NativeMethods.IsWindow(_edit))
        {
            return new NativeQuery<NativeRect>(unchecked((int)0x80004005), default,
                "edit-window-destroyed");
        }

        var parentBefore = WindowRect(_parent);
        var viewportBefore = WindowRect(_viewport);
        var editRectAvailable = NativeMethods.GetWindowRect(_edit, out var editRectBefore);
        var editBefore = editRectAvailable ? NativeRect.FromClient(editRectBefore) : default;
        if (!editRectAvailable)
        {
            return new NativeQuery<NativeRect>(unchecked((int)0x80004005), default,
                "edit-window-rect-unavailable");
        }

        var ok = NativeMethods.SetWindowPos(_edit, IntPtr.Zero, 0, 0, width, height,
            NativeEditNative.SWP_NOMOVE | NativeEditNative.SWP_NOZORDER
            | NativeEditNative.SWP_NOACTIVATE);
        var win32Error = ok ? 0 : Marshal.GetLastWin32Error();

        var editAfter = WindowRect(_edit);
        var editClientAfter = ClientScreen(_edit);
        var parentUnchanged = parentBefore == WindowRect(_parent);
        var viewportUnchanged = viewportBefore == WindowRect(_viewport);

        _log.Record("action", "native-edit", "resize-edit-child", new Dictionary<string, object?>
        {
            ["check_action"] = checkAction,
            ["requested_size"] = $"{width}x{height}",
            ["set_window_pos_ok"] = ok,
            ["win32_error"] = win32Error,
            ["flags"] = "SWP_NOMOVE|SWP_NOZORDER|SWP_NOACTIVATE",
            ["edit_before"] = editBefore.ToString(),
            ["edit_after"] = editAfter.ToString(),
            ["edit_client_after"] = editClientAfter.ToString(),
            ["parent_unchanged"] = parentUnchanged,
            ["viewport_unchanged"] = viewportUnchanged,
            ["edit_visible"] = NativeMethods.IsWindowVisible(_edit),
            ["edit_style"] = $"0x{NativeEditNative.GetWindowLongPtrW(_edit, NativeEditNative.GWL_STYLE).ToInt64():X}",
            ["focus"] = $"0x{NativeEditNative.GetFocus().ToInt64():X}",
            ["foreground"] = $"0x{NativeMethods.GetForegroundWindow().ToInt64():X}",
        });

        if (!ok)
        {
            return new NativeQuery<NativeRect>(unchecked((int)0x80004005), editAfter,
                $"set-window-pos-failed(win32_error={win32Error})");
        }

        if (!parentUnchanged || !viewportUnchanged)
        {
            return new NativeQuery<NativeRect>(unchecked((int)0x80004005), editAfter,
                "parent-or-viewport-geometry-changed");
        }

        return new NativeQuery<NativeRect>(0, editAfter, null);
    }

    /// <summary>
    /// 当前焦点/前台/可见性/样式的**如实快照**。任何一项都不是通过条件本身，
    /// 只供日志与人工核对“几何动作没有改变焦点身份”。
    /// </summary>
    internal Dictionary<string, object?> FocusWindowSnapshot()
        => new()
        {
            ["focus"] = $"0x{NativeEditNative.GetFocus().ToInt64():X}",
            ["focus_is_edit"] = NativeEditNative.GetFocus() == _edit,
            ["foreground"] = $"0x{NativeMethods.GetForegroundWindow().ToInt64():X}",
            ["foreground_is_parent"] = NativeMethods.GetForegroundWindow() == _parent,
            ["edit_visible"] = NativeMethods.IsWindowVisible(_edit),
            ["parent_visible"] = NativeMethods.IsWindowVisible(_parent),
            ["edit_style"] = $"0x{NativeEditNative.GetWindowLongPtrW(_edit, NativeEditNative.GWL_STYLE).ToInt64():X}",
        };

    /// <summary>
    /// 在限定时间内持续派发消息。返回 false 表示超时或收到 WM_QUIT（窗口已关闭）。
    /// 绝不阻塞 UI 线程而拒绝派发消息。
    /// </summary>
    /// <summary>
    /// 在限定时间内持续派发消息，并在**每个迭代**检查统一运行状态。
    /// 返回 false 表示超时、窗口已关闭或已中止。
    /// </summary>
    internal bool Pump(int durationMs, RunState state)
    {
        var deadline = Environment.TickCount64 + Math.Max(1, durationMs);
        while (Environment.TickCount64 < deadline)
        {
            if (state.ShouldStop)
            {
                return false;
            }

            if (!PumpOnce())
            {
                state.MarkWindowClosed();
                return false;
            }

            // 窗口句柄失效也必须立即停止，不能继续对无效 HWND 操作。
            if (_parent == IntPtr.Zero || !NativeMethods.IsWindow(_parent))
            {
                state.MarkWindowClosed();
                return false;
            }

            Thread.Sleep(2);
        }

        return !state.ShouldStop;
    }

    /// <summary>派发当前排队消息一次。返回 false 表示收到 WM_QUIT（窗口已关闭）。</summary>
    internal bool PumpOnce()
    {
        while (NativeEditNative.PeekMessageW(out var msg, IntPtr.Zero, 0, 0,
            NativeEditNative.PM_REMOVE))
        {
            if (msg.message == NativeEditNative.WM_QUIT)
            {
                return false;
            }

            NativeEditNative.TranslateMessage(ref msg);
            NativeEditNative.DispatchMessageW(ref msg);
        }

        return true;
    }

    /// <summary>
    /// 观测子类过程：只记录，然后调用 <c>DefSubclassProc</c> 继续原生默认处理。
    /// 绝不在这里返回值或吞掉消息。
    /// </summary>
    private IntPtr EditSubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam,
        IntPtr idSubclass, IntPtr refData)
    {
        try
        {
            if (hwnd == _edit)
            {
                switch (msg)
                {
                    case NativeEditNative.WM_IME_STARTCOMPOSITION:
                    case NativeEditNative.WM_IME_ENDCOMPOSITION:
                    case NativeEditNative.WM_IME_COMPOSITION:
                        _observations.OnImeMessage(msg, lParam, Phase, _edit);
                        break;
                    case NativeEditNative.WM_CHAR:
                        _observations.OnChar(Phase);
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            // 观测失败不得影响原生默认处理，也绝不让异常穿过原生边界。
            TryLogObservationFailure(ex);
        }

        return NativeEditNative.DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    private void TryLogObservationFailure(Exception ex)
    {
        try
        {
            _log.Record("ime", "native-edit", "observe-exception",
                new Dictionary<string, object?> { ["message"] = ex.Message });
        }
        catch (Exception)
        {
            // 日志自身失败也不能穿越原生边界。
        }
    }

    /// <summary>
    /// viewport 观测子类：EDIT 的 EN_CHANGE 作为 WM_COMMAND 交给**直接父窗口**（viewport），
    /// 因此必须在这里观测；随后调用 DefSubclassProc 走 STATIC 的默认处理。
    /// 预编辑期间的 EN_CHANGE 只记录，绝不当成 Binding 提交。
    /// </summary>
    private IntPtr ViewportSubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam,
        IntPtr idSubclass, IntPtr refData)
    {
        try
        {
            switch (msg)
            {
                case NativeEditNative.WM_COMMAND:
                    // HIWORD(wParam)=通知码，LOWORD(wParam)=控件 ID，lParam=控件 HWND。
                    var notify = (int)((wParam.ToInt64() >> 16) & 0xFFFF);
                    if (notify == NativeEditNative.EN_CHANGE)
                    {
                        _observations.OnEnChange(Phase, lParam == _edit);
                    }

                    if (notify is NativeEditNative.EN_SETFOCUS or NativeEditNative.EN_KILLFOCUS)
                    {
                        _observations.OnFocusChange(notify, Phase);
                    }

                    break;
            }
        }
        catch (Exception ex)
        {
            TryLogObservationFailure(ex);
        }

        return NativeEditNative.DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    private IntPtr ParentWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == NativeEditNative.WM_DESTROY)
        {
            NativeEditNative.PostQuitMessage(0);
        }

        return NativeEditNative.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    /// <summary>幂等释放：重复调用安全；任何一步失败都不抛出，但会记录。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            if (_edit != IntPtr.Zero && _subclassed)
            {
                NativeEditNative.RemoveWindowSubclass(_edit,
                    Marshal.GetFunctionPointerForDelegate(_editSubclassProc), EditSubclassId);
            }
        }
        catch (Exception ex)
        {
            TryLogObservationFailure(ex);
        }
        finally
        {
            _subclassed = false;
        }

        try
        {
            if (_viewport != IntPtr.Zero && _viewportSubclassed)
            {
                NativeEditNative.RemoveWindowSubclass(_viewport,
                    Marshal.GetFunctionPointerForDelegate(_viewportSubclassProc), ViewportSubclassId);
            }
        }
        catch (Exception ex)
        {
            TryLogObservationFailure(ex);
        }
        finally
        {
            _viewportSubclassed = false;
        }

        if (_parent != IntPtr.Zero)
        {
            NativeEditNative.DestroyWindow(_parent);
        }

        _edit = IntPtr.Zero;
        _viewport = IntPtr.Zero;
        _parent = IntPtr.Zero;

        if (_classRegistered && _classAtom != IntPtr.Zero)
        {
            var unregistered = NativeEditNative.UnregisterClassW(_classAtom,
                NativeEditNative.GetModuleHandleW(null));
            _classRegistered = !unregistered;
            _log.Record("host", "native-edit", "class-unregister", new Dictionary<string, object?>
            {
                ["unregistered"] = unregistered,
            });
        }

        IsCreated = false;
    }
}
