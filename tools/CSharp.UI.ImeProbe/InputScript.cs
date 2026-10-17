using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace CSharp.UI.ImeProbe;

/// <summary>
/// 用 <c>SendInput</c> 向探针自己的窗口发送真实键盘事件（驱动真实输入法会话）。
/// </summary>
/// <remarks>
/// <para>
/// 安全边界：每次发送前都确认 <c>GetForegroundWindow()</c> 就是本探针窗口，且其进程就是本进程；
/// 一旦前台窗口不是自己的窗口就立即中止脚本，绝不把按键发给其他窗口（不操控 DSH 桌面 UI）。
/// </para>
/// <para>
/// 这里注入的是真实键盘事件；<c>WM_IME_*</c> 消息由本机真实输入法产生，探针从不合成 IME 消息。
/// </para>
/// </remarks>
internal sealed class InputScript
{
    private const int HWND_TOPMOST = -1;
    private const int HWND_NOTOPMOST = -2;
    private const int InputSizeX64 = 40;
    private const int InputSizeX86 = 28;
    private const int KeyboardOffsetX64 = 8;
    private const int KeyboardOffsetX86 = 4;

    private readonly ProbeLog _log;
    private readonly IntPtr _hwnd;
    private readonly string _label;
    private bool _aborted;

    internal InputScript(ProbeLog log, IntPtr hwnd, string label)
    {
        _log = log;
        _hwnd = hwnd;
        _label = label;
    }

    internal bool Aborted => _aborted;

    /// <summary>
    /// 可选的等待钩子：设置后，按键之间的等待改为泵消息（而不是 <c>Thread.Sleep</c>），
    /// 用于对照 TSF 输入法在“逐键都泵消息”与“整段注入后统一泵”两种驱动方式下的键处理差异。
    /// </summary>
    internal Action<int>? PumpHook { get; set; }

    /// <summary>
    /// 为 true 时用真实扫描码（<c>KEYEVENTF_SCANCODE</c>，wScan 来自 <c>MapVirtualKey(VK_TO_VSC)</c>）
    /// 发送字母数字键，而不是 wScan=0 的虚拟键；用于对照输入法早期键处理。仍然只发送真实键盘事件。
    /// </summary>
    internal bool UseScanCodes { get; set; }

    internal int InputStructSize => IntPtr.Size == 8 ? InputSizeX64 : InputSizeX86;

    /// <summary>构造一次按下/抬起：可选真实扫描码模式。</summary>
    private byte[] BuildKeyInput(ushort virtualKey, bool keyUp)
    {
        if (UseScanCodes)
        {
            var scan = (ushort)NativeMethods.MapVirtualKey(virtualKey, NativeMethods.MAPVK_VK_TO_VSC);
            var flags = NativeMethods.KEYEVENTF_SCANCODE | (keyUp ? NativeMethods.KEYEVENTF_KEYUP : 0u);
            return BuildKeyboardInput(0, scan, flags);
        }

        return BuildKeyboardInput(virtualKey, 0, keyUp ? NativeMethods.KEYEVENTF_KEYUP : 0u);
    }

    /// <summary>等待指定毫秒；设置 <see cref="PumpHook"/> 时改为泵消息，保持消息循环活着。</summary>
    private void Pause(int milliseconds)
    {
        if (PumpHook is { } hook)
        {
            hook(milliseconds);
        }
        else
        {
            Thread.Sleep(Math.Max(1, milliseconds));
        }
    }

    /// <summary>
    /// 把探针窗口带到前台；只有确认前台窗口属于本窗口时才返回 true。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 只做<b>一轮</b>尝试，不反复争抢前台：Windows 前台锁定会让 SetForegroundWindow 反复静默失败，
    /// 循环重试既没有收益，又可能打扰用户当前正在使用的应用。失败就在这里如实记录并置 <see cref="Aborted"/>，
    /// 由调用方按“前置条件缺失”跳过，而不是继续注入按键。
    /// </para>
    /// <para>
    /// 单轮里依次尝试：topmost 提升 + SetForegroundWindow；若仍失败且当前前台是别的线程，
    /// 则短暂 AttachThreadInput 到该前台线程后再尝试（随即立刻解除附加）。
    /// 若本进程自身是被显式授权的调用方（前台切换被允许），这一轮通常就能成功。
    /// </para>
    /// </remarks>
    internal bool EnsureForeground(int timeoutMs = 4000)
    {
        var before = DescribeWindow(NativeMethods.GetForegroundWindow());
        var ourThread = NativeMethods.GetWindowThreadProcessId(_hwnd, out _);
        var attachUsed = false;
        var attempts = 1;

        var foregroundThread = NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out _);
        NativeMethods.SetWindowPos(_hwnd, new IntPtr(HWND_TOPMOST), 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_SHOWWINDOW);
        NativeMethods.SetForegroundWindow(_hwnd);
        NativeMethods.SetWindowPos(_hwnd, new IntPtr(HWND_NOTOPMOST), 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_SHOWWINDOW);

