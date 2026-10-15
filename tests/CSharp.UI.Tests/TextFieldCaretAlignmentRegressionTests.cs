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

    private static void AssertSameVerticalCenter(Rect expected, Rect actual, string label)
    {
        var delta = Math.Abs(expected.Center.Y - actual.Center.Y);
        Assert.True(delta < 0.001, $"{label} 的纵向中心应一致，实际偏差 {delta}。");
    }

    [Theory]
    [InlineData(24, 14)]
    [InlineData(32, 14)]
    [InlineData(48, 14)]
    [InlineData(64, 14)]
    [InlineData(32, 20)]
    [InlineData(48, 20)]
    [InlineData(64, 24)]
    public void 光标与文字纹理共享纵向原点且保持垂直居中(double height, double fontSize)
    {
        using var app = CreateApp("Hello 中文", height, fontSize);
        var (node, commands) = FocusAndFrame(app);

        var text = TextTexture(commands);
        var caret = Caret(commands);

        // 光标行与文字纹理共享同一纵向中心，且与输入框外观中心一致。
        AssertSameVerticalCenter(text.Bounds, caret.Bounds, "光标");
        AssertSameVerticalCenter(node.Frame, text.Bounds, "文字");
        Assert.Equal(text.Bounds.Height, caret.Bounds.Height, 3);
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

        AssertSameVerticalCenter(text.Bounds, selection.Bounds, "选区");
        AssertSameVerticalCenter(text.Bounds, caret.Bounds, "光标");
        Assert.Equal(text.Bounds.Height, selection.Bounds.Height, 3);
        AssertSameVerticalCenter(node.Frame, selection.Bounds, "输入框");
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
        AssertSameVerticalCenter(node.Frame, caret.Bounds, "空文本光标");
    }

    [Fact]
    public void 空文本与placeholder共享同一纵向中心()
    {
        using var app = CreateApp(string.Empty, 48, placeholder: "请输入");
        var (node, commands) = FocusAndFrame(app);

        var placeholder = TextTexture(commands);
        Assert.Equal(Theme.Light.MutedForeground, placeholder.Tint);

        var caret = Caret(commands);
        AssertSameVerticalCenter(node.Frame, placeholder.Bounds, "placeholder");
        AssertSameVerticalCenter(placeholder.Bounds, caret.Bounds, "光标");
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

        AssertSameVerticalCenter(text.Bounds, caret.Bounds, "光标");
        AssertSameVerticalCenter(node.Frame, text.Bounds, "文字");

        // 逻辑几何不随 DPI 改变，纹理像素高度按 DPI 缩放。
        Assert.Equal((int)Math.Ceiling(text.Bounds.Height * pixelScale), text.Texture.PixelHeight);
    }

    [Fact]
    public void 横向滚动后光标与文字仍共享纵向原点()
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
        AssertSameVerticalCenter(text.Bounds, caret.Bounds, "光标");

        // 纵向原点不受滚动影响：光标仍在输入框垂直中心所在的行内。
        AssertSameVerticalCenter(node.Frame, caret.Bounds, "输入框");
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
