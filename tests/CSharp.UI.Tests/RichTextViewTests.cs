using CSharp.UI.Testing;
using Xunit;

namespace CSharp.UI.Tests;


/// <summary>
/// RT-03：<see cref="RichText"/> 视图——paint 解算缓存、装饰矩形、纹理键、plain 快速路径与
/// handle 生命周期。替身 <see cref="StubTextService"/> 实现 <see cref="IRichTextService"/>，
/// 命令断言在真实 runtime 的 BuildFrame 输出上执行。
/// </summary>
public class RichTextViewTests
{
    private static readonly Color BackgroundColor = new(255, 255, 0);

    private static int IndexOf(IReadOnlyList<DrawCommand> commands, DrawCommand target)
    {
        for (var i = 0; i < commands.Count; i++)
        {
            if (ReferenceEquals(commands[i], target))
            {
                return i;
            }
        }

        return -1;
    }

    private static StyledText Styled(string text, params TextStyleRange[] ranges)
        => new(text, ranges, null);

    private static StyledText Linked(string text, params TextInteractionRange[] interactions)
        => new(text, null, interactions);

    private static TestApp App(View root, Size? viewport = null, double pixelScale = 1.0)
        => new(root, viewport ?? new Size(400, 300), pixelScale);

    private static ViewNode RichNode(TestApp app) => app.Runtime.Root.Require<RichText>();

    [Fact]
    public void 无区间内容走plain路径_RichTextService零调用()
    {
        var app = App(new RichText(StyledText.Create("plain")));
        var node = RichNode(app);

        Assert.IsType<TextLayoutResult>(node.ViewCache);
        Assert.Equal(0, ((IRichTextService)app.Text).CachedLayoutCount);
        Assert.Equal(0, ((IRichTextService)app.Text).CachedRasterCount);

        var commands = app.Frame();
        var texture = Assert.IsType<TextureCommand>(Assert.Single(commands));
        Assert.True(texture.Texture.IsAlphaMask, "plain 路径应与 Text 一样输出 Alpha8 遮罩。");
    }

    [Fact]
    public void 多样式段落_背景文本装饰按序输出()
    {
        var styled = Styled(
            "ab",
            new TextStyleRange(0, 1, new TextStyle { Foreground = Color.Red, Background = BackgroundColor, Underline = true }),
            new TextStyleRange(1, 2, new TextStyle { Foreground = Color.Blue, Strikethrough = true }));
        var app = App(new RichText(styled));
        var node = RichNode(app);

        var cache = Assert.IsType<RichText.RichTextViewCache>(node.ViewCache);
        Assert.NotNull(cache.Layout);
        Assert.Equal(2, cache.Layout!.Segments.Count);

        var commands = app.Frame();
        var textures = commands.OfType<TextureCommand>().ToArray();
        var rects = commands.OfType<RectangleCommand>().ToArray();

        Assert.Single(textures);
        Assert.False(textures[0].Texture.IsAlphaMask, "rich 路径输出 RGBA 纹理（非 Alpha8 遮罩）。");
        Assert.Equal(Color.White, textures[0].Tint);

        // 顺序：背景矩形先于纹理，underline/strike 矩形在其后。
        var textureIndex = IndexOf(commands, textures[0]);
        var backgroundIndex = IndexOf(commands, rects.First(r => r.Fill == BackgroundColor));
        Assert.True(backgroundIndex >= 0 && backgroundIndex < textureIndex, "背景矩形必须先于文本纹理。");
        var decorations = rects.Where(r => r.Fill == Color.Red || r.Fill == Color.Blue).ToArray();
        Assert.Equal(2, decorations.Length);
        Assert.All(decorations, d => Assert.True(IndexOf(commands, d) > textureIndex, "装饰矩形必须在纹理之后。"));
        // underline 在 strikethrough 之前（契约顺序 背景→纹理→underline→strike）。
        Assert.True(IndexOf(commands, decorations[0]) < IndexOf(commands, decorations[1]));
        // underline 的段前景是红、strike 段是蓝。
        Assert.Equal(Color.Red, decorations[0].Fill);
        Assert.Equal(Color.Blue, decorations[1].Fill);
    }

