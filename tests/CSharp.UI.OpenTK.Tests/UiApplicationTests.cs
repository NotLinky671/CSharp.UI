using CSharp.UI.Testing;
using OpenTK.Windowing.Common;
using Xunit;

namespace CSharp.UI.OpenTK.Tests;

/// <summary>
/// 宿主集成测试：真实创建窗口与 GL 上下文，验证事件循环、后台投递、空闲不重绘、尺寸/最小化与资源回收。
/// </summary>
/// <remarks>
/// 测试在 xunit 的 worker 线程运行，因此先放开 GLFW 的主线程限制（生产宿主仍在主线程运行）。
/// 除最小化用例外，窗口默认隐藏，避免测试期间闪烁。
/// </remarks>
[Collection(GraphicsCollection.Name)]
[Trait("Category", "Graphics")]
public sealed class UiApplicationTests
{
    private readonly GlContextFixture _fixture;

    /// <summary>注入共享上下文夹具（保证 GLFW 已按测试需要初始化）。</summary>
    public UiApplicationTests(GlContextFixture fixture) => _fixture = fixture;

    [Fact]
    public void 宿主创建窗口运行循环并按请求退出()
    {
        Assert.False(string.IsNullOrWhiteSpace(_fixture.ContextDescription));
        GlOffscreenSurface.AllowNonMainThreadCreation();
        var before = Snapshot();
        var postedOnUiThread = false;
        var framesAtIdleStart = -1;
        var framesAfterIdle = -1;
        var options = new UiApplicationOptions
        {
            Title = "CSharp.UI 宿主测试",
            Width = 120,
            Height = 90,
            VSync = false,
            StartVisible = false,
            Started = context =>
            {
                Assert.True(context.Dispatcher.IsOnUiThread);
                Assert.False(string.IsNullOrWhiteSpace(context.GlContextDescription));

                // 窗口系统可能对最小窗口宽度做钳制（Windows 上可放宽到约 180px），因此只断言下界。
                Assert.True(
                    context.LogicalSize.Width >= 120 && context.LogicalSize.Height >= 90,
                    $"逻辑尺寸应至少为请求的 120×90，实际 {context.LogicalSize}");
                Assert.True(context.PixelScale > 0);

                StartBackgroundDriver(() =>
                {
                    // 后台线程投递：必须在 UI 线程执行。
                    context.Dispatcher.Post(() => postedOnUiThread = context.Dispatcher.IsOnUiThread);

                    // 等待窗口创建后的尺寸/首帧事件稳定，再记录基线帧数。
                    Thread.Sleep(200);
                    context.Dispatcher.Post(() => framesAtIdleStart = context.RenderedFrameCount);

                    // 静置 300ms：界面静止时不应持续重绘。
                    Thread.Sleep(300);
                    context.Dispatcher.Post(() =>
                    {
                        framesAfterIdle = context.RenderedFrameCount;
                        context.RequestExit(0);
                    });
                });
            },
        };

        var result = UiApplication.Run(new Rectangle(Color.Parse("#2563EB")), options);

        Assert.Equal(0, result);
        Assert.True(postedOnUiThread, "后台投递的动作必须在 UI 线程执行。");
        Assert.True(framesAtIdleStart >= 1, "启动后应至少渲染一帧。");
        Assert.Equal(framesAtIdleStart, framesAfterIdle);
        Assert.False(UiApplication.IsRunning);
        AssertResourcesReleased(before);
    }

