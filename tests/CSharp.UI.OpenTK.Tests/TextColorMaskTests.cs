using CSharp.UI.Skia;
using CSharp.UI.Testing;
using Xunit;

namespace CSharp.UI.OpenTK.Tests;

/// <summary>
/// R17 图形回归：真实 SkiaSharp 颜色感知遮罩 → 真实 OpenGL 纹理 → 真实渲染器，
/// 逐物理像素把<b>最终 RGB</b> 与“同一排版、同一 <c>DrawLine</c> 定位、直接绘制到颜色表面”的参考比较。
/// </summary>
/// <remarks>
/// <para>
/// 断言的不变量：深字浅底/浅字深底、12/14/16/28 号、中英混排与重复细竖线在 DPI 1/1.25/1.5/2 下，
/// GPU 合成结果的 RGB 与直接颜色绘制一致（阈值见 <see cref="MeanRgbThreshold"/>/<see cref="MaxRgbThreshold"/>）。
/// </para>
/// <para>
/// 负对照：在同一 GPU 路径上用“旧的白字遮罩 + 同一 tint”绘制，误差必须超过阈值；
/// 该断言以“独立参考本身颜色相关”为条件，在 Skia 遮罩颜色中立的平台上不会误报。
/// </para>
/// <para>
/// 参考实现不调用 <see cref="ITextService.Rasterize(TextLayoutResult, Color)"/>，因此不是用同一个 helper 自证。
/// </para>
/// </remarks>
[Collection(GraphicsCollection.Name)]
[Trait("Category", "Graphics")]
public sealed class TextColorMaskTests
{
    /// <summary>整块文字区域的 RGB 平均绝对差上限（0—255）：1:1 映射下只剩 8 位合成舍入。</summary>
    private const double MeanRgbThreshold = 0.75;

    /// <summary>
    /// 单像素 RGB 最大差上限（0—255）。实测颜色感知路径最大 3（8 位合成舍入），
    /// 而旧白字遮罩在深字浅底下最大 82—84，两者相差 20 倍以上，阈值仍能明确区分。
    /// </summary>
    private const int MaxRgbThreshold = 4;

    /// <summary>遮罩覆盖率逐像素最大差上限（0—255），用于判定颜色相关性。</summary>
    private const int MaxCoverageThreshold = 1;

    private static readonly Size Viewport = new(420, 96);

    private readonly GlContextFixture _fixture;

    /// <summary>注入共享 GL 上下文夹具。</summary>
    public TextColorMaskTests(GlContextFixture fixture) => _fixture = fixture;

    [Theory]
    [InlineData("Hello, CSharp.UI!", 14.0, 1.5, true)] // 用户原句：深字浅底
    [InlineData("Hello, CSharp.UI!", 14.0, 1.5, false)] // 用户原句：浅字深底
    [InlineData("Hello, CSharp.UI!", 12.0, 1.0, true)]
    [InlineData("Hello, CSharp.UI!", 16.0, 1.25, false)]
    [InlineData("Hello, CSharp.UI!", 28.0, 2.0, true)]
    [InlineData("中文 English 混排", 14.0, 1.5, true)]
    [InlineData("中文 English 混排", 16.0, 2.0, false)]
    [InlineData("llllllllllll", 14.0, 1.5, true)] // 重复细竖线
    [InlineData("llllllllllll", 28.0, 1.0, false)]
    public void 最终RGB与同一路径直接颜色绘制一致(string text, double fontSize, double pixelScale, bool darkOnLight)
    {
        using var scope = _fixture.CreateScope();
        using var textService = new SkiaTextService();

        var foreground = darkOnLight ? Theme.Light.Foreground : Theme.Dark.Foreground;
        var background = darkOnLight ? Theme.Light.Background : Theme.Dark.Background;

        var scene = new VStack(
                new Text(text).WithFont(FontDescription.Default.WithSize(fontSize)).Foreground(foreground))
            .Padding(8);

        var (runtime, _, command, layout) = BuildScene(scene, scope, textService, pixelScale);
        using (runtime)
        {
            Assert.Equal(foreground, command.Tint);
            var actual = RenderCommand(scope, command, pixelScale, background);
            var reference = DirectReference(textService, layout, command, background, actual);

            var error = RgbError(actual, reference, command, pixelScale);
            Assert.True(
                HasInk(reference, background, command, pixelScale),
                $"{Describe(text, fontSize, pixelScale, darkOnLight)}：参考区域应包含字形像素。");
            Assert.True(
                error.Mean <= MeanRgbThreshold && error.Max <= MaxRgbThreshold,
                $"{Describe(text, fontSize, pixelScale, darkOnLight)}：最终 RGB 必须与直接颜色绘制一致，" +
                $"实际 mean={error.Mean:F4} max={error.Max}（阈值 mean<={MeanRgbThreshold} max<={MaxRgbThreshold}）。");
            Console.WriteLine(
                $"[R17] {Describe(text, fontSize, pixelScale, darkOnLight)}：颜色感知遮罩 mean={error.Mean:F4} max={error.Max}");

            AssertLegacyWhiteMaskFails(textService, scope, command, layout, reference, pixelScale, background, text, fontSize, darkOnLight);
        }
    }