    [Fact]
    public void 相邻同色背景合并_行间不合并()
    {
        // "a" 与 "b" 同背景相邻 → 一个矩形；"c" 无背景 → 断开后 "d" 又一个矩形。
        var styled = Styled(
            "abcd",
            new TextStyleRange(0, 1, new TextStyle { Background = BackgroundColor }),
            new TextStyleRange(1, 2, new TextStyle { Background = BackgroundColor, Weight = FontWeight.Bold }),
            new TextStyleRange(3, 4, new TextStyle { Background = BackgroundColor }));
        var app = App(new RichText(styled));
        var commands = app.Frame();
        var backgrounds = commands.OfType<RectangleCommand>().Where(r => r.Fill == BackgroundColor).ToArray();
        Assert.Equal(2, backgrounds.Length);
        Assert.True(backgrounds[1].Bounds.X > backgrounds[0].Bounds.Right - 0.01, "被无背景段断开后应产出两个独立矩形。");
    }

    [Fact]
    public void 换前景只重栅格化_不重排版_纹理键变化()
    {
        var component = new StyledTextHost(Styled("hi", new TextStyleRange(0, 2, new TextStyle { Foreground = Color.Red })));
        var app = App(component);
        var node = RichNode(app);
        var cache = (RichText.RichTextViewCache)node.ViewCache!;
        var layout = cache.Layout!;
        app.Frame();

        var rasterMisses = app.Text.RichRasterMisses;
        var layoutMisses = app.Text.RichLayoutMisses;
        var textureUploads = app.Textures.FactoryInvocations;
        // 纯换色：端点不变 → GeometryKey 相同。
        component.SetContent(Styled("hi", new TextStyleRange(0, 2, new TextStyle { Foreground = Color.Blue })));
        app.Runtime.ProcessPendingWork();
        app.Frame();

        Assert.Same(layout, cache.Layout);
        Assert.Equal(layoutMisses, app.Text.RichLayoutMisses);
        Assert.Equal(rasterMisses + 1, app.Text.RichRasterMisses);
        Assert.Equal(textureUploads + 1, app.Textures.FactoryInvocations);
    }

    [Fact]
    public void 环境前景变化只重栅格化()
    {
        var component = new StyledTextHost(Styled("hi", new TextStyleRange(0, 2, new TextStyle { Weight = FontWeight.Bold })));
        var app = App(component);
        var node = RichNode(app);
        var cache = (RichText.RichTextViewCache)node.ViewCache!;
        var layout = cache.Layout!;
        app.Frame();

        var rasterMisses = app.Text.RichRasterMisses;
        var layoutMisses = app.Text.RichLayoutMisses;

        // 环境前景变化（无区间颜色覆盖）→ paint 键变 → 重栅格化，不重排版。
        app.Runtime.SetEnvironment(new EnvironmentValues { Theme = Theme.Light, Foreground = Color.Green });
        app.Runtime.ProcessPendingWork();
        app.Frame();

        Assert.Same(layout, cache.Layout);
        Assert.Equal(layoutMisses, app.Text.RichLayoutMisses);
        Assert.Equal(rasterMisses + 1, app.Text.RichRasterMisses);
        Assert.Equal(Color.Green, cache.ParagraphForeground);
    }

    [Fact]
    public void 改payload端点不变_两计数均不变()
    {
        var component = new StyledTextHost(Linked("go", new TextInteractionRange(0, 2, 1, "u1")));
        var app = App(component);
        var node = RichNode(app);
        var cache = (RichText.RichTextViewCache)node.ViewCache!;
        var layout = cache.Layout!;
        app.Frame();

        var layoutMisses = app.Text.RichLayoutMisses;
        var rasterMisses = app.Text.RichRasterMisses;

        component.SetContent(Linked("go", new TextInteractionRange(0, 2, 1, "u2")));
        app.Runtime.ProcessPendingWork();
        app.Frame();

        Assert.Same(layout, cache.Layout);
        Assert.Equal(layoutMisses, app.Text.RichLayoutMisses);
        Assert.Equal(rasterMisses, app.Text.RichRasterMisses);
    }

    [Fact]
    public void 改交互端点_重排版()
    {
        var component = new StyledTextHost(Linked("go!", new TextInteractionRange(0, 2, 1, "u1")));
        var app = App(component);
        var node = RichNode(app);
        var cache = (RichText.RichTextViewCache)node.ViewCache!;
        var layout = cache.Layout!;
        app.Frame();

        var layoutMisses = app.Text.RichLayoutMisses;

        component.SetContent(Linked("go!", new TextInteractionRange(0, 3, 1, "u1")));
        app.Runtime.ProcessPendingWork();
        app.Frame();

        Assert.NotSame(layout, cache.Layout);
        Assert.Equal(layoutMisses + 1, app.Text.RichLayoutMisses);
    }

