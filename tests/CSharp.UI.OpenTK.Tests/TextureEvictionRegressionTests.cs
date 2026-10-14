using CSharp.UI;
using CSharp.UI.Skia;
using CSharp.UI.Testing;
using Xunit;

namespace CSharp.UI.OpenTK.Tests;

/// <summary>
/// 代码审核返修 R03 的图形侧回归测试：缓存容量小于“当前帧纹理数量”时，
/// 整帧命令生成 + 真实离屏渲染仍必须把每个文本都画出来。
/// </summary>
[Collection(GraphicsCollection.Name)]
public class TextureEvictionRegressionTests
{
    private static readonly Color Background = Color.Black;

    private readonly GlContextFixture _fixture;

    public TextureEvictionRegressionTests(GlContextFixture fixture) => _fixture = fixture;

    [Fact]
    public void R03_FrameWithMoreTexturesThanCapacity_RendersEveryText()
    {
        _fixture.ClearSurface(Background);
        using var scope = _fixture.CreateScope(textureCapacity: 1);

        using var textService = new SkiaTextService();
        var dispatcher = new HeadlessUiDispatcher();
        using var runtime = new UiRuntime(
            new ZStack(
                new Rectangle(Background),
                new VStack(
                    new Text("AAAAAAAA").WithFont(FontDescription.Default.WithSize(24)),
                    new Text("BBBBBBBB").WithFont(FontDescription.Default.WithSize(24)))
                    .Spacing(8)
                    .Padding(24)),
            new UiRuntimeServices
            {
                Dispatcher = dispatcher,
                TextService = textService,
                TextureService = scope.Textures,
                Clock = new TestUiClock(),
            });

        // 暗色主题：前景为浅色，便于在黑色背景上检测“有字形像素”。
        runtime.SetEnvironment(new EnvironmentValues { Theme = Theme.Dark });
        runtime.SetViewport(new Size(256, 256), 1.0);
        runtime.Start();
        runtime.ProcessPendingWork();

        var frame = runtime.BuildFrame();
        var commands = frame.Commands.OfType<TextureCommand>().ToArray();
        Assert.Equal(2, commands.Length);
        Assert.All(commands, c => Assert.True(c.Texture.IsValid));

        // 帧内引用了两个纹理，容量 1 时允许临时超出，但不能淘汰本帧仍在引用的纹理。
        Assert.Equal(2, scope.Textures.LiveTextureCount);

        var stats = scope.Render(frame);
        Assert.Equal(0, stats.SkippedCommands);
        Assert.Equal(2, stats.TextureCommands);

        // 两个文本区域都必须出现字形像素（修复前第一个文本因纹理被淘汰而不绘制）。
        Assert.True(HasInk(scope, commands[0].Bounds), $"第一个文本没有绘制：{commands[0].Bounds}");
        Assert.True(HasInk(scope, commands[1].Bounds), $"第二个文本没有绘制：{commands[1].Bounds}");

        // 第二个文本停止使用后，经过一个帧边界回收：长期缓存仍然有界。
        // （上一帧引用的纹理会保护到下一个帧边界，避免刚用过的纹理被重复上传。）
        scope.Textures.BeginFrame();
        scope.Textures.BeginFrame();
        Assert.True(scope.Textures.LiveTextureCount <= 1, $"帧边界后仍存活 {scope.Textures.LiveTextureCount} 个纹理");
    }

    [Fact]
    public void R03_RepeatedFrames_DoNotGrowResources()
    {
        _fixture.ClearSurface(Background);
        using var scope = _fixture.CreateScope(textureCapacity: 2);

        using var textService = new SkiaTextService();
        var dispatcher = new HeadlessUiDispatcher();
        using var runtime = new UiRuntime(
            new ZStack(
                new Rectangle(Background),
                new VStack(
                    new Text("AAAAAAAA").WithFont(FontDescription.Default.WithSize(24)),
                    new Text("BBBBBBBB").WithFont(FontDescription.Default.WithSize(24)),
                    new Text("CCCCCCCC").WithFont(FontDescription.Default.WithSize(24)))
                    .Spacing(8)
                    .Padding(24)),
            new UiRuntimeServices
            {
                Dispatcher = dispatcher,
                TextService = textService,
                TextureService = scope.Textures,
                Clock = new TestUiClock(),
            });

        runtime.SetEnvironment(new EnvironmentValues { Theme = Theme.Dark });
        runtime.SetViewport(new Size(256, 256), 1.0);
        runtime.Start();
        runtime.ProcessPendingWork();

        // 先渲染一帧（此时才产生纹理），再重复提交同一场景。
        scope.Render(runtime.BuildFrame());
        var createdAfterFirst = scope.Textures.CreatedTextureCount;
        Assert.Equal(3, createdAfterFirst);

        for (var i = 0; i < 5; i++)
        {
            var frame = runtime.BuildFrame();
            scope.Render(frame);
        }

        // 重复提交同一场景：帧保护避免重复上传，存活数量受容量约束。
        Assert.Equal(createdAfterFirst, scope.Textures.CreatedTextureCount);
        Assert.True(scope.Textures.LiveTextureCount <= 3, $"存活纹理 {scope.Textures.LiveTextureCount} 超出 帧内容数量");
        Assert.Equal(0, scope.Textures.ReleasedTextureCount);
    }

    private static bool HasInk(GlTestScope scope, Rect bounds)
    {
        var left = Math.Max(0, (int)Math.Floor(bounds.X));
        var top = Math.Max(0, (int)Math.Floor(bounds.Y));
        var right = Math.Min(256, (int)Math.Ceiling(bounds.Right));
        var bottom = Math.Min(256, (int)Math.Ceiling(bounds.Bottom));
        for (var y = top; y < bottom; y++)
        {
            for (var x = left; x < right; x++)
            {
                var pixel = scope.GetPixel(x, y);
                if (pixel.R > 60 || pixel.G > 60 || pixel.B > 60)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
