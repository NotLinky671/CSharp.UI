namespace CSharp.UI;

/// <summary>解码后的图片资源，持有 CPU 位图。</summary>
/// <remarks>
/// 所有权：<see cref="ImageResource"/> 由 <see cref="IImageService"/> 创建、缓存并释放；
/// 视图只保存来源字符串，运行时节点通过服务按需取回，不直接持有或释放资源。
/// </remarks>
public sealed class ImageResource : IDisposable
{
    private bool _disposed;

    /// <summary>创建图片资源。</summary>
    public ImageResource(string source, PixelBuffer pixels, double scale = 1.0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentNullException.ThrowIfNull(pixels);
        Source = source;
        Pixels = pixels;
        Scale = scale <= 0 ? 1.0 : scale;
    }

    /// <summary>来源字符串（文件路径或应用资源标识）。</summary>
    public string Source { get; }

    /// <summary>解码后的位图。</summary>
    public PixelBuffer Pixels { get; }

    /// <summary>像素密度：逻辑尺寸 = 像素尺寸 / Scale。</summary>
    public double Scale { get; }

    /// <summary>像素尺寸。</summary>
    public Size PixelSize => new(Pixels.Width, Pixels.Height);

    /// <summary>自然逻辑尺寸（保持像素与逻辑 1:1 缩放按 <see cref="Scale"/> 折算）。</summary>
    public Size NaturalSize => new(Pixels.Width / Scale, Pixels.Height / Scale);

    /// <summary>是否已释放。</summary>
    public bool IsDisposed => _disposed;

    /// <summary>释放位图引用；由图片服务调用。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        PixelsDisposed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>位图被释放时触发，纹理服务借此失效对应纹理。</summary>
    public event EventHandler? PixelsDisposed;

    public override string ToString() => $"Image({Source}, {PixelSize}{(IsDisposed ? ", disposed" : string.Empty)})";
}

/// <summary>图片资源服务契约：按来源解码并缓存图片。实现位于 <c>CSharp.UI.Skia</c>（SkiaSharp 解码）。</summary>
/// <remarks>
/// 所有权：服务拥有返回的资源并负责最终释放；同一来源返回同一实例，避免重建时重复解码与上传。
/// 缓存必须有界，淘汰时释放位图并通知纹理服务失效。
/// </remarks>
public interface IImageService
{
    /// <summary>取回（必要时解码）来源对应的图片。文件不存在或内容无效时抛出 <see cref="ImageLoadException"/>。</summary>
    ImageResource Load(string source);

    /// <summary>尝试取回图片，失败时返回 false 而不抛出。</summary>
    bool TryLoad(string source, out ImageResource? resource);

    /// <summary>当前缓存的图片数量。</summary>
    int CachedImageCount { get; }

    /// <summary>缓存容量上限。</summary>
    int Capacity { get; set; }

    /// <summary>清空缓存并释放全部位图。</summary>
    void Clear();
}

/// <summary>图片加载失败（文件不存在、格式无效或解码失败）。</summary>
public sealed class ImageLoadException : Exception
{
    /// <summary>使用来源与原因构造。</summary>
    public ImageLoadException(string source, string message)
        : base($"无法加载图片 \"{source}\"：{message}")
    {
        ImageSource = source;
    }

    /// <summary>图片来源。</summary>
    public string ImageSource { get; }
}

/// <summary>剪贴板服务契约；由宿主实现（OpenTK 宿主使用 GLFW 剪贴板）。</summary>
public interface IClipboardService
{
    /// <summary>读取纯文本；剪贴板为空或无文本时返回 null。</summary>
    string? GetText();

    /// <summary>写入纯文本。</summary>
    void SetText(string text);
}

/// <summary>UI 时钟；用于光标闪烁等与时间相关的重绘判定。测试可注入可控实现。</summary>
public interface IUiClock
{
    /// <summary>自任意起点起的单调时间。</summary>
    TimeSpan Now { get; }
}

/// <summary>基于 <see cref="System.Diagnostics.Stopwatch"/> 的默认时钟。</summary>
public sealed class StopwatchUiClock : IUiClock
{
    private readonly System.Diagnostics.Stopwatch _stopwatch = System.Diagnostics.Stopwatch.StartNew();

    /// <inheritdoc />
    public TimeSpan Now => _stopwatch.Elapsed;
}

/// <summary>
/// UI 调度契约：把工作投递到 UI 线程、请求重绘并安排定时唤醒。
/// </summary>
/// <remarks>
/// 约定：<see cref="Post"/> 与 <see cref="RequestRedraw"/> 可以在任意线程调用；
/// 其余所有框架 API（状态读写、布局、绘制命令生成）只能在 UI 线程调用。
/// </remarks>
public interface IUiDispatcher
{
    /// <summary>当前线程是否为 UI 线程。</summary>
    bool IsOnUiThread { get; }

    /// <summary>把动作排入 UI 线程队列；UI 线程空闲后按顺序执行。</summary>
    void Post(Action action);

    /// <summary>请求重绘一帧；静止界面不因此持续重绘。</summary>
    void RequestRedraw();

    /// <summary>
    /// 请求事件循环在 <paramref name="delay"/> 之后至少唤醒一次。
    /// 用于光标闪烁等定时重绘；多个请求取最早时间。
    /// </summary>
    void RequestWakeup(TimeSpan delay);
}

/// <summary>在非 UI 线程读写 UI 状态或执行布局/绘制时抛出。</summary>
public sealed class UiThreadViolationException : InvalidOperationException
{
    /// <summary>使用说明构造。</summary>
    public UiThreadViolationException(string message)
        : base(message)
    {
    }

    /// <summary>以“应使用 UiDispatcher.Post”的默认诊断构造。</summary>
    public static UiThreadViolationException ForPost()
        => new("状态只能在 UI 线程读写；后台线程请使用 UiDispatcher.Post(action) 把结果投递到 UI 线程。");
}

/// <summary>
/// 全局 UI 调度入口：<c>UiDispatcher.Post(action)</c>。
/// </summary>
/// <remarks>
/// 首版单窗口：<see cref="UiRuntime"/> 启动时把自身调度器注册为 <see cref="Current"/>，释放时清除。
/// 未启动运行时时调用 <see cref="Post"/> 会抛出 <see cref="InvalidOperationException"/>。
/// </remarks>
public static class UiDispatcher
{
    private static IUiDispatcher? _current;

    /// <summary>当前 UI 调度器；没有活动运行时时为 null。</summary>
    public static IUiDispatcher? Current => _current;

    /// <summary>把动作投递到当前 UI 线程。</summary>
    public static void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var dispatcher = _current ?? throw new InvalidOperationException("没有活动的 UI 运行时；请先调用 UiApplication.Run 启动宿主，或使用注入的调度器。");
        dispatcher.Post(action);
    }

    /// <summary>请求重绘当前 UI。</summary>
    public static void RequestRedraw() => _current?.RequestRedraw();

    internal static void Register(IUiDispatcher dispatcher) => _current = dispatcher;

    internal static void Unregister(IUiDispatcher dispatcher)
    {
        if (ReferenceEquals(_current, dispatcher))
        {
            _current = null;
        }
    }
}
