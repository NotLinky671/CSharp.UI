using Xunit;

namespace CSharp.UI.OpenTK.Tests;

/// <summary>
/// 逻辑坐标 ↔ framebuffer 坐标换算的纯 CPU 单元测试，不依赖 GL 上下文。
/// </summary>
public sealed class GlCoordinateMathTests
{
    [Fact]
    public void 裁剪框按帧高度翻转Y轴()
    {
        var clip = new Rect(10, 20, 30, 40);
        var (x, y, width, height) = GlCoordinateMath.ToScissorRect(clip, new Size(200, 100), 1.0);

        // 逻辑下边界 60 → OpenGL 的 y = 100 - 60 = 40。
        Assert.Equal(10, x);
        Assert.Equal(40, y);
        Assert.Equal(30, width);
        Assert.Equal(40, height);
    }

    [Fact]
    public void 裁剪框按DPI缩放()
    {
        var (x, y, width, height) = GlCoordinateMath.ToScissorRect(new Rect(10, 10, 10, 10), new Size(200, 200), 2.0);

        Assert.Equal(20, x);
        Assert.Equal(200 - 40, y);
        Assert.Equal(20, width);
        Assert.Equal(20, height);
    }

    [Fact]
    public void 空裁剪与零尺寸framebuffer返回零裁剪框()
    {
        Assert.Equal((0, 0, 0, 0), GlCoordinateMath.ToScissorRect(Rect.Empty, new Size(100, 100), 1.0));
        Assert.Equal((0, 0, 0, 0), GlCoordinateMath.ToScissorRect(new Rect(0, 0, 10, 10), new Size(0, 0), 1.0));
        Assert.Equal((0, 0, 0, 0), GlCoordinateMath.ToScissorRect(new Rect(5, 5, 0, 10), new Size(100, 100), 1.0));
    }

    [Fact]
    public void 裁剪框夹取到framebuffer范围内()
    {
        var (x, y, width, height) = GlCoordinateMath.ToScissorRect(new Rect(-10, -10, 300, 300), new Size(100, 50), 1.0);

        Assert.Equal(0, x);
        Assert.Equal(0, y);
        Assert.Equal(100, width);
        Assert.Equal(50, height);
    }

    [Fact]
    public void 嵌套裁剪取交集()
    {
        var current = new Rect(0, 0, 100, 100);
        var nested = GlCoordinateMath.IntersectClip(current, new Rect(50, 50, 100, 100));
        Assert.Equal(new Rect(50, 50, 50, 50), nested);

        var disjoint = GlCoordinateMath.IntersectClip(current, new Rect(200, 200, 10, 10));
        Assert.True(disjoint.IsEmpty);

        var empty = GlCoordinateMath.IntersectClip(current, Rect.Empty);
        Assert.True(empty.IsEmpty);
    }

    [Fact]
    public void NDC换算翻转Y轴()
    {
        var topLeft = GlCoordinateMath.ToNdc(new Point(0, 0), new Size(100, 50));
        Near(-1, topLeft.X);
        Near(1, topLeft.Y);

        var bottomRight = GlCoordinateMath.ToNdc(new Point(100, 50), new Size(100, 50));
        Near(1, bottomRight.X);
        Near(-1, bottomRight.Y);

        var center = GlCoordinateMath.ToNdc(new Point(50, 25), new Size(100, 50));
        Near(0, center.X);
        Near(0, center.Y);
    }

    private static void Near(double expected, double actual, double tolerance = 1e-4)
        => Assert.True(Math.Abs(expected - actual) <= tolerance, $"期望 {expected}，实际 {actual}（容差 {tolerance}）");

    [Fact]
    public void framebuffer尺寸向上取整()
    {
        Assert.Equal(new Size(10, 10), GlCoordinateMath.ToFramebufferSize(new Size(10, 10), 1.0));
        Assert.Equal(new Size(33, 24), GlCoordinateMath.ToFramebufferSize(new Size(11, 8), 3.0));
        Assert.Equal(new Size(17, 17), GlCoordinateMath.ToFramebufferSize(new Size(11, 11), 1.5));
    }

    [Fact]
    public void 逻辑矩形按缩放换算为framebuffer矩形()
    {
        var rect = GlCoordinateMath.ToFramebufferRect(new Rect(4, 6, 8, 10), 2.0);
        Assert.Equal(new Rect(8, 12, 16, 20), rect);
        Assert.Equal(new Point(8, 12), GlCoordinateMath.ToFramebufferPoint(new Point(4, 6), 2.0));
    }
}
