using CSharp.UI.Samples.ControlGallery;
using CSharp.UI.Skia;
using CSharp.UI.Testing;
using Xunit;

namespace CSharp.UI.OpenTK.Tests;

/// <summary>
/// RL-08 图形集成：把真实 <c>GalleryApp</c> 的完整绘制命令提交给真实 <c>GlRenderer</c> / 离屏 framebuffer，
/// 验证最终 RGB（沿用 R17 颜色感知遮罩契约）、内层滚动裁剪与物理像素映射。
/// </summary>
/// <remarks>
/// <para>
/// 不使用“整帧非黑 / 纹理非空”这类空洞断言：文字区域逐物理像素与“同一排版、同一 DrawLine 定位、
/// 直接绘制到颜色表面”的独立参考比较；裁剪用“跨边界行在边界外不得出现墨迹”验证。
/// </para>
/// <para>
/// 每个用例通过 <see cref="GlContextFixture.CreateScope"/> 取得独占的纹理服务与渲染器；
/// 离屏表面按帧的逻辑视口 × DPI 重新 <c>EnsureSize</c>，因此不受其他图形用例改变 framebuffer 尺寸的影响。
/// </para>
/// </remarks>
[Collection(GraphicsCollection.Name)]
[Trait("Category", "Graphics")]
public sealed class GalleryGpuIntegrationTests
{
    private const double MeanRgbThreshold = 0.75;

    private const int MaxRgbThreshold = 4;

    private readonly GlContextFixture _fixture;

    /// <summary>注入共享 GL 上下文夹具。</summary>
    public GalleryGpuIntegrationTests(GlContextFixture fixture) => _fixture = fixture;

    [Theory]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void 控件展示真实文字最终RGB与直接绘制一致且物理像素对齐(double scale)
    {
        using var scope = _fixture.CreateScope();
        using var textService = new SkiaTextService();
        using var runtime = CreateRuntime(scope, textService, new Size(900, 700), scale);

        var node = runtime.Root.FindDescendants<Text>()
            .First(candidate => ((Text)candidate.View).Content.StartsWith("默认字体", StringComparison.Ordinal));
        var layout = Assert.IsType<TextLayoutResult>(node.ViewCache);

        var frame = runtime.BuildFrame();
        var command = FindCommand(frame, node, scale);
        var background = runtime.ClearColor;

        // 物理像素映射：原点吸附到整数物理像素，纹理像素数 = 逻辑尺寸 × DPI。
        Assert.Equal(0, Math.IEEERemainder(command.Bounds.X * scale, 1), 6);
        Assert.Equal(0, Math.IEEERemainder(command.Bounds.Y * scale, 1), 6);
        Assert.Equal(command.Texture.PixelWidth, command.Bounds.Width * scale, 6);
        Assert.Equal(command.Texture.PixelHeight, command.Bounds.Height * scale, 6);

        scope.Render(frame);
        var actual = scope.Surface.ReadPixels();
        var reference = DirectReference(textService, layout, command, background, actual);

        var error = RgbError(actual, reference, command, scale);
        Assert.True(HasInk(reference, background, command, scale), $"{scale}：参考文字区域应包含字形像素。");
        Assert.True(
            error.Mean <= MeanRgbThreshold && error.Max <= MaxRgbThreshold,
            $"{scale}：真实 Gallery 文字最终 RGB 必须与直接颜色绘制一致，实际 mean={error.Mean:F4} max={error.Max}" +
            $"（阈值 mean<={MeanRgbThreshold} max<={MaxRgbThreshold}）。");

        // 实际渲染的墨迹包围盒必须与参考一致（物理像素映射没有整段平移）。
        var actualInk = InkBounds(actual, background, command, scale);
        var referenceInk = InkBounds(reference, background, command, scale);
        Assert.True(actualInk.HasValue && referenceInk.HasValue, $"{scale}：实际与参考都应存在墨迹包围盒。");
        Assert.True(
            Math.Abs(actualInk!.Value.X - referenceInk!.Value.X) <= 1
            && Math.Abs(actualInk.Value.Y - referenceInk.Value.Y) <= 1
            && Math.Abs(actualInk.Value.Width - referenceInk.Value.Width) <= 1
            && Math.Abs(actualInk.Value.Height - referenceInk.Value.Height) <= 1,
            $"{scale}：墨迹包围盒应一致，实际 {actualInk} 参考 {referenceInk}。");

        Console.WriteLine($"[RL-08] Gallery 文字 DPI={scale}：mean={error.Mean:F4} max={error.Max} 墨迹 {actualInk}");
    }

