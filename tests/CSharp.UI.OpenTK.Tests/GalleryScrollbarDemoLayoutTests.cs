using CSharp.UI.Samples.ControlGallery;
using CSharp.UI.Testing;
using Xunit;

namespace CSharp.UI.OpenTK.Tests;

/// <summary>
/// 控件展示“滚动条”一节的演示视口宽度回归：演示 ScrollView 必须占满所在区块的可用宽度，
/// 使 Standard gutter 与 Floating overlay 条都落在视口右缘、远离短文本行尾。
/// </summary>
/// <remarks>
/// 2026-10-25 用户反馈：Floating 模式下灰色 thumb 直接压在“纵向滚动第 X 行”末尾字形上。
/// 根因是演示 ScrollView 没有宽度声明，Floating 0 gutter 时视口收缩到内容自然宽度，
/// 右缘恰好贴在每行文本末尾（Standard 的 12px persistent gutter 槽是空白掩盖了同一窄视口问题）。
/// 本测试用真实 <see cref="GalleryApp"/> + 真实排版断言：视口右缘与区块内其它占满宽度的
/// 说明文本右缘一致，且 Floating 显示时 thumb 不覆盖短行文本的可见字形区域。
/// 断言面向消费者可观察的几何（frame 关系），不断言 .FillWidth 修饰符的存在或默认宽度字面值。
/// </remarks>
public sealed class GalleryScrollbarDemoLayoutTests
{
    private const double Epsilon = 0.001;

    /// <summary>演示视口在 Standard 与 Floating 模式下都占满区块可用宽度（与同行说明文本右缘一致）。</summary>
    [Theory]
    [InlineData(320)]
    [InlineData(1024)]
    public void 滚动条演示视口在两种样式下都占满区块可用宽度(double viewportWidth)
    {
        using var host = new GalleryIntegrationHost(new GalleryApp(), new Size(viewportWidth, 800), 1.0);
        BringSectionIntoView(host);

        var demo = FindByKey(host, "scrollbar-vertical-demo");
        var labelRight = SectionLabelRight(host, demo);

        // Standard：视口右缘 = 区块内容右缘（gutter 在 frame 内）。
        AssertFrameRight(demo, labelRight);

        // 真实切换：点击示例自己的“浮动滚动条”开关（通过 state 驱动的模式变化，不私改节点）。
        var toggle = FindToggle(host, "浮动滚动条");
        ScrollNodeIntoView(host, toggle);
        toggle = FindToggle(host, "浮动滚动条");
        host.Click(toggle.Frame.Center);

        demo = FindByKey(host, "scrollbar-vertical-demo");
        AssertFrameRight(demo, labelRight);
    }