    [Fact]
    public void Emit连续帧不重建paint或纹理()
    {
        var app = App(new RichText(Styled("hi", new TextStyleRange(0, 1, new TextStyle { Foreground = Color.Red }))));
        app.Frame();

        // 消费者可观察行为：同一内容连续帧 Emit 不触发新栅格化（paint/纹理键不变）。
        // 内部 cache.Paint 对象引用是实现细节，不作永久断言。
        var uploads = app.Textures.FactoryInvocations;
        for (var i = 0; i < 5; i++)
        {
            app.Runtime.InvalidateRender();
            app.Frame();
            Assert.Equal(uploads, app.Textures.FactoryInvocations);
        }
    }

    [Fact]
    public void 换内容与卸载释放handle_raster对已释放handle抛()
    {
        var component = new StyledTextHost(Styled("ab", new TextStyleRange(0, 1, new TextStyle { Weight = FontWeight.Bold })));
        var app = App(component);
        var node = RichNode(app);
        var cache = (RichText.RichTextViewCache)node.ViewCache!;
        var oldHandle = cache.Layout!;

        // 换内容（端点变）→ 重排版 → 旧 handle 释放。
        component.SetContent(Styled("abc", new TextStyleRange(0, 2, new TextStyle { Weight = FontWeight.Bold })));
        app.Runtime.ProcessPendingWork();
        app.Frame();

        Assert.Throws<ObjectDisposedException>(() => { _ = oldHandle.Size; });
        Assert.Throws<ObjectDisposedException>(() => { _ = app.Text.Rasterize(oldHandle, cache.Paint!); });
        Assert.NotSame(oldHandle, cache.Layout);

        // 卸载 → handle 释放。
        var released = cache.Layout!;
        component.Visible.Value = false;
        app.Runtime.ProcessPendingWork();
        Assert.Throws<ObjectDisposedException>(() => { _ = released.Size; });
    }

    [Fact]
    public void plain到rich切换释放与重建_再切回plain无rich分配()
    {
        var component = new StyledTextHost(Styled("ab", new TextStyleRange(0, 1, new TextStyle { Weight = FontWeight.Bold })));
        var app = App(component);
        var node = RichNode(app);
        var richHandle = ((RichText.RichTextViewCache)node.ViewCache!).Layout!;
        app.Frame();

        // rich → plain
        component.SetContent(StyledText.Create("ab"));
        app.Runtime.ProcessPendingWork();
        app.Frame();

        Assert.IsType<TextLayoutResult>(node.ViewCache);
        Assert.Throws<ObjectDisposedException>(() => { _ = richHandle.Size; });
        var commands = app.Frame();
        Assert.All(commands.OfType<TextureCommand>(), t => Assert.True(t.Texture.IsAlphaMask));

        // plain → rich
        component.SetContent(Styled("ab", new TextStyleRange(0, 1, new TextStyle { Weight = FontWeight.Bold })));
        app.Runtime.ProcessPendingWork();
        app.Frame();
        Assert.IsType<RichText.RichTextViewCache>(node.ViewCache);
    }

    [Fact]
    public void RichTextService缺失时_有样式抛明确异常_无样式正常()
    {
        var runtime = new UiRuntime(
            new RichText(Styled("x", new TextStyleRange(0, 1, new TextStyle { Foreground = Color.Red }))),
            new UiRuntimeServices
            {
                Dispatcher = new HeadlessUiDispatcher(),
                TextService = new StubTextService(),
                // RichTextService = null（默认）
            });
        runtime.SetViewport(new Size(200, 100), 1.0);
        var exception = Assert.Throws<InvalidOperationException>(() => runtime.ProcessPendingWork());
        Assert.Contains("RichTextService", exception.Message);
        runtime.Dispose();

        var plainRuntime = new UiRuntime(
            new RichText(StyledText.Create("x")),
            new UiRuntimeServices
            {
                Dispatcher = new HeadlessUiDispatcher(),
                TextService = new StubTextService(),
            });
        plainRuntime.SetViewport(new Size(200, 100), 1.0);
        plainRuntime.ProcessPendingWork(); // 不抛
        plainRuntime.Dispose();
    }