    [Fact]
    public void 同一运行时跨断点与DPI变化后文字重新排版且最终RGB正确()
    {
        using var scope = _fixture.CreateScope();
        using var textService = new SkiaTextService();
        using var runtime = CreateRuntime(scope, textService, new Size(1024, 700), 1.0);

        var node = runtime.Root.FindDescendants<Text>()
            .First(candidate => ((Text)candidate.View).Content.StartsWith("有限宽度内自动换行", StringComparison.Ordinal));

        var checkpoints = new (Size Viewport, double Scale)[]
        {
            (new Size(1024, 700), 1.0),
            (new Size(320, 700), 1.0),
            (new Size(320, 700), 1.25),
            (new Size(320, 700), 1.5),
            (new Size(320, 700), 2.0),
            (new Size(768, 700), 1.5),
            (new Size(480, 700), 1.5),
        };

        var probes = new List<(double Width, double Scale, TextProbe Probe)>();
        foreach (var (viewport, scale) in checkpoints)
        {
            // 同一个运行时只改视口与 DPI：强制真实重排与重新上传，排除“新建窗口才正确”的缺口。
            runtime.SetViewport(viewport, scale);
            runtime.ProcessPendingWork();
            probes.Add((viewport.Width, scale, ProbeRgb(scope, textService, runtime, node, scale)));
        }

        var wideAtOne = probes.First(p => p.Width == 1024 && p.Scale == 1.0).Probe;
        var narrowAtOne = probes.First(p => p.Width == 320 && p.Scale == 1.0).Probe;
        Assert.True(
            Math.Abs(wideAtOne.Layout.Size.Width - narrowAtOne.Layout.Size.Width) > 10,
            $"跨断点必须真实重排：宽 {wideAtOne.Layout.Size.Width:0.###} 窄 {narrowAtOne.Layout.Size.Width:0.###}。");
        Assert.True(
            narrowAtOne.Layout.Lines.Count > wideAtOne.Layout.Lines.Count,
            $"窄宽度必须折出更多行：宽 {wideAtOne.Layout.Lines.Count} 窄 {narrowAtOne.Layout.Lines.Count}。");

        // DPI 不改变逻辑排版，但纹理必须以当前 DPI 重新栅格化（不是缩放旧文字纹理）。
        var narrowProbes = probes.Where(p => p.Width == 320).ToArray();
        Assert.All(
            narrowProbes,
            p => Assert.Equal(narrowAtOne.Layout.Size.Width, p.Probe.Layout.Size.Width, 3));
        foreach (var p in narrowProbes)
        {
            Assert.Equal((int)Math.Ceiling(p.Probe.Layout.Size.Width * p.Scale), p.Probe.Command.Texture.PixelWidth);
            Assert.Equal((int)Math.Ceiling(p.Probe.Layout.Size.Height * p.Scale), p.Probe.Command.Texture.PixelHeight);
        }

        Assert.Equal(
            narrowProbes.Length,
            narrowProbes.Select(p => p.Probe.Command.Texture.Id).Distinct().Count());

        foreach (var p in probes)
        {
            Console.WriteLine(
                $"[RL-08] 同运行时 {p.Width}@{p.Scale}：排版 {p.Probe.Layout.Size.Width:0.###}×{p.Probe.Layout.Size.Height:0.###}" +
                $" {p.Probe.Layout.Lines.Count} 行，纹理 {p.Probe.Command.Texture.PixelWidth}×{p.Probe.Command.Texture.PixelHeight}，" +
                $"RGB mean={p.Probe.Mean:F4} max={p.Probe.Max}，墨迹像素 {p.Probe.InkPixels}");
        }
    }

    /// <summary>一次文字探针：命令、排版、最终 RGB 误差与实际墨迹。</summary>
    private sealed record TextProbe(TextLayoutResult Layout, TextureCommand Command, double Mean, int Max, Rect Ink, int InkPixels);

