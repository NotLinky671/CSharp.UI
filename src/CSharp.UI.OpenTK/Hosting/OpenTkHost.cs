using CSharp.UI.Skia;
using global::OpenTK.Graphics.OpenGL4;
using global::OpenTK.Windowing.Common;
using global::OpenTK.Windowing.Desktop;
using global::OpenTK.Windowing.GraphicsLibraryFramework;
using System.Runtime.InteropServices;

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
/// 三个坐标空间严格区分：GLFW 窗口内容区坐标、UI 逻辑像素、framebuffer 物理像素。UI 缩放取窗口当前内容缩放
/// （<c>glfwGetWindowContentScale</c>），逻辑视口 = framebuffer / 内容缩放，指针逻辑坐标 = 窗口坐标 ×
/// (framebuffer/window) / 内容缩放；换算集中在 <see cref="ViewportMetrics"/>。
/// </para>
/// <para>
/// 初始 <c>UiApplicationOptions.Width/Height</c> 是逻辑尺寸：窗口创建后按当前内容缩放反推客户区尺寸，
/// 使 framebuffer 覆盖请求的逻辑像素（Windows 150% 下 400×300 → 600×450）。系统缩放或显示器变化时由 GLFW
/// 内容缩放回调重新计算视口、失效文本/纹理缓存并重绘；不使用逐帧轮询。
/// </para>
/// </remarks>
internal sealed class OpenTkHost : IDisposable
{
    private const double MinimumWaitSeconds = 0.001;

    /// <summary>单次窗口事件内允许的即时重绘遍数上限，避免用户回调反复触发尺寸事件时在回调里死循环。</summary>
    private const int MaxImmediateRedrawPasses = 4;

    private readonly UiApplicationOptions _options;
    private readonly NativeWindow _window;
    private readonly OpenTkUiDispatcher _dispatcher;
    private GlTextureService _textures = null!;
    private GlRenderer _renderer = null!;
    private GlfwClipboardService _clipboard = null!;
    private OpenTkInputAdapter _input = null!;
    private WindowsImeBridge? _ime;
    private IWindowsInputBridge? _inputBridge;

    /// <summary>TSF 输入桥（内部测试开关启用时非 null）；验收工具读取其状态。</summary>
    /// <summary>稳定的 Windows 输入路由器（构造期创建，运行时与适配器共同持有）。</summary>
    private WindowsTsf.WindowsTextInputRouter? _router;

    /// <summary>候选 overlay：帧末、内容之后、提交之前追加；非 Windows 时为 null。</summary>
    private WindowsTsf.CandidateOverlay? _candidateOverlay;

    /// <summary>非 Windows 平台化 committed-only 文本输入服务（Windows 为 null）。</summary>
    private ITextInputService? _platformTextInput;

    private UiRuntime _runtime = null!;
    private UiHostContext _context = null!;
    private IDisposable? _ownedTextService;
    private IDisposable? _ownedImageService;
    private GLFWCallbacks.WindowContentScaleCallback? _contentScaleCallback;
    private GLFWCallbacks.WindowContentScaleCallback? _previousContentScaleCallback;
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
    private bool _immediateRedrawEnabled;
    private bool _immediateRedrawActive;
    private bool _immediateRedrawPending;
    private bool _frameInProgress;
    private bool _processingWork;

