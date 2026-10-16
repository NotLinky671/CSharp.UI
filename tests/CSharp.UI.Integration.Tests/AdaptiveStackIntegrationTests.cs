using CSharp.UI;
using CSharp.UI.Skia;
using CSharp.UI.Testing;
using Xunit;

// 文本实现位于 CSharp.UI.Skia：命名空间不能与核心视图类型 CSharp.UI.Text 同名。
namespace CSharp.UI.IntegrationTests;

/// <summary>
/// RL-04：真实 SkiaSharp/HarfBuzz 下的 AdaptiveStack —— 宽窄切换保持输入框中文数据、焦点、光标与选区，
/// 缩窄后真实文字光标仍在可见区，真实换行文本高度随模式变化，DPI 不影响模式选择。
/// </summary>
public class AdaptiveStackIntegrationTests
{
    private const string LongText =
        "Adaptive stack verification paragraph. 自适应堆叠验证文本，用于确认宽窄切换后按最终宽度重新排版。"
        + "Second sentence keeps wrapping across several lines so switching modes changes the line count and height. "
        + "第三句继续增加内容，确保窄行宽下换行行数明显增多。";

    private static UiRuntime CreateRuntime(View root, Size viewport, double pixelScale = 1.0)
    {
        var dispatcher = new HeadlessUiDispatcher();
        var text = new SkiaTextService();
        var runtime = new UiRuntime(root, new UiRuntimeServices
        {
            Dispatcher = dispatcher,
            TextService = text,
            TextureService = new RecordingTextureService(),
            ImageService = null,
            Clipboard = new StubClipboardService(),
            Clock = new TestUiClock(),
        });

        runtime.SetViewport(viewport, pixelScale);
        runtime.Start();
        runtime.ProcessPendingWork();
        return runtime;
    }

    [Fact]
    public void AdaptiveStack_WideNarrowWide_PreservesTextFieldStateAndRealCaret()
    {
        var original = "响应式布局切换验证 0123456789 the quick brown fox jumps";
        var value = original;
        var binding = Binding<string>.From(() => value, v => value = v);

        using var runtime = CreateRuntime(
            new VStack(new AdaptiveStack(300, new TextField(binding).FillWidth())),
            new Size(500, 60));

        var stackNode = runtime.Root.FindDescendant<AdaptiveStack>()!;
        var node = runtime.Root.FindDescendant<TextField>()!;
        var field = (TextField)node.View;

        Assert.True(stackNode.AdaptiveIsHorizontal);
        Assert.Equal(500, node.Frame.Width, 3);

        runtime.SetFocus(node);
        runtime.DispatchTextInput(new TextInputEvent("中文"));
        var valueAfterTyping = value;
        var expectedCaret = valueAfterTyping.Length;
        runtime.DispatchKey(new KeyEvent(KeyEventKind.Down, UiKey.Home, KeyModifiers.None));
        runtime.DispatchKey(new KeyEvent(KeyEventKind.Down, UiKey.End, KeyModifiers.Shift));

        var state = (TextFieldState)node.InteractionState!;
        Assert.True(state.HasSelection);
        Assert.Equal(expectedCaret, state.Caret);
        Assert.Equal(0, state.Anchor);

        // 宽 → 窄 → 宽：节点身份与全部编辑状态保持。
        runtime.SetViewport(new Size(180, 60), 1.0);
        runtime.ProcessPendingWork();

        Assert.False(stackNode.AdaptiveIsHorizontal);
        Assert.Same(node, runtime.Root.FindDescendant<TextField>());
        Assert.Equal(180, node.Frame.Width, 3);
        Assert.Same(state, node.InteractionState);
        Assert.Equal(valueAfterTyping, value);
        Assert.Equal(expectedCaret, state.Caret);
        Assert.Equal(0, state.Anchor);
        Assert.True(state.HasSelection);
        AssertSameFocusAndCaret(runtime, node, field, state, expectedCaret, valueAfterTyping);

        runtime.SetViewport(new Size(500, 60), 1.0);
        runtime.ProcessPendingWork();

        Assert.True(stackNode.AdaptiveIsHorizontal);
        Assert.Same(node, runtime.Root.FindDescendant<TextField>());
        Assert.Equal(500, node.Frame.Width, 3);
        Assert.Equal(valueAfterTyping, value);
        Assert.Equal(expectedCaret, state.Caret);
        Assert.Equal(0, state.Anchor);
        AssertSameFocusAndCaret(runtime, node, field, state, expectedCaret, valueAfterTyping);
    }

