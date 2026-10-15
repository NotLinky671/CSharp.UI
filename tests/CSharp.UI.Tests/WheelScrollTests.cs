using CSharp.UI;
using Xunit;

namespace CSharp.UI.Tests;

/// <summary>
/// R14：滚轮步数 → 逻辑像素距离的换算（<see cref="WheelScrollSettings"/>）与 <see cref="ScrollView"/> 的实际滚动行为。
/// </summary>
/// <remarks>
/// 默认滚轮事件是归一化步数（真实适配器普通一格为 ±1），不是逻辑像素；像素式的辅助调用必须显式使用
/// <see cref="WheelDeltaMode.Pixels"/>。系统设置由 <see cref="StubWheelScrollSettingsService"/> 注入，不读取本机设置。
/// </remarks>
public class WheelScrollTests
{
    /// <summary>与真实适配器一致的普通一格：GLFW 在 Windows 上把 120 归一化为 1。</summary>
    private const double OneNotch = -1;

    private static VStack LongContent(int count)
        => new([.. Enumerable.Range(0, count).Select(i => (View)new Text($"line {i}"))]);

    private static View WideContent() => new Text("wide content").Frame(width: 1000, height: 50, alignment: Alignment.Start);

    // ---------------------------------------------------------------- 换算契约

    [Fact]
    public void 默认设置每格三行折合48逻辑像素()
    {
        var settings = WheelScrollSettings.Default;

        Assert.Equal(3u, settings.LinesPerNotch);
        Assert.Equal(3u, settings.CharsPerNotch);
        Assert.Equal(48, settings.StepDistance(horizontal: false, viewportExtent: 1000), 6);
        Assert.Equal(24, settings.StepDistance(horizontal: true, viewportExtent: 1000), 6);
    }

    [Fact]
    public void 每步距离不超过对应视口大小()
    {
        var settings = new WheelScrollSettings(1_000_000, 1_000_000);
        Assert.Equal(200, settings.StepDistance(horizontal: false, viewportExtent: 200), 6);
        Assert.Equal(200, settings.StepDistance(horizontal: true, viewportExtent: 200), 6);
    }

    [Fact]
    public void 整页设置使用视口大小()
    {
        var settings = new WheelScrollSettings(uint.MaxValue, uint.MaxValue);
        Assert.Equal(300, settings.StepDistance(horizontal: false, viewportExtent: 300), 6);
        Assert.Equal(180, settings.StepDistance(horizontal: true, viewportExtent: 180), 6);
    }

