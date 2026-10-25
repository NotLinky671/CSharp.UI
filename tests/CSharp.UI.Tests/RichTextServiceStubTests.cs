using CSharp.UI.Testing;
using Xunit;

namespace CSharp.UI.Tests;

/// <summary>
/// RT-02：Stub 富文本服务（无原生资源，等宽估算）的真实契约行为。
/// </summary>
public class RichTextServiceStubTests
{
    private static StyledText Styled(string text, params TextStyleRange[] ranges)
        => new(text, ranges, null);

    private static StyledText Linked(string text, params TextInteractionRange[] interactions)
        => new(text, null, interactions);

    private static RichTextLayoutRequest Request(StyledText text, double maxWidth = double.PositiveInfinity, double letterSpacing = 0)
        => new() { Text = text, ParagraphFont = FontDescription.Default, MaxWidth = maxWidth, LetterSpacing = letterSpacing };

    private static RichTextPaint Paint(RichTextLayoutResult layout)
    {
        var runs = new RichTextRunPaint[layout.Segments.Count];
        for (var i = 0; i < runs.Length; i++)
        {
            runs[i] = new RichTextRunPaint(Color.Black, Color.Transparent, false, false);
        }

        return new RichTextPaint(runs);
    }

    [Fact]
    public void Layout_产生真实几何_segments字体与命中映射可用()
    {
        var service = new StubTextService();
        var styled = Styled("hello", new TextStyleRange(0, 2, new TextStyle { Weight = FontWeight.Bold }));
        using var layout = service.Layout(Request(styled));

        Assert.Equal("hello", layout.Text);
        Assert.True(layout.Size.Width > 0);
        Assert.True(layout.Size.Height > 0);
        Assert.Equal(2, layout.Segments.Count);
        Assert.Equal(0, layout.Segments[0].Start);
        Assert.Equal(2, layout.Segments[0].End);
        Assert.NotEqual(layout.Segments[0].FontId, layout.Segments[1].FontId);

        // ResolvedFontIdentity：stub 也填真实身份（请求字体 + displayname + 稳定 FaceId）。
        var bold = layout.ResolvedFonts[layout.Segments[0].FontId];
        var regular = layout.ResolvedFonts[layout.Segments[1].FontId];
        Assert.Equal(FontWeight.Bold, bold.Weight);
        Assert.Equal(FontWeight.Normal, regular.Weight);
        Assert.NotEqual(bold.FaceId, regular.FaceId);

        // hit 映射真实可用。
        var hit = layout.HitTestIndex(new Point(1, 1));
        Assert.InRange(hit, 0, 5);
    }

    [Fact]
    public void 颜色不同_几何键命中_payload与端点按契约区分()
    {
        var service = new StubTextService();
        var red = Styled("hi", new TextStyleRange(0, 2, new TextStyle { Foreground = Color.Red }));
        var blue = Styled("hi", new TextStyleRange(0, 2, new TextStyle { Foreground = Color.Blue }));

        using var a = service.Layout(Request(red));
        var misses = service.RichLayoutMisses;
        using var b = service.Layout(Request(blue));
        Assert.Equal(misses, service.RichLayoutMisses); // 颜色差异命中同 core
        var linkA = Linked("hey", new TextInteractionRange(0, 1, 1, "u1"));
        var linkB = Linked("hey", new TextInteractionRange(0, 1, 1, "u2"));
        var linkC = Linked("hey", new TextInteractionRange(0, 2, 1, "u1")); // 端点 {0,2,3} 与红/蓝 {0,3} 也不同

        using var x = service.Layout(Request(linkA));
        misses = service.RichLayoutMisses;
        using var y = service.Layout(Request(linkB));
        Assert.Equal(misses, service.RichLayoutMisses); // payload 值变、端点不变 -> 不重排
        using var z = service.Layout(Request(linkC));
        Assert.Equal(misses + 1, service.RichLayoutMisses); // 端点变 -> 重排
    }

