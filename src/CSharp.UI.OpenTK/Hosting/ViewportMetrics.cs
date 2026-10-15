namespace CSharp.UI.OpenTK;

/// <summary>
/// 三个坐标空间之间的换算：GLFW 窗口内容区坐标、UI 逻辑像素、framebuffer 像素。
/// </summary>
/// <remarks>
/// <para>
/// 设 UI 缩放为 <c>D</c>（窗口当前内容缩放，来自 <c>glfwGetWindowContentScale</c>），
/// framebuffer 与窗口内容区之比为 <c>R</c>（<c>FramebufferSize / ClientSize</c>），则：
/// 逻辑视口 = framebuffer / D；鼠标逻辑坐标 = 窗口坐标 × R / D。
/// </para>
/// <para>
/// Windows 150% 时 <c>R=1</c>、<c>D=1.5</c>：600×450 物理像素的客户区对应 400×300 逻辑像素，鼠标坐标除以 1.5；
/// macOS Retina 时 <c>R=2</c>、<c>D=2</c>：窗口坐标（点）与逻辑像素一致，鼠标坐标保持原值，不会被二次放大。
/// </para>
/// <para>
/// 逻辑尺寸保留小数：framebuffer 无法被 D 整除时（例如 535 与 1.5）取整会让渲染器
/// <c>Ceiling(逻辑 × D)</c> 多出 1 像素。这里返回满足 <c>Ceiling(逻辑 × D) == framebuffer</c> 的最大逻辑长度，
/// 因此视口与实际 framebuffer 逐像素一致。
/// </para>
/// <para>
/// 原始内容缩放不可用（零、负数、NaN、无穷）时按轴回退：先取另一轴的有效内容缩放，
/// 两轴都不可用时才回退到 framebuffer/window 比例，保证不把无效值传给布局与纹理服务。
/// </para>
/// <para>全部为纯函数，不依赖窗口与 GL 上下文，可在无显示环境下直接单元测试。</para>
/// </remarks>
internal readonly struct ViewportMetrics
{
    /// <summary>可接受的最小内容缩放；更小的原始值视为不可用。</summary>
    internal const double MinimumContentScale = 0.05;

    /// <summary>可接受的最大内容缩放；更大的原始值视为不可用。</summary>
    internal const double MaximumContentScale = 64.0;

    private ViewportMetrics(
        Size windowSize,
        Size framebufferSize,
        Size logicalSize,
        double pixelScale,
        double framebufferRatioX,
        double framebufferRatioY)
    {
        WindowSize = windowSize;
        FramebufferSize = framebufferSize;
        LogicalSize = logicalSize;
        PixelScale = pixelScale;
        FramebufferRatioX = framebufferRatioX;
        FramebufferRatioY = framebufferRatioY;
    }

    /// <summary>GLFW 窗口内容区尺寸（屏幕坐标；Windows/X11 与物理像素 1:1，macOS 为点）。</summary>
    internal Size WindowSize { get; }

    /// <summary>framebuffer 尺寸（物理像素）。</summary>
    internal Size FramebufferSize { get; }

    /// <summary>UI 逻辑像素视口尺寸，等于 framebuffer / 内容缩放。</summary>
    internal Size LogicalSize { get; }

    /// <summary>UI 缩放 D（逻辑像素 → framebuffer 像素）。</summary>
    internal double PixelScale { get; }

    /// <summary>framebuffer / 窗口内容区宽度比 R（Windows 150% 为 1，macOS Retina 为 2）。</summary>
    internal double FramebufferRatioX { get; }

    /// <summary>framebuffer / 窗口内容区高度比 R。</summary>
    internal double FramebufferRatioY { get; }

    /// <summary>指针换算系数：窗口 X 坐标乘以它得到逻辑 X 坐标（R / D）。</summary>
    internal double PointerScaleX => FramebufferRatioX / PixelScale;

    /// <summary>指针换算系数：窗口 Y 坐标乘以它得到逻辑 Y 坐标（R / D）。</summary>
    internal double PointerScaleY => FramebufferRatioY / PixelScale;

    /// <summary>framebuffer 是否有可渲染的像素范围。</summary>
    internal bool IsRenderable => FramebufferSize.Width > 0 && FramebufferSize.Height > 0;

    /// <summary>把 GLFW 窗口内容区坐标换算为逻辑坐标。</summary>
    internal Point ToLogicalPoint(double windowX, double windowY)
        => new(windowX * PointerScaleX, windowY * PointerScaleY);

    /// <summary>
    /// 计算换算结果。
    /// </summary>
    /// <param name="windowWidth">GLFW 窗口内容区宽度（屏幕坐标）。</param>
    /// <param name="windowHeight">GLFW 窗口内容区高度（屏幕坐标）。</param>
    /// <param name="framebufferWidth">framebuffer 宽度（物理像素）。</param>
    /// <param name="framebufferHeight">framebuffer 高度（物理像素）。</param>
    /// <param name="contentScaleX">窗口内容缩放 X（原始值，可为零/非有限数，此时回退）。</param>
    /// <param name="contentScaleY">窗口内容缩放 Y（原始值，可为零/非有限数，此时回退）。</param>
    internal static ViewportMetrics Create(
        int windowWidth,
        int windowHeight,
        int framebufferWidth,
        int framebufferHeight,
        double contentScaleX,
        double contentScaleY)
    {
        var window = new Size(Math.Max(0, windowWidth), Math.Max(0, windowHeight));
        var framebuffer = new Size(Math.Max(0, framebufferWidth), Math.Max(0, framebufferHeight));

        var ratioX = Ratio(framebuffer.Width, window.Width);
        var ratioY = Ratio(framebuffer.Height, window.Height);

        // 布局与渲染只需要一个标量缩放：先取有效的内容缩放轴（X 优先，其次 Y），
        // 两轴都不可用时才回退到 framebuffer/window 比，避免 X 轴的回退值遮住有效的 Y 轴缩放。
        var scale = IsValidContentScale(contentScaleX) ? contentScaleX
            : IsValidContentScale(contentScaleY) ? contentScaleY
            : ratioX;

        var logical = new Size(
            LogicalLength(framebuffer.Width, scale),
            LogicalLength(framebuffer.Height, scale));

        return new ViewportMetrics(window, framebuffer, logical, scale, ratioX, ratioY);
    }

    /// <summary>
    /// 让 framebuffer 覆盖指定逻辑尺寸所需的 GLFW 窗口内容区尺寸（屏幕坐标）。
    /// </summary>
    /// <remarks>
    /// 用当前测得的 R 与 D 反推：窗口尺寸 = 逻辑尺寸 × D / R。
    /// Windows 150%（R=1、D=1.5）会把 400×300 逻辑换算为 600×450；
    /// macOS Retina（R=2、D=2）保持窗口坐标不变，不会二次放大。
    /// </remarks>
    internal (int Width, int Height) WindowSizeForLogicalSize(double logicalWidth, double logicalHeight)
    {
        var width = logicalWidth * PixelScale / FramebufferRatioX;
        var height = logicalHeight * PixelScale / FramebufferRatioY;
        // 向上取整：framebuffer = 窗口尺寸 × R 必须完整覆盖 逻辑尺寸 × D，不能少 1 像素。
        return (
            Math.Max(1, (int)Math.Ceiling(FiniteOrZero(width))),
            Math.Max(1, (int)Math.Ceiling(FiniteOrZero(height))));
    }

    private static double Ratio(double framebufferLength, double windowLength)
        => windowLength > 0 && framebufferLength > 0 ? framebufferLength / windowLength : 1.0;

    /// <summary>
    /// 逻辑长度：framebuffer 长度 ÷ 缩放，并保证 <c>Ceiling(结果 × 缩放) == framebuffer 长度</c>。
    /// </summary>
    /// <remarks>
    /// 浮点误差可能让 <c>(framebuffer / scale) × scale</c> 略微大于真实 framebuffer（例如 535 与 1.5 得到
    /// 535.0000000000001），渲染器据此 <c>Ceiling</c> 会多出 1 像素。这里按最小步长向下修正，
    /// 使逻辑视口与真实 framebuffer 逐像素一致，而不是靠容差掩盖。
    /// </remarks>
    private static double LogicalLength(double framebufferLength, double scale)
    {
        if (!(framebufferLength > 0) || !double.IsFinite(framebufferLength))
        {
            return 0;
        }

        var logical = framebufferLength / scale;
        for (var step = 0; step < 4 && Math.Ceiling(logical * scale) > framebufferLength; step++)
        {
            logical = Math.BitDecrement(logical);
        }

        return logical;
    }

    private static bool IsValidContentScale(double scale)
        => double.IsFinite(scale) && scale >= MinimumContentScale && scale <= MaximumContentScale;

    private static double FiniteOrZero(double value) => double.IsFinite(value) ? value : 0;
}