    [Fact]
    public void 窗口尺寸变化更新逻辑尺寸与framebuffer()
    {
        GlOffscreenSurface.AllowNonMainThreadCreation();
        var logical = Size.Zero;
        var framebuffer = Size.Zero;
        var scale = 0.0;
        var resizeObserved = false;

        var result = UiApplication.Run(new Rectangle(Color.Red), new UiApplicationOptions
        {
            Title = "CSharp.UI 尺寸测试",
            Width = 100,
            Height = 80,
            VSync = false,
            StartVisible = false,
            Started = context =>
            {
                var host = UiApplication.CurrentHost;
                Assert.NotNull(host);
                StartBackgroundDriver(() =>
                {
                    context.Dispatcher.Post(() => host!.Window.ClientSize = new global::OpenTK.Mathematics.Vector2i(200, 150));
                    Thread.Sleep(300);
                    context.Dispatcher.Post(() =>
                    {
                        logical = context.LogicalSize;
                        framebuffer = context.FramebufferSize;
                        scale = context.PixelScale;
                        resizeObserved = true;
                    });
                    Thread.Sleep(100);
                    context.Dispatcher.Post(() => context.RequestExit(0));
                });
            },
        });

        Assert.Equal(0, result);
        Assert.True(resizeObserved, "尺寸变化后应能在 UI 线程读取到新的视口状态。");
        Assert.Equal(new Size(200, 150), logical);
        Assert.True(
            Math.Abs((logical.Width * scale) - framebuffer.Width) < 0.5,
            $"framebuffer 宽度应与逻辑宽度 × DPI 一致：逻辑 {logical}、缩放 {scale}、framebuffer {framebuffer}");
        Assert.True(framebuffer.Width >= 200, $"framebuffer 应随 DPI 放大，实际 {framebuffer}");
    }

    [Fact]
    public void 最小化时跳过渲染恢复后重绘()
    {
        GlOffscreenSurface.AllowNonMainThreadCreation();
        var framesWhenMinimized = -1;
        var framesAfterIdle = -1;
        var framesAfterRestore = -1;
        var minimizedObserved = false;

        var result = UiApplication.Run(new Rectangle(Color.Red), new UiApplicationOptions
        {
            Title = "CSharp.UI 最小化测试",
            Width = 160,
            Height = 120,
            VSync = false,
            // 可见窗口才能可靠地最小化/恢复。
            StartVisible = true,
            Started = context =>
            {
                var host = UiApplication.CurrentHost;
                Assert.NotNull(host);
                StartBackgroundDriver(() =>
                {
                    context.Dispatcher.Post(() => host!.Window.WindowState = WindowState.Minimized);
                    Thread.Sleep(250);
                    context.Dispatcher.Post(() =>
                    {
                        minimizedObserved = context.IsMinimized;
                        framesWhenMinimized = context.RenderedFrameCount;
                    });

                    Thread.Sleep(250);
                    context.Dispatcher.Post(() =>
                    {
                        framesAfterIdle = context.RenderedFrameCount;
                        host!.Window.WindowState = WindowState.Normal;
                    });

                    Thread.Sleep(250);
                    context.Dispatcher.Post(() =>
                    {
                        framesAfterRestore = context.RenderedFrameCount;
                        context.RequestExit(0);
                    });
                });
            },
        });

        Assert.Equal(0, result);
        Assert.True(minimizedObserved, "宿主应识别窗口已最小化。");
        Assert.Equal(framesWhenMinimized, framesAfterIdle);
        Assert.True(framesAfterRestore > framesAfterIdle, $"恢复后应重绘（最小化期间 {framesAfterIdle}，恢复后 {framesAfterRestore}）。");
    }

    [Fact]
    public void 重复启动宿主会抛出且不影响退出码()
    {
        GlOffscreenSurface.AllowNonMainThreadCreation();
        InvalidOperationException? captured = null;

        var result = UiApplication.Run(new Rectangle(Color.Red), new UiApplicationOptions
        {
            Width = 60,
            Height = 60,
            VSync = false,
            StartVisible = false,
            Started = context =>
            {
                captured = Assert.Throws<InvalidOperationException>(() => UiApplication.Run(new Rectangle(Color.Blue)));
                context.RequestExit(3);
            },
        });

        Assert.Equal(3, result);
        Assert.NotNull(captured);
        Assert.False(UiApplication.IsRunning);
    }

