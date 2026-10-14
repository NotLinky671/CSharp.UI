using CSharp.UI;
using CSharp.UI.Testing;
using Xunit;

namespace CSharp.UI.Tests;

/// <summary>任务 02：平台无关基础类型与服务契约。</summary>
public class PrimitiveTests
{
    [Fact]
    public void Rect_Intersection_HandlesOverlapEmptyAndTouching()
    {
        var a = new Rect(0, 0, 100, 50);
        var b = new Rect(50, 25, 100, 50);

        var overlap = a.Intersect(b);
        Assert.Equal(new Rect(50, 25, 50, 25), overlap);

        // 仅在边界相接不算相交。
        Assert.True(a.Intersect(new Rect(100, 0, 10, 10)).IsEmpty);
        Assert.False(a.IntersectsWith(new Rect(100, 0, 10, 10)));

        // 空矩形不参与相交。
        Assert.True(a.Intersect(Rect.Empty).IsEmpty);
        Assert.False(Rect.Empty.IntersectsWith(a));
    }

    [Fact]
    public void Rect_EmptyClip_IsRepresentableAndContainsNothing()
    {
        var empty = new Rect(10, 10, 0, 20);
        Assert.True(empty.IsEmpty);
        Assert.False(empty.Contains(new Point(10, 10)));
        Assert.True(empty.Intersect(new Rect(0, 0, 100, 100)).IsEmpty);
        Assert.True(Rect.FromLtrb(50, 50, 10, 10).IsEmpty);
    }

    [Fact]
    public void Rect_DeflateAndAlign_RespectAlignment()
    {
        var frame = new Rect(10, 20, 200, 100);
        var inner = frame.Deflate(new Thickness(10, 5));
        Assert.Equal(new Rect(20, 25, 180, 90), inner);

        var centered = frame.AlignInside(new Size(50, 20), Alignment.Center);
        Assert.Equal(new Rect(85, 60, 50, 20), centered);

        var stretched = frame.AlignInside(new Size(50, 20), Alignment.Stretch);
        Assert.Equal(frame, stretched);
    }

    [Fact]
    public void Constraints_ConstrainAndDeflate()
    {
        var tight = LayoutConstraints.Tight(new Size(100, 50));
        Assert.Equal(new Size(100, 50), tight.Constrain(new Size(10, 10)));
        Assert.Equal(new Size(100, 50), tight.Constrain(new Size(500, 500)));
        Assert.True(tight.IsWidthTight);
        Assert.True(tight.IsHeightTight);

        var unbounded = LayoutConstraints.Unbounded;
        Assert.False(unbounded.HasBoundedWidth);
        Assert.Equal(new Size(10, 10), unbounded.Constrain(new Size(10, 10)));

        var deflated = tight.Deflate(new Thickness(10));
        Assert.Equal(new Size(80, 30), deflated.Constrain(new Size(0, 0)));
    }

    [Fact]
    public void Constraints_ZeroSizeViewport_ProducesFiniteLayout()
    {
        using var app = new TestApp(new VStack(new Text("x"), new Button("b")).WithSpacing(4).Padding(8), new Size(0, 0));
        var commands = app.Frame();

        // 零尺寸视口：不应出现 NaN 或无穷坐标。
        foreach (var command in commands)
        {
            var bounds = command.GetBounds();
            Assert.True(double.IsFinite(bounds.X) && double.IsFinite(bounds.Y));
            Assert.True(bounds.Width < double.PositiveInfinity && bounds.Height < double.PositiveInfinity);
        }
    }

    [Fact]
    public void LogicalAndFramebufferPixels_AreDistinguishedByPixelScale()
    {
        // 根视图始终被拉伸到视口；把矩形放进 HStack 才能验证显式尺寸不受 DPI 影响。
        using var app = new TestApp(new HStack(new Rectangle(Color.Red).Size(100, 50)), new Size(400, 300), pixelScale: 2.0);
        var frame = app.Runtime.BuildFrame();

        var node = app.Runtime.Require<Rectangle>();
        Assert.Equal(100, node.Frame.Width);
        Assert.Equal(50, node.Frame.Height);
        Assert.Equal(new Size(400, 300), frame.ViewportSize);
        Assert.Equal(new Size(800, 600), frame.FramebufferSize);

        var rect = frame.Commands.OfType<RectangleCommand>().First();
        Assert.Equal(100, rect.Bounds.Width);
    }

    [Fact]
    public void Color_ParsesHexAndComposesAlpha()
    {
        Assert.Equal(new Color(255, 0, 0, 255), Color.Parse("#FF0000"));
        Assert.Equal(new Color(255, 0, 0, 128), Color.Parse("#FF000080"));
        Assert.Equal(new Color(255, 255, 255, 255), Color.Parse("#fff"));
        Assert.True(Color.TryParse("not-a-color", out _) is false);

        var half = Color.White.ScaleAlpha(0.5);
        Assert.InRange(half.A, 127, 128);
        Assert.Equal(Color.Black, Color.White.Lerp(Color.Black, 1.0));
    }

