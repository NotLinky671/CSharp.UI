using Xunit;

namespace CSharp.UI.TsfHostCheck.Tests;

public sealed class CompositionOwnerTests
{
    [Fact]
    public void StoreExposesOwnerCompositionSink()
    {
        var store = new AcpTextStore(StoreTestHelpers.NoTrace);
        Assert.IsAssignableFrom<ITfContextOwnerCompositionSink>(store);
    }

    [Fact]
    public void OwnerSinkDelegatesToObserver()
    {
        var store = new AcpTextStore(StoreTestHelpers.NoTrace);
        var observer = new RecordingCompositionSink();
        store.CompositionObserver = observer;
        var owner = (ITfContextOwnerCompositionSink)store;

        Assert.Equal(TsfHResult.S_OK, owner.OnStartComposition(null!, out var pfOk));
        Assert.Equal(1, pfOk);
        Assert.Equal(TsfHResult.S_OK, owner.OnUpdateComposition(null!, IntPtr.Zero));
        Assert.Equal(TsfHResult.S_OK, owner.OnEndComposition(null!));

        Assert.Equal(1, observer.StartCount);
        Assert.Equal(1, observer.UpdateCount);
        Assert.Equal(1, observer.EndCount);
    }

    [Fact]
    public void MissingObserverAcceptsStartWithoutCrash()
    {
        var store = new AcpTextStore(StoreTestHelpers.NoTrace);
        var owner = (ITfContextOwnerCompositionSink)store;

        Assert.Equal(TsfHResult.S_OK, owner.OnStartComposition(null!, out var pfOk));
        Assert.Equal(1, pfOk);
        Assert.Equal(TsfHResult.S_OK, owner.OnUpdateComposition(null!, IntPtr.Zero));
        Assert.Equal(TsfHResult.S_OK, owner.OnEndComposition(null!));
    }
}
