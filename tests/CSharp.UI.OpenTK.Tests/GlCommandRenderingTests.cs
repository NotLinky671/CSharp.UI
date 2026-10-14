using Xunit;

namespace CSharp.UI.OpenTK.Tests;

/// <summary>
/// 离屏 framebuffer 绘制结果测试：填充、描边、圆角、层叠顺序、透明混合、裁剪与 DPI。
/// </summary>
/// <remarks>
/// 坐标约定：逻辑矩形 <c>(x, y, w, h)</c> 在 scale=1 时覆盖 framebuffer 像素 <c>x…x+w-1</c>（左闭右开），
/// 与光栅化规则一致。
/// </remarks>
[Collection(GraphicsCollection.Name)]
[Trait("Category", "Graphics")]
public sealed class GlCommandRenderingTests
{
    private static readonly Color Background = new(255, 255, 255);

    private readonly GlContextFixture _fixture;

    /// <summary>注入共享上下文夹具。</summary>
    public GlCommandRenderingTests(GlContextFixture fixture) => _fixture = fixture;

    [Fact]
    public void 背景与实心矩形()
    {
        using var scope = _fixture.CreateScope();
        var frame = Frame(
            new Size(32, 32),
            1.0,
            new RectangleCommand(new Rect(8, 8, 16, 16), Color.Red));

        var stats = scope.Render(frame);

        Assert.Equal(1, stats.RectCommands);
        Assert.Equal(1, stats.Batches);
        AssertColor.Equal(Color.Red, scope.GetPixel(8, 8));
        AssertColor.Equal(Color.Red, scope.GetPixel(23, 23));
        AssertColor.Equal(Background, scope.GetPixel(7, 8));
        AssertColor.Equal(Background, scope.GetPixel(24, 8));
        AssertColor.Equal(Background, scope.GetPixel(8, 24));
    }

    [Fact]
    public void 描边只覆盖边界内侧()
    {
        using var scope = _fixture.CreateScope();
        var frame = Frame(
            new Size(32, 32),
            1.0,
            new RectangleCommand(new Rect(8, 8, 16, 16), Color.Blue, 0, Color.Red, 2));

        scope.Render(frame);

        // 描边以边界为中心线向内绘制，宽度 2 → 像素 8、9 为红色，中心保持蓝色。
        AssertColor.Equal(Color.Red, scope.GetPixel(8, 8));
        AssertColor.Equal(Color.Red, scope.GetPixel(9, 16));
        AssertColor.Equal(Color.Blue, scope.GetPixel(10, 16));
        AssertColor.Equal(Color.Blue, scope.GetPixel(16, 16));
        AssertColor.Equal(Color.Red, scope.GetPixel(22, 16));
        AssertColor.Equal(Color.Blue, scope.GetPixel(21, 16));
    }

    [Fact]
    public void 圆角外像素保持背景色()
    {
        using var scope = _fixture.CreateScope();
        var frame = Frame(
            new Size(32, 32),
            1.0,
            new RectangleCommand(new Rect(8, 8, 16, 16), Color.Red, 8));

        scope.Render(frame);

        // 半径等于半尺寸时是内切圆：四角外侧必须保持背景色。
        AssertColor.Equal(Background, scope.GetPixel(8, 8));
        AssertColor.Equal(Background, scope.GetPixel(23, 23));
        AssertColor.Equal(Background, scope.GetPixel(9, 9));
        AssertColor.Equal(Color.Red, scope.GetPixel(16, 16));
        // 圆内靠近中心轴的像素仍是填充色。
        AssertColor.Equal(Color.Red, scope.GetPixel(16, 9));
        AssertColor.Equal(Color.Red, scope.GetPixel(9, 16));
    }

    [Fact]
    public void 直角矩形四角被填充()
    {
        using var scope = _fixture.CreateScope();
        var frame = Frame(
            new Size(32, 32),
            1.0,
            new RectangleCommand(new Rect(8, 8, 16, 16), Color.Red, 0));

        scope.Render(frame);

        AssertColor.Equal(Color.Red, scope.GetPixel(8, 8));
        AssertColor.Equal(Color.Red, scope.GetPixel(23, 8));
        AssertColor.Equal(Color.Red, scope.GetPixel(8, 23));
        AssertColor.Equal(Color.Red, scope.GetPixel(23, 23));
    }

