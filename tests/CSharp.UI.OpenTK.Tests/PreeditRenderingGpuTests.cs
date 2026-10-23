using CSharp.UI;
using CSharp.UI.Skia;
using CSharp.UI.Testing;
using Xunit;

namespace CSharp.UI.OpenTK.Tests;

/// <summary>
/// TI-06 图形回归：真实 SkiaSharp 排版 + 真实 OpenGL framebuffer，逐像素断言预编辑墨迹、下划线、
/// 活动段与组合 caret 的相对位置，而不是只检查绘制命令存在。
/// </summary>
/// <remarks>
/// 颜色语义：正文使用主题前景色（深色），预编辑下划线/组合 caret 使用强调色（蓝色），
/// 因此可以区分字形墨迹与装饰。所有采样坐标都由同一帧的排版结果与绘制命令换算到物理像素。
/// </remarks>
[Collection(GraphicsCollection.Name)]
[Trait("Category", "Graphics")]
public sealed class PreeditRenderingGpuTests
{
    private const double Epsilon = 1e-6;

    private readonly GlContextFixture _fixture;

    /// <summary>注入共享 GL 上下文夹具。</summary>
    public PreeditRenderingGpuTests(GlContextFixture fixture) => _fixture = fixture;

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void 真实GL下预编辑墨迹下划线与组合caret保持相对位置(double scale)
    {
        using var scope = _fixture.CreateScope();
        using var textService = new SkiaTextService();
        var service = new FakeTextInputService();
        var field = new TextField(new Binding<string>(() => "ab", _ => { })) { Width = 320 };
        using var runtime = Create(scope, textService, service, new VStack(field).Align(Alignment.Start), new Size(480, 120), scale);
        var node = runtime.Root.FindDescendant<TextField>()
            ?? throw new InvalidOperationException("未找到 TextField。");
        runtime.SetFocus(node);
        runtime.ProcessPendingWork();
        runtime.BuildFrame();

        StartComposition(service, node, new TextRange(2, 2), "ni", 1, [new TextRange(0, 1), new TextRange(1, 2)]);
        runtime.ProcessPendingWork();
        var frame = runtime.BuildFrame();
        var commands = frame.Commands;
        var layout = Layout(textService, "abni", scale);
        var texture = Assert.Single(commands.OfType<TextureCommand>());
        var inactive = Assert.Single(commands.OfType<RectangleCommand>(), c => c.Fill == Theme.Light.MutedForeground && c.Bounds.Height == 1);
        var active = Assert.Single(commands.OfType<RectangleCommand>(), c => c.Fill == Theme.Light.Accent && c.Bounds.Height == 2);
        var caret = Assert.Single(commands.OfType<RectangleCommand>(), c => c.Fill == Theme.Light.Accent && c.Bounds.Height > 2);

        // R16/R17：显示串纹理 1 纹素 = 1 物理像素，原点在整数物理像素上。
        Assert.Equal((int)Math.Ceiling(layout.Size.Width * scale), texture.Texture.PixelWidth);
        Assert.Equal((int)Math.Ceiling(layout.Size.Height * scale), texture.Texture.PixelHeight);
        Assert.Equal(0, Math.IEEERemainder(texture.Bounds.X * scale, 1), 6);
        Assert.Equal(0, Math.IEEERemainder(texture.Bounds.Y * scale, 1), 6);

        // 下划线按预编辑分段展开：第一段次要色 1px，第二段活动色 2px；caret 位于第二段起点。
        var first = layout.GetRangeBounds(2, 3);
        var second = layout.GetRangeBounds(3, 4);
        AssertRect(new Rect(texture.Bounds.X + first.X, texture.Bounds.Y + first.Bottom - 1, first.Width, 1), inactive.Bounds);
        AssertRect(new Rect(texture.Bounds.X + second.X, texture.Bounds.Y + second.Bottom - 2, second.Width, 2), active.Bounds);
        var caretLayout = layout.GetCaretBounds(3, 1.5);
        AssertRect(new Rect(texture.Bounds.X + caretLayout.X, texture.Bounds.Y + caretLayout.Y, caretLayout.Width, caretLayout.Height), caret.Bounds);
        Assert.Equal(active.Bounds.Bottom, inactive.Bounds.Bottom, 6);
        Assert.Equal(active.Bounds.Bottom, caret.Bounds.Bottom, 6);

        scope.Render(frame);
        var pixels = scope.Surface.ReadPixels();

        // 下划线物理像素确实是活动/次要色，且横跨预编辑对应范围。
        // 1px 逻辑下划线落在小数物理坐标上时会被拆到相邻像素（覆盖率不足 100%），
        // 因此取矩形附近最接近目标颜色的真实像素，而不是假设某个像素中心落在完全覆盖区。
        AssertClosest(pixels, scale, inactive.Bounds, Theme.Light.MutedForeground, 200, "非活动下划线");
        AssertClosest(pixels, scale, active.Bounds, Theme.Light.Accent, 40, "活动下划线起点");
        AssertClosest(pixels, scale, new Rect(active.Bounds.Right - 2, active.Bounds.Y, 2, active.Bounds.Height), Theme.Light.Accent, 40, "活动下划线终点");

        // 组合 caret 是独立的高竖线：位于下划线上方、与文本行同高，颜色为强调色。
        AssertClosest(pixels, scale, new Rect(caret.Bounds.X, caret.Bounds.Y, caret.Bounds.Width, caret.Bounds.Height * 0.5), Theme.Light.Accent, 40, "组合 caret");
        Assert.True(caret.Bounds.Bottom <= active.Bounds.Bottom + Epsilon);
        Assert.True(caret.Bounds.Y < active.Bounds.Y);

        // 预编辑字形区域有真实墨迹（不是只有命令），且墨迹在下划线之上。
        var preeditInk = ScanInk(pixels, scale, texture.Bounds.X + layout.GetCaretX(2), texture.Bounds.Y, layout.GetCaretX(4) - layout.GetCaretX(2), active.Bounds.Y - texture.Bounds.Y);
        Assert.True(preeditInk > 0, $"{scale}：预编辑区域应有字形墨迹。");
        var prefixInk = ScanInk(pixels, scale, texture.Bounds.X, texture.Bounds.Y, layout.GetCaretX(2) - layout.GetCaretX(0), active.Bounds.Y - texture.Bounds.Y);
        Assert.True(prefixInk > 0, $"{scale}：已提交前缀应有字形墨迹。");

        // 已提交前缀的同一行没有下划线：预编辑起点左侧保持输入框表面色。
        var prefixSample = Sample(pixels, scale, texture.Bounds.X + 2, active.Bounds.Y + (active.Bounds.Height / 2));
        AssertColor.Equal(Theme.Light.Surface, prefixSample);
    }

