using CSharp.UI;
using CSharp.UI.Skia;
using CSharp.UI.Testing;
using Xunit;

namespace CSharp.UI.OpenTK.Tests;

/// <summary>
/// TE-06 图形回归：真实 SkiaSharp 排版 + 真实 OpenGL framebuffer，多行 TextEditor 的
/// 逐行预编辑下划线（不 union 填空）、组合 caret、逐行选区 marker、已发布几何与真实像素。
/// </summary>
/// <remarks>
/// 语义与 <see cref="PreeditRenderingGpuTests"/> 同型：颜色用主题家族区分字形墨迹/装饰/选区；
/// 所有采样坐标由同一帧排版结果与绘制命令换算到物理像素。断言的是<b>几何与可见性不变量</b>，
/// 不是字形位图字节或特定 RGB 值（字体渲染随平台微差属环境边界）。
/// </remarks>
[Collection(GraphicsCollection.Name)]
[Trait("Category", "Graphics")]
public sealed class TextEditorPreeditRenderingGpuTests
{
    private const double Epsilon = 1e-6;

    private readonly GlContextFixture _fixture;

    public TextEditorPreeditRenderingGpuTests(GlContextFixture fixture) => _fixture = fixture;

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void 跨行预编辑在真实GL下逐行绘制下划线且不填行间(double scale)
    {
        using var scope = _fixture.CreateScope();
        using var textService = new SkiaTextService();
        var service = new FakeTextInputService();
        // 窄宽度强制 "NihaoShijie" 预编辑折行；committed "ab" 让前缀仍占上行。
        var editor = new TextEditor(new Binding<string>(() => "ab", _ => { }), null)
        {
            Width = 110,
            MinLines = 4,
        };
        using var runtime = Create(scope, textService, service,
            new VStack(editor).Align(Alignment.Start), new Size(320, 220), scale);
        var node = FindEditor(runtime);
        runtime.SetFocus(node);
        runtime.ProcessPendingWork();
        runtime.BuildFrame();

        // 预编辑足够长以软折行；单段下划线。
        var preedit = "NihaoShijieDada";
        StartComposition(service, node, new TextRange(2, 2), preedit, preedit.Length, [new TextRange(0, preedit.Length)]);
        runtime.ProcessPendingWork();
        var frame = runtime.BuildFrame();
        var commands = frame.Commands;

        var state = State(node);
        var display = Client(node).DisplayString;
        var layout = LayoutEditing(textService, display, InnerWidth(node), scale);
        Assert.True(layout.Lines.Count >= 2, $"scale={scale}：预编辑应折成至少两行。");

        var texture = Assert.Single(commands.OfType<TextureCommand>());
        Assert.Equal((int)Math.Ceiling(layout.Size.Width * scale), texture.Texture.PixelWidth);
        Assert.Equal((int)Math.Ceiling(layout.Size.Height * scale), texture.Texture.PixelHeight);
        // 纹理原点在整数物理像素网格上。
        Assert.Equal(0, Math.IEEERemainder(texture.Bounds.X * scale, 1), 6);
        Assert.Equal(0, Math.IEEERemainder(texture.Bounds.Y * scale, 1), 6);

        // 单段跨行预编辑：Emit 必须给折行后的两行各画一条下划线（逐行 GetRangeRects），
        // 不是横跨内容区宽的单一 union 矩形。
        var underlines = commands.OfType<RectangleCommand>()
            .Where(c => (c.Fill == Theme.Light.Accent || c.Fill == Theme.Light.MutedForeground) && c.Bounds.Height <= 2)
            .ToList();
        Assert.True(underlines.Count >= 2, $"scale={scale}：跨行单段应产 >=2 条逐行下划线，实际 {underlines.Count}。");
        var distinctRows = underlines.Select(u => u.Bounds.Y).Distinct().Count();
        Assert.True(distinctRows >= 2, "跨行下划线必须分布在至少两个不同行 Y 上。");

        // 每条下划线都与显示串排版该行该段的逐行边界一致（宽度不越过该行段界）。
        // 单段全选时全段都是 active：每条下划线 Y = 该行段界 Bottom - 厚度(2)。
        var expectedRects = layout.GetRangeRects(2, 2 + preedit.Length)
            .Where(r => r.Width > 0)
            .OrderBy(r => r.Y)
            .ToList();
        var sortedUnderlines = underlines.OrderBy(u => u.Bounds.Y).ToList();
        Assert.Equal(expectedRects.Count, sortedUnderlines.Count);
        foreach (var (expected, actual) in expectedRects.Zip(sortedUnderlines))
        {
            var thickness = actual.Fill == Theme.Light.Accent ? 2.0 : 1.0;
            Assert.Equal(texture.Bounds.X + expected.X, actual.Bounds.X, 6);
            Assert.Equal(texture.Bounds.Y + expected.Bottom - thickness, actual.Bounds.Y, 6);
            Assert.Equal(expected.Width, actual.Bounds.Width, 6);
        }

        scope.Render(frame);
        var pixels = scope.Surface.ReadPixels();

        // 两行各自的下划线区有真实强调/次要色墨迹。
        foreach (var underline in underlines)
        {
            AssertClosest(pixels, scale, underline.Bounds,
                underline.Fill == Theme.Light.Accent ? Theme.Light.Accent : Theme.Light.MutedForeground,
                200, "逐行预编辑下划线");
        }

        // 行间空隙（下划线矩形之间的行间距带）不得被 union 填色：
        // 下边界之下、下一行下划线之上的窄带应保持输入框表面色（无装饰填充）。
        var sorted = underlines.OrderBy(u => u.Bounds.Y).ToList();
        if (sorted.Count >= 2 && sorted[1].Bounds.Y - sorted[0].Bounds.Bottom > 2)
        {
            var gapY = sorted[0].Bounds.Bottom + ((sorted[1].Bounds.Y - sorted[0].Bounds.Bottom) / 2);
            var sample = Sample(pixels, scale, sorted[0].Bounds.X + 2, gapY);
            AssertColor.Equal(Theme.Light.Surface, sample);
        }
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void 多行选区在真实GL下逐行marker且不跨行填色(double scale)
    {
        using var scope = _fixture.CreateScope();
        using var textService = new SkiaTextService();
        var service = new FakeTextInputService();
        var editor = new TextEditor(new Binding<string>(() => "ab\ncd\nef", _ => { }), null)
        {
            Width = 200,
            MinLines = 4,
        };
        using var runtime = Create(scope, textService, service,
            new VStack(editor).Align(Alignment.Start), new Size(400, 240), scale);
        var node = FindEditor(runtime);
        runtime.SetFocus(node);
        runtime.ProcessPendingWork();
        runtime.BuildFrame();

        var state = State(node);
        state.Anchor = 1;      // 行 0 中
        state.Caret = 7;       // 行 2 中（"ab\ncd\nef"：行界 0..3/4..7/8..10）
        runtime.InvalidateRender();
        var frame = runtime.BuildFrame();
        var commands = frame.Commands;

        var layout = LayoutEditing(textService, "ab\ncd\nef", InnerWidth(node), scale);
        var texture = Assert.Single(commands.OfType<TextureCommand>());

        // 选区逐行矩形：行0 部分 + 行1 整行（含 \n marker）+ 行2 部分。
        // SelectionLineEndMarkerWidth 把零宽行末 marker 画成可见窄块。
        var selectionRects = commands.OfType<RectangleCommand>()
            .Where(c => c.Fill == Theme.Light.SelectionBackground)
            .ToList();
        Assert.True(selectionRects.Count >= 3, $"跨三行选区应产 >=3 个逐行矩形，实际 {selectionRects.Count}。");

        // 逐行 Y 与排版行一致；行1 行末 marker 紧跟该行内容右侧。
        var lineYs = selectionRects.Select(r => r.Bounds.Y).Distinct().OrderBy(y => y).ToList();
        Assert.Equal(3, lineYs.Count);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(texture.Bounds.Y + layout.Lines[i].Bounds.Y, lineYs[i], 6);
        }

        scope.Render(frame);
        var pixels = scope.Surface.ReadPixels();
        foreach (var rect in selectionRects)
        {
            AssertClosest(pixels, scale, rect.Bounds, Theme.Light.SelectionBackground, 200, "选区矩形");
        }
    }