    [Theory]
    [InlineData("Hello, CSharp.UI!", 14.0, 1.5, 128, 1.0)] // 半透明前景色
    [InlineData("Hello, CSharp.UI!", 14.0, 1.5, 255, 0.5)] // Opacity 修饰符
    [InlineData("Hello, CSharp.UI!", 14.0, 1.5, 128, 0.5)] // 两者叠加，仍只能乘一次
    [InlineData("中文边缘 English 123", 14.0, 1.25, 128, 0.63)] // 多字体 run：整组合成，不能逐 run 传 alpha
    public void 半透明文字与透明度叠加不重复混合(string text, double fontSize, double pixelScale, byte tintAlpha, double viewOpacity)
    {
        using var scope = _fixture.CreateScope();
        using var textService = new SkiaTextService();

        var background = Theme.Light.Background;
        var foreground = Theme.Light.Foreground.WithAlpha(tintAlpha);
        var scene = new VStack(
                new Text(text).WithFont(FontDescription.Default.WithSize(fontSize)).Foreground(foreground).Opacity(viewOpacity))
            .Padding(8);

        var (runtime, _, command, layout) = BuildScene(scene, scope, textService, pixelScale);
        using (runtime)
        {
            Assert.Equal(tintAlpha, command.Tint.A);
            Assert.Equal(viewOpacity, command.Opacity, 6);

            var actual = RenderCommand(scope, command, pixelScale, background);
            var reference = DirectReference(textService, layout, command, background, actual);
            var error = RgbError(actual, reference, command, pixelScale);
            Console.WriteLine($"[R17] \"{text}\" {fontSize}px DPI={pixelScale} alpha={tintAlpha} opacity={viewOpacity}：mean={error.Mean:F4} max={error.Max}");
            Assert.True(
                error.Mean <= MeanRgbThreshold && error.Max <= MaxRgbThreshold,
                $"\"{text}\" alpha={tintAlpha} opacity={viewOpacity}：半透明文字必须只混合一次，" +
                $"实际 mean={error.Mean:F4} max={error.Max}（阈值 mean<={MeanRgbThreshold} max<={MaxRgbThreshold}）。");
        }
    }

    [Fact]
    public void 禁用按钮标签使用禁用前景色且与直接绘制一致()
    {
        using var scope = _fixture.CreateScope();
        using var textService = new SkiaTextService();

        var background = Theme.Light.Background;
        var scene = new VStack(
                new Button("Hello, CSharp.UI!") { Font = FontDescription.Default.WithSize(14) }.Disabled())
            .Padding(8);

        var (runtime, _, command, layout) = BuildScene(scene, scope, textService, 1.5, button: true);
        using (runtime)
        {
            Assert.Equal(Theme.Light.DisabledForeground, command.Tint);
            var actual = RenderCommand(scope, command, 1.5, background);
            var reference = DirectReference(textService, layout, command, background, actual);
            var error = RgbError(actual, reference, command, 1.5);
            Console.WriteLine($"[R17] 禁用按钮标签 mean={error.Mean:F4} max={error.Max}");
            Assert.True(
                error.Mean <= MeanRgbThreshold && error.Max <= MaxRgbThreshold,
                $"禁用标签必须按 DisabledForeground 栅格化且只混合一次，实际 mean={error.Mean:F4} max={error.Max}。");
        }
    }

