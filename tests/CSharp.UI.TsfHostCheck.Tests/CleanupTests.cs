using Xunit;

namespace CSharp.UI.TsfHostCheck.Tests;

public sealed class CleanupTests
{
    [Fact]
    public void CleanupChainContinuesAfterFailureAndException()
    {
        var executed = new List<string>();
        var chain = new CleanupChain();
        chain.Add("ok-1", () =>
        {
            executed.Add("ok-1");
            return TsfHResult.S_OK;
        });
        chain.Add("fails", () =>
        {
            executed.Add("fails");
            return TsfHResult.E_FAIL;
        });
        chain.Add("throws", () =>
        {
            executed.Add("throws");
            throw new InvalidOperationException("injected");
        });
        chain.Add("ok-2", () =>
        {
            executed.Add("ok-2");
            return TsfHResult.S_OK;
        });

        var report = chain.Run();

        Assert.Equal(new[] { "ok-1", "fails", "throws", "ok-2" }, executed);
        Assert.Equal(4, report.Steps.Count);
        Assert.Equal(2, report.Failures);
        Assert.Equal("ok-2", report.Steps[^1].Name);
        Assert.IsType<InvalidOperationException>(report.Steps[2].Exception);
    }

    [Fact]
    public void SessionCleanupIsIdempotentWithoutEngagement()
    {
        var store = new AcpTextStore(StoreTestHelpers.NoTrace);
        var host = new TsfSessionHost(store, IntPtr.Zero, null, StoreTestHelpers.NoTrace,
            sta: false, () => { });

        host.Cleanup();
        host.Cleanup();

        Assert.Equal(0, host.CleanupFailures);
        Assert.False(host.Engaged);
    }

    [Fact]
    public void UnadvisedStoreRejectsNewLockRequests()
    {
        var (store, sink) = StoreTestHelpers.NewStore();
        Assert.Equal(TsfHResult.S_OK, store.UnadviseSink(sink));

        Assert.Equal(TsfHResult.S_OK, store.RequestLock(
            TsfFlags.TS_LF_SYNC | TsfFlags.TS_LF_READ, out var sessionHr));
        Assert.Equal(TsfHResult.E_FAIL, sessionHr);
    }

    [Fact]
    public void CleanupAfterCompositionClearsTracking()
    {
        var (store, sink) = StoreTestHelpers.NewStore();
        store.ResetDocument(string.Empty);
        store.BeginComposition(0, 0);
        StoreTestHelpers.UnderLock(store, sink, TsfFlags.TS_LF_READWRITE,
            () => StoreTestHelpers.InsertText(store, "ni"));
        Assert.True(store.IsComposing);

        var host = new TsfSessionHost(store, IntPtr.Zero, null, StoreTestHelpers.NoTrace,
            sta: false, () => { });
        host.Cleanup();

        Assert.False(store.IsComposing);
        Assert.Equal(0, host.CleanupFailures);
    }
}