    /// <summary>本轮是否真的交付了延后输入（用于“有进展才立即继续”，避免忙等）。</summary>
    private bool _deferredProgressThisIteration;
    private int _immediateRedrawCount;

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
            purpose: "UiApplication.Run 宿主窗口",
            // 桌面宿主窗口：请求尺寸按逻辑像素解释，由 GLFW 在 Windows/X11 上按显示器缩放创建。
            // 离屏/测试窗口不走这条路径（保持 false）。
            scaleToMonitor: true);

        try
        {
            _window.MakeCurrent();
            _textures = new GlTextureService(options.TextureCacheCapacity);
            _clipboard = new GlfwClipboardService(_window);

            // Windows 输入：**先**组装稳定的路由器（TI-07 P3a R7）。
            // 路由器必须在 UiRuntime 构造之前存在，并被 UiRuntimeServices 与输入适配器**同时**持有，
            // 否则后端切换只会改宿主字段，运行时/适配器仍指向已释放的旧 IMM。
            if (OperatingSystem.IsWindows())
            {
                var hwnd = WindowsImeBridge.GetWindowHandle(_window);
                if (hwnd != IntPtr.Zero)
                {
                    _ime = new WindowsImeBridge(
                        hwnd,
                        new Imm32Interop(hwnd),
                        _dispatcher,
                        () => (_window.ClientSize.X, _window.ClientSize.Y));
                    _ime.InstallSubclass();
                }

                // 候选 overlay 必须在运行时与 TSF 会话之前存在：运行时的帧末入口直接指向它，
                // 会话惰性建立时由路由器把同一实例交给 sink/几何发布方。
                _candidateOverlay = new WindowsTsf.CandidateOverlay();

                // 工厂：TSF 退役/失败后创建**新的**可用 IMM（绝不交回已释放实例）。
                var immHwnd = hwnd;
                _router = new WindowsTsf.WindowsTextInputRouter(
                    _ime,
                    options.UseTsfInputBridge,
                    immFactory: () =>
                    {
                        try
                        {
                            var fresh = new WindowsImeBridge(
                                immHwnd,
                                new Imm32Interop(immHwnd),
                                _dispatcher,
                                () => (_window.ClientSize.X, _window.ClientSize.Y));
                            fresh.InstallSubclass();
                            return fresh;
                        }
                        catch (Exception ex) when (ex is not OutOfMemoryException)
                        {
                            return null;
                        }
                    });
                _router.AttachHostWindow(hwnd);
                if (_candidateOverlay is not null)
                {
                    _router.AttachCandidateOverlay(_candidateOverlay);
                }

                // 真实宿主工作状态：重建/布局/帧期间不安全（不得硬编码 false）。
                // 必须同时纳入 frame：BuildFrame/提交/交换缓冲期间写文档同样是重入。
                _router.AttachHostWorkGuard(() => _processingWork || _frameInProgress);
                _inputBridge = _router;
            }
            else
            {
                // TI-08：非 Windows 只有已提交文本通道——分级 Reason 如实报告平台接入限制，
                // 不实例化任何 Windows 桥类型，也不触发 Windows 原生库。
                _platformTextInput = new NoTextInputService(DescribeCurrentPlatformReason());
            }

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
                // null → 复用 TextService 若其实现 IRichTextService；外部单接口实现不强转（得 null）。
                RichTextService = options.RichTextService ?? (textService as IRichTextService),
                TextureService = _textures,
                ImageService = imageService,
                Clipboard = options.ClipboardService ?? _clipboard,
                Clock = options.Clock ?? new StopwatchUiClock(),
                WheelScrollSettings = options.WheelScrollSettingsService ?? WindowsWheelScrollSettingsService.Instance,
                TextInputService = (ITextInputService?)_inputBridge ?? _platformTextInput,
            });

            // 宿主后续接线只依赖桥契约：路由器是稳定实例，运行时与适配器共同持有它。
            _inputBridge?.AttachRuntime(_runtime);

            // 同帧候选入口：BuildFrame 在 Root.View.Emit 之后、命令快照之前调用，位于同一
            // TextureService.BeginFrame 保护内，不额外 BeginFrame。路由器在活动 TSF 桥上先复核身份。
            if (_candidateOverlay is not null)
            {
                _runtime.FrameOverlay = DrawCandidateOverlay;
            }

            _runtime.SetEnvironment(new EnvironmentValues
            {
                Theme = options.Theme,
                Font = options.DefaultFont,
            });
            _runtime.ExplicitClearColor = options.ClearColor;

            _renderer = new GlRenderer(_textures);
            _input = new OpenTkInputAdapter(_runtime, () => RequestExit(0), options.ExitOnEscape, options.ExitOnCloseShortcut, _inputBridge);

            // R10：延后输入的唤醒是**事件驱动**的单次请求，不是周期性轮询。
            AttachDeferredInputSignals();
            _context = new UiHostContext(_runtime, _dispatcher, _textures, GlWindowFactory.DescribeContext(), RequestExit)
            {
                PixelScale = 1.0,
            };

            HookWindowEvents();

            // 初始尺寸按逻辑像素解释：窗口创建后按实际内容缩放反推客户区尺寸（已正确时不改动）。
            EnsureInitialLogicalSize();
            HookContentScaleCallback();
            UpdateViewport(force: true);
        }
        catch
        {
            // 初始化失败：先释放需要上下文的 GL 资源，再销毁窗口。
            if (_runtime is not null)
            {
                _runtime.FrameOverlay = null;
            }

            _candidateOverlay?.Clear();
            _renderer?.Dispose();
            _runtime?.Dispose();
            _textures?.Dispose();
            _ownedTextService?.Dispose();
            _ownedImageService?.Dispose();
            // 窗口销毁前恢复 IME 子类化（窗口仍有效时可验证恢复）。
            _inputBridge?.Dispose();
            _window.Dispose();
            throw;
        }
    }

    /// <summary>宿主上下文（供应用与测试观察状态、请求退出）。</summary>
    internal UiHostContext Context => _context;

    /// <summary>宿主窗口；仅供内部与测试使用，应用不应直接操作其生命周期。</summary>
    internal NativeWindow Window => _window;

    /// <summary>Windows IME 桥（仅 Windows 且取到 HWND 时非 null）；验收工具经 InternalsVisibleTo 观察。</summary>
    internal WindowsImeBridge? ImeBridge => _ime;

    /// <summary>宿主使用的输入桥契约实例（与 <see cref="ImeBridge"/> 同一对象；非 Windows 时为 null）。</summary>
    internal IWindowsInputBridge? InputBridge => _inputBridge;

    /// <summary>输入适配器（验收用于读取有序交付计数）。</summary>
    internal OpenTkInputAdapter? InputAdapter => _input;

    /// <summary>稳定的 Windows 输入路由器（构造期创建；运行时与适配器共同持有）。</summary>
    internal WindowsTsf.WindowsTextInputRouter? Router => _router;

    /// <summary>当前活动的 TSF 桥（未建立时为 null）。</summary>
    internal WindowsTsf.TsfInputBridge? TsfBridge => _router?.TsfBridge;

    /// <summary>文本输入后端选择/回退的准确原因（脱敏，用于能力报告）。</summary>
    internal string TextInputBackendReason
        => _router?.BackendReason
            ?? _platformTextInput?.Capabilities.Reason
            ?? "默认 IMM 输入桥。";

    /// <summary>
    /// 非 Windows committed-only 能力的平台分级 Reason（TI-08）：按真实 GLFW 后端如实描述，
    /// committedText 恒 true（GLFW char 回调通道），不写成“无字符”。
    /// </summary>
    /// <param name="platform">
    /// 当前 OS 判定结果；生产由 <see cref="DescribeCurrentPlatformReason"/> 传入
    /// （macOS=Cocoa、Linux=真实 <see cref="GLFW.GetPlatform"/>、其余=Null），
    /// throwaway/测试可传入任意档位驱动同一映射。
    /// </param>
    internal static string DescribeCommittedOnlyReason(Platform platform) => platform switch
    {
        Platform.Cocoa =>
            "macOS：GLFW 的 NSTextInputClient 实现把 marked text 消费在视图内部，公共 API 无组合/候选暴露；已提交字符经 char 回调可用。",
        Platform.X11 =>
            "Linux/X11：GLFW 内部使用 XIM（预编辑在 GLFW 内消费）；已提交字符经 char 回调可用。",
        Platform.Wayland =>
            "Linux/Wayland：GLFW 3.4 Wayland 后端无 text-input 协议；普通字符经 xkb/char 回调可达，IME 协议级组合/候选缺失。",
        _ =>
            "当前平台：框架只提供已提交字符通道（GLFW char 回调）；组合/候选未接入，未判定本机原生 IME 可用性。",
    };

    /// <summary>
    /// 取当前平台的 committed-only Reason（TI-08）：macOS→Cocoa 档；Linux→真实
    /// <see cref="GLFW.GetPlatform"/>（GLFW 初始化后调用）；其余/查询失败→unknown 档。
    /// 本机 Windows 分支不经过此函数（Windows 由桥如实报告）。
    /// </summary>
    internal static string DescribeCurrentPlatformReason()
    {
        if (OperatingSystem.IsMacOS())
        {
            return DescribeCommittedOnlyReason(Platform.Cocoa);
        }

        if (OperatingSystem.IsLinux())
        {
            Platform platform;
            try
            {
                platform = GLFW.GetPlatform();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                platform = Platform.Null;
            }

            return DescribeCommittedOnlyReason(platform);
        }

        return DescribeCommittedOnlyReason(Platform.Null);
    }

    /// <summary>已渲染帧数。</summary>
    internal int RenderedFrameCount => _renderedFrameCount;

    /// <summary>由窗口尺寸/刷新回调内的即时重绘路径提交的帧数。</summary>
    internal int ImmediateRedrawCount => _immediateRedrawCount;

    /// <summary>渲染器；仅供内部与测试读取最近一帧的 framebuffer 尺寸与统计。</summary>
    internal GlRenderer Renderer => _renderer;

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

        // 启动回调返回后才允许在窗口尺寸/刷新回调里即时绘制：构造期间与 Start 之前的事件只更新视口状态，
        // 绘制交给正常循环，避免在服务未就绪或未请求首帧时提交内容。
        _immediateRedrawEnabled = true;
        try
        {
            while (!_exitRequested && _window.Exists)
            {
                // 1. 先非阻塞排空系统事件，保证输入在绘制之前生效。
                PumpEvents();


                // 1b. TI-07 P3a：在**任何后续工作之前**应用已捕获的原生事实。
                // COM 回调只捕获；真正的提交/选区/组合变更必须在这里、且排在下一批输入之前完成，
                // 否则会出现“旧版本被拒 / 文字进错输入框”的顺序倒置。
                DrainTsfSafePhase();

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

                // 5. 组件重建与布局。用户组件可能在 Build/布局中间接触发窗口尺寸/刷新事件，
                //    因此整个阶段标记为“正在处理工作”，窗口回调此时只能保留重绘请求。
                ProcessPendingWork();

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
                // R10：只有“本轮确实交付了”才立即继续（还有更多待交付）。
                // 若仍有延后项但当前不安全，**不**在这里周期性请求唤醒——
                // 改为由真实安全边界（ExitSafePhase）或队列转移单次唤醒（见 AttachDeferredInputSignals）。
                if (_deferredProgressThisIteration)
                {
                    continue;
                }

                if ((_runtime.NeedsDraw && CanRender()) || _runtime.HasPendingBuild || _dispatcher.HasPendingActions)
                {
                    continue;
                }

                // 8. 空闲：阻塞等待事件或定时唤醒。
                WaitForEvents(ComputeWaitTimeout());
            }

            return _exitCode;
        }
        finally
        {
            // 事件循环退出（正常或异常）后，窗口事件只保留状态，不再提交帧；
            // 宿主尚未 Dispose 的间隙也不能因窗口通知绘制。
            _immediateRedrawEnabled = false;

            // 解除延后输入信号，避免循环退出后仍被回调唤醒。
            DetachDeferredInputSignals();
        }
    }

    /// <summary>
    /// 挂接延后输入的**单次**唤醒信号（TI-07 P3a R10）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么不是轮询：</b>上一版在队列不空时每轮 <c>RequestWakeup(1ms)</c>，
    /// 那是持续的周期性轮询，违反“有界”要求。这里只在两种<b>真实状态转移</b>时请求一次唤醒：
    /// </para>
    /// <list type="bullet">
    /// <item><description>队列由空变非空（有新输入被延后）。</description></item>
    /// <item><description>安全阶段真正退出（<c>ExitSafePhase</c>，可能出现可交付机会）。</description></item>
    /// </list>
    /// <para>
    /// <b>必须订阅路由器而不是某个具体协调器：</b>TSF 接管后，真实 apply 退出发生在
    /// <b>TSF 桥自己的</b>协调器上。路由器会把本回调跟随活动协调器搬迁（建立/客户端替换/回退/释放），
    /// 因此这里订阅一次即覆盖默认 IMM 与真实 TSF 两条路径（R10 指出的断链）。
    /// </para>
    /// <para>
    /// 若届时仍不安全，就等下一次真实转移再唤醒——不忙等、不自投递、不持续重绘。
    /// </para>
    /// </remarks>
    private void AttachDeferredInputSignals()
    {
        if (_input is null)
        {
            return;
        }

        // 队列转移 → 单次唤醒。
        _input.DeferredInputQueued += OnDeferredInputSignal;
        _input.RequestWake = OnDeferredInputSignal;

        // 真实安全边界退出 → 单次唤醒（跟随活动协调器，只在确实有排队项时才需要）。
        _router?.AttachHostSafePhaseSignal(OnDeferredInputSignal);
    }

    /// <summary>单次唤醒：仅在确有排队输入时请求一次事件循环唤醒。</summary>
    private void OnDeferredInputSignal()
    {
        // 释放后（或已解绑）任何迟到通知都不得再触碰已释放宿主的调度器。
        if (_disposed)
        {
            return;
        }

        if (_input is { HasDeferredInput: true } || _router?.HasPendingSafeWork == true)
        {
            _dispatcher.RequestWakeup(TimeSpan.Zero);
        }
    }

    /// <summary>解除延后输入信号挂接（避免宿主释放后仍被回调）。</summary>
    private void DetachDeferredInputSignals()
    {
        if (_input is not null)
        {
            _input.DeferredInputQueued -= OnDeferredInputSignal;
            _input.RequestWake = null;
        }

        // 先解绑真实安全边界订阅，再让路由器释放真实 TSF 会话。
        _router?.DetachHostSafePhaseSignal();
    }

    /// <summary>
    /// TI-07 P3a：在安全阶段应用已捕获的 TSF 原生事实（未启用 TSF 时为空操作）。
    /// </summary>
    /// <remarks>
    /// <b>为什么在 pump 之后、其它工作之前：</b>一次消息泵可能连续产生
    /// “TSF 确认 → 普通字符 → 点击另一输入框”。已确认的事实必须先落地，
    /// 后续普通输入才能基于最新已提交文档与选区，而不是旧版本。
    /// <para>
    /// <b>不安全就延后：</b>锁/通知/重入期间 <see cref="WindowsTsf.TsfInputBridge.DrainSafePhase"/>
    /// 返回 false 并保留 pending，绝不越过未应用事实交付后续输入。
    /// </para>
    /// <para>
    /// <b>宿主工作中不应用：</b>正在重建/布局/生成帧时不进入安全阶段（避免递归写文档）。
    /// 此处与注入给桥的 guard 使用同一谓词。
    /// </para>
    /// </remarks>
    private void DrainTsfSafePhase()
    {
        if (_router is null)
        {
            return;
        }

        if (_processingWork || _frameInProgress)
        {
            // 宿主自身正在重建/布局/帧：此时进入安全阶段的唯一后果是被判为 InsideHostWork 而延后。
            // 不在这里请求唤醒（不吞掉真实队列转移的唤醒）：宿主工作结束时由
            // RequestDeferredInputRecoveryWake 明确补一次恢复机会。
            return;
        }

        // 1) 先应用已捕获的原生事实（真实 apply 状态由协调器/会话判定）。
        _router.DrainSafePhase();

        // 1a) 正常安全阶段的一次机会：重试那些原生解绑失败、仍被强根持有的消息钩子。
        //     只在本循环已有的正常机会调用（不周期轮询、不为此自唤醒）；
        //     返回值为本次真正完成的退役数，无待清理项时为 0。
        WindowsTsf.TsfMessageHook.RetryPendingRetirementsForCurrentThread();

        // 2) R10：安全恢复后**主动**按序交付已延后的输入。
        //    不依赖“下一次用户输入”，不递归 pump，不忙等：这是事件循环的正常阶段。
        _deferredProgressThisIteration = false;
        if (_input is { HasDeferredInput: true } adapter)
        {
            _deferredProgressThisIteration = adapter.DeliverDeferredAtSafePhase() > 0;
        }
    }

    /// <summary>
    /// 宿主工作结束时的一次恢复机会：若仍有延后输入，请求一次唤醒让事件循环重试安全阶段。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么需要：</b>宿主工作期间捕获的输入会被判为不安全而延后；若此刻没有任何新的真实边界，
    /// 队列会滞留到下一次用户输入。这里在<b>宿主工作结束</b>这个真实状态转移上请求一次唤醒。
    /// </para>
    /// <para>
    /// <b>为什么有界：</b>只在“工作结束且确实有延后输入”时请求一次；若届时仍不安全，
    /// 由下一次真实边界（或有界的一次重试）再唤醒，不周期轮询、不忙等、不自投递。
    /// </para>
    /// </remarks>
    private void RequestDeferredInputRecoveryWake()
    {
        if (_disposed || _frameInProgress || _processingWork)
        {
            return;
        }

        if (_input is { HasDeferredInput: true } || _router?.HasPendingSafeWork == true)
        {
            _dispatcher.RequestWakeup(TimeSpan.Zero);
        }
    }

    /// <summary>在“正在处理重建/布局”标记下执行 <see cref="UiRuntime.ProcessPendingWork"/>。</summary>
    private void ProcessPendingWork()
    {
        _processingWork = true;
        try
        {
            _runtime.ProcessPendingWork();
        }
        finally
        {
            _processingWork = false;
            RequestDeferredInputRecoveryWake();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        // 释放顺序要求：立即禁止窗口事件绘制路径，避免销毁窗口期间的重入提交帧。
        _immediateRedrawEnabled = false;
        try
        {
            // 候选 overlay 先于运行时/输入桥撤销：帧末入口立刻解绑，避免销毁期间再追加命令。
            if (_runtime is not null)
            {
                _runtime.FrameOverlay = null;
            }

            _candidateOverlay?.Clear();

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
            UnhookContentScaleCallback();
            // 先解绑真实安全边界/队列唤醒订阅，再释放输入桥（含真实 TSF 会话），
            // 保证释放后的迟到通知不会进入已释放宿主的调度器。
            DetachDeferredInputSignals();
            // 先于窗口销毁恢复 IME 子类化：WM_NCDESTROY 里的恢复路径随后成为幂等空操作。
            _inputBridge?.Dispose();
            // 最终解绑机会：会话退役时若原生解绑失败，钩子仍被强根持有；
            // 这里在所属线程再重试一次（不是轮询；无待清理项时为 0）。
            WindowsTsf.TsfMessageHook.RetryPendingRetirementsForCurrentThread();
            _window.Dispose();
        }
    }

    private void HookWindowEvents()
    {
        // 尺寸/移动/刷新回调：先按真实 GLFW 尺寸刷新视口、DPI 与输入换算，再在主线程即时绘制一帧。
        // Windows 的交互调整大小会进入窗口过程模态循环，外层 PollEvents/WaitEvents 不保证及时返回，
        // 因此新扩出的区域必须在回调返回前完成清屏与提交，不能只置“需要重绘”等待下一轮循环。
        _window.Resize += _ => OnWindowResized();
        _window.FramebufferResize += _ => OnWindowResized();
        _window.Move += _ => RunWindowEventSafely(
            () =>
            {
                UpdateViewport(force: false);
                // 窗口移动使系统候选/组合窗的屏幕位置失效：按缓存逻辑几何重新发布（R7）。
                _inputBridge?.NotifyGeometryInvalidated();
            },
            "窗口移动");
        _window.Refresh += OnWindowRefresh;
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

    /// <summary>窗口尺寸/ framebuffer 尺寸变化：刷新视口后立即尝试提交一帧。</summary>
    private void OnWindowResized()
    {
        if (_disposed)
        {
            return;
        }

        RunWindowEventSafely(
            () =>
            {
                UpdateViewport(force: false);
                _inputBridge?.NotifyGeometryInvalidated();
                TryRedrawImmediately();
            },
            "窗口尺寸");
    }

    /// <summary>窗口内容需要刷新（例如被遮挡后重新暴露）：刷新真实尺寸并立即提交一帧。</summary>
    private void OnWindowRefresh()
    {
        if (_disposed)
        {
            return;
        }

        RunWindowEventSafely(
            () =>
            {
                UpdateViewport(force: false);
                // 内容可能已被系统丢弃，即使尺寸没变也要重新提交一帧。
                _runtime.InvalidateRender();
                TryRedrawImmediately();
            },
            "窗口刷新");
    }

    /// <summary>执行窗口事件动作并把异常挡在 native 回调边界内：记录诊断后请求以错误码退出。</summary>
    private void RunWindowEventSafely(Action action, string source)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            HandleWindowEventFailure(source, ex);
        }
    }

    private void HandleWindowEventFailure(string source, Exception exception)
    {
        try
        {
            _runtime?.ReportDiagnostic($"{source}事件处理失败：{exception.GetType().Name}：{exception.Message}");
        }
        catch (Exception)
        {
            // 诊断上报本身失败时忽略：仍要保证异常不穿过 native 回调。
        }

        // 事件路径已不可信：请求带错误码退出，让 Run 返回而不是崩溃或静默继续。
        RequestExit(1);
    }

    /// <summary>
    /// 在当前窗口事件回调内立即提交一帧（如果确实需要绘制）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 与正常循环共用同一套渲染顺序：刷新视口 → 处理必要布局 → 按需绘制并交换缓冲；不排空调度队列、
    /// 不递归调用 <c>GLFW.PollEvents/WaitEvents</c>，因此不会在用户回调里执行任意投递动作。
    /// </para>
    /// <para>
    /// 重入保护：正在即时重绘时只记录后续请求，由本轮的下一遍补画；正常循环正在重建/布局或绘制时直接返回，
    /// 外层循环会依据新的 <c>NeedsDraw</c> 再绘一帧，因此不会在 Build 尚未返回时进入 <c>FrameRendered</c>。
    /// 构造/启动未就绪、最小化、零尺寸、退出或已销毁时只保留 <c>NeedsDraw</c>，恢复正常后由事件循环补画。
    /// </para>
    /// </remarks>
    private void TryRedrawImmediately()
    {
        if (_immediateRedrawActive)
        {
            _immediateRedrawPending = true;
            return;
        }

        if (_frameInProgress || _processingWork)
        {
            return;
        }

        if (!_immediateRedrawEnabled || !CanRender())
        {
            return;
        }

        _immediateRedrawActive = true;
        try
        {
            var passes = 0;
            do
            {
                _immediateRedrawPending = false;

                // 尺寸变化会触发布局失效；这里只处理组件重建与布局，不执行用户队列里的动作。
                ProcessPendingWork();
                if (!_runtime.NeedsDraw || !CanRender())
                {
                    break;
                }

                DrawFrame();
                _immediateRedrawCount++;
                passes++;
            }
            while (_immediateRedrawPending && passes < MaxImmediateRedrawPasses);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            HandleWindowEventFailure("即时重绘", ex);
        }
        finally
        {
            _immediateRedrawActive = false;
        }
    }

    /// <summary>挂接 GLFW 窗口内容缩放回调（OpenTK 4.9.4 的 NativeWindow 没有该事件的包装）。</summary>
    private unsafe void HookContentScaleCallback()
    {
        _contentScaleCallback = OnWindowContentScaleChanged;
        var previous = GLFW.SetWindowContentScaleCallback(_window.WindowPtr, _contentScaleCallback);
        _previousContentScaleCallback = previous == IntPtr.Zero
            ? null
            : Marshal.GetDelegateForFunctionPointer<GLFWCallbacks.WindowContentScaleCallback>(previous);
    }

    /// <summary>注销内容缩放回调并释放委托引用。</summary>
    private unsafe void UnhookContentScaleCallback()
    {
        if (_contentScaleCallback is null)
        {
            return;
        }

        try
        {
            GLFW.SetWindowContentScaleCallback(_window.WindowPtr, _previousContentScaleCallback);
        }
        catch (Exception)
        {
            // 窗口/GLFW 已不可用时忽略：注销失败不影响进程退出。
        }

        _contentScaleCallback = null;
        _previousContentScaleCallback = null;
    }

    /// <summary>
    /// 系统缩放变化或跨屏移动：链式调用原有回调，再按新内容缩放刷新视口，并保持当前逻辑窗口尺寸。
    /// </summary>
    private unsafe void OnWindowContentScaleChanged(Window* window, float xScale, float yScale)
    {
        _previousContentScaleCallback?.Invoke(window, xScale, yScale);
        RefreshForContentScaleChange((xScale, yScale));
    }

    /// <summary>
    /// 按新的内容缩放刷新视口，并把窗口客户区调整到仍覆盖原逻辑尺寸。
    /// </summary>
    /// <remarks>
    /// 顺序很重要：先记录旧的逻辑尺寸，再用新的 D/R 重新解释实际像素，最后按“旧逻辑尺寸 × 新 D / 新 R”
    /// 反推客户区。窗口管理器若已经自行按新 DPI 调整过客户区，反推结果与当前尺寸一致（±1 像素），
    /// 不会重复放大；全程只在事件路径处理，不做逐帧轮询。
    /// </remarks>
    private void RefreshForContentScaleChange((double X, double Y) contentScale)
    {
        var previousLogicalSize = _logicalSize;
        UpdateViewport(force: true, contentScale);
        if (!_isMinimized && previousLogicalSize.Width > 0 && previousLogicalSize.Height > 0)
        {
            ApplyLogicalClientSize(previousLogicalSize, contentScale);
        }
    }

    /// <summary>
    /// 测试与诊断用：按给定内容缩放模拟一次系统 DPI 变化，走与 GLFW 回调完全相同的刷新路径。
    /// </summary>
    /// <remarks>生产路径使用 GLFW 上报的真实内容缩放；本方法不读取也不修改系统显示设置。</remarks>
    internal void SimulateContentScaleChange(double contentScaleX, double contentScaleY)
        => RefreshForContentScaleChange((contentScaleX, contentScaleY));

    /// <summary>
    /// 测试与诊断用：重新执行一次“初始逻辑尺寸 → 客户区”的调整（与构造函数完全相同的路径）。
    /// </summary>
    /// <remarks>
    /// 平台忽略 <c>ScaleToMonitor</c> 或创建期间被窗口管理器钳制时会真正进入尺寸调整分支，
    /// 该分支随后会刷新视口与输入换算，因此调用前必须已完成服务组装（构造函数已保证顺序）。
    /// </remarks>
    internal void ApplyInitialLogicalSize() => EnsureInitialLogicalSize();

    /// <summary>按给定内容缩放调整窗口客户区，使 framebuffer 覆盖指定的逻辑尺寸。</summary>
    private void ApplyLogicalClientSize(Size logicalSize, (double X, double Y) contentScale)
    {
        var (client, framebuffer) = ReadActualWindowSizes();
        if (framebuffer.X <= 0 || framebuffer.Y <= 0)
        {
            return;
        }

        var metrics = ViewportMetrics.Create(
            client.X, client.Y, framebuffer.X, framebuffer.Y, contentScale.X, contentScale.Y);
        var (targetWidth, targetHeight) = metrics.WindowSizeForLogicalSize(logicalSize.Width, logicalSize.Height);
        if (Math.Abs(targetWidth - client.X) <= 1 && Math.Abs(targetHeight - client.Y) <= 1)
        {
            // 窗口管理器已经按新 DPI 调整过客户区：不重复放大。
            return;
        }

        _window.ClientSize = new global::OpenTK.Mathematics.Vector2i(targetWidth, targetHeight);

        // 平台不保证同步派发尺寸事件，这里补一次刷新（尺寸未变时 UpdateViewport 内部会直接返回）。
        UpdateViewport(force: false, contentScale);
    }

    private void OnMinimizedChanged(bool minimized)
    {
        _isMinimized = minimized;
        if (minimized)
        {
            // 最小化：显示表面为零矩形 + S_OK（GetScreenExt 不依赖文本布局），候选几何撤销。
            _router?.NotifySurfaceUnavailable();
            return;
        }

        // 恢复后需要重新布局与重绘（最小化期间的尺寸变化会被合并处理）。
        UpdateViewport(force: true);
        _inputBridge?.NotifyGeometryInvalidated();
    }

    /// <summary>
    /// 让初始客户区覆盖 <c>UiApplicationOptions.Width/Height</c> 指定的逻辑尺寸。
    /// </summary>
    /// <remarks>
    /// <c>ScaleToMonitor</c> 提示已让 Windows/X11 按内容缩放创建窗口，这里再读一次实际尺寸做反推：
    /// 若平台忽略提示或做了钳制，会补一次尺寸调整；已经正确时不做任何修改，因此不会二次放大。
    /// </remarks>
    private void EnsureInitialLogicalSize()
        => ApplyLogicalClientSize(new Size(_options.Width, _options.Height), GlWindowFactory.GetWindowContentScale(_window));

    /// <summary>按 GLFW 上报的当前内容缩放刷新视口（尺寸变化等事件路径）。</summary>
    private void UpdateViewport(bool force)
        => UpdateViewport(force, GlWindowFactory.GetWindowContentScale(_window));

    /// <summary>更新逻辑尺寸、framebuffer 尺寸与 DPI；变化时重新布局、刷新输入换算并重绘。</summary>
    private void UpdateViewport(bool force, (double X, double Y) contentScale)
    {
        // 回调内 OpenTK 的缓存尺寸可能还是旧值（事件顺序不保证），这里查询真实 GLFW 尺寸。
        var (client, framebuffer) = ReadActualWindowSizes();

        if (framebuffer.X <= 0 || framebuffer.Y <= 0 || _window.WindowState == WindowState.Minimized)
        {
            _isMinimized = true;
            _context.IsMinimized = true;
            return;
        }

        var metrics = ViewportMetrics.Create(
            client.X, client.Y, framebuffer.X, framebuffer.Y, contentScale.X, contentScale.Y);
        var logical = metrics.LogicalSize;
        var physical = metrics.FramebufferSize;
        var scale = metrics.PixelScale;

        _isMinimized = false;
        var scaleChanged = Math.Abs(scale - _pixelScale) > 0.0001;
        var changed = force
            || logical != _logicalSize
            || physical != _framebufferSize
            || scaleChanged;
        _logicalSize = logical;
        _framebufferSize = physical;
        _pixelScale = scale;
        _context.LogicalSize = logical;
        _context.FramebufferSize = physical;
        _context.PixelScale = scale;
        _context.IsMinimized = false;

        // 输入换算与视口同步：DPI 变化后缓存过的指针位置会按新比例重新解释；
        // IME 候选锚点走同一换算（逻辑几何 → 窗口客户区像素）。
        _input.UpdatePointerTransform(metrics.PointerScaleX, metrics.PointerScaleY);
        _inputBridge?.UpdatePointerTransform(metrics.PointerScaleX, metrics.PointerScaleY);

        if (!changed)
        {
            return;
        }

        // SetViewport 会触发布局失效，并在 DPI 变化时清空文本与纹理缓存。
        _runtime.SetViewport(logical, scale);
        _runtime.InvalidateRender();

        // DPI 变化使已换算的屏幕物理几何立即失效：撤销缓存与候选放置，下一安全布局重发。
        if (scaleChanged)
        {
            _inputBridge?.NotifyGeometryInvalidated();
        }
    }

    /// <summary>帧末候选追加：路由器在真实 TSF 桥上复核缓存身份，失效立即清几何。</summary>
    private void DrawCandidateOverlay(DrawContext context)
    {
        if (_router is not null)
        {
            _router.DrawCandidates(context);
            return;
        }

        _candidateOverlay?.Draw(context);
    }

    private void DrawFrame()
    {
        // _frameInProgress 覆盖用户 FrameRendered 回调：回调内触发尺寸/刷新事件时不得递归进入绘制。
        _frameInProgress = true;
        try
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
        finally
        {
            _frameInProgress = false;
        }
    }

    /// <summary>
    /// 读取窗口真实内容区与 framebuffer 尺寸。
    /// </summary>
    /// <remarks>
    /// 优先查询 GLFW：尺寸/刷新回调内 OpenTK 缓存的 <c>ClientSize/FramebufferSize</c> 不保证已经更新，
    /// 若沿用旧 framebuffer 就会在新区域上提交旧尺寸的帧。查询失败（窗口销毁中或 GLFW 不可用）时回退到
    /// OpenTK 缓存值。最小化窗口的 framebuffer 为 0，由调用方按零尺寸处理。
    /// </remarks>
    private unsafe (global::OpenTK.Mathematics.Vector2i Window, global::OpenTK.Mathematics.Vector2i Framebuffer) ReadActualWindowSizes()
    {
        var window = _window.WindowPtr;
        if (window != null)
        {
            try
            {
                GLFW.GetWindowSize(window, out var windowWidth, out var windowHeight);
                GLFW.GetFramebufferSize(window, out var framebufferWidth, out var framebufferHeight);
                if (windowWidth > 0 && windowHeight > 0 && framebufferWidth >= 0 && framebufferHeight >= 0)
                {
                    return (
                        new global::OpenTK.Mathematics.Vector2i(windowWidth, windowHeight),
                        new global::OpenTK.Mathematics.Vector2i(framebufferWidth, framebufferHeight));
                }
            }
            catch (Exception)
            {
                // 窗口正在销毁或 GLFW 不可用：回退到缓存值。
            }
        }

        return (_window.ClientSize, _window.FramebufferSize);
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
}