    /// <summary>Floating 条显示时落在视口右缘、不覆盖短文本行的可见字形区域（用户截图场景）。</summary>
    [Theory]
    [InlineData(320)]
    [InlineData(1024)]
    public void Floating滚动条显示时不覆盖演示短行文本(double viewportWidth)
    {
        using var host = new GalleryIntegrationHost(new GalleryApp(), new Size(viewportWidth, 800), 1.0);
        BringSectionIntoView(host);

        var toggle = FindToggle(host, "浮动滚动条");
        ScrollNodeIntoView(host, toggle);
        toggle = FindToggle(host, "浮动滚动条");
        host.Click(toggle.Frame.Center);

        var demo = FindByKey(host, "scrollbar-vertical-demo");
        ScrollNodeIntoView(host, demo);
        demo = FindByKey(host, "scrollbar-vertical-demo");

        // 真实滚轮让 Floating bar 显示；窄视口下演示底缘可能仍略出屏，
        // 用视口内可见区域内的真实命中点投递。
        var wheelAt = new Point(
            demo.Frame.X + 10,
            Math.Min(demo.Frame.Center.Y, GalleryScrollLookup.RootScroll(host.Runtime).Frame.Bottom - 10));
        host.Wheel(wheelAt, -1);

        // 断言真实交互合约而不是内部状态位/几何常数：可见 overlay 命中区
        // （HitTestOverlay 是 Floating bar 可交互时的权威 rect，hidden 时返回 null）。
        var overlay = demo.View.HitTestOverlay(demo);
        Assert.True(overlay.HasValue, "实际滚动后 Floating bar 应处于可交互 overlay 状态。");
        var bar = overlay!.Value;
        Assert.True(ReferenceEquals(demo, host.Runtime.HitTest(bar.Center)), "overlay 中心应命中演示 ScrollView 自己。");

        // 用户截图场景：可见短行文本的字形右缘必须落在 overlay 左缘之左（真实空隙，不 pin 4px 常量）。
        var widestTextRight = 0.0;
        var visibleRows = 0;
        foreach (var row in demo.Children[0].Descendants())
        {
            if (row.View is not Text)
            {
                continue;
            }

            if (row.Frame.Bottom <= demo.Frame.Top || row.Frame.Top >= demo.Frame.Bottom)
            {
                continue; // 视口外被裁剪的行不参与可见性断言。
            }

            visibleRows++;
            widestTextRight = Math.Max(widestTextRight, row.Frame.X + row.DesiredSize.Width);
        }

        Assert.True(visibleRows > 0, "视口内应至少有一行可见文本参与字形右缘断言。");
        Assert.True(
            widestTextRight < bar.Left,
            $"Floating overlay（左缘 {bar.Left:0.###}）压到了可见文本行右缘 {widestTextRight:0.###}——" +
            "视口未占满区块宽度，回到了用户截图中的字尾遮挡场景。");
    }

    /// <summary>横向演示视口同样占满区块可用宽度（thumb 轨道在底缘）。</summary>
    [Theory]
    [InlineData(320)]
    [InlineData(1024)]
    public void 横向滚动条演示视口占满区块可用宽度(double viewportWidth)
    {
        using var host = new GalleryIntegrationHost(new GalleryApp(), new Size(viewportWidth, 800), 1.0);
        BringSectionIntoView(host);

        // 纵向演示与横向演示同在“滚动条”区块；用纵向的右缘作为区块右缘参照。
        var verticalDemo = FindByKey(host, "scrollbar-vertical-demo");
        var horizontal = FindHorizontalDemo(host);

        Assert.Equal(verticalDemo.Frame.Right, horizontal.Frame.Right, 1);
    }

    // ------------------------------------------------------------ helpers

    /// <summary>按示例注入的 Key 定位节点。</summary>
    private static ViewNode FindByKey(GalleryIntegrationHost host, string key)
        => host.Runtime.Root.Descendants()
            .First(n => n.HasIdentityKey && key.Equals(n.IdentityKey as string, StringComparison.Ordinal));

    /// <summary>按标签找“浮动滚动条”开关（示例中唯一该标签 Toggle）。</summary>
    private static ViewNode FindToggle(GalleryIntegrationHost host, string label)
        => host.Runtime.Root.Descendants()
            .First(n => n.View is Toggle t && t.Label == label);

    /// <summary>“滚动条”区块内没有 Key 的横向演示 ScrollView：整页唯一的横向轴 ScrollView。</summary>
    private static ViewNode FindHorizontalDemo(GalleryIntegrationHost host)
    {
        var scrolls = host.Runtime.Root.FindDescendants<ScrollView>()
            .Where(n => ((ScrollView)n.View).Axis == ScrollAxis.Horizontal)
            .ToList();
        // 页面只有这一个横向 ScrollView 演示。
        return Assert.Single(scrolls);
    }

    /// <summary>沿父链找到承载区块内容的 VStack 容器（跳过 Key/Frame/FillWidth 等修饰符节点）。</summary>
    private static ViewNode SectionContentStack(ViewNode demo)
    {
        for (var node = demo.Parent; node is not null; node = node.Parent)
        {
            if (node.View is VStack)
            {
                return node;
            }
        }

        throw new InvalidOperationException("演示视口的父链上找不到区块内容 VStack。");
    }

