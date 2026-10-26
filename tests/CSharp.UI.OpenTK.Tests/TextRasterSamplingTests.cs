using CSharp.UI.Skia;
using CSharp.UI.Testing;
using Xunit;

namespace CSharp.UI.OpenTK.Tests;

/// <summary>
/// R16 图形回归：真实 SkiaSharp 文字遮罩上传到真实 OpenGL 纹理，再由真实渲染器画到离屏 framebuffer，
/// 逐物理像素与 CPU 遮罩对照。
/// </summary>
/// <remarks>
/// <para>
/// 断言的不变量（不是“纹理非空/上传成功/整帧非黑”）：
/// 目标物理尺寸必须等于纹理像素数（1 纹素 = 1 物理像素），原点必须是整数物理像素，
/// 且 framebuffer 读回的覆盖率与 CPU 遮罩逐像素误差不超过 1（0—255）。
/// </para>
/// <para>
/// 覆盖 DPI 1 / 1.25 / 1.5 / 2 与小数逻辑原点（Padding 产生的分数物理坐标）。
/// 同一用例还包含一个“旧放置方式”的负对照：把同一张遮罩按排版尺寸（小数物理尺寸、未吸附原点）
/// 绘制，误差必须远大于阈值，证明该回归确实能捕获二次重采样。
/// </para>
/// </remarks>
[Collection(GraphicsCollection.Name)]
[Trait("Category", "Graphics")]
public sealed class TextRasterSamplingTests
{
    /// <summary>允许的逐像素 alpha 误差（0—255）；1:1 映射下应为 0，留 1 对抗 8 位舍入。</summary>
    private const double MaxPixelError = 1;

    private readonly GlContextFixture _fixture;

    /// <summary>注入共享 GL 上下文夹具。</summary>
    public TextRasterSamplingTests(GlContextFixture fixture) => _fixture = fixture;

