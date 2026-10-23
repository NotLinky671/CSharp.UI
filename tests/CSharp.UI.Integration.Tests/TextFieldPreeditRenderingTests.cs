using CSharp.UI;
using CSharp.UI.Skia;
using CSharp.UI.Testing;
using Xunit;

namespace CSharp.UI.IntegrationTests;

/// <summary>
/// TI-06 集成：真实 SkiaSharp/HarfBuzz 排版驱动预编辑显示串，验证下划线/活动段/组合 caret 的相对位置、
/// 显示→文档映射、长预编辑横向滚动、响应式同节点保持与最终布局后的候选锚点几何。
/// </summary>
public class TextFieldPreeditRenderingTests : IDisposable
{
    private const double Epsilon = 1e-6;

    private readonly SkiaTextService _text = new();

    public void Dispose() => _text.Dispose();

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void 真实Skia下预编辑装饰与组合caret共享物理像素原点(double pixelScale)
    {
        var binding = new MutableText("ab");
        var service = new FakeTextInputService();
        using var runtime = Create(
            new VStack(new TextField(binding.Binding) { Width = 240 }),
            service,
            new Size(400, 120),
            pixelScale);
        var node = (runtime.Root.FindDescendant<TextField>() ?? throw new InvalidOperationException("未找到 TextField。"));
        runtime.SetFocus(node);
        runtime.ProcessPendingWork();
        runtime.BuildFrame();

        StartComposition(service, node, new TextRange(2, 2), "ni", 1, [new TextRange(0, 1), new TextRange(1, 2)]);
        runtime.ProcessPendingWork();
        var commands = runtime.BuildFrame().Commands;

        var state = State(node);
        var displayLayout = Layout("abni", pixelScale);
        var texture = Assert.Single(commands.OfType<TextureCommand>());

        // R16/R17：显示串排版按当前 DPI 栅格化，1 纹素 = 1 物理像素，原点吸附整数物理像素。
        Assert.Equal((int)Math.Ceiling(displayLayout.Size.Width * pixelScale), texture.Texture.PixelWidth);
        Assert.Equal((int)Math.Ceiling(displayLayout.Size.Height * pixelScale), texture.Texture.PixelHeight);
        Assert.Equal(0, Math.IEEERemainder(texture.Bounds.X * pixelScale, 1), 6);
        Assert.Equal(0, Math.IEEERemainder(texture.Bounds.Y * pixelScale, 1), 6);
        Assert.Equal(0, state.ScrollX);

        var first = displayLayout.GetRangeBounds(2, 3);
        var second = displayLayout.GetRangeBounds(3, 4);
        var inactive = Assert.Single(commands.OfType<RectangleCommand>(), c => c.Fill == Theme.Light.MutedForeground && c.Bounds.Height == 1);
        var active = Assert.Single(commands.OfType<RectangleCommand>(), c => c.Fill == Theme.Light.Accent && c.Bounds.Height == 2);
        var caret = Assert.Single(commands.OfType<RectangleCommand>(), c => c.Fill == Theme.Light.Accent && c.Bounds.Height > 2);

        AssertRect(new Rect(texture.Bounds.X + first.X, texture.Bounds.Y + first.Bottom - 1, first.Width, 1), inactive.Bounds);
        AssertRect(new Rect(texture.Bounds.X + second.X, texture.Bounds.Y + second.Bottom - 2, second.Width, 2), active.Bounds);
        var caretLayout = displayLayout.GetCaretBounds(3, 1.5);
        AssertRect(new Rect(texture.Bounds.X + caretLayout.X, texture.Bounds.Y + caretLayout.Y, caretLayout.Width, caretLayout.Height), caret.Bounds);

        var line = displayLayout.Lines[0];
        Assert.Equal(texture.Bounds.Y + line.Bounds.Bottom, inactive.Bounds.Bottom, 6);
        Assert.Equal(texture.Bounds.Y + line.Bounds.Bottom, active.Bounds.Bottom, 6);
        Assert.Equal(texture.Bounds.Y + line.Bounds.Bottom, caret.Bounds.Bottom, 6);
        Assert.Equal(texture.Bounds.Y, caret.Bounds.Top, 6);

        // 发布几何与真实 Skia 显示排版一致：caret 是组合 caret，组合矩形覆盖预编辑范围。
        var geometry = service.LastGeometry;
        Assert.True(geometry.HasCaret);
        Assert.True(geometry.HasComposition);
        AssertRect(caret.Bounds, geometry.CaretBounds);
        var composition = displayLayout.GetRangeBounds(2, 4);
        AssertRect(
            new Rect(texture.Bounds.X + composition.X, texture.Bounds.Y + composition.Y, composition.Width, composition.Height),
            geometry.CompositionBounds);
    }