    [Fact]
    public void 已发布caret几何与真实GL绘制像素一致()
    {
        using var scope = _fixture.CreateScope();
        using var textService = new SkiaTextService();
        var service = new FakeTextInputService();
        var editor = new TextEditor(new Binding<string>(() => "hello\nworld", _ => { }), null)
        {
            Width = 200,
            MinLines = 4,
        };
        using var runtime = Create(scope, textService, service,
            new VStack(editor).Align(Alignment.Start), new Size(400, 240), 1.5);
        var node = FindEditor(runtime);
        runtime.SetFocus(node);
        runtime.ProcessPendingWork();
        runtime.BuildFrame();

        // caret 移到第二行行中（"world" 的 'r' 与 'l' 之间）。
        var state = State(node);
        state.Caret = 9;
        state.Anchor = 9;
        state.CaretAffinity = TextAffinity.Unspecified;
        runtime.InvalidateRender();
        runtime.ProcessPendingWork();
        var frame = runtime.BuildFrame();

        // 已发布几何：caret 矩形必须等于真实排版 + Emit 原点（顶部对齐），不能是居中原点。
        var geometry = service.LastGeometry;
        Assert.True(geometry.HasCaret);

        var layout = LayoutEditing(textService, "hello\nworld", InnerWidth(node), 1.5);
        var texture = Assert.Single(frame.Commands.OfType<TextureCommand>());
        var caretLayout = layout.GetCaretBounds(9, state.CaretAffinity, 1.5);
        AssertRect(
            new Rect(texture.Bounds.X + caretLayout.X, texture.Bounds.Y + caretLayout.Y, caretLayout.Width, caretLayout.Height),
            geometry.CaretBounds);

        scope.Render(frame);
        var pixels = scope.Surface.ReadPixels();
        AssertClosest(pixels, 1.5, geometry.CaretBounds, Theme.Light.Foreground, 60, "已发布 caret 墨迹");
    }

