using CSharp.UI.Samples.ControlGallery;
using CSharp.UI.Testing;
using OpenTK.Windowing.Common;
using Xunit;

namespace CSharp.UI.OpenTK.Tests;

/// <summary>
/// R15：控件展示示例“滚动容器”一节的内层滚动回归；直接实例化示例程序集里的真实
/// <see cref="GalleryApp"/>，不复制简化视图树。
/// </summary>
/// <remarks>
/// <para>
/// 该区域是真实的内层 <see cref="ScrollView"/>：120 逻辑像素的视口由滚动视图之外的 <see cref="FrameView"/>
/// 约束，内部内容保持自然高度（旧写法 <c>VStack + Frame + Clip</c> 没有滚动行为，滚轮只能交给整页外层）。
/// </para>
/// <para>
/// 回归通过真实 <see cref="OpenTkInputAdapter"/>（GLFW 归一化后普通鼠标一格为 <c>-1</c>，不随 DPI 缩放）
/// 投递滚轮：先用外层滚动把内层视口带进可见范围，再验证一格只移动内层 48 逻辑像素且外层不动、
/// 内层到底后外层接手、向上可回到首行，以及首尾行可见、视口外的行被真正裁剪。
/// 滚轮设置注入每格 3 行的替身，因此结果与本机系统滚动设置无关，DPI 1 与 1.5 下逻辑距离一致。
/// </para>
/// </remarks>
public sealed class GalleryNestedScrollTests
{
    /// <summary>真实适配器上普通鼠标的一格：GLFW 在 Windows 上把原生 120 归一化为 <c>±1</c>。</summary>
    private const double WheelNotch = -1;

    /// <summary>每格 3 行 × 16 逻辑像素 = 48 逻辑像素（<see cref="WheelScrollSettings.Default"/>）。</summary>
    private const double ExpectedStep = 48;

    private const double Epsilon = 0.001;

    private static readonly Size GalleryViewport = new(900, 700);

    /// <summary>实际示例的“滚动容器”必须由视口在外的真实内层滚动视图构成。</summary>
    [Fact]
    public void 控件展示的滚动容器是视口在外的真实内层滚动视图()
    {
        using var host = new GalleryHost(GalleryViewport, 1.0);

        Assert.NotNull(host.Outer);
        Assert.NotNull(host.Inner);
        var outer = host.Outer;
        var inner = host.Inner;

        // 内层是整页外层滚动视图的后代：页面仍然只靠外层整页滚动。
        Assert.Contains(inner, outer.Descendants());

        // 视口 Frame 在滚动视图外：内层的父节点是固定 120 高度的 FrameView，其子项正是该滚动视图。
        var frame = Assert.IsType<FrameView>(inner.Parent!.View);
        Assert.Equal(120, frame.Height!.Value, 3);
        // FrameView.Content 由 .Key 包裹的 ScrollView 提供；Key 是定位用身份不是被测契约，断言结构不在此。
        var keyed = Assert.IsType<KeyedView>(frame.Content);
        Assert.Same(inner.View, keyed.Child);
        Assert.Equal(120, inner.Frame.Height, 3);

        // 内部内容保持自然高度（替身行高 17.5、间距 4）：1 行说明 + 40 行内容，远高于 120 的视口。
        const double stubLineHeight = 14 * 1.25;
        const double spacing = 4;
        var content = inner.Children[0];
        Assert.Equal(41, content.Children.Count);
        Assert.Equal((41 * stubLineHeight) + (40 * spacing), content.DesiredSize.Height, 6);
        Assert.True(
            content.DesiredSize.Height > inner.Frame.Height + ExpectedStep,
            $"内层内容高度 {content.DesiredSize.Height} 应远高于视口 {inner.Frame.Height}。");

        // 旧实现的 Clip 修饰符不再存在：视口裁剪由内层 ScrollView 自身完成。
        Assert.Empty(host.Runtime.Root.FindDescendants<ClipView>());
    }

