using CSharp.UI.OpenTK;

namespace CSharp.UI;

/// <summary>
/// 单窗口应用入口：创建窗口与 OpenGL 上下文，运行主线程事件循环，直到窗口关闭或请求退出。
/// </summary>
/// <remarks>
/// <para>本类型由 <c>CSharp.UI.OpenTK</c> 程序集实现，命名空间与核心 API 保持一致。</para>
/// <para>
/// 必须在进程主线程调用：GLFW 要求窗口与上下文在主线程创建。只支持单窗口，
/// 重复调用 <see cref="Run"/> 会抛出 <see cref="InvalidOperationException"/>。
/// </para>
/// <para>
/// 返回值为退出码：正常关闭窗口或按下退出快捷键为 0；<see cref="RequestExit"/> 可指定其他值。
/// 窗口/上下文创建失败时抛出 <see cref="UiHostException"/>，消息中包含平台、GLFW 与窗口设置诊断。
/// </para>
/// </remarks>
public static class UiApplication
{
    private static OpenTkHost? _current;
    private static int _running;

    /// <summary>当前是否有正在运行的宿主。</summary>
    public static bool IsRunning => _current is not null;

    /// <summary>当前宿主上下文；没有运行时为 null。</summary>
    public static UiHostContext? Current => _current?.Context;

    /// <summary>当前宿主实例；仅供同程序集与测试程序集（<c>InternalsVisibleTo</c>）观察窗口状态。</summary>
    internal static OpenTkHost? CurrentHost => _current;

    /// <summary>启动应用主循环并返回退出码。</summary>
    /// <param name="root">根视图。</param>
    /// <param name="options">窗口与服务配置；null 使用默认值。</param>
    public static int Run(View root, UiApplicationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        options ??= UiApplicationOptions.Default;
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            throw new InvalidOperationException("首版只支持单个 UiApplication.Run 实例；请等待前一个宿主退出后再启动。");
        }

        try
        {
            using var host = new OpenTkHost(root, options);
            _current = host;
            return host.Run();
        }
        catch (UiHostException ex)
        {
            // 把完整诊断同时写到标准错误，方便在无调试器时定位平台/驱动问题。
            Console.Error.WriteLine(ex.Message);
            throw;
        }
        finally
        {
            _current = null;
            Volatile.Write(ref _running, 0);
        }
    }

    /// <summary>请求当前宿主退出事件循环。</summary>
    /// <param name="exitCode">返回给 <see cref="Run"/> 的退出码。</param>
    /// <exception cref="InvalidOperationException">没有正在运行的宿主时抛出。</exception>
    public static void RequestExit(int exitCode = 0)
    {
        var host = _current ?? throw new InvalidOperationException("没有正在运行的 UI 宿主；请先调用 UiApplication.Run。");
        host.RequestExit(exitCode);
    }

    /// <summary>请求重绘一帧（等价于使当前运行时渲染失效）。</summary>
    public static void RequestRedraw() => _current?.Context.RequestRedraw();
}

/// <summary>
/// OpenTK 宿主配置：窗口属性、主题、默认字体、可替换的文本/图片/剪贴板服务与观测钩子。
/// </summary>
public sealed record UiApplicationOptions
{
    /// <summary>默认配置：800×600 可调整大小窗口、明亮主题、启用 VSync。</summary>
    public static UiApplicationOptions Default { get; } = new();

    /// <summary>窗口标题。</summary>
    public string Title { get; init; } = "CSharp.UI";

    /// <summary>初始逻辑宽度（逻辑像素）。</summary>
    public double Width { get; init; } = 800;

    /// <summary>初始逻辑高度（逻辑像素）。</summary>
    public double Height { get; init; } = 600;

    /// <summary>窗口是否可调整大小。</summary>
    public bool Resizable { get; init; } = true;

    /// <summary>启动时是否显示窗口（测试或无头场景可设为 false）。</summary>
    public bool StartVisible { get; init; } = true;

    /// <summary>是否启用垂直同步（关闭后交换缓冲立即返回，便于压测）。</summary>
    public bool VSync { get; init; } = true;

    /// <summary>初始主题。</summary>
    public Theme Theme { get; init; } = Theme.Light;

    /// <summary>默认字体；null 表示由文本服务按平台默认解析。</summary>
    public FontDescription? DefaultFont { get; init; }

    /// <summary>清屏色；null 表示使用主题背景色。</summary>
    public Color? ClearColor { get; init; }

    /// <summary>
    /// 文本服务；null 时宿主创建并持有 <c>CSharp.UI.Text.SkiaTextService</c>（退出时由宿主释放）。
    /// 由应用传入的实例由应用自行释放，宿主不会代为释放。
    /// </summary>
    public ITextService? TextService { get; init; }

    /// <summary>
    /// 图片解码服务；null 时宿主创建并持有 <c>CSharp.UI.Text.SkiaImageService</c>（退出时由宿主释放）。
    /// 由应用传入的实例由应用自行释放，宿主不会代为释放。
    /// </summary>
    public IImageService? ImageService { get; init; }