    [Fact]
    public void 编辑器宽度resize重排排版且缓存按wrap宽分键()
    {
        using var scope = _fixture.CreateScope();
        using var textService = new SkiaTextService();
        var service = new FakeTextInputService();
        var text = "alpha beta gamma delta epsilon zeta";
        var binding = new MutableText(text);
        var host = new WidthHost(binding.Binding);
        using var runtime = Create(scope, textService, service,
            new VStack(host).Align(Alignment.Start), new Size(500, 260), 1.0);
        var node = FindEditor(runtime);
        runtime.SetFocus(node);
        runtime.ProcessPendingWork();
        var narrowFrame = runtime.BuildFrame();

        var narrowLayout = LayoutEditing(textService, text, InnerWidth(node), 1.0);
        var narrowTexture = Assert.Single(narrowFrame.Commands.OfType<TextureCommand>());
        Assert.Equal((int)Math.Ceiling(narrowLayout.Size.Width * 1.0), narrowTexture.Texture.PixelWidth);

        // 状态驱动同节点重建：宽度变化重排，纹理像素尺寸跟随新 wrap 宽——编辑排版缓存键含 wrap 宽。
        host.SetWidth(320);
        runtime.ProcessPendingWork();
        var wideNode = FindEditor(runtime);
        Assert.Same(node, wideNode);
        var wideFrame = runtime.BuildFrame();
        var wideLayout = LayoutEditing(textService, text, InnerWidth(wideNode), 1.0);
        var wideTexture = Assert.Single(wideFrame.Commands.OfType<TextureCommand>());

        Assert.True(wideLayout.Size.Width > narrowLayout.Size.Width, "加宽后排版行宽应增大。");
        Assert.Equal((int)Math.Ceiling(wideLayout.Size.Width * 1.0), wideTexture.Texture.PixelWidth);
        Assert.NotEqual(narrowTexture.Texture.PixelWidth, wideTexture.Texture.PixelWidth);
        // 行数应减少（更宽的 wrap 更少折行）。
        Assert.True(wideLayout.Lines.Count <= narrowLayout.Lines.Count);
    }

    /// <summary>状态驱动宽度重建的宿主；同节点同绑定仅换 init 宽度。</summary>
    private sealed class WidthHost : Component
    {
        private readonly Binding<string> _binding;
        private State<int>? _tick;

        public WidthHost(Binding<string> binding) => _binding = binding;

        public void SetWidth(int _) => _tick!.Value++;

        protected override View Build()
        {
            _tick = UseState("tick", 0);
            var width = _tick.Value == 0 ? 140 : 320;
            return new TextEditor(_binding, null) { Width = width, MinLines = 4 };
        }
    }

    [Fact]
    public void 嵌套裁剪把多行预编辑与装饰一起截断且发布Unavailable()
    {
        using var scope = _fixture.CreateScope();
        using var textService = new SkiaTextService();
        var service = new FakeTextInputService();
        var editor = new TextEditor(new Binding<string>(() => "ab", _ => { }), null)
        {
            Width = 200,
            MinLines = 6,
        };
        var clipped = new ClipView(new VStack(new Spacer(4), editor)).Frame(height: 34);
        using var runtime = Create(scope, textService, service,
            new VStack(clipped), new Size(400, 200), 1.5);
        var node = FindEditor(runtime);
        var clipNode = runtime.Root.FindDescendant<ClipView>()
            ?? throw new InvalidOperationException("未找到 ClipView。");
        runtime.SetFocus(node);
        runtime.ProcessPendingWork();
        runtime.BuildFrame();

        StartComposition(service, node, new TextRange(2, 2), "NihaoShijie", 6, [new TextRange(0, 11)]);
        runtime.ProcessPendingWork();
        var frame = runtime.BuildFrame();

        scope.Render(frame);
        var pixels = scope.Surface.ReadPixels();

        // 裁剪下边界之下：编辑器范围不得有装饰强调色或字形墨迹。
        var boundary = (int)Math.Ceiling(clipNode.Frame.Bottom * 1.5);
        var left = (int)Math.Ceiling(node.Frame.Left * 1.5) + 1;
        var right = (int)Math.Floor(node.Frame.Right * 1.5) - 2;
        var bottom = Math.Min(pixels.Height - 1, (int)Math.Floor(node.Frame.Bottom * 1.5));
        for (var y = boundary; y <= bottom; y++)
        {
            for (var x = left; x <= right; x++)
            {
                var color = pixels.GetColor(x, y);
                Assert.False(IsAccent(color), $"裁剪边界以下 ({x},{y}) 不应有强调色 {color}。");
                Assert.False(IsInk(color), $"裁剪边界以下 ({x},{y}) 不应有墨迹 {color}。");
            }
        }
    }

