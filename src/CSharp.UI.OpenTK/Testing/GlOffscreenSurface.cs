using global::OpenTK.Graphics.OpenGL4;
using global::OpenTK.Windowing.Desktop;
using GlPixelFormat = global::OpenTK.Graphics.OpenGL4.PixelFormat;

namespace CSharp.UI.OpenTK;
/// <summary>
/// 离屏 framebuffer 测试入口：创建隐藏窗口 + FBO，渲染给定 <see cref="RenderFrame"/> 后读回像素。
/// </summary>
/// <remarks>
/// <para>
/// 用途：图形集成测试在不打开可见窗口的情况下断言颜色、层叠顺序、透明混合、圆角与裁剪结果。
/// 主渲染路径不使用 Skia GPU surface，本类型也只使用 OpenGL。
/// </para>
/// <para>
/// 像素读回约定：本类型把读回的行序翻转为<b>左上原点</b>，与 CPU 位图（<see cref="PixelBuffer"/>）一致；
/// 颜色为预乘 alpha 的 RGBA8，<see cref="GetPixel"/> 返回非预乘颜色。
/// </para>
/// <para>生命周期：创建会建立并保持一个 GL 上下文；所有成员内部都会先让上下文成为当前上下文。</para>
/// </remarks>
public sealed class GlOffscreenSurface : IDisposable
{
    private readonly NativeWindow _window;
    private int _framebuffer;
    private int _colorTexture;
    private int _width;
    private int _height;
    private bool _disposed;

    /// <summary>
    /// 允许在非主线程创建窗口与 GL 上下文；只应在测试运行器（在 worker 线程执行测试）中调用。
    /// </summary>
    /// <remarks>
    /// Windows/Linux 的 GLFW 支持在非主线程创建窗口与上下文；macOS（Cocoa）仍然要求主线程，
    /// 因此该选项在 macOS 上不会让测试通过，只会在创建窗口时给出明确错误。
    /// 生产宿主（<c>UiApplication.Run</c>）不应调用本方法：它必须在主线程运行。
    /// </remarks>
    public static void AllowNonMainThreadCreation() => GLFWProvider.CheckForMainThread = false;

