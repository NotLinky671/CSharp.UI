using CSharp.UI.OpenTK.WindowsTsf;
using Xunit;

namespace CSharp.UI.OpenTK.Tests;

/// <summary>
/// TI-07 P3b：生产 ACP 几何提供方的只读身份/裁剪/显示表面边界（消费者契约，不测源文本）。
/// </summary>
public sealed class HostAcpGeometryProviderTests
{
    private const int ScreenLeft = 10;
    private const int ScreenTop = 20;

    [Fact]
    public void TextQuery_RequiresMatchingNativeAndStoreIdentity()
    {
        var provider = Bound();

        Assert.True(provider.TryGetTextRect(0, 2, Identity(), out _, out _, out _));
        Assert.False(provider.TryGetTextRect(0, 2,
            Identity(nativeGeneration: 10), out _, out _, out _));
        Assert.False(provider.TryGetTextRect(0, 2,
            Identity(documentVersion: 8), out _, out _, out _));
        Assert.False(provider.TryGetTextRect(0, 2,
            Identity(text: "ax"), out _, out _, out _));
    }

    [Fact]
    public void TextQuery_MapsCaretAndRangeToScreenPixels()
    {
        var provider = Bound();

        Assert.True(provider.TryGetTextRect(1, 1, Identity(), out var caret, out var caretClipped, out _));
        Assert.False(caretClipped);
        Assert.Equal(ScreenLeft + 165, caret.Left); // (origin 100 + caret x 10) × 1.5
        Assert.Equal(ScreenTop + 150, caret.Top);
        Assert.Equal(2, caret.Width);

        Assert.True(provider.TryGetTextRect(0, 2, Identity(), out var range, out var rangeClipped, out _));
        Assert.False(rangeClipped);
        Assert.Equal(ScreenLeft + 150, range.Left);
        Assert.Equal(ScreenLeft + 180, range.Right);
    }

    [Fact]
    public void TextQuery_PartialAndFullClipsNeverReturnStaleCoordinates()
    {
        var partial = Bound(editableClip: new Rect(100, 100, 5, 10));
        Assert.True(partial.TryGetTextRect(0, 2, Identity(), out var clippedRect, out var clipped, out _));
        Assert.True(clipped);
        Assert.Equal(ScreenLeft + 150, clippedRect.Left);
        Assert.Equal(ScreenLeft + 158, clippedRect.Right);

        var hidden = Bound(editableClip: new Rect(400, 400, 10, 10));
        Assert.True(hidden.TryGetTextRect(0, 2, Identity(), out var empty, out var emptyClipped, out var knownEmpty));
        Assert.True(knownEmpty);
        Assert.True(emptyClipped);
        Assert.True(empty.IsEmpty);

        // caret 完全裁出 = 真正不可布局（TextField 此时已发布 Unavailable）。
        Assert.False(hidden.TryGetTextRect(1, 1, Identity(), out _, out _, out _));
    }

    [Fact]
    public void ScreenExt_SurvivesTextGeometryClear_AndOnlySurfaceInvalidationClearsIt()
    {
        var provider = Bound();
        provider.PublishSurface(TsfRect.FromLtrb(ScreenLeft, ScreenTop, 610, 320));
        provider.Clear(); // 文本布局/预编辑失效

        Assert.False(provider.TryGetTextRect(0, 1, Identity(), out _, out _, out _));
        Assert.True(provider.TryGetScreenRect(out var surface, out var hresult));
        Assert.Equal(TsfHResult.S_OK, hresult);
        Assert.Equal(TsfRect.FromLtrb(ScreenLeft, ScreenTop, 610, 320), surface);

        provider.PublishSurfaceHidden();
        Assert.True(provider.TryGetScreenRect(out var hidden, out hresult));
        Assert.Equal(TsfHResult.S_OK, hresult);
        Assert.True(hidden.IsEmpty);

        provider.PublishSurfaceFailure(TsfHResult.E_FAIL);
        Assert.False(provider.TryGetScreenRect(out _, out var failure));
        Assert.Equal(TsfHResult.E_FAIL, failure);

        provider.ClearSurface();
        Assert.False(provider.TryGetScreenRect(out _, out var noSurface));
        Assert.Equal(TsfHResult.TS_E_NOLAYOUT, noSurface);
    }

    [Fact]
    public void UnboundProvider_StillReportsNoLayoutForBothKinds()
    {
        var provider = UnboundGeometryProvider.Instance;

        Assert.False(provider.TryGetTextRect(0, 0, Identity(), out _, out _, out _));
        Assert.False(provider.TryGetScreenRect(out _, out var hresult));
        Assert.Equal(TsfHResult.TS_E_NOLAYOUT, hresult);
    }

    private static AcpGeometryQueryIdentity Identity(
        long nativeGeneration = 9, long documentVersion = 7, string text = "ab")
        => new(nativeGeneration, documentVersion, text);

    private static HostAcpGeometryProvider Bound(Rect? editableClip = null)
    {
        var provider = new HostAcpGeometryProvider();
        var layout = new TextLayoutResult(
            "ab",
            FontDescription.Default,
            1.5,
            new Size(20, 10),
            [new TextLineInfo(0, 2, new Rect(0, 0, 20, 10), 10)],
            [0, 1, 2],
            [0, 10, 20]);

        var charGeometry = new CompositionCharGeometrySnapshot
        {
            Layout = layout,
            Preedit = string.Empty,
            PreeditStart = 0,
            CaretIndex = 1,
            Origin = new Point(100, 100),
            EditableClip = editableClip ?? new Rect(0, 0, 400, 200),
            Epoch = 3,
            DocumentVersion = 2,
            LayoutSerial = 5,
            ScrollX = 0,
        };

        provider.Publish(new HostGeometryCache(
            new TextInputGeometry(new Rect(100, 100, 2, 10)),
            charGeometry,
            "ab",
            string.Empty,
            Epoch: 3,
            CoreDocumentVersion: 2,
            StoreDocumentVersion: 7,
            NativeGeneration: 9,
            MirrorRevision: 1,
            NativeRevision: 1,
            LayoutSerial: 5,
            ScrollX: 0,
            ClientScreen: TsfRect.FromLtrb(ScreenLeft, ScreenTop, 610, 320),
            PixelScale: 1.5));
        return provider;
    }
}