    [Fact]
    public void 主题前景色变化使纹理缓存分离且排版缓存不失效()
    {
        using var scope = _fixture.CreateScope();
        using var textService = new SkiaTextService();

        var scene = new VStack(
                new Text("Hello, CSharp.UI!").WithFont(FontDescription.Default.WithSize(14)))
            .Padding(8);

        using var runtime = new UiRuntime(scene, new UiRuntimeServices
        {
            Dispatcher = new HeadlessUiDispatcher(),
            TextService = textService,
            RichTextService = textService as IRichTextService,
            TextureService = scope.Textures,
            Clock = new TestUiClock(),
        });
        runtime.SetEnvironment(new EnvironmentValues { Theme = Theme.Light });
        runtime.SetViewport(Viewport, 1.5);
        runtime.Start();
        runtime.ProcessPendingWork();

        var light = TextCommand(runtime);
        Assert.Equal(Theme.Light.Foreground, light.Tint);
        var createdAfterLight = scope.Textures.CreatedTextureCount;
        var layoutsAfterLight = textService.LayoutMisses;

        // 同色再生成一帧：必须复用同一张纹理，不重新上传。
        var lightAgain = TextCommand(runtime);
        Assert.Equal(light.Texture.Id, lightAgain.Texture.Id);
        Assert.Equal(createdAfterLight, scope.Textures.CreatedTextureCount);

        // 切换深色主题：文字颜色变化必须产生新的颜色感知遮罩与纹理，但排版缓存不受影响。
        runtime.SetEnvironment(new EnvironmentValues { Theme = Theme.Dark });
        var dark = TextCommand(runtime);
        Assert.Equal(Theme.Dark.Foreground, dark.Tint);
        Assert.NotEqual(light.Texture.Id, dark.Texture.Id);
        Assert.Equal(createdAfterLight + 1, scope.Textures.CreatedTextureCount);
        Assert.Equal(layoutsAfterLight, textService.LayoutMisses);
        Assert.Equal(1, textService.CachedLayoutCount);

        // 切回浅色主题：命中已有浅色纹理，不产生第三种纹理。
        runtime.SetEnvironment(new EnvironmentValues { Theme = Theme.Light });
        var backToLight = TextCommand(runtime);
        Assert.Equal(light.Texture.Id, backToLight.Texture.Id);
        Assert.Equal(createdAfterLight + 1, scope.Textures.CreatedTextureCount);
    }

