namespace CSharp.UI.ImeProbe;

/// <summary>
/// WM_IME_COMPOSITION 的提交判定（纯托管，可用确定性测试覆盖）。
/// </summary>
/// <remarks>
/// lParam 的 GCS_* 标志说明“本条消息更新了哪些数据”。IMM32 的组合上下文会保留上一次查询到的内容，
/// 因此只要查询到非空结果串就计一次提交，会把旧结果/分段状态重复计数。判定必须同时依赖标志与本条查询结果，
/// 无标志但有数据时只作快照记录，不参与提交计数。
/// </remarks>
internal static class CompositionObservation
{
    /// <summary>本条消息带 GCS_RESULTSTR 且确实读到非空结果串：算一次新提交。</summary>
    internal static bool IsNewResult(bool resultFlagSet, string? resultStr)
        => resultFlagSet && !string.IsNullOrEmpty(resultStr);

    /// <summary>本条消息没有结果标志但查询仍有数据：只作快照，不算提交。</summary>
    internal static bool IsSnapshotOnly(bool resultFlagSet, string? resultStr)
        => !resultFlagSet && !string.IsNullOrEmpty(resultStr);

    /// <summary>本条消息同时携带结果串与后续预编辑串：必须按“先结果、后预编辑”的顺序处理。</summary>
    internal static bool HasResultAndPreedit(bool resultFlagSet, string? resultStr, bool compFlagSet, string? compStr)
        => IsNewResult(resultFlagSet, resultStr) && compFlagSet && !string.IsNullOrEmpty(compStr);
}
