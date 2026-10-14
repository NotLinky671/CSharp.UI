using CSharp.UI.Skia;
using global::OpenTK.Graphics.OpenGL4;
using global::OpenTK.Windowing.Common;
using global::OpenTK.Windowing.Desktop;
using global::OpenTK.Windowing.GraphicsLibraryFramework;

namespace CSharp.UI.OpenTK;

/// <summary>
/// 单窗口 OpenTK 宿主：窗口/上下文生命周期、主线程事件循环、按需重绘、输入适配与 DPI 处理。
/// </summary>
/// <remarks>
/// <para>
/// 主循环遵循“有工作就处理，空闲就阻塞等待事件”的模型：每个迭代先非阻塞排空系统事件、执行后台投递的动作、
/// 处理组件重建与布局，需要时绘制并交换缓冲；没有任何工作时才调用 <c>GLFW.WaitEvents</c>（有定时唤醒时用
/// <c>GLFW.WaitEventsTimeout</c>）阻塞等待，因此界面静止时既不忙等也不持续重绘。
/// </para>
/// <para>
/// 逻辑尺寸与 framebuffer 尺寸严格区分：<c>window.ClientSize</c> 作为逻辑像素视口，
/// <c>window.FramebufferSize</c> 与它之比作为 DPI 缩放，交给 <c>UiRuntime.SetViewport</c>。
/// </para>
/// </remarks>
internal sealed class OpenTkHost : IDisposable
{
    private const double MinimumWaitSeconds = 0.001;

    private readonly UiApplicationOptions _options;
    private readonly NativeWindow _window;
    private readonly OpenTkUiDispatcher _dispatcher;
    private GlTextureService _textures = null!;
    private GlRenderer _renderer = null!;
    private GlfwClipboardService _clipboard = null!;
    private OpenTkInputAdapter _input = null!;
    private UiRuntime _runtime = null!;
    private UiHostContext _context = null!;
    private IDisposable? _ownedTextService;
    private IDisposable? _ownedImageService;
    private bool _disposed;
    private volatile bool _exitRequested;
    private volatile int _exitCode;
    private bool _isMinimized;
    private Size _logicalSize;
    private Size _framebufferSize;
    private double _pixelScale = 1.0;
    private int _renderedFrameCount;
    private int _skippedFrameCount;
    private int _glErrorCount;

    /// <summary>创建宿主：创建窗口与 GL 上下文，组装服务，挂接事件。</summary>
    /// <exception cref="UiHostException">窗口或上下文创建失败时抛出（包含平台与驱动诊断）。</exception>
    internal OpenTkHost(View root, UiApplicationOptions options)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        _dispatcher = new OpenTkUiDispatcher(WakeUp);

        _window = GlWindowFactory.CreateWindow(
            options.Title,
            (int)Math.Round(options.Width),
            (int)Math.Round(options.Height),
            options.Resizable,
            options.VSync,
            visible: options.StartVisible,
            purpose: "UiApplication.Run 宿主窗口");