    [Theory]
    [InlineData(320)]
    [InlineData(480)]
    [InlineData(768)]
    [InlineData(1024)]
    public void 长预编辑在真实GL中按内容区裁剪且组合caret贴边可见(int width)
    {
        using var scope = _fixture.CreateScope();
        using var textService = new SkiaTextService();
        var service = new FakeTextInputService();
        var field = new TextField(new Binding<string>(() => "ab", _ => { })) { Width = Math.Min(width - 40, 260) };
        using var runtime = Create(
            scope,
            textService,
            service,
            new VStack(field).Align(Alignment.Start),
            new Size(width, 120),
            1.5);
        var node = runtime.Root.FindDescendant<TextField>()
            ?? throw new InvalidOperationException("未找到 TextField。");
        runtime.SetFocus(node);
        runtime.ProcessPendingWork();
        runtime.BuildFrame();

        var preedit = new string('长', 40);
        StartComposition(service, node, new TextRange(2, 2), preedit, preedit.Length, null);
        runtime.ProcessPendingWork();
        var frame = runtime.BuildFrame();
        var commands = frame.Commands;
        var clip = Assert.Single(commands.OfType<PushClipCommand>()).Bounds;
        var texture = Assert.Single(commands.OfType<TextureCommand>());
        var caret = Assert.Single(commands.OfType<RectangleCommand>(), c => c.Fill == Theme.Light.Accent && c.Bounds.Height > 2);
        var display = Client(node).DisplayString;
        var layout = Layout(textService, display, 1.5);
        var caretX = layout.GetCaretX(display.Length);

        Assert.True(caret.Bounds.Right <= clip.Right + Epsilon);
        Assert.Equal(texture.Bounds.X + caretX, caret.Bounds.X, 6);
        Assert.True(texture.Bounds.X + caretX + caret.Bounds.Width <= clip.Right + Epsilon, "真实排版 caret 右边缘应可见。");
        Assert.InRange(caret.Bounds.X, clip.Left, clip.Right - caret.Bounds.Width + Epsilon);
        Assert.True(State(node).ScrollX > 0);

        scope.Render(frame);
        var pixels = scope.Surface.ReadPixels();

        // 组合 caret 在裁剪区内真实可见。
        AssertClosest(pixels, 1.5, new Rect(caret.Bounds.X, caret.Bounds.Y, caret.Bounds.Width, caret.Bounds.Height * 0.5), Theme.Light.Accent, 40, "长预编辑组合 caret");

        // 内容区右侧（仍在输入框背景内）不得出现预编辑墨迹或强调色：GPU 裁剪真实生效。
        // 跳过圆角/焦点描边覆盖的上下边缘，只看文本行所在的中段。
        var bandLeft = (int)Math.Ceiling(clip.Right * 1.5) + 1;
        var bandRight = (int)Math.Floor((node.Frame.Right - 4) * 1.5) - 1;
        var bandTop = (int)Math.Ceiling((clip.Top + (clip.Height * 0.25)) * 1.5);
        var bandBottom = (int)Math.Floor((clip.Bottom - (clip.Height * 0.25)) * 1.5);
        for (var y = bandTop; y <= bandBottom; y++)
        {
            for (var x = bandLeft; x <= bandRight; x++)
            {
                var color = pixels.GetColor(x, y);
                Assert.False(IsAccent(color), $"{width}：裁剪区右侧 ({x},{y}) 不应出现强调色 {color}。");
                Assert.False(IsInk(color), $"{width}：裁剪区右侧 ({x},{y}) 不应出现文字墨迹 {color}。");
            }
        }

        // 裁剪区内仍有可见墨迹（不是整段被丢弃）。
        Assert.True(
            ScanInk(pixels, 1.5, clip.Left, clip.Top, clip.Width, clip.Height) > 0,
            $"{width}：裁剪区内应保留长预编辑的可见墨迹。");
    }

