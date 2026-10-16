using CSharp.UI;
using CSharp.UI.Skia;
using CSharp.UI.Testing;
using Xunit;

// 文本实现位于 CSharp.UI.Skia：命名空间不能与核心视图类型 CSharp.UI.Text 同名。
namespace CSharp.UI.IntegrationTests;

/// <summary>
/// RL-01：探测测量不得把真实 Skia 排版缓存泄漏为最终布局提案，
/// 用真实文本几何与绘制命令（而不只是 DesiredSize/提案标记）验证。
/// </summary>
public class LayoutProbeIntegrationTests
{
    private static ViewNode Require<TView>(ViewNode root)
        where TView : View
        => root.FindDescendant<TView>() ?? throw new InvalidOperationException($"未找到 {typeof(TView).Name} 节点。");

    [Fact]
    public void ProbeOfRealText_DoesNotLeakLayoutCacheOrDrawGeometry()
    {
        var dispatcher = new HeadlessUiDispatcher();
        var text = new SkiaTextService();
        using var runtime = new UiRuntime(
            new VStack(new Text("响应式布局探测测量验证 ABCDEFGHIJKL").Wrap()).Padding(4),
            new UiRuntimeServices
            {
                Dispatcher = dispatcher,
                TextService = text,
                TextureService = new RecordingTextureService(),
                ImageService = null,
                Clipboard = new StubClipboardService(),
                Clock = new TestUiClock(),
            });

        runtime.SetViewport(new Size(240, 220), 1.0);
        runtime.Start();
        runtime.ProcessPendingWork();

        var textNode = Require<Text>(runtime.Root);
        var originalCache = textNode.ViewCache;
        Assert.IsType<TextLayoutResult>(originalCache);
        var originalConstraints = textNode.LastConstraints;
        var originalFrame = textNode.Frame;

        var originalBounds = runtime.BuildFrame().Commands
            .OfType<TextureCommand>()
            .Select(command => command.Bounds)
            .ToArray();
        Assert.NotEmpty(originalBounds);

        var context = new LayoutContext(runtime, runtime.Services.TextService, runtime.Services.ImageService, 1.0);

        // 嵌套探测 + 异常退出：探测期间的排版缓存、环境与提案状态都不得残留。
        Action probeThatThrows = () =>
        {
            using var outer = context.BeginProbe();
            context.Measure(textNode, LayoutConstraints.BoundedWidth(60));
            Assert.Equal(MeasureProposalKind.Probe, textNode.LastProposalKind);
            Assert.NotEqual(((TextLayoutResult)originalCache!).Size, textNode.DesiredSize);

            using var inner = context.BeginProbe();
            context.Measure(textNode, LayoutConstraints.BoundedWidth(30));
            Assert.Equal(MeasureProposalKind.Probe, textNode.LastProposalKind);

            throw new InvalidOperationException("探测期间异常退出");
        };

        Assert.Throws<InvalidOperationException>(probeThatThrows);

        Assert.False(context.IsProbing);
        Assert.Same(originalCache, textNode.ViewCache);
        Assert.Equal(originalConstraints, textNode.LastConstraints);
        Assert.Equal(originalFrame, textNode.Frame);

        // 普通 VStack 的子项只经过自然尺寸测量，恢复的是 Intrinsic 提案（不是最终有效提案）。
        Assert.Equal(MeasureProposalKind.Intrinsic, textNode.LastProposalKind);

        // 下一帧必须复用进入探测前的排版几何，不得按探测宽度（30/60）绘制。
        var afterBounds = runtime.BuildFrame().Commands
            .OfType<TextureCommand>()
            .Select(command => command.Bounds)
            .ToArray();
        Assert.Equal(originalBounds, afterBounds);
    }
}