    [Fact]
    public void 响应式断点与DPI变化保持同一组合节点且长预编辑caret始终可见()
    {
        var binding = new MutableText("ab");
        var service = new FakeTextInputService();
        var field = new TextField(binding.Binding) { Width = 200 };
        var adaptive = new AdaptiveStack(
            480,
            new VStack(field).Align(Alignment.Start),
            new Text("side").WithFont(FontDescription.Default));
        using var runtime = Create(adaptive, service, new Size(1024, 120), 1.0);

        var node = (runtime.Root.FindDescendant<TextField>() ?? throw new InvalidOperationException("未找到 TextField。"));
        runtime.SetFocus(node);
        runtime.ProcessPendingWork();
        runtime.BuildFrame();

        var preedit = new string('长', 30);
        StartComposition(service, node, new TextRange(2, 2), preedit, preedit.Length, null);
        runtime.ProcessPendingWork();

        var epoch = Client(node).Epoch;
        var checkpoints = new (double Width, double Scale)[]
        {
            (1024, 1.0), (768, 1.25), (480, 1.5), (320, 2.0), (1024, 1.0), (320, 1.0),
        };

        foreach (var (width, scale) in checkpoints)
        {
            runtime.SetViewport(new Size(width, 120), scale);
            runtime.ProcessPendingWork();
            var commands = runtime.BuildFrame().Commands;

            var current = (runtime.Root.FindDescendant<TextField>() ?? throw new InvalidOperationException("未找到 TextField。"));
            Assert.Same(node, current);
            Assert.Equal(epoch, Client(node).Epoch);
            Assert.True(Client(node).IsComposing);
            Assert.Equal(preedit, Client(node).Preedit);

            var clip = Assert.Single(commands.OfType<PushClipCommand>()).Bounds;
            var layout = Layout("ab" + preedit, scale);
            var texture = Assert.Single(
                commands.OfType<TextureCommand>(),
                c => c.Texture.PixelWidth == (int)Math.Ceiling(layout.Size.Width * scale));
            Assert.Equal((int)Math.Ceiling(layout.Size.Width * scale), texture.Texture.PixelWidth);
            Assert.Equal(0, Math.IEEERemainder(texture.Bounds.X * scale, 1), 6);
            Assert.Equal(texture.Texture.PixelWidth, texture.Bounds.Width * scale, 6);
            Assert.Equal(texture.Texture.PixelHeight, texture.Bounds.Height * scale, 6);

            var caret = Assert.Single(commands.OfType<RectangleCommand>(), c => c.Fill == Theme.Light.Accent && c.Bounds.Height > 2);
            Assert.InRange(caret.Bounds.X, clip.X, clip.Right - caret.Bounds.Width + Epsilon);
            Assert.True(caret.Bounds.Right <= clip.Right + Epsilon);
            Assert.True(texture.Bounds.X < clip.X, $"{width}@{scale}：长预编辑应横向滚动。");
            Assert.True(State(node).ScrollX > 0);
            Assert.True(service.LastGeometry.HasCaret);
            AssertRect(caret.Bounds, service.LastGeometry.CaretBounds);
        }
    }

