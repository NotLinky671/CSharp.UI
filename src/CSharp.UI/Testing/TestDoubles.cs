using System.Collections.Concurrent;
using System.Globalization;

namespace CSharp.UI.Testing;

/// <summary>
/// 无窗口测试用调度器：记录投递的动作与重绘/唤醒请求，由测试显式执行队列。
/// </summary>
/// <remarks>创建它的线程被视作 UI 线程，便于在测试中验证线程约束。</remarks>
public sealed class HeadlessUiDispatcher : IUiDispatcher
{
    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly int _uiThreadId = Environment.CurrentManagedThreadId;
    private int _redrawRequests;
    private long _nextWakeupTicks = -1;

    /// <inheritdoc />
    public bool IsOnUiThread => Environment.CurrentManagedThreadId == _uiThreadId;

    /// <summary>待执行的投递动作数量。</summary>
    public int PendingCount => _queue.Count;

    /// <summary>累计重绘请求次数。</summary>
    public int RedrawRequestCount => Volatile.Read(ref _redrawRequests);

    /// <summary>最近一次被请求的最早唤醒延迟；未请求过时为 null。</summary>
    public TimeSpan? NextWakeup
    {
        get
        {
            var ticks = Interlocked.Read(ref _nextWakeupTicks);
            return ticks < 0 ? null : TimeSpan.FromTicks(ticks);
        }
    }

    /// <inheritdoc />
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _queue.Enqueue(action);
    }

    /// <inheritdoc />
    public void RequestRedraw() => Interlocked.Increment(ref _redrawRequests);

    /// <inheritdoc />
    public void RequestWakeup(TimeSpan delay)
    {
        var ticks = delay.Ticks;
        long current;
        do
        {
            current = Interlocked.Read(ref _nextWakeupTicks);
            if (current >= 0 && current <= ticks)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref _nextWakeupTicks, ticks, current) != current);
    }

    /// <summary>在当前线程执行全部已投递动作，返回执行数量。</summary>
    public int RunPending()
    {
        var count = 0;
        while (_queue.TryDequeue(out var action))
        {
            action();
            count++;
        }

        return count;
    }

    /// <summary>清除唤醒请求（模拟一次事件循环迭代消费了定时唤醒）。</summary>
    public void ClearWakeup() => Interlocked.Exchange(ref _nextWakeupTicks, -1);
}

/// <summary>测试用可控时钟。</summary>
public sealed class TestUiClock : IUiClock
{
    /// <summary>当前时间。</summary>
    public TimeSpan Now { get; set; }

    /// <summary>推进时间。</summary>
    public void Advance(TimeSpan delta) => Now += delta;
}

/// <summary>
/// 测试用文本服务：按等宽字体估算排版结果，不加载任何原生字体库。
/// </summary>
/// <remarks>
/// 通过 <see cref="CharWidthScale"/> 可以让文本“变宽”，用于验证宽度变化触发布局重新测量。
/// </remarks>
public sealed class StubTextService : ITextService
{
    private readonly LruCache<TextLayoutRequest, TextLayoutResult> _layouts = new(64);
    private readonly LruCache<TextLayoutResult, PixelBuffer> _rasters = new(32);
    private int _layoutMisses;
    private int _rasterMisses;

    private double _charWidthScale = 0.6;

    /// <summary>
    /// 由字符宽度比例调整的实测宽度系数（默认 0.6 × 字号）。
    /// 修改会清空缓存，模拟“度量提供方发生变化”的场景。
    /// </summary>
    public double CharWidthScale
    {
        get => _charWidthScale;
        set
        {
            if (Math.Abs(value - _charWidthScale) < 0.0001)
            {
                return;
            }

            _charWidthScale = value;
            ClearCaches();
        }
    }

    /// <inheritdoc />
    public int CachedLayoutCount => _layouts.Count;

    /// <inheritdoc />
    public int CachedRasterCount => _rasters.Count;

    /// <inheritdoc />
    public int LayoutCacheCapacity
    {
        get => _layouts.Capacity;
        set => _layouts.Capacity = value;
    }

    /// <summary>实际执行排版的次数（缓存命中不计数）。</summary>
    public int LayoutMisses => _layoutMisses;

    /// <summary>实际执行栅格化的次数。</summary>
    public int RasterMisses => _rasterMisses;

    /// <inheritdoc />
    public TextLayoutResult Layout(TextLayoutRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var hadValue = _layouts.TryGet(request, out var cached);
        if (hadValue)
        {
            return cached!;
        }

        _layoutMisses++;
        var result = Build(request);
        _layouts.GetOrAdd(request, _ => result, out _);
        return result;
    }

    /// <inheritdoc />
    public Size Measure(TextLayoutRequest request) => Layout(request).Size;

    /// <inheritdoc />
    public FontMetrics GetFontMetrics(FontDescription font)
        => new(font.Size * 0.8, font.Size * 0.2, font.Size * 0.25);

