using CSharp.UI.Testing;
using Xunit;

namespace CSharp.UI.Skia.Tests;

/// <summary>
/// R17：文本遮罩必须按实际文字颜色栅格化。
/// </summary>
/// <remarks>
/// <para>
/// 断言的不变量：<c>Rasterize(layout, color)</c> 得到的 Alpha8 覆盖率，必须逐像素等于
/// “用同一排版、同一字形定位（<see cref="TextDirectReference"/>）直接绘制到透明颜色表面”的 alpha 通道。
/// 参考实现不调用 <c>Rasterize</c>，因此不是自证。
/// </para>
/// <para>
/// Skia 的字形遮罩带颜色相关的 gamma/对比度处理：在白字遮罩上染色复用于深色文字会得到明显不同的边缘
/// （负对照按“参考本身颜色相关”为条件，在遮罩颜色中立的平台上不会误报）。
/// </para>
/// </remarks>
public class TextMaskColorTests
{
    private const string UserSentence = "Hello, CSharp.UI!";

    private static readonly Color Dark = new(30, 31, 35);
    private static readonly Color Light = new(245, 246, 249);
    private static readonly Color Mid = new(128, 128, 128);

    /// <summary>逐像素最大差允许 1（0—255），吸收 8 位舍入。</summary>
    private const int MaxCoverageError = 1;

    [Fact]
    public void 默认白色遮罩与显式白色一致_保持旧契约()
    {
        using var service = new SkiaTextService();
        var layout = service.Layout(TextTestHelpers.Request(UserSentence, pixelScale: 1.5));

        var legacy = service.Rasterize(layout);
        var explicitWhite = service.Rasterize(layout, Color.White);

        Assert.Same(legacy, explicitWhite);
        Assert.Equal(PixelFormat.Alpha8, legacy.Format);
        Assert.True(legacy.Pixels.Any(value => value > 0), "白色遮罩不应为空。");
    }

    [Theory]
    [InlineData(30, 31, 35)]
    [InlineData(245, 246, 249)]
    [InlineData(128, 128, 128)]
    public void 遮罩覆盖率与同色直接绘制一致(byte r, byte g, byte b)
    {
        using var service = new SkiaTextService();
        var layout = service.Layout(TextTestHelpers.Request(UserSentence, pixelScale: 1.5));
        var color = new Color(r, g, b);

        var mask = service.Rasterize(layout, color);
        var reference = TextDirectReference.RenderCoverage(service, layout, color);

        Assert.Equal(reference.Width, mask.Width);
        Assert.Equal(reference.Height, mask.Height);
        var error = MaxDifference(mask.Pixels, reference.Pixels);
        Assert.True(
            error <= MaxCoverageError,
            $"颜色 {color} 的遮罩覆盖率必须与同色直接绘制一致：最大差 {error} > {MaxCoverageError}。");
    }

    [Fact]
    public void 白字遮罩复用于深色文字时确实不达标()
    {
        using var service = new SkiaTextService();
        var layout = service.Layout(TextTestHelpers.Request(UserSentence, pixelScale: 1.5));

        // 先独立确认该平台的直接绘制确实与颜色相关；颜色中立的实现不构成回归场景。
        var whiteReference = TextDirectReference.RenderCoverage(service, layout, Color.White);
        var darkReference = TextDirectReference.RenderCoverage(service, layout, Dark);
        var referenceError = MaxDifference(whiteReference.Pixels, darkReference.Pixels);
        if (referenceError <= MaxCoverageError)
        {
            return;
        }

        // 负对照：旧实现（固定白字遮罩）不能代表深色文字的真实覆盖率。
        var legacyWhite = service.Rasterize(layout, Color.White);
        var legacyError = MaxDifference(legacyWhite.Pixels, darkReference.Pixels);
        Assert.True(
            legacyError > MaxCoverageError,
            $"负对照：旧白字遮罩对深色文字的误差应超过阈值，实际最大差 {legacyError}。");

        // 修复后：颜色感知遮罩与深色参考一致。
        var fixedMask = service.Rasterize(layout, Dark);
        Assert.True(MaxDifference(fixedMask.Pixels, darkReference.Pixels) <= MaxCoverageError);
        Assert.True(
            legacyError > MaxDifference(fixedMask.Pixels, darkReference.Pixels),
            "颜色感知遮罩必须严格优于旧白字遮罩。");
    }

