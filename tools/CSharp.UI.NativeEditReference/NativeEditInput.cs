using System.Runtime.InteropServices;
using CSharp.UI.ImeProbe;

namespace CSharp.UI.NativeEditReference;

/// <summary>
/// 自有输入后端：**不使用** <c>InputScript.EnsureForeground</c>（它会 TOPMOST + AttachThreadInput），
/// 也不修改共享 ImeProbe 的安全策略。
/// <para>
/// 每一次 keydown 与 keyup 之前都重新核对：自有父窗口前台、EDIT 持有焦点、PID/线程均属本进程。
/// 任一项不满足立即停止，绝不把按键发给其他窗口，也绝不强抢焦点。
/// </para>
/// <para>
/// 按键之间与等待期间持续 pump 消息，使 UI 线程不阻塞、用户点击可正常派发。
/// </para>
/// </summary>
internal sealed class NativeEditInput
{
    private const uint INPUT_KEYBOARD = 1;

    private readonly ProbeLog _log;
    private readonly NativeEditHost _host;
    private readonly string _label;
    private readonly RunState _state;

    internal NativeEditInput(ProbeLog log, NativeEditHost host, string label, RunState state)
    {
        _log = log;
        _host = host;
        _label = label;
        _state = state;
    }

    internal bool Aborted => _state.Aborted;

    /// <summary>每次 down/up 前复核安全门与运行状态；失败即中止并记录，不发送该事件。</summary>
    private bool Gate(string step)
    {
        if (_state.ShouldStop)
        {
            return false;
        }

        if (!InputGate.Check(_host.Parent, _host.Edit, out var detail))
        {
            _state.MarkAborted();
            _log.Record("script", "native-edit", "aborted-gate", new Dictionary<string, object?>
            {
                ["label"] = _label,
                ["step"] = step,
                ["detail"] = detail,
                ["note"] = "no key event was sent to any other window",
            });
            return false;
        }

        return true;
    }

    /// <summary>发送一个真实扫描码按键：down → pump → up，每步都过安全门。</summary>
    internal bool PressScanCode(ushort virtualKey, string step)
    {
        var scanCode = (ushort)NativeMethods.MapVirtualKey(virtualKey, 0);
        if (scanCode == 0)
        {
            _log.Record("script", "native-edit", "scan-code-missing",
                new Dictionary<string, object?> { ["virtual_key"] = $"0x{virtualKey:X2}" });
            _state.MarkAborted();
            return false;
        }

        if (!SendKey(virtualKey, scanCode, keyUp: false, step + ":down"))
        {
            return false;
        }

        // 按住期间继续派发消息，让 IME 正常处理 keydown。
        if (!_host.Pump(25, _state))
        {
            return false;
        }

        return SendKey(virtualKey, scanCode, keyUp: true, step + ":up");
    }

    private bool SendKey(ushort virtualKey, ushort scanCode, bool keyUp, string step)
    {
        if (!Gate(step))
        {
            return false;
        }

        var flags = NativeMethods.KEYEVENTF_SCANCODE;
        if (keyUp)
        {
            flags |= NativeMethods.KEYEVENTF_KEYUP;
        }

        var bytes = BuildKeyboardInput(virtualKey, scanCode, flags);        var sent = NativeMethods.SendInput(1, bytes, bytes.Length);
        var error = sent == 1 ? 0 : Marshal.GetLastWin32Error();

        _log.Record("script", "native-edit", "key-event", new Dictionary<string, object?>
        {
            ["label"] = _label,
            ["step"] = step,
            ["virtual_key"] = $"0x{virtualKey:X2}",
            ["scan_code"] = $"0x{scanCode:X2}",
            ["key_up"] = keyUp,
            ["sent"] = sent,
            ["win32_error"] = error,
        });

        if (sent != 1)
        {
            _state.MarkAborted();
            return false;
        }

        return true;
    }

    /// <summary>逐字符输入 ASCII（每个字符独立 down/up + 安全门 + pump）。</summary>
    internal bool SendAsciiText(string text, string step)
    {
        foreach (var ch in text)
        {
            if (Aborted)
            {
                return false;
            }

            var vk = (ushort)char.ToUpperInvariant(ch);
            if (!PressScanCode(vk, $"{step}:{ch}"))
            {
                return false;
            }

            if (!_host.Pump(90, _state))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 复用 ImeProbe 已审核的 INPUT/KEYBDINPUT 字节布局（wVk 在前、wScan 在后、含 KEYEVENTF_SCANCODE），
    /// 避免自写一份易错的 ABI。
    /// </summary>
    internal static byte[] BuildKeyboardInput(ushort virtualKey, ushort scanCode, uint flags)
        => InputScript.BuildKeyboardInput(virtualKey, scanCode, flags);
}