    [Fact]
    public void 探测测量不替换已提交handle_恢复后纹理仍有效()
    {
        // 把 RichText 放进 Flow，probe 触发其子项的重测；提交 handle 必须存活且不被换走。
        var styled = Styled("ab", new TextStyleRange(0, 1, new TextStyle { Weight = FontWeight.Bold }));
        var rich = new RichText(styled);
        var app = App(new Flow(rich));
        var node = RichNode(app);
        var cache = (RichText.RichTextViewCache)node.ViewCache!;
        var committed = cache.Layout!;
        app.Frame();

        var uploads = app.Textures.FactoryInvocations;

        // 直接驱动一次探测测量（与 Flow/Stack 自然测量走的同一入口）。
        var context = new LayoutContext(app.Runtime, app.Text, app.Images, 1.0);
        using (context.BeginProbe())
        {
            context.Measure(node, new LayoutConstraints(0, 5, 0, 300));
        }

        // 探测结束后：ViewCache 引用与 handle 均未变（探测不得 Dispose/替换提交句柄）。
        Assert.Same(cache, node.ViewCache);
        Assert.Same(committed, cache.Layout);
        var size = committed.Size; // 已 Dispose 会抛——这里验证存活。
        Assert.True(size.Width > 0);

        // 再画一帧：纹理键命中已有缓存，不重复上传。
        app.Runtime.InvalidateRender();
        app.Frame();
        Assert.Equal(uploads, app.Textures.FactoryInvocations);
    }

    [Fact]
    public void 透明样式前景与背景经真实预乘合成_祖先Opacity只乘一次()
    {
        // 半透明前景 + 半透明背景 + 祖先 Opacity：命令层只传一次 opacity，
        // 纹理内 run 色保留各自 alpha（像素级验证在服务栅格侧已覆盖；这里验证视图不强制 255）。
        var styled = Styled(
            "x",
            new TextStyleRange(0, 1, new TextStyle
            {
                Foreground = Color.FromArgb(128, 255, 0, 0),
                Background = Color.FromArgb(64, 0, 0, 255),
            }));
        var app = App(new VStack(new RichText(styled)).Padding(4));
        app.Frame();
        var node = RichNode(app);
        var cache = (RichText.RichTextViewCache)node.ViewCache!;

        Assert.Equal(128, cache.Paint!.Runs[0].Foreground.A);
        Assert.Equal(64, cache.Paint.Runs[0].Background.A);
        Assert.Equal(Color.FromArgb(128, 255, 0, 0), cache.Paint.Runs[0].Foreground);

        var texture = app.Frame().OfType<TextureCommand>().Single();
        Assert.Equal(1.0, texture.Opacity, 3); // 无祖先 Opacity 修饰时为 1
    }

    [Fact]
    public void 祖先Opacity只作用于命令_不进入paint()
    {
        var styled = Styled("x", new TextStyleRange(0, 1, new TextStyle { Foreground = Color.Red }));
        var component = new StyledTextHost(styled).WithOpacity(0.5);
        var app = App(component);
        var node = RichNode(app);
        var cache = (RichText.RichTextViewCache)node.ViewCache!;
        app.Frame();

        // Opacity 不入 paint 键（run 色保持原值），只在命令侧乘一次。
        Assert.Equal(Color.Red, cache.Paint!.Runs[0].Foreground);
        var texture = app.Frame().OfType<TextureCommand>().Single();
        Assert.Equal(0.5, texture.Opacity, 3);
    }

    /// <summary>驱动 RichText 内容切换的测试组件。</summary>
    internal sealed class StyledTextHost : Component
    {
        private StyledText _content;

        public StyledTextHost(StyledText content) => _content = content;

        public State<int>? Trigger { get; private set; }

        public State<bool> Visible { get; private set; } = null!;

        public double? OpacityValue { get; set; }

        /// <summary>RT-04：链接激活回调（null 时不设置）。</summary>
        public Action<object?>? OnLinkActivate { get; set; }

        /// <summary>RT-04：链接悬停回调（null 时不设置）。</summary>
        public Action<object?>? OnLinkHover { get; set; }

        /// <summary>RT-04：悬停 paint 覆盖样式（null 时不设置）。</summary>
        public TextPaintStyle? HoverStyle { get; set; }