    [Fact]
    public void ZStack层叠顺序后者覆盖前者()
    {
        using var scope = _fixture.CreateScope();
        var frame = Frame(
            new Size(32, 32),
            1.0,
            new RectangleCommand(new Rect(0, 0, 20, 20), Color.Red),
            new RectangleCommand(new Rect(10, 10, 20, 20), Color.Blue));

        var stats = scope.Render(frame);

        Assert.Equal(2, stats.RectCommands);
        AssertColor.Equal(Color.Red, scope.GetPixel(5, 5));
        AssertColor.Equal(Color.Blue, scope.GetPixel(15, 15));
        AssertColor.Equal(Color.Blue, scope.GetPixel(25, 25));
    }

    [Fact]
    public void 半透明内容按预乘混合()
    {
        using var scope = _fixture.CreateScope();
        var halfRed = new Color(255, 0, 0, 128);
        var frame = Frame(
            new Size(8, 8),
            1.0,
            new RectangleCommand(new Rect(0, 0, 8, 8), halfRed));

        scope.Render(frame);

        // src 预乘 (0.502, 0, 0, 0.502) 叠加在白色上 → (1.0, 0.498, 0.498)。
        AssertColor.Equal(new Color(255, 127, 127), scope.GetPixel(4, 4));
    }

    [Fact]
    public void 命令不透明度与颜色alpha相乘()
    {
        using var scope = _fixture.CreateScope();
        var frame = Frame(
            new Size(8, 8),
            1.0,
            new RectangleCommand(new Rect(0, 0, 8, 8), Color.Red, 0, null, 0, 0.5));

        scope.Render(frame);

        AssertColor.Equal(new Color(255, 127, 127), scope.GetPixel(4, 4));
    }

    [Fact]
    public void 完全透明的矩形不产生绘制()
    {
        using var scope = _fixture.CreateScope();
        var frame = Frame(
            new Size(8, 8),
            1.0,
            new RectangleCommand(new Rect(0, 0, 8, 8), Color.Transparent));

        var stats = scope.Render(frame);

        Assert.Equal(0, stats.RectCommands);
        Assert.Equal(1, stats.SkippedCommands);
        Assert.Equal(0, stats.Batches);
        AssertColor.Equal(Background, scope.GetPixel(4, 4));
    }

    [Fact]
    public void 裁剪限制绘制范围()
    {
        using var scope = _fixture.CreateScope();
        var frame = Frame(
            new Size(32, 32),
            1.0,
            new PushClipCommand(new Rect(0, 0, 16, 32)),
            new RectangleCommand(new Rect(0, 0, 32, 32), Color.Red),
            new PopClipCommand());

        var stats = scope.Render(frame);

        Assert.Equal(1, stats.ClipPushes);
        AssertColor.Equal(Color.Red, scope.GetPixel(8, 8));
        AssertColor.Equal(Color.Red, scope.GetPixel(15, 31));
        AssertColor.Equal(Background, scope.GetPixel(16, 8));
        AssertColor.Equal(Background, scope.GetPixel(31, 31));
    }

    [Fact]
    public void 嵌套裁剪取交集()
    {
        using var scope = _fixture.CreateScope();
        var frame = Frame(
            new Size(32, 32),
            1.0,
            new PushClipCommand(new Rect(0, 0, 20, 20)),
            new PushClipCommand(new Rect(10, 10, 20, 20)),
            new RectangleCommand(new Rect(0, 0, 32, 32), Color.Green),
            new PopClipCommand(),
            new PopClipCommand());

        var stats = scope.Render(frame);

        Assert.Equal(2, stats.ClipPushes);
        AssertColor.Equal(Color.Green, scope.GetPixel(12, 12));
        AssertColor.Equal(Color.Green, scope.GetPixel(19, 19));
        AssertColor.Equal(Background, scope.GetPixel(5, 5));
        AssertColor.Equal(Background, scope.GetPixel(22, 22));
    }

