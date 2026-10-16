using CSharp.UI;
using Xunit;

namespace CSharp.UI.Tests;

/// <summary>
/// R12 回归：TextField 的文字纹理、光标、选择高亮与点击命中必须共用同一个文本原点。
/// </summary>
/// <remarks>
/// 文字纹理在内容区内垂直居中；修复前光标与选区直接以内容区顶边为原点，
/// 输入框变高或字号变大后光标偏移量随之变化（height=32 时偏差 5.25，height=48 时 13.25）。
/// 这里用实际绘制命令断言几何关系，而不是复制实现里的偏移公式：
/// 三者共享同一纵向中心，且该中心与输入框外观中心重合（垂直居中外观保持不变）。
/// </remarks>
public class TextFieldCaretAlignmentRegressionTests
{
    private static TestApp CreateApp(
        string text,
        double height,
        double fontSize = 14,
        string? placeholder = null,
        double pixelScale = 1.0,
        Size? viewport = null)
    {
        var field = new TextField(Binding<string>.Constant(text), placeholder) { Height = height };
        var app = new TestApp(new VStack(field), viewport ?? new Size(400, 200), pixelScale);
        app.Runtime.SetEnvironment(new EnvironmentValues
        {
            Theme = Theme.Light,
            Font = FontDescription.Default.WithSize(fontSize),
        });
        app.Runtime.ProcessPendingWork();
        app.Frame();
        return app;
    }

    private static (ViewNode Node, IReadOnlyList<DrawCommand> Commands) FocusAndFrame(TestApp app)
    {
        var node = app.Runtime.Root.Require<TextField>();
        app.Runtime.SetFocus(node);
        app.Runtime.ProcessPendingWork();
        return (node, app.Frame());
    }

    private static TextureCommand TextTexture(IReadOnlyList<DrawCommand> commands)
        => Assert.Single(commands.OfType<TextureCommand>());

    private static RectangleCommand Caret(IReadOnlyList<DrawCommand> commands)
        => Assert.Single(commands.OfType<RectangleCommand>()
            .Where(c => c.Fill == Theme.Light.Foreground));

    private static RectangleCommand Selection(IReadOnlyList<DrawCommand> commands)
        => Assert.Single(commands.OfType<RectangleCommand>()
            .Where(c => c.Fill == Theme.Light.SelectionBackground));

    private static void AssertPhysicalPixelOrigin(Rect bounds, double pixelScale, string label)
    {
        Assert.Equal(0, Math.IEEERemainder(bounds.X * pixelScale, 1), 6);
        Assert.Equal(0, Math.IEEERemainder(bounds.Y * pixelScale, 1), 6);
    }

    /// <summary>R16 契约：文字遮罩目标物理尺寸与纹理像素 1:1（1 纹素 = 1 物理像素）。</summary>
    private static void AssertOneTexelPerPhysicalPixel(TextureCommand text, double pixelScale)
    {
        Assert.Equal(text.Texture.PixelWidth, text.Bounds.Width * pixelScale, 6);
        Assert.Equal(text.Texture.PixelHeight, text.Bounds.Height * pixelScale, 6);
    }

    /// <summary>单行文本里光标/选区的行内 Y 偏移为 0，因此它们的顶边必须与纹理顶边完全一致。</summary>
    private static void AssertSharedVerticalOrigin(TextureCommand text, RectangleCommand part, double pixelScale, string label)
    {
        AssertPhysicalPixelOrigin(text.Bounds, pixelScale, "文字纹理");
        Assert.Equal(text.Bounds.Y, part.Bounds.Y, 9);
    }

    /// <summary>吸附只允许平移不到半个物理像素，排版后的文字行仍必须在输入框内垂直居中。</summary>
    private static void AssertLineBoxCentered(Rect frame, double originY, double lineHeight, double pixelScale)
        => Assert.True(
            Math.Abs((originY + (lineHeight / 2)) - frame.Center.Y) <= (0.5 / pixelScale) + 1e-6,
            $"文字行应在输入框内垂直居中（吸附误差不超过半个物理像素），实际偏差 {Math.Abs((originY + (lineHeight / 2)) - frame.Center.Y)}。");

    [Theory]
    [InlineData(24, 14)]
    [InlineData(32, 14)]
    [InlineData(48, 14)]
    [InlineData(64, 14)]
    [InlineData(32, 20)]
    [InlineData(48, 20)]
    [InlineData(64, 24)]
    public void 光标与文字纹理共享物理像素原点和垂直居中(double height, double fontSize)
    {
        using var app = CreateApp("Hello 中文", height, fontSize);
        var (node, commands) = FocusAndFrame(app);

        var text = TextTexture(commands);
        var caret = Caret(commands);

        AssertSharedVerticalOrigin(text, caret, app.Runtime.PixelScale, "光标");
        AssertOneTexelPerPhysicalPixel(text, app.Runtime.PixelScale);
        AssertLineBoxCentered(node.Frame, text.Bounds.Y, caret.Bounds.Height, app.Runtime.PixelScale);
        Assert.True(caret.Bounds.Height > 0);
    }