    [Fact]
    public void 嵌套裁剪在真实GL中同时截断预编辑墨迹与下划线()
    {
        using var scope = _fixture.CreateScope();
        using var textService = new SkiaTextService();
        var service = new FakeTextInputService();
        var field = new TextField(new Binding<string>(() => "ab", _ => { })) { Width = 260 };
        var clipped = new ClipView(new VStack(new Spacer(5), field)).Frame(height: 30);
        using var runtime = Create(
            scope,
            textService,
            service,
            new VStack(clipped),
            new Size(400, 120),
            1.5);
        var node = runtime.Root.FindDescendant<TextField>()
            ?? throw new InvalidOperationException("未找到 TextField。");
        var clipNode = runtime.Root.FindDescendant<ClipView>()
            ?? throw new InvalidOperationException("未找到 ClipView。");
        runtime.SetFocus(node);
        runtime.ProcessPendingWork();
        runtime.BuildFrame();

        StartComposition(service, node, new TextRange(2, 2), "ni", 2, [new TextRange(0, 2)]);
        runtime.ProcessPendingWork();
        var frame = runtime.BuildFrame();
        var caret = Assert.Single(frame.Commands.OfType<RectangleCommand>(), c => c.Fill == Theme.Light.Accent && c.Bounds.Height > 2);
        var underline = Assert.Single(frame.Commands.OfType<RectangleCommand>(), c => c.Fill == Theme.Light.Accent && c.Bounds.Height == 2);
        Assert.True(caret.Bounds.Top < clipNode.Frame.Bottom, "组合 caret 应至少部分位于嵌套裁剪区内。");
        Assert.True(underline.Bounds.Top < clipNode.Frame.Bottom, "下划线应至少部分位于嵌套裁剪区内。");

        scope.Render(frame);
        var pixels = scope.Surface.ReadPixels();

        // 裁剪下边界以下：输入框范围内不得出现任何预编辑墨迹或强调色。
        var boundary = (int)Math.Ceiling(clipNode.Frame.Bottom * 1.5);
        var left = (int)Math.Ceiling(node.Frame.Left * 1.5) + 1;
        var right = (int)Math.Floor(node.Frame.Right * 1.5) - 2;
        var bottom = Math.Min(pixels.Height - 1, (int)Math.Floor(node.Frame.Bottom * 1.5));
        for (var y = boundary; y <= bottom; y++)
        {
            for (var x = left; x <= right; x++)
            {
                var color = pixels.GetColor(x, y);
                Assert.False(IsAccent(color), $"嵌套裁剪边界以下 ({x},{y}) 不应出现强调色 {color}。");
                Assert.False(IsInk(color), $"嵌套裁剪边界以下 ({x},{y}) 不应出现预编辑墨迹 {color}。");
            }
        }

        // 边界以上仍可见 caret/下划线的强调色与字形墨迹。
        Assert.True(
            ScanAccent(pixels, 1.5, clipNode.Frame.Left, clipNode.Frame.Top, clipNode.Frame.Width, clipNode.Frame.Bottom - clipNode.Frame.Top) > 0,
            "嵌套裁剪区内应有强调色装饰。");
        Assert.True(
            ScanInk(pixels, 1.5, clipNode.Frame.Left, clipNode.Frame.Top, clipNode.Frame.Width, clipNode.Frame.Bottom - clipNode.Frame.Top) > 0,
            "嵌套裁剪区内应有字形墨迹。");
    }

