namespace CSharp.UI;

/// <summary>
/// 无组合能力时的文本输入服务：所有会话/组合请求都被显式拒绝，并声明只有已提交文本能力。
/// </summary>
/// <remarks>
/// <para>
/// 这是“已提交文本回退”的权威声明：宿主或测试把它作为注入值使用时，UI 行为与完全没有文本输入服务
/// 完全一致——提交文本继续通过 <see cref="UiRuntime.DispatchTextInput"/> 走既有字符通道。
/// </para>
/// <para>
/// <b>报告语义：</b><see cref="ApplyComposition"/> 只报告 <see cref="TextInputOutcome.Rejected"/> 且
/// <c>Handled=false</c>，绝不把请求阶段（Started/Updated/ResultCommitted…）映射成“已执行”的动作，
/// 也不要求宿主把该批次重投到当前焦点。
/// </para>
/// <para>它是无状态、线程安全的（所有成员只返回常量），因此可以作为默认实例重复使用。</para>
/// </remarks>
public sealed class NoTextInputService : ITextInputService
{
    /// <summary>共享实例。</summary>
    public static NoTextInputService Instance { get; } = new();

    private NoTextInputService()
    {
    }

    /// <inheritdoc />
    public TextInputCapabilities Capabilities => TextInputCapabilities.CommittedTextOnly;

    /// <inheritdoc />
    public ITextInputClient? ActiveClient => null;

    /// <summary>没有组合能力：不绑定客户端。</summary>
    public bool StartSession(ITextInputClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        return false;
    }

    /// <summary>本来就不同意，因此没有会话可以结束。</summary>
    public bool EndSession() => false;

    /// <summary>拒绝批次：不报告任何已执行动作，也不要求重投。</summary>
    public TextInputApplyResult ApplyComposition(IReadOnlyList<CompositionEvent> events)
        => TextInputApplyResult.Rejected(
            TextSnapshot.Empty,
            "当前文本输入服务没有组合能力；未执行任何组合动作，已提交字符请走既有文本通道。");

    /// <inheritdoc />
    public bool RequestCancel() => false;

    /// <summary>没有组合能力，候选锚点也无人接收。</summary>
    public bool PublishGeometry(TextInputGeometry geometry) => false;
}

/// <summary>
/// 已提交文本回退的转发入口：在有组合能力的服务与既有已提交字符通道之间选路。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么需要它：</b>TI-01 的证据显示 GLFW 字符回调与 IME 结果通道会同时出现，必须由归属和序列决定所有权，
/// 不能按字符串或时间窗去重。本转发器把“谁处理这一次输入”收敛为唯一判断点。
/// </para>
/// <para>
/// <b>回退不重放：</b>已提交文本的回退始终是平台既有的字符输入通道
/// （<see cref="DispatchCommittedText"/> → <see cref="UiRuntime.DispatchTextInput"/>）。
/// 组合批次在没有服务/没有组合能力/没有活动客户端时一律被拒绝，<b>不会</b>因为“批次里含 Result”
/// 就被重投到当前焦点——失去归属的结果重投会串写新焦点。是否允许某批次回退，由拥有该结果的
/// 客户端通过显式归属（<see cref="ITextInputService.ActiveClient"/>）决定。
/// </para>
/// <para>
/// <b>所有权：</b>转发器是无状态静态类，不持有运行时或服务；宿主在事件循环中调用。
/// 传入的服务为 null 时等价于 <see cref="NoTextInputService"/>，便于无窗口测试与只支持已提交文本的宿主。
/// </para>
/// </remarks>
public static class TextInputForwarder
{
    /// <summary>返回实际生效的服务；null 时回退为 <see cref="NoTextInputService.Instance"/>。</summary>
    public static ITextInputService EffectiveService(ITextInputService? service) => service ?? NoTextInputService.Instance;

    /// <summary>
    /// 分发已经提交的字符：始终通过 <see cref="UiRuntime.DispatchTextInput"/> 走既有通道。
    /// </summary>
    /// <remarks>
    /// 组合结果不得调用本方法（否则与 <see cref="ITextInputService.ApplyComposition"/> 的结果提交重复）；
    /// 只有“平台已提交字符”与“不具备组合能力时的回退”才使用它。
    /// </remarks>
    public static void DispatchCommittedText(UiRuntime runtime, string? text)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        runtime.DispatchTextInput(new TextInputEvent(text));
    }

    /// <summary>
    /// 应用一个组合批次；无服务/无能力/无归属时返回显式拒绝结果（不抛出、不重放）。
    /// </summary>
    /// <param name="service">文本输入服务；null 等价于 <see cref="NoTextInputService"/>。</param>
    /// <param name="events">有序组合事件批次。</param>
    /// <remarks>
    /// 返回的 <see cref="TextInputApplyResult.Outcome"/> 只包含实际完成的动作；被拒绝时只有
    /// <see cref="TextInputOutcome.Rejected"/> 且 <see cref="TextInputApplyResult.Handled"/> 为 false。
    /// 调用方不得把被拒绝的批次重投到当前焦点；已提交字符走 <see cref="DispatchCommittedText"/>。
    /// </remarks>
    public static TextInputApplyResult ApplyComposition(ITextInputService? service, IReadOnlyList<CompositionEvent> events)
    {
        if (events is null || events.Count == 0)
        {
            return TextInputApplyResult.NotHandled(TextSnapshot.Empty, "组合事件批次为空；没有需要应用的内容。");
        }

        if (service is null)
        {
            return TextInputApplyResult.Rejected(
                TextSnapshot.Empty,
                "没有文本输入服务；组合事件未执行，也不重投到当前焦点。");
        }

        if (!service.Capabilities.Composition)
        {
            return TextInputApplyResult.Rejected(
                service.ActiveClient?.Document ?? TextSnapshot.Empty,
                "文本输入服务没有组合能力；组合事件未执行，已提交字符请走既有文本通道。");
        }

        if (service.ActiveClient is null)
        {
            return TextInputApplyResult.Rejected(
                TextSnapshot.Empty,
                "文本输入服务没有活动客户端；事件归属不明确，不执行也不重投。");
        }

        return service.ApplyComposition(events);
    }
}