    [Theory]
    [InlineData(1.0, 200, "")]
    [InlineData(1.25, 200, "")]
    [InlineData(1.5, 200, "")]
    [InlineData(2.0, 200, "")]
    [InlineData(1.5, 120, "")]
    [InlineData(2.0, 160, "ABCDEF")]
    [InlineData(1.5, 200, "ABCDEF")]
    public void 长预编辑末尾真实caret矩形在四DPI与窄框下完整可见(double pixelScale, int width, string initial)
    {
        var binding = new MutableText(initial);
        var service = new FakeTextInputService();
        var field = new TextField(binding.Binding) { Width = width };
        using var runtime = Create(new VStack(field).Align(Alignment.Start), service, new Size(400, 120), pixelScale);
        var node = runtime.Root.FindDescendant<TextField>()
            ?? throw new InvalidOperationException("未找到 TextField。");
        runtime.SetFocus(node);
        runtime.ProcessPendingWork();
        runtime.BuildFrame();

        var preedit = new string('W', 80);
        var start = initial.Length;
        StartComposition(service, node, new TextRange(start, start), preedit, preedit.Length, null);
        runtime.ProcessPendingWork();
        var commands = runtime.BuildFrame().Commands;

        var display = Client(node).DisplayString;
        var layout = Layout(display, pixelScale);
        var clip = Assert.Single(commands.OfType<PushClipCommand>()).Bounds;
        var originX = TextPainter.SnapToPhysicalPixel(new Point(clip.X - State(node).ScrollX, 0), pixelScale).X;
        var caretX = layout.GetCaretX(display.Length);

        // 真实排版原点 + 真实 caret 矩形（含宽度）必须完整处于内容区；不是事后夹取矢量线。
        Assert.True(originX + caretX >= clip.X - Epsilon, $"{pixelScale}@{width}：caret 左边缘应在内容区内。");
        Assert.True(originX + caretX + 1.5 <= clip.Right + Epsilon, $"{pixelScale}@{width}：caret 右边缘应在内容区内。");
        Assert.True(State(node).ScrollX > 0);

        var caret = Assert.Single(commands.OfType<RectangleCommand>(), c => c.Fill == Theme.Light.Accent && c.Bounds.Height > 2);
        Assert.Equal(originX + caretX, caret.Bounds.X, 6);
        Assert.True(service.LastGeometry.HasCaret);
        AssertRect(caret.Bounds, service.LastGeometry.CaretBounds);
        Assert.Equal(service.LastGeometry, Client(node).GetState().Geometry);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void 普通长文档End真实caret在四DPI下可见且发布一致(double pixelScale)
    {
        var binding = new MutableText(new string('W', 80));
        var service = new FakeTextInputService();
        var field = new TextField(binding.Binding) { Width = 200 };
        using var runtime = Create(new VStack(field).Align(Alignment.Start), service, new Size(400, 120), pixelScale);
        var node = runtime.Root.FindDescendant<TextField>()
            ?? throw new InvalidOperationException("未找到 TextField。");
        runtime.SetFocus(node);
        runtime.ProcessPendingWork();
        runtime.BuildFrame();

        runtime.DispatchKey(new KeyEvent(KeyEventKind.Down, UiKey.End));
        runtime.ProcessPendingWork();
        var commands = runtime.BuildFrame().Commands;

        var layout = Layout(new string('W', 80), pixelScale);
        var clip = Assert.Single(commands.OfType<PushClipCommand>()).Bounds;
        var texture = Assert.Single(commands.OfType<TextureCommand>());
        var caret = Assert.Single(commands.OfType<RectangleCommand>(), c => c.Fill == Theme.Light.Foreground && c.Bounds.Height > 2);
        var caretX = layout.GetCaretX(80);

        Assert.True(State(node).ScrollX > 0);
        Assert.True(texture.Bounds.X + caretX >= clip.X - Epsilon, $"{pixelScale}：普通 caret 左边缘应可见。");
        Assert.True(texture.Bounds.X + caretX + 1.5 <= clip.Right + Epsilon, $"{pixelScale}：普通 caret 右边缘应可见。");
        Assert.Equal(texture.Bounds.X + caretX, caret.Bounds.X, 6);
        Assert.True(service.LastGeometry.HasCaret);
        AssertRect(caret.Bounds, service.LastGeometry.CaretBounds);
        Assert.Equal(service.LastGeometry, Client(node).GetState().Geometry);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void 长预编辑Result确认后普通caret在四DPI下可见且发布一致(double pixelScale)
    {
        var binding = new MutableText(string.Empty);
        var service = new FakeTextInputService();
        var field = new TextField(binding.Binding) { Width = 200 };
        using var runtime = Create(new VStack(field).Align(Alignment.Start), service, new Size(400, 120), pixelScale);
        var node = runtime.Root.FindDescendant<TextField>()
            ?? throw new InvalidOperationException("未找到 TextField。");
        runtime.SetFocus(node);
        runtime.ProcessPendingWork();
        runtime.BuildFrame();

        var preedit = new string('W', 80);
        StartComposition(service, node, new TextRange(0, 0), preedit, preedit.Length, null);
        runtime.ProcessPendingWork();

        var session = Client(node).Session.SessionId;
        var committed = new string('R', 80);
        var result = service.Apply(CompositionEvent.Result(session, 0, 0, 1, committed));
        Assert.False(result.HasRejection, result.RejectionReason);
        Assert.True(result.Outcome.HasFlag(TextInputOutcome.ResultCommitted), $"{result.Outcome}（{result.RejectionReason}）");
        runtime.ProcessPendingWork();
        var commands = runtime.BuildFrame().Commands;

        var layout = Layout(committed, pixelScale);
        var clip = Assert.Single(commands.OfType<PushClipCommand>()).Bounds;
        var texture = Assert.Single(commands.OfType<TextureCommand>());
        var caret = Assert.Single(commands.OfType<RectangleCommand>(), c => c.Fill == Theme.Light.Foreground && c.Bounds.Height > 2);
        var caretX = layout.GetCaretX(committed.Length);

        Assert.False(Client(node).IsComposing);
        Assert.Equal(committed, binding.Text);
        Assert.True(State(node).ScrollX > 0);
        Assert.True(texture.Bounds.X + caretX >= clip.X - Epsilon, $"{pixelScale}：提交后 caret 左边缘应可见。");
        Assert.True(texture.Bounds.X + caretX + 1.5 <= clip.Right + Epsilon, $"{pixelScale}：提交后 caret 右边缘应可见。");
        Assert.Equal(texture.Bounds.X + caretX, caret.Bounds.X, 6);
        Assert.True(service.LastGeometry.HasCaret);
        Assert.False(service.LastGeometry.HasComposition);
        AssertRect(caret.Bounds, service.LastGeometry.CaretBounds);
        Assert.Equal(service.LastGeometry, Client(node).GetState().Geometry);
    }

    [Fact]
    public void 长预编辑滚动后外部缩短文本在同一安全阶段发布正式caret()
    {
        var binding = new MutableText("ABCD");
        var service = new FakeTextInputService();
        var field = new TextField(binding.Binding) { Width = 200 };
        using var runtime = Create(new VStack(field).Align(Alignment.Start), service, new Size(400, 120), 1.5);
        var node = runtime.Root.FindDescendant<TextField>()
            ?? throw new InvalidOperationException("未找到 TextField。");
        runtime.SetFocus(node);
        runtime.ProcessPendingWork();
        runtime.BuildFrame();

        var preedit = new string('W', 80);
        StartComposition(service, node, new TextRange(1, 3), preedit, preedit.Length, null);
        runtime.ProcessPendingWork();
        runtime.BuildFrame();
        Assert.True(State(node).ScrollX > 100);

        // 纯重绘路径：外部短文本必须在发布锚点前完成显示串/滚动收敛，而不是等 Emit。
        binding.Text = "X";
        runtime.InvalidateRender();
        runtime.ProcessPendingWork();

        Assert.Equal(0, State(node).ScrollX);
        var active = Assert.IsAssignableFrom<TextCompositionClient>(service.ActiveClient);
        Assert.False(active.IsComposing);
        Assert.True(service.LastGeometry.HasCaret);
        Assert.Equal(service.LastGeometry, active.GetState().Geometry);
        Assert.Equal("X", active.GetState().Document.Text);

        var commands = runtime.BuildFrame().Commands;
        var caret = Assert.Single(commands.OfType<RectangleCommand>(), c => c.Fill == Theme.Light.Foreground && c.Bounds.Height > 2);
        AssertRect(caret.Bounds, service.LastGeometry.CaretBounds);
        var layout = Layout("X", 1.5);
        var texture = Assert.Single(commands.OfType<TextureCommand>());
        Assert.Equal((int)Math.Ceiling(layout.Size.Width * 1.5), texture.Texture.PixelWidth);
    }

    [Fact]
    public void 显示到文档映射在前后缀与预编辑上一致()
    {
        var binding = new MutableText("abef");
        var service = new FakeTextInputService();
        using var runtime = Create(new VStack(new TextField(binding.Binding) { Width = 240 }), service, new Size(400, 120), 1.0);
        var node = (runtime.Root.FindDescendant<TextField>() ?? throw new InvalidOperationException("未找到 TextField。"));
        runtime.SetFocus(node);
        runtime.ProcessPendingWork();
        runtime.BuildFrame();

        StartComposition(service, node, new TextRange(2, 4), "ni", 2, null);
        var client = Client(node);
        var expected = new (int Display, int Document, bool Preedit)[]
        {
            (0, 0, false),
            (1, 1, false),
            (2, 2, true),
            (3, 2, true),
            (4, 4, false),
            (5, 5, false),
        };

        foreach (var (display, document, preedit) in expected)
        {
            var map = client.MapDisplayToDocument(display);
            Assert.Equal(document, map.ToDocumentOffset());
            Assert.Equal(preedit, map.IsPreedit);
            Assert.Equal(preedit ? display - 2 : 0, map.ToPreeditOffset());
        }

        Assert.Equal("abni", client.DisplayString);
        Assert.Equal("abef", binding.Text);
    }

    [Fact]
    public void 嵌套裁剪与祖先滚动下真实Skia几何仍落在最终文本原点()
    {
        var binding = new MutableText("ab");
        var service = new FakeTextInputService();
        var field = new TextField(binding.Binding) { Width = 200 };
        var scroll = new ScrollView(new VStack(field, new Spacer(200)));
        var root = new VStack(new ClipView(scroll).Frame(height: 50));
        using var runtime = Create(root, service, new Size(300, 140), 1.5);
        var node = (runtime.Root.FindDescendant<TextField>() ?? throw new InvalidOperationException("未找到 TextField。"));
        var scrollNode = (runtime.Root.FindDescendant<ScrollView>() ?? throw new InvalidOperationException("未找到 ScrollView。"));
        var clipNode = (runtime.Root.FindDescendant<ClipView>() ?? throw new InvalidOperationException("未找到 ClipView。"));
        runtime.SetFocus(node);
        runtime.ProcessPendingWork();
        runtime.BuildFrame();

        StartComposition(service, node, new TextRange(2, 2), "ni", 2, [new TextRange(0, 2)]);
        runtime.ProcessPendingWork();

        // 未滚动：发布几何的 x 来自真实 Skia 显示排版，y 来自最终节点矩形 + 垂直居中。
        var commands = runtime.BuildFrame().Commands;
        var texture = Assert.Single(commands.OfType<TextureCommand>());
        var layout = Layout("abni", 1.5);
        var caretLayout = layout.GetCaretBounds(4, 1.5);
        var visible = service.LastGeometry;
        Assert.True(visible.HasCaret);
        Assert.Equal(texture.Bounds.X + caretLayout.X, visible.CaretBounds.X, 6);
        Assert.True(visible.CaretBounds.Bottom <= clipNode.Frame.Bottom + Epsilon);

        // 滚动 10：caret 顶部被嵌套 Clip 裁掉，发布几何使用裁剪后的有效区域。
        scrollNode.ScrollOffsetY = 10;
        scrollNode.InvalidateLayout();
        runtime.ProcessPendingWork();
        var clipped = service.LastGeometry;
        Assert.True(clipped.HasCaret);
        Assert.Equal(clipNode.Frame.Top, clipped.CaretBounds.Top, 6);
        Assert.True(clipped.CaretBounds.Bottom <= clipNode.Frame.Bottom + Epsilon);
        Assert.Equal(clipNode.Frame.Top, clipped.ExclusionBounds.Top, 6);

        // 完全滚出：撤销锚点，不把候选定位到窗口原点。
        scrollNode.ScrollOffsetY = 80;
        scrollNode.InvalidateLayout();
        runtime.ProcessPendingWork();
        Assert.False(service.LastGeometry.HasCaret);
    }

    private UiRuntime Create(View root, FakeTextInputService service, Size viewport, double pixelScale)
    {
        var runtime = new UiRuntime(root, new UiRuntimeServices
        {
            Dispatcher = new HeadlessUiDispatcher(),
            TextService = _text,
            TextureService = new RecordingTextureService(),
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

    private TextLayoutResult Layout(string text, double pixelScale)
        => _text.Layout(TextPainter.CreateRequest(text, FontDescription.Default, double.PositiveInfinity, pixelScale));

    private static void StartComposition(
        FakeTextInputService service,
        ViewNode node,
        TextRange replacement,
        string preedit,
        int caret,
        IReadOnlyList<TextRange>? segments)
    {
        var session = Client(node).Session.SessionId;
        var result = service.Apply(CompositionEvent.Start(
            session,
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

    private static void AssertRect(Rect expected, Rect actual)
    {
        Assert.Equal(expected.X, actual.X, 6);
        Assert.Equal(expected.Y, actual.Y, 6);
        Assert.Equal(expected.Width, actual.Width, 6);
        Assert.Equal(expected.Height, actual.Height, 6);
    }

    private static TextCompositionClient Client(ViewNode node)
        => State(node).Composing ?? throw new InvalidOperationException("该节点没有组合客户端（会话未建立）。");

    private static TextFieldState State(ViewNode node) => Assert.IsType<TextFieldState>(node.InteractionState);

    /// <summary>可写文本绑定：身份稳定并记录写入次数。</summary>
    private sealed class MutableText
    {
        public MutableText(string initial)
        {
            Text = initial;
            Binding = new Binding<string>(
                () => Text,
                value =>
                {
                    Text = value;
                    Writes++;
                });
        }

        public string Text { get; set; }

        public int Writes { get; private set; }

        public Binding<string> Binding { get; }
    }

    /// <summary>把组合事件投递给活动客户端并记录候选几何的输入服务替身。</summary>
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
