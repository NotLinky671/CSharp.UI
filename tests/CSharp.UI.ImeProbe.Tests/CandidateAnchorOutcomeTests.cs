using CSharp.UI.ImeProbe;
using Xunit;

namespace CSharp.UI.ImeProbe.Tests;

/// <summary>
/// 候选锚点判定的确定性测试：API 往返成功 + 窗口矩形一致仍然只能到 OBSERVED，
/// 因为“系统候选窗口实际可见并随锚点移动”没有被机器检测，必须由截图人工核对。
/// </summary>
public sealed class CandidateAnchorOutcomeTests
{
    [Fact]
    public void Evaluate_ApiRoundTripWithoutBothPublishes_IsFail()
    {
        var verdict = CandidateAnchorOutcome.Evaluate(
            requestCount: 2, setOkCount: 1, getOkCount: 2, windowRectStable: true, composing: true);

        Assert.Equal("FAIL", verdict.Status);
    }

    [Fact]
    public void Evaluate_UnstableWindowRect_IsUnverified()
    {
        var verdict = CandidateAnchorOutcome.Evaluate(
            requestCount: 2, setOkCount: 2, getOkCount: 2, windowRectStable: false, composing: true);

        Assert.Equal("UNVERIFIED", verdict.Status);
        Assert.Contains("window rect changed", verdict.Reason);
    }

    [Fact]
    public void Evaluate_NoComposition_IsUnverified()
    {
        var verdict = CandidateAnchorOutcome.Evaluate(
            requestCount: 2, setOkCount: 2, getOkCount: 2, windowRectStable: true, composing: false);

        Assert.Equal("UNVERIFIED", verdict.Status);
        Assert.Contains("no composition", verdict.Reason);
    }

    [Fact]
    public void Evaluate_ApiRoundTripWithStableRect_IsOnlyObservedNotPass()
    {
        var verdict = CandidateAnchorOutcome.Evaluate(
            requestCount: 2, setOkCount: 2, getOkCount: 2, windowRectStable: true, composing: true);

        // 关键回归：旧的 ime-resize 会把这个形状写成 PASS，掩盖“候选实际位置未验证”。
        Assert.NotEqual("PASS", verdict.Status);
        Assert.Equal("OBSERVED", verdict.Status);
        Assert.Contains("UNVERIFIED", verdict.Reason);
    }
}