        public StyledTextHost WithOpacity(double opacity)
        {
            OpacityValue = opacity;
            return this;
        }

        public void SetContent(StyledText content)
        {
            _content = content;
            Trigger!.Value++;
        }

        protected override View Build()
        {
            Trigger = UseState("trigger", 0);
            Visible = UseState("visible", true);
            _ = Trigger.Value;
            if (!Visible.Value)
            {
                return new Spacer(0);
            }

            View view = new RichText(_content) { OnLinkActivate = OnLinkActivate, OnLinkHover = OnLinkHover, LinkHoverStyle = HoverStyle };
            if (OpacityValue is { } opacity)
            {
                view = view.Opacity(opacity);
            }

            return view;
        }
    }

    // ---------- RT-04：链接 hit（行内矩形命中 + Id 身份状态机） ----------

    /// <summary>按真实 runtime 计算第 <paramref name="index"/> 个 interaction 的第一个行内矩形中点（frame 绝对坐标）。</summary>
    private static Point LinkCenter(ViewNode node, int index)
    {
        var layout = ((RichText.RichTextViewCache)node.ViewCache!).Layout!;
        var range = ((RichText)node.View).Content.Interactions[index];
        var rect = layout.GetRangeRects(range.Start, range.End)[0];
        var origin = TextPainter.ResolveOrigin(node.Frame, layout.Size, ((RichText)node.View).Alignment, layout.PixelScale);
        return new Point(origin.X + rect.X + rect.Width / 2, origin.Y + rect.Y + rect.Height / 2);
    }

    /// <summary>返回 RichText frame 内但不在任何 interaction 行内矩形上的点（行间空隙或末尾空白）。</summary>
    private static Point NonLinkPoint(ViewNode node)
    {
        var layout = ((RichText.RichTextViewCache)node.ViewCache!).Layout!;
        var origin = TextPainter.ResolveOrigin(node.Frame, layout.Size, ((RichText)node.View).Alignment, layout.PixelScale);
        // 内容底端之下、frame 底端之上：排版垂直居中时下缘空隙必在 frame 内且不被任何矩形覆盖。
        var y = Math.Min(node.Frame.Bottom - 1, origin.Y + layout.Size.Height + 1);
        return new Point(node.Frame.X + 2, y);
    }

    [Fact]
    public void 按下并释放同一链接_激活回调收到payload()
    {
        object? activated = null;
        var view = new RichText(Linked("click me", new TextInteractionRange(0, 5, 7, "payload-A")))
        {
            OnLinkActivate = p => activated = p,
        };
        var app = App(view);
        var node = RichNode(app);

        app.Click(LinkCenter(node, 0));

        Assert.Equal("payload-A", activated);
    }