        if (NativeMethods.GetForegroundWindow() != _hwnd
            && foregroundThread != 0
            && foregroundThread != ourThread
            && NativeMethods.AttachThreadInput(ourThread, foregroundThread, true))
        {
            // Windows 前台锁定会拒绝非前台进程的 SetForegroundWindow；附加到当前前台线程的输入队列后
            // 才能把本探针窗口带到前台（只影响本探针窗口与短暂的前台状态，随后立即解除附加）。
            attachUsed = true;
            NativeMethods.BringWindowToTop(_hwnd);
            NativeMethods.SetForegroundWindow(_hwnd);
            NativeMethods.SetFocus(_hwnd);
            NativeMethods.AttachThreadInput(ourThread, foregroundThread, false);
        }

        Pause(250);

        var after = NativeMethods.GetForegroundWindow();
        var ok = after == _hwnd;
        _log.Record("script", "sendinput", "foreground-check", new Dictionary<string, object?>
        {
            ["label"] = _label,
            ["foreground_before"] = before,
            ["foreground_after"] = DescribeWindow(after),
            ["attempts"] = attempts,
            ["single_attempt"] = true,
            ["attach_thread_input_used"] = attachUsed,
            ["is_own_window"] = ok,
            ["note"] = "a single attempt only: the probe does not fight for the foreground. "
                + "If this fails, the window must be clicked/activated by the user before key injection can proceed.",
        });
        if (!ok)
        {
            _aborted = true;
        }

