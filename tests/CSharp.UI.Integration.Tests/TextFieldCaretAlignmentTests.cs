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

    private static void AssertPhysicalPixelOrigin(Rect bounds, double pixelScale)
    {
        Assert.Equal(0, Math.IEEERemainder(bounds.X * pixelScale, 1), 6);
        Assert.Equal(0, Math.IEEERemainder(bounds.Y * pixelScale, 1), 6);
    }

    /// <summary>R16 契约：真实 Skia 遮罩的目标物理尺寸与纹理像素 1:1（1 纹素 = 1 物理像素）。</summary>
    private static void AssertOneTexelPerPhysicalPixel(TextureCommand text, double pixelScale)
    {
        Assert.Equal(text.Texture.PixelWidth, text.Bounds.Width * pixelScale, 6);
        Assert.Equal(text.Texture.PixelHeight, text.Bounds.Height * pixelScale, 6);
    }

    /// <summary>单行文本里光标/选区的行内 Y 偏移为 0，顶边必须与纹理顶边完全一致。</summary>
    private static void AssertSharedVerticalOrigin(TextureCommand text, RectangleCommand part, double pixelScale, string label)
    {
        AssertPhysicalPixelOrigin(text.Bounds, pixelScale);
        Assert.Equal(text.Bounds.Y, part.Bounds.Y, 9);
    }

    /// <summary>吸附只允许平移不到半个物理像素，真实排版后的文字行仍必须在输入框内垂直居中。</summary>
    private static void AssertLineBoxCentered(Rect frame, double originY, double lineHeight, double pixelScale)
        => Assert.True(
            Math.Abs((originY + (lineHeight / 2)) - frame.Center.Y) <= (0.5 / pixelScale) + 1e-6,
            $"文字行应在输入框内垂直居中（吸附误差不超过半个物理像素），实际偏差 {Math.Abs((originY + (lineHeight / 2)) - frame.Center.Y)}。");

    [Theory]
    [InlineData(32, 14)]
    [InlineData(48, 14)]
    [InlineData(64, 18)]
    [InlineData(40, 22)]
    public void 真实排版下光标与文字纹理共享物理像素原点(double height, double fontSize)
    {
        var runtime = Create("Hello 中文混排", height, fontSize);
        using var _runtime = runtime;

        var (node, commands) = FocusAndFrame(runtime);
        var text = TextTexture(commands);
        var caret = Caret(commands);

        Assert.True(text.Texture.PixelWidth > 0);
        AssertSharedVerticalOrigin(text, caret, runtime.PixelScale, "光标");
        AssertOneTexelPerPhysicalPixel(text, runtime.PixelScale);
        AssertLineBoxCentered(node.Frame, text.Bounds.Y, caret.Bounds.Height, runtime.PixelScale);
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
        AssertSharedVerticalOrigin(text, selection, runtime.PixelScale, "选区");
        AssertSharedVerticalOrigin(text, caret, runtime.PixelScale, "光标");
        AssertLineBoxCentered(node.Frame, text.Bounds.Y, selection.Bounds.Height, runtime.PixelScale);
    }

    [Fact]
    public void 真实排版下空文本与placeholder使用居中文本原点()
    {
        var runtime = Create(string.Empty, 48, placeholder: "请输入内容");
        using var _runtime = runtime;

        var (node, commands) = FocusAndFrame(runtime);
        var placeholder = TextTexture(commands);
        var caret = Caret(commands);

        AssertSharedVerticalOrigin(placeholder, caret, runtime.PixelScale, "光标");
        AssertLineBoxCentered(node.Frame, placeholder.Bounds.Y, caret.Bounds.Height, runtime.PixelScale);

        // 无 placeholder 的空文本：没有纹理，光标仍在文字行本应出现的位置。
        var plainRuntime = Create(string.Empty, 48);
        using var _plainRuntime = plainRuntime;
        var (plainNode, plainCommands) = FocusAndFrame(plainRuntime);
        Assert.DoesNotContain(plainCommands, command => command is TextureCommand);
        AssertPhysicalPixelOrigin(Caret(plainCommands).Bounds, plainRuntime.PixelScale);
        AssertLineBoxCentered(plainNode.Frame, Caret(plainCommands).Bounds.Y, Caret(plainCommands).Bounds.Height, plainRuntime.PixelScale);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void 真实排版下不同DPI保持同一几何约定(double pixelScale)
    {
        var runtime = Create("dpi 中文", 48, pixelScale: pixelScale);
        using var _runtime = runtime;

        var (node, commands) = FocusAndFrame(runtime);
        var text = TextTexture(commands);
        var caret = Caret(commands);

        AssertSharedVerticalOrigin(text, caret, pixelScale, "光标");
        AssertOneTexelPerPhysicalPixel(text, pixelScale);
        AssertLineBoxCentered(node.Frame, text.Bounds.Y, caret.Bounds.Height, pixelScale);
        Assert.Equal((int)Math.Ceiling(text.Bounds.Height * pixelScale), text.Texture.PixelHeight);
    }

    [Fact]
    public void 真实排版下横向滚动后光标仍与文字共享物理像素原点()
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
        AssertSharedVerticalOrigin(text, caret, runtime.PixelScale, "光标");
        AssertOneTexelPerPhysicalPixel(text, runtime.PixelScale);
        AssertLineBoxCentered(node.Frame, caret.Bounds.Y, caret.Bounds.Height, runtime.PixelScale);
        Assert.InRange(caret.Bounds.X, clip.Bounds.X, clip.Bounds.Right);
    }
}
