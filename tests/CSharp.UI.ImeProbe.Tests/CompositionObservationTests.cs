using CSharp.UI.ImeProbe;
using Xunit;

namespace CSharp.UI.ImeProbe.Tests;

/// <summary>
/// 提交判定的确定性测试：IMM32 组合上下文会保留上一次查询到的数据，判定必须由本条消息的
/// GCS_RESULTSTR 标志决定，不能因为“查询仍有数据”就重复计数。
/// </summary>
public sealed class CompositionObservationTests
{
    [Fact]
    public void NewResult_RequiresFlagAndNonEmptyPayload()
    {
        Assert.True(CompositionObservation.IsNewResult(resultFlagSet: true, resultStr: "你好"));
        Assert.False(CompositionObservation.IsNewResult(resultFlagSet: true, resultStr: string.Empty));
        Assert.False(CompositionObservation.IsNewResult(resultFlagSet: true, resultStr: null));
        Assert.False(CompositionObservation.IsNewResult(resultFlagSet: false, resultStr: "你好"));
    }

    [Fact]
    public void CachedOldResultWithoutFlag_IsSnapshotOnlyAndNeverCounts()
    {
        // 本条消息没有 GCS_RESULTSTR，但组合上下文里仍保留着上一次的结果串。
        Assert.False(CompositionObservation.IsNewResult(resultFlagSet: false, resultStr: "你好"));
        Assert.True(CompositionObservation.IsSnapshotOnly(resultFlagSet: false, resultStr: "你好"));
        Assert.False(CompositionObservation.IsSnapshotOnly(resultFlagSet: true, resultStr: "你好"));
        Assert.False(CompositionObservation.IsSnapshotOnly(resultFlagSet: false, resultStr: string.Empty));
    }

    [Fact]
    public void ResultAndPreeditInOneMessage_IsDetectedForOrderedHandling()
    {
        Assert.True(CompositionObservation.HasResultAndPreedit(
            resultFlagSet: true, resultStr: "你好", compFlagSet: true, compStr: "ma"));
        // 只有结果、没有预编辑：不是“结果 + 后续预编辑”。
        Assert.False(CompositionObservation.HasResultAndPreedit(
            resultFlagSet: true, resultStr: "你好", compFlagSet: false, compStr: null));
        // 预编辑标志存在但串为空：同样不构成“结果 + 后续预编辑”。
        Assert.False(CompositionObservation.HasResultAndPreedit(
            resultFlagSet: true, resultStr: "你好", compFlagSet: true, compStr: string.Empty));
    }
}
