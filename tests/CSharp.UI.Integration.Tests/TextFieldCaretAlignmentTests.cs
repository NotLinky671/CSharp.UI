using CSharp.UI;
using CSharp.UI.Testing;
using Xunit;
using CSharp.UI.Skia;

namespace CSharp.UI.IntegrationTests;

/// <summary>
/// R12 集成回归：真实 SkiaSharp/HarfBuzz 排版驱动 TextField 时，
/// 文字纹理、光标与选择高亮必须共用同一个文本原点（内容区垂直居中），
/// 且该约定不随输入框高度、字号与 DPI 改变。
/// </summary>
public class TextFieldCaretAlignmentTests
{
    private static UiRuntime Create(
        string text,
        double height,
        double fontSize = 14,
        string? placeholder = null,
        double pixelScale = 1.0,
        Size? viewport = null)
    {
        var field = new TextField(Binding<string>.Constant(text), placeholder) { Height = height };
        var runtime = new UiRuntime(new VStack(field), new UiRuntimeServices
        {
            Dispatcher = new HeadlessUiDispatcher(),
            TextService = new SkiaTextService(),
            TextureService = new RecordingTextureService(),
            Clipboard = new StubClipboardService(),
            Clock = new TestUiClock(),
        });

        runtime.SetEnvironment(new EnvironmentValues
        {
            Theme = Theme.Light,
            Font = FontDescription.Default.WithSize(fontSize),
        });
        runtime.SetViewport(viewport ?? new Size(400, 200), pixelScale);
        runtime.Start();
        runtime.ProcessPendingWork();
        runtime.BuildFrame();
        return runtime;
    }

    private static (ViewNode Node, IReadOnlyList<DrawCommand> Commands) FocusAndFrame(UiRuntime runtime)
    {
        var node = runtime.Root.FindDescendant<TextField>()
            ?? throw new InvalidOperationException("未找到 TextField 节点。");
        runtime.SetFocus(node);
        runtime.ProcessPendingWork();
        return (node, runtime.BuildFrame().Commands);
    }

    private static TextureCommand TextTexture(IReadOnlyList<DrawCommand> commands)
        => Assert.Single(commands.OfType<TextureCommand>());

    private static RectangleCommand Caret(IReadOnlyList<DrawCommand> commands)
        => Assert.Single(commands.OfType<RectangleCommand>()
            .Where(c => c.Fill == Theme.Light.Foreground));

    private static RectangleCommand Selection(IReadOnlyList<DrawCommand> commands)
        => Assert.Single(commands.OfType<RectangleCommand>()
            .Where(c => c.Fill == Theme.Light.SelectionBackground));

    private static void AssertSameVerticalCenter(Rect expected, Rect actual, string label)
    {
        var delta = Math.Abs(expected.Center.Y - actual.Center.Y);
        Assert.True(delta < 0.001, $"{label} 的纵向中心应一致，实际偏差 {delta}。");
    }

    [Theory]
    [InlineData(32, 14)]
    [InlineData(48, 14)]
    [InlineData(64, 18)]
    [InlineData(40, 22)]
    public void 真实排版下光标与文字纹理共享纵向中心(double height, double fontSize)
    {
        var runtime = Create("Hello 中文混排", height, fontSize);
        using var _runtime = runtime;

        var (node, commands) = FocusAndFrame(runtime);
        var text = TextTexture(commands);
        var caret = Caret(commands);

        Assert.True(text.Texture.PixelWidth > 0);
        AssertSameVerticalCenter(text.Bounds, caret.Bounds, "光标");
        AssertSameVerticalCenter(node.Frame, text.Bounds, "文字");
        Assert.Equal(text.Bounds.Height, caret.Bounds.Height, 3);
    }

    [Fact]
    public void 真实排版下选择高亮与文字行纵向对齐()
    {
        var runtime = Create("hello 中文选择", 48);
        using var _runtime = runtime;

        var (node, commands) = FocusAndFrame(runtime);
        runtime.DispatchKey(new KeyEvent(KeyEventKind.Down, UiKey.A, KeyModifiers.Control));
        runtime.ProcessPendingWork();
        commands = runtime.BuildFrame().Commands;

        var text = TextTexture(commands);
        var selection = Selection(commands);
        var caret = Caret(commands);

        Assert.True(selection.Bounds.Width > 0);
        AssertSameVerticalCenter(text.Bounds, selection.Bounds, "选区");
        AssertSameVerticalCenter(text.Bounds, caret.Bounds, "光标");
        AssertSameVerticalCenter(node.Frame, selection.Bounds, "输入框");
    }

    [Fact]
    public void 真实排版下空文本与placeholder使用居中文本原点()
    {
        var runtime = Create(string.Empty, 48, placeholder: "请输入内容");
        using var _runtime = runtime;

        var (node, commands) = FocusAndFrame(runtime);
        var placeholder = TextTexture(commands);
        var caret = Caret(commands);

        AssertSameVerticalCenter(node.Frame, placeholder.Bounds, "placeholder");
        AssertSameVerticalCenter(placeholder.Bounds, caret.Bounds, "光标");

        // 无 placeholder 的空文本：没有纹理，光标仍在文字行本应出现的位置。
        var plainRuntime = Create(string.Empty, 48);
        using var _plainRuntime = plainRuntime;
        var (plainNode, plainCommands) = FocusAndFrame(plainRuntime);
        Assert.DoesNotContain(plainCommands, command => command is TextureCommand);
        AssertSameVerticalCenter(plainNode.Frame, Caret(plainCommands).Bounds, "空文本光标");
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void 真实排版下不同DPI保持同一几何约定(double pixelScale)
    {
        var runtime = Create("dpi 中文", 48, pixelScale: pixelScale);
        using var _runtime = runtime;

        var (node, commands) = FocusAndFrame(runtime);
        var text = TextTexture(commands);
        var caret = Caret(commands);

        AssertSameVerticalCenter(text.Bounds, caret.Bounds, "光标");
        AssertSameVerticalCenter(node.Frame, text.Bounds, "文字");
        Assert.Equal((int)Math.Ceiling(text.Bounds.Height * pixelScale), text.Texture.PixelHeight);
    }

    [Fact]
    public void 真实排版下横向滚动后光标仍与文字共享纵向原点()
    {
        var runtime = Create(new string('w', 80), 48, viewport: new Size(200, 200));
        using var _runtime = runtime;

        var (node, commands) = FocusAndFrame(runtime);
        runtime.DispatchKey(new KeyEvent(KeyEventKind.Down, UiKey.End));
        runtime.ProcessPendingWork();
        commands = runtime.BuildFrame().Commands;

        var text = TextTexture(commands);
        var caret = Caret(commands);
        var clip = Assert.Single(commands.OfType<PushClipCommand>());

        Assert.True(text.Bounds.X < clip.Bounds.X, "文本应已横向滚动。");
        AssertSameVerticalCenter(text.Bounds, caret.Bounds, "光标");
        AssertSameVerticalCenter(node.Frame, caret.Bounds, "输入框");
        Assert.InRange(caret.Bounds.X, clip.Bounds.X, clip.Bounds.Right);
    }
}