    private UiRuntime Create(
        GlTestScope scope,
        SkiaTextService textService,
        FakeTextInputService service,
        View root,
        Size viewport,
        double pixelScale)
    {
        var runtime = new UiRuntime(root, new UiRuntimeServices
        {
            Dispatcher = new HeadlessUiDispatcher(),
            TextService = textService,
            TextureService = scope.Textures,
            Clipboard = new StubClipboardService(),
            Clock = new TestUiClock(),
            TextInputService = service,
        });

        runtime.SetEnvironment(new EnvironmentValues { Theme = Theme.Light, Font = FontDescription.Default });
        runtime.SetViewport(viewport, pixelScale);
        runtime.Start();
        runtime.ProcessPendingWork();
        runtime.BuildFrame();
        return runtime;
    }

    private static TextLayoutResult Layout(SkiaTextService textService, string text, double pixelScale)
        => textService.Layout(TextPainter.CreateRequest(text, FontDescription.Default, double.PositiveInfinity, pixelScale));

    private static Color Sample(PixelBuffer pixels, double scale, double logicalX, double logicalY)
    {
        var x = Math.Clamp((int)Math.Floor(logicalX * scale), 0, pixels.Width - 1);
        var y = Math.Clamp((int)Math.Floor(logicalY * scale), 0, pixels.Height - 1);
        return pixels.GetColor(x, y);
    }

    /// <summary>
    /// 在逻辑矩形内选择“像素中心落在矩形内”的最接近期望色的真实像素并断言颜色家族。
    /// 1px 逻辑线在分数物理坐标上可能被拆到相邻像素，不能用单一中心采样假设 100% 覆盖。
    /// </summary>
    private static void AssertClosest(PixelBuffer pixels, double scale, Rect bounds, Color expected, int maxDistance, string label)
    {
        var left = Math.Clamp((int)Math.Ceiling((bounds.Left * scale) - 0.5), 0, pixels.Width - 1);
        var top = Math.Clamp((int)Math.Ceiling((bounds.Top * scale) - 0.5), 0, pixels.Height - 1);
        var right = Math.Clamp((int)Math.Floor((bounds.Right * scale) - 0.5), 0, pixels.Width - 1);
        var bottom = Math.Clamp((int)Math.Floor((bounds.Bottom * scale) - 0.5), 0, pixels.Height - 1);
        var best = int.MaxValue;
        var bestColor = Color.Transparent;
        for (var y = top; y <= bottom; y++)
        {
            for (var x = left; x <= right; x++)
            {
                var color = pixels.GetColor(x, y);
                var distance = ColorDistance(color, expected);
                if (distance < best)
                {
                    best = distance;
                    bestColor = color;
                }
            }
        }

        Assert.True(best <= maxDistance, $"{label}：{bounds} 内最接近的像素 {bestColor} 与期望 {expected} 距离 {best} 超过 {maxDistance}。");
    }

    private static int ColorDistance(Color left, Color right)
        => Math.Abs(left.R - right.R) + Math.Abs(left.G - right.G) + Math.Abs(left.B - right.B) + Math.Abs(left.A - right.A);

