using CSharp.UI;
using CSharp.UI.Skia;
using CSharp.UI.Testing;
using Xunit;

// 文本实现位于 CSharp.UI.Skia：命名空间不能与核心视图类型 CSharp.UI.Text 同名。
namespace CSharp.UI.IntegrationTests;

/// <summary>
/// RL-05：真实 SkiaSharp/HarfBuzz 排版下 <see cref="Responsive"/> 的安全内容构建 ——
/// 以父级最终有效宽度重建内容、换行高度向上重新汇总，滚动范围沿用新尺寸。
/// </summary>
public class ResponsiveIntegrationTests
{
    private const string LongText =
        "Responsive layout verification paragraph. 响应式布局验证文本，用于确认响应式内容按最终宽度重新排版。"
        + "Second sentence keeps wrapping across several lines so narrowing changes the line count and height. "
        + "第三句继续增加内容，确保窄宽度下换行行数明显增多。";

    private static UiRuntime CreateRuntime(View root, Size viewport)
    {
        var runtime = new UiRuntime(root, new UiRuntimeServices
        {
            Dispatcher = new HeadlessUiDispatcher(),
            TextService = new SkiaTextService(),
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
    public void Responsive_ReBuildsRealTextAtFinalWidthAndPropagatesWrappedHeightToScrollRange()
    {
        var widths = new List<double>();

        using var runtime = CreateRuntime(
            new ScrollView(new VStack(
                new Responsive(info =>
                {
                    widths.Add(info.AvailableWidth);
                    return new Text(LongText).Wrap();
                }),
                new Text("tail"))),
            new Size(300, 120));

        var stack = runtime.Root.FindDescendant<VStack>()!;
        var texts = runtime.Root.FindDescendants<Text>().ToList();
        var body = texts[0];
        var tail = texts[1];

        // 首帧：按父级可用宽度 300 真实排版换行，内容完整（不是空帧）。
        Assert.Equal<double>(new[] { 300d }, widths);
        var wideLayout = (TextLayoutResult)body.ViewCache!;
        var wideLines = wideLayout.Lines.Count;
        Assert.True(wideLines > 1, $"宽布局应真实换行，实际行数 {wideLines}。");
        Assert.Equal(wideLayout.Size.Height, tail.Frame.Y, 3);

        // 变窄：响应式内容按 180 重建，真实排版行数增加、高度增长。
        runtime.SetViewport(new Size(180, 120), 1.0);
        runtime.ProcessPendingWork();

        Assert.Equal(180, widths[^1], 3);
        var narrowLayout = (TextLayoutResult)body.ViewCache!;
        Assert.True(
            narrowLayout.Lines.Count > wideLines,
            $"窄宽度行数 {narrowLayout.Lines.Count} 应多于宽宽度 {wideLines}。");
        Assert.Equal(180, body.Frame.Width, 3);
        Assert.InRange(body.Frame.Height, narrowLayout.Size.Height - 0.01, narrowLayout.Size.Height + 1.01);

        // 最终子项尺寸改变后必须向上重新汇总自然高度，尾部位置与滚动内容高度都跟着更新。
        Assert.Equal(body.Frame.Height, tail.Frame.Y, 3);
        Assert.Equal(stack.DesiredSize.Height, tail.Frame.Bottom, 3);
        Assert.True(stack.DesiredSize.Height > 120, $"内容高度 {stack.DesiredSize.Height} 应超出视口。");

        // 滚动范围沿用新尺寸：能滚动到新内容高度。
        runtime.DispatchWheel(new WheelEvent(new Point(90, 60), 0, -3, KeyModifiers.None, WheelDeltaMode.Steps));
        Assert.True(runtime.Root.ScrollOffsetY > 0, "内容变高后应能真正滚动到新范围。");
    }

    [Fact]
    public void Responsive_BranchSwap_UsesRealContentAndKeepsOuterBinding()
    {
        var widths = new List<double>();

        using var runtime = CreateRuntime(
            new ScrollView(new VStack(
                new Responsive(info =>
                {
                    widths.Add(info.AvailableWidth);
                    return info.AvailableWidth < 200
                        ? new Text("narrow").Wrap()
                        : new Text(LongText).Wrap();
                }),
                new Text("tail"))),
            new Size(300, 200));

        var body = runtime.Root.FindDescendants<Text>().First();
        var wideHeight = body.Frame.Height;

        runtime.SetViewport(new Size(160, 200), 1.0);
        runtime.ProcessPendingWork();

        Assert.Equal(160, widths[^1], 3);
        var narrowBody = runtime.Root.FindDescendants<Text>().First();
        Assert.Equal("narrow", Assert.IsType<Text>(narrowBody.View).Content);
        Assert.True(narrowBody.Frame.Height < wideHeight, "窄分支内容更少，高度应变小。");
        Assert.Equal(narrowBody.Frame.Height, runtime.Root.FindDescendants<Text>().Last().Frame.Y, 3);
    }

    [Fact]
    public void Responsive_BuilderStateRead_ReBuildsRealTextAndUpdatesScrollRange()
    {
        var host = new StateDrivenHost();
        using var runtime = CreateRuntime(host, new Size(300, 120));

        var body = runtime.Root.FindDescendants<Text>().First();
        Assert.Equal(StateDrivenHost.LongText, Assert.IsType<Text>(body.View).Content);
        var longLayout = (TextLayoutResult)body.ViewCache!;
        Assert.True(longLayout.Lines.Count > 1, $"宽布局应真实换行，实际行数 {longLayout.Lines.Count}。");

        // builder 直接读取状态、外层组件不读取：内容仍必须按新文本重新排版并向上汇总高度。
        host.Text!.Value = "short";
        runtime.ProcessPendingWork();

        var updated = runtime.Root.FindDescendants<Text>().First();
        Assert.Same(body, updated);
        Assert.Equal("short", Assert.IsType<Text>(updated.View).Content);
        Assert.Single(((TextLayoutResult)updated.ViewCache!).Lines);
        Assert.True(updated.Frame.Height < longLayout.Size.Height, "换成短文本后高度应变小。");
        Assert.Equal(1, host.Builds);
        Assert.Equal(updated.Frame.Height, runtime.Root.FindDescendants<Text>().Last().Frame.Y, 3);
    }

    /// <summary>builder 直接读取文本状态；外层组件不读取它。</summary>
    private sealed class StateDrivenHost : Component
    {
        public const string LongText =
            "State driven responsive paragraph. 状态驱动的响应式段落，用于确认 builder 读取的状态变化后真实重新排版。"
            + "Second sentence keeps wrapping across several lines so the height changes when the text becomes short. "
            + "第三句继续增加内容，确保宽布局下换行行数明显多于一行。";

        public State<string>? Text { get; private set; }

        public int Builds { get; private set; }

        protected override View Build()
        {
            Builds++;
            Text = UseState("text", LongText);
            return new ScrollView(new VStack(
                new Responsive(_ => new Text(Text.Value).Wrap()),
                new Text("tail")));
        }
    }
}
