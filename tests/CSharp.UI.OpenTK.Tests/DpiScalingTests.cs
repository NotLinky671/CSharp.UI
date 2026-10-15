using Xunit;

namespace CSharp.UI.OpenTK.Tests;

/// <summary>
/// R13：逻辑像素 / GLFW 窗口内容区坐标 / framebuffer 像素的换算（纯计算，不需要窗口与 GL 上下文）。
/// </summary>
/// <remarks>
/// 约定：D = 窗口内容缩放（<c>glfwGetWindowContentScale</c>），R = framebuffer / 窗口内容区之比。
/// 逻辑视口 = framebuffer / D；鼠标逻辑坐标 = 窗口坐标 × R / D。
/// </remarks>
public sealed class ViewportMetricsTests
{
    [Theory]
    [InlineData(1.0, 400, 300)]    // Windows 100%
    [InlineData(1.25, 500, 375)]   // Windows 125%
    [InlineData(1.5, 600, 450)]    // Windows 150%
    [InlineData(2.0, 800, 600)]    // Windows 200%
    public void 窗口坐标与物理像素一致时逻辑视口与鼠标按内容缩放换算(double scale, int windowWidth, int windowHeight)
    {
        var metrics = ViewportMetrics.Create(windowWidth, windowHeight, windowWidth, windowHeight, scale, scale);

        Assert.Equal(scale, metrics.PixelScale, 6);
        Assert.Equal(windowWidth / scale, metrics.LogicalSize.Width, 6);
        Assert.Equal(windowHeight / scale, metrics.LogicalSize.Height, 6);

        // 150% 下 400×300 逻辑尺寸对应 600×450 物理像素。
        Assert.Equal(windowWidth, metrics.LogicalSize.Width * metrics.PixelScale, 6);

        // 鼠标：窗口坐标 × R/D = 窗口坐标 / D。
        var pointer = metrics.ToLogicalPoint(150, 90);
        Assert.Equal(150 / scale, pointer.X, 6);
        Assert.Equal(90 / scale, pointer.Y, 6);
    }

    [Fact]
    public void Retina窗口坐标已是逻辑像素且不二次放大()
    {
        // macOS：窗口 400×300 点、framebuffer 800×600 像素、内容缩放 2。
        var metrics = ViewportMetrics.Create(400, 300, 800, 600, 2, 2);

        Assert.Equal(2.0, metrics.PixelScale, 6);
        Assert.Equal(2.0, metrics.FramebufferRatioX, 6);
        Assert.Equal(new Size(400, 300), metrics.LogicalSize);

        // R/D = 1：鼠标坐标保持原值，不会被再放大一遍。
        var pointer = metrics.ToLogicalPoint(150, 90);
        Assert.Equal(150, pointer.X, 6);
        Assert.Equal(90, pointer.Y, 6);
    }