    /// <summary>剪贴板服务；null 时使用 GLFW 剪贴板实现。</summary>
    public IClipboardService? ClipboardService { get; init; }

    /// <summary>UI 时钟；null 时使用 <see cref="StopwatchUiClock"/>。</summary>
    public IUiClock? Clock { get; init; }

    /// <summary>
    /// 系统滚轮设置服务；null 时宿主使用只读查询 <c>SPI_GETWHEELSCROLLLINES/CHARS</c> 的 Windows 实现，
    /// 非 Windows 或查询失败回退到每格 3 行/3 字符。注入自定义实现可覆盖滚轮步长（测试或特殊设备）。
    /// </summary>
    public IWheelScrollSettingsService? WheelScrollSettingsService { get; init; }

    /// <summary>纹理缓存容量上限（超出后按最近最少使用淘汰）。</summary>
    public int TextureCacheCapacity { get; init; } = 128;

    /// <summary>按 Esc 是否退出应用。</summary>
    public bool ExitOnEscape { get; init; } = true;

    /// <summary>按 Ctrl+Q / Cmd+Q 是否退出应用。</summary>
    public bool ExitOnCloseShortcut { get; init; } = true;

    /// <summary>窗口与渲染器就绪后、进入事件循环之前在主线程调用一次。</summary>
    public Action<UiHostContext>? Started { get; init; }

    /// <summary>每渲染一帧后调用（主线程），可用于统计或测试。</summary>
    public Action<UiHostContext>? FrameRendered { get; init; }
    /// <summary>
    /// <b>内部测试开关</b>：让生产宿主改用 TSF 输入桥（TI-07 P3a）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>不是公开 API：</b>internal，仅供本仓库验收工具经 <c>InternalsVisibleTo</c> 设置。
    /// 默认 <c>false</c>，生产默认仍使用既有 IMM 桥；未暴露“后端选择器”。
    /// </para>
    /// <para>
    /// <b>失败即回退：</b>TSF 建立失败（MTA/初始化/冲突等）时，宿主按显式原因回退到 IMM，
    /// 并把原因记录在 <see cref="UiHostContext.TextInputBackendReason"/>，不并行启用两个写入来源。
    /// </para>
    /// </remarks>
    internal bool UseTsfInputBridge { get; init; }

    /// <summary>校验配置；非法值抛出 <see cref="ArgumentOutOfRangeException"/>。</summary>
    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Title);
        if (Width <= 0 || double.IsNaN(Width) || double.IsInfinity(Width))
        {
            throw new ArgumentOutOfRangeException(nameof(Width), Width, "窗口逻辑宽度必须为正的有限值。");
        }

        if (Height <= 0 || double.IsNaN(Height) || double.IsInfinity(Height))
        {
            throw new ArgumentOutOfRangeException(nameof(Height), Height, "窗口逻辑高度必须为正的有限值。");
        }

        if (TextureCacheCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(TextureCacheCapacity), TextureCacheCapacity, "纹理缓存容量必须为正。");
        }

        ArgumentNullException.ThrowIfNull(Theme);
    }
}

/// <summary>
/// 宿主上下文：向应用与测试暴露运行时、调度器、纹理服务与窗口/DPI 状态。
/// </summary>
public sealed class UiHostContext
{
    private readonly Action<int> _requestExit;

    /// <summary>由宿主创建。</summary>
    internal UiHostContext(
        UiRuntime runtime,
        IUiDispatcher dispatcher,
        ITextureService? textureService,
        string glContextDescription,
        Action<int> requestExit)
    {
        Runtime = runtime;
        Dispatcher = dispatcher;
        TextureService = textureService;
        GlContextDescription = glContextDescription;
        _requestExit = requestExit;
    }

    /// <summary>UI 运行时。</summary>
    public UiRuntime Runtime { get; }

    /// <summary>UI 调度器（可在后台线程调用 <see cref="IUiDispatcher.Post"/>）。</summary>
    public IUiDispatcher Dispatcher { get; }

    /// <summary>纹理服务；宿主始终提供 OpenGL 纹理服务。</summary>
    public ITextureService? TextureService { get; }

    /// <summary>当前 GL 上下文描述（版本 / 渲染器 / 厂商）。</summary>
    public string GlContextDescription { get; }

    /// <summary>已渲染帧数。</summary>
    public int RenderedFrameCount { get; internal set; }

    /// <summary>窗口当前是否最小化（最小化期间跳过渲染）。</summary>
    public bool IsMinimized { get; internal set; }

    /// <summary>逻辑像素视口尺寸。</summary>
    public Size LogicalSize { get; internal set; }

    /// <summary>framebuffer 像素尺寸。</summary>
    public Size FramebufferSize { get; internal set; }

    /// <summary>逻辑像素 → framebuffer 像素的缩放（DPI）。</summary>
    public double PixelScale { get; internal set; } = 1.0;

    /// <summary>请求退出事件循环；可从任意线程调用。</summary>
    public void RequestExit(int exitCode = 0) => _requestExit(exitCode);

    /// <summary>请求重绘一帧；可从任意线程调用。</summary>
    public void RequestRedraw() => Runtime.InvalidateRender();
}