        return ok;
    }

    /// <summary>按下并释放一个虚拟键。</summary>
    internal bool PressVirtualKey(ushort virtualKey, string label, int holdMs = 25)
    {
        if (!SendRaw(BuildKeyInput(virtualKey, keyUp: false), $"{label}:down", virtualKey))
        {
            return false;
        }

        Pause(Math.Max(1, holdMs));
        return SendRaw(BuildKeyInput(virtualKey, keyUp: true), $"{label}:up", virtualKey);
    }

    // 说明：这里刻意不提供“组合键（如 Ctrl+Space）”注入。
    // 组合键在第二次 keydown 之后若失去前台，SendRaw 依保护规则会拒绝发送 keyup，
    // “失败也恢复键态”因此无法保证；探针不为可选预热新建可注入键盘后端。
    // --warmup ctrl-space 在 ProbeOptions/Program 中被明确拒绝。

    /// <summary>按住一个虚拟键一段时间，产生真实自动重复（repeat）路径。</summary>
    internal bool PressAndHoldVirtualKey(ushort virtualKey, string label, int holdMs)
        => SendRepeatBurst(virtualKey, label, 2, holdMs);

    /// <summary>
    /// 注入重复按键序列（连续多次 keydown，最后一次 keyup）。
    /// </summary>
    /// <remarks>
    /// 用 SendInput 注入的按键不会由系统产生硬件自动重复（重复由键盘类驱动在物理按键保持时生成），
    /// 因此这里显式注入多次 keydown：GLFW 的重复判定基于自己的按键状态表（input.c:287-296），
    /// 第二次 keydown 会被上报为 GLFW_REPEAT。这是真实键盘事件序列，不是合成的 WM_IME 消息。
    /// </remarks>
    internal bool SendRepeatBurst(ushort virtualKey, string label, int repeatCount, int holdMs)
    {
        for (var i = 0; i < Math.Max(1, repeatCount); i++)
        {
            if (!SendRaw(BuildKeyboardInput(virtualKey, 0, 0), $"{label}:down[{i}]", virtualKey))
            {
                return false;
            }

            Pause(Math.Max(20, holdMs));
        }

        return SendRaw(BuildKeyboardInput(virtualKey, 0, NativeMethods.KEYEVENTF_KEYUP), $"{label}:up", virtualKey);
    }

    /// <summary>用 KEYEVENTF_UNICODE 发送一个 UTF-16 码元（合成边界测试，不经输入法）。</summary>
    internal bool SendUnicodeCodeUnit(ushort codeUnit, string label)
    {
        if (!SendRaw(BuildKeyboardInput(0, codeUnit, NativeMethods.KEYEVENTF_UNICODE), $"{label}:down", null))
        {
            return false;
        }

        Pause(20);
        return SendRaw(BuildKeyboardInput(0, codeUnit, NativeMethods.KEYEVENTF_UNICODE | NativeMethods.KEYEVENTF_KEYUP), $"{label}:up", null);
    }

    /// <summary>按 a-z / 数字的虚拟键码逐字发送（拼音串等 ASCII）。</summary>
    internal bool SendAsciiText(string text, string label, int interKeyDelayMs = 90)
    {
        foreach (var ch in text)
        {
            var vk = (ushort)char.ToUpperInvariant(ch);
            if (!PressVirtualKey(vk, $"{label}:{ch}"))
            {
                return false;
            }

            Pause(Math.Max(1, interKeyDelayMs));
        }

        return true;
    }

    internal static bool IsSupportedHost => OperatingSystem.IsWindows();

    /// <summary>构造 Win32 INPUT 结构（原始字节，避免结构联合体布局差异）。</summary>
    /// <remarks>
    /// INPUT = { DWORD type; union { MOUSEINPUT; KEYBDINPUT; HARDWAREINPUT } }。
    /// x64: MOUSEINPUT 32 字节 → INPUT 40 字节，KEYBDINPUT 从偏移 8 开始；
    /// x86: MOUSEINPUT 24 字节 → INPUT 28 字节，KEYBDINPUT 从偏移 4 开始。
    /// KEYBDINPUT = { WORD wVk; WORD wScan; DWORD dwFlags; DWORD time; ULONG_PTR dwExtraInfo }。
    /// </remarks>
    internal static byte[] BuildKeyboardInput(ushort virtualKey, ushort scanCode, uint flags, int? pointerSize = null)
    {
        var is64 = (pointerSize ?? IntPtr.Size) == 8;
        var buffer = new byte[is64 ? InputSizeX64 : InputSizeX86];
        var keyboardOffset = is64 ? KeyboardOffsetX64 : KeyboardOffsetX86;

        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), NativeMethods.INPUT_KEYBOARD);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(keyboardOffset, 2), virtualKey);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(keyboardOffset + 2, 2), scanCode);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(keyboardOffset + 4, 4), flags);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(keyboardOffset + 8, 4), 0u);
        // dwExtraInfo stays zero (x64 offset keyboardOffset+16, x86 keyboardOffset+12).
        return buffer;
    }

    /// <summary>
    /// 合成边界测试：在真实 keydown 之后注入一条带“前一状态按下”位的 WM_KEYDOWN（PostMessage），
    /// 用于验证 GLFW/OpenTK 的重复键上报路径。明确标注为合成输入：这不是真实自动重复。
    /// </summary>
    /// <remarks>
    /// 真实自动重复由系统键盘输入路径生成，SendInput 注入的按键不会触发它（三次注入 keydown 只到达一条
    /// WM_KEYDOWN，见 probe 日志）。GLFW 的重复判定基于自己的按键状态表（input.c:287-296），因此需要一条
    /// “第二次按下”的通知才能进入 GLFW_REPEAT 分支。post 的消息仍会经过 GLFW 事件泵里的 TranslateMessage，
    /// 所以同样会产生 WM_CHAR；真实自动重复下输入法对重复键的消费（VK_PROCESSKEY）只能由真实按键验证。
    /// </remarks>
    internal bool PostSyntheticRepeatKeyDown(ushort virtualKey, string label)
    {
        // lParam: repeat count 1, scan code 0 (GLFW 会用 MapVirtualKeyW 反查), previous key state = down (bit 30).
        const uint scanCode = 0;
        var lParam = (IntPtr)(1 | (scanCode << 16) | (1 << 30));
        var ok = NativeMethods.PostMessage(_hwnd, NativeMethods.WM_KEYDOWN, new IntPtr(virtualKey), lParam);
        _log.Record("script", "synthetic", "post-synthetic-wm-keydown", new Dictionary<string, object?>
        {
            ["label"] = _label,
            ["step"] = label,
            ["virtual_key"] = $"0x{virtualKey:X2}",
            ["lparam"] = $"0x{lParam.ToInt64():X}",
            ["synthetic"] = true,
            ["note"] = "boundary test only: posted WM_KEYDOWN with bit30 set; not a real auto-repeat",
            ["posted"] = ok,
        });
        return ok;
    }

    private bool SendRaw(byte[] input, string label, ushort? virtualKey)
    {
        if (_aborted)
        {
            return false;
        }

        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground != _hwnd)
        {
            _aborted = true;
            _log.Record("script", "sendinput", "aborted-foreground-lost", new Dictionary<string, object?>
            {
                ["label"] = _label,
                ["step"] = label,
                ["foreground"] = DescribeWindow(foreground),
                ["note"] = "no key event was sent to any other window",
            });
            return false;
        }

        var sent = NativeMethods.SendInput(1, input, input.Length);
        var error = sent == 1 ? 0 : Marshal.GetLastWin32Error();
        _log.Record("script", "sendinput", "key-event", new Dictionary<string, object?>
        {
            ["label"] = _label,
            ["step"] = label,
            ["virtual_key"] = virtualKey is { } vk ? $"0x{vk:X2}" : "(unicode)",
            ["input_size_bytes"] = input.Length,
            ["sent"] = sent,
            ["win32_error"] = error,
            ["unit"] = "INPUT struct bytes",
        });

        if (sent != 1)
        {
            _aborted = true;
            return false;
        }

        return true;
    }

    private string DescribeWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return "(null)";
        }

        var threadId = NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        var className = NativeMethods.GetWindowClassName(hwnd);
        var isOwn = hwnd == _hwnd;
        return $"0x{hwnd.ToInt64():X} pid={pid} tid={threadId} class=\"{className}\" own={isOwn}";
    }
}