    /// <summary>在当前运行时状态下取文字命令，按物理像素映射渲染整帧并与直接颜色绘制比较最终 RGB。</summary>
    private static TextProbe ProbeRgb(
        GlTestScope scope,
        SkiaTextService textService,
        UiRuntime runtime,
        ViewNode node,
        double scale)
    {
        var layout = Assert.IsType<TextLayoutResult>(node.ViewCache);
        var frame = runtime.BuildFrame();
        var command = FindCommand(frame, node, scale);
        var background = runtime.ClearColor;

        // 物理像素映射：原点吸附到整数物理像素，纹理像素数 = 逻辑尺寸 × DPI。
        Assert.Equal(0, Math.IEEERemainder(command.Bounds.X * scale, 1), 6);
        Assert.Equal(0, Math.IEEERemainder(command.Bounds.Y * scale, 1), 6);
        Assert.Equal(command.Texture.PixelWidth, command.Bounds.Width * scale, 6);
        Assert.Equal(command.Texture.PixelHeight, command.Bounds.Height * scale, 6);

        scope.Render(frame);
        var actual = scope.Surface.ReadPixels();
        var reference = DirectReference(textService, layout, command, background, actual);

        var error = RgbError(actual, reference, command, scale);
        Assert.True(HasInk(reference, background, command, scale), $"{scale}：参考文字区域应包含字形像素。");
        Assert.True(
            error.Mean <= MeanRgbThreshold && error.Max <= MaxRgbThreshold,
            $"{scale}：真实 Gallery 文字最终 RGB 必须与直接颜色绘制一致，实际 mean={error.Mean:F4} max={error.Max}" +
            $"（阈值 mean<={MeanRgbThreshold} max<={MaxRgbThreshold}）。");

        // 实际渲染的墨迹包围盒必须与参考一致（物理像素映射没有整段平移）。
        var actualInk = InkBounds(actual, background, command, scale);
        var referenceInk = InkBounds(reference, background, command, scale);
        Assert.True(actualInk.HasValue && referenceInk.HasValue, $"{scale}：实际与参考都应存在墨迹包围盒。");
        Assert.True(
            Math.Abs(actualInk!.Value.X - referenceInk!.Value.X) <= 1
            && Math.Abs(actualInk.Value.Y - referenceInk!.Value.Y) <= 1
            && Math.Abs(actualInk.Value.Width - referenceInk.Value.Width) <= 1
            && Math.Abs(actualInk.Value.Height - referenceInk.Value.Height) <= 1,
            $"{scale}：墨迹包围盒应一致，实际 {actualInk} 参考 {referenceInk}。");

        return new TextProbe(layout, command, error.Mean, error.Max, actualInk.Value, CountInk(actual, background, command, scale));
    }

