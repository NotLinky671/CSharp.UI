using System.Runtime.InteropServices;

namespace CSharp.UI.OpenTK.WindowsTsf;

/// <summary>
/// 一条被拦截消息的处理结论（TI-07 P3a）。
/// </summary>
internal enum TsfHookMessageAction
{
    /// <summary>原样交回调用方（默认路径；绝不修改消息）。</summary>
    PassThrough = 0,

    /// <summary>已被输入法消费：把消息改写成无翻译语义的形式，使 <c>TranslateMessage</c> 不产生回声。</summary>
    Consumed = 1,
}

/// <summary>
/// <c>WH_GETMESSAGE</c> 钩子的<b>纯托管</b>决策核心（TI-07 P3a）。
/// </summary>
/// <remarks>
/// <para>
/// <b>官方语义（必须遵守）：</b>钩子“可以检查或修改消息”，钩子返回后 <c>GetMessage</c>/<c>PeekMessage</c>
/// 会把消息<b>连同修改</b>返回给调用方。因此<b>返回值不负责消费</b>——
/// 仅仅 <c>return 1</c> 不会阻止后续 <c>TranslateMessage</c> 生成 <c>WM_CHAR</c>。
/// </para>
/// <para>
/// <b>正确做法：</b>在<b>本次确实被 TSF 消费</b>时，把 <c>lParam</c> 指向的 <c>MSG</c> 改写成
/// <see cref="WmNull"/>（无翻译/无派发语义），并照常链式调用下一个钩子。
/// </para>
/// <para>
/// <b>只处理 PM_REMOVE 且只处理自有 HWND：</b><c>PM_NOREMOVE</c> 只是窥视，不得修改；
/// 其它窗口的消息必须原样保留（线程本地钩子会看到本线程所有窗口消息）。
/// </para>
/// <para>
/// <b>WM_CHAR 的 wParam 是 Unicode 字符，不是虚拟键：</b>判断“某键是否产生回声”必须比较字符码点
/// （例如未按 Shift 的 C 产生 <c>U+0063</c>，而 VK 是 <c>0x43</c>）。混用会得到假通过。
/// </para>
/// </remarks>
internal static class TsfMessageHookPolicy
{
    /// <summary><c>WM_NULL</c>：无翻译/无派发语义，用作“已消费”的改写目标。</summary>
    internal const uint WmNull = 0x0000;

    internal const uint PmRemove = 0x0001;
    internal const uint PmNoRemove = 0x0000;

    internal const uint WmKeyDown = 0x0100;
    internal const uint WmKeyUp = 0x0101;
    internal const uint WmChar = 0x0102;
    internal const uint WmSysKeyDown = 0x0104;
    internal const uint WmSysKeyUp = 0x0105;

    /// <summary>
    /// 判断该消息是否应交给 TSF 键盘管理器判定（key down/up，含系统键）。
    /// </summary>
    internal static bool IsKeyMessage(uint message)
        => message is WmKeyDown or WmKeyUp or WmSysKeyDown or WmSysKeyUp;

    /// <summary>该消息是否 key up（决定调用 <c>TestKeyUp</c>/<c>KeyUp</c>）。</summary>
    internal static bool IsKeyUpMessage(uint message) => message is WmKeyUp or WmSysKeyUp;

    /// <summary>
    /// 该消息是否允许被改写。
    /// </summary>
    /// <param name="removal"><c>wParam</c>：<see cref="PmRemove"/> 或 <see cref="PmNoRemove"/>。</param>
    /// <param name="message">消息 ID。</param>
    /// <param name="isOwnWindow">是否属于自有 HWND。</param>
    /// <remarks>
    /// 三个条件缺一不可：只有<b>已从队列移除</b>、<b>是按键消息</b>、<b>属于自有窗口</b>的消息才可被消费改写。
    /// <c>PM_NOREMOVE</c> 窥视绝不改写，否则调用方随后取到的消息已经被破坏。
    /// </remarks>
    internal static bool CanRewrite(uint removal, uint message, bool isOwnWindow)
        => removal == PmRemove && isOwnWindow && IsKeyMessage(message);

