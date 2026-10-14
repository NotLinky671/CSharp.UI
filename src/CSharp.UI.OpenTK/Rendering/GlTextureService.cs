using global::OpenTK.Graphics.OpenGL4;
using GlPixelFormat = global::OpenTK.Graphics.OpenGL4.PixelFormat;

namespace CSharp.UI.OpenTK;

/// <summary>
/// 把平台无关的 <see cref="TextureHandle"/> 解析为 OpenGL 纹理名。
/// </summary>
/// <remarks>渲染器只通过本接口取回 GL 纹理，纹理名不会进入框架公开 API。</remarks>
public interface IGlTextureProvider
{
    /// <summary>取回句柄对应的 GL 纹理名；句柄无效、已释放或属于其他服务时返回 false。</summary>
    bool TryGetTextureName(TextureHandle handle, out uint name);
}

/// <summary>
/// OpenGL 纹理服务：按稳定键缓存纹理、有界 LRU 淘汰、上传时统一像素格式与行序约定。
/// </summary>
/// <remarks>
/// <para>
/// 键使用默认的相等比较（值相等），因此文本纹理键等 record 值类型可以命中缓存；重复请求同一键不会重复上传。
/// </para>
/// <para>
/// 像素约定：<see cref="PixelFormat.Rgba8888Premultiplied"/> 与 <see cref="PixelFormat.Bgra8888Premultiplied"/>
/// 直接上传为 RGBA8（预乘 alpha）；<see cref="PixelFormat.Alpha8"/> 上传为 R8 覆盖率遮罩，
/// 由渲染器用 <c>tint * coverage</c> 着色。上传时把 <c>GL_UNPACK_ALIGNMENT</c> 设为 1 并按需设置
/// <c>GL_UNPACK_ROW_LENGTH</c>，行序保持 CPU 位图的左上原点（GL 纹理坐标 (0,0) 即位图左上角）。
/// </para>
/// <para>生命周期：所有方法都必须在拥有当前 GL 上下文的线程上调用；释放前必须保证上下文仍然有效。</para>
/// </remarks>
public sealed class GlTextureService : ITextureService, IGlTextureProvider, IDisposable
{
    private sealed class Entry
    {
        internal required object Key { get; init; }

        internal required TextureHandle Handle { get; init; }

        internal required uint Name { get; init; }

        internal required long ByteCount { get; init; }
    }

    private readonly Dictionary<object, LinkedListNode<Entry>> _map = new(EqualityComparer<object>.Default);
    private readonly Dictionary<long, uint> _namesById = [];
    private readonly LinkedList<Entry> _order = [];

    // 帧保护分两代：本帧引用与上一帧引用。两代都受保护，避免“本帧尚未用到的纹理被本帧新建的纹理挤掉，
    // 随后又要重新上传”的抖动；上一帧未被再次引用的条目会在下一次帧边界回收。
    private HashSet<Entry> _frameReferenced = [];
    private HashSet<Entry> _previousFrameReferenced = [];
    private long _nextId;
    private int _capacity;
    private int _created;
    private int _released;
    private long _liveBytes;
    private bool _disposed;