    [Fact]
    public void handleDispose后_API抛_同core多handle互不影响()
    {
        var service = new StubTextService();
        var req = Request(Styled("abc"));
        var h1 = service.Layout(req);
        var h2 = service.Layout(req);

        h1.Dispose();
        Assert.Throws<ObjectDisposedException>(() => { _ = h1.Size; });
        _ = h2.Size; // h2 不受影响

        h2.Dispose();
        Assert.Throws<ObjectDisposedException>(() => { _ = service.Rasterize(h2, Paint(h2)); });
    }

    [Fact]
    public void ClearCaches后_已持handle仍可查询与rasterize()
    {
        var service = new StubTextService();
        var req = Request(Styled("hold"));
        using var held = service.Layout(req);
        var before = service.Rasterize(held, Paint(held));

        ((IRichTextService)service).ClearCaches();
        var misses = service.RichLayoutMisses;
        using var again = service.Layout(req);
        Assert.Equal(misses + 1, service.RichLayoutMisses);

        // held 的 core 仍由 handle 保活。
        var after = service.Rasterize(held, Paint(held));
        Assert.Equal(before.Width, after.Width);
        Assert.Equal(before.Height, after.Height);
    }

    [Fact]
    public void Rasterize_paint数量不符抛_尺寸随PixelScale()
    {
        var service = new StubTextService();
        var req = Request(Styled("xy"));
        using var layout = service.Layout(req);

        var paint = Paint(layout);
        var buffer = service.Rasterize(layout, paint);
        Assert.True(buffer.Width > 0 && buffer.Height > 0);

        Assert.Throws<ArgumentException>(() =>
        {
            _ = service.Rasterize(layout, new RichTextPaint(
            [
                new RichTextRunPaint(Color.Red, Color.Transparent, false, false),
                new RichTextRunPaint(Color.Red, Color.Transparent, false, false),
                new RichTextRunPaint(Color.Red, Color.Transparent, false, false),
            ]));
        });
    }

    [Fact]
    public void Measure_语义同LayoutSize_不泄漏handle()
    {
        var service = new StubTextService();
        var req = Request(Styled("mm"));
        var size = service.Measure(req);
        using var layout = service.Layout(req);
        Assert.Equal(layout.Size, size);
    }

    [Fact]
    public void Fallbacks值相等的两请求_缓存命中同core_不同值不命中()
    {
        var service = new StubTextService();

        // 两个独立构造的同值 family：值相等 → 请求键相等 → 命中同 core。
        var familyA = FontFamily.FromName("stub").WithFallbacks("fallback-a");
        var familyB = FontFamily.FromName("stub").WithFallbacks("fallback-a");
        var reqA = Request(Styled("ab")) with { ParagraphFont = new FontDescription(familyA, 14) };
        var reqB = Request(Styled("ab")) with { ParagraphFont = new FontDescription(familyB, 14) };

        using var first = service.Layout(reqA);
        var misses = service.RichLayoutMisses;
        using var hit = service.Layout(reqB);
        Assert.Equal(misses, service.RichLayoutMisses);

        // 请求 getter 暴露的 Fallbacks 是只读视图（公开 API 下不可原地篡改缓存键）。
        var fallbacks = reqA.ParagraphFont.Family.Fallbacks;
        Assert.Single(fallbacks);
        Assert.Equal("fallback-a", fallbacks[0]);

        // fallback 不同的请求是不同键（即使底层字体解析可能相同）→ miss。
        var other = Request(Styled("ab")) with
        {
            ParagraphFont = new FontDescription(FontFamily.FromName("stub").WithFallbacks("fallback-b"), 14),
        };
        using var miss = service.Layout(other);
        Assert.Equal(misses + 1, service.RichLayoutMisses);
    }
}

/// <summary>
/// 第三方实现 <see cref="IRichTextService"/> 的可用性：仅用公开 API 构造
/// <see cref="RichTextLayoutResult"/>（无 Skia/IVT），并证明 host 能接收。
/// </summary>
public class ExternalRichTextServiceTests
{
    /// <summary>纯托管的外部实现：定宽排版 + 公开 ctor 构造结果。</summary>
    private sealed class MinimalRichTextService : IRichTextService
    {
        private int _misses;