    private static int ScanInk(PixelBuffer pixels, double scale, double logicalX, double logicalY, double logicalWidth, double logicalHeight)
    {
        var left = Math.Clamp((int)Math.Floor(logicalX * scale), 0, pixels.Width - 1);
        var top = Math.Clamp((int)Math.Floor(logicalY * scale), 0, pixels.Height - 1);
        var right = Math.Clamp((int)Math.Ceiling((logicalX + logicalWidth) * scale) - 1, 0, pixels.Width - 1);
        var bottom = Math.Clamp((int)Math.Ceiling((logicalY + logicalHeight) * scale) - 1, 0, pixels.Height - 1);
        var count = 0;
        for (var y = top; y <= bottom; y++)
        {
            for (var x = left; x <= right; x++)
            {
                if (IsInk(pixels.GetColor(x, y)))
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static int ScanAccent(PixelBuffer pixels, double scale, double logicalX, double logicalY, double logicalWidth, double logicalHeight)
    {
        var left = Math.Clamp((int)Math.Floor(logicalX * scale), 0, pixels.Width - 1);
        var top = Math.Clamp((int)Math.Floor(logicalY * scale), 0, pixels.Height - 1);
        var right = Math.Clamp((int)Math.Ceiling((logicalX + logicalWidth) * scale) - 1, 0, pixels.Width - 1);
        var bottom = Math.Clamp((int)Math.Ceiling((logicalY + logicalHeight) * scale) - 1, 0, pixels.Height - 1);
        var count = 0;
        for (var y = top; y <= bottom; y++)
        {
            for (var x = left; x <= right; x++)
            {
                if (IsAccent(pixels.GetColor(x, y)))
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static bool IsInk(Color color) => color.R < 160 && color.G < 160 && color.B < 160;

    private static bool IsAccent(Color color)
        => Math.Abs(color.R - Theme.Light.Accent.R) <= 12
            && Math.Abs(color.G - Theme.Light.Accent.G) <= 12
            && Math.Abs(color.B - Theme.Light.Accent.B) <= 12;

    private static void AssertRect(Rect expected, Rect actual)
    {
        Assert.Equal(expected.X, actual.X, 6);
        Assert.Equal(expected.Y, actual.Y, 6);
        Assert.Equal(expected.Width, actual.Width, 6);
        Assert.Equal(expected.Height, actual.Height, 6);
    }

    private static void StartComposition(
        FakeTextInputService service,
        ViewNode node,
        TextRange replacement,
        string preedit,
        int caret,
        IReadOnlyList<TextRange>? segments)
    {
        var client = State(node).Composing ?? throw new InvalidOperationException("该节点没有组合客户端。");
        var result = service.Apply(CompositionEvent.Start(
            client.Session.SessionId,
            0,
            0,
            0,
            replacement,
            preedit,
            TextSelection.Caret(replacement.Start),
            caret,
            segments));
        Assert.False(result.HasRejection, result.RejectionReason);
        Assert.True(result.Outcome.HasFlag(TextInputOutcome.Started), $"{result.Outcome}（{result.RejectionReason}）");
    }

    private static TextFieldState State(ViewNode node) => Assert.IsType<TextFieldState>(node.InteractionState);

    private static TextCompositionClient Client(ViewNode node)
        => State(node).Composing ?? throw new InvalidOperationException("该节点没有组合客户端。");

    /// <summary>记录服务端几何并把组合事件投递给活动客户端。</summary>
    private sealed class FakeTextInputService : ITextInputService
    {
        public TextInputCapabilities Capabilities => TextInputCapabilities.Full;

        public ITextInputClient? ActiveClient { get; private set; }

        public bool StartSession(ITextInputClient client)
        {
            ActiveClient = client;
            return true;
        }

        public bool EndSession()
        {
            ActiveClient = null;
            return true;
        }

        public TextInputApplyResult ApplyComposition(IReadOnlyList<CompositionEvent> events)
            => ActiveClient?.ApplyComposition(events)
                ?? TextInputApplyResult.Rejected(TextSnapshot.Empty, "没有活动客户端。");

        public bool RequestCancel() => ActiveClient?.CancelComposition() ?? false;

        public bool PublishGeometry(TextInputGeometry geometry) => true;

        public TextInputApplyResult Apply(params CompositionEvent[] events)
            => ActiveClient?.ApplyComposition(events)
                ?? TextInputApplyResult.Rejected(TextSnapshot.Empty, "没有活动客户端。");
    }
}
