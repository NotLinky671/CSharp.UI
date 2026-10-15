using CSharp.UI.Testing;
using OpenTK.Windowing.Common;
using Xunit;

namespace CSharp.UI.OpenTK.Tests;

/// <summary>
/// R14：从真实输入适配器（GLFW 归一化后的普通一格 <c>±1</c>）到 <see cref="ScrollView"/> 的集成回归。
/// </summary>
/// <remarks>
/// 宿主把“窗口坐标 → 逻辑坐标”的系数（R/D）交给适配器；滚轮增量是归一化步数，不随 DPI 缩放。
/// Windows 上普通鼠标一格是 <c>±1</c>，必须换算成 48 逻辑像素而不是 1 像素。
/// </remarks>
public sealed class WheelScrollIntegrationTests
{
    private static VStack LongContent(int count)
        => new([.. Enumerable.Range(0, count).Select(i => (View)new Text($"line {i}"))]);

    /// <summary>先在窗口坐标里把指针移到滚动容器中心，再投递滚轮（GLFW 滚轮回调不携带坐标）。</summary>
    private static (OpenTkInputAdapter Adapter, UiRuntime Runtime, ViewNode Scroll) Setup(double pixelScale, double notches, double deltaX = 0)
    {
        var (adapter, runtime) = CreateAdapter(new VStack(new ScrollView(LongContent(60))), new Size(200, 200), pixelScale);
        var scroll = runtime.Root.FindDescendant<ScrollView>()!;
        var center = scroll.Frame.Center;

        // Windows：窗口内容区是物理像素、R=1，窗口 → 逻辑系数为 1/D。
        adapter.HandleMouseMove(new MouseMoveEventArgs((float)(center.X * pixelScale), (float)(center.Y * pixelScale), 0, 0));
        adapter.HandleWheel(new MouseWheelEventArgs((float)deltaX, (float)notches));
        runtime.ProcessPendingWork();
        return (adapter, runtime, scroll);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void 适配器普通一格在任意DPI下滚动48逻辑像素(double pixelScale)
    {
        var (adapter, _, scroll) = Setup(pixelScale, -1);

        Assert.Equal(48, scroll.ScrollOffsetY, 6);
        Assert.Equal(WheelDeltaMode.Steps, adapter.LastWheelEvent!.Value.Mode);
        Assert.Equal(-1.0, adapter.LastWheelEvent!.Value.DeltaY, 6);
    }

    [Fact]
    public void 适配器小数步数按比例滚动()
    {
        var (_, _, scroll) = Setup(1.0, -0.25);

        // 四分之一格 = 12 逻辑像素，小数步数被保留而不是截断成整格。
        Assert.Equal(12, scroll.ScrollOffsetY, 6);
    }

    [Fact]
    public void 适配器向上与向下滚轮对称且回到起点()
    {
        var (adapter, runtime, scroll) = Setup(1.0, -1);
        Assert.Equal(48, scroll.ScrollOffsetY, 6);

        var center = scroll.Frame.Center;
        adapter.HandleMouseMove(new MouseMoveEventArgs((float)center.X, (float)center.Y, 0, 0));
        adapter.HandleWheel(new MouseWheelEventArgs(0, 1f));
        runtime.ProcessPendingWork();

        Assert.Equal(0, scroll.ScrollOffsetY, 6);
    }

    [Fact]
    public void 适配器空白区域滚轮不影响滚动()
    {
        var (adapter, runtime, scroll) = Setup(1.0, -1);
        Assert.Equal(48, scroll.ScrollOffsetY, 6);

        // 视口外的窗口坐标：命中测试不落到滚动容器，事件不产生滚动。
        adapter.HandleMouseMove(new MouseMoveEventArgs(5000, 5000, 0, 0));
        adapter.HandleWheel(new MouseWheelEventArgs(0, -1f));
        runtime.ProcessPendingWork();

        Assert.Equal(48, scroll.ScrollOffsetY, 6);
    }

    /// <summary>只读核对 Windows 宿主服务：返回系统当前值，或非 Windows/查询失败时回退默认。</summary>
    /// <remarks>
    /// 按复审要求固定核对官方 SDK 数值（不是拿实现自己的常量当预期），并用文档字面量动作在服务调用前后各查一次，
    /// 确认查询不会修改系统设置：相邻的 <c>SPI_SETWHEELSCROLLLINES = 0x0069</c> /
    /// <c>SPI_SETWHEELSCROLLCHARS = 0x006D</c> 一旦误用会写入用户设置。
    /// </remarks>
    [Fact]
    public void Windows滚轮设置只读查询或回退默认()
    {
        // 固定核对官方 SDK 数值，防止 GET 误用 SET（0x0069 会修改系统行数）。
        Assert.Equal(0x0068u, WindowsWheelScrollSettingsService.SpiGetWheelScrollLines);
        Assert.Equal(0x006Cu, WindowsWheelScrollSettingsService.SpiGetWheelScrollChars);

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(WheelScrollSettings.Default, WindowsWheelScrollSettingsService.Instance.Settings);
            Assert.Equal(WheelScrollSettings.Default, WindowsWheelScrollSettingsService.Read());
            return;
        }

        // 用文档字面量独立查询作为预期，而不是复用实现常量。
        var expectedLines = ReadRaw(0x0068);
        var expectedChars = ReadRaw(0x006C);
        var settings = WindowsWheelScrollSettingsService.Instance.Settings;

        Assert.Equal(expectedLines, settings.LinesPerNotch);
        Assert.Equal(expectedChars, settings.CharsPerNotch);

        // 只读查询：调用服务与重复读取都不会改变系统当前设置。
        Assert.Equal(expectedLines, ReadRaw(0x0068));
        Assert.Equal(expectedChars, ReadRaw(0x006C));
        Assert.Equal(settings, WindowsWheelScrollSettingsService.Read());
    }

    /// <summary>用调用方给出的文档字面量动作只读查询一项设置；查询失败时回退默认值。</summary>
    private static uint ReadRaw(uint action)
        => WindowsWheelScrollSettingsService.TryQuery(action, out var value)
            ? value
            : WheelScrollSettings.DefaultNotches;

    private static (OpenTkInputAdapter Adapter, UiRuntime Runtime) CreateAdapter(View root, Size viewport, double pixelScale)
    {
        var runtime = new UiRuntime(root, new UiRuntimeServices
        {
            Dispatcher = new HeadlessUiDispatcher(),
            TextService = new StubTextService(),
        });
        runtime.SetViewport(viewport, pixelScale);
        runtime.ProcessPendingWork();
        var adapter = new OpenTkInputAdapter(runtime, () => { });
        adapter.UpdatePointerTransform(1 / pixelScale, 1 / pixelScale);
        return (adapter, runtime);
    }
}