    [Fact]
    public void caret完全裁出时发布Unavailable不留旧锚点()
    {
        using var scope = _fixture.CreateScope();
        using var textService = new SkiaTextService();
        var service = new FakeTextInputService();
        var lines = string.Join("\n", Enumerable.Range(0, 40).Select(i => $"line-{i:00}"));
        var editor = new TextEditor(new Binding<string>(() => lines, _ => { }), null)
        {
            Width = 200,
            MinLines = 4,
        };
        var scroll = new ScrollView(new VStack(editor, new Spacer(400)));
        var clipped = new ClipView(scroll).Frame(height: 90);
        using var runtime = Create(scope, textService, service,
            new VStack(clipped), new Size(400, 200), 1.0);
        var node = FindEditor(runtime);
        var scrollNode = runtime.Root.FindDescendant<ScrollView>()
            ?? throw new InvalidOperationException("未找到 ScrollView。");
        runtime.SetFocus(node);
        runtime.ProcessPendingWork();
        runtime.BuildFrame();
        Assert.True(service.LastGeometry.HasCaret, "可见时应有 caret 几何。");

        // 把编辑器滚到裁剪区之外：caret 完全裁出 → Unavailable，不留旧锚点。
        scrollNode.ScrollOffsetY = 400;
        scrollNode.InvalidateLayout();
        runtime.ProcessPendingWork();
        runtime.BuildFrame();
        Assert.False(service.LastGeometry.HasCaret);
    }

    // ------------------------------------------------------------------ helpers

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

    private static ViewNode FindEditor(UiRuntime runtime)
        => runtime.Root.FindDescendant<TextEditor>()
           ?? throw new InvalidOperationException("未找到 TextEditor。");

    private static double InnerWidth(ViewNode node)
    {
        var editor = Assert.IsType<TextEditor>(node.View);
        // 与产品 InnerRect 同步：frame - padding - 右侧滚动条槽宽（persistent gutter 12）。
        return node.Frame.Deflate(new Thickness(editor.HorizontalPadding, editor.VerticalPadding)).Width - ScrollbarMetrics.Thickness;
    }

    private static TextLayoutResult LayoutEditing(SkiaTextService textService, string text, double innerWidth, double pixelScale)
        => textService.Layout(TextPainter.CreateRequest(
            text, FontDescription.Default, innerWidth, pixelScale,
            wrapping: TextWrapping.Wrap, maxLines: int.MaxValue, lineHeight: 0) with { Editing = true });

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

    private static TextEditorState State(ViewNode node) => Assert.IsType<TextEditorState>(node.InteractionState);

    private static TextCompositionClient Client(ViewNode node)
        => State(node).Composing ?? throw new InvalidOperationException("该节点没有组合客户端。");

    private static Color Sample(PixelBuffer pixels, double scale, double logicalX, double logicalY)
    {
        var x = Math.Clamp((int)Math.Floor(logicalX * scale), 0, pixels.Width - 1);
        var y = Math.Clamp((int)Math.Floor(logicalY * scale), 0, pixels.Height - 1);
        return pixels.GetColor(x, y);
    }

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

    private sealed class MutableText
    {
        public MutableText(string initial)
        {
            Text = initial;
            Binding = new Binding<string>(() => Text, v => Text = v);
        }

        public string Text { get; set; }

        public Binding<string> Binding { get; }
    }

    /// <summary>记录服务端几何并把组合事件投递给活动客户端（与 PreeditRenderingGpuTests 同型）。</summary>
    private sealed class FakeTextInputService : ITextInputService
    {
        public TextInputCapabilities Capabilities => TextInputCapabilities.Full;
        public ITextInputClient? ActiveClient { get; private set; }
        public TextInputGeometry LastGeometry { get; private set; } = TextInputGeometry.Unavailable;

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

        public bool PublishGeometry(TextInputGeometry geometry)
        {
            LastGeometry = geometry;
            return true;
        }

        public TextInputApplyResult Apply(params CompositionEvent[] events)
            => ActiveClient?.ApplyComposition(events)
               ?? TextInputApplyResult.Rejected(TextSnapshot.Empty, "没有活动客户端。");
    }
}
