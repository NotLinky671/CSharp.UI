using Xunit;

namespace CSharp.UI.TsfHostCheck.Tests;

public sealed class ProgramOutcomeTests
{
    [Theory]
    [InlineData(0, true, false, true, 0)]
    [InlineData(0, false, false, true, 1)]
    [InlineData(0, true, true, true, 1)]
    [InlineData(2, false, true, true, 2)]
    [InlineData(1, true, false, true, 1)]
    [InlineData(0, true, false, false, 1)]
    [InlineData(3, true, false, true, 3)]
    [InlineData(3, true, true, true, 1)]
    public void WatchdogAndPrematureCloseAreNotSuccess(
        int runnerExit, bool completedBeforeLoopExit, bool watchdogFired, bool joined, int expected)
    {
        Assert.Equal(expected,
            ProgramOutcome.Resolve(runnerExit, completedBeforeLoopExit, watchdogFired, joined));
    }
}
