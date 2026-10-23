using CSharp.UI;
using CSharp.UI.Samples.ControlGallery;
using CSharp.UI.Skia;
using CSharp.UI.Testing;
using Xunit;

namespace CSharp.UI.OpenTK.Tests;

/// <summary>
/// RL-07：直接实例化真实控件展示示例，验证 320/480/768/1024 逻辑宽度下的可用性、
/// 纵向滚动范围，以及主题/计数/开关/滑块/中文输入等交互仍由父容器分配空间驱动。
/// </summary>
/// <remarks>
/// 布局与控件都是示例真实实现，只替换平台服务（文本用真实 SkiaSharp，纹理/图片/时钟用替身）；
/// 不复制简化视图树，不读取窗口全局尺寸。
/// </remarks>
public sealed class GalleryResponsiveTests
{
    private const double Epsilon = 0.01;

    private static readonly double[] CommonWidths = [320, 480, 768, 1024];

    public static TheoryData<double> Widths()
    {
        var data = new TheoryData<double>();
        foreach (var width in CommonWidths)
        {
            data.Add(width);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Widths))]
    public void 控件展示在常用逻辑宽度下无横向溢出且可纵向滚动到底(double width)
    {
        using var runtime = CreateRuntime(new GalleryApp(), new Size(width, 300));

        var leaves = runtime.Root.Descendants()
            .Where(node => node.View is Button or Toggle or Slider or TextField or Image or Text)
            .ToArray();
        Assert.NotEmpty(leaves);

        foreach (var leaf in leaves)
        {
            Assert.True(double.IsFinite(leaf.Frame.Width), $"{leaf.View.DebugName} 的宽度不是有限值：{leaf.Frame}。");
            Assert.True(leaf.Frame.Width >= 0, $"{leaf.View.DebugName} 出现负宽度：{leaf.Frame}。");
            Assert.True(leaf.Frame.Left >= -Epsilon, $"{leaf.View.DebugName} 越过左边界：{leaf.Frame}。");
            // 横向 ScrollView 内容可宽于视口（由 ScrollView 裁剪）；其他叶子不得越过右边界。
            var inHorizontalScroll = false;
            for (var p = leaf.Parent; p is not null; p = p.Parent)
            {
                if (p.View is ScrollView s && s.Axis == ScrollAxis.Horizontal)
                {
                    inHorizontalScroll = true;
                    break;
                }
            }
            if (!inHorizontalScroll)
            {
                Assert.True(
                    leaf.Frame.Right <= width + Epsilon,
                    $"{leaf.View.DebugName} 越过 {width} 逻辑宽度右边界：{leaf.Frame}。");
            }
        }

        // 通过示例注入的 Key 定位外层整页与"滚动容器"内层。
        var outer = GalleryScrollLookup.RootScroll(runtime);
        var inner = GalleryScrollLookup.NestedScroll(runtime);
        Assert.Contains(inner, outer.Descendants());
        Assert.True(
            inner.Children[0].DesiredSize.Height > inner.Frame.Height,
            "内层滚动内容应高于 120 逻辑像素视口。");

        // 纵向滚动可到达页面内容末尾：偏移被夹紧到内容高度与视口之差。
        var maxOffset = Math.Max(0, outer.Children[0].DesiredSize.Height - outer.Frame.Height);
        Assert.True(maxOffset > Epsilon, $"{width} 宽度下页面内容应高于 300 逻辑像素视口。");
        runtime.DispatchWheel(new WheelEvent(
            new Point(outer.Frame.X + 8, outer.Frame.Y + 6),
            0,
            -100000,
            KeyModifiers.None,
            WheelDeltaMode.Pixels));
        runtime.ProcessPendingWork();
        Assert.Equal(maxOffset, outer.ScrollOffsetY, 3);

        // 到达底部后最后一个可见文本仍在整页视口内。
        var last = leaves[^1];
        Assert.True(
            last.Frame.Bottom <= outer.Frame.Bottom + Epsilon,
            $"滚动到底后 {last.View.DebugName} 应位于整页视口内：{last.Frame}，视口 {outer.Frame}。");
    }

    [Theory]
    [MemberData(nameof(Widths))]
    public void 控件展示的真实文本按容器宽度换行与省略(double width)
    {
        using var runtime = CreateRuntime(new GalleryApp(), new Size(width, 700));

        var wrap = runtime.Root.FindDescendants<Text>()
            .Single(node => ((Text)node.View).Content.StartsWith("有限宽度内自动换行", StringComparison.Ordinal));
        var wrapLayout = Assert.IsType<TextLayoutResult>(wrap.ViewCache);
        Assert.True(wrapLayout.Lines.Count > 1, $"{width} 宽度下换行演示应真实折行，实际 {wrapLayout.Lines.Count} 行。");
        Assert.True(wrap.Frame.Width <= 360 + Epsilon, $"换行演示宽度不应超过 maxWidth 360：{wrap.Frame}。");
        Assert.True(wrap.Frame.Width <= width + Epsilon);

        var ellipsis = runtime.Root.FindDescendants<Text>()
            .Single(node => ((Text)node.View).Content.StartsWith("超出可用宽度时以省略号截断", StringComparison.Ordinal));
        var ellipsisLayout = Assert.IsType<TextLayoutResult>(ellipsis.ViewCache);
        Assert.Single(ellipsisLayout.Lines);
        Assert.True(ellipsis.Frame.Width <= 320 + Epsilon, $"省略演示宽度不应超过 maxWidth 320：{ellipsis.Frame}。");
        Assert.True(
            ellipsisLayout.Size.Width <= ellipsis.Frame.Width + Epsilon,
            "省略排版宽度不得超过容器实际宽度。");
    }

    [Theory]
    [MemberData(nameof(Widths))]
    public void 控件展示主题按钮在常用宽度下可见且可切换主题(double width)
    {
        using var runtime = CreateRuntime(new GalleryApp(), new Size(width, 400));

        var dark = runtime.Root.FindDescendants<Button>()
            .Single(node => ((Button)node.View).Label == "暗色主题");
        Assert.True(dark.Frame.Left >= -Epsilon && dark.Frame.Right <= width + Epsilon, $"主题按钮应在视口内：{dark.Frame}。");

        Click(runtime, dark);

        var light = runtime.Root.FindDescendants<Button>()
            .SingleOrDefault(node => ((Button)node.View).Label == "明亮主题");
        Assert.NotNull(light);
    }

    [Fact]
    public void 控件展示计数开关滑块与中文输入在窄宽度下保持可用()
    {
        // 使用足够高的视口，保证窄宽度下所有控件都落在整页可见范围内（不依赖滚动命中）。
        using var runtime = CreateRuntime(new GalleryApp(), new Size(320, 1600));

        // 按钮计数：点击后标签与状态文本同步更新。
        var counter = runtime.Root.FindDescendants<Button>()
            .Single(node => ((Button)node.View).Label == "点击计数：0");
        Click(runtime, counter);
        Assert.NotNull(runtime.Root.FindDescendants<Button>()
            .SingleOrDefault(node => ((Button)node.View).Label == "点击计数：1"));

        // 开关：点击切换布尔状态，状态文本从“开”变“关”。
        var toggle = runtime.Root.FindDescendant<Toggle>()!;
        Click(runtime, toggle);
        Assert.Contains("开关状态：关", StatusText(runtime));

        // 滑块：在窄宽度下仍占满容器，按下最左端把值置为 0。
        var slider = runtime.Root.FindDescendant<Slider>()!;
        Assert.True(slider.Frame.Width > 100, $"窄宽度下滑块应占满父容器，实际 {slider.Frame}。");
        runtime.DispatchPointer(new PointerEvent(PointerEventKind.Down, new Point(slider.Frame.Left + 0.5, slider.Frame.Center.Y)));
        runtime.DispatchPointer(new PointerEvent(PointerEventKind.Up, new Point(slider.Frame.Left + 0.5, slider.Frame.Center.Y)));
        runtime.ProcessPendingWork();
        Assert.Contains("滑块值：0.00", StatusText(runtime));

        // 中文输入：已提交文本追加到绑定，输入框仍是同一节点。
        var field = runtime.Root.FindDescendants<TextField>()
            .Single(node => ((TextField)node.View).Placeholder == "请输入文本");
        runtime.SetFocus(field);
        runtime.DispatchKey(new KeyEvent(KeyEventKind.Down, UiKey.End));
        runtime.DispatchTextInput(new TextInputEvent("中文"));
        runtime.ProcessPendingWork();
        Assert.Same(field, runtime.Root.FindDescendants<TextField>()
            .Single(node => ((TextField)node.View).Placeholder == "请输入文本"));
        Assert.EndsWith("中文", ((TextField)field.View).Text.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void 嵌套响应式示例在同一窗口按不同父宽度选择不同布局()
    {
        using var runtime = CreateRuntime(new NestedResponsiveExample(), new Size(1024, 900));

        var adaptives = runtime.Root.FindDescendants<AdaptiveStack>().ToArray();
        Assert.Equal(2, adaptives.Length);
        Assert.Contains(adaptives, node => node.AdaptiveIsHorizontal == true);
        Assert.Contains(adaptives, node => node.AdaptiveIsHorizontal == false);

        var branchRoots = runtime.Root.FindDescendants<Responsive>()
            .Select(node => node.Children[0].View)
            .ToArray();
        Assert.Equal(2, branchRoots.Length);
        Assert.Contains(branchRoots, view => view is HStack);
        Assert.Contains(branchRoots, view => view is VStack);
    }

    [Fact]
    public void 嵌套响应式示例断点切换保持稳定输入与内层滚动()
    {
        using var runtime = CreateRuntime(new NestedResponsiveExample(), new Size(1024, 900));

        var stable = runtime.Root.FindDescendants<TextField>()
            .Where(node => ((TextField)node.View).Placeholder == "稳定输入（切换布局后继续编辑）")
            .ToArray();
        Assert.Equal(2, stable.Length);

        var field = stable[0];
        var adaptive = runtime.Root.FindDescendants<AdaptiveStack>()
            .Single(node => node.Descendants().Any(candidate => ReferenceEquals(candidate, field)));
        Assert.True(adaptive.AdaptiveIsHorizontal);
        var scroll = adaptive.FindDescendant<ScrollView>()!;

        // 真实滚动 + 中文输入 + 选区，然后切到窄模式。
        runtime.DispatchWheel(new WheelEvent(scroll.Frame.Center, 0, -2, KeyModifiers.None, WheelDeltaMode.Steps));
        runtime.ProcessPendingWork();
        Assert.True(scroll.ScrollOffsetY > 0, "内层滚动视图应已滚动。");
        var offset = scroll.ScrollOffsetY;

        runtime.SetFocus(field);
        runtime.DispatchKey(new KeyEvent(KeyEventKind.Down, UiKey.End));
        runtime.DispatchTextInput(new TextInputEvent("中文"));
        runtime.DispatchKey(new KeyEvent(KeyEventKind.Down, UiKey.End));
        runtime.DispatchKey(new KeyEvent(KeyEventKind.Down, UiKey.Left, KeyModifiers.Shift));
        runtime.ProcessPendingWork();

        var state = (TextFieldState)field.InteractionState!;
        var caret = state.Caret;
        var anchor = state.Anchor;
        var typed = ((TextField)field.View).Text.Value;
        Assert.EndsWith("中文", typed, StringComparison.Ordinal);

        // 同一窗口缩窄：宽面板的 AdaptiveStack 改为纵向，Responsive 切到窄分支。
        runtime.SetViewport(new Size(400, 900), 1.0);
        runtime.ProcessPendingWork();

        Assert.False(adaptive.AdaptiveIsHorizontal);
        Assert.Same(field, runtime.Root.FindDescendants<TextField>()
            .Single(node => ((TextField)node.View).Placeholder == "稳定输入（切换布局后继续编辑）"
                && adaptive.Descendants().Contains(node)));
        Assert.Same(field, runtime.FocusedNode);
        Assert.Same(state, field.InteractionState);
        Assert.Equal(caret, state.Caret);
        Assert.Equal(anchor, state.Anchor);
        Assert.Equal(typed, ((TextField)field.View).Text.Value);
        Assert.Same(scroll, adaptive.FindDescendant<ScrollView>());
        Assert.Equal(offset, scroll.ScrollOffsetY, 3);
        Assert.All(
            runtime.Root.FindDescendants<Responsive>(),
            node => Assert.IsType<VStack>(node.Children[0].View));

        // 切回宽窗口：持久数据仍在外层绑定，分支恢复宽结构。
        runtime.SetViewport(new Size(1024, 900), 1.0);
        runtime.ProcessPendingWork();
        Assert.True(adaptive.AdaptiveIsHorizontal);
        Assert.Contains(
            runtime.Root.FindDescendants<Responsive>(),
            node => node.Children[0].View is HStack);
        Assert.Equal(typed, ((TextField)field.View).Text.Value);
    }

    private static string StatusText(UiRuntime runtime)
        => ((Text)runtime.Root.FindDescendants<Text>()
            .Single(node => ((Text)node.View).Content.StartsWith("开关状态", StringComparison.Ordinal)).View).Content;

    private static void Click(UiRuntime runtime, ViewNode node)
    {
        runtime.DispatchPointer(new PointerEvent(PointerEventKind.Down, node.Frame.Center));
        runtime.DispatchPointer(new PointerEvent(PointerEventKind.Up, node.Frame.Center));
        runtime.ProcessPendingWork();
    }

    private static UiRuntime CreateRuntime(View root, Size viewport)
    {
        var runtime = new UiRuntime(root, new UiRuntimeServices
        {
            Dispatcher = new HeadlessUiDispatcher(),
            TextService = new SkiaTextService(),
            TextureService = new RecordingTextureService(),
            ImageService = CreateImages(),
            Clipboard = new StubClipboardService(),
            Clock = new TestUiClock(),
            WheelScrollSettings = new StubWheelScrollSettingsService(WheelScrollSettings.Default),
        });

        runtime.SetEnvironment(new EnvironmentValues { Theme = Theme.Light });
        runtime.SetViewport(viewport, 1.0);
        runtime.Start();
        runtime.ProcessPendingWork();
        return runtime;
    }

    private static StubImageService CreateImages()
    {
        var images = new StubImageService();
        images.Register(Path.Combine(AppContext.BaseDirectory, "shared", "Assets", "logo.png"), 64, 64);
        return images;
    }
}