    /// <summary>创建一个隐藏窗口与指定尺寸的离屏 framebuffer。</summary>
    /// <param name="width">初始 framebuffer 宽度（物理像素）。</param>
    /// <param name="height">初始 framebuffer 高度（物理像素）。</param>
    /// <param name="title">隐藏窗口标题（仅用于诊断）。</param>
    public GlOffscreenSurface(int width = 256, int height = 256, string title = "CSharp.UI 离屏测试")
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        const string Purpose = "离屏 framebuffer 测试";
        _window = GlWindowFactory.CreateWindow(
            title,
            // 隐藏窗口大小只用于获得上下文，实际渲染目标是 FBO；给一个稳妥的默认尺寸避免平台最小尺寸限制。
            Math.Max(64, width),
            Math.Max(64, height),
            resizable: false,
            vsync: false,
            visible: false,
            purpose: Purpose);
        ContextDescription = GlWindowFactory.DescribeContext();
        _window.MakeCurrent();
        EnsureSize(width, height);
    }

    /// <summary>当前 GL 上下文描述（版本 / 渲染器 / 厂商）。</summary>
    public string ContextDescription { get; }

    /// <summary>framebuffer 对象名。</summary>
    public uint FramebufferName => (uint)_framebuffer;

    /// <summary>颜色附件纹理名。</summary>
    public uint ColorTextureName => (uint)_colorTexture;

    /// <summary>当前 framebuffer 尺寸（物理像素）。</summary>
    public Size Size => new(_width, _height);

    /// <summary>使本表面的 GL 上下文成为当前上下文。</summary>
    public void MakeCurrent()
    {
        ThrowIfDisposed();
        _window.MakeCurrent();
    }

    /// <summary>按需重建 framebuffer（尺寸变化时重新分配颜色附件）。</summary>
    public void EnsureSize(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ThrowIfDisposed();
        if (_framebuffer != 0 && width == _width && height == _height)
        {
            return;
        }

        MakeCurrent();
        if (_framebuffer == 0)
        {
            GL.GenFramebuffers(1, out _framebuffer);
            GlResourceCounters.ReportCreated(GlResourceKind.Framebuffer);
        }

        if (_colorTexture != 0)
        {
            GL.DeleteTextures(1, [(uint)_colorTexture]);
            GlResourceCounters.ReportDeleted(GlResourceKind.Texture);
        }

        GL.GenTextures(1, out uint colorTexture);
        GlResourceCounters.ReportCreated(GlResourceKind.Texture);
        _colorTexture = (int)colorTexture;
        GL.BindTexture(TextureTarget.Texture2D, colorTexture);
        GL.TexImage2D(
            TextureTarget.Texture2D,
            0,
            PixelInternalFormat.Rgba8,
            width,
            height,
            0,
            GlPixelFormat.Rgba,
            PixelType.UnsignedByte,
            IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.BindTexture(TextureTarget.Texture2D, 0);

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _framebuffer);
        GL.FramebufferTexture2D(
            FramebufferTarget.Framebuffer,
            FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D,
            colorTexture,
            0);
        GL.DrawBuffer(DrawBufferMode.ColorAttachment0);
        GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
        var status = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        if (status != FramebufferErrorCode.FramebufferComplete)
        {
            var diagnostics = GlWindowFactory.DescribeEnvironment(purpose: "离屏 framebuffer 测试");
            throw new UiHostException(
                $"离屏 framebuffer 不完整：{status}（尺寸 {width}×{height}）。{Environment.NewLine}{diagnostics}",
                diagnostics);
        }

        _width = width;
        _height = height;
    }

    /// <summary>清空整个 framebuffer（不受视口与裁剪影响），便于测试之间互不干扰。</summary>
    public void Clear(Color color)
    {
        ThrowIfDisposed();
        MakeCurrent();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _framebuffer);
        GL.Disable(EnableCap.ScissorTest);
        var premultiplied = GlRenderer.ToPremultiplied(color, 1);
        GL.ClearColor(premultiplied.X, premultiplied.Y, premultiplied.Z, premultiplied.W);
        GL.Clear(ClearBufferMask.ColorBufferBit);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    /// <summary>渲染一帧并返回统计；framebuffer 会按帧的视口与 DPI 自动调整大小。</summary>
    public GlFrameStatistics Render(GlRenderer renderer, RenderFrame frame)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(frame);
        ThrowIfDisposed();

        var target = GlCoordinateMath.ToFramebufferSize(frame.ViewportSize, frame.PixelScale);
        EnsureSize((int)target.Width, (int)target.Height);
        MakeCurrent();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _framebuffer);
        try
        {
            renderer.Submit(frame);
        }
        finally
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        }

        return renderer.LastStatistics;
    }

    /// <summary>读回整张 framebuffer（左上原点、预乘 RGBA8）。</summary>
    public PixelBuffer ReadPixels()
    {
        ThrowIfDisposed();
        MakeCurrent();
        var width = _width;
        var height = _height;
        var bytes = new byte[width * height * 4];
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _framebuffer);
        GL.PixelStore(PixelStoreParameter.PackAlignment, 1);
        try
        {
            unsafe
            {
                fixed (byte* pixels = bytes)
                {
                    GL.ReadPixels(0, 0, width, height, GlPixelFormat.Rgba, PixelType.UnsignedByte, (IntPtr)pixels);
                }
            }
        }
        finally
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        }

        // OpenGL 的原点在左下角；翻转为左上原点，使 (0,0) 与逻辑坐标的左上角一致。
        var flipped = new byte[bytes.Length];
        for (var y = 0; y < height; y++)
        {
            var sourceRow = (height - 1 - y) * width * 4;
            var targetRow = y * width * 4;
            Array.Copy(bytes, sourceRow, flipped, targetRow, width * 4);
        }

        return new PixelBuffer(width, height, PixelFormat.Rgba8888Premultiplied, flipped);
    }

    /// <summary>读取左上原点坐标处的非预乘颜色。</summary>
    public Color GetPixel(int x, int y) => ReadPixels().GetColor(x, y);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _window.MakeCurrent();
        if (_colorTexture != 0)
        {
            GL.DeleteTextures(1, [(uint)_colorTexture]);
            GlResourceCounters.ReportDeleted(GlResourceKind.Texture);
            _colorTexture = 0;
        }

        if (_framebuffer != 0)
        {
            GL.DeleteFramebuffers(1, [(uint)_framebuffer]);
            GlResourceCounters.ReportDeleted(GlResourceKind.Framebuffer);
            _framebuffer = 0;
        }

        _window.Dispose();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
