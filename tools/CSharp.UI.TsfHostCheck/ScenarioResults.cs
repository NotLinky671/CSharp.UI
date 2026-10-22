namespace CSharp.UI.TsfHostCheck;

internal enum CandidateVisualStatus
{
    NotRequired,
    Pending,
}

/// <summary>
/// 结构化场景结果：脚本 checks 与候选视觉评估分离，pending 不能被自动消费者当成完整 pass。
/// </summary>
internal readonly record struct ScenarioOutcome(
    bool ChecksPass,
    CandidateVisualStatus CandidateVisual,
    string Detail)
{
    internal bool CompletePass => ChecksPass && CandidateVisual == CandidateVisualStatus.NotRequired;

    internal string Assessment => !ChecksPass
        ? "fail"
        : CandidateVisual == CandidateVisualStatus.Pending ? "pending-visual" : "pass";
}

internal static class ScenarioAggregate
{
    /// <summary>0 = 全部脚本检查通过且无待视觉项；3 = 脚本通过但候选视觉待人工；1 = 断言失败/无场景。</summary>
    internal static int ResolveExitCode(int failed, int pending, int total)
        => failed > 0 ? 1 : pending > 0 ? 3 : total == 0 ? 1 : 0;
}
