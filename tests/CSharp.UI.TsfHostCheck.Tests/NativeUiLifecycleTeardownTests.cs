using Xunit;

namespace CSharp.UI.TsfHostCheck.Tests;

/// <summary>
/// R27 teardown 控制流：与真实场景共用 <see cref="NativeUiLifecycleTeardown"/>。
/// 覆盖：恢复抑制值总是发生、失焦不发 Esc 而交由 session cleanup、异常被记录且不抛出。
/// </summary>
public sealed class NativeUiLifecycleTeardownTests
{
    private static (NativeUiLifecycleTeardown.Result Result, List<string> Records,
        bool Restored, int CleanupCalls, bool RestoredValue) RunTeardown(
        bool previousSuppress,
        bool composing,
        bool ownForeground,
        bool restoreThrows = false,
        bool cleanupThrows = false,
        bool cleanupActuallyEnds = true)
    {
        var records = new List<string>();
        var restored = false;
        var restoredValue = false;
        var cleanupCalls = 0;
        var stillComposing = composing;

        var result = NativeUiLifecycleTeardown.Run(
            previousSuppress,
            restore =>
            {
                if (restoreThrows)
                {
                    throw new InvalidOperationException("restore boom");
                }

                restored = true;
                restoredValue = restore;
            },
            () => stillComposing,
            () => ownForeground,
            () =>
            {
                cleanupCalls++;
                if (cleanupThrows)
                {
                    throw new InvalidOperationException("cleanup boom");
                }

                if (cleanupActuallyEnds)
                {
                    stillComposing = false;
                }
            },
            (kind, _) => records.Add(kind));

        return (result, records, restored, cleanupCalls, restoredValue);
    }

    [Fact]
    public void RestoresSuppressValueWhenNothingToClean()
    {
        var (result, records, restored, cleanupCalls, restoredValue) =
            RunTeardown(previousSuppress: false, composing: false, ownForeground: true);

        Assert.Equal(NativeUiLifecycleTeardown.Outcome.NothingToClean, result.Outcome);
        Assert.True(restored);
        Assert.False(restoredValue);
        Assert.Equal(0, cleanupCalls);
        Assert.Contains("native-ui-lifecycle-restore", records);
    }

    [Fact]
    public void CleansOnceWhenOwnForegroundHeld()
    {
        var (result, records, _, cleanupCalls, _) =
            RunTeardown(previousSuppress: true, composing: true, ownForeground: true);

        Assert.Equal(NativeUiLifecycleTeardown.Outcome.Cleaned, result.Outcome);
        Assert.Equal(1, cleanupCalls);
        Assert.Contains("native-ui-lifecycle-teardown", records);
    }

    [Fact]
    public void CleanupThatLeavesCompositionActiveIsNotReportedAsCleaned()
    {
        // 清理回调已返回但组合仍在（例如失焦后 Esc 未生效）：不得声称 Cleaned。
        var (result, records, restored, cleanupCalls, _) =
            RunTeardown(previousSuppress: true, composing: true, ownForeground: true,
                cleanupActuallyEnds: false);

        Assert.Equal(NativeUiLifecycleTeardown.Outcome.CleanupIncomplete, result.Outcome);
        Assert.NotEqual(NativeUiLifecycleTeardown.Outcome.Cleaned, result.Outcome);
        Assert.NotNull(result.Error);
        Assert.True(restored);
        Assert.Equal(1, cleanupCalls);
        Assert.Contains("native-ui-lifecycle-teardown-incomplete", records);
        Assert.True(result.ShouldReportCleanup);
    }

    [Fact]
    public void NeverSendsEscapeWhenForegroundLost()
    {
        var (result, records, restored, cleanupCalls, _) =
            RunTeardown(previousSuppress: true, composing: true, ownForeground: false);

        // 关键：失焦时不做任何清理动作（不发 Esc 到其他应用）。
        Assert.Equal(NativeUiLifecycleTeardown.Outcome.DeferredToSession, result.Outcome);
        Assert.Equal(0, cleanupCalls);
        Assert.True(restored);
        Assert.Contains("native-ui-lifecycle-teardown-deferred", records);
        Assert.True(result.ShouldReportCleanup);
    }

    [Fact]
    public void RestoreFailureIsReportedNotThrown()
    {
        var (result, records, _, cleanupCalls, _) =
            RunTeardown(previousSuppress: true, composing: true, ownForeground: true,
                restoreThrows: true);

        Assert.Equal(NativeUiLifecycleTeardown.Outcome.Failed, result.Outcome);
        Assert.NotNull(result.Error);
        Assert.Equal(0, cleanupCalls);
        Assert.Contains("native-ui-lifecycle-teardown-failed", records);
    }

    [Fact]
    public void CleanupFailureIsReportedNotThrown()
    {
        var (result, records, _, cleanupCalls, _) =
            RunTeardown(previousSuppress: true, composing: true, ownForeground: true,
                cleanupThrows: true);

        Assert.Equal(NativeUiLifecycleTeardown.Outcome.Failed, result.Outcome);
        Assert.Equal(1, cleanupCalls);
        Assert.Contains("native-ui-lifecycle-teardown-failed", records);
    }
}
