using System.Runtime.InteropServices;
using global::OpenTK.Graphics.OpenGL4;
using global::OpenTK.Mathematics;

namespace CSharp.UI.OpenTK;

/// <summary>一帧的绘制统计，用于验证批处理边界与资源复用行为。</summary>
/// <param name="Batches">提交的批次数（每次批处理对应一次绘制调用）。</param>
/// <param name="DrawCalls">实际执行的 <c>glDrawElements</c> 次数（等于批次数）。</param>
/// <param name="Triangles">提交的三角形数量。</param>
/// <param name="RectCommands">实际绘制的矩形命令数量。</param>
/// <param name="TextureCommands">实际绘制的纹理命令数量。</param>
/// <param name="SkippedCommands">被跳过（不可见、空裁剪、无效纹理）的命令数量。</param>
/// <param name="ClipPushes">本帧压入裁剪区的次数。</param>
/// <param name="UnbalancedClipPops">多余的弹出次数（命令流不平整，仅诊断用）。</param>
public readonly record struct GlFrameStatistics(
    int Batches,
    int DrawCalls,
    int Triangles,
    int RectCommands,
    int TextureCommands,
    int SkippedCommands,
    int ClipPushes,
    int UnbalancedClipPops)
{
    /// <summary>空统计。</summary>
    public static readonly GlFrameStatistics Empty = new(0, 0, 0, 0, 0, 0, 0, 0);
}

/// <summary>
/// OpenGL 命令渲染器：按绘制顺序消费 <see cref="RenderFrame"/>，输出圆角矩形与纹理。
/// </summary>
/// <remarks>
/// <para>
/// 管线：GLSL 330 + VAO/VBO/索引缓冲；只合并<b>相邻且管线状态兼容</b>（同一程序、同一纹理、同一裁剪、同一
/// 预乘约定）的命令，因此不会跨越层叠顺序重排绘制。
/// </para>
/// <para>
/// 混合固定为 <c>glBlendFunc(GL_ONE, GL_ONE_MINUS_SRC_ALPHA)</c>，所有颜色在 CPU 侧或着色器中预乘。
/// </para>
/// <para>
/// 裁剪：<see cref="PushClipCommand"/> / <see cref="PopClipCommand"/> 维护矩形裁剪栈并换算为
/// <c>glScissor</c>（含 DPI 缩放与 Y 轴翻转），空裁剪直接把 scissor 置零并跳过其中命令。
/// </para>
/// <para>生命周期：所有成员都必须在拥有当前 GL 上下文的线程上调用；<see cref="Dispose"/> 会删除全部 GL 对象。</para>
/// </remarks>
public sealed class GlRenderer : IRenderCommandTarget, IDisposable
{
    /// <summary>矩形顶点：位置、局部坐标、半尺寸、圆角、预乘填充色、预乘描边色、描边宽度。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct RectVertex
    {
        public Vector2 Position;
        public Vector2 Local;
        public Vector2 HalfSize;
        public float Radius;
        public Vector4 Fill;
        public Vector4 Stroke;
        public float StrokeWidth;
    }

    /// <summary>纹理顶点：位置、纹理坐标、着色（rgb 非预乘 + alpha）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct TextureVertex
    {
        public Vector2 Position;
        public Vector2 Uv;
        public Vector4 Tint;
    }

    private enum BatchKind
    {
        None,
        Rectangle,
        Texture,
    }

    private const int RectVertexStride = 64;
    private const int TextureVertexStride = 32;

    private readonly IGlTextureProvider _textures;
    private readonly GlShaderProgram _rectangleProgram;
    private readonly GlShaderProgram _textureProgram;
    private readonly int _rectangleVao;
    private readonly int _textureVao;
    private readonly int _vertexBuffer;
    private readonly int _indexBuffer;
    private readonly int _rectangleViewportLocation;
    private readonly int _textureViewportLocation;
    private readonly int _textureSamplerLocation;
    private readonly int _texturePremultipliedLocation;
    private readonly Stack<Rect> _clipStack = new();

