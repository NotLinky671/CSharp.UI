using Xunit;

namespace CSharp.UI.TsfHostCheck.Tests;

public sealed class GeometryTests
{
    [Fact]
    public void FreshRangeReturnsLayoutRect()
    {
        var (store, sink) = StoreTestHelpers.NewStore();
        StoreTestHelpers.UnderLock(store, sink, TsfFlags.TS_LF_READWRITE,
            () => StoreTestHelpers.SetText(store, 0, 0, "ab"));
        StoreTestHelpers.Publish(store, new[] { 10, 10 });

        var (hr, rect, clipped) = StoreTestHelpers.TextExt(store, sink, 0, 1);
        Assert.Equal(TsfHResult.S_OK, hr);
        Assert.Equal(0, rect.Left);
        Assert.Equal(10, rect.Right);
        Assert.Equal(0, clipped);

        var (hr2, rect2, _) = StoreTestHelpers.TextExt(store, sink, 0, 2);
        Assert.Equal(TsfHResult.S_OK, hr2);
        Assert.Equal(20, rect2.Right);
    }

    [Fact]
    public void EmptyRangeReturnsOnePixelCaret()
    {
        var (store, sink) = StoreTestHelpers.NewStore();
        StoreTestHelpers.UnderLock(store, sink, TsfFlags.TS_LF_READWRITE,
            () => StoreTestHelpers.SetText(store, 0, 0, "ab"));
        StoreTestHelpers.Publish(store, new[] { 10, 10 });

        var (hr, rect, _) = StoreTestHelpers.TextExt(store, sink, 1, 1);
        Assert.Equal(TsfHResult.S_OK, hr);
        Assert.Equal(10, rect.Left);
        Assert.Equal(11, rect.Right);
    }

    [Fact]
    public void TextChangeInvalidatesGeometryWithoutRepublish()
    {
        var (store, sink) = StoreTestHelpers.NewStore();
        StoreTestHelpers.UnderLock(store, sink, TsfFlags.TS_LF_READWRITE,
            () => StoreTestHelpers.SetText(store, 0, 0, "ab"));
        StoreTestHelpers.Publish(store, new[] { 10, 10 });

        StoreTestHelpers.UnderLock(store, sink, TsfFlags.TS_LF_READWRITE,
            () => StoreTestHelpers.SetText(store, 2, 2, "c"));

        var (hr, _, _) = StoreTestHelpers.TextExt(store, sink, 0, 1);
        Assert.Equal(TsfHResult.TS_E_NOLAYOUT, hr);
    }

    [Fact]
    public void InvalidateLayoutRejectsOldSnapshot()
    {
        var (store, sink) = StoreTestHelpers.NewStore();
        StoreTestHelpers.UnderLock(store, sink, TsfFlags.TS_LF_READWRITE,
            () => StoreTestHelpers.SetText(store, 0, 0, "ab"));
        var surface = StoreTestHelpers.Publish(store, new[] { 10, 10 });
        store.InvalidateLayout("window-move");

        var (hr, _, _) = StoreTestHelpers.TextExt(store, sink, 0, 1);
        Assert.Equal(TsfHResult.TS_E_NOLAYOUT, hr);

        // R24：显示表面是独立的窗口客户区，不随文本布局失效而不可查。
        var (screenHr, screen) = StoreTestHelpers.ScreenExt(store, sink);
        Assert.Equal(TsfHResult.S_OK, screenHr);
        Assert.Equal(surface.ClientScreen, screen);
    }

    [Fact]
    public void FullyInvisibleRangeReturnsSuccessWithZeroRect()
    {
        var (store, sink) = StoreTestHelpers.NewStore();
        StoreTestHelpers.UnderLock(store, sink, TsfFlags.TS_LF_READWRITE,
            () => StoreTestHelpers.SetText(store, 0, 0, "ab"));
        StoreTestHelpers.Publish(store, new[] { 10, 10 }, scrollY: 50);

        var (hr, rect, clipped) = StoreTestHelpers.TextExt(store, sink, 0, 1);
        Assert.Equal(TsfHResult.S_OK, hr);
        Assert.True(rect.IsEmpty);
        Assert.Equal(1, clipped);
    }

    [Fact]
    public void PartiallyClippedRangeIsFlagged()
    {
        var (store, sink) = StoreTestHelpers.NewStore();
        StoreTestHelpers.UnderLock(store, sink, TsfFlags.TS_LF_READWRITE,
            () => StoreTestHelpers.SetText(store, 0, 0, "ab"));
        StoreTestHelpers.Publish(store, new[] { 10, 10 }, scrollY: 10);

        var (hr, rect, clipped) = StoreTestHelpers.TextExt(store, sink, 0, 1);
        Assert.Equal(TsfHResult.S_OK, hr);
        Assert.Equal(1, clipped);
        Assert.Equal(0, rect.Top);
        Assert.Equal(10, rect.Bottom);
    }