        try
        {
            _window.MakeCurrent();
            _textures = new GlTextureService(options.TextureCacheCapacity);
            _clipboard = new GlfwClipboardService(_window);

            // 宿主组装平台服务：未注入时创建 CSharp.UI.Skia（程序集 CSharp.UI.Text）的 SkiaSharp 实现，
            // 并负责在退出时释放；应用显式传入的实例由应用自行释放。
            var textService = options.TextService;
            if (textService is null)
            {
                var skiaText = new SkiaTextService();
                _ownedTextService = skiaText;
                textService = skiaText;
            }

            var imageService = options.ImageService;
            if (imageService is null)
            {
                var skiaImage = new SkiaImageService();
                _ownedImageService = skiaImage;
                imageService = skiaImage;
            }

            _runtime = new UiRuntime(root, new UiRuntimeServices
            {
                Dispatcher = _dispatcher,
                TextService = textService,
                TextureService = _textures,
                ImageService = imageService,
                Clipboard = options.ClipboardService ?? _clipboard,
                Clock = options.Clock ?? new StopwatchUiClock(),
            });

            _runtime.SetEnvironment(new EnvironmentValues
            {
                Theme = options.Theme,
                Font = options.DefaultFont,
            });
            _runtime.ExplicitClearColor = options.ClearColor;

            _renderer = new GlRenderer(_textures);
            _input = new OpenTkInputAdapter(_runtime, () => RequestExit(0), options.ExitOnEscape, options.ExitOnCloseShortcut);
            _context = new UiHostContext(_runtime, _dispatcher, _textures, GlWindowFactory.DescribeContext(), RequestExit)
            {
                PixelScale = 1.0,
            };

            HookWindowEvents();
            UpdateViewport(force: true);
        }
        catch
        {
            // 初始化失败：先释放需要上下文的 GL 资源，再销毁窗口。
            _renderer?.Dispose();
            _runtime?.Dispose();
            _textures?.Dispose();
            _ownedTextService?.Dispose();
            _ownedImageService?.Dispose();
            _window.Dispose();
            throw;
        }
    }

    /// <summary>宿主上下文（供应用与测试观察状态、请求退出）。</summary>
    internal UiHostContext Context => _context;

    /// <summary>宿主窗口；仅供内部与测试使用，应用不应直接操作其生命周期。</summary>
    internal NativeWindow Window => _window;

    /// <summary>已渲染帧数。</summary>
    internal int RenderedFrameCount => _renderedFrameCount;

    /// <summary>因最小化或尺寸为 0 而跳过的帧数。</summary>
    internal int SkippedFrameCount => _skippedFrameCount;

    /// <summary>请求退出事件循环。</summary>
    internal void RequestExit(int exitCode)
    {
        _exitCode = exitCode;
        _exitRequested = true;
        _dispatcher.RequestRedraw();
    }

    /// <summary>运行主事件循环，返回退出码。</summary>
    internal int Run()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _window.MakeCurrent();
        _runtime.Start();
        _options.Started?.Invoke(_context);

        while (!_exitRequested && _window.Exists)
        {
            // 1. 先非阻塞排空系统事件，保证输入在绘制之前生效。
            PumpEvents();

            // 2. 执行后台线程投递的动作（全部在 UI 线程）。
            _dispatcher.ProcessPostedActions();

            // 3. 定时唤醒（由 IUiDispatcher.RequestWakeup 安排）到期则请求重绘。
            if (_dispatcher.ConsumeDueWakeup())
            {
                _runtime.InvalidateRender();
            }

            _dispatcher.ConsumeRedrawRequest();

            // 4. 时钟推进回调：焦点视图（例如 TextField 光标闪烁）声明需要定时重绘时请求重绘，
            //    界面静止且没有此类焦点视图时不会产生额外重绘。
            _runtime.OnClockTick();

            // 5. 组件重建与布局。
            _runtime.ProcessPendingWork();

            // 6. 需要时绘制一帧。
            if (_runtime.NeedsDraw && CanRender())
            {
                DrawFrame();
            }

            if (_exitRequested)
            {
                break;
            }

            // 7. 还有工作时立即进入下一个迭代，避免无谓等待。
            if ((_runtime.NeedsDraw && CanRender()) || _runtime.HasPendingBuild || _dispatcher.HasPendingActions)
            {
                continue;
            }

            // 8. 空闲：阻塞等待事件或定时唤醒。
            WaitForEvents(ComputeWaitTimeout());
        }

        return _exitCode;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            // 顺序要求：先释放需要 GL 上下文的资源（渲染器/纹理），再销毁窗口；
            // 只在宿主默认组装时释放文本/图片服务，应用注入的服务由应用自行释放。
            _window.MakeCurrent();
            _renderer?.Dispose();
            _runtime?.Dispose();
            _textures?.Dispose();
            _ownedTextService?.Dispose();
            _ownedImageService?.Dispose();
        }
        finally
        {
            _window.Dispose();
        }
    }

    private void HookWindowEvents()
    {
        _window.Resize += _ => UpdateViewport(force: false);
        _window.FramebufferResize += _ => UpdateViewport(force: false);
        _window.Refresh += () => _runtime.InvalidateRender();
        _window.FocusedChanged += e => _input.HandleFocusChanged(e.IsFocused);
        _window.Minimized += e => OnMinimizedChanged(e.IsMinimized);
        _window.Closing += _ => RequestExit(0);
        _window.KeyDown += _input.HandleKeyDown;
        _window.KeyUp += _input.HandleKeyUp;
        _window.TextInput += _input.HandleTextInput;
        _window.MouseMove += _input.HandleMouseMove;
        _window.MouseDown += _input.HandleMouseDown;
        _window.MouseUp += _input.HandleMouseUp;
        _window.MouseLeave += _input.HandleMouseLeave;
        _window.MouseWheel += _input.HandleWheel;
    }

    private void OnMinimizedChanged(bool minimized)
    {
        _isMinimized = minimized;
        if (!minimized)
        {
            // 恢复后需要重新布局与重绘（最小化期间的尺寸变化会被合并处理）。
            UpdateViewport(force: true);
        }
    }

    /// <summary>更新逻辑尺寸、framebuffer 尺寸与 DPI；变化时重新布局并重绘。</summary>
    private void UpdateViewport(bool force)
    {
        var client = _window.ClientSize;
        var framebuffer = _window.FramebufferSize;
        var logical = new Size(client.X, client.Y);
        var physical = new Size(framebuffer.X, framebuffer.Y);

        if (framebuffer.X <= 0 || framebuffer.Y <= 0 || _window.WindowState == WindowState.Minimized)
        {
            _isMinimized = true;
            _context.IsMinimized = true;
            return;
        }

        _isMinimized = false;
        var scale = ComputePixelScale(logical, physical);
        var changed = force
            || logical != _logicalSize
            || physical != _framebufferSize
            || Math.Abs(scale - _pixelScale) > 0.0001;
        _logicalSize = logical;
        _framebufferSize = physical;
        _pixelScale = scale;
        _context.LogicalSize = logical;
        _context.FramebufferSize = physical;
        _context.PixelScale = scale;
        _context.IsMinimized = false;

        if (!changed)
        {
            return;
        }

        // SetViewport 会触发布局失效，并在 DPI 变化时清空文本与纹理缓存。
        _runtime.SetViewport(logical, scale);
        _runtime.InvalidateRender();
    }

    private void DrawFrame()
    {
        var frame = _runtime.BuildFrame();
        _renderer.Submit(frame);
        _window.Context.SwapBuffers();
        _renderedFrameCount++;
        _context.RenderedFrameCount = _renderedFrameCount;

        var error = GL.GetError();
        if (error != global::OpenTK.Graphics.OpenGL4.ErrorCode.NoError)
        {
            _glErrorCount++;
            _runtime.ReportDiagnostic($"OpenGL 错误：{error}（第 {_renderedFrameCount} 帧）");
        }

        _options.FrameRendered?.Invoke(_context);
    }

    private bool CanRender()
    {
        if (_isMinimized || _disposed || !_window.Exists || _exitRequested)
        {
            return false;
        }

        var framebuffer = _window.FramebufferSize;
        if (framebuffer.X <= 0 || framebuffer.Y <= 0)
        {
            _skippedFrameCount++;
            _isMinimized = true;
            _context.IsMinimized = true;
            return false;
        }

        return true;
    }

    private TimeSpan? ComputeWaitTimeout()
    {
        TimeSpan? best = _runtime.GetNextWakeup();
        var dispatcherWakeup = _dispatcher.NextWakeupDelay;
        if (dispatcherWakeup is { } delay && (best is null || delay < best.Value))
        {
            best = delay;
        }

        if (best is not { } wait)
        {
            return null;
        }

        // 已经到期或极短：给一个最小等待，避免在视图持续请求 0 延迟时忙等。
        return wait <= TimeSpan.Zero ? TimeSpan.FromSeconds(MinimumWaitSeconds) : wait;
    }

    private void PumpEvents()
    {
        _window.NewInputFrame();
        GLFW.PollEvents();
    }

    private void WaitForEvents(TimeSpan? timeout)
    {
        _window.NewInputFrame();
        if (timeout is not { } wait)
        {
            // 没有待办工作与定时唤醒：阻塞等待事件；后台 Post 通过 PostEmptyEvent 唤醒。
            GLFW.WaitEvents();
            return;
        }

        GLFW.WaitEventsTimeout(Math.Max(MinimumWaitSeconds, wait.TotalSeconds));
    }

    private void WakeUp()
    {
        try
        {
            // PostEmptyEvent 可在任意线程调用，用于打断 WaitEvents 的阻塞等待。
            GLFW.PostEmptyEvent();
        }
        catch (Exception)
        {
            // 窗口/GLFW 已不可用时忽略：调用方只关心事件循环尽快醒来看一眼队列。
        }
    }

    private static double ComputePixelScale(Size logical, Size physical)
    {
        if (logical.Width > 0 && physical.Width > 0)
        {
            return physical.Width / logical.Width;
        }

        if (logical.Height > 0 && physical.Height > 0)
        {
            return physical.Height / logical.Height;
        }

        return 1.0;
    }
}