    [Fact]
    public void AdaptiveStack_RealWrappedTextHeightChangesWithMode()
    {
        using var runtime = CreateRuntime(
            new VStack(new AdaptiveStack(300, new Text(LongText).Wrap().FillWidth())),
            new Size(600, 400));

        var stackNode = runtime.Root.FindDescendant<AdaptiveStack>()!;
        var textNode = runtime.Root.FindDescendant<Text>()!;

        var wideLines = ((TextLayoutResult)textNode.ViewCache!).Lines.Count;
        var wideHeight = stackNode.DesiredSize.Height;
        Assert.True(stackNode.AdaptiveIsHorizontal);
        Assert.Equal(600, textNode.Frame.Width, 3);

        runtime.SetViewport(new Size(180, 400), 1.0);
        runtime.ProcessPendingWork();

        var narrowLines = ((TextLayoutResult)textNode.ViewCache!).Lines.Count;
        var narrowHeight = stackNode.DesiredSize.Height;

        // 真实排版：窄模式换行行数增加，堆叠高度随之增长。
        Assert.False(stackNode.AdaptiveIsHorizontal);
        Assert.Equal(180, textNode.Frame.Width, 3);
        Assert.True(narrowLines > wideLines, $"窄模式行数 {narrowLines} 应多于 {wideLines}。");
        Assert.True(narrowHeight > wideHeight + 1, $"窄模式高度 {narrowHeight} 应大于 {wideHeight}。");
        Assert.InRange(textNode.Frame.Height, ((TextLayoutResult)textNode.ViewCache!).Size.Height - 0.01, ((TextLayoutResult)textNode.ViewCache!).Size.Height + 1.01);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void AdaptiveStack_ModeSelectionIsDpiIndependent(double pixelScale)
    {
        using var runtime = CreateRuntime(
            new VStack(new AdaptiveStack(300, new Text(LongText).Wrap().FillWidth())),
            new Size(300, 400),
            pixelScale);

        var stackNode = runtime.Root.FindDescendant<AdaptiveStack>()!;
        Assert.True(stackNode.AdaptiveIsHorizontal);

        runtime.SetViewport(new Size(299, 400), pixelScale);
        runtime.ProcessPendingWork();
        Assert.False(stackNode.AdaptiveIsHorizontal);

        runtime.SetViewport(new Size(300, 400), pixelScale);
        runtime.ProcessPendingWork();
        Assert.True(stackNode.AdaptiveIsHorizontal);
    }

    private static void AssertSameFocusAndCaret(UiRuntime runtime, ViewNode node, TextField field, TextFieldState state, int caret, string expectedText)
    {
        Assert.Same(node, runtime.FocusedNode);

        var inner = InnerRect(node.Frame, field);
        var font = node.Environment.Font ?? FontDescription.Default;
        var layout = runtime.Services.TextService.Layout(
            TextPainter.CreateRequest(expectedText, font, double.PositiveInfinity, runtime.PixelScale));

        var caretStop = layout.SnapToCaretStop(caret);
        var caretScreenX = inner.X + layout.GetCaretX(caretStop) - state.ScrollX;
        Assert.InRange(caretScreenX, inner.Left - 0.5, inner.Right + 0.5);

        // 文本末端必须真的滚入可见区：只把矢量光标夹在边缘不算通过。
        Assert.InRange(layout.Size.Width - state.ScrollX, inner.Left, inner.Right + 1.5);

        var commands = runtime.BuildFrame().Commands;
        var caretCommand = Assert.Single(
            commands.OfType<RectangleCommand>(),
            command => command.Bounds.Width <= 2.0 && command.Bounds.Height > 4);
        Assert.InRange(caretCommand.Bounds.X, inner.Left - 0.5, inner.Right + 0.5);

        var texture = Assert.Single(commands.OfType<TextureCommand>());
        var expectedRight = Math.Min(inner.Right, inner.Left + layout.Size.Width - state.ScrollX);
        Assert.InRange(texture.Bounds.Right, expectedRight - 1.5, expectedRight + 1.5);
    }

    private static Rect InnerRect(Rect frame, TextField field)
        => frame
            .Deflate(new Thickness(field.HorizontalPadding, 0))
            .Deflate(new Thickness(0, 2));
}
