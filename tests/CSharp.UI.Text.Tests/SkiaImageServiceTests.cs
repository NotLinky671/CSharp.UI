using SkiaSharp;
using Xunit;

namespace CSharp.UI.Skia.Tests;

/// <summary>图片解码、预乘像素格式、按来源缓存与失败行为测试。</summary>
public class SkiaImageServiceTests
{
    /// <summary>生成一个纯色 PNG 临时文件。</summary>
    private static string WritePng(int width, int height, byte alpha = 255)
    {
        var path = TextTestHelpers.UniqueTempPath(".png");
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            using var paint = new SKPaint { Color = new SKColor(255, 0, 0, alpha) };
            canvas.DrawRect(new SKRect(0, 0, width, height), paint);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        Assert.NotNull(data);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    private static void DeleteAll(params string[] paths)
    {
        foreach (var path in paths)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void 解码PNG_自然尺寸与预乘像素格式正确()
    {
        var path = WritePng(8, 4, alpha: 128);
        try
        {
            using var service = new SkiaImageService();
            var resource = service.Load(path);

            Assert.Equal(path, resource.Source);
            Assert.Equal(8, resource.Pixels.Width);
            Assert.Equal(4, resource.Pixels.Height);
            Assert.Equal(PixelFormat.Rgba8888Premultiplied, resource.Pixels.Format);
            Assert.True(resource.Pixels.IsPremultiplied);
            Assert.Equal(new Size(8, 4), resource.PixelSize);
            Assert.Equal(new Size(8, 4), resource.NaturalSize);
            Assert.Equal(1.0, resource.Scale);
            Assert.False(resource.IsDisposed);

            // 预乘 8888：半透明红存为 (128,0,0,128)，反预乘后应还原原始颜色（避免透明边缘色晕的前提）。
            var color = resource.Pixels.GetColor(0, 0);
            Assert.Equal(255, color.R);
            Assert.Equal(0, color.G);
            Assert.Equal(0, color.B);
            Assert.Equal(128, color.A);
        }
        finally
        {
            DeleteAll(path);
        }
    }

    [Fact]
    public void 同一来源_复用同一实例且只解码一次()
    {
        var path = WritePng(6, 3);
        try
        {
            using var service = new SkiaImageService();
            var first = service.Load(path);
            var second = service.Load(path);

            Assert.Same(first, second);
            Assert.Equal(1, service.DecodeCount);
            Assert.Equal(1, service.CachedImageCount);
        }
        finally
        {
            DeleteAll(path);
        }
    }

    [Fact]
    public void 容量上限_超出的来源被淘汰并释放()
    {
        var paths = new[] { WritePng(2, 2), WritePng(3, 3), WritePng(4, 4), WritePng(5, 5) };
        try
        {
            using var service = new SkiaImageService(capacity: 2);
            var disposed = false;
            var first = service.Load(paths[0]);
            first.PixelsDisposed += (_, _) => disposed = true;

            service.Load(paths[1]);
            service.Load(paths[2]);
            service.Load(paths[3]);

            Assert.True(service.CachedImageCount <= 2, $"图片缓存应保持有界：{service.CachedImageCount}");
            Assert.True(first.IsDisposed, "被淘汰的图片资源应被释放");
            Assert.True(disposed, "释放时应通知纹理服务失效");

            // 被淘汰后再次请求会重新解码（4 次首次解码 + 1 次重新解码）。
            var again = service.Load(paths[0]);
            Assert.NotSame(first, again);
            Assert.Equal(5, service.DecodeCount);
            Assert.Equal(2, service.Capacity);
        }
        finally
        {
            DeleteAll(paths);
        }
    }

    [Fact]
    public void 清空缓存_释放全部资源()
    {
        var paths = new[] { WritePng(2, 2), WritePng(3, 3) };
        try
        {
            using var service = new SkiaImageService();
            var first = service.Load(paths[0]);
            service.Load(paths[1]);

            service.Clear();

            Assert.Equal(0, service.CachedImageCount);
            Assert.True(first.IsDisposed);
        }
        finally
        {
            DeleteAll(paths);
        }
    }

    [Fact]
    public void 文件不存在_TryLoad返回false且Load抛出可诊断异常()
    {
        var missing = TextTestHelpers.UniqueTempPath(".png");
        using var service = new SkiaImageService();

        Assert.False(service.TryLoad(missing, out var resource));
        Assert.Null(resource);

        var exception = Assert.Throws<ImageLoadException>(() => service.Load(missing));
        Assert.Equal(missing, exception.ImageSource);
        Assert.Contains(missing, exception.Message);
        Assert.Equal(0, service.CachedImageCount);
    }

    [Fact]
    public void 内容无效_TryLoad返回false且Load抛出()
    {
        var path = TextTestHelpers.UniqueTempPath(".png");
        File.WriteAllText(path, "这不是图片内容");
        try
        {
            using var service = new SkiaImageService();

            Assert.False(service.TryLoad(path, out _));
            Assert.Throws<ImageLoadException>(() => service.Load(path));
        }
        finally
        {
            DeleteAll(path);
        }
    }

    [Fact]
    public void 空来源_抛出参数异常且TryLoad返回false()
    {
        using var service = new SkiaImageService();

        Assert.Throws<ArgumentException>(() => service.Load("   "));
        Assert.False(service.TryLoad(string.Empty, out _));
    }

    [Fact]
    public void 释放后_不再可用()
    {
        var path = WritePng(2, 2);
        try
        {
            var service = new SkiaImageService();
            service.Load(path);
            service.Dispose();

            Assert.Equal(0, service.CachedImageCount);
            Assert.Throws<ObjectDisposedException>(() => service.Load(path));
        }
        finally
        {
            DeleteAll(path);
        }
    }
}