    [Fact]
    public void 按下后拖离链接区再松手_不激活()
    {
        object? activated = "unset";
        var view = new RichText(Linked("click me", new TextInteractionRange(0, 5, 7, "payload-A")))
        {
            OnLinkActivate = p => activated = p,
        };
        var app = App(view);
        var node = RichNode(app);
        var link = LinkCenter(node, 0);

        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Move, link));
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Down, link, PointerButton.Left));
        // 拖离区间（移到 frame 内但不在矩形并集上）。
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Move, NonLinkPoint(node)));
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Up, NonLinkPoint(node), PointerButton.Left));

        Assert.Equal("unset", activated);
        Assert.Null(node.InteractionState is RichText.RichTextInteractionState s ? s.PressedId : null);
    }

    [Fact]
    public void 按下拖离再移回_仍不激活()
    {
        object? activated = "unset";
        var view = new RichText(Linked("click me", new TextInteractionRange(0, 5, 7, "payload-A")))
        {
            OnLinkActivate = p => activated = p,
        };
        var app = App(view);
        var node = RichNode(app);
        var link = LinkCenter(node, 0);

        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Move, link));
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Down, link, PointerButton.Left));
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Move, NonLinkPoint(node)));
        // 移回：cancelled 永久置位，up 不激活。
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Move, link));
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Up, link, PointerButton.Left));

        Assert.Equal("unset", activated);
    }

    [Fact]
    public void 同URL不同Id_downA_upB_不激活()
    {
        object? activated = "unset";
        var view = new RichText(Linked("ab", 
                new TextInteractionRange(0, 1, 1, "same-url"),
                new TextInteractionRange(1, 2, 2, "same-url")))
        {
            OnLinkActivate = p => activated = p,
        };
        var app = App(view);
        var node = RichNode(app);
        var a = LinkCenter(node, 0);
        var b = LinkCenter(node, 1);

        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Move, a));
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Down, a, PointerButton.Left));
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Move, b));
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Up, b, PointerButton.Left));

        Assert.Equal("unset", activated);
    }

    [Fact]
    public void PointerCancel_不激活()
    {
        object? activated = "unset";
        var view = new RichText(Linked("click me", new TextInteractionRange(0, 5, 7, "payload-A")))
        {
            OnLinkActivate = p => activated = p,
        };
        var app = App(view);
        var node = RichNode(app);
        var link = LinkCenter(node, 0);

        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Move, link));
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Down, link, PointerButton.Left));
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Cancel, link));
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Up, link, PointerButton.Left));

        Assert.Equal("unset", activated);
    }

    [Fact]
    public void 非链接区按下_不消费不capture()
    {
        var view = new RichText(Linked("click me", new TextInteractionRange(0, 5, 7, "p")))
        {
            OnLinkActivate = _ => { },
        };
        var app = App(view);
        var node = RichNode(app);

        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Move, NonLinkPoint(node)));
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Down, NonLinkPoint(node), PointerButton.Left));

        // 未消费：view 的 OnPointerDown 返回 false，无 pressed state。
        var state = node.InteractionState as RichText.RichTextInteractionState;
        Assert.Null(state?.PressedId);
    }

    [Fact]
    public void OnLinkActivate为null_按下不capture()
    {
        var view = new RichText(Linked("click me", new TextInteractionRange(0, 5, 7, "p")));
        var app = App(view);
        var node = RichNode(app);

        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Move, LinkCenter(node, 0)));
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Down, LinkCenter(node, 0), PointerButton.Left));

        var state = node.InteractionState as RichText.RichTextInteractionState;
        Assert.Null(state?.PressedId);
    }

    [Fact]
    public void 悬停进入与离开_回调payload与null()
    {
        var seen = new List<object?>();
        var view = new RichText(Linked("click me", new TextInteractionRange(0, 5, 7, "hovered-payload")))
        {
            OnLinkHover = p => seen.Add(p),
        };
        var app = App(view);
        var node = RichNode(app);
        var link = LinkCenter(node, 0);

        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Move, link));
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Move, link)); // 同点不重复
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Move, NonLinkPoint(node)));

        Assert.Equal(new object?[] { "hovered-payload", null }, seen);
    }

    [Fact]
    public void LinkHoverStyle_悬停命中换paint_离开还原()
    {
        var hoverStyle = new TextPaintStyle { Foreground = Color.Red, Underline = true };
        var view = new RichText(Linked("hi", new TextInteractionRange(0, 1, 3, "p")))
        {
            LinkHoverStyle = hoverStyle,
        };
        var app = App(view);
        var node = RichNode(app);
        var cache = (RichText.RichTextViewCache)node.ViewCache!;
        var layout = cache.Layout!;
        var normalPaint = cache.Paint;
        app.Frame(); // 预热纹理键

        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Move, LinkCenter(node, 0)));
        // hover 重 paint（不重排版）：Paint 对象不同、Layout handle 不变。
        Assert.Same(layout, cache.Layout);
        Assert.NotSame(normalPaint, cache.Paint);
        var hoverPaint = cache.Paint;
        // 悬停 segment 前景已覆盖为 Red。
        var segIndex = layout.Segments[0].Start == 0 ? 0 : 1;
        Assert.Equal(Color.Red, hoverPaint.Runs[segIndex].Foreground);

        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Move, NonLinkPoint(node)));
        // 离开：paint 还原（非 hover 覆盖）；值相等判定，不 pin 对象引用。
        Assert.Equal(normalPaint, cache.Paint);
    }

    [Fact]
    public void 换内容清链接交互态_不stale激活()
    {
        object? activated = "unset";
        var host = new StyledTextHost(Linked("click me", new TextInteractionRange(0, 5, 7, "A")));
        host.OnLinkActivate = p => activated = p;
        var app = App(host);
        var node = app.Runtime.Root.Require<RichText>();
        var link = LinkCenter(node, 0);

        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Move, link));
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Down, link, PointerButton.Left));

        // 换内容：Interactions 全变 → OnUpdate 清 pressed/hover。
        host.SetContent(Linked("xy", new TextInteractionRange(0, 1, 99, "B")));
        app.Runtime.ProcessPendingWork();

        var state = node.InteractionState as RichText.RichTextInteractionState;
        Assert.Null(state?.PressedId);
        Assert.Equal("unset", activated);
    }

    [Fact]
    public void 祖先Disabled_按下不激活()
    {
        object? activated = "unset";
        var view = new RichText(Linked("click", new TextInteractionRange(0, 3, 1, "p")))
        {
            OnLinkActivate = p => activated = p,
        };
        var app = App(view.Disabled());
        var node = app.Runtime.Root.Require<RichText>();

        app.Click(LinkCenter(node, 0));

        Assert.Equal("unset", activated);
        var state = node.InteractionState as RichText.RichTextInteractionState;
        Assert.Null(state?.PressedId);
    }

    [Fact]
    public void 卸载后按下_不激活()
    {
        object? activated = "unset";
        var host = new StyledTextHost(Linked("click", new TextInteractionRange(0, 3, 1, "p")));
        host.OnLinkActivate = p => activated = p;
        var app = App(host);
        var node = app.Runtime.Root.Require<RichText>();
        var link = LinkCenter(node, 0);

        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Move, link));
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Down, link, PointerButton.Left));

        host.Visible.Value = false;
        app.Runtime.ProcessPendingWork();
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Up, link, PointerButton.Left));

        Assert.Equal("unset", activated);
    }

    [Fact]
    public void 捕获期间指针移到祖先clip外_命中被取消()
    {
        // ScrollView 把 RichText 推入 clip 内：按 link 后把指针移到 clip 外
        // （即使 layout 局部坐标仍落在 link 矩形上）——HitLinkAt 的 router 命中门
        // 必须把它判定为离区，up 不激活。
        object? activated = "unset";
        var rich = new RichText(Linked("click", new TextInteractionRange(0, 3, 1, "p")))
        {
            OnLinkActivate = p => activated = p,
        };
        // 高度 8 < 行高，RichText 大半被裁。
        var app = App(new ScrollView(new VStack(rich)) { ScrollbarStyle = ScrollbarStyle.Floating },
            viewport: new Size(200, 8));
        var node = app.Runtime.Root.Require<RichText>();
        var scroll = app.Runtime.Root.Require<ScrollView>();
        var link = LinkCenter(node, 0);
        // link 中心若恰在 clip 内用它，否则裁到 frame 边缘内 1px。
        var clipY = Math.Min(link.Y, scroll.Frame.Bottom - 1);
        var linkPt = new Point(link.X, clipY);

        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Move, linkPt));
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Down, linkPt, PointerButton.Left));
        // 移到 clip 下缘之外（scroll frame 下缘下方）。
        var outPt = new Point(linkPt.X, scroll.Frame.Bottom + 5);
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Move, outPt));
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Up, outPt, PointerButton.Left));

        Assert.Equal("unset", activated);
    }

    [Fact]
    public void 悬停后父组件重挂同值描述_paint恢复原始()
    {
        // 悬停进入 link → 无关 state 触发重建相同 RichText 描述（同 Content/LinkHoverStyle）→
        // OnUpdate 清 hover（回调 null）+ paint 恢复非覆盖色（不卡 hover 态）。
        object? lastHover = "unset";
        var host = new StyledTextHost(Linked("hi", new TextInteractionRange(0, 1, 3, "p")))
        {
            HoverStyle = new TextPaintStyle { Foreground = Color.Red },
            OnLinkHover = p => lastHover = p,
        };
        var app = App(host);
        var node = app.Runtime.Root.Require<RichText>();
        var cache = (RichText.RichTextViewCache)node.ViewCache!;
        var normalPaint = cache.Paint;

        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Move, LinkCenter(node, 0)));
        Assert.NotEqual(normalPaint, cache.Paint); // hover paint 已覆盖
        Assert.Equal("p", lastHover);

        // 无关重建：host 重 Build 相同描述 → OnUpdate 清 hover + paint 还原。
        host.Trigger!.Value++;
        app.Runtime.ProcessPendingWork();

        Assert.Null(lastHover); // hover 收敛收 null
        Assert.Equal(normalPaint, cache.Paint); // paint 还原（值相等）
    }
}
