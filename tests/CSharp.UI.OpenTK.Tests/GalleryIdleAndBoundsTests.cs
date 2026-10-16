using CSharp.UI.Samples.ControlGallery;
using Xunit;

namespace CSharp.UI.OpenTK.Tests;

/// <summary>
/// RL-08：在真实示例上验证静止时不再布局/构建/重绘，连续宽度变化的布局/构建/纹理缓存有界，
/// 以及极窄与无界容器具有确定的几何和诊断。
/// </summary>
public sealed class GalleryIdleAndBoundsTests
{
    [Fact]
    public void 静止时不再布局构建或重复上传纹理()
    {
        using var host = new GalleryIntegrationHost(new GalleryApp(), new Size(900, 700), 1.5);
        host.Runtime.BuildFrame();

        var builds = host.Runtime.BuildCount;
        var layouts = host.Runtime.LayoutPassCount;
        var uploads = host.Textures.FactoryInvocations;
        Assert.False(host.Runtime.NeedsDraw, "生成一帧后不应仍处于需要重绘状态。");

        // 没有任何失效时，重复处理待办工作不得重新测量、布局或重绘。
        for (var i = 0; i < 5; i++)
        {
            host.Runtime.ProcessPendingWork();
        }

        Assert.Equal(builds, host.Runtime.BuildCount);
        Assert.Equal(layouts, host.Runtime.LayoutPassCount);
        Assert.False(host.Runtime.NeedsDraw);

        // 再次生成帧：布局未失效，纹理全部命中缓存，不重复上传。
        host.Runtime.BuildFrame();
        Assert.Equal(layouts, host.Runtime.LayoutPassCount);
        Assert.Equal(uploads, host.Textures.FactoryInvocations);
    }

    [Fact]
    public void 连续宽度变化的布局测量与纹理缓存有界()
    {
        using var host = new GalleryIntegrationHost(new GalleryApp(), new Size(300, 700), 1.0);
        host.Runtime.BuildFrame();

        var builds = host.Runtime.BuildCount;
        var layoutsBefore = host.Runtime.LayoutPassCount;
        var maxMeasures = 0;
        var maxLiveTextures = 0;
        var widths = 0;

        // 连续跨断点（560 / 840）宽度扫描：每一步最多触发一轮布局，测量工作量与纹理存活数保持有界。
        for (var width = 300.0; width <= 1100.0; width += 20.0)
        {
            widths++;
            var layoutsBeforeStep = host.Runtime.LayoutPassCount;
            host.Resize(new Size(width, 700), 1.0);
            host.Runtime.BuildFrame();

            var passes = host.Runtime.LayoutPassCount - layoutsBeforeStep;
            Assert.InRange(passes, 0, 2);
            maxMeasures = Math.Max(maxMeasures, host.Runtime.LastMeasureCount);
            maxLiveTextures = Math.Max(maxLiveTextures, host.Textures.LiveTextureCount);
        }

        Assert.True(widths >= 40, $"应覆盖足够多的宽度步数，实际 {widths}。");

        // 纯宽度变化只重新布局，不重建组件（示例没有响应式内容）。
        Assert.Equal(builds, host.Runtime.BuildCount);
        Assert.True(host.Runtime.LayoutPassCount - layoutsBefore <= widths, "每步布局轮数不得超过步数。");
        Assert.True(maxMeasures > 0 && maxMeasures < 1000, $"单轮测量量应有界，实际最大 {maxMeasures}。");

        // 纹理容量是长期目标：帧引用保护期间可临时超出，帧边界回收后必须回落到容量以内，且不得无限增长。
        host.Runtime.BuildFrame();
        host.Runtime.BuildFrame();
        var settledTextures = host.Textures.LiveTextureCount;
        Assert.True(
            settledTextures <= host.Textures.Capacity,
            $"帧边界回收后存活纹理应回到容量目标内：{settledTextures} > {host.Textures.Capacity}。");
        Assert.True(settledTextures <= maxLiveTextures, "回收后存活纹理不得高于过程峰值。");

        Console.WriteLine(
            $"[RL-08] 连续 {widths} 档宽度：最大测量 {maxMeasures}，存活纹理峰值 {maxLiveTextures}，回落 {settledTextures}/{host.Textures.Capacity}，" +
            $"构建 {host.Runtime.BuildCount}，布局轮 {host.Runtime.LayoutPassCount}");
    }

