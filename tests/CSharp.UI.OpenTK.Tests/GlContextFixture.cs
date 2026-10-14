using CSharp.UI.OpenTK;
using Xunit;

namespace CSharp.UI.OpenTK.Tests;

/// <summary>
/// 图形测试集合：所有需要 GL 上下文的测试共用一个隐藏窗口与离屏 framebuffer。
/// </summary>
/// <remarks>
/// 共享同一个上下文可以避免每例创建窗口的开销，同时保证 GL 调用始终在同一线程（xunit 同一集合内串行执行）。
/// </remarks>
[CollectionDefinition(Name)]
public sealed class GraphicsCollection : ICollectionFixture<GlContextFixture>
{
    /// <summary>集合名称；测试类通过 <c>[Collection(GraphicsCollection.Name)]</c> 加入。</summary>
    public const string Name = "OpenTK 图形测试";
}

/// <summary>
/// 共享 GL 上下文夹具：创建隐藏窗口 + 离屏 framebuffer，并提供隔离的纹理服务与渲染器。
/// </summary>
/// <remarks>
/// 如果本机无法创建 GL 上下文（无显示、驱动缺失、沙箱限制），构造函数会抛出 <see cref="UiHostException"/>，
/// 消息中包含平台、GLFW 与驱动诊断；相关测试会以失败而不是“跳过”的形式暴露该环境事实。
/// </remarks>
public sealed class GlContextFixture : IDisposable
{
    private bool _disposed;

    /// <summary>创建隐藏窗口、GL 3.3 Core 上下文与 256×256 的离屏 framebuffer。</summary>
    public GlContextFixture()
    {
        // xunit 的进程内运行器在 worker 线程执行测试，而 GLFW 默认只允许主线程创建窗口；
        // Windows/Linux 上放开该限制即可让图形测试正常运行（macOS 仍要求主线程）。
        GlOffscreenSurface.AllowNonMainThreadCreation();
        try
        {
            Surface = new GlOffscreenSurface(256, 256);
        }
        catch (UiHostException ex)
        {
            throw new InvalidOperationException(
                $"无法在本机创建 OpenGL 上下文，图形测试无法执行。{Environment.NewLine}{ex.Diagnostics}",
                ex);
        }

        ContextDescription = Surface.ContextDescription;
    }

    /// <summary>共享的离屏 framebuffer（内部始终使用同一个 GL 上下文）。</summary>
    public GlOffscreenSurface Surface { get; }

    /// <summary>GL 上下文描述（版本 / 渲染器 / 厂商）。</summary>
    public string ContextDescription { get; }

    /// <summary>创建一套互相隔离的纹理服务与渲染器；由测试负责释放。</summary>
    public GlTestScope CreateScope(int textureCapacity = 128)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Surface.MakeCurrent();
        return new GlTestScope(Surface, new GlTextureService(textureCapacity));
    }

    /// <summary>清空离屏 framebuffer，避免测试之间相互影响。</summary>
    public void ClearSurface(Color color)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Surface.Clear(color);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Surface.Dispose();
    }
}

/// <summary>一套隔离的纹理服务与渲染器，保证资源计数断言不受其他测试影响。</summary>
public sealed class GlTestScope : IDisposable
{
    /// <summary>创建作用域并编译渲染器。</summary>
    public GlTestScope(GlOffscreenSurface surface, GlTextureService textures)
    {
        Surface = surface;
        Textures = textures;
        Surface.MakeCurrent();
        Renderer = new GlRenderer(textures);
    }

    /// <summary>所属离屏表面。</summary>
    public GlOffscreenSurface Surface { get; }

    /// <summary>本次测试独占的纹理服务。</summary>
    public GlTextureService Textures { get; }

    /// <summary>本次测试独占的渲染器。</summary>
    public GlRenderer Renderer { get; }

    /// <summary>渲染一帧并返回统计。</summary>
    public GlFrameStatistics Render(RenderFrame frame)
    {
        Surface.MakeCurrent();
        return Surface.Render(Renderer, frame);
    }

    /// <summary>读取左上原点像素（非预乘颜色）。</summary>
    public Color GetPixel(int x, int y) => Surface.GetPixel(x, y);

    /// <inheritdoc />
    public void Dispose()
    {
        Surface.MakeCurrent();
        Renderer.Dispose();
        Textures.Dispose();
    }
}