    private RectVertex[] _rectangleVertices = new RectVertex[1024];
    private TextureVertex[] _textureVertices = new TextureVertex[512];
    private uint[] _indices = new uint[6144];
    private int _rectangleVertexCount;
    private int _textureVertexCount;
    private int _indexCount;
    private BatchKind _batchKind;
    private uint _batchTexture;
    private bool _batchPremultiplied;
    private Rect _currentClip;
    private Rect _currentClipFramebuffer;
    private bool _clipEmpty;
    private Size _framebufferSize = new(1, 1);
    private double _pixelScale = 1.0;
    private int _unbalancedClipPops;
    private int _batchCount;
    private int _drawCallCount;
    private bool _disposed;

    /// <summary>创建渲染器并编译着色器、分配缓冲对象。</summary>
    /// <param name="textures">纹理解析器，通常是 <see cref="GlTextureService"/>。</param>
    public GlRenderer(IGlTextureProvider textures)
    {
        ArgumentNullException.ThrowIfNull(textures);
        _textures = textures;

        _rectangleProgram = GlShaderProgram.Compile(GlShaderSources.RectangleVertex, GlShaderSources.RectangleFragment, "rectangle");
        try
        {
            _textureProgram = GlShaderProgram.Compile(GlShaderSources.TextureVertex, GlShaderSources.TextureFragment, "texture");
        }
        catch
        {
            _rectangleProgram.Dispose();
            throw;
        }

        _rectangleViewportLocation = _rectangleProgram.GetUniformLocation("uViewport");
        _textureViewportLocation = _textureProgram.GetUniformLocation("uViewport");
        _textureSamplerLocation = _textureProgram.GetUniformLocation("uTexture");
        _texturePremultipliedLocation = _textureProgram.GetUniformLocation("uPremultiplied");

        GL.GenVertexArrays(1, out _rectangleVao);
        GlResourceCounters.ReportCreated(GlResourceKind.VertexArray);
        LiveVertexArrayCount++;
        GL.GenVertexArrays(1, out _textureVao);
        GlResourceCounters.ReportCreated(GlResourceKind.VertexArray);
        LiveVertexArrayCount++;

        GL.GenBuffers(1, out _vertexBuffer);
        GlResourceCounters.ReportCreated(GlResourceKind.Buffer);
        LiveBufferCount++;
        GL.GenBuffers(1, out _indexBuffer);
        GlResourceCounters.ReportCreated(GlResourceKind.Buffer);
        LiveBufferCount++;

        ConfigureRectangleVertexArray();
        ConfigureTextureVertexArray();

        GL.UseProgram(_textureProgram.Handle);
        GL.Uniform1(_textureSamplerLocation, 0);
        GL.UseProgram(0);
    }

    /// <summary>最近一帧使用的 framebuffer 像素尺寸。</summary>
    public Size FramebufferSize => _framebufferSize;

    /// <summary>最近一帧的绘制统计。</summary>
    public GlFrameStatistics LastStatistics { get; private set; } = GlFrameStatistics.Empty;

    /// <summary>当前存活的顶点数组对象数量。</summary>
    public int LiveVertexArrayCount { get; private set; }

    /// <summary>当前存活的缓冲对象数量。</summary>
    public int LiveBufferCount { get; private set; }

    /// <summary>当前存活的着色器程序数量。</summary>
    public int LiveProgramCount => (_rectangleProgram.IsDisposed ? 0 : 1) + (_textureProgram.IsDisposed ? 0 : 1);

    /// <summary>是否已释放。</summary>
    public bool IsDisposed => _disposed;

    /// <inheritdoc />
    public void Submit(RenderFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ThrowIfDisposed();

        _pixelScale = frame.PixelScale is > 0 and < double.PositiveInfinity ? frame.PixelScale : 1.0;
        _framebufferSize = GlCoordinateMath.ToFramebufferSize(frame.ViewportSize, _pixelScale);
        var framebufferWidth = (int)_framebufferSize.Width;
        var framebufferHeight = (int)_framebufferSize.Height;

        GL.Viewport(0, 0, framebufferWidth, framebufferHeight);
        GL.Disable(EnableCap.DepthTest);
        GL.Disable(EnableCap.CullFace);
        GL.Disable(EnableCap.Multisample);
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);