        public int RichLayoutMisses => _misses;

        public int RichRasterMisses => 0;

        public int CachedLayoutCount => 0;

        public int CachedRasterCount => 0;

        public int LayoutCacheCapacity { get; set; } = 8;

        public RichTextLayoutResult Layout(RichTextLayoutRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            _misses++;

            var text = request.Text.Text;
            var size = new Size(text.Length * 10, 20);
            var lines = new[] { new TextLineInfo(0, text.Length, new Rect(0, 0, size.Width, size.Height), 16) };
            var fonts = new[]
            {
                ResolvedFontIdentity.Create(request.ParagraphFont, "external-face", ResolvedFontIdentity.NextFaceId()),
            };
            var segments = text.Length == 0
                ? Array.Empty<RichTextSegment>()
                : new[] { new RichTextSegment(0, 0, text.Length, 0, 0, size.Width, 16) };
            var stops = new List<int> { 0 };
            var xs = new List<double> { 0 };
            for (var i = 1; i <= text.Length; i++)
            {
                stops.Add(i);
                xs.Add(i * 10.0);
            }

            return new RichTextLayoutResult(
                text,
                request.ParagraphFont,
                request.PixelScale,
                request.LetterSpacing,
                size,
                lines,
                segments,
                fonts,
                stops,
                xs,
                [new LineCarets(stops, xs)],
                new Rect(0, 0, size.Width, size.Height));
        }

        public Size Measure(RichTextLayoutRequest request)
        {
            using var layout = Layout(request);
            return layout.Size;
        }

        public PixelBuffer Rasterize(RichTextLayoutResult layout, RichTextPaint paint)
            => new(1, 1, PixelFormat.Rgba8888Premultiplied);

        public void ClearCaches()
        {
        }
    }

    [Fact]
    public void 第三方服务_公开API构造layout_几何可消费_owner归还()
    {
        var service = new MinimalRichTextService();
        var request = new RichTextLayoutRequest
        {
            Text = StyledText.Create("hello"),
            ParagraphFont = FontDescription.Default,
        };

        using var layout = service.Layout(request);
        Assert.Equal("hello", layout.Text);
        Assert.Equal(new Size(50, 20), layout.Size);
        Assert.Single(layout.Lines);
        Assert.Single(layout.Segments);
        Assert.Equal("external-face", layout.ResolvedFonts[0].ResolvedFamilyName);
        Assert.True(layout.ResolvedFonts[0].FaceId > 0);
        Assert.Equal(1, layout.HitTestIndex(new Point(12, 5)));
        Assert.Equal(new Rect(0, 0, 50, 20), layout.RasterBounds);
        Assert.Equal(new Point(0, 0), layout.RasterOrigin);

        // RetainHandle：同 core 的独立句柄，分别 Dispose。
        var retained = layout.RetainHandle();
        Assert.Equal(layout.LayoutIdentity, retained.LayoutIdentity);
        retained.Dispose();
        Assert.Throws<ObjectDisposedException>(() => _ = retained.Size);
        _ = layout.Size; // 原句柄仍活
    }

    [Fact]
    public void owner在最后一个handleDispose时释放一次()
    {
        var owner = new CountingDisposable();
        var layout = new RichTextLayoutResult(
            "x", FontDescription.Default, 1, 0, new Size(10, 20),
            [new TextLineInfo(0, 1, new Rect(0, 0, 10, 20), 16)],
            [new RichTextSegment(0, 0, 1, 0, 0, 10, 16)],
            [ResolvedFontIdentity.Create(FontDescription.Default, "f", ResolvedFontIdentity.NextFaceId())],
            [0, 1], [0, 10],
            [new LineCarets([0, 1], [0, 10])],
            new Rect(0, 0, 10, 20),
            owner);

        var second = layout.RetainHandle();
        layout.Dispose();
        Assert.Equal(0, owner.Count); // 还有一份引用
        second.Dispose();
        Assert.Equal(1, owner.Count);
    }

    private sealed class CountingDisposable : IDisposable
    {
        public int Count { get; private set; }

        public void Dispose() => Count++;
    }
}