    [Fact]
    public void ScreenExtReturnsClientRect()
    {
        var (store, sink) = StoreTestHelpers.NewStore();
        StoreTestHelpers.UnderLock(store, sink, TsfFlags.TS_LF_READWRITE,
            () => StoreTestHelpers.SetText(store, 0, 0, "ab"));
        var client = TsfRect.FromLtrb(100, 200, 500, 300);
        StoreTestHelpers.Publish(store, new[] { 10, 10 }, client: client);

        var (hr, rect) = StoreTestHelpers.ScreenExt(store, sink);
        Assert.Equal(TsfHResult.S_OK, hr);
        Assert.Equal(client.Left, rect.Left);
        Assert.Equal(client.Right, rect.Right);
    }

    [Fact]
    public void HitTestUsesRealCharBoundaries()
    {
        var (store, sink) = StoreTestHelpers.NewStore();
        StoreTestHelpers.UnderLock(store, sink, TsfFlags.TS_LF_READWRITE,
            () => StoreTestHelpers.SetText(store, 0, 0, "ab"));
        StoreTestHelpers.Publish(store, new[] { 10, 10 });

        Assert.Equal((TsfHResult.S_OK, 0), StoreTestHelpers.HitTest(store, sink, 5, 5));
        Assert.Equal((TsfHResult.S_OK, 1), StoreTestHelpers.HitTest(store, sink, 15, 5));
        Assert.Equal((TsfHResult.S_OK, 2), StoreTestHelpers.HitTest(store, sink, 25, 5,
            TsfFlags.GXFPF_NEAREST));
        Assert.Equal(TsfHResult.TS_E_INVALIDPOINT, StoreTestHelpers.HitTest(store, sink, -5, 5).Hr);
        Assert.Equal((TsfHResult.S_OK, 0), StoreTestHelpers.HitTest(store, sink, -5, 5,
            TsfFlags.GXFPF_NEAREST));
    }

    [Fact]
    public void RoundNearestSelectsClosestBoundary()
    {
        var (store, sink) = StoreTestHelpers.NewStore();
        StoreTestHelpers.UnderLock(store, sink, TsfFlags.TS_LF_READWRITE,
            () => StoreTestHelpers.SetText(store, 0, 0, "ab"));
        StoreTestHelpers.Publish(store, new[] { 10, 10 });

        Assert.Equal(1, StoreTestHelpers.HitTest(store, sink, 9, 5,
            TsfFlags.GXFPF_ROUND_NEAREST).Acp);
        Assert.Equal(0, StoreTestHelpers.HitTest(store, sink, 1, 5,
            TsfFlags.GXFPF_ROUND_NEAREST).Acp);
    }

    [Fact]
    public void InvalidViewAndRangeAreRejected()
    {
        var (store, sink) = StoreTestHelpers.NewStore();
        StoreTestHelpers.UnderLock(store, sink, TsfFlags.TS_LF_READWRITE,
            () => StoreTestHelpers.SetText(store, 0, 0, "ab"));
        StoreTestHelpers.Publish(store, new[] { 10, 10 });

        // R24：非法 view 按官方样例返回 E_INVALIDARG（不再是 TF_E_INVALIDVIEW）。
        var viewHr = 0;
        StoreTestHelpers.UnderLock(store, sink, TsfFlags.TS_LF_READ,
            () => viewHr = store.GetScreenExt(99, out _));
        Assert.Equal(TsfHResult.E_INVALIDARG, viewHr);
        Assert.Equal(TsfHResult.TS_E_INVALIDPOS,
            StoreTestHelpers.TextExt(store, sink, 2, 1).Hr);
        Assert.Equal(TsfHResult.TS_E_INVALIDPOS,
            StoreTestHelpers.TextExt(store, sink, 0, 5).Hr);
    }

    [Fact]
    public void NegativeEndMeansDocumentEnd()
    {
        var (store, sink) = StoreTestHelpers.NewStore();
        StoreTestHelpers.UnderLock(store, sink, TsfFlags.TS_LF_READWRITE,
            () => StoreTestHelpers.SetText(store, 0, 0, "ab"));
        StoreTestHelpers.Publish(store, new[] { 10, 10 });

        var (hr, rect, _) = StoreTestHelpers.TextExt(store, sink, 0, -1);
        Assert.Equal(TsfHResult.S_OK, hr);
        Assert.Equal(20, rect.Right);
    }
}