/// <summary>
/// 可选的 TSF 进程内输入法配置切换（ITfInputProcessorProfileMgr）。
/// </summary>
/// <remarks>
/// <para>
/// 只使用 <c>TF_IPPMF_FORPROCESS</c>（0x10000000，见本机 SDK 10.0.26100.0 的 msctf.idl 第 392 行），
/// 不传 <c>TF_IPPMF_FORSESSION</c>（0x20000000），因此不修改用户全局默认输入法；进程退出即失效。
/// </para>
/// <para>
/// 接口布局：IUnknown(0-2) 之后依次是 ActivateProfile(3)/DeactivateProfile(4)/GetProfile(5)/
/// EnumProfiles(6)/ReleaseInputProcessor(7)/RegisterProfile(8)/UnregisterProfile(9)/GetActiveProfile(10)
/// （本机 msctf.idl 第 2054-2104 行）。本文件只做 <c>--profile</c> 的一次性 ActivateProfile；
/// 实际活动 profile 的只读身份查询与受控临时启用/恢复见 <c>TsfProfileManagement.cs</c>。
/// </para>
/// </remarks>
internal static partial class TsfProfile
{
    internal static readonly Guid ClsidInputProcessorProfiles = new("33C53A50-F456-4884-B049-85FD643ECFED");
    internal static readonly Guid IidInputProcessorProfileMgr = new("71C6E74C-0F28-11D8-A82A-00065B84435C");
    internal static readonly Guid GuidTfCatTipKeyboard = new("34745C63-B2F0-4784-8B67-5E12C8701A31");
    internal static readonly Guid MicrosoftPinyinClsid = new("81D4E9C9-1D3B-41BC-9E6C-4B40BF79E35E");
    internal static readonly Guid MicrosoftPinyinProfile = new("FA550B04-5AD7-411F-A5AC-CA038EC515D7");
    internal static readonly Guid WeTypeClsid = new("86598FB9-66A2-463E-B9C2-AEB906D477AD");
    internal static readonly Guid WeTypeProfile = new("607FDF85-FCC8-4DBD-A365-41296F980C9C");

    internal const uint TfProfileTypeInputProcessor = 0x0001;
    internal const uint TfIppmForProcess = 0x10000000;
    internal const ushort LangIdChineseSimplified = 0x0804;

    private const uint ClsctxInprocServer = 0x0001;
    private const int SFalse = 1;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ActivateProfileDelegate(
        IntPtr self, uint profileType, ushort langid, ref Guid clsid, ref Guid profile, IntPtr hkl, uint flags);

