using CSharp.UI;
using Xunit;

namespace CSharp.UI.Tests;

/// <summary>
/// R16：统一文字绘制契约（<see cref="TextPainter"/>）的绘制命令级回归。
/// 文字遮罩必须 1 纹素 = 1 物理像素、原点吸附到物理像素；一般图片纹理不受影响。
/// </summary>
/// <remarks>布局尺寸仍是小数逻辑坐标，只有纹理目标矩形与原点参与物理像素对齐。</remarks>
public class TextPhysicalPixelContractTests
{
    private static void AssertMapped(TextureCommand command, double pixelScale)
    {
        Assert.Equal(0, Math.IEEERemainder(command.Bounds.X * pixelScale, 1), 6);
        Assert.Equal(0, Math.IEEERemainder(command.Bounds.Y * pixelScale, 1), 6);
        Assert.Equal(command.Texture.PixelWidth, command.Bounds.Width * pixelScale, 6);
        Assert.Equal(command.Texture.PixelHeight, command.Bounds.Height * pixelScale, 6);
    }

    private static TextureCommand[] TextCommands(TestApp app)
        => [.. app.Frame().OfType<TextureCommand>().Where(command => command.Texture.IsAlphaMask)];

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void 文本按钮开关的文字纹理都按物理像素一对一映射(double pixelScale)
    {
        var roots = new View[]
        {
            // Padding 3.3 让逻辑原点在多数 DPI 下落在小数物理像素上。
            new VStack(new Text("中文字体边缘 English").WithFont(FontDescription.Default.WithSize(14))).Padding(3.3),
            new VStack(new Button("确定 OK")).Padding(3.3),
            new VStack(new Toggle(true, null, "开关 Switch")).Padding(3.3),
        };

        foreach (var root in roots)
        {
            using var app = new TestApp(root, new Size(400, 120), pixelScale);
            var texts = TextCommands(app);
            Assert.NotEmpty(texts);
            foreach (var text in texts)
            {
                AssertMapped(text, pixelScale);
            }
        }
    }

    [Fact]
    public void 一般图片纹理不做物理像素吸附()
    {
        using var app = new TestApp(
            new VStack(new Image("wide.png").Resize(ImageResizeMode.Fit).Frame(width: 101, height: 101, alignment: Alignment.Start)),
            new Size(400, 300),
            pixelScale: 1.25,
            configureImages: images => images.Register("wide.png", 200, 100));

        var image = Assert.Single(app.Frame().OfType<TextureCommand>());

        // 图片保持 Fit 计算出的逻辑目标（101×50.5），不因为纹理是 Alpha 或非 Alpha 被吸附/改尺寸。
        Assert.False(image.Texture.IsAlphaMask);
        Assert.Equal(101, image.Bounds.Width, 6);
        Assert.Equal(50.5, image.Bounds.Height, 6);
        Assert.NotEqual(image.Texture.PixelHeight, image.Bounds.Height * 1.25, 3);
        Assert.NotEqual(0, Math.IEEERemainder(image.Bounds.Height * 1.25, 1), 6);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void 滚动视图上下滚动后的文字仍按物理像素一对一映射(double pixelScale)
    {
        var content = new VStack(
            new Text("顶部说明").WithFont(FontDescription.Default.WithSize(14)),
            new Text("滚动内容 中文 English").WithFont(FontDescription.Default.WithSize(14)),
            new Text("第二行 second line").WithFont(FontDescription.Default.WithSize(14)),
            new Text("第三行 third line").WithFont(FontDescription.Default.WithSize(14)),
            new Text("第四行 fourth line").WithFont(FontDescription.Default.WithSize(14)))
            .Spacing(4);
        using var app = new TestApp(
            new VStack(new ScrollView(content).Frame(height: 60)),
            new Size(300, 200),
            pixelScale);

        var scroll = app.Runtime.Root.FindDescendant<ScrollView>()
            ?? throw new InvalidOperationException("未找到 ScrollView。");
        var before = TextCommands(app);
        Assert.NotEmpty(before);

        // 分数逻辑像素增量：制造小数滚动偏移，从而让文字逻辑原点落在小数物理像素上。
        app.Wheel(scroll.Frame.Center, -7.3, WheelDeltaMode.Pixels);
        Assert.True(scroll.ScrollOffsetY > 0, "内容应已向下滚动。");
        Assert.NotEqual(0, Math.IEEERemainder(scroll.ScrollOffsetY * pixelScale, 1), 6);

        var down = TextCommands(app);
        Assert.NotEmpty(down);
        foreach (var text in down)
        {
            AssertMapped(text, pixelScale);
        }

        Assert.NotEqual(
            before.Select(command => command.Bounds.Y).ToArray(),
            down.Select(command => command.Bounds.Y).ToArray());

        app.Wheel(scroll.Frame.Center, 3.7, WheelDeltaMode.Pixels);
        foreach (var text in TextCommands(app))
        {
            AssertMapped(text, pixelScale);
        }
    }
}
