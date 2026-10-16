using CSharp.UI;
using CSharp.UI.Skia;
using CSharp.UI.Testing;
using Xunit;

// 文本实现位于 CSharp.UI.Skia：命名空间不能与核心视图类型 CSharp.UI.Text 同名。
namespace CSharp.UI.IntegrationTests;

/// <summary>
/// RL-03：真实 SkiaSharp/HarfBuzz 排版下的 Flow —— 换行文本按最终行宽重新排版并增高、
/// 纵向 ScrollView 的内容高度与滚动边界随宽度变化，以及缩窄后真实文字光标仍在可见区。
/// </summary>
public class FlowIntegrationTests
{
    private const string LongText =
        "Responsive flow verification paragraph. 响应式自动换行验证文本，用于确认分行后按最终行宽重新排版。"
        + "Second sentence keeps wrapping across several lines so shrinking changes the line count and height. "
        + "第三句继续增加内容，确保窄行宽下换行行数明显增多。";

    private static UiRuntime CreateRuntime(View root, Size viewport)
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

        runtime.SetViewport(viewport, 1.0);
        runtime.Start();
        runtime.ProcessPendingWork();
        return runtime;
    }

    [Fact]
    public void Flow_RemeasuresRealWrappedTextToLineWidth_AndGrowsHeight()
    {
        using var runtime = CreateRuntime(
            new VStack(
                new Flow(
                    new Text(LongText).Wrap(),
                    new Rectangle(Color.Blue) { FixedSize = new Size(90, 20) })
                    .Spacing(8)
                    .LineSpacing(6)),
            new Size(520, 600));

        var flowNode = runtime.Root.FindDescendant<Flow>()!;
        var textNode = flowNode.Children[0];
        var wideLines = ((TextLayoutResult)textNode.ViewCache!).Lines.Count;
        var wideHeight = flowNode.DesiredSize.Height;

        runtime.SetViewport(new Size(200, 600), 1.0);
        runtime.ProcessPendingWork();

        var narrowLines = ((TextLayoutResult)textNode.ViewCache!).Lines.Count;
        var narrowHeight = flowNode.DesiredSize.Height;

        // 真实排版：窄行宽下换行行数增加，Flow 内容高度随之增长。
        Assert.True(textNode.Frame.Width <= 200 + 0.01, $"文本宽度 {textNode.Frame.Width} 不应超过行宽 200。");
        Assert.True(narrowLines > wideLines, $"窄行宽行数 {narrowLines} 应多于 {wideLines}。");
        Assert.True(narrowHeight > wideHeight + 1, $"窄行宽内容高度 {narrowHeight} 应大于 {wideHeight}。");
        Assert.InRange(textNode.Frame.Height, ((TextLayoutResult)textNode.ViewCache!).Size.Height - 0.01, ((TextLayoutResult)textNode.ViewCache!).Size.Height + 1.01);

        // 纹理按新排版输出（不是缩放旧纹理）。
        var texture = Assert.Single(runtime.BuildFrame().Commands.OfType<TextureCommand>());
        Assert.InRange(texture.Bounds.Height, textNode.Frame.Height - 0.01, textNode.Frame.Height + 1.01);
    }

    [Fact]
    public void FlowInVerticalScrollView_ContentHeightAndScrollBoundsFollowWidth()
    {
        var flow = new Flow(
                new Text(LongText).Wrap(),
                new Rectangle(Color.Blue) { FixedSize = new Size(80, 20) })
            .Spacing(8)
            .LineSpacing(6);

        using var runtime = CreateRuntime(new ScrollView(flow), new Size(200, 120));

        var flowNode = runtime.Root.FindDescendant<Flow>()!;
        var narrowHeight = flowNode.DesiredSize.Height;
        Assert.True(narrowHeight > 120, $"内容高度 {narrowHeight} 应超出视口以产生滚动范围。");

        // 内容确实可滚动到边界。
        runtime.DispatchWheel(new WheelEvent(new Point(100, 60), 0, -50, KeyModifiers.None, WheelDeltaMode.Pixels));
        Assert.True(runtime.Root.ScrollOffsetY > 0, "内容高于视口时应能滚动。");

        // 展开后行数减少、内容高度下降，滚动偏移回落到范围内（可能为 0）。
        runtime.SetViewport(new Size(760, 120), 1.0);
        runtime.ProcessPendingWork();

        var wideHeight = flowNode.DesiredSize.Height;
        Assert.True(wideHeight < narrowHeight, $"宽行宽内容高度 {wideHeight} 应小于 {narrowHeight}。");
        Assert.InRange(runtime.Root.ScrollOffsetY, 0, Math.Max(0, flowNode.DesiredSize.Height - 120) + 0.01);
    }

    [Fact]
    public void FlowFillTextField_KeepsRealCaretVisibleAfterNarrowing()
    {
        var original = "The quick brown fox jumps over the lazy dog 0123456789";
        var value = original;
        var binding = Binding<string>.From(() => value, v => value = v);

        using var runtime = CreateRuntime(
            new VStack(new Flow(new TextField(binding).FillWidth())),
            new Size(500, 60));

        var node = runtime.Root.FindDescendant<TextField>()!;
        var field = (TextField)node.View;

        runtime.SetFocus(node);
        runtime.DispatchKey(new KeyEvent(KeyEventKind.Down, UiKey.Home, KeyModifiers.None));
        runtime.DispatchKey(new KeyEvent(KeyEventKind.Down, UiKey.End, KeyModifiers.Shift));

        var state = (TextFieldState)node.InteractionState!;
        Assert.True(state.HasSelection);
        Assert.Equal(original.Length, state.Caret);
        Assert.Equal(0, state.Anchor);

        var wideWidth = node.Frame.Width;
        Assert.Equal(500, wideWidth, 3);

        // 仅缩窄：光标、选区与绑定值必须保留。
        runtime.SetViewport(new Size(180, 60), 1.0);
        runtime.ProcessPendingWork();

        Assert.Equal(180, node.Frame.Width, 3);
        Assert.True(node.Frame.Width < wideWidth);
        Assert.Equal(original.Length, state.Caret);
        Assert.Equal(0, state.Anchor);
        Assert.True(state.HasSelection);
        Assert.Equal(original, value);

        var inner = InnerRect(node.Frame, field);

        // 与控件共用同一排版请求，取回真实 Skia 排版结果。
        var font = node.Environment.Font ?? FontDescription.Default;
        var layout = runtime.Services.TextService.Layout(
            TextPainter.CreateRequest(original, font, double.PositiveInfinity, runtime.PixelScale));

        var caretStop = layout.SnapToCaretStop(state.Caret);
        var caretScreenX = inner.X + layout.GetCaretX(caretStop) - state.ScrollX;
        Assert.InRange(caretScreenX, inner.Left - 0.5, inner.Right + 0.5);

        // 文本末端必须真的滚入可见区：只把矢量光标夹在边缘不算通过。
        Assert.InRange(layout.Size.Width - state.ScrollX, inner.Left, inner.Right + 1.5);

        var commands = runtime.BuildFrame().Commands;
        var caret = Assert.Single(
            commands.OfType<RectangleCommand>(),
            command => command.Bounds.Width <= 2.0 && command.Bounds.Height > 4);
        Assert.InRange(caret.Bounds.X, inner.Left - 0.5, inner.Right + 0.5);

        var texture = Assert.Single(commands.OfType<TextureCommand>());
        Assert.InRange(texture.Bounds.Right, inner.Right - 1.5, inner.Right + 1.5);
    }

    private static Rect InnerRect(Rect frame, TextField field)
        => frame
            .Deflate(new Thickness(field.HorizontalPadding, 0))
            .Deflate(new Thickness(0, 2));
}