    /// <summary>一格 <c>-1</c> 只移动内层 48 逻辑像素，外层偏移不变；DPI 1 与 1.5 距离一致。</summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    public void 控件展示内层一格滚动48逻辑像素且外层不动(double pixelScale)
    {
        using var host = new GalleryHost(GalleryViewport, pixelScale);
        Assert.NotNull(host.Outer);
        Assert.NotNull(host.Inner);
        BringInnerIntoView(host);

        var outerOffset = host.Outer.ScrollOffsetY;
        host.Wheel(host.Inner.Frame.Center, WheelNotch);

        // 真实适配器路径：归一化步数单位，普通一格是 -1，不是 -120 像素。
        Assert.Equal(WheelDeltaMode.Steps, host.Adapter.LastWheelEvent!.Value.Mode);
        Assert.Equal(WheelNotch, host.Adapter.LastWheelEvent!.Value.DeltaY, 6);

        Assert.Equal(ExpectedStep, host.Inner.ScrollOffsetY, 6);
        Assert.Equal(outerOffset, host.Outer.ScrollOffsetY, 6);
    }

    /// <summary>内层到边界后同一方向的滚轮由外层接手；反向滚动先回到内层首行。</summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    public void 控件展示内层到底后外层接手且向上可回到首行(double pixelScale)
    {
        using var host = new GalleryHost(GalleryViewport, pixelScale);
        BringInnerIntoView(host);

        var innerMax = InnerMaxOffset(host);
        Assert.True(innerMax > ExpectedStep, $"内层应有可滚动空间，实际最大偏移 {innerMax}。");

        // 向下滚到底：内层自己消费并夹紧到最大偏移。
        var downNotches = (int)Math.Ceiling(innerMax / ExpectedStep) + 1;
        for (var i = 0; i < downNotches; i++)
        {
            host.Wheel(host.Inner.Frame.Center, WheelNotch);
        }

        Assert.Equal(innerMax, host.Inner.ScrollOffsetY, 6);

        // 内层已在底部：继续向下的同一格交给外层，内层保持不动。
        var outerBefore = host.Outer.ScrollOffsetY;
        host.Wheel(host.Inner.Frame.Center, WheelNotch);
        Assert.Equal(innerMax, host.Inner.ScrollOffsetY, 6);
        Assert.Equal(outerBefore + ExpectedStep, host.Outer.ScrollOffsetY, 6);

        // 向上：内层先回到首行（说明反向也先由内层消费），首行完整可见。
        var upNotches = (int)Math.Ceiling(innerMax / ExpectedStep);
        for (var i = 0; i < upNotches; i++)
        {
            host.Wheel(host.Inner.Frame.Center, -WheelNotch);
        }

        Assert.Equal(0, host.Inner.ScrollOffsetY, 6);

        var first = host.ContentRows[0].Frame;
        Assert.True(first.Y >= host.Inner.Frame.Y - Epsilon, "回到顶部后首行不应被滚出视口上方。");
        Assert.True(first.Bottom <= host.Inner.Frame.Bottom + Epsilon, "回到顶部后首行应完整可见。");
        Assert.True(first.IntersectsWith(host.Outer.Frame), "首行应位于整页外层视口内。");
    }

    /// <summary>内层首行与末行分别能进入 120 像素视口，视口外的行被真正裁剪掉。</summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    public void 控件展示内层首尾行可见且裁剪正确(double pixelScale)
    {
        using var host = new GalleryHost(GalleryViewport, pixelScale);
        BringInnerIntoView(host);

        var rows = host.ContentRows;
        Assert.Equal(40, rows.Count);

        // 顶部：首行完整可见，末行在视口之外（没有绘制命令）。
        var top = InnerViewportCommands(host, host.Runtime.BuildFrame().Commands);
        Assert.Equal(host.Inner.Frame.Height, top.Clip.Height, 3);
        Assert.True(rows[0].Frame.Y >= host.Inner.Frame.Y - Epsilon);
        Assert.True(rows[0].Frame.Bottom <= host.Inner.Frame.Bottom + Epsilon);
        Assert.False(rows[^1].Frame.IntersectsWith(top.Clip));
        AssertRowsMatchClip(host, rows, top.Content);

        // 底部：末行完整可见，首行被滚出视口并裁剪。
        var innerMax = InnerMaxOffset(host);
        var downNotches = (int)Math.Ceiling(innerMax / ExpectedStep) + 1;
        for (var i = 0; i < downNotches; i++)
        {
            host.Wheel(host.Inner.Frame.Center, WheelNotch);
        }

        Assert.Equal(innerMax, host.Inner.ScrollOffsetY, 6);

        var bottom = InnerViewportCommands(host, host.Runtime.BuildFrame().Commands);
        Assert.True(rows[^1].Frame.Y >= host.Inner.Frame.Y - Epsilon, "到底后末行不应被滚出视口下方。");
        Assert.True(rows[^1].Frame.Bottom <= host.Inner.Frame.Bottom + Epsilon, "到底后末行应完整可见。");
        Assert.False(rows[0].Frame.IntersectsWith(bottom.Clip));
        AssertRowsMatchClip(host, rows, bottom.Content);
    }

