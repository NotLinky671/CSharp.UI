using Xunit;

namespace CSharp.UI.OpenTK.Tests;

/// <summary>
/// 着色器编译失败诊断与部分资源清理测试。
/// </summary>
[Collection(GraphicsCollection.Name)]
[Trait("Category", "Graphics")]
public sealed class GlShaderDiagnosticsTests
{
    private readonly GlContextFixture _fixture;

    /// <summary>注入共享上下文夹具。</summary>
    public GlShaderDiagnosticsTests(GlContextFixture fixture) => _fixture = fixture;

    [Fact]
    public void 片元着色器编译失败报告日志并释放已创建的着色器()
    {
        _fixture.Surface.MakeCurrent();
        var shadersBefore = GlResourceCounters.GetLiveCount(GlResourceKind.Shader);
        var programsBefore = GlResourceCounters.GetLiveCount(GlResourceKind.Program);

        var invalidFragment = "#version 330 core\nvoid main() { 这不是合法的 GLSL }\n";
        var exception = Assert.Throws<GlShaderException>(() => GlShaderProgram.Compile(
            GlShaderSources.RectangleVertex,
            invalidFragment,
            "diagnostics-test"));

        Assert.Contains("ERROR", exception.Log, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("片元着色器", exception.Message, StringComparison.Ordinal);
        Assert.NotEmpty(exception.ShaderSource);
        Assert.Equal("片元着色器", exception.Stage);

        // 顶点着色器已经创建成功，失败路径必须把它删除，且不应留下程序对象。
        Assert.Equal(shadersBefore, GlResourceCounters.GetLiveCount(GlResourceKind.Shader));
        Assert.Equal(programsBefore, GlResourceCounters.GetLiveCount(GlResourceKind.Program));
    }

    [Fact]
    public void 顶点着色器编译失败报告日志()
    {
        _fixture.Surface.MakeCurrent();
        var shadersBefore = GlResourceCounters.GetLiveCount(GlResourceKind.Shader);

        var exception = Assert.Throws<GlShaderException>(() => GlShaderProgram.Compile(
            "#version 330 core\nthis is not glsl\n",
            GlShaderSources.TextureFragment,
            "diagnostics-test"));

        Assert.Contains("ERROR", exception.Log, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("顶点着色器", exception.Stage);
        Assert.Equal(shadersBefore, GlResourceCounters.GetLiveCount(GlResourceKind.Shader));
    }

    [Fact]
    public void 正常编译的程序可以查询uniform并释放()
    {
        _fixture.Surface.MakeCurrent();
        var programsBefore = GlResourceCounters.GetLiveCount(GlResourceKind.Program);
        var shadersBefore = GlResourceCounters.GetLiveCount(GlResourceKind.Shader);

        using (var program = GlShaderProgram.Compile(GlShaderSources.RectangleVertex, GlShaderSources.RectangleFragment, "ok"))
        {
            Assert.False(program.IsDisposed);
            Assert.True(program.Handle != 0);
            Assert.True(program.GetUniformLocation("uViewport") >= 0);
            Assert.Equal(programsBefore + 1, GlResourceCounters.GetLiveCount(GlResourceKind.Program));
        }

        Assert.Equal(programsBefore, GlResourceCounters.GetLiveCount(GlResourceKind.Program));
        // 链接完成后着色器对象立即释放。
        Assert.Equal(shadersBefore, GlResourceCounters.GetLiveCount(GlResourceKind.Shader));
    }

    [Fact]
    public void 渲染器释放后程序与缓冲计数回落()
    {
        var before = Enum.GetValues<GlResourceKind>().ToDictionary(kind => kind, GlResourceCounters.GetLiveCount);
        var scope = _fixture.CreateScope();
        Assert.Equal(before[GlResourceKind.Program] + 2, GlResourceCounters.GetLiveCount(GlResourceKind.Program));
        Assert.Equal(2, scope.Renderer.LiveProgramCount);
        Assert.Equal(2, scope.Renderer.LiveBufferCount);
        Assert.Equal(2, scope.Renderer.LiveVertexArrayCount);

        scope.Renderer.Dispose();

        Assert.Equal(0, scope.Renderer.LiveProgramCount);
        Assert.Equal(0, scope.Renderer.LiveBufferCount);
        Assert.Equal(0, scope.Renderer.LiveVertexArrayCount);
        Assert.True(scope.Renderer.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => scope.Renderer.Submit(new RenderFrame([], new Size(4, 4), 1.0, Color.Black)));
        scope.Dispose();
    }

    [Fact]
    public void 上下文描述包含驱动信息()
    {
        Assert.False(string.IsNullOrWhiteSpace(_fixture.ContextDescription));
        Assert.DoesNotContain("没有 GL 上下文", _fixture.ContextDescription, StringComparison.Ordinal);
    }
}
