namespace CSharp.UI.OpenTK.WindowsTsf;

/// <summary>
/// 一次原生按键的<b>路由结论</b>（TI-07 P3a）。
/// </summary>
internal enum TsfKeyRoutingDecision
{
    /// <summary>把按键原样交给应用（默认路径，保持原有普通/系统/快捷键输入）。</summary>
    PassToApplication = 0,

    /// <summary>输入法消费该按键：既不派发也不翻译，因此不会产生 WM_CHAR 回声。</summary>
    Consumed = 1,
}

/// <summary>
/// <c>ITfKeystrokeMgr</c> 的按键判定与消费顺序（TI-07 P3a 的<b>纯托管</b>核心）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么单独抽出：</b>官方契约容易写错成“先 KeyDown 再判断”或“TestKeyDown 成功就消费”。
/// 这里把顺序与判据固定在一个可确定性测试的类型里，原生接线只负责传参。
/// </para>
/// <para>
/// <b>官方顺序：</b><c>TestKeyDown == S_OK &amp;&amp; eaten</c> 才调用 <c>KeyDown</c>；
/// 只有 <c>KeyDown == S_OK &amp;&amp; eaten</c> 才消费。任一条件不满足都必须交回应用。
/// </para>
/// <para>
/// <b>绝不使用</b>“记住上一次是否吞掉按键”的全局标志，也不按文本/时间窗猜测；
/// 是否消费完全由该次调用的返回值决定。
/// </para>
/// <para>
/// <b>重复/按住：</b>每个 WM_KEYDOWN（含系统键与自动重复）各走一次完整判定，
/// 因此被消费的按键在其重复期间也逐次消费，key up 单独判定。
/// </para>
/// </remarks>
internal static class TsfKeyRouting
{
    /// <summary><c>S_OK</c>：调用成功（只有该值配合 eaten 才有意义）。</summary>
    internal const int S_OK = 0;

    /// <summary>
    /// 依据键盘管理器的两次返回决定该按键是否被消费。
    /// </summary>
    /// <param name="testHr"><c>TestKeyDown</c>/<c>TestKeyUp</c> 的 HRESULT。</param>
    /// <param name="testEaten">该调用报告的 eaten 值（非 0 表示会被处理）。</param>
    /// <param name="applyHr"><c>KeyDown</c>/<c>KeyUp</c> 的 HRESULT；未调用时传 null。</param>
    /// <param name="applyEaten"><c>KeyDown</c>/<c>KeyUp</c> 的 eaten；未调用时传 false。</param>
    /// <returns>该按键的路由结论。</returns>
    /// <remarks>
    /// <b>只做契约判定，不做任何“补救”</b>：<c>TestKeyDown</c> 返回 <c>S_FALSE</c>（无 key event sink）、
    /// 失败或 eaten 为 FALSE 时一律交回应用；<c>KeyDown</c> 失败或未 eaten 同样交回应用
    /// （此时输入法已看到该键但选择不处理，应用仍需收到）。
    /// </remarks>
    internal static TsfKeyRoutingDecision Decide(
        int testHr,
        bool testEaten,
        int? applyHr,
        bool applyEaten)
    {
        // 第一步：TestKeyDown 必须成功且报告“会被处理”，否则不得调用 KeyDown，也不得消费。
        if (testHr != S_OK || !testEaten)
        {
            return TsfKeyRoutingDecision.PassToApplication;
        }

        // 第二步：必须真的调用过 KeyDown/KeyUp，且它成功并确认处理，才算消费。
        if (applyHr is not { } hr || hr != S_OK || !applyEaten)
        {
            return TsfKeyRoutingDecision.PassToApplication;
        }

        return TsfKeyRoutingDecision.Consumed;
    }

    /// <summary>
    /// 该消息是否是需要经过 TSF 键盘管理器判定的按键消息。
    /// </summary>
    /// <param name="message">窗口消息 ID。</param>
    /// <remarks>
    /// 只处理 key down / key up（含系统键）。<c>WM_CHAR</c>、鼠标、IME 合成消息等不在此列：
    /// 它们要么由翻译阶段产生，要么属于其它归属路径，不能被“顺手”吞掉。
    /// </remarks>
    internal static bool IsKeyMessage(uint message)
        => message is WmKeyDown or WmSysKeyDown or WmKeyUp or WmSysKeyUp;

    /// <summary>该消息是否是 key up（用于选择 <c>TestKeyUp</c>/<c>KeyUp</c>）。</summary>
    internal static bool IsKeyUp(uint message) => message is WmKeyUp or WmSysKeyUp;

    internal const uint WmKeyDown = 0x0100;
    internal const uint WmKeyUp = 0x0101;
    internal const uint WmSysKeyDown = 0x0104;
    internal const uint WmSysKeyUp = 0x0105;
}
