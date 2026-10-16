using System.Text;
using CSharp.UI.Samples.ControlGallery;
using Xunit;

namespace CSharp.UI.OpenTK.Tests;

/// <summary>
/// RL-08：在真实 <c>GalleryApp</c> 与 <c>NestedResponsiveExample</c> 上验证 DPI 1/1.25/1.5/2 下
/// 同一逻辑宽度得到同一布局选择与实际子控件坐标，文字纹理由实际 DPI 栅格化（不是缩放旧纹理），
/// 嵌套裁剪矩形在逻辑空间一致。
/// </summary>
/// <remarks>
/// 布局按父容器分配的逻辑宽度计算，理应与 DPI 无关；本回归把该契约固定在真实示例上：
/// 逻辑几何在所有 DPI 下必须逐节点一致，而纹理物理尺寸必须等于逻辑尺寸 × DPI。
/// </remarks>
public sealed class GalleryDpiLayoutTests
{
    private const double Epsilon = 0.001;

    private static readonly double[] DpiScales = [1.0, 1.25, 1.5, 2.0];

    private static readonly double[] LogicalWidths = [320, 480, 768, 1024];

    public static TheoryData<double> Widths()
    {
        var data = new TheoryData<double>();
        foreach (var width in LogicalWidths)
        {
            data.Add(width);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Widths))]
    public void 控件展示四档DPI下同一逻辑宽度的布局与坐标完全一致(double width)
    {
        using var baselineHost = new GalleryIntegrationHost(new GalleryApp(), new Size(width, 1600), 1.0);
        var baseline = LayoutSignature(baselineHost.Runtime);
        var baselineModes = BreakpointModes(baselineHost.Runtime);

        foreach (var scale in DpiScales)
        {
            using var host = new GalleryIntegrationHost(new GalleryApp(), new Size(width, 1600), scale);
            Assert.Equal(baseline, LayoutSignature(host.Runtime));
            Assert.Equal(baselineModes, BreakpointModes(host.Runtime));
        }
    }

    [Theory]
    [MemberData(nameof(Widths))]
    public void 控件展示四档DPI下文字纹理按DPI栅格化且原点吸附物理像素(double width)
    {
        foreach (var scale in DpiScales)
        {
            using var host = new GalleryIntegrationHost(new GalleryApp(), new Size(width, 1600), scale);
            var texts = host.Runtime.BuildFrame().Commands
                .OfType<TextureCommand>()
                .Where(command => command.Texture.IsAlphaMask)
                .ToArray();
            Assert.True(texts.Length >= 8, $"{width}@{scale}：真实页面应包含足量文字纹理，实际 {texts.Length}。");

            foreach (var text in texts)
            {
                // 原点必须落在整数物理像素上，否则线性过滤会二次混合整张遮罩。
                Assert.Equal(0, Math.IEEERemainder(text.Bounds.X * scale, 1), 6);
                Assert.Equal(0, Math.IEEERemainder(text.Bounds.Y * scale, 1), 6);

                // 1 纹素 = 1 物理像素：纹理像素数必须等于逻辑尺寸 × 当前 DPI，而不是复用另一档 DPI 的旧纹理。
                Assert.Equal(text.Texture.PixelWidth, text.Bounds.Width * scale, 6);
                Assert.Equal(text.Texture.PixelHeight, text.Bounds.Height * scale, 6);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Widths))]
    public void 控件展示内层滚动裁剪矩形在四档DPI下逻辑一致(double width)
    {
        Rect? baselineFrame = null;
        Rect? baselineClip = null;

        foreach (var scale in DpiScales)
        {
            using var host = new GalleryIntegrationHost(new GalleryApp(), new Size(width, 1600), scale);
            var scrollViews = host.Runtime.Root.FindDescendants<ScrollView>().ToArray();
            Assert.Equal(2, scrollViews.Length);
            var inner = scrollViews[1];
            Assert.True(
                inner.Children[0].DesiredSize.Height > inner.Frame.Height,
                $"{width}@{scale}：内层滚动内容应高于其视口。");

            // 内层 ScrollView 必须真的压入与自身视口一致的裁剪矩形（嵌套裁剪由真实命令表达）。
            var clip = Assert.Single(
                host.Runtime.BuildFrame().Commands.OfType<PushClipCommand>(),
                command => SameRect(command.Bounds, inner.Frame));

            if (baselineFrame is null)
            {
                baselineFrame = inner.Frame;
                baselineClip = clip.Bounds;
            }
            else
            {
                Assert.True(SameRect(baselineFrame.Value, inner.Frame), $"{width}@{scale}：内层视口逻辑矩形应跨 DPI 一致。");
                Assert.True(SameRect(baselineClip!.Value, clip.Bounds), $"{width}@{scale}：内层裁剪矩形应跨 DPI 一致。");
            }
        }
    }

    [Theory]
    [InlineData(1024)]
    [InlineData(768)]
    public void 嵌套响应式示例四档DPI下分支选择与布局一致(double width)
    {
        using var baselineHost = new GalleryIntegrationHost(new NestedResponsiveExample(), new Size(width, 900), 1.0);
        var baseline = LayoutSignature(baselineHost.Runtime);
        var baselineBranches = BranchTypes(baselineHost.Runtime);
        var baselineAdaptive = AdaptiveModes(baselineHost.Runtime);

        foreach (var scale in DpiScales)
        {
            using var host = new GalleryIntegrationHost(new NestedResponsiveExample(), new Size(width, 900), scale);
            Assert.Equal(baseline, LayoutSignature(host.Runtime));
            Assert.Equal(baselineBranches, BranchTypes(host.Runtime));
            Assert.Equal(baselineAdaptive, AdaptiveModes(host.Runtime));
        }
    }

    /// <summary>逐节点逻辑几何签名：帧矩形、期望尺寸、自适应模式与滚动偏移。</summary>
    private static string LayoutSignature(UiRuntime runtime)
    {
        var builder = new StringBuilder();
        foreach (var node in runtime.Root.Descendants())
        {
            builder.Append(node.DebugPath).Append('|')
                .Append(Format(node.Frame)).Append('|')
                .Append(Format(node.DesiredSize)).Append('|')
                .Append(node.AdaptiveIsHorizontal?.ToString() ?? "-").Append('|')
                .Append(node.ScrollOffsetY.ToString("0.###"))
                .AppendLine();
        }

        return builder.ToString();
    }

    /// <summary>每个 AdaptiveStack 的实际模式（宽/窄），跨 DPI 必须一致。</summary>
    private static string BreakpointModes(UiRuntime runtime)
        => string.Join(
            ";",
            runtime.Root.FindDescendants<AdaptiveStack>()
                .Select(node => $"{node.DebugPath}={node.AdaptiveIsHorizontal}"));

    /// <summary>每个 Responsive 当前激活的分支视图类型。</summary>
    private static string BranchTypes(UiRuntime runtime)
        => string.Join(
            ";",
            runtime.Root.FindDescendants<Responsive>()
                .Select(node => $"{node.DebugPath}={node.Children[0].View.GetType().Name}"));

    /// <summary>嵌套示例中每个 AdaptiveStack 的实际模式。</summary>
    private static string AdaptiveModes(UiRuntime runtime)
        => string.Join(
            ";",
            runtime.Root.FindDescendants<AdaptiveStack>()
                .Select(node => $"{node.DebugPath}={node.AdaptiveIsHorizontal}"));

    private static string Format(Rect rect)
        => $"{rect.X:0.###},{rect.Y:0.###},{rect.Width:0.###},{rect.Height:0.###}";

    private static string Format(Size size)
        => $"{size.Width:0.###}x{size.Height:0.###}";

    private static bool SameRect(Rect a, Rect b)
        => Math.Abs(a.X - b.X) < Epsilon
            && Math.Abs(a.Y - b.Y) < Epsilon
            && Math.Abs(a.Width - b.Width) < Epsilon
            && Math.Abs(a.Height - b.Height) < Epsilon;
}