    [Fact]
    public void 未注入服务时宿主默认组装Skia实现并在退出时释放()
    {
        GlOffscreenSurface.AllowNonMainThreadCreation();
        ITextService? capturedText = null;
        IImageService? capturedImage = null;

        var result = UiApplication.Run(new Rectangle(Color.Red), new UiApplicationOptions
        {
            Width = 60,
            Height = 60,
            VSync = false,
            StartVisible = false,
            Started = context =>
            {
                capturedText = context.Runtime.Services.TextService;
                capturedImage = context.Runtime.Services.ImageService;

                // 按类型名断言宿主默认组装结果（不直接引用实现类型，避免测试与实现程序集强耦合）。
                Assert.Equal("SkiaTextService", capturedText?.GetType().Name);
                Assert.Equal("CSharp.UI.Skia", capturedText?.GetType().Namespace);
                Assert.Equal("SkiaImageService", capturedImage?.GetType().Name);
                Assert.Equal("CSharp.UI.Skia", capturedImage?.GetType().Namespace);
                context.RequestExit(0);
            },
        });

        Assert.Equal(0, result);
        Assert.NotNull(capturedText);
        Assert.NotNull(capturedImage);

        // 宿主创建的服务在退出时由宿主释放：释放后调用服务方法会抛出。
        Assert.Throws<ObjectDisposedException>(() => capturedText.Layout(new TextLayoutRequest("已释放")));
        Assert.Throws<ObjectDisposedException>(() => capturedImage.Load("missing.png"));
    }

    [Fact]
    public void 应用注入的服务不被宿主释放()
    {
        GlOffscreenSurface.AllowNonMainThreadCreation();
        var tracking = new TrackingTextService();

        var result = UiApplication.Run(new Rectangle(Color.Red), new UiApplicationOptions
        {
            Width = 60,
            Height = 60,
            VSync = false,
            StartVisible = false,
            TextService = tracking,
            Started = context =>
            {
                Assert.Same(tracking, context.Runtime.Services.TextService);
                context.RequestExit(0);
            },
        });

        Assert.Equal(0, result);
        Assert.False(tracking.IsDisposed);
    }