    private static double InnerMaxOffset(GalleryHost host)
        => host.Inner.Children[0].DesiredSize.Height - host.Inner.Frame.Height;

    /// <summary>让外层整页滚动，直到 120 像素的内层视口完全进入外层可见范围。</summary>
    private static void BringInnerIntoView(GalleryHost host)
    {
        // 外层专属滚轮点：整页视口左上角，处于内层滚动视图的水平与垂直命中范围之外。
        var outerOnly = new Point(host.Outer.Frame.X + 8, host.Outer.Frame.Y + 6);
        var notches = 0;

        while (!FullyInside(host.Inner.Frame, host.Outer.Frame) && notches < 200)
        {
            Assert.Same(host.Outer, host.NearestScrollView(outerOnly));
            host.Wheel(outerOnly, WheelNotch);
            notches++;
        }

        Assert.True(FullyInside(host.Inner.Frame, host.Outer.Frame), "外层滚动后内层视口应完全可见。");
        Assert.True(notches > 0, "内层初始应在外层视口之外，必须先由外层滚动才可见。");
        Assert.Equal(0, host.Inner.ScrollOffsetY, 6);
    }

    private static bool FullyInside(Rect target, Rect clip)
        => !target.IsEmpty
            && target.X >= clip.X - Epsilon
            && target.Y >= clip.Y - Epsilon
            && target.Right <= clip.Right + Epsilon
            && target.Bottom <= clip.Bottom + Epsilon;

    /// <summary>取内层滚动视图压入的视口裁剪区及其内部的绘制命令。</summary>
    private static (Rect Clip, IReadOnlyList<DrawCommand> Content) InnerViewportCommands(
        GalleryHost host,
        IReadOnlyList<DrawCommand> commands)
    {
        // 内层 ScrollView 现在用 persistent gutter：压入两次裁剪——外层 frame（含滚动条槽）与内层 content-rect（frame - right 12）。
        // 视口裁剪 = 内层 content-rect；先找外层 frame 的 push，再取其内部的 content-rect push。
        var outerStart = -1;
        for (var i = 0; i < commands.Count; i++)
        {
            if (commands[i] is PushClipCommand push && IsSameRect(push.Bounds, host.Inner.Frame))
            {
                outerStart = i;
                break;
            }
        }
        Assert.True(outerStart >= 0, "内层滚动视图没有按整 frame 压入外层裁剪区。");

        var expectedContent = host.Inner.Frame with { Width = host.Inner.Frame.Width - ScrollbarMetrics.Thickness };
        var start = -1;
        for (var i = outerStart + 1; i < commands.Count; i++)
        {
            if (commands[i] is PopClipCommand)
            {
                break;
            }
            if (commands[i] is PushClipCommand push && IsSameRect(push.Bounds, expectedContent))
            {
                start = i;
                break;
            }
        }
        Assert.True(start >= 0, "内层滚动视图没有按内容区（frame - 滚动条槽）压入视口裁剪区。");

        // 内层区域只包含文本：紧随其后的弹出命令就是配对项。
        var end = start + 1;
        while (end < commands.Count && commands[end] is not PopClipCommand)
        {
            end++;
        }
        Assert.True(end < commands.Count, "内层滚动视图的视口裁剪区没有配对的弹出命令。");

        var clip = ((PushClipCommand)commands[start]).Bounds;
        return (clip, [.. commands.Skip(start + 1).Take(end - start - 1)]);
    }

