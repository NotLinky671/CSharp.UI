namespace CSharp.UI;

/// <summary>
/// 可选内部能力（TI-07 P2a）：平台桥按 <b>epoch + 已提交文档版本</b> 设置已提交选区。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么需要它：</b>TSF 的 store 会报告与组合无关的选区变化（例如用户点击移动 caret、方向键移动）。
/// 框架不提供“设置选区”的公开入口，也不允许平台桥伪造按键/指针事件来驱动选区——那会把平台行为
/// 混进用户输入通道，并且无法与真实的键鼠事件区分。
/// </para>
/// <para>
/// <b>边界（刻意很小）：</b>只有选区。不写绑定、不进入撤销历史、不触发编辑事务、不产生平台调用；
/// 端点按核心字素规则吸附并只标记布局失效（不在回调内排版）。
/// </para>
/// <para>
/// <b>归属保护：</b>实现必须校验 epoch 与文档版本，任一不符即拒绝；已被撤销或正在应用组合批次的客户端
/// 一律拒绝。组合进行中调用它同样拒绝——预编辑期间的选区必须通过
/// <see cref="CompositionEvent.Update"/> 修改组合内 caret，不能把预编辑当成已提交文本去移动选区。
/// </para>
/// <para>该接口是 internal 的可选扩展：<see cref="ITextInputClient"/> 的公开契约面不变，不支持它的客户端
/// 只是让平台桥得到显式 false。</para>
/// </remarks>
internal interface ITextInputSelectionTarget
{
    /// <summary>
    /// 在 epoch 与文档版本都匹配时设置已提交选区。
    /// </summary>
    /// <param name="session">调用方捕获的会话身份；必须等于该客户端的 epoch。</param>
    /// <param name="documentVersion">调用方期望的已提交文档版本；必须等于真实回读版本。</param>
    /// <param name="selection">目标选区（UTF-16 偏移，越界会被夹取、端点会被吸附到字素边界）。</param>
    /// <returns>确实设置了选区时返回 true；epoch/版本不符、组合中、已撤销或重入时返回 false。</returns>
    bool TrySetCommittedSelection(TextInputSession session, int documentVersion, TextSelection selection);
}