    /// <inheritdoc />
    public PixelBuffer Rasterize(TextLayoutResult layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var hadValue = _rasters.TryGet(layout, out var cached);
        if (hadValue)
        {
            return cached!;
        }

        _rasterMisses++;
        var width = Math.Max(1, (int)Math.Ceiling(layout.Size.Width * layout.PixelScale));
        var height = Math.Max(1, (int)Math.Ceiling(layout.Size.Height * layout.PixelScale));
        var buffer = new PixelBuffer(width, height, PixelFormat.Alpha8);
        Array.Fill(buffer.Pixels, (byte)200);
        _rasters.GetOrAdd(layout, _ => buffer, out _);
        return buffer;
    }

    /// <inheritdoc />
    public void ClearCaches()
    {
        _layouts.Clear();
        _rasters.Clear();
    }

    private TextLayoutResult Build(TextLayoutRequest request)
    {
        var text = request.Text;
        var glyphWidth = Math.Max(1, request.Font.Size * CharWidthScale) + request.LetterSpacing;
        var lineHeight = request.LineHeight > 0 ? request.LineHeight : request.Font.Size * 1.25;
        var clusters = GraphemeStarts(text);

        var lines = new List<(int Start, int End)>();
        var isTrimmed = false;

        if (request.Wrapping == TextWrapping.Wrap && !double.IsPositiveInfinity(request.MaxWidth))
        {
            lines.AddRange(WrapClusters(text, clusters, glyphWidth, request.MaxWidth));
        }
        else
        {
            lines.Add((0, text.Length));
        }

        // 省略：截断可见文本。
        var visibleText = text;
        if (request.Trimming == TextTrimming.Ellipsis && !request.MaxWidth.Equals(double.PositiveInfinity))
        {
            var maxClusters = (int)Math.Floor(Math.Max(0, request.MaxWidth - glyphWidth) / glyphWidth);
            if (clusters.Count - 1 > maxClusters)
            {
                visibleText = string.Concat(text.AsSpan(0, clusters[Math.Max(0, maxClusters)]), "…");
                isTrimmed = true;
                lines.Clear();
                lines.Add((0, visibleText.Length));
            }
        }

        if (lines.Count > request.MaxLines && request.MaxLines > 0)
        {
            lines.RemoveRange(request.MaxLines, lines.Count - request.MaxLines);
            if (request.Trimming == TextTrimming.Ellipsis)
            {
                isTrimmed = true;
            }
        }

        var visibleClusters = GraphemeStarts(visibleText);
        var stops = new List<int>(visibleClusters);
        if (stops.Count == 0 || stops[^1] != visibleText.Length)
        {
            stops.Add(visibleText.Length);
        }

        double maxWidth = 0;
        foreach (var (start, end) in lines)
        {
            maxWidth = Math.Max(maxWidth, (end - start) * glyphWidth);
        }

        var lineInfos = new List<TextLineInfo>(lines.Count);
        var caretXs = new List<double>(stops.Count);
        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            var (start, end) = lines[lineIndex];
            var contentWidth = (end - start) * glyphWidth;
            var offsetX = request.Alignment switch
            {
                TextAlignment.Center => (maxWidth - contentWidth) / 2,
                TextAlignment.Right => maxWidth - contentWidth,
                _ => 0,
            };
            var bounds = new Rect(offsetX, lineIndex * lineHeight, contentWidth, lineHeight);
            lineInfos.Add(new TextLineInfo(start, end, bounds, lineIndex * lineHeight + (lineHeight * 0.8)));

            foreach (var stop in stops)
            {
                if (stop < start || stop > end)
                {
                    continue;
                }

                caretXs.Add(offsetX + ((stop - start) * glyphWidth));
            }
        }

        if (caretXs.Count != stops.Count)
        {
            // 兜底：保证数量一致（通常不会发生）。
            caretXs.Clear();
            foreach (var stop in stops)
            {
                caretXs.Add(stop * glyphWidth);
            }
        }

        var size = new Size(maxWidth, Math.Max(lineHeight, lines.Count * lineHeight));
        return new TextLayoutResult(visibleText, request.Font, request.PixelScale, size, lineInfos, stops, caretXs, isTrimmed);
    }

    private static List<(int Start, int End)> WrapClusters(string text, List<int> clusters, double glyphWidth, double maxWidth)
    {
        var lines = new List<(int Start, int End)>();
        if (text.Length == 0)
        {
            lines.Add((0, 0));
            return lines;
        }

        var perLine = maxWidth.Equals(double.PositiveInfinity)
            ? int.MaxValue
            : Math.Max(1, (int)Math.Floor(maxWidth / glyphWidth));
        var lineStart = 0;
        var count = 0;
        for (var i = 0; i < clusters.Count; i++)
        {
            count++;
            if (count >= perLine)
            {
                var end = i + 1 < clusters.Count ? clusters[i + 1] : text.Length;
                lines.Add((lineStart, end));
                lineStart = end;
                count = 0;
            }
        }

        if (lineStart < text.Length || lines.Count == 0)
        {
            lines.Add((lineStart, text.Length));
        }

        return lines;
    }

    private static List<int> GraphemeStarts(string text)
    {
        var result = new List<int>();
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            result.Add(enumerator.ElementIndex);
        }

        return result;
    }
}

