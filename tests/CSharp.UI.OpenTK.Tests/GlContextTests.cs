using Xunit;

namespace CSharp.UI.OpenTK.Tests;

/// <summary>验证共享 GL 上下文可用、清屏与像素读回的基本行为。</summary>
[Collection(GraphicsCollection.Name)]
[Trait("Category", "Graphics")]
public sealed class GlContextTests
{
    private readonly GlContextFixture _fixture;

    /// <summary>注入共享上下文夹具。</summary>
    public GlContextTests(GlContextFixture fixture) => _fixture = fixture;

    [Fact]
    public void 上下文信息包含版本与渲染器()
    {
        Assert.False(string.IsNullOrWhiteSpace(_fixture.ContextDescription));
        Assert.Contains("/", _fixture.ContextDescription, StringComparison.Ordinal);
    }

    [Fact]
    public void 清屏后读回颜色一致()
    {
        using var scope = _fixture.CreateScope();
        var frame = new RenderFrame([], new Size(8, 6), 1.0, new Color(10, 20, 30));
        scope.Render(frame);

        Assert.Equal(8, scope.Surface.Size.Width);
        Assert.Equal(6, scope.Surface.Size.Height);
        AssertColor.Equal(new Color(10, 20, 30), scope.GetPixel(0, 0));
        AssertColor.Equal(new Color(10, 20, 30), scope.GetPixel(7, 5));
    }

    [Fact]
    public void 帧尺寸随DPI变化()
    {
        using var scope = _fixture.CreateScope();
        var frame = new RenderFrame([], new Size(8, 6), 2.0, Color.Black);
        scope.Render(frame);

        Assert.Equal(16, scope.Surface.Size.Width);
        Assert.Equal(12, scope.Surface.Size.Height);
        Assert.Equal(16, scope.Renderer.FramebufferSize.Width);
    }
}
