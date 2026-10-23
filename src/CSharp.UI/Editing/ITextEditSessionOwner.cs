namespace CSharp.UI;

/// <summary>
/// 文本编辑控件的内部会话所有者接缝（TE-02）：把 <see cref="TextCompositionClient"/> 与
/// 焦点/生命周期接线从 <see cref="TextField"/> 解耦，使其他编辑控件复用同一组合客户端，
/// 而不复制第二套组合状态机。
/// </summary>
/// <remarks>
/// <para>
/// 接口面严格按现有调用点冻结：只声明 <see cref="TextCompositionClient"/>、
/// <see cref="TextInputServiceSession"/> 与 <see cref="UiRuntime"/> 实际访问的成员。
/// </para>
/// <para>
/// 语义与 <see cref="TextFieldState"/> 的既有实现一致：会话/文档状态来自真实绑定同步，
/// 选区与滚动是控件自身的运行时状态；实现方保证组合回调内不排版、不调用平台服务。
/// </para>
/// </remarks>
internal interface ITextEditSessionOwner
{
    /// <summary>原子编辑会话（TI-03）：确认文本、文档版本、来源身份与失效接缝。</summary>
    TextEditSession Editor { get; }

    /// <summary>有界撤销/重做历史（TI-05）；外部变化或事务异常时由客户端/运行时清理或打断合并。</summary>
    TextEditHistory History { get; }

    /// <summary>运行时唯一的文本输入会话接线：归属判定、几何发布与取消收敛。</summary>
    TextInputServiceSession InputSession { get; }

    /// <summary>光标位置的 UTF-16 索引（始终位于字素边界）。</summary>
    int Caret { get; set; }

    /// <summary>选择锚点；与 <see cref="Caret"/> 相同表示无选择。</summary>
    int Anchor { get; set; }

    /// <summary>本节点当前原生按键的身份 token（TI-07 归属判定接缝）。</summary>
    object NativeKeyToken { get; }

    /// <summary>横向滚动偏移（逻辑像素）；无横向滚动的控件恒为 0。</summary>
    double ScrollX { get; }

    /// <summary>纵向滚动偏移（逻辑像素）；单行的 TextField 恒为 0。</summary>
    double ScrollY { get; }

    /// <summary>
    /// 当前 caret 的跨行归属（TE-04）；单行控件恒为 <see cref="TextAffinity.Unspecified"/>。
    /// 多行控件按最近点击/导航实时更新，逐字符几何快照用它消歧共享 offset 的 caret 应答。
    /// </summary>
    TextAffinity CaretAffinity { get; }

    /// <summary>
    /// 组合提交/回读前同步真实绑定：读取文本、识别外部文本/来源/长度限制变化并取消未提交段。
    /// </summary>
    /// <returns>true 表示观测到外部变化且组合已失效。</returns>
    bool SyncCompositionDocument(ViewNode node);

    /// <summary>
    /// 把一段组合结果作为一次真实原子事务提交（TI-03）；来源/配置变化或拒绝时返回真实事务结果。
    /// </summary>
    EditResult ApplyCompositionCommit(ViewNode node, TextRange range, string result);

    /// <summary>当前文档的最大长度限制；无有效控件上下文时返回 <see cref="int.MaxValue"/>。</summary>
    int MaxLength(ViewNode node);

    /// <summary>当前文本绑定的来源身份；无有效控件上下文时返回稳定的后备对象。</summary>
    object TextIdentity(ViewNode node);

    /// <summary>
    /// 节点是否仍是由本所有者驱动的编辑控件（已挂载且视图类型匹配）；不检查焦点或禁用状态。
    /// </summary>
    bool IsTextEditNode(ViewNode node);

    /// <summary>
    /// 节点当前是否仍可输入（已挂载、未禁用、视图类型匹配）；选区写入等敏感操作使用。
    /// </summary>
    bool IsEditableNode(ViewNode node);

    /// <summary>结束本状态上的焦点文本输入会话（仅当本节点仍是当前所有者；幂等）。</summary>
    bool EndInputSession();

    /// <summary>撤销归属前的本地取消：恢复活动段开始前的选区并清空未提交段。</summary>
    void CancelCompositionForRelease();

    /// <summary>声明一次原生按键的归属（TI-07 接缝）；返回控件是否应放弃该按键。</summary>
    bool TrackOriginatingKey(object nativeKeyToken, bool consumedByInputMethod);
}