    /// <summary>同一“滚动条”区块内说明文本行（Wrap 占满区块宽）的最右缘——区块可用宽度的真实代理。</summary>
    private static double SectionLabelRight(GalleryIntegrationHost host, ViewNode demo)
    {
        // 演示 ScrollView 外层是 Key/Frame/FillWidth 修饰符；区块内容 VStack 是最近的 VStack 祖先。
        var section = SectionContentStack(demo);
        var right = 0.0;
        foreach (var sibling in section.Children)
        {
            if (sibling.View is not Text)
            {
                continue;
            }

            right = Math.Max(right, sibling.Frame.Right);
        }

        Assert.True(right > demo.Frame.X, "区块内应存在占满宽度的说明文本作为右缘参照。");
        return right;
    }

    private static void AssertFrameRight(ViewNode demo, double expectedRight)
        => Assert.True(
            Math.Abs(demo.Frame.Right - expectedRight) <= 1,
            $"演示视口右缘 {demo.Frame.Right:0.###} 应与区块可用宽右缘 {expectedRight:0.###} 一致" +
            "（收缩到内容自然宽会让 overlay/gutter 压到行尾文本）。");

    /// <summary>把“滚动条”区块滚进整页视口（示例内容很长，演示在首屏之下）。</summary>
    private static void BringSectionIntoView(GalleryIntegrationHost host)
    {
        var root = GalleryScrollLookup.RootScroll(host.Runtime);
        var demo = FindByKey(host, "scrollbar-vertical-demo");

        // 逐格外层滚动直到演示视口进入页面视口内（真实滚轮路径，边界自动停）。
        var guard = 0;
        while (demo.Frame.Top >= root.Frame.Bottom - 10 && guard++ < 200)
        {
            var before = root.ScrollOffsetY;
            WheelRoot(host, -1);
            if (Math.Abs(root.ScrollOffsetY - before) < Epsilon)
            {
                break; // 已到底。
            }

            demo = FindByKey(host, "scrollbar-vertical-demo");
        }
    }

    /// <summary>若目标节点在当前页面视口之外，逐格滚外层直到它的可见部分最大化（位置每步重读）。</summary>
    /// <remarks>目标高于视口时滚到顶/底即停——不苛求整帧入屏，避免窄视口下死循环。</remarks>
    private static void ScrollNodeIntoView(GalleryIntegrationHost host, ViewNode target)
    {
        var root = GalleryScrollLookup.RootScroll(host.Runtime);
        var guard = 0;
        while ((target.Frame.Top < root.Frame.Top || target.Frame.Bottom > root.Frame.Bottom + Epsilon)
               && guard++ < 200)
        {
            var before = root.ScrollOffsetY;
            WheelRoot(host, target.Frame.Bottom > root.Frame.Bottom ? -1 : 1);
            if (Math.Abs(root.ScrollOffsetY - before) < Epsilon)
            {
                break; // 外层到边界仍不可见（目标被裁出视口外），交给断言报告。
            }
        }

        Assert.True(
            target.Frame.Bottom > root.Frame.Top && target.Frame.Top < root.Frame.Bottom,
            $"滚动后目标 {target.DebugPath} frame={target.Frame} 应至少有可见部分落在页面视口 {root.Frame} 内。");
    }

    /// <summary>在外层根滚动的真实 gutter 命中点上滚一格——不会被内层内容/演示视口消费。</summary>
    private static void WheelRoot(GalleryIntegrationHost host, double notches)
    {
        var root = GalleryScrollLookup.RootScroll(host.Runtime);
        var at = new Point(root.Frame.Right - 6, root.Frame.Center.Y);
        Assert.True(ReferenceEquals(root, host.Runtime.HitTest(at)), "根滚动 gutter 点应命中根 ScrollView 自身（Standard 槽）。");
        host.Wheel(at, notches);
    }
}