    [Theory]
    [InlineData(1.0, 3.5)]
    [InlineData(1.25, 3.3)]
    [InlineData(1.5, 3.3)]
    [InlineData(2.0, 3.3)]
    [InlineData(1.5, 7.7)]
    [InlineData(2.0, 6.4)]
    public void 真实文字遮罩逐纹素映射到framebuffer且原点是整数物理像素(double pixelScale, double padding)
    {
        using var scope = _fixture.CreateScope();
        using var textService = new SkiaTextService();
        var viewport = new Size(320, 80);
        using var runtime = new UiRuntime(
            new VStack(new Text("中文字体边缘 English 123").WithFont(FontDescription.Default.WithSize(14))).Padding(padding),
            new UiRuntimeServices
            {
                Dispatcher = new HeadlessUiDispatcher(),
                TextService = textService,
            RichTextService = textService as IRichTextService,
                TextureService = scope.Textures,
                Clock = new TestUiClock(),
            });

        runtime.SetViewport(viewport, pixelScale);
        runtime.Start();
        runtime.ProcessPendingWork();

        var textNode = runtime.Root.FindDescendant<Text>()
            ?? throw new InvalidOperationException("未找到 Text 节点。");
        var command = Assert.Single(runtime.BuildFrame().Commands.OfType<TextureCommand>());
        var layout = Assert.IsType<TextLayoutResult>(textNode.ViewCache);

        // R17：遮罩按实际文字颜色栅格化，因此这里必须用命令的 tint（RGB）取同一张颜色感知遮罩，
        // 否则帧缓冲的 alpha（覆盖率 × tint alpha）会与白色遮罩不一致。
        var mask = textService.Rasterize(layout, command.Tint);

        // 1 纹素 = 1 物理像素；原点落在整数物理像素上（含小数逻辑原点的情况）。
        Assert.Equal(0, Math.IEEERemainder(command.Bounds.X * pixelScale, 1), 6);
        Assert.Equal(0, Math.IEEERemainder(command.Bounds.Y * pixelScale, 1), 6);
        Assert.Equal(command.Texture.PixelWidth, command.Bounds.Width * pixelScale, 6);
        Assert.Equal(command.Texture.PixelHeight, command.Bounds.Height * pixelScale, 6);
        Assert.Equal(Math.Ceiling(layout.Size.Width * pixelScale), command.Texture.PixelWidth, 6);
        Assert.Equal(Math.Ceiling(layout.Size.Height * pixelScale), command.Texture.PixelHeight, 6);

        var physicalX = (int)Math.Round(command.Bounds.X * pixelScale);
        var physicalY = (int)Math.Round(command.Bounds.Y * pixelScale);
        var actual = Render(scope, command, viewport, pixelScale);
        var error = AlphaError(actual, mask, physicalX, physicalY);

        Assert.True(mask.Pixels.Any(value => value > 0), "文字遮罩不应为空。");
        Assert.True(
            error.Mean <= MaxPixelError && error.Max <= MaxPixelError,
            $"DPI={pixelScale} padding={padding}：1:1 映射的逐像素误差应 ≤ {MaxPixelError}，" +
            $"实际 mean={error.Mean:F4} max={error.Max}（非零像素 {error.NonZero}）。");

        // 负对照：旧放置方式（排版尺寸 + 未吸附原点）必须产生远大于阈值的误差。
        var legacyOrigin = new Point(
            textNode.Frame.X,
            textNode.Frame.Y + ((textNode.Frame.Height - layout.Size.Height) / 2));
        var legacy = command with
        {
            Bounds = new Rect(Math.Round(legacyOrigin.X, 3), Math.Round(legacyOrigin.Y, 3), layout.Size.Width, layout.Size.Height),
        };
        Assert.True(
            Math.Abs((legacy.Bounds.Width * pixelScale) - command.Texture.PixelWidth) > 1e-6
                || Math.Abs(Math.IEEERemainder(legacy.Bounds.X * pixelScale, 1)) > 1e-6,
            "负对照必须确实使用了非 1:1 的尺寸或非整数原点。");

        var legacyActual = Render(scope, legacy, viewport, pixelScale);
        var legacyError = AlphaError(legacyActual, mask, physicalX, physicalY);
        Assert.True(
            legacyError.Mean > 1,
            $"旧放置方式应产生明显误差（证明回归灵敏度），实际 mean={legacyError.Mean:F4} max={legacyError.Max}。");
    }

    private static PixelBuffer Render(GlTestScope scope, TextureCommand command, Size viewport, double pixelScale)
    {
        // 隔离成单独一帧并清成透明：framebuffer 的 alpha 就等于 遮罩覆盖率 × tint alpha。
        scope.Render(new RenderFrame([command], viewport, pixelScale, Color.Transparent));
        return scope.Surface.ReadPixels();
    }

    /// <summary>把 framebuffer alpha 与放在（maskX, maskY）的 CPU 遮罩逐像素比较。</summary>
    private static (double Mean, int Max, int NonZero) AlphaError(PixelBuffer framebuffer, PixelBuffer mask, int maskX, int maskY)
    {
        double sum = 0;
        var max = 0;
        var nonZero = 0;
        for (var y = 0; y < framebuffer.Height; y++)
        {
            for (var x = 0; x < framebuffer.Width; x++)
            {
                var expected = x >= maskX && x < maskX + mask.Width && y >= maskY && y < maskY + mask.Height
                    ? mask.Pixels[((y - maskY) * mask.Stride) + (x - maskX)]
                    : (byte)0;
                var actual = framebuffer.Pixels[(y * framebuffer.Stride) + (x * 4) + 3];
                var difference = Math.Abs(expected - actual);
                sum += difference;
                max = Math.Max(max, difference);
                if (difference > 0)
                {
                    nonZero++;
                }
            }
        }

        return (sum / (framebuffer.Width * framebuffer.Height), max, nonZero);
    }
}
