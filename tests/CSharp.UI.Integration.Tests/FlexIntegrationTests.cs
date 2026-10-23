using CSharp.UI;
using CSharp.UI.Skia;
using CSharp.UI.Testing;
using Xunit;

// 文本实现位于 CSharp.UI.Skia：命名空间不能与核心视图类型 CSharp.UI.Text 同名。
namespace CSharp.UI.IntegrationTests;

/// <summary>
/// RL-02：真实 SkiaSharp/HarfBuzz 排版下的弹性分配 —— 换行文本按最终宽度复测并增高，
/// TextField 缩窄后真实光标仍在可见区（不靠夹取矢量光标掩盖旧滚动偏移）。
/// </summary>
public class FlexIntegrationTests
{
    private const string LongText =
        "Responsive layout verification paragraph. 响应式布局验证文本，用于确认弹性分配后按最终宽度重新排版。"
        + "Second sentence keeps wrapping across several lines so shrinking changes the line count and height. "
        + "第三句继续增加内容，确保窄宽度下换行行数明显增多。";

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
    public void FlexShrink_ReWrapsRealTextAndGrowsHeight()
    {
        using var runtime = CreateRuntime(
            new VStack(
                new Text(LongText).Wrap().FillWidth(),
                new Rectangle(Color.Red).FillWidth())
                .Spacing(4),
            new Size(500, 400));

        var textNode = runtime.Root.FindDescendant<Text>()!;
        var wideLayout = (TextLayoutResult)textNode.ViewCache!;
        var wideHeight = textNode.Frame.Height;
        var wideTextureHeight = TextureCommand(runtime).Bounds.Height;

        runtime.SetViewport(new Size(160, 400), 1.0);
        runtime.ProcessPendingWork();

        var narrowLayout = (TextLayoutResult)textNode.ViewCache!;
        var narrowHeight = textNode.Frame.Height;
        var narrowTextureHeight = TextureCommand(runtime).Bounds.Height;

        // 分配后按有效宽度复测：换行行数增加、高度随之增长，且纹理按新排版输出（不是只压缩旧纹理）。
        Assert.Equal(160, textNode.Frame.Width, 3);
        Assert.True(narrowLayout.Lines.Count > wideLayout.Lines.Count);
        Assert.True(narrowHeight > wideHeight + 1, $"窄宽度高度 {narrowHeight} 应大于宽宽度 {wideHeight}。");
        Assert.InRange(narrowTextureHeight, narrowHeight - 0.01, narrowHeight + 1.01);
        Assert.True(narrowTextureHeight > wideTextureHeight + 1);
    }

    [Fact]
    public void FlexibleTextField_KeepsRealCaretVisibleAfterNarrowing()
    {
        var original = "The quick brown fox jumps over the lazy dog 0123456789";
        var value = original;
        var binding = Binding<string>.From(() => value, v => value = v);

        using var runtime = CreateRuntime(
            new HStack(new TextField(binding).Flex(basis: 420, grow: 1, shrink: 1, min: 60))
                .Align(Alignment.Stretch),
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

        // 不再按键，仅缩窄字段：光标与选区必须保留，绑定值不得被写入。
        runtime.SetViewport(new Size(200, 60), 1.0);
        runtime.ProcessPendingWork();

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

        // 真实光标位置 = 文本原点 + 排版光标位置 - 内部滚动偏移，必须落在可见区内。
        var caretStop = layout.SnapToCaretStop(state.Caret);
        var caretScreenX = inner.X + layout.GetCaretX(caretStop) - state.ScrollX;
        Assert.InRange(caretScreenX, inner.Left - 0.5, inner.Right + 0.5);

        // 文本末端也必须真的滚入可见区：仅把矢量光标夹在边缘掩盖旧 ScrollX 不算通过。
        Assert.InRange(layout.Size.Width - state.ScrollX, inner.Left, inner.Right + 1.5);

        // 真实光标矩形（含宽度）必须完整落在可见区内；滚动预算包含 caret 宽度与吸附余量，
        // 因此文本末端不再要求贴死右边缘，但文本仍必须确实滚入可见区（上面的末端检查）。
        Assert.True(caretScreenX + 1.5 <= inner.Right + 0.5, "真实排版 caret 矩形必须完整可见。");

        var commands = runtime.BuildFrame().Commands;
        var caret = Assert.Single(
            commands.OfType<RectangleCommand>(), command => command.Bounds.Width <= 2.0 && command.Bounds.Height > 4);
        Assert.InRange(caret.Bounds.X, inner.Left - 0.5, inner.Right + 0.5);

        // 文字纹理不得越过内容区右边界；末端贴边属于旧滚动预算，不再作为验收目标。
        var texture = Assert.Single(commands.OfType<TextureCommand>());
        Assert.True(texture.Bounds.Right <= inner.Right + 1.5);
    }

    [Fact]
    public void NestedFlex_PropagatesFinalWrappedHeightToPlainAncestors()
    {
        using var runtime = CreateRuntime(
            new ScrollView(new VStack(
                new HStack(
                    new Text(LongText).Wrap().Flex(basis: 300),
                    new Text("label").Frame(width: 60)),
                new Text("tail"))),
            new Size(180, 120));

        var stack = runtime.Root.FindDescendant<VStack>()!;
        var wrapped = stack.Children[0];
        var texts = runtime.Root.FindDescendants<Text>().ToList();
        var body = texts[0];
        var tail = texts[2];
        var layout = (TextLayoutResult)body.ViewCache!;

        // 内层弹性分配后按最终宽度（180-60=120）真实换行。
        Assert.Equal(120, body.Frame.Width, 3);
        Assert.True(layout.Lines.Count > 1, $"应真实换行，实际行数 {layout.Lines.Count}。");
        Assert.InRange(body.Frame.Height, layout.Size.Height - 0.01, layout.Size.Height + 1.01);

        // 普通 VStack 必须在有效复测之后重新汇总最终尺寸：内层换行高度要向上传递，
        // 而不是继续报告单行旧自然高度。
        Assert.Equal(body.Frame.Height, wrapped.DesiredSize.Height, 3);
        Assert.Equal(wrapped.DesiredSize.Height, tail.Frame.Y, 3);
        Assert.Equal(wrapped.DesiredSize.Height + tail.DesiredSize.Height, stack.DesiredSize.Height, 3);
        Assert.True(stack.DesiredSize.Height > 120, $"内容高度 {stack.DesiredSize.Height} 应超出视口以产生滚动范围。");

        // ScrollView 的滚动范围必须覆盖新的内容高度。
        runtime.DispatchWheel(new WheelEvent(new Point(90, 60), 0, -3, KeyModifiers.None, WheelDeltaMode.Steps));
        Assert.True(runtime.Root.ScrollOffsetY > 0, "内容变高后应能真正滚动。");
    }

    private static TextureCommand TextureCommand(UiRuntime runtime)
        => Assert.Single(runtime.BuildFrame().Commands.OfType<TextureCommand>());

    private static Rect InnerRect(Rect frame, TextField field)
        => frame
            .Deflate(new Thickness(field.HorizontalPadding, 0))
            .Deflate(new Thickness(0, 2));
}