    [Fact]
    public void 聚焦输入框后光标闪烁驱动定时重绘()
    {
        GlOffscreenSurface.AllowNonMainThreadCreation();
        var framesAtStart = -1;
        var framesAtEnd = -1;
        var wakeupObserved = false;

        var result = UiApplication.Run(new TextField(Binding<string>.Constant("abc")), new UiApplicationOptions
        {
            Title = "CSharp.UI 闪烁测试",
            Width = 200,
            Height = 40,
            VSync = false,
            StartVisible = false,
            // 使用测试替身文本服务，避免图形测试依赖字体渲染结果。
            TextService = new StubTextService(),
            Started = context =>
            {
                // 先完成一次布局，再走与真实输入相同的路径点击输入框获得焦点。
                context.Runtime.ProcessPendingWork();
                context.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Down, new Point(10, 20)));
                Assert.NotNull(context.Runtime.FocusedNode);
                Assert.NotNull(context.Runtime.GetNextWakeup());

                StartBackgroundDriver(() =>
                {
                    Thread.Sleep(300);
                    context.Dispatcher.Post(() => framesAtStart = context.RenderedFrameCount);

                    // 覆盖 1—2 个闪烁周期（TextField 默认半周期 530ms），期间没有任何输入事件。
                    Thread.Sleep(1700);
                    context.Dispatcher.Post(() =>
                    {
                        framesAtEnd = context.RenderedFrameCount;
                        wakeupObserved = context.Runtime.GetNextWakeup() is not null;
                        context.RequestExit(0);
                    });
                });
            },
        });

        Assert.Equal(0, result);
        Assert.True(wakeupObserved, "聚焦输入框期间应持续声明定时唤醒。");
        Assert.True(framesAtStart >= 1, "启动后应至少渲染一帧。");
        Assert.True(framesAtEnd > framesAtStart, $"光标闪烁应驱动定时重绘：{framesAtStart} → {framesAtEnd}");
    }

    [Fact]
    public void GLFW剪贴板可读写并恢复原内容()
    {
        GlOffscreenSurface.AllowNonMainThreadCreation();
        var roundTrip = string.Empty;
        var original = string.Empty;
        var originalReadable = false;
        var attempts = 0;
        var writeFailed = false;
        Exception? failure = null;
        const string payload = "CSharp.UI 剪贴板往返测试";

        var result = UiApplication.Run(new Rectangle(Color.Red), new UiApplicationOptions
        {
            Width = 60,
            Height = 60,
            VSync = false,
            StartVisible = false,
            Started = context =>
            {
                var host = UiApplication.CurrentHost;
                Assert.NotNull(host);
                var clipboard = new GlfwClipboardService(host!.Window);

                // 先确认读路径本身可用：原内容为空也可能是“本来就没有内容”，因此单独记录“是否读到非空”。
                original = clipboard.GetText() ?? string.Empty;
                originalReadable = original.Length > 0;

                try
                {
                    clipboard.SetText(payload);

                    // 剪贴板是进程间共享资源：其他进程可能短暂占用（Windows 的 OpenClipboard 会失败并返回空），
                    // 因此按“读回目标内容”重试若干次，而不是只读一次。
                    roundTrip = ReadClipboardUntil(clipboard, payload, out attempts);
                }
                catch (Exception ex)
                {
                    failure = ex;
                    writeFailed = true;
                }
                finally
                {
                    // 尽力恢复原有文本内容。
                    clipboard.SetText(original);
                }

                context.RequestExit(0);
            },
        });

        Assert.Equal(0, result);
        Assert.Null(failure);
        Assert.True(
            roundTrip == payload,
            $"GLFW 剪贴板往返失败：写入 \"{payload}\"，读回 \"{roundTrip}\"（重试 {attempts} 次）。" +
            $"写入{(writeFailed ? "抛出异常" : "未抛出异常")}；写入前读到的原内容长度 {original.Length}" +
            $"（{(originalReadable ? "读路径可用" : "写入前就读不到非空内容")}）。" +
            $"环境：{Environment.OSVersion} / {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}。" +
            "若该环境禁止访问系统剪贴板（沙箱、无交互会话），本失败属于环境限制；" +
            "请在允许访问剪贴板的环境复测，不要把它当作通过。");
    }

    /// <summary>读取剪贴板直到得到期望内容或重试耗尽（返回最后一次读到的内容）。</summary>
    private static string ReadClipboardUntil(GlfwClipboardService clipboard, string expected, out int attempts, int maxAttempts = 20)
    {
        var value = string.Empty;
        attempts = 0;
        for (var i = 1; i <= maxAttempts; i++)
        {
            attempts = i;
            value = clipboard.GetText() ?? string.Empty;
            if (value == expected)
            {
                return value;
            }

            Thread.Sleep(15);
        }

        return value;
    }

    private static void StartBackgroundDriver(Action driver)
    {
        var thread = new Thread(() => driver()) { IsBackground = true, Name = "CSharp.UI 宿主测试驱动" };
        thread.Start();
    }

    private static Dictionary<GlResourceKind, long> Snapshot()
        => Enum.GetValues<GlResourceKind>().ToDictionary(kind => kind, GlResourceCounters.GetLiveCount);

    private static void AssertResourcesReleased(Dictionary<GlResourceKind, long> before)
    {
        foreach (var kind in Enum.GetValues<GlResourceKind>())
        {
            var live = GlResourceCounters.GetLiveCount(kind);
            Assert.True(
                live == before[kind],
                $"宿主退出后 {kind} 存活数量应回到 {before[kind]}，实际 {live}（{GlResourceCounters.Describe()}）");
        }
    }

    /// <summary>记录是否被释放的文本服务替身，用于验证服务所有权（应用注入的服务由应用释放）。</summary>
    private sealed class TrackingTextService : ITextService, IDisposable
    {
        private readonly StubTextService _inner = new();

        internal bool IsDisposed { get; private set; }

        internal int ClearCacheCalls { get; private set; }

        public TextLayoutResult Layout(TextLayoutRequest request) => _inner.Layout(request);

        public Size Measure(TextLayoutRequest request) => _inner.Measure(request);

        public FontMetrics GetFontMetrics(FontDescription font) => _inner.GetFontMetrics(font);

        public PixelBuffer Rasterize(TextLayoutResult layout) => _inner.Rasterize(layout);

        public int CachedLayoutCount => _inner.CachedLayoutCount;

        public int CachedRasterCount => _inner.CachedRasterCount;

        public int LayoutCacheCapacity
        {
            get => _inner.LayoutCacheCapacity;
            set => _inner.LayoutCacheCapacity = value;
        }

        public void ClearCaches()
        {
            ClearCacheCalls++;
            _inner.ClearCaches();
        }

        public void Dispose() => IsDisposed = true;
    }
}
