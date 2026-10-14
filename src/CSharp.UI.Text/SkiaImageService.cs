using SkiaSharp;

namespace CSharp.UI.Skia;

/// <summary>
/// 基于 SkiaSharp 的图片资源服务：把本地图片文件解码为预乘 8888 位图并按来源缓存。
/// </summary>
/// <remarks>
/// <para>
/// 像素约定：统一输出 <see cref="PixelFormat.Rgba8888Premultiplied"/>（<see cref="PixelFormat.Rgba8888Premultiplied"/>
/// 是核心文档中“纹理与图片统一使用”的格式），因此不同平台的 Skia 原生顺序（Windows 为 BGRA）都会被转换成 RGBA，
/// 且始终为预乘 alpha，避免透明边缘在合成时出现色晕。
/// </para>
/// <para>
/// 缓存约定：键为来源字符串；容量由 <see cref="Capacity"/> 控制，超出后按最近最少使用淘汰，
/// 淘汰与 <see cref="Clear"/> 都会 <see cref="ImageResource.Dispose"/> 对应资源并触发
/// <see cref="ImageResource.PixelsDisposed"/>，让纹理服务失效对应纹理。
/// </para>
/// </remarks>
public sealed class SkiaImageService : IImageService, IDisposable
{
    /// <summary>默认缓存容量。</summary>
    public const int DefaultCapacity = 64;

    private readonly LruCache<string, ImageResource> _images;
    private bool _disposed;

    /// <summary>创建图片服务。</summary>
    /// <param name="capacity">缓存容量（至少 1）。</param>
    public SkiaImageService(int capacity = DefaultCapacity)
    {
        _images = new LruCache<string, ImageResource>(Math.Max(1, capacity))
        {
            OnEvicted = (_, resource) => resource.Dispose(),
        };
    }

    /// <summary>实际解码次数（缓存命中不计数）。</summary>
    public int DecodeCount { get; private set; }

    /// <inheritdoc />
    public int CachedImageCount => _images.Count;

    /// <inheritdoc />
    public int Capacity
    {
        get => _images.Capacity;
        set => _images.Capacity = Math.Max(1, value);
    }

    /// <inheritdoc />
    public ImageResource Load(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_images.TryGet(source, out var cached))
        {
            return cached;
        }

        var resource = Decode(source);
        _images.GetOrAdd(source, _ => resource, out _);
        return resource;
    }

    /// <inheritdoc />
    public bool TryLoad(string source, out ImageResource? resource)
    {
        resource = null;
        if (string.IsNullOrWhiteSpace(source))
        {
            return false;
        }

        try
        {
            resource = Load(source);
            return true;
        }
        catch (ObjectDisposedException)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public void Clear() => _images.Clear();

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Clear();
    }

    private ImageResource Decode(string source)
    {
        if (!File.Exists(source))
        {
            throw new ImageLoadException(source, "文件不存在。");
        }

        SKBitmap? decoded;
        try
        {
            decoded = SKBitmap.Decode(source);
        }
        catch (Exception exception)
        {
            throw new ImageLoadException(source, $"解码失败：{exception.Message}");
        }

        if (decoded is null)
        {
            throw new ImageLoadException(source, "不是可识别的图片格式，或文件内容已损坏。");
        }

        using (decoded)
        {
            using var converted = ToPremultipliedRgba(decoded);
            var width = converted.Width;
            var height = converted.Height;
            if (width <= 0 || height <= 0)
            {
                throw new ImageLoadException(source, $"图片尺寸无效：{width}×{height}。");
            }

            var buffer = new PixelBuffer(width, height, PixelFormat.Rgba8888Premultiplied);
            var span = converted.GetPixelSpan();
            var rowBytes = converted.RowBytes;
            var rowSize = width * 4;
            for (var y = 0; y < height; y++)
            {
                span.Slice(y * rowBytes, rowSize).CopyTo(buffer.Pixels.AsSpan(y * rowSize, rowSize));
            }

            DecodeCount++;
            return new ImageResource(source, buffer);
        }
    }

    /// <summary>把解码结果转换为预乘 RGBA8888；已经是目标格式时直接返回原图。</summary>
    private static SKBitmap ToPremultipliedRgba(SKBitmap source)
    {
        if (source.ColorType == SKColorType.Rgba8888 && source.AlphaType == SKAlphaType.Premul)
        {
            return source.Copy();
        }

        var target = new SKBitmap(new SKImageInfo(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(target);
        using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
        canvas.DrawBitmap(source, 0, 0, paint);
        canvas.Flush();
        return target;
    }
}
