namespace CSharp.UI;

/// <summary>组合期间原生按键的归属判定（TI-04）：由调用方提供的显式输入 token 决定，不按时间窗或文本猜测。</summary>
internal enum TextInputKeyDecision
{
    /// <summary>按键不属于当前输入法消息；控件按既有编辑命令处理。</summary>
    Forward = 0,

    /// <summary>按键属于当前输入法消息（组合中或刚结束的同一按键）；控件不得再执行编辑命令。</summary>
    Consume = 1,
}

/// <summary>
/// 原生按键归属状态（TI-04 最小接缝）：记录“某次原生按键由当前输入消息拥有/已消费”，
/// 覆盖“组合在同一原生按键内结束、随后仍进入 KeyDown”的情况。
/// </summary>
/// <remarks>
/// <para>
/// 调用方在原生消息入口把按键出现的身份 token 交给 <see cref="Track"/>，随后同一次 KeyDown 用
/// <see cref="ShouldOwn"/>/<see cref="ShouldDropForCommand"/> 判定是否把该按键交给输入法。只比较显式 token，
/// 不检查文本内容，也不使用时间窗；不同的原生按键必须传不同的 token。
/// </para>
/// <para>该接缝不涉及任何平台类型，原生桥（TI-07）负责用明确的 token 调用它。</para>
/// </remarks>
internal sealed class TextFieldKeyOwnership
{
    private object? _pendingToken;
    private bool _pendingConsumed;

    /// <summary>当前是否正在组合（由客户端在判定前设置）。</summary>
    public bool IsComposing { get; set; }

    /// <summary>
    /// 记录一次原生按键的归属。
    /// </summary>
    /// <param name="nativeKeyToken">原生按键出现的身份 token（同一次按键的 KeyDown 必须传同一个实例）。</param>
    /// <param name="consumedByInputMethod">输入法是否已声明消费该按键。</param>
    /// <returns>控件是否应在本次 KeyDown 中放弃该按键。</returns>
    public bool Track(object nativeKeyToken, bool consumedByInputMethod)
    {
        ArgumentNullException.ThrowIfNull(nativeKeyToken);
        _pendingToken = nativeKeyToken;
        _pendingConsumed = consumedByInputMethod;

        // 组合进行中所有按键都归输入法（返回 true）；只有显式声明“已消费”的按键保留一次性归属，
        // 因此组合因其它消息结束后，普通按键不会被陈旧 token 误吞。
        return consumedByInputMethod || IsComposing;
    }

    /// <summary>该次原生按键是否仍归输入法所有（组合中，或同一次已消费按键）。</summary>
    /// <remarks>
    /// 判定“组合刚刚结束的同一按键”时是<b>一次性</b>的：命中后即清空该 token，后续按键不再被误吞。
    /// </remarks>
    public TextInputKeyDecision ShouldOwn(object nativeKeyToken)
    {
        ArgumentNullException.ThrowIfNull(nativeKeyToken);
        if (IsComposing)
        {
            return TextInputKeyDecision.Consume;
        }

        if (_pendingToken is not null && _pendingConsumed && ReferenceEquals(_pendingToken, nativeKeyToken))
        {
            _pendingToken = null;
            _pendingConsumed = false;
            return TextInputKeyDecision.Consume;
        }

        return TextInputKeyDecision.Forward;
    }

    /// <summary>编辑命令键（复制/粘贴/全选等）是否必须跳过：该按键已由输入法消费。</summary>
    public bool ShouldDropForCommand(object nativeKeyToken)
        => ShouldOwn(nativeKeyToken) == TextInputKeyDecision.Consume;

    /// <summary>清空未消费的按键归属（撤销焦点归属时调用）。</summary>
    public void Reset()
    {
        _pendingToken = null;
        _pendingConsumed = false;
        IsComposing = false;
    }
}