    [Fact]
    public void 选择高亮与文字行纵向对齐()
    {
        using var app = CreateApp("hello selection", 48);
        var (node, _) = FocusAndFrame(app);
        app.Key(UiKey.A, KeyModifiers.Control);
        var commands = app.Frame();

        var text = TextTexture(commands);
        var selection = Selection(commands);
        var caret = Caret(commands);

        AssertSharedVerticalOrigin(text, selection, app.Runtime.PixelScale, "选区");
        AssertSharedVerticalOrigin(text, caret, app.Runtime.PixelScale, "光标");
        AssertLineBoxCentered(node.Frame, text.Bounds.Y, selection.Bounds.Height, app.Runtime.PixelScale);
    }

    [Fact]
    public void 空文本时光标仍使用居中的文本原点()
    {
        using var app = CreateApp(string.Empty, 48);
        var (node, commands) = FocusAndFrame(app);

        // 空文本没有纹理，但光标必须落在文字行本应出现的位置：内容区纵向中心。
        Assert.DoesNotContain(commands, command => command is TextureCommand);
        var caret = Caret(commands);
        Assert.True(caret.Bounds.Height > 0);
        AssertPhysicalPixelOrigin(caret.Bounds, app.Runtime.PixelScale, "空文本光标");
        AssertLineBoxCentered(node.Frame, caret.Bounds.Y, caret.Bounds.Height, app.Runtime.PixelScale);
    }

    [Fact]
    public void 空文本与placeholder共享同一纵向原点()
    {
        using var app = CreateApp(string.Empty, 48, placeholder: "请输入");
        var (node, commands) = FocusAndFrame(app);

        var placeholder = TextTexture(commands);
        Assert.Equal(Theme.Light.MutedForeground, placeholder.Tint);

        var caret = Caret(commands);
        AssertSharedVerticalOrigin(placeholder, caret, app.Runtime.PixelScale, "光标");
        AssertLineBoxCentered(node.Frame, placeholder.Bounds.Y, caret.Bounds.Height, app.Runtime.PixelScale);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void 不同DPI下几何关系一致且纹理按缩放栅格化(double pixelScale)
    {
        using var app = CreateApp("dpi hello", 48, pixelScale: pixelScale);
        var (node, commands) = FocusAndFrame(app);

        var text = TextTexture(commands);
        var caret = Caret(commands);

        AssertSharedVerticalOrigin(text, caret, pixelScale, "光标");
        AssertOneTexelPerPhysicalPixel(text, pixelScale);
        AssertLineBoxCentered(node.Frame, text.Bounds.Y, caret.Bounds.Height, pixelScale);

        // 逻辑几何不随 DPI 改变，纹理像素尺寸就是排版尺寸向上取整后的物理像素数。
        Assert.Equal(0, Math.IEEERemainder(text.Bounds.X * pixelScale, 1), 6);
    }

    [Fact]
    public void 横向滚动后光标与文字仍共享物理像素原点()
    {
        using var app = CreateApp(new string('w', 200), 48, viewport: new Size(200, 200));
        var (node, _) = FocusAndFrame(app);

        // 光标移到末尾触发横向滚动。
        app.Key(UiKey.End);
        var commands = app.Frame();

        var text = TextTexture(commands);
        var caret = Caret(commands);
        var clip = Assert.Single(commands.OfType<PushClipCommand>());

        // 文字左移出裁剪区，说明确实发生了滚动。
        Assert.True(text.Bounds.X < clip.Bounds.X, "文本应已横向滚动。");
        AssertSharedVerticalOrigin(text, caret, app.Runtime.PixelScale, "光标");
        AssertOneTexelPerPhysicalPixel(text, app.Runtime.PixelScale);

        // 纵向原点不受滚动影响：光标仍在输入框垂直中心所在的行内。
        AssertLineBoxCentered(node.Frame, caret.Bounds.Y, caret.Bounds.Height, app.Runtime.PixelScale);
        Assert.InRange(caret.Bounds.X, clip.Bounds.X, clip.Bounds.Right);
        Assert.True(caret.Bounds.Bottom <= clip.Bounds.Bottom + 0.001);
    }

    [Fact]
    public void 点击命中使用同一文本原点定位字素边界()
    {
        using var app = CreateApp("abcdef", 48);
        var (node, _) = FocusAndFrame(app);

        // 用绘制命令给出的内容区与排版结果自己的光标 X 计算点击位置，避免复制实现坐标。
        var clip = Assert.Single(app.Frame().OfType<PushClipCommand>()).Bounds;
        var layout = app.Text.Layout(TextPainter.CreateRequest(
            "abcdef",
            FontDescription.Default,
            double.PositiveInfinity,
            app.Runtime.PixelScale));
        var targetX = clip.X + layout.GetCaretX(3);

        app.Runtime.DispatchPointer(new PointerEvent(
            PointerEventKind.Down,
            new Point(targetX, node.Frame.Center.Y)));
        app.Runtime.DispatchPointer(new PointerEvent(
            PointerEventKind.Up,
            new Point(targetX, node.Frame.Center.Y)));

        var state = Assert.IsType<TextFieldState>(node.InteractionState);
        Assert.Equal(3, state.Caret);
    }
}