    /// <summary>内层视口内绘制的行必须与“与视口相交的行”完全一致，从而证明视口外的行被裁剪。</summary>
    private static void AssertRowsMatchClip(GalleryHost host, IReadOnlyList<ViewNode> rows, IReadOnlyList<DrawCommand> content)
    {
        var texts = content.OfType<TextureCommand>().ToList();
        Assert.Equal(content.Count, texts.Count);

        // 视口 = inner.Frame - 右 gutter（与 InnerViewportCommands 找到的 clip 一致）。
        var viewport = host.Inner.Frame with { Width = host.Inner.Frame.Width - ScrollbarMetrics.Thickness };
        // 视口内绘制的内容都落在内层视口裁剪区里。
        Assert.All(texts, text => Assert.True(text.Bounds.IntersectsWith(viewport)));

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i].Frame;
            var intersectsViewport = row.IntersectsWith(viewport);
            var hasCommand = texts.Any(text => text.Bounds.Y < row.Bottom - Epsilon && row.Y < text.Bounds.Bottom - Epsilon);
            Assert.True(
                intersectsViewport == hasCommand,
                $"第 {i + 1} 行的可见状态与绘制命令不一致：与视口相交={intersectsViewport}，有绘制命令={hasCommand}。");
        }
    }

    private static bool IsSameRect(Rect a, Rect b)
        => Math.Abs(a.X - b.X) < Epsilon
            && Math.Abs(a.Y - b.Y) < Epsilon
            && Math.Abs(a.Width - b.Width) < Epsilon
            && Math.Abs(a.Height - b.Height) < Epsilon;

    /// <summary>
    /// 无窗口宿主：真实 <see cref="GalleryApp"/> + 真实 <see cref="OpenTkInputAdapter"/>，
    /// 只替换平台服务（文本、纹理、图片、时钟、滚轮设置），不替换任何布局或滚动逻辑。
    /// </summary>
    private sealed class GalleryHost : IDisposable
    {
        private readonly RecordingTextureService _textures = new();
        private readonly StubWheelScrollSettingsService _wheel = new(WheelScrollSettings.Default);

        public GalleryHost(Size viewport, double pixelScale)
        {
            PixelScale = pixelScale;
            Runtime = new UiRuntime(new GalleryApp(), new UiRuntimeServices
            {
                Dispatcher = new HeadlessUiDispatcher(),
                TextService = new StubTextService(),
            RichTextService = new StubTextService() as IRichTextService,
                TextureService = _textures,
                ImageService = CreateImages(),
                Clipboard = new StubClipboardService(),
                Clock = new TestUiClock(),
                WheelScrollSettings = _wheel,
            });

            Runtime.SetEnvironment(new EnvironmentValues { Theme = Theme.Light });
            Runtime.SetViewport(viewport, pixelScale);
            Runtime.Start();
            Runtime.ProcessPendingWork();

            Adapter = new OpenTkInputAdapter(Runtime, () => { });
            Adapter.UpdatePointerTransform(1 / pixelScale, 1 / pixelScale);

            // 通过示例注入的 Key 定位；新增 demo ScrollView 不影响命中。
            var outerNode = GalleryScrollLookup.RootScroll(Runtime);
            var innerNode = GalleryScrollLookup.NestedScroll(Runtime);
            ScrollViews = [outerNode, innerNode];
            var content = innerNode.Children[0];
            ContentRows = [.. content.Children.Skip(1)];
        }

        public UiRuntime Runtime { get; }

        public OpenTkInputAdapter Adapter { get; }

        public double PixelScale { get; }

        public IReadOnlyList<ViewNode> ScrollViews { get; }

        /// <summary>整页外层滚动视图。</summary>
        public ViewNode Outer => ScrollViews[0];

        /// <summary>“滚动容器”一节内的真实内层滚动视图。</summary>
        public ViewNode Inner => ScrollViews[1];

        /// <summary>内层的 40 行内容节点（跳过第一行说明）。</summary>
        public IReadOnlyList<ViewNode> ContentRows { get; }

        /// <summary>移动指针并投递一次归一化步数的滚轮（宿主继续按 R14 的单位契约换算）。</summary>
        public void Wheel(Point logicalPosition, double notches)
        {
            Adapter.HandleMouseMove(new MouseMoveEventArgs(
                (float)(logicalPosition.X * PixelScale),
                (float)(logicalPosition.Y * PixelScale),
                0,
                0));
            Adapter.HandleWheel(new MouseWheelEventArgs(0, (float)notches));
            Runtime.ProcessPendingWork();
        }

        /// <summary>命中点向上冒泡时最先遇到的滚动容器；没有则返回 null。</summary>
        public ViewNode? NearestScrollView(Point logicalPosition)
        {
            for (var node = Runtime.HitTest(logicalPosition); node is not null; node = node.Parent)
            {
                if (node.View is ScrollView)
                {
                    return node;
                }
            }

            return null;
        }

        public void Dispose() => Runtime.Dispose();

        private static StubImageService CreateImages()
        {
            var images = new StubImageService();

            // 与示例一致：图片来自输出目录下的 shared/Assets/logo.png；这里注册同路径的替身，避免加载失败诊断。
            images.Register(Path.Combine(AppContext.BaseDirectory, "shared", "Assets", "logo.png"), 64, 64);
            return images;
        }
    }
}
