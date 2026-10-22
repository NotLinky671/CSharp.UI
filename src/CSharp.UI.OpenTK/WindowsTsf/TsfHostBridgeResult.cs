namespace CSharp.UI.OpenTK.WindowsTsf;

/// <summary>
/// 正式宿主 TSF 接线失败的原因分类（TI-07 P3a）。
/// </summary>
/// <remarks>
/// 失败必须<b>可解释</b>并可用于能力报告：调用方据此决定降级到既有 IMM 路径，
/// 而不是让用户面对“没有输入法”的黑箱。
/// </remarks>
internal enum TsfHostBridgeFailure
{
    /// <summary>没有失败。</summary>
    None = 0,

    /// <summary>非 Windows 平台：完全没有调用 ole32/msctf。</summary>
    PlatformNotSupported = 1,

    /// <summary>当前线程不是 STA（COM 套间已被 MTA 占用）。</summary>
    ApartmentNotSta = 2,

    /// <summary>COM 套间初始化失败（非套间模型问题）。</summary>
    ApartmentInitFailed = 3,

    /// <summary>拿不到自有窗口句柄。</summary>
    NoWindowHandle = 4,

    /// <summary><c>TF_CreateThreadMgr</c> 失败。</summary>
    ThreadMgrUnavailable = 5,

    /// <summary><c>ITfThreadMgr::Activate</c> 失败。</summary>
    ActivateFailed = 6,

    /// <summary>创建文档管理器/上下文失败。</summary>
    DocumentContextFailed = 7,

    /// <summary>编辑完成 sink 注册失败。</summary>
    EditSinkAdviseFailed = 8,

    /// <summary>该窗口已有其它输入来源（IMM 或另一 TSF 会话）在写。</summary>
    WriterConflict = 9,

    /// <summary>初始权威同步被 store 拒绝（版本/代数不符）。</summary>
    InitialSyncFailed = 10,

    /// <summary>UI-less 能力不可用：<c>ITfThreadMgrEx</c> QI、flags 或 UIElement sink 注册失败。</summary>
    UiElementUnavailable = 11,
}

/// <summary>
/// 正式宿主 TSF 接线的一次<b>显式结果</b>（TI-07 P3a）。
/// </summary>
/// <param name="Succeeded">是否成功建立可用会话。</param>
/// <param name="Failure">失败分类；成功时为 <see cref="TsfHostBridgeFailure.None"/>。</param>
/// <param name="Reason">脱敏原因（不含用户文本），用于能力报告与诊断。</param>
internal readonly record struct TsfHostBridgeResult(
    bool Succeeded,
    TsfHostBridgeFailure Failure,
    string? Reason)
{
    internal static TsfHostBridgeResult Success { get; } =
        new(true, TsfHostBridgeFailure.None, null);

    internal static TsfHostBridgeResult Failed(TsfHostBridgeFailure failure, string reason) =>
        new(false, failure, reason);

    /// <summary>只输出分类与原因，不含用户内容。</summary>
    public override string ToString()
        => Succeeded ? "tsf-bridge ok" : $"tsf-bridge failed {Failure}: {Reason}";
}