    /// <summary>
    /// 依据 TSF 的消费结论决定该消息的处置。
    /// </summary>
    /// <param name="removal"><c>wParam</c>（PM_REMOVE / PM_NOREMOVE）。</param>
    /// <param name="message">消息 ID。</param>
    /// <param name="isOwnWindow">是否自有 HWND。</param>
    /// <param name="consumedByIme">TSF 是否<b>确实</b>消费了该按键（TestKeyDown+KeyDown 双重确认）。</param>
    /// <returns>处置结论；<see cref="TsfHookMessageAction.Consumed"/> 时调用方必须改写 MSG。</returns>
    internal static TsfHookMessageAction Decide(uint removal, uint message, bool isOwnWindow, bool consumedByIme)
    {
        if (!consumedByIme)
        {
            return TsfHookMessageAction.PassThrough;
        }

        return CanRewrite(removal, message, isOwnWindow)
            ? TsfHookMessageAction.Consumed
            : TsfHookMessageAction.PassThrough;
    }

    /// <summary>
    /// 把一个 <c>MSG</c> 改写为“已消费”形态（<see cref="WmNull"/>），保留 hwnd 与时间戳便于诊断。
    /// </summary>
    /// <param name="hwnd">原 hwnd。</param>
    /// <param name="time">原时间戳。</param>
    /// <returns>改写后的 <c>MSG</c>。</returns>
    /// <remarks>
    /// <b>为什么改成 WM_NULL 而不是清空 hwnd：</b><c>WM_NULL</c> 不参与 <c>TranslateMessage</c> 的字符生成，
    /// 也不产生有意义的派发；保留 hwnd 让上层仍能识别消息归属（不破坏其它窗口消息的链式语义）。
    /// </remarks>
    internal static NativeMsg ConsumedMessage(IntPtr hwnd, uint time)
        => new()
        {
            Hwnd = hwnd,
            Message = WmNull,
            WParam = IntPtr.Zero,
            LParam = IntPtr.Zero,
            Time = time,
            PointX = 0,
            PointY = 0,
        };

    /// <summary>
    /// 某虚拟键在“未按 Shift”时的 Unicode 字符（用于回声判定）。
    /// </summary>
    /// <param name="virtualKey">虚拟键码。</param>
    /// <returns>对应字符码点；无法确定性映射时返回 null（调用方不得据此断言）。</returns>
    /// <remarks>
    /// 只覆盖本批用于<b>合成</b>验证的确定性子集：字母 A-Z → 小写 a-z。
    /// 不猜测 Shift/CapsLock/键盘布局，避免“看起来对”的错误断言。
    /// </remarks>
    internal static int? UnshiftedCharacterFor(int virtualKey)
        => virtualKey is >= 0x41 and <= 0x5A ? virtualKey + 0x20 : null;

    /// <summary>
    /// <c>WM_CHAR</c> 的 <c>wParam</c> 是否是某虚拟键（未按 Shift）应产生的字符。
    /// </summary>
    /// <param name="virtualKey">被消费的虚拟键。</param>
    /// <param name="wParam">收到的 <c>WM_CHAR</c> wParam（Unicode 码点）。</param>
    /// <remarks>
    /// <b>这正是上一版的缺陷：</b>比较 <c>wParam == 0x43</c>（VK_C）而不是字符 <c>0x63</c>（'c'），
    /// 于是真正的回声被当成“普通字符”计数，得到假通过。
    /// </remarks>
    internal static bool IsCharacterEchoOf(int virtualKey, IntPtr wParam)
        => UnshiftedCharacterFor(virtualKey) is { } expected && wParam == (IntPtr)expected;
}

/// <summary>
/// 原生 <c>MSG</c> 布局（与 <c>user32</c> 的 <c>MSG</c> 一致）。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NativeMsg
{
    public IntPtr Hwnd;
    public uint Message;
    public IntPtr WParam;
    public IntPtr LParam;
    public uint Time;
    public int PointX;
    public int PointY;
}