        // 清屏色同样按预乘写入，保证 framebuffer 内容与混合约定一致。
        var clear = ToPremultiplied(frame.ClearColor, 1);
        GL.ClearColor(clear.X, clear.Y, clear.Z, clear.W);

        var fullClip = new Rect(0, 0, Math.Max(0, frame.ViewportSize.Width), Math.Max(0, frame.ViewportSize.Height));
        _clipStack.Clear();
        _currentClip = fullClip;
        _currentClipFramebuffer = GlCoordinateMath.ToFramebufferRect(fullClip, _pixelScale);
        _clipEmpty = fullClip.IsEmpty;
        _unbalancedClipPops = 0;
        GL.Enable(EnableCap.ScissorTest);
        ApplyScissor(_currentClip);
        GL.Clear(ClearBufferMask.ColorBufferBit);

        ResetBatch();
        _batchCount = 0;
        _drawCallCount = 0;
        var statistics = default(GlFrameStatistics);
        var commands = frame.Commands ?? [];
        foreach (var command in commands)
        {
            switch (command)
            {
                case RectangleCommand rectangle:
                    statistics = DrawRectangle(rectangle, statistics);
                    break;
                case TextureCommand texture:
                    statistics = DrawTexture(texture, statistics);
                    break;
                case PushClipCommand push:
                    PushClip(push);
                    statistics = statistics with { ClipPushes = statistics.ClipPushes + 1 };
                    break;
                case PopClipCommand:
                    PopClip();
                    break;
                case null:
                    statistics = statistics with { SkippedCommands = statistics.SkippedCommands + 1 };
                    break;
                default:
                    // 未知命令：跳过而不破坏顺序。
                    statistics = statistics with { SkippedCommands = statistics.SkippedCommands + 1 };
                    break;
            }
        }

        Flush();
        statistics = statistics with
        {
            Batches = _batchCount,
            DrawCalls = _drawCallCount,
            UnbalancedClipPops = _unbalancedClipPops,
        };
        LastStatistics = statistics;