    /// <summary>负对照：用旧的白字遮罩走同一 GPU 路径必须超过 RGB 阈值。</summary>
    private static void AssertLegacyWhiteMaskFails(
        SkiaTextService textService,
        GlTestScope scope,
        TextureCommand command,
        TextLayoutResult layout,
        PixelBuffer reference,
        double pixelScale,
        Color background,
        string text,
        double fontSize,
        bool darkOnLight)
    {
        // 独立的“颜色相关性”判定：只比较参考覆盖率本身，不看生产实现；
        // 完全不可见像素的颜色差不会污染该判定（浅色文字在深底上本来就接近白字遮罩）。
        var whiteCoverage = TextDirectReference.RenderCoverage(textService, layout, Color.White);
        var inkCoverage = TextDirectReference.RenderCoverage(textService, layout, command.Tint.WithAlpha(255));
        var coverageError = MaxDifference(whiteCoverage.Pixels, inkCoverage.Pixels);
        if (coverageError <= MaxCoverageThreshold)
        {
            // 该颜色下字形遮罩与白字遮罩一致，旧实现并不构成错误场景。
            Console.WriteLine(
                $"[R17] {Describe(text, fontSize, pixelScale, darkOnLight)}：参考遮罩与白字遮罩一致（max={coverageError}），跳过负对照。");
            return;
        }

        var legacyMask = textService.Rasterize(layout);
        var legacyHandle = scope.Textures.GetOrCreate(new object(), () => legacyMask);
        var legacyActual = RenderCommand(scope, command with { Texture = legacyHandle }, pixelScale, background);
        var legacyError = RgbError(legacyActual, reference, command, pixelScale);
        Console.WriteLine(
            $"[R17] {Describe(text, fontSize, pixelScale, darkOnLight)}：旧白字遮罩 mean={legacyError.Mean:F4} max={legacyError.Max}（参考遮罩差异 max={coverageError}）。");
        Assert.True(
            legacyError.Mean > MeanRgbThreshold || legacyError.Max > MaxRgbThreshold,
            $"负对照：{Describe(text, fontSize, pixelScale, darkOnLight)} 旧白字遮罩必须超过 RGB 阈值，" +
            $"实际 mean={legacyError.Mean:F4} max={legacyError.Max}（参考遮罩差异 max={coverageError}）。");
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

    private static TextureCommand TextCommand(UiRuntime runtime)
    {
        var commands = runtime.BuildFrame().Commands.OfType<TextureCommand>();
        return Assert.Single(commands, static command => command.Texture.IsAlphaMask);
    }

    private static (UiRuntime Runtime, ViewNode Node, TextureCommand Command, TextLayoutResult Layout) BuildScene(
        View scene,
        GlTestScope scope,
        SkiaTextService textService,
        double pixelScale,
        bool button = false)
    {
        var runtime = new UiRuntime(scene, new UiRuntimeServices
        {
            Dispatcher = new HeadlessUiDispatcher(),
            TextService = textService,
            RichTextService = textService as IRichTextService,
            TextureService = scope.Textures,
            Clock = new TestUiClock(),
        });

        runtime.SetViewport(Viewport, pixelScale);
        runtime.Start();
        runtime.ProcessPendingWork();

        ViewNode node = button
            ? runtime.Root.FindDescendant<Button>() ?? throw new InvalidOperationException("未找到 Button 节点。")
            : runtime.Root.FindDescendant<Text>() ?? throw new InvalidOperationException("未找到 Text 节点。");

        var command = Assert.Single(
            runtime.BuildFrame().Commands.OfType<TextureCommand>(),
            static candidate => candidate.Texture.IsAlphaMask);
        var layout = Assert.IsType<TextLayoutResult>(node.ViewCache);
        return (runtime, node, command, layout);
    }

    private static PixelBuffer RenderCommand(GlTestScope scope, TextureCommand command, double pixelScale, Color background)
    {
        scope.Render(new RenderFrame([command], Viewport, pixelScale, background));
        return scope.Surface.ReadPixels();
    }

    private static PixelBuffer DirectReference(
        SkiaTextService textService,
        TextLayoutResult layout,
        TextureCommand command,
        Color background,
        PixelBuffer actual)
    {
        // GPU 的 tint alpha = 颜色 alpha × opacity；参考按同一个最终 alpha 直接绘制。
        var alpha = (byte)Math.Clamp(Math.Round(command.Tint.A * Math.Clamp(command.Opacity, 0, 1)), 0, 255);
        return TextDirectReference.RenderOnBackground(
            textService,
            layout,
            command.Tint.WithAlpha(alpha),
            background,
            actual.Width,
            actual.Height,
            PhysicalX(command, layout.PixelScale),
            PhysicalY(command, layout.PixelScale));
    }

    private static int PhysicalX(TextureCommand command, double pixelScale) => (int)Math.Round(command.Bounds.X * pixelScale);

    private static int PhysicalY(TextureCommand command, double pixelScale) => (int)Math.Round(command.Bounds.Y * pixelScale);

    private static (double Mean, int Max) RgbError(PixelBuffer actual, PixelBuffer reference, TextureCommand command, double pixelScale)
    {
        var x0 = PhysicalX(command, pixelScale);
        var y0 = PhysicalY(command, pixelScale);
        var width = command.Texture.PixelWidth;
        var height = command.Texture.PixelHeight;
        double sum = 0;
        var count = 0;
        var max = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                for (var channel = 0; channel < 3; channel++)
                {
                    var index = ((y0 + y) * actual.Stride) + ((x0 + x) * 4) + channel;
                    var difference = Math.Abs(actual.Pixels[index] - reference.Pixels[((y0 + y) * reference.Stride) + ((x0 + x) * 4) + channel]);
                    sum += difference;
                    count++;
                    max = Math.Max(max, difference);
                }
            }
        }

        return (sum / count, max);
    }

    private static bool HasInk(PixelBuffer reference, Color background, TextureCommand command, double pixelScale)
    {
        var x0 = PhysicalX(command, pixelScale);
        var y0 = PhysicalY(command, pixelScale);
        for (var y = 0; y < command.Texture.PixelHeight; y++)
        {
            for (var x = 0; x < command.Texture.PixelWidth; x++)
            {
                var index = ((y0 + y) * reference.Stride) + ((x0 + x) * 4);
                if (Math.Abs(reference.Pixels[index] - background.R) > 2
                    || Math.Abs(reference.Pixels[index + 1] - background.G) > 2
                    || Math.Abs(reference.Pixels[index + 2] - background.B) > 2)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string Describe(string text, double fontSize, double pixelScale, bool darkOnLight)
        => $"\"{text}\" {fontSize}px DPI={pixelScale} {(darkOnLight ? "深字浅底" : "浅字深底")}";
}
