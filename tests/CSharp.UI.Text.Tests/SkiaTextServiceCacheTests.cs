using Xunit;

namespace CSharp.UI.Skia.Tests;

/// <summary>排版/栅格缓存的有界性、复用与失效测试，以及栅格化输出约定测试。</summary>
public class SkiaTextServiceCacheTests
{
    [Fact]
    public void 相同请求_复用同一排版结果实例()
    {
        using var service = new SkiaTextService();
        var request = TextTestHelpers.Request(TextTestHelpers.MixedText, maxLines: 1);

        var first = service.Layout(request);
        var missesAfterFirst = service.LayoutMisses;
        var second = service.Layout(request);

        Assert.Same(first, second);
        Assert.Equal(missesAfterFirst, service.LayoutMisses);
        Assert.Equal(1, service.LayoutHits);
        Assert.Equal(1, service.CachedLayoutCount);
    }

    [Fact]
    public void 宽度或DPI或字体变化_不复用缓存()
    {
        using var service = new SkiaTextService();
        const string text = TextTestHelpers.ChineseText;

        var baseRequest = TextTestHelpers.Request(text, maxWidth: 200, wrapping: TextWrapping.Wrap);
        var baseLayout = service.Layout(baseRequest);
        Assert.Same(baseLayout, service.Layout(baseRequest));

        var narrower = service.Layout(baseRequest with { MaxWidth = 100 });
        var scaled = service.Layout(baseRequest with { PixelScale = 2 });
        var resized = service.Layout(baseRequest with { Font = TextTestHelpers.BodyFont.WithSize(28) });

        Assert.NotSame(baseLayout, narrower);
        Assert.NotSame(baseLayout, scaled);
        Assert.NotSame(baseLayout, resized);
        Assert.Equal(4, service.LayoutMisses);
        Assert.True(narrower.Lines.Count > baseLayout.Lines.Count);
    }

    [Fact]
    public void 排版缓存容量_高频内容变化下保持有界()
    {
        using var service = new SkiaTextService { LayoutCacheCapacity = 4 };

        for (var i = 0; i < 200; i++)
        {
            service.Layout(TextTestHelpers.Request($"item-{i}"));
            Assert.True(service.CachedLayoutCount <= 4, $"排版缓存应保持有界：{service.CachedLayoutCount}");
        }

        Assert.Equal(4, service.LayoutCacheCapacity);
        Assert.Equal(4, service.CachedLayoutCount);

        // 缩小容量立即淘汰。
        service.LayoutCacheCapacity = 2;
        Assert.Equal(2, service.CachedLayoutCount);

        // 最近使用的条目仍然命中。
        var recent = service.Layout(TextTestHelpers.Request("item-199"));
        Assert.Same(recent, service.Layout(TextTestHelpers.Request("item-199")));
    }

    [Fact]
    public void 栅格缓存_按排版结果引用复用且有界()
    {
        using var service = new SkiaTextService { RasterCacheCapacity = 2 };

        var first = service.Layout(TextTestHelpers.Request("Ag"));
        var buffer = service.Rasterize(first);
        Assert.Same(buffer, service.Rasterize(first));
        Assert.Equal(1, service.RasterMisses);
        Assert.Equal(1, service.CachedRasterCount);

        for (var i = 0; i < 50; i++)
        {
            var layout = service.Layout(TextTestHelpers.Request($"row-{i}"));
            service.Rasterize(layout);
            Assert.True(service.CachedRasterCount <= 2, $"栅格缓存应保持有界：{service.CachedRasterCount}");
        }

        Assert.Equal(2, service.CachedRasterCount);
    }

    [Fact]
    public void 清空缓存_释放资源并清零计数()
    {
        using var service = new SkiaTextService();
        var layout = service.Layout(TextTestHelpers.Request(TextTestHelpers.ChineseText, maxLines: 1));
        service.Rasterize(layout);
        Assert.Equal(1, service.CachedLayoutCount);
        Assert.Equal(1, service.CachedRasterCount);

        service.ClearCaches();
        Assert.Equal(0, service.CachedLayoutCount);
        Assert.Equal(0, service.CachedRasterCount);

        // 清空后可以重新排版并得到新实例。
        var rebuilt = service.Layout(TextTestHelpers.Request(TextTestHelpers.ChineseText, maxLines: 1));
        Assert.NotSame(layout, rebuilt);
        Assert.Equal(layout.Size.Width, rebuilt.Size.Width, 3);
    }

    [Fact]
    public void 栅格化_输出Alpha8覆盖率遮罩且尺寸按DPI放大()
    {
        using var service = new SkiaTextService();
        var layout = service.Layout(TextTestHelpers.Request("Ag中", pixelScale: 1.5));
        var raster = service.Rasterize(layout);

        Assert.Equal(PixelFormat.Alpha8, raster.Format);
        Assert.Equal((int)Math.Ceiling(layout.Size.Width * 1.5), raster.Width);
        Assert.Equal((int)Math.Ceiling(layout.Size.Height * 1.5), raster.Height);
        Assert.Equal(raster.Width, raster.Stride);
        Assert.Equal(raster.Width * raster.Height, raster.Pixels.Length);
        Assert.True(raster.Pixels.Any(value => value > 0), "栅格化结果应包含可见字形覆盖率");
        Assert.True(raster.Pixels.Max() > 128, "字形内部应接近完全不透明");
    }

    [Fact]
    public void 栅格化_空文本与极小尺寸至少为1x1()
    {
        using var service = new SkiaTextService();
        var empty = service.Layout(TextTestHelpers.Request(string.Empty));
        var raster = service.Rasterize(empty);

        Assert.Equal(1, raster.Width);
        Assert.True(raster.Height >= 1);
        Assert.Equal(PixelFormat.Alpha8, raster.Format);
    }

    [Fact]
    public void 释放后_不再可用()
    {
        var service = new SkiaTextService();
        service.Layout(TextTestHelpers.Request("Ag"));
        service.Dispose();

        Assert.Throws<ObjectDisposedException>(() => service.Layout(TextTestHelpers.Request("Ag")));
        Assert.Throws<ObjectDisposedException>(() => service.GetFontMetrics(TextTestHelpers.BodyFont));
        Assert.Throws<ObjectDisposedException>(() => service.Rasterize(service.Layout(TextTestHelpers.Request("Ag"))));
    }

    [Fact]
    public void 测量与排版一致()
    {
        using var service = new SkiaTextService();
        var request = TextTestHelpers.Request(TextTestHelpers.ChineseText, maxWidth: 120, wrapping: TextWrapping.Wrap);

        var layout = service.Layout(request);
        Assert.Equal(layout.Size, service.Measure(request));
    }
}