    [Fact]
    public void Framebuffer为窗口两倍但内容缩放为一时按比例换算鼠标()
    {
        // GLFW 屏幕坐标与 framebuffer 不同的平台：R=2、D=1 → 逻辑视口 = framebuffer，鼠标坐标翻倍。
        var metrics = ViewportMetrics.Create(400, 300, 800, 600, 1, 1);

        Assert.Equal(1.0, metrics.PixelScale, 6);
        Assert.Equal(new Size(800, 600), metrics.LogicalSize);

        var pointer = metrics.ToLogicalPoint(150, 90);
        Assert.Equal(300, pointer.X, 6);
        Assert.Equal(180, pointer.Y, 6);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.5)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(0.001)]   // 小于最小可接受缩放
    [InlineData(1000.0)]  // 大于最大可接受缩放
    public void 无效内容缩放回退到framebuffer与窗口之比(double invalidScale)
    {
        var metrics = ViewportMetrics.Create(400, 300, 800, 600, invalidScale, invalidScale);

        Assert.True(double.IsFinite(metrics.PixelScale));
        Assert.Equal(2.0, metrics.PixelScale, 6);
        Assert.Equal(new Size(400, 300), metrics.LogicalSize);

        // 回退后 R/D = 1，鼠标坐标保持原值（等价于修复前的行为，且不传播无效值）。
        var pointer = metrics.ToLogicalPoint(150, 90);
        Assert.Equal(150, pointer.X, 6);
        Assert.Equal(90, pointer.Y, 6);
    }

    [Fact]
    public void X轴内容缩放无效时使用有效的Y轴()
    {
        // X 轴原始值无效、Y 轴有效：不能拿 X 轴的回退值（R=1）遮住有效的 2 倍缩放。
        var metrics = ViewportMetrics.Create(400, 300, 400, 300, double.NaN, 2);

        Assert.Equal(2.0, metrics.PixelScale, 6);
        Assert.Equal(new Size(200, 150), metrics.LogicalSize);
        Assert.Equal(0.5, metrics.PointerScaleX, 6);
        Assert.Equal(0.5, metrics.PointerScaleY, 6);
    }

    [Fact]
    public void Y轴内容缩放无效时使用有效的X轴()
    {
        var metrics = ViewportMetrics.Create(400, 300, 400, 300, 2, double.NaN);

        Assert.Equal(2.0, metrics.PixelScale, 6);
        Assert.Equal(new Size(200, 150), metrics.LogicalSize);
        Assert.Equal(0.5, metrics.PointerScaleX, 6);
        Assert.Equal(0.5, metrics.PointerScaleY, 6);
    }

    [Fact]
    public void 窗口尺寸为零时回退且不产生无效值()
    {
        var metrics = ViewportMetrics.Create(0, 0, 600, 450, 1.5, 1.5);

        Assert.Equal(1.5, metrics.PixelScale, 6);
        Assert.Equal(new Size(400, 300), metrics.LogicalSize);
        Assert.Equal(1.0, metrics.FramebufferRatioX, 6);
        Assert.Equal(1 / 1.5, metrics.PointerScaleX, 6);
        Assert.True(double.IsFinite(metrics.PointerScaleY));
    }

    [Fact]
    public void framebuffer为零时全部结果有限且PixelScale回退为1()
    {
        var metrics = ViewportMetrics.Create(0, 0, 0, 0, 0, 0);

        Assert.False(metrics.IsRenderable);
        Assert.Equal(Size.Zero, metrics.LogicalSize);
        Assert.Equal(1.0, metrics.PixelScale, 6);
        Assert.True(double.IsFinite(metrics.PointerScaleX));
        Assert.True(double.IsFinite(metrics.PointerScaleY));
        Assert.True(metrics.PointerScaleX > 0);
        Assert.True(metrics.PointerScaleY > 0);
    }

    [Theory]
    [InlineData(1.0, 400, 300)]    // 100%
    [InlineData(1.25, 500, 375)]   // 125%
    [InlineData(1.5, 600, 450)]    // 150%
    [InlineData(2.0, 800, 600)]    // 200%
    public void 初始窗口尺寸按逻辑尺寸与内容缩放反推(double scale, int expectedWidth, int expectedHeight)
    {
        // 刚创建、尚未按逻辑尺寸调整的窗口：窗口坐标 = 物理像素（Windows/X11），内容缩放来自系统。
        var metrics = ViewportMetrics.Create(400, 300, 400, 300, scale, scale);
        var (width, height) = metrics.WindowSizeForLogicalSize(400, 300);

        Assert.Equal(expectedWidth, width);
        Assert.Equal(expectedHeight, height);

        // 反推后的窗口 framebuffer = 窗口尺寸 × R（R=1），逻辑尺寸恰好回到请求值。
        var scaled = ViewportMetrics.Create(width, height, width, height, scale, scale);
        Assert.Equal(400, scaled.LogicalSize.Width, 6);
        Assert.Equal(300, scaled.LogicalSize.Height, 6);
    }

    [Fact]
    public void Retina初始窗口尺寸保持不变()
    {
        // macOS 窗口坐标是点：400×300 点已经对应 400×300 逻辑像素，不能再乘内容缩放。
        var metrics = ViewportMetrics.Create(400, 300, 800, 600, 2, 2);
        var (width, height) = metrics.WindowSizeForLogicalSize(400, 300);

        Assert.Equal(400, width);
        Assert.Equal(300, height);
    }

    [Theory]
    [InlineData(535, 401, 1.5)]
    [InlineData(533, 400, 1.25)]
    [InlineData(601, 334, 1.5)]
    [InlineData(701, 497, 1.75)]
    [InlineData(999, 667, 2.0)]
    public void 不可整除的framebuffer尺寸下逻辑视口与渲染器逐像素一致(int framebufferWidth, int framebufferHeight, double scale)
    {
        var metrics = ViewportMetrics.Create(framebufferWidth, framebufferHeight, framebufferWidth, framebufferHeight, scale, scale);

        // 逻辑尺寸保留小数，等于 framebuffer / D。
        Assert.Equal(framebufferWidth / scale, metrics.LogicalSize.Width, 9);
        Assert.Equal(framebufferHeight / scale, metrics.LogicalSize.Height, 9);

        // 渲染器由逻辑尺寸推导出的 framebuffer 必须与真实尺寸完全相同，不能多/少 1 像素。
        Assert.Equal(
            new Size(framebufferWidth, framebufferHeight),
            GlCoordinateMath.ToFramebufferSize(metrics.LogicalSize, metrics.PixelScale));

        // 输入换算与视口一致：窗口右下角对应逻辑右下角，且乘回 D 恰好还原窗口坐标。
        var bottomRight = metrics.ToLogicalPoint(framebufferWidth, framebufferHeight);
        Assert.Equal(metrics.LogicalSize.Width, bottomRight.X, 9);
        Assert.Equal(metrics.LogicalSize.Height, bottomRight.Y, 9);
        Assert.Equal(framebufferWidth, bottomRight.X * metrics.PixelScale, 9);
        Assert.Equal(framebufferHeight, bottomRight.Y * metrics.PixelScale, 9);
    }

    [Fact]
    public void 取整逻辑尺寸会让framebuffer多出像素()
    {
        // 535 ÷ 1.5 = 356.66…：四舍五入成 357 后渲染器会算出 536 像素，比真实 framebuffer 多 1。
        Assert.Equal(536, Math.Ceiling(357 * 1.5));

        var metrics = ViewportMetrics.Create(535, 401, 535, 401, 1.5, 1.5);
        Assert.NotEqual(357d, metrics.LogicalSize.Width);
        Assert.Equal(new Size(535, 401), GlCoordinateMath.ToFramebufferSize(metrics.LogicalSize, metrics.PixelScale));
    }

    [Fact]
    public void 小数比例下逻辑尺寸不被取整()
    {
        // 125% 下 framebuffer 533×400：逻辑尺寸是 426.4×320，不是 426×320。
        var metrics = ViewportMetrics.Create(533, 400, 533, 400, 1.25, 1.25);

        Assert.Equal(426.4, metrics.LogicalSize.Width, 9);
        Assert.Equal(320, metrics.LogicalSize.Height, 9);
        Assert.Equal(new Size(533, 400), GlCoordinateMath.ToFramebufferSize(metrics.LogicalSize, metrics.PixelScale));

        // 取整成 426 也能被 Ceiling 覆盖，但 426.4 才是与输入换算一致的正确约定：
        // 窗口 X 坐标 533 对应逻辑 426.4，而不是 426。
        Assert.Equal(426.4, metrics.ToLogicalPoint(533, 400).X, 9);
    }
}
