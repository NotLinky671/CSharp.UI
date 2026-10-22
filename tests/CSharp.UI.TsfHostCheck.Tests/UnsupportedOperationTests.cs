using System.Runtime.InteropServices;
using Xunit;

namespace CSharp.UI.TsfHostCheck.Tests;

public sealed class UnsupportedOperationTests
{
    [Fact]
    public void FormattedTextIsNotImplemented()
    {
        var store = new AcpTextStore(StoreTestHelpers.NoTrace);
        Assert.Equal(TsfHResult.E_NOTIMPL, store.GetFormattedText(0, 0, out _));
    }

    [Fact]
    public void EmbeddedObjectsAreRejected()
    {
        var store = new AcpTextStore(StoreTestHelpers.NoTrace);
        Assert.Equal(TsfHResult.TS_E_NOOBJECT, store.GetEmbedded(0, IntPtr.Zero, IntPtr.Zero, out _));
        Assert.Equal(TsfHResult.E_NOTIMPL,
            store.InsertEmbedded(0, 0, 0, new object(), out _));
        Assert.Equal(TsfHResult.E_NOTIMPL,
            store.InsertEmbeddedAtSelection(0, new object(), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero));
    }

    [Fact]
    public void QueryInsertEmbeddedExplicitlyReportsNotInsertable()
    {
        var store = new AcpTextStore(StoreTestHelpers.NoTrace);
        Assert.Equal(TsfHResult.S_OK,
            store.QueryInsertEmbedded(IntPtr.Zero, IntPtr.Zero, out var insertable));
        Assert.Equal(0, insertable);
    }

    [Fact]
    public void AttributeRequestsAreNotImplemented()
    {
        var store = new AcpTextStore(StoreTestHelpers.NoTrace);
        Assert.Equal(TsfHResult.E_NOTIMPL, store.RequestSupportedAttrs(0, 0, IntPtr.Zero));
        Assert.Equal(TsfHResult.E_NOTIMPL, store.RequestAttrsAtPosition(0, 0, IntPtr.Zero, 0));
        Assert.Equal(TsfHResult.E_NOTIMPL,
            store.RequestAttrsTransitioningAtPosition(0, 0, IntPtr.Zero, 0));
        Assert.Equal(TsfHResult.E_NOTIMPL, store.FindNextAttrTransition(
            0, 0, 0, IntPtr.Zero, 0, out _, out _, out _));

        Assert.Equal(TsfHResult.S_OK, store.RetrieveRequestedAttrs(0, IntPtr.Zero, out var fetched));
        Assert.Equal(0u, fetched);
    }

    [Fact]
    public void StatusReportsSingleSelectionNotReadOnly()
    {
        var store = new AcpTextStore(StoreTestHelpers.NoTrace);
        Assert.Equal(TsfHResult.S_OK, store.GetStatus(out var status));
        Assert.Equal(0u, status.DynamicFlags);
        Assert.Equal(0u, status.StaticFlags);
    }

    [Fact]
    public void ActiveViewAndWindowAreStable()
    {
        var store = new AcpTextStore(StoreTestHelpers.NoTrace);
        var hwnd = new IntPtr(0x1234);
        store.AttachWindow(hwnd);

        Assert.Equal(TsfHResult.S_OK, store.GetActiveView(out var view));
        Assert.Equal(1u, view);
        Assert.Equal(TsfHResult.S_OK, store.GetWnd(view, out var actual));
        Assert.Equal(hwnd, actual);
        Assert.Equal(TsfHResult.TF_E_INVALIDVIEW, store.GetWnd(view + 1, out _));
    }
}