    [Fact]
    public void PixelBuffer_ReportsPremultipliedColors()
    {
        var buffer = new PixelBuffer(2, 2, PixelFormat.Rgba8888Premultiplied);
        Assert.Equal(2, buffer.Stride / buffer.BytesPerPixel);
        Assert.True(buffer.IsPremultiplied);

        // 预乘 50% 白：R=G=B=128,A=128 → 反算回白色。
        buffer.Pixels[0] = 128;
        buffer.Pixels[1] = 128;
        buffer.Pixels[2] = 128;
        buffer.Pixels[3] = 128;
        var color = buffer.GetColor(0, 0);
        Assert.Equal(255, color.R);
        Assert.Equal(128, color.A);

        var mask = new PixelBuffer(1, 1, PixelFormat.Alpha8);
        Assert.False(mask.IsPremultiplied);
        Assert.Equal(1, mask.BytesPerPixel);
    }

    [Fact]
    public void TextureHandle_InvalidByDefault()
    {
        Assert.False(default(TextureHandle).IsValid);
        var handle = new TextureHandle(7, 100, 50, false);
        Assert.True(handle.IsValid);
        Assert.Equal(new Size(50, 25), handle.LogicalSize(2.0));
    }
}

/// <summary>任务 02：文本契约的无窗口可用性。</summary>
public class TextContractTests
{
    private static readonly ITextService Service = new StubTextService();

    [Fact]
    public void Layout_UsesVisibleTextAndPlainNewlines()
    {
        var layout = Service.Layout(new TextLayoutRequest("中文 abc"));
        Assert.Equal("中文 abc", layout.Text);
        Assert.True(layout.Size.Width > 0);
        Assert.Equal(0, layout.HitTestIndex(new Point(-100, 0)));
        Assert.Equal(layout.Text.Length, layout.HitTestIndex(new Point(9999, 0)));
    }

    [Fact]
    public void CaretStops_DoNotSplitSurrogatePairs()
    {
        var layout = Service.Layout(new TextLayoutRequest("a\U0001F600b"));
        Assert.Contains(0, layout.CaretStops);
        Assert.Contains(1, layout.CaretStops);
        Assert.Contains(3, layout.CaretStops);
        Assert.Contains(4, layout.CaretStops);

        // 代理对中间索引会被吸附到合法边界。
        Assert.Contains(layout.SnapToCaretStop(2), layout.CaretStops);
        Assert.NotEqual(2, layout.SnapToCaretStop(2));
    }

    [Fact]
    public void CaretX_IsMonotonicAndRangeBoundsCoverSelection()
    {
        var layout = Service.Layout(new TextLayoutRequest("hello"));
        var previous = double.NegativeInfinity;
        foreach (var stop in layout.CaretStops)
        {
            var x = layout.GetCaretX(stop);
            Assert.True(x >= previous);
            previous = x;
        }

        var bounds = layout.GetRangeBounds(1, 4);
        Assert.True(bounds.Width > 0);
        Assert.Equal(layout.GetCaretX(1), bounds.X, 3);
        Assert.Equal(layout.GetCaretX(4), bounds.Right, 3);
    }

    [Fact]
    public void Constructor_RejectsIncompleteCaretMapping()
    {
        var line = new TextLineInfo(0, 3, new Rect(0, 0, 10, 10), 8);

        // caretStops 与 caretXs 数量不一致。
        Assert.Throws<ArgumentException>(() => new TextLayoutResult(
            "abc", FontDescription.Default, 1.0, new Size(10, 10), [line], [0, 3], [0]));

        // caretStops 未覆盖到文本结尾。
        Assert.Throws<ArgumentException>(() => new TextLayoutResult(
            "abc", FontDescription.Default, 1.0, new Size(10, 10), [line], [0], [0]));
    }

    [Fact]
    public void StubTextService_CachesLayoutsAndRasters()
    {
        var service = new StubTextService();
        var request = new TextLayoutRequest("cache me") { Font = FontDescription.Default };

        var first = service.Layout(request);
        var second = service.Layout(request);
        Assert.Same(first, second);
        Assert.Equal(1, service.LayoutMisses);
        Assert.Equal(1, service.CachedLayoutCount);

        var raster = service.Rasterize(first);
        Assert.Same(raster, service.Rasterize(first));
        Assert.Equal(1, service.RasterMisses);

        service.ClearCaches();
        Assert.Equal(0, service.CachedLayoutCount);
    }
}

/// <summary>任务 02：绘制命令接收端与命令顺序可表达性。</summary>
public class DrawCommandTests
{
    [Fact]
    public void CommandList_PreservesOrderIncludingClipPushPop()
    {
        var list = new DrawCommandList();
        var outer = new Rect(0, 0, 100, 100);
        list.Add(new RectangleCommand(outer, Color.Red));
        list.Add(new PushClipCommand(outer));
        list.Add(new TextureCommand(new TextureHandle(1, 10, 10, true), new Rect(0, 0, 10, 10), new Rect(0, 0, 10, 10), Color.White));
        list.Add(new PopClipCommand());

        Assert.Equal(4, list.Count);
        Assert.IsType<RectangleCommand>(list[0]);
        Assert.IsType<PushClipCommand>(list[1]);
        Assert.IsType<TextureCommand>(list[2]);
        Assert.IsType<PopClipCommand>(list[3]);
        Assert.Equal(outer, list[1].GetBounds());
    }
}