    [Fact]
    public void 极窄视口有确定几何与溢出诊断()
    {
        using var host = new GalleryIntegrationHost(new GalleryApp(), new Size(80, 700), 1.0);

        foreach (var node in host.Runtime.Root.Descendants())
        {
            Assert.True(double.IsFinite(node.Frame.X), $"{node.DebugPath} 的 X 非有限值：{node.Frame}。");
            Assert.True(double.IsFinite(node.Frame.Y), $"{node.DebugPath} 的 Y 非有限值：{node.Frame}。");
            Assert.True(node.Frame.Width >= 0, $"{node.DebugPath} 出现负宽度：{node.Frame}。");
            Assert.True(node.Frame.Height >= 0, $"{node.DebugPath} 出现负高度：{node.Frame}。");
            Assert.True(node.Frame.Right <= 80 + 0.01, $"{node.DebugPath} 越过极窄视口：{node.Frame}。");
        }

        // 硬固定内容（96×96 图片、140 宽的 ZStack 文本、maxWidth 演示）超出父级可用宽度时必须有明确诊断。
        Assert.Contains(host.Runtime.Diagnostics, diagnostic => diagnostic.Contains("布局溢出"));
    }

    [Fact]
    public void 无界宽度下响应式与自适应有确定回退()
    {
        // 横向滚动容器给内容无界宽度：Responsive 必须收到 IsWidthBounded=false，
        // AdaptiveStack 必须按横向自然布局处理（不读取窗口或 DPI）。
        var observedBounded = true;
        var observedWidth = -1.0;

        using var host = new GalleryIntegrationHost(
            new ScrollView(
                new VStack(
                    new Responsive(info =>
                    {
                        observedBounded = info.IsWidthBounded;
                        observedWidth = info.AvailableWidth;
                        return new Text(info.IsWidthBounded ? "有界回退" : "无界回退");
                    }),
                    new AdaptiveStack(560, new Text("a"), new Text("b"))),
                ScrollAxis.Horizontal),
            new Size(400, 400),
            1.0);

        foreach (var node in host.Runtime.Root.Descendants())
        {
            Assert.True(double.IsFinite(node.Frame.X) && double.IsFinite(node.Frame.Y), $"{node.DebugPath} 几何非有限：{node.Frame}。");
            Assert.True(node.Frame.Width >= 0 && node.Frame.Height >= 0, $"{node.DebugPath} 出现负尺寸：{node.Frame}。");
        }

        Assert.False(observedBounded, "无界宽度必须以 IsWidthBounded=false 表达。");
        Assert.True(double.IsPositiveInfinity(observedWidth), $"无界宽度必须用 PositiveInfinity 表达，实际 {observedWidth}。");

        var branch = host.Runtime.Root.FindDescendant<Responsive>()!.Children[0].View;
        Assert.Equal("无界回退", Assert.IsType<Text>(branch).Content);

        var adaptive = host.Runtime.Root.FindDescendant<AdaptiveStack>()!;
        Assert.True(adaptive.AdaptiveIsHorizontal, "无界宽度应按横向自然布局处理。");
    }

    [Fact]
    public void 嵌套容器极窄时结构收敛且有诊断()
    {
        using var host = new GalleryIntegrationHost(new NestedResponsiveExample(), new Size(120, 500), 1.0);

        foreach (var node in host.Runtime.Root.Descendants())
        {
            Assert.True(double.IsFinite(node.Frame.Width) && double.IsFinite(node.Frame.Height), $"{node.DebugPath} 几何非有限：{node.Frame}。");
            Assert.True(node.Frame.Width >= 0 && node.Frame.Height >= 0, $"{node.DebugPath} 出现负尺寸：{node.Frame}。");
        }

        // 两个面板都低于 480 阈值，选择窄分支；硬固定的 180×64 内层滚动视口在 120 宽父级内溢出并诊断。
        Assert.All(
            host.Runtime.Root.FindDescendants<Responsive>(),
            node => Assert.IsType<VStack>(node.Children[0].View));
        Assert.Contains(host.Runtime.Diagnostics, diagnostic => diagnostic.Contains("布局溢出"));
    }
}