    [Fact]
    public void 零设置表示禁用()
    {
        var settings = new WheelScrollSettings(0, 0);
        Assert.True(settings.IsDisabled(horizontal: false));
        Assert.True(settings.IsDisabled(horizontal: true));
        Assert.Equal(0, settings.StepDistance(horizontal: false, viewportExtent: 300), 6);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-10)]
    public void 无效视口尺寸不产生无效距离(double viewportExtent)
    {
        Assert.Equal(0, WheelScrollSettings.Default.StepDistance(horizontal: false, viewportExtent), 6);
    }

    [Fact]
    public void 滚轮事件默认单位是步数()
    {
        var wheel = new WheelEvent(new Point(10, 10), 0, OneNotch);
        Assert.Equal(WheelDeltaMode.Steps, wheel.Mode);
        Assert.Equal(-1, wheel.DeltaY, 6);
    }

    // ---------------------------------------------------------------- 步数换算与 DPI

    [Fact]
    public void 一格向下滚动48逻辑像素()
    {
        using var app = new TestApp(new VStack(new ScrollView(LongContent(60))), new Size(200, 200));
        app.Frame();

        var scroll = app.Runtime.Root.Require<ScrollView>();
        app.Wheel(scroll.Frame.Center, OneNotch);
        app.Runtime.ProcessPendingWork();

        Assert.Equal(48, scroll.ScrollOffsetY, 6);
    }

    [Fact]
    public void 四分之一格滚动12逻辑像素并保留小数()
    {
        using var app = new TestApp(new VStack(new ScrollView(LongContent(60))), new Size(200, 200));
        app.Frame();

        var scroll = app.Runtime.Root.Require<ScrollView>();
        app.Wheel(scroll.Frame.Center, -0.25);

        Assert.Equal(12, scroll.ScrollOffsetY, 6);

        // 触摸板式的小数增量：0.1 格 = 4.8 逻辑像素，不被截断为整数。
        app.Wheel(scroll.Frame.Center, -0.1);
        Assert.Equal(16.8, scroll.ScrollOffsetY, 6);

        // 多次小数增量累积，不因取整而丢失。
        for (var i = 0; i < 5; i++)
        {
            app.Wheel(scroll.Frame.Center, -0.1);
        }

        Assert.Equal(40.8, scroll.ScrollOffsetY, 6);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void 不同DPI下一格逻辑距离一致(double pixelScale)
    {
        using var app = new TestApp(new VStack(new ScrollView(LongContent(60))), new Size(200, 200), pixelScale);
        app.Frame();

        var scroll = app.Runtime.Root.Require<ScrollView>();
        app.Wheel(scroll.Frame.Center, OneNotch);
        app.Runtime.ProcessPendingWork();

        // 滚轮步数换算为逻辑像素，与 DPI 无关：100%/150%/200% 下都是一格 48 逻辑像素。
        Assert.Equal(48, scroll.ScrollOffsetY, 6);
    }

    // ---------------------------------------------------------------- 系统设置

    [Fact]
    public void 行数为零时禁用滚动()
    {
        using var app = new TestApp(
            new VStack(new ScrollView(LongContent(60))),
            new Size(200, 200),
            wheelScrollSettings: new WheelScrollSettings(0, 3));
        app.Frame();

        var scroll = app.Runtime.Root.Require<ScrollView>();
        app.Wheel(scroll.Frame.Center, OneNotch);

        Assert.Equal(0, scroll.ScrollOffsetY, 6);

        // 设置恢复后立即生效：服务在每次滚轮事件时读取。
        app.WheelScroll.Settings = WheelScrollSettings.Default;
        app.Wheel(scroll.Frame.Center, OneNotch);
        Assert.Equal(48, scroll.ScrollOffsetY, 6);
    }

    [Fact]
    public void 整页设置一格滚动一个视口()
    {
        using var app = new TestApp(
            new VStack(new ScrollView(LongContent(60))),
            new Size(200, 200),
            wheelScrollSettings: new WheelScrollSettings(uint.MaxValue, uint.MaxValue));
        app.Frame();

        var scroll = app.Runtime.Root.Require<ScrollView>();
        app.Wheel(scroll.Frame.Center, OneNotch);
        app.Runtime.ProcessPendingWork();

        Assert.Equal(scroll.Frame.Height, scroll.ScrollOffsetY, 6);
    }

    [Fact]
    public void 超大行数被视口大小限制()
    {
        using var app = new TestApp(
            new VStack(new ScrollView(LongContent(60))),
            new Size(200, 200),
            wheelScrollSettings: new WheelScrollSettings(10_000_000, 10_000_000));
        app.Frame();

        var scroll = app.Runtime.Root.Require<ScrollView>();
        app.Wheel(scroll.Frame.Center, OneNotch);
        app.Runtime.ProcessPendingWork();

        Assert.Equal(scroll.Frame.Height, scroll.ScrollOffsetY, 6);
    }

    // ---------------------------------------------------------------- 边界与嵌套

    [Fact]
    public void 滚动到边界后夹紧且向上能回到起点()
    {
        using var app = new TestApp(new VStack(new ScrollView(LongContent(60))), new Size(200, 200));
        app.Frame();

        var scroll = app.Runtime.Root.Require<ScrollView>();
        var maxOffset = scroll.Children[0].DesiredSize.Height - scroll.Frame.Height;
        Assert.True(maxOffset > 48);

        for (var i = 0; i < 200; i++)
        {
            app.Wheel(scroll.Frame.Center, OneNotch);
            app.Runtime.ProcessPendingWork();
        }

        app.Frame();
        Assert.Equal(maxOffset, scroll.ScrollOffsetY, 6);
        Assert.True(scroll.ScrollOffsetY <= maxOffset);

        for (var i = 0; i < 200; i++)
        {
            app.Wheel(scroll.Frame.Center, -OneNotch);
            app.Runtime.ProcessPendingWork();
        }

        app.Frame();
        Assert.Equal(0, scroll.ScrollOffsetY, 6);
    }

    [Fact]
    public void 嵌套滚动容器内层到边界后由外层接手()
    {
        var inner = new ScrollView(LongContent(40), ScrollAxis.Vertical);
        var outer = new ScrollView(
            new VStack(new Text("header"), new FrameView(inner, height: 100), LongContent(40)),
            ScrollAxis.Vertical);

        using var app = new TestApp(new VStack(outer), new Size(200, 200));
        app.Frame();

        var outerNode = app.Runtime.Root.Require<ScrollView>();
        var innerNode = app.Runtime.All<ScrollView>().Last();

        // 一格步数在内部容器上滚动：内部先消费 48 逻辑像素。
        app.Wheel(innerNode.Frame.Center, OneNotch);
        app.Runtime.ProcessPendingWork();
        app.Frame();
        Assert.Equal(48, innerNode.ScrollOffsetY, 6);
        Assert.Equal(0, outerNode.ScrollOffsetY, 6);

        // 内部滚到末端后，继续滚动的步数由外层消费。
        for (var i = 0; i < 80; i++)
        {
            app.Wheel(innerNode.Frame.Center, OneNotch);
            app.Runtime.ProcessPendingWork();
        }

        app.Frame();
        Assert.True(innerNode.ScrollOffsetY > 0);
        Assert.True(outerNode.ScrollOffsetY > 0);
    }

    // ---------------------------------------------------------------- 水平

    [Fact]
    public void 水平滚动按字符换算且纵向增量可回退()
    {
        using var app = new TestApp(
            new VStack(new ScrollView(WideContent(), ScrollAxis.Horizontal)),
            new Size(200, 200));
        app.Frame();

        var scroll = app.Runtime.Root.Require<ScrollView>();
        Assert.Equal(ScrollAxis.Horizontal, Assert.IsType<ScrollView>(scroll.View).Axis);

        // 系统设置每格 3 字符 → 3 × 8 = 24 逻辑像素。
        app.WheelX(scroll.Frame.Center, OneNotch);
        app.Runtime.ProcessPendingWork();
        Assert.Equal(24, scroll.ScrollOffsetX, 6);
        Assert.Equal(0, scroll.ScrollOffsetY, 6);

        // 普通鼠标没有横向倾斜：DeltaX 为 0 时回退到 DeltaY，仍按字符距离滚动。
        app.Wheel(scroll.Frame.Center, OneNotch);
        app.Runtime.ProcessPendingWork();
        Assert.Equal(48, scroll.ScrollOffsetX, 6);
    }

    // ---------------------------------------------------------------- 显式像素单位

    [Fact]
    public void 像素单位事件直接使用逻辑像素()
    {
        using var app = new TestApp(new VStack(new ScrollView(LongContent(60))), new Size(200, 200));
        app.Frame();

        var scroll = app.Runtime.Root.Require<ScrollView>();

        // 显式像素：-1 只移动 1 逻辑像素，不会被当成一格（48）也不会被猜测放大。
        app.Wheel(scroll.Frame.Center, -1, WheelDeltaMode.Pixels);
        Assert.Equal(1, scroll.ScrollOffsetY, 6);

        app.Wheel(scroll.Frame.Center, -120, WheelDeltaMode.Pixels);
        Assert.Equal(121, scroll.ScrollOffsetY, 6);
    }

    // ---------------------------------------------------------------- 灵敏度

    [Fact]
    public void 灵敏度按倍数缩放滚动距离()
    {
        var scrollView = new ScrollView(LongContent(60)) { WheelSensitivity = 2.5 };
        using var app = new TestApp(new VStack(scrollView), new Size(200, 200));
        app.Frame();

        var scroll = app.Runtime.Root.Require<ScrollView>();
        app.Wheel(scroll.Frame.Center, OneNotch);
        app.Runtime.ProcessPendingWork();

        Assert.Equal(120, scroll.ScrollOffsetY, 6);
    }

    [Fact]
    public void 零灵敏度不滚动()
    {
        var scrollView = new ScrollView(LongContent(60)) { WheelSensitivity = 0 };
        using var app = new TestApp(new VStack(scrollView), new Size(200, 200));
        app.Frame();

        var scroll = app.Runtime.Root.Require<ScrollView>();
        app.Wheel(scroll.Frame.Center, OneNotch);

        Assert.Equal(0, scroll.ScrollOffsetY, 6);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-1.0)]
    public void 非有限或负灵敏度回退为默认值(double sensitivity)
    {
        var scrollView = new ScrollView(LongContent(60)) { WheelSensitivity = sensitivity };
        Assert.Equal(1.0, scrollView.WheelSensitivity, 6);

        using var app = new TestApp(new VStack(scrollView), new Size(200, 200));
        app.Frame();

        var scroll = app.Runtime.Root.Require<ScrollView>();
        app.Wheel(scroll.Frame.Center, OneNotch);
        app.Runtime.ProcessPendingWork();

        Assert.Equal(48, scroll.ScrollOffsetY, 6);
    }
}