        GL.Disable(EnableCap.ScissorTest);
        GL.UseProgram(0);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_vertexBuffer != 0)
        {
            GL.DeleteBuffer(_vertexBuffer);
            GlResourceCounters.ReportDeleted(GlResourceKind.Buffer);
            LiveBufferCount--;
        }

        if (_indexBuffer != 0)
        {
            GL.DeleteBuffer(_indexBuffer);
            GlResourceCounters.ReportDeleted(GlResourceKind.Buffer);
            LiveBufferCount--;
        }

        if (_rectangleVao != 0)
        {
            GL.DeleteVertexArray(_rectangleVao);
            GlResourceCounters.ReportDeleted(GlResourceKind.VertexArray);
            LiveVertexArrayCount--;
        }

        if (_textureVao != 0)
        {
            GL.DeleteVertexArray(_textureVao);
            GlResourceCounters.ReportDeleted(GlResourceKind.VertexArray);
            LiveVertexArrayCount--;
        }

        _textureProgram.Dispose();
        _rectangleProgram.Dispose();
        _rectangleVertices = [];
        _textureVertices = [];
        _indices = [];
        _rectangleVertexCount = 0;
        _textureVertexCount = 0;
        _indexCount = 0;
    }

    private void ConfigureRectangleVertexArray()
    {
        GL.BindVertexArray(_rectangleVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBuffer);
        GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, RectVertexStride, 0);
        GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, RectVertexStride, 8);
        GL.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, RectVertexStride, 16);
        GL.VertexAttribPointer(3, 1, VertexAttribPointerType.Float, false, RectVertexStride, 24);
        GL.VertexAttribPointer(4, 4, VertexAttribPointerType.Float, false, RectVertexStride, 28);
        GL.VertexAttribPointer(5, 4, VertexAttribPointerType.Float, false, RectVertexStride, 44);
        GL.VertexAttribPointer(6, 1, VertexAttribPointerType.Float, false, RectVertexStride, 60);
        for (var i = 0; i <= 6; i++)
        {
            GL.EnableVertexAttribArray(i);
        }

        GL.BindBuffer(BufferTarget.ElementArrayBuffer, _indexBuffer);
        GL.BindVertexArray(0);
    }

    private void ConfigureTextureVertexArray()
    {
        GL.BindVertexArray(_textureVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBuffer);
        GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, TextureVertexStride, 0);
        GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, TextureVertexStride, 8);
        GL.VertexAttribPointer(2, 4, VertexAttribPointerType.Float, false, TextureVertexStride, 16);
        for (var i = 0; i <= 2; i++)
        {
            GL.EnableVertexAttribArray(i);
        }

        GL.BindBuffer(BufferTarget.ElementArrayBuffer, _indexBuffer);
        GL.BindVertexArray(0);
    }

    private GlFrameStatistics DrawRectangle(RectangleCommand command, GlFrameStatistics statistics)
    {
        if (command.Bounds.IsEmpty)
        {
            return statistics with { SkippedCommands = statistics.SkippedCommands + 1 };
        }

        var fill = ToPremultiplied(command.Fill, command.Opacity);
        var strokeColor = command.Stroke;
        var hasStroke = strokeColor is { } candidate && command.StrokeWidth > 0 && candidate.A > 0;
        var stroke = hasStroke ? ToPremultiplied(strokeColor!.Value, command.Opacity) : default(Vector4);

        // 完全透明（无填充也无描边）或落在裁剪区之外时不产生绘制。
        if ((fill.W <= 0 && !hasStroke) || _clipEmpty)
        {
            return statistics with { SkippedCommands = statistics.SkippedCommands + 1 };
        }

        var bounds = GlCoordinateMath.ToFramebufferRect(command.Bounds, _pixelScale);
        if (!bounds.IntersectsWith(_currentClipFramebuffer))
        {
            return statistics with { SkippedCommands = statistics.SkippedCommands + 1 };
        }

        EnsureBatch(BatchKind.Rectangle, 0, false);
        var left = (float)bounds.Left;
        var top = (float)bounds.Top;
        var right = (float)bounds.Right;
        var bottom = (float)bounds.Bottom;
        var center = new Vector2((left + right) * 0.5f, (top + bottom) * 0.5f);
        var halfSize = new Vector2((right - left) * 0.5f, (bottom - top) * 0.5f);
        var radius = (float)(command.CornerRadius * _pixelScale);
        var strokeWidth = hasStroke ? (float)(command.StrokeWidth * _pixelScale) : 0;

        EnsureRectangleCapacity(4);
        var baseVertex = _rectangleVertexCount;
        AddRectangleVertex(new Vector2(left, top), new Vector2(-halfSize.X, -halfSize.Y), halfSize, radius, fill, stroke, strokeWidth);
        AddRectangleVertex(new Vector2(right, top), new Vector2(halfSize.X, -halfSize.Y), halfSize, radius, fill, stroke, strokeWidth);
        AddRectangleVertex(new Vector2(right, bottom), new Vector2(halfSize.X, halfSize.Y), halfSize, radius, fill, stroke, strokeWidth);
        AddRectangleVertex(new Vector2(left, bottom), new Vector2(-halfSize.X, halfSize.Y), halfSize, radius, fill, stroke, strokeWidth);
        AddQuadIndices(baseVertex);

        return statistics with
        {
            RectCommands = statistics.RectCommands + 1,
            Triangles = statistics.Triangles + 2,
        };
    }

    private GlFrameStatistics DrawTexture(TextureCommand command, GlFrameStatistics statistics)
    {
        if (command.Bounds.IsEmpty || !command.Texture.IsValid || _clipEmpty)
        {
            return statistics with { SkippedCommands = statistics.SkippedCommands + 1 };
        }

        if (!_textures.TryGetTextureName(command.Texture, out var textureName))
        {
            return statistics with { SkippedCommands = statistics.SkippedCommands + 1 };
        }

        var bounds = GlCoordinateMath.ToFramebufferRect(command.Bounds, _pixelScale);
        if (!bounds.IntersectsWith(_currentClipFramebuffer))
        {
            return statistics with { SkippedCommands = statistics.SkippedCommands + 1 };
        }

        var textureWidth = Math.Max(1, command.Texture.PixelWidth);
        var textureHeight = Math.Max(1, command.Texture.PixelHeight);
        var source = command.Source.IsEmpty
            ? new Rect(0, 0, textureWidth, textureHeight)
            : command.Source;
        var u0 = (float)(source.Left / textureWidth);
        var v0 = (float)(source.Top / textureHeight);
        var u1 = (float)(source.Right / textureWidth);
        var v1 = (float)(source.Bottom / textureHeight);
        var tint = ToTint(command.Tint, command.Opacity);
        var premultiplied = !command.Texture.IsAlphaMask;

        EnsureBatch(BatchKind.Texture, textureName, premultiplied);
        var left = (float)bounds.Left;
        var top = (float)bounds.Top;
        var right = (float)bounds.Right;
        var bottom = (float)bounds.Bottom;

        EnsureTextureCapacity(4);
        var baseVertex = _textureVertexCount;
        AddTextureVertex(new Vector2(left, top), new Vector2(u0, v0), tint);
        AddTextureVertex(new Vector2(right, top), new Vector2(u1, v0), tint);
        AddTextureVertex(new Vector2(right, bottom), new Vector2(u1, v1), tint);
        AddTextureVertex(new Vector2(left, bottom), new Vector2(u0, v1), tint);
        AddQuadIndices(baseVertex);

        return statistics with
        {
            TextureCommands = statistics.TextureCommands + 1,
            Triangles = statistics.Triangles + 2,
        };
    }

    private void PushClip(PushClipCommand command)
    {
        // 裁剪状态变化必须结束当前批处理，避免把不同裁剪状态的命令合并成一次绘制。
        Flush();
        _clipStack.Push(_currentClip);
        _currentClip = GlCoordinateMath.IntersectClip(_currentClip, command.Bounds);
        _currentClipFramebuffer = GlCoordinateMath.ToFramebufferRect(_currentClip, _pixelScale);
        _clipEmpty = _currentClip.IsEmpty;
        ApplyScissor(_currentClip);
    }

    private void PopClip()
    {
        Flush();
        if (_clipStack.Count > 0)
        {
            _currentClip = _clipStack.Pop();
        }
        else
        {
            // 命令流不平衡：恢复为整帧视口，并记录诊断计数。
            _unbalancedClipPops++;
            _currentClip = new Rect(0, 0, Math.Max(0, _framebufferSize.Width / _pixelScale), Math.Max(0, _framebufferSize.Height / _pixelScale));
        }

        _currentClipFramebuffer = GlCoordinateMath.ToFramebufferRect(_currentClip, _pixelScale);
        _clipEmpty = _currentClip.IsEmpty;
        ApplyScissor(_currentClip);
    }

    private void ApplyScissor(Rect logicalClip)
    {
        var (x, y, width, height) = GlCoordinateMath.ToScissorRect(logicalClip, _framebufferSize, _pixelScale);
        GL.Scissor(x, y, width, height);
    }

    private void EnsureBatch(BatchKind kind, uint texture, bool premultiplied)
    {
        if (_batchKind == kind && (kind != BatchKind.Texture || (_batchTexture == texture && _batchPremultiplied == premultiplied)))
        {
            return;
        }

        Flush();
        _batchKind = kind;
        _batchTexture = texture;
        _batchPremultiplied = premultiplied;
    }

    private void Flush()
    {
        if (_indexCount == 0 || _batchKind == BatchKind.None)
        {
            ResetBatch();
            return;
        }

        GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBuffer);
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, _indexBuffer);
        GL.BufferData(BufferTarget.ElementArrayBuffer, _indexCount * sizeof(uint), _indices, BufferUsageHint.StreamDraw);
        if (_batchKind == BatchKind.Rectangle)
        {
            GL.BindVertexArray(_rectangleVao);
            GL.BufferData(BufferTarget.ArrayBuffer, _rectangleVertexCount * RectVertexStride, _rectangleVertices, BufferUsageHint.StreamDraw);
            _rectangleProgram.Use();
            GL.Uniform2(_rectangleViewportLocation, (float)_framebufferSize.Width, (float)_framebufferSize.Height);
        }
        else
        {
            GL.BindVertexArray(_textureVao);
            GL.BufferData(BufferTarget.ArrayBuffer, _textureVertexCount * TextureVertexStride, _textureVertices, BufferUsageHint.StreamDraw);
            _textureProgram.Use();
            GL.Uniform2(_textureViewportLocation, (float)_framebufferSize.Width, (float)_framebufferSize.Height);
            GL.Uniform1(_texturePremultipliedLocation, _batchPremultiplied ? 1f : 0f);
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, _batchTexture);
        }

        GL.DrawElements(PrimitiveType.Triangles, _indexCount, DrawElementsType.UnsignedInt, 0);
        _batchCount++;
        _drawCallCount++;
        ResetBatch();
    }

    private void ResetBatch()
    {
        _batchKind = BatchKind.None;
        _batchTexture = 0;
        _batchPremultiplied = false;
        _rectangleVertexCount = 0;
        _textureVertexCount = 0;
        _indexCount = 0;
    }

    private void AddRectangleVertex(Vector2 position, Vector2 local, Vector2 halfSize, float radius, Vector4 fill, Vector4 stroke, float strokeWidth)
    {
        _rectangleVertices[_rectangleVertexCount++] = new RectVertex
        {
            Position = position,
            Local = local,
            HalfSize = halfSize,
            Radius = radius,
            Fill = fill,
            Stroke = stroke,
            StrokeWidth = strokeWidth,
        };
    }

    private void AddTextureVertex(Vector2 position, Vector2 uv, Vector4 tint)
    {
        _textureVertices[_textureVertexCount++] = new TextureVertex
        {
            Position = position,
            Uv = uv,
            Tint = tint,
        };
    }

    /// <summary>追加一个四边形的两个三角形索引（相对当前批次起点）。</summary>
    private void AddQuadIndices(int baseVertex)
    {
        EnsureIndexCapacity(6);
        _indices[_indexCount++] = (uint)baseVertex;
        _indices[_indexCount++] = (uint)(baseVertex + 1);
        _indices[_indexCount++] = (uint)(baseVertex + 2);
        _indices[_indexCount++] = (uint)baseVertex;
        _indices[_indexCount++] = (uint)(baseVertex + 2);
        _indices[_indexCount++] = (uint)(baseVertex + 3);
    }

    private void EnsureRectangleCapacity(int additional)
    {
        var required = _rectangleVertexCount + additional;
        if (required <= _rectangleVertices.Length)
        {
            return;
        }

        Array.Resize(ref _rectangleVertices, Math.Max(required, _rectangleVertices.Length * 2));
    }

    private void EnsureTextureCapacity(int additional)
    {
        var required = _textureVertexCount + additional;
        if (required <= _textureVertices.Length)
        {
            return;
        }

        Array.Resize(ref _textureVertices, Math.Max(required, _textureVertices.Length * 2));
    }

    private void EnsureIndexCapacity(int additional)
    {
        var required = _indexCount + additional;
        if (required <= _indices.Length)
        {
            return;
        }

        Array.Resize(ref _indices, Math.Max(required, _indices.Length * 2));
    }

    /// <summary>把非预乘颜色与透明度倍数合成为预乘 alpha 的顶点颜色。</summary>
    internal static Vector4 ToPremultiplied(Color color, double opacity)
    {
        var alpha = Math.Clamp(color.A / 255.0 * Math.Clamp(opacity, 0, 1), 0, 1);
        return new Vector4(
            (float)(color.R / 255.0 * alpha),
            (float)(color.G / 255.0 * alpha),
            (float)(color.B / 255.0 * alpha),
            (float)alpha);
    }

    /// <summary>把非预乘着色颜色与透明度倍数合成为“rgb 非预乘 + alpha”的纹理着色值。</summary>
    internal static Vector4 ToTint(Color color, double opacity)
    {
        var alpha = Math.Clamp(color.A / 255.0 * Math.Clamp(opacity, 0, 1), 0, 1);
        return new Vector4(color.R / 255f, color.G / 255f, color.B / 255f, (float)alpha);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