    [Fact]
    public void 空裁剪跳过其中全部绘制()
    {
        using var scope = _fixture.CreateScope();
        var frame = Frame(
            new Size(32, 32),
            1.0,
            new PushClipCommand(new Rect(40, 40, 8, 8)),
            new RectangleCommand(new Rect(0, 0, 32, 32), Color.Red),
            new PopClipCommand());

        var stats = scope.Render(frame);

        Assert.Equal(0, stats.RectCommands);
        Assert.Equal(1, stats.SkippedCommands);
        AssertColor.Equal(Background, scope.GetPixel(16, 16));
    }

    [Fact]
    public void 裁剪状态变化会打断批处理()
    {
        using var scope = _fixture.CreateScope();

        // 状态相同的相邻矩形合并为一次绘制。
        var merged = scope.Render(Frame(
            new Size(16, 16),
            1.0,
            new RectangleCommand(new Rect(0, 0, 8, 16), Color.Red),
            new RectangleCommand(new Rect(8, 0, 8, 16), Color.Blue)));
        Assert.Equal(1, merged.Batches);
        Assert.Equal(1, merged.DrawCalls);

        // 中间插入裁剪变化：不得跨裁剪状态合并，且后绘制的层不被裁剪影响。
        var clipped = scope.Render(Frame(
            new Size(32, 32),
            1.0,
            new RectangleCommand(new Rect(0, 0, 8, 32), Color.Red),
            new PushClipCommand(new Rect(16, 0, 16, 32)),
            new RectangleCommand(new Rect(0, 0, 32, 32), Color.Blue),
            new PopClipCommand()));
        Assert.Equal(2, clipped.Batches);
        Assert.Equal(2, clipped.RectCommands);
        AssertColor.Equal(Color.Red, scope.GetPixel(2, 10));
        AssertColor.Equal(Color.Blue, scope.GetPixel(20, 10));
        AssertColor.Equal(Background, scope.GetPixel(10, 10));
    }

    [Fact]
    public void 高DPI下裁剪框位置正确()
    {
        using var scope = _fixture.CreateScope();
        var frame = Frame(
            new Size(16, 16),
            2.0,
            new PushClipCommand(new Rect(4, 4, 8, 8)),
            new RectangleCommand(new Rect(0, 0, 16, 16), Color.Red),
            new PopClipCommand());

        var stats = scope.Render(frame);

        Assert.Equal(32, scope.Surface.Size.Width);
        Assert.Equal(32, scope.Surface.Size.Height);
        Assert.Equal(1, stats.RectCommands);

        // 逻辑 (4,4)+(8,8) → framebuffer (8,8)+(16,16)，读回坐标同样是左上原点。
        AssertColor.Equal(Color.Red, scope.GetPixel(8, 8));
        AssertColor.Equal(Color.Red, scope.GetPixel(23, 23));
        AssertColor.Equal(Background, scope.GetPixel(7, 8));
        AssertColor.Equal(Background, scope.GetPixel(8, 7));
        AssertColor.Equal(Background, scope.GetPixel(24, 24));
        AssertColor.Equal(Background, scope.GetPixel(23, 24));
    }

    [Fact]
    public void 高DPI下圆角半径按缩放放大()
    {
        using var scope = _fixture.CreateScope();
        var frame = Frame(
            new Size(16, 16),
            2.0,
            new RectangleCommand(new Rect(0, 0, 16, 16), Color.Red, 8));

        scope.Render(frame);

        // 半径 8 逻辑像素 = 16 framebuffer 像素，恰好是内切圆。
        AssertColor.Equal(Background, scope.GetPixel(0, 0));
        AssertColor.Equal(Color.Red, scope.GetPixel(16, 16));
        AssertColor.Equal(Color.Red, scope.GetPixel(16, 1));
        AssertColor.Equal(Background, scope.GetPixel(1, 1));
    }

    [Fact]
    public void 多余的裁剪弹出被记录为诊断()
    {
        using var scope = _fixture.CreateScope();
        var stats = scope.Render(Frame(new Size(8, 8), 1.0, new PopClipCommand()));

        Assert.Equal(1, stats.UnbalancedClipPops);
    }

    private RenderFrame Frame(Size viewport, double scale, params DrawCommand[] commands)
        => new(commands, viewport, scale, Background);
}