/// <summary>
/// 测试用纹理服务：不上传 GPU 资源，只分配递增句柄并记录创建/释放数量。
/// </summary>
public sealed class RecordingTextureService : ITextureService
{
    // 键按 Equals 比较：值类型键（例如文本纹理键）按键值，引用类型键（例如图片资源）按引用。
    private readonly Dictionary<object, TextureHandle> _map = new(EqualityComparer<object>.Default);
    private readonly Queue<object> _order = new();
    private long _nextId;
    private int _created;
    private int _released;

    /// <inheritdoc />
    public int LiveTextureCount => _map.Count;

    /// <summary>累计创建的纹理数量。</summary>
    public int CreatedCount => _created;

    /// <summary>累计释放的纹理数量。</summary>
    public int ReleasedCount => _released;

    /// <summary>工厂被调用的次数（即实际上传次数）。</summary>
    public int FactoryInvocations { get; private set; }

    /// <summary>最近一次上传的位图尺寸。</summary>
    public Size? LastUploadSize { get; private set; }

    private int _capacity = 256;

    /// <inheritdoc />
    public int Capacity
    {
        get => _capacity;
        set
        {
            _capacity = Math.Max(1, value);
            while (_map.Count > _capacity && _order.Count > 0)
            {
                if (_map.Remove(_order.Dequeue()))
                {
                    _released++;
                }
            }
        }
    }

    /// <inheritdoc />
    public TextureHandle GetOrCreate(object key, Func<PixelBuffer> factory)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(factory);
        if (_map.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var buffer = factory();
        FactoryInvocations++;
        LastUploadSize = new Size(buffer.Width, buffer.Height);
        var handle = new TextureHandle(
            Interlocked.Increment(ref _nextId),
            buffer.Width,
            buffer.Height,
            buffer.Format == PixelFormat.Alpha8);
        _created++;
        _map[key] = handle;
        _order.Enqueue(key);

        while (_map.Count > Capacity && _order.Count > 0)
        {
            var oldest = _order.Dequeue();
            if (_map.Remove(oldest))
            {
                _released++;
            }
        }

        return handle;
    }

    /// <inheritdoc />
    public void Release(object key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (_map.Remove(key))
        {
            _released++;
        }
    }

    /// <inheritdoc />
    public void Clear()
    {
        _released += _map.Count;
        _map.Clear();
        _order.Clear();
    }
}

/// <summary>测试用图片服务：按注册的来源返回位图，未注册的来源抛出 <see cref="ImageLoadException"/>。</summary>
public sealed class StubImageService : IImageService
{
    private readonly Dictionary<string, ImageResource> _resources = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();

    /// <inheritdoc />
    public int CachedImageCount => _resources.Count;

    private int _capacity = 16;

    /// <inheritdoc />
    public int Capacity
    {
        get => _capacity;
        set
        {
            _capacity = Math.Max(1, value);
            while (_resources.Count > _capacity && _order.Count > 0)
            {
                var oldest = _order.Dequeue();
                if (_resources.Remove(oldest, out var evicted))
                {
                    evicted.Dispose();
                }
            }
        }
    }

    /// <summary>加载失败的次数（用于验证诊断）。</summary>
    public int FailureCount { get; private set; }

    /// <summary>注册（或替换）一个来源对应的图片。</summary>
    public ImageResource Register(string source, int width, int height, double scale = 1.0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        var buffer = new PixelBuffer(width, height, PixelFormat.Rgba8888Premultiplied);
        Array.Fill(buffer.Pixels, (byte)255);
        var resource = new ImageResource(source, buffer, scale);
        if (_resources.TryGetValue(source, out var existing))
        {
            existing.Dispose();
        }
        else
        {
            _order.Enqueue(source);
        }

        _resources[source] = resource;
        while (_resources.Count > Capacity && _order.Count > 0)
        {
            var oldest = _order.Dequeue();
            if (oldest != source && _resources.Remove(oldest, out var evicted))
            {
                evicted.Dispose();
            }
        }

        return resource;
    }

    /// <inheritdoc />
    public ImageResource Load(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (_resources.TryGetValue(source, out var resource) && !resource.IsDisposed)
        {
            return resource;
        }

        FailureCount++;
        throw new ImageLoadException(source, "测试替身未注册该来源");
    }

    /// <inheritdoc />
    public bool TryLoad(string source, out ImageResource? resource)
    {
        if (_resources.TryGetValue(source, out var found) && !found.IsDisposed)
        {
            resource = found;
            return true;
        }

        FailureCount++;
        resource = null;
        return false;
    }

    /// <inheritdoc />
    public void Clear()
    {
        foreach (var resource in _resources.Values)
        {
            resource.Dispose();
        }

        _resources.Clear();
        _order.Clear();
    }
}

/// <summary>测试用剪贴板服务。</summary>
public sealed class StubClipboardService : IClipboardService
{
    private string _text = string.Empty;

    /// <summary>写入次数。</summary>
    public int SetCount { get; private set; }

    /// <inheritdoc />
    public string? GetText() => _text;

    /// <inheritdoc />
    public void SetText(string text)
    {
        _text = text ?? string.Empty;
        SetCount++;
    }
}