    [DllImport("ole32.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int CoCreateInstance(
        ref Guid clsid, IntPtr outer, uint context, ref Guid iid, out IntPtr instance);

    [DllImport("ole32.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();

    internal static int InitializeCom() => CoInitializeEx(IntPtr.Zero, 0x2); // COINIT_APARTMENTTHREADED

    internal static void UninitializeCom() => CoUninitialize();

    /// <summary>
    /// 为目标输入法配置调用 ActivateProfile(FORPROCESS)。
    /// </summary>
    /// <remarks>
    /// 成功判据参考微软文档：只有 <c>S_OK</c>（0）且 <c>GetActiveProfile</c> 返回的实际活动身份与请求一致
    /// 才算成功；<c>S_FALSE</c>（1）表示 “the language profile is not enabled”，不是激活成功；
    /// 异常按实际失败返回 <c>E_FAIL</c>，绝不把异常伪装成 <c>S_FALSE</c>。该规则与 <c>CoInitializeEx</c> 的
    /// <c>S_FALSE</c>（已初始化）含义不同，不能套用同一判断。
    /// </remarks>
    internal static (int HResult, string Detail) ActivateForProcess(ProbeLog log, string label, Guid clsid, Guid profile, ushort langid)
    {
        var clsidLocal = ClsidInputProcessorProfiles;
        var iidLocal = IidInputProcessorProfileMgr;
        var hresCreate = CoCreateInstance(
            ref clsidLocal, IntPtr.Zero, ClsctxInprocServer, ref iidLocal, out var manager);
        if (hresCreate < 0 || manager == IntPtr.Zero)
        {
            var detail = $"CoCreateInstance(TF_InputProcessorProfiles) hr=0x{hresCreate:X8}";
            log.Record("tsf", "msctf-api", "activate-profile-failed", new Dictionary<string, object?>
            {
                ["label"] = label,
                ["stage"] = "cocreate",
                ["hresult"] = $"0x{hresCreate:X8}",
                ["clsid"] = ClsidInputProcessorProfiles.ToString(),
                ["iid"] = IidInputProcessorProfileMgr.ToString(),
                ["flags"] = $"0x{TfIppmForProcess:X8}",
                ["detail"] = detail,
            });
            return (hresCreate, detail);
        }

        try
        {
            var hres = Method<ActivateProfileDelegate>(manager, 3)(
                manager, TfProfileTypeInputProcessor, langid, ref clsid, ref profile, IntPtr.Zero, TfIppmForProcess);
            log.Record("tsf", "msctf-api", "activate-profile", new Dictionary<string, object?>
            {
                ["label"] = label,
                ["stage"] = "ActivateProfile",
                ["hresult"] = $"0x{hres:X8}",
                ["is_s_ok"] = hres == 0,
                ["is_s_false"] = hres == SFalse,
                ["profile_type"] = $"0x{TfProfileTypeInputProcessor:X4} (TF_PROFILETYPE_INPUTPROCESSOR)",
                ["langid"] = $"0x{langid:X4}",
                ["clsid"] = clsid.ToString(),
                ["profile_guid"] = profile.ToString(),
                ["flags"] = $"0x{TfIppmForProcess:X8} (TF_IPPMF_FORPROCESS only; not FORSESSION)",
                ["scope"] = "process-local; session default input method is not modified",
            });

            if (hres == SFalse)
            {
                return (SFalse, "ActivateProfile returned S_FALSE: the language profile is not enabled (not a success)");
            }

            if (hres != 0)
            {
                return (hres, $"ActivateProfile failed hr=0x{hres:X8}");
            }

            // S_OK 还不足以判定成功：必须核对系统报告的实际活动身份与请求一致。
            var category = GuidTfCatTipKeyboard;
            var hresActive = Method<GetActiveProfileDelegate>(manager, 10)(manager, ref category, out var active);
            var identityMatches = hresActive >= 0 && active.Clsid == clsid && active.ProfileGuid == profile;
            log.Record("tsf", "msctf-api", "activate-profile-verify", new Dictionary<string, object?>
            {
                ["label"] = label,
                ["stage"] = "GetActiveProfile",
                ["hresult"] = $"0x{hresActive:X8}",
                ["verified_actual_active"] = identityMatches,
                ["actual_clsid"] = active.Clsid.ToString(),
                ["actual_profile_guid"] = active.ProfileGuid.ToString(),
                ["actual_flags"] = $"0x{active.Flags:X}",
                ["requested_clsid"] = clsid.ToString(),
                ["requested_profile_guid"] = profile.ToString(),
                ["note"] = "success requires S_OK plus an actual-active identity match; the --profile argument is not identity",
            });

            return identityMatches
                ? (0, "ActivateProfile S_OK and GetActiveProfile confirmed the requested profile is actually active")
                : (hresActive < 0 ? hresActive : EFail,
                    $"ActivateProfile S_OK but actual active identity does not match (GetActiveProfile hr=0x{hresActive:X8}, "
                    + $"actual {active.Clsid}/{active.ProfileGuid})");
        }
        catch (Exception ex)
        {
            var detail = $"{ex.GetType().Name}: {ex.Message}";
            log.Record("tsf", "msctf-api", "activate-profile-failed", new Dictionary<string, object?>
            {
                ["label"] = label,
                ["stage"] = "invoke",
                ["hresult"] = $"0x{EFail:X8}",
                ["detail"] = detail,
                ["note"] = "exception is reported as E_FAIL, not S_FALSE",
            });
            return (EFail, detail);
        }
        finally
        {
            Marshal.Release(manager);
        }
    }
}