    [Fact]
    public void 栅格缓存按RGB复用且忽略alpha()
    {
        using var service = new SkiaTextService();
        var layout = service.Layout(TextTestHelpers.Request(UserSentence, pixelScale: 1.5));

        var first = service.Rasterize(layout, Dark);
        Assert.Equal(1, service.RasterMisses);

        Assert.Same(first, service.Rasterize(layout, Dark));
        Assert.Equal(1, service.RasterMisses);

        // alpha 不影响遮罩，也不应产生新的缓存条目。
        Assert.Same(first, service.Rasterize(layout, Dark.WithAlpha(0)));
        Assert.Same(first, service.Rasterize(layout, Dark.WithAlpha(128)));
        Assert.Equal(1, service.RasterMisses);
        Assert.Equal(1, service.CachedRasterCount);

        // RGB 变化必须分离缓存条目。
        var different = service.Rasterize(layout, new Color(31, 31, 35));
        Assert.NotSame(first, different);
        Assert.Equal(2, service.RasterMisses);
        Assert.Equal(2, service.CachedRasterCount);
    }

    [Fact]
    public void 颜色变化不触发重新排版()
    {
        using var service = new SkiaTextService();
        var request = TextTestHelpers.Request(UserSentence, pixelScale: 1.5);
        var layout = service.Layout(request);
        var layoutMisses = service.LayoutMisses;

        for (var i = 0; i < 6; i++)
        {
            service.Rasterize(layout, new Color((byte)(i * 40), 31, 35));
        }

        Assert.Equal(layoutMisses, service.LayoutMisses);
        Assert.Equal(1, service.CachedLayoutCount);

        // 排版几何与颜色无关：同一实例的尺寸、行与光标位置不变。
        Assert.Same(layout, service.Layout(request));
        Assert.Equal(layout.Size, service.Measure(request));
    }

    [Fact]
    public void 栅格缓存颜色条目仍受容量约束()
    {
        using var service = new SkiaTextService { RasterCacheCapacity = 2 };
        var layout = service.Layout(TextTestHelpers.Request(UserSentence, pixelScale: 1.5));

        for (var i = 0; i < 30; i++)
        {
            service.Rasterize(layout, new Color((byte)(i * 7), 31, 35));
            Assert.True(service.CachedRasterCount <= 2, $"颜色条目应受容量约束：{service.CachedRasterCount}");
        }

        Assert.Equal(2, service.CachedRasterCount);
    }

    [Fact]
    public void 颜色不改变遮罩尺寸与排版几何()
    {
        using var service = new SkiaTextService();
        var layout = service.Layout(TextTestHelpers.Request(UserSentence, pixelScale: 1.25));

        var dark = service.Rasterize(layout, Dark);
        var light = service.Rasterize(layout, Light);

        Assert.Equal(dark.Width, light.Width);
        Assert.Equal(dark.Height, light.Height);
        Assert.Equal((int)Math.Ceiling(layout.Size.Width * 1.25), dark.Width);
        Assert.Equal((int)Math.Ceiling(layout.Size.Height * 1.25), dark.Height);
        Assert.Equal(dark.Width, dark.Stride);
        Assert.Equal(dark.Width * dark.Height, dark.Pixels.Length);
    }

    private static int MaxDifference(byte[] a, byte[] b)
    {
        Assert.Equal(a.Length, b.Length);
        var max = 0;
        for (var i = 0; i < a.Length; i++)
        {
            max = Math.Max(max, Math.Abs(a[i] - b[i]));
        }

        return max;
    }
}