    /// <summary>创建纹理服务。</summary>
    /// <param name="capacity">缓存容量上限；超出后按最近最少使用淘汰。</param>
    public GlTextureService(int capacity = 128)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
    }

    /// <summary>当前存活纹理数量（用于验证资源不持续增长）。</summary>
    public int LiveTextureCount => _map.Count;

    /// <summary>累计创建（上传）的纹理数量。</summary>
    public int CreatedTextureCount => _created;

    /// <summary>累计释放的纹理数量（含 LRU 淘汰与 <see cref="Clear"/>）。</summary>
    public int ReleasedTextureCount => _released;

    /// <summary>当前存活纹理占用的 CPU 位图字节数（诊断用）。</summary>
    public long LiveTextureBytes => _liveBytes;

    /// <summary>当前是否有存活纹理。</summary>
    public bool IsDisposed => _disposed;

    /// <inheritdoc />
    public int Capacity
    {
        get => _capacity;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _capacity = value;

            // 显式调整容量属于管理操作：立即生效（不受帧保护影响）。
            _frameReferenced.Clear();
            _previousFrameReferenced.Clear();
            Trim();
        }
    }

    /// <inheritdoc />
    public TextureHandle GetOrCreate(object key, Func<PixelBuffer> factory)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(factory);
        ThrowIfDisposed();

        if (_map.TryGetValue(key, out var existing))
        {
            _order.Remove(existing);
            _order.AddLast(existing);
            _frameReferenced.Add(existing.Value);
            return existing.Value.Handle;
        }

        var buffer = factory() ?? throw new InvalidOperationException($"纹理工厂为键 {key} 返回了 null 位图。");
        if (buffer.Width <= 0 || buffer.Height <= 0)
        {
            throw new ArgumentException($"纹理尺寸必须为正，实际为 {buffer.Width}×{buffer.Height}（键 {key}）。", nameof(factory));
        }

        var name = Upload(buffer);
        var id = ++_nextId;
        var handle = new TextureHandle(id, buffer.Width, buffer.Height, buffer.Format == PixelFormat.Alpha8);
        var entry = new Entry
        {
            Key = key,
            Handle = handle,
            Name = name,
            ByteCount = (long)buffer.Stride * buffer.Height,
        };

        _map[key] = _order.AddLast(entry);
        _namesById[id] = name;
        _created++;
        _liveBytes += entry.ByteCount;
        _frameReferenced.Add(entry);
        Trim();
        return handle;
    }

    /// <inheritdoc />
    public void Release(object key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (_disposed)
        {
            return;
        }

        if (_map.Remove(key, out var node))
        {
            _frameReferenced.Remove(node.Value);
            _previousFrameReferenced.Remove(node.Value);
            RemoveEntry(node);
        }
    }

    /// <inheritdoc />
    public void Clear()
    {
        if (_disposed)
        {
            return;
        }

        foreach (var entry in _order)
        {
            DeleteTexture(entry.Name);
            _namesById.Remove(entry.Handle.Id);
            _released++;
            _liveBytes -= entry.ByteCount;
        }

        _map.Clear();
        _order.Clear();
        _frameReferenced.Clear();
        _previousFrameReferenced.Clear();
    }

    /// <inheritdoc />
    /// <remarks>清除上一帧的引用保护并尝试回落容量；随后本帧引用到的纹理会一直存活到下一次调用。</remarks>
    public void BeginFrame()
    {
        if (_disposed)
        {
            return;
        }

        _previousFrameReferenced = _frameReferenced;
        _frameReferenced = [];
        Trim();
    }

    /// <inheritdoc />
    public bool TryGetTextureName(TextureHandle handle, out uint name)
    {
        name = 0;
        if (_disposed || !handle.IsValid)
        {
            return false;
        }

        return _namesById.TryGetValue(handle.Id, out name);
    }

    /// <summary>释放全部纹理；必须在 GL 上下文仍然有效的线程上调用。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Clear();
        _disposed = true;
    }

    private void Trim()
    {
        // 只淘汰未被保护的条目：容量是长期上限，帧内引用可临时超出，在后续帧边界回收。
        var node = _order.First;
        while (_map.Count > _capacity && node is not null)
        {
            var next = node.Next;
            if (!IsProtected(node.Value))
            {
                _map.Remove(node.Value.Key);
                RemoveEntry(node);
            }

            node = next;
        }
    }

    private bool IsProtected(Entry entry)
        => _frameReferenced.Contains(entry) || _previousFrameReferenced.Contains(entry);

    private void RemoveEntry(LinkedListNode<Entry> node)
    {
        _order.Remove(node);
        DeleteTexture(node.Value.Name);
        _namesById.Remove(node.Value.Handle.Id);
        _released++;
        _liveBytes -= node.Value.ByteCount;
    }

    private static void DeleteTexture(uint name)
    {
        GL.DeleteTextures(1, [name]);
        GlResourceCounters.ReportDeleted(GlResourceKind.Texture);
    }

    private static uint Upload(PixelBuffer buffer)
    {
        var rowLength = buffer.Stride / buffer.BytesPerPixel;
        var needsRowLength = rowLength != buffer.Width;
        GL.GenTextures(1, out uint name);
        GlResourceCounters.ReportCreated(GlResourceKind.Texture);
        GL.BindTexture(TextureTarget.Texture2D, name);

        // Alpha8 位图的行可能不是 4 字节对齐；统一按 1 字节对齐上传，避免行错位。
        GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        if (needsRowLength)
        {
            GL.PixelStore(PixelStoreParameter.UnpackRowLength, rowLength);
        }

        try
        {
            var (internalFormat, format) = buffer.Format switch
            {
                PixelFormat.Alpha8 => (PixelInternalFormat.R8, GlPixelFormat.Red),
                PixelFormat.Bgra8888Premultiplied => (PixelInternalFormat.Rgba8, GlPixelFormat.Bgra),
                _ => (PixelInternalFormat.Rgba8, GlPixelFormat.Rgba),
            };

            unsafe
            {
                fixed (byte* pixels = buffer.Pixels)
                {
                    GL.TexImage2D(
                        TextureTarget.Texture2D,
                        0,
                        internalFormat,
                        buffer.Width,
                        buffer.Height,
                        0,
                        format,
                        PixelType.UnsignedByte,
                        (IntPtr)pixels);
                }
            }

            // 线性过滤 + 边缘夹取：文本遮罩与图片缩放都不应出现接缝或重复边缘。
            // 必须在纹理仍然绑定、且已写入 level 0 之后设置：默认的 min filter 是 mipmap 过滤，
            // 只定义 level 0 时纹理会不完整（采样结果为 (0,0,0,1)），表现为“纹理全黑”。
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        }
        catch
        {
            GL.DeleteTextures(1, [name]);
            GlResourceCounters.ReportDeleted(GlResourceKind.Texture);
            throw;
        }
        finally
        {
            if (needsRowLength)
            {
                GL.PixelStore(PixelStoreParameter.UnpackRowLength, 0);
            }

            GL.BindTexture(TextureTarget.Texture2D, 0);
        }

        return name;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
