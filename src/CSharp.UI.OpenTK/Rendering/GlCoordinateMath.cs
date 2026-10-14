namespace CSharp.UI.OpenTK;

/// <summary>
/// 逻辑像素坐标与 OpenGL framebuffer 坐标之间的换算。
/// </summary>
/// <remarks>
/// <para>
/// 约定：逻辑坐标原点在内容区左上角、Y 轴向下；OpenGL 的 framebuffer 原点在左下角、Y 轴向上。
/// 渲染器把逻辑坐标乘以 DPI 缩放（<see cref="RenderFrame.PixelScale"/>）得到 framebuffer 像素，
/// 并在设置 <c>glScissor</c> 时翻转 Y 轴。
/// </para>
/// <para>
/// 本类型全部是纯函数，不依赖 GL 上下文与窗口，可在无显示环境下由单元测试直接验证。
/// </para>
/// </remarks>
public static class GlCoordinateMath
{
    /// <summary>把逻辑矩形换算为 framebuffer 像素矩形（仍使用左上原点、Y 轴向下）。</summary>
    public static Rect ToFramebufferRect(Rect logical, double pixelScale)
    {
        var scale = NormalizeScale(pixelScale);
        return new Rect(logical.X * scale, logical.Y * scale, logical.Width * scale, logical.Height * scale);
    }

    /// <summary>把逻辑点换算为 framebuffer 像素点（左上原点、Y 轴向下）。</summary>
    public static Point ToFramebufferPoint(Point logical, double pixelScale)
    {
        var scale = NormalizeScale(pixelScale);
        return new Point(logical.X * scale, logical.Y * scale);
    }

    /// <summary>把 framebuffer 像素点换算为 OpenGL 的归一化设备坐标（NDC，范围 -1—1，Y 轴向上）。</summary>
    public static (float X, float Y) ToNdc(Point framebufferPoint, Size framebufferSize)
    {
        var width = Math.Max(1.0, framebufferSize.Width);
        var height = Math.Max(1.0, framebufferSize.Height);
        return (
            (float)((framebufferPoint.X / width * 2.0) - 1.0),
            (float)(1.0 - (framebufferPoint.Y / height * 2.0)));
    }

    /// <summary>两个裁剪矩形的交集；任一为空或不相交时返回 <see cref="Rect.Empty"/>。</summary>
    public static Rect IntersectClip(Rect current, Rect pushed) => current.Intersect(pushed);

    /// <summary>
    /// 把逻辑裁剪矩形换算为 <c>glScissor(x, y, width, height)</c> 参数。
    /// </summary>
    /// <param name="logicalClip">逻辑像素裁剪矩形（左上原点）。</param>
    /// <param name="framebufferSize">framebuffer 像素尺寸。</param>
    /// <param name="pixelScale">逻辑像素 → framebuffer 像素的缩放。</param>
    /// <returns>裁剪框；空裁剪返回 <c>(0, 0, 0, 0)</c>。</returns>
    /// <remarks>
    /// 裁剪框会夹取到 framebuffer 范围内，并按 <c>y = framebufferHeight - bottom</c> 翻转 Y 轴，
    /// 使逻辑坐标的“上边”对应 OpenGL 的“高处”。
    /// </remarks>
    public static (int X, int Y, int Width, int Height) ToScissorRect(Rect logicalClip, Size framebufferSize, double pixelScale)
    {
        var framebufferWidth = Math.Max(0, (int)Math.Round(framebufferSize.Width, MidpointRounding.AwayFromZero));
        var framebufferHeight = Math.Max(0, (int)Math.Round(framebufferSize.Height, MidpointRounding.AwayFromZero));
        if (logicalClip.IsEmpty || framebufferWidth == 0 || framebufferHeight == 0)
        {
            return (0, 0, 0, 0);
        }

        var scale = NormalizeScale(pixelScale);
        var left = Clamp(Round(logicalClip.Left * scale), 0, framebufferWidth);
        var right = Clamp(Round(logicalClip.Right * scale), 0, framebufferWidth);
        var top = Clamp(Round(logicalClip.Top * scale), 0, framebufferHeight);
        var bottom = Clamp(Round(logicalClip.Bottom * scale), 0, framebufferHeight);

        var width = Math.Max(0, right - left);
        var height = Math.Max(0, bottom - top);
        if (width == 0 || height == 0)
        {
            return (0, 0, 0, 0);
        }

        return (left, framebufferHeight - bottom, width, height);
    }

    /// <summary>framebuffer 像素尺寸：逻辑视口尺寸 × DPI 缩放，向上取整以避免丢边。</summary>
    public static Size ToFramebufferSize(Size viewportSize, double pixelScale)
    {
        var scale = NormalizeScale(pixelScale);
        return new Size(
            Math.Max(1, Math.Ceiling(Math.Max(0, viewportSize.Width) * scale)),
            Math.Max(1, Math.Ceiling(Math.Max(0, viewportSize.Height) * scale)));
    }

    private static double NormalizeScale(double pixelScale) => pixelScale is > 0 and < double.PositiveInfinity ? pixelScale : 1.0;

    private static int Round(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);

    private static int Clamp(int value, int min, int max) => Math.Clamp(value, min, max);
}
