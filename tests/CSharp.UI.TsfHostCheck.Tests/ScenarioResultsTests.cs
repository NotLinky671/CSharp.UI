using Xunit;

namespace CSharp.UI.TsfHostCheck.Tests;

public sealed class ScenarioResultsTests
{
    [Fact]
    public void PendingVisualIsNotCompletePass()
    {
        var outcome = new ScenarioOutcome(true, CandidateVisualStatus.Pending, "geometry ok");
        Assert.False(outcome.CompletePass);
        Assert.Equal("pending-visual", outcome.Assessment);
    }

    [Fact]
    public void NonVisualSuccessIsCompletePass()
    {
        var outcome = new ScenarioOutcome(true, CandidateVisualStatus.NotRequired, "commit ok");
        Assert.True(outcome.CompletePass);
        Assert.Equal("pass", outcome.Assessment);
    }

    [Fact]
    public void FailedChecksAreNotSuccessEvenIfVisualNotRequired()
    {
        var outcome = new ScenarioOutcome(false, CandidateVisualStatus.NotRequired, "failed");
        Assert.False(outcome.CompletePass);
        Assert.Equal("fail", outcome.Assessment);
    }

    [Theory]
    [InlineData(0, 0, 3, 0)]
    [InlineData(0, 1, 3, 3)]
    [InlineData(1, 0, 3, 1)]
    [InlineData(1, 1, 3, 1)]
    [InlineData(0, 0, 0, 1)]
    public void ExitCodeSeparatesPendingFromFailure(int failed, int pending, int total, int expected)
    {
        Assert.Equal(expected, ScenarioAggregate.ResolveExitCode(failed, pending, total));
    }
}