    /// <summary>统计纹理区域内与背景不同的实际像素数（非空洞的墨迹采样量）。</summary>
    private static int CountInk(PixelBuffer buffer, Color background, TextureCommand command, double scale)
    {
        var x0 = PhysicalX(command, scale);
        var y0 = PhysicalY(command, scale);
        var count = 0;
        for (var y = 0; y < command.Texture.PixelHeight; y++)
        {
            for (var x = 0; x < command.Texture.PixelWidth; x++)
            {
                var index = ((y0 + y) * buffer.Stride) + ((x0 + x) * 4);
                if (Math.Abs(buffer.Pixels[index] - background.R) > 2
                    || Math.Abs(buffer.Pixels[index + 1] - background.G) > 2
                    || Math.Abs(buffer.Pixels[index + 2] - background.B) > 2)
                {
                    count++;
                }
            }
        }

        return count;
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    public void 控件展示内层滚动内容在GPU按视口裁剪(double scale)
    {
        using var scope = _fixture.CreateScope();
        using var textService = new SkiaTextService();
        using var runtime = CreateRuntime(scope, textService, new Size(900, 700), scale);

        var outer = GalleryScrollLookup.RootScroll(runtime);
        var inner = GalleryScrollLookup.NestedScroll(runtime);
        BringInnerIntoView(runtime, outer, inner);

        // 让内层内容滚动到某一行恰好横跨视口下边缘：该命令仍被生成（与视口相交），但超出下边缘的部分必须由 GPU 裁剪。
        RenderFrame? straddlingFrame = null;
        TextureCommand? straddling = null;
        for (var offset = 2.0; offset < 46.0 && straddling is null; offset += 2.0)
        {
            inner.ScrollOffsetY = offset;
            inner.InvalidateLayout();
            runtime.ProcessPendingWork();
            var frame = runtime.BuildFrame();
            straddling = frame.Commands
                .OfType<TextureCommand>()
                .FirstOrDefault(command => command.Texture.IsAlphaMask
                    && command.Bounds.Top < inner.Frame.Bottom - 1
                    && command.Bounds.Bottom > inner.Frame.Bottom + 1
                    && command.Bounds.Left >= inner.Frame.Left - 0.5
                    && command.Bounds.Right <= inner.Frame.Right + 0.5);
            if (straddling is not null)
            {
                straddlingFrame = frame;
            }
        }

        Assert.True(straddling is not null, $"{scale}：应能找到一个横跨内层视口下边缘、仍被提交绘制的文字纹理。");

        // 裁剪框必须真实对应内层视口（裁剪契约）。
        var clip = Assert.Single(
            straddlingFrame!.Commands.OfType<PushClipCommand>(),
            command => SameRect(command.Bounds, inner.Frame));

        scope.Render(straddlingFrame);
        var actual = scope.Surface.ReadPixels();
        var background = runtime.ClearColor;
        var boundary = (int)Math.Round(inner.Frame.Bottom * scale, MidpointRounding.AwayFromZero);

        var xStart = (int)Math.Ceiling(straddling!.Bounds.Left * scale) + 1;
        var xEnd = (int)Math.Floor(straddling.Bounds.Right * scale) - 1;
        Assert.True(xEnd > xStart, $"{scale}：跨边界文字应有足够的物理宽度用于采样。");
        Assert.True(boundary + 6 < actual.Height, $"{scale}：视口下边缘 {boundary} 应留出可读的帧缓冲空间。");

        // 边界以下的区域必须是背景：内层裁剪真的在 GPU 上生效（否则跨边界字形会画到视口外）。
        var leaked = 0;
        for (var y = boundary + 1; y <= boundary + 5; y++)
        {
            for (var x = xStart; x <= xEnd; x++)
            {
                if (!IsBackground(actual, x, y, background, 2))
                {
                    leaked++;
                }
            }
        }

        Assert.True(leaked == 0, $"{scale}：内层视口下边缘外不应出现墨迹，实际越界像素 {leaked}。");

        // 边界以上必须能看到该行的墨迹：证明上方确实有内容，下面的“无墨迹”来自裁剪而不是空行。
        var inkAbove = 0;
        for (var y = boundary - 5; y <= boundary - 1; y++)
        {
            for (var x = xStart; x <= xEnd; x++)
            {
                if (!IsBackground(actual, x, y, background, 2))
                {
                    inkAbove++;
                }
            }
        }

        Assert.True(inkAbove > 0, $"{scale}：内层视口内应能看到跨边界行的墨迹，实际 {inkAbove}。");
        Console.WriteLine($"[RL-08] Gallery 内层裁剪 DPI={scale}：clip={clip.Bounds} 越界像素={leaked} 边界内墨迹={inkAbove}");
    }

    private static UiRuntime CreateRuntime(GlTestScope scope, SkiaTextService textService, Size viewport, double scale)
    {
        var images = new StubImageService();
        images.Register(Path.Combine(AppContext.BaseDirectory, "shared", "Assets", "logo.png"), 64, 64);
        var runtime = new UiRuntime(new GalleryApp(), new UiRuntimeServices
        {
            Dispatcher = new HeadlessUiDispatcher(),
            TextService = textService,
            TextureService = scope.Textures,
            ImageService = images,
            Clipboard = new StubClipboardService(),
            Clock = new TestUiClock(),
            WheelScrollSettings = new StubWheelScrollSettingsService(WheelScrollSettings.Default),
        });

        runtime.SetEnvironment(new EnvironmentValues { Theme = Theme.Light });
        runtime.SetViewport(viewport, scale);
        runtime.Start();
        runtime.ProcessPendingWork();
        return runtime;
    }

    /// <summary>在命令流中定位某个 <see cref="Text"/> 节点的纹理命令（原点吸附后与节点左上角对齐）。</summary>
    private static TextureCommand FindCommand(RenderFrame frame, ViewNode node, double scale)
    {
        var tolerance = (1.0 / scale) + 0.001;
        var layout = Assert.IsType<TextLayoutResult>(node.ViewCache);
        var expectedWidth = (int)Math.Ceiling(layout.Size.Width * scale);
        var expectedHeight = (int)Math.Ceiling(layout.Size.Height * scale);
        return frame.Commands
            .OfType<TextureCommand>()
            .Single(command => command.Texture.IsAlphaMask
                && command.Texture.PixelWidth == expectedWidth
                && command.Texture.PixelHeight == expectedHeight
                && Math.Abs(command.Bounds.X - node.Frame.X) <= tolerance
                && Math.Abs(command.Bounds.Y - node.Frame.Y) <= tolerance);
    }

    private static PixelBuffer DirectReference(
        SkiaTextService textService,
        TextLayoutResult layout,
        TextureCommand command,
        Color background,
        PixelBuffer actual)
    {
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

    private static int PhysicalX(TextureCommand command, double scale) => (int)Math.Round(command.Bounds.X * scale);

    private static int PhysicalY(TextureCommand command, double scale) => (int)Math.Round(command.Bounds.Y * scale);

    private static (double Mean, int Max) RgbError(PixelBuffer actual, PixelBuffer reference, TextureCommand command, double scale)
    {
        var x0 = PhysicalX(command, scale);
        var y0 = PhysicalY(command, scale);
        double sum = 0;
        var count = 0;
        var max = 0;
        for (var y = 0; y < command.Texture.PixelHeight; y++)
        {
            for (var x = 0; x < command.Texture.PixelWidth; x++)
            {
                for (var channel = 0; channel < 3; channel++)
                {
                    var index = ((y0 + y) * actual.Stride) + ((x0 + x) * 4) + channel;
                    var difference = Math.Abs(actual.Pixels[index] - reference.Pixels[index]);
                    sum += difference;
                    count++;
                    max = Math.Max(max, difference);
                }
            }
        }

        return (sum / count, max);
    }

    private static Rect? InkBounds(PixelBuffer buffer, Color background, TextureCommand command, double scale)
    {
        var x0 = PhysicalX(command, scale);
        var y0 = PhysicalY(command, scale);
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (var y = 0; y < command.Texture.PixelHeight; y++)
        {
            for (var x = 0; x < command.Texture.PixelWidth; x++)
            {
                var index = ((y0 + y) * buffer.Stride) + ((x0 + x) * 4);
                if (Math.Abs(buffer.Pixels[index] - background.R) > 2
                    || Math.Abs(buffer.Pixels[index + 1] - background.G) > 2
                    || Math.Abs(buffer.Pixels[index + 2] - background.B) > 2)
                {
                    minX = Math.Min(minX, x);
                    minY = Math.Min(minY, y);
                    maxX = Math.Max(maxX, x);
                    maxY = Math.Max(maxY, y);
                }
            }
        }

        return maxX < 0 ? null : new Rect(minX, minY, (maxX - minX) + 1, (maxY - minY) + 1);
    }

    private static bool HasInk(PixelBuffer reference, Color background, TextureCommand command, double scale)
        => InkBounds(reference, background, command, scale).HasValue;

    private static bool IsBackground(PixelBuffer buffer, int x, int y, Color background, int tolerance)
    {
        var index = (y * buffer.Stride) + (x * 4);
        return Math.Abs(buffer.Pixels[index] - background.R) <= tolerance
            && Math.Abs(buffer.Pixels[index + 1] - background.G) <= tolerance
            && Math.Abs(buffer.Pixels[index + 2] - background.B) <= tolerance;
    }

    private static void BringInnerIntoView(UiRuntime runtime, ViewNode outer, ViewNode inner)
    {
        var point = new Point(outer.Frame.X + 8, outer.Frame.Y + 6);
        for (var i = 0; i < 400 && !FullyInside(inner.Frame, outer.Frame); i++)
        {
            runtime.DispatchWheel(new WheelEvent(point, 0, -1, KeyModifiers.None, WheelDeltaMode.Steps));
            runtime.ProcessPendingWork();
        }

        Assert.True(FullyInside(inner.Frame, outer.Frame), $"外层滚动后内层视口应完全可见：{inner.Frame} / {outer.Frame}。");

        // 再向上多滚一点，保证内层视口下边缘与整页视口底部之间留出可读取的物理像素带。
        for (var i = 0; i < 60 && inner.Frame.Bottom > outer.Frame.Bottom - 24; i++)
        {
            runtime.DispatchWheel(new WheelEvent(point, 0, -1, KeyModifiers.None, WheelDeltaMode.Steps));
            runtime.ProcessPendingWork();
        }
    }

    private static bool FullyInside(Rect target, Rect clip)
        => !target.IsEmpty
            && target.X >= clip.X - 0.001
            && target.Y >= clip.Y - 0.001
            && target.Right <= clip.Right + 0.001
            && target.Bottom <= clip.Bottom + 0.001;

    private static bool SameRect(Rect a, Rect b)
        => Math.Abs(a.X - b.X) < 0.001
            && Math.Abs(a.Y - b.Y) < 0.001
            && Math.Abs(a.Width - b.Width) < 0.001
            && Math.Abs(a.Height - b.Height) < 0.001;
}
