using CSharp.UI.Testing;
using global::OpenTK.Graphics.OpenGL4;
using global::OpenTK.Mathematics;
using global::OpenTK.Windowing.Desktop;
using global::OpenTK.Windowing.GraphicsLibraryFramework;
using System.Reflection;
using Xunit;

namespace CSharp.UI.OpenTK.Tests;

/// <summary>
/// R18 回归：交互调整大小/刷新期间必须在窗口回调返回前按<b>真实</b> framebuffer 提交一帧，
/// 覆盖放大、缩小、连续尺寸事件、DPI、嵌套裁剪、重入与生命周期保护。
/// </summary>
/// <remarks>
/// 这些测试使用真实 OpenTkHost、真实 GL 上下文与隐藏窗口。程序化 resize 会同步派发 OpenTK 的
/// Resize/FramebufferResize 事件，再由反射触发 <c>NativeWindow.OnRefresh</c>，与 Codex 复现
/// (<c>artifacts/resize-review/Program.cs</c>) 的路径一致；<b>不代表</b>已人工验证 Windows 鼠标拖拽模态循环。
/// </remarks>
[Collection(GraphicsCollection.Name)]
[Trait("Category", "Graphics")]
public sealed class WindowResizeRedrawTests
{
    private static readonly MethodInfo OnRefreshMethod = typeof(NativeWindow).GetMethod(
        "OnRefresh",
        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
        ?? throw new InvalidOperationException("找不到 NativeWindow.OnRefresh。");

    private readonly GlContextFixture _fixture;

    /// <summary>注入共享上下文夹具。</summary>
    public WindowResizeRedrawTests(GlContextFixture fixture) => _fixture = fixture;

    [Fact]
    public void 尺寸与刷新回调返回前按真实framebuffer提交新帧()
    {
        GlOffscreenSurface.AllowNonMainThreadCreation();
        var framesAfterResize = -1;
        var framesAfterRefresh = -1;
        var framesBefore = -1;
        var immediateAfterRefresh = -1;
        var rendererFramebuffer = Size.Zero;
        var realFramebuffer = Vector2i.Zero;
        var contextFramebuffer = Size.Zero;
        var contextLogical = Size.Zero;
        var pixelScale = 0.0;
        var contentScale = (X: 0f, Y: 0f);
        var needsDraw = true;
        var clearColor = Color.Black;
        var cornerColor = Color.Black;
        var originColor = Color.Black;
        var observed = false;

        var result = UiApplication.Run(
            SmallSquare(),
            HostOptions("R18 尺寸即时重绘", context =>
            {
                if (context.RenderedFrameCount != 1)
                {
                    return;
                }

                context.Dispatcher.Post(() =>
                {
                    try
                    {
                        var host = UiApplication.CurrentHost!;
                        framesBefore = context.RenderedFrameCount;

                        // 程序化放大客户区：OpenTK 会同步派发 Resize/FramebufferResize。
                        host.Window.ClientSize = new Vector2i(
                            host.Window.ClientSize.X + 240,
                            host.Window.ClientSize.Y + 160);
                        framesAfterResize = context.RenderedFrameCount;

                        // 再显式触发刷新回调（等价于 WM_PAINT → GLFW damage 通知）。
                        InvokeRefresh(host.Window);
                        framesAfterRefresh = context.RenderedFrameCount;
                        immediateAfterRefresh = host.ImmediateRedrawCount;

                        realFramebuffer = QueryFramebufferSize(host);
                        rendererFramebuffer = host.Renderer.FramebufferSize;
                        contextFramebuffer = context.FramebufferSize;
                        contextLogical = context.LogicalSize;
                        pixelScale = context.PixelScale;
                        contentScale = GlWindowFactory.GetWindowContentScale(host.Window);
                        needsDraw = context.Runtime.NeedsDraw;
                        clearColor = context.Runtime.ClearColor;

                        // GL 坐标原点在左下角：右下角新扩出的区域应为清屏色，左上角的小方块仍为红色。
                        originColor = ReadFrontPixel(host, 4, realFramebuffer.Y - 4);
                        cornerColor = ReadFrontPixel(host, realFramebuffer.X - 3, 3);
                        observed = true;
                    }
                    finally
                    {
                        context.RequestExit(0);
                    }
                });
            }));

        Assert.Equal(0, result);
        Assert.True(observed, "未能在首帧后执行尺寸/刷新验证。");

        var diagnostics =
            $"帧 {framesBefore}→(resize){framesAfterResize}→(refresh){framesAfterRefresh}，" +
            $"真实 framebuffer {realFramebuffer}，渲染器 {rendererFramebuffer}，上下文 {contextFramebuffer}，" +
            $"逻辑 {contextLogical}，DPI {pixelScale}/{contentScale.X}，清屏色 {clearColor}，右下 {cornerColor}";

        // 尺寸回调与刷新回调各提交了一帧；刷新回调返回时新帧已经可见。
        Assert.True(framesAfterResize > framesBefore, $"尺寸回调内必须立即提交一帧：{diagnostics}");
        Assert.True(framesAfterRefresh > framesAfterResize, $"刷新回调内必须立即提交一帧：{diagnostics}");
        Assert.True(immediateAfterRefresh >= 2, $"即时重绘计数应至少为 2：{diagnostics}");

        // framebuffer 已经真的变大，且渲染器/运行时/GLFW 三者一致。
        Assert.True(realFramebuffer.X > 450, $"窗口应扩宽到超过初始 framebuffer：{diagnostics}");
        Assert.Equal(new Size(realFramebuffer.X, realFramebuffer.Y), rendererFramebuffer);
        Assert.Equal(new Size(realFramebuffer.X, realFramebuffer.Y), contextFramebuffer);
        Assert.Equal(contextFramebuffer, GlCoordinateMath.ToFramebufferSize(contextLogical, pixelScale));
        if (contentScale.X > 0)
        {
            Assert.True(
                Math.Abs(pixelScale - contentScale.X) < 0.01,
                $"DPI 应等于 GLFW 内容缩放：{diagnostics}");
        }

        Assert.False(needsDraw, "即时绘制后不应仍处于需要重绘状态。");

        // 新扩出的右下区域是清屏色（不是黑带），左上角仍是内容。
        AssertColor.Equal(clearColor, cornerColor);
        AssertColor.Equal(Color.Red, originColor);
    }

    [Fact]
    public void 连续放大缩小每次回调都按新尺寸绘制()
    {
        GlOffscreenSurface.AllowNonMainThreadCreation();
        var steps = new List<string>();
        var mismatches = new List<string>();
        var cornerMismatches = new List<string>();
        var observed = false;

        var result = UiApplication.Run(
            new Rectangle(Color.Red)
                .Frame(40, 40, Alignment.Start)
                .Clip()
                .Padding(6)
                .Clip(),
            HostOptions("R18 连续尺寸", context =>
            {
                if (context.RenderedFrameCount != 1)
                {
                    return;
                }

                context.Dispatcher.Post(() =>
                {
                    try
                    {
                        var host = UiApplication.CurrentHost!;
                        var clearColor = context.Runtime.ClearColor;
                        var initial = new Vector2i(host.Window.ClientSize.X, host.Window.ClientSize.Y);

                        // 放大 → 继续放大 → 缩小 → 恢复，每一步只在回调内完成。
                        var targets = new[]
                        {
                            new Vector2i(initial.X + 120, initial.Y + 90),
                            new Vector2i(initial.X + 260, initial.Y + 200),
                            new Vector2i(initial.X + 40, initial.Y + 30),
                            initial,
                        };

                        foreach (var target in targets)
                        {
                            var before = context.RenderedFrameCount;
                            host.Window.ClientSize = target;
                            InvokeRefresh(host.Window);

                            var real = QueryFramebufferSize(host);
                            var renderer = host.Renderer.FramebufferSize;
                            var frames = context.RenderedFrameCount;
                            steps.Add($"{target}=>fb{real},renderer{renderer},frames{before}->{frames}");

                            if (renderer != new Size(real.X, real.Y) || frames <= before)
                            {
                                mismatches.Add(steps[^1]);
                            }

                            var corner = ReadFrontPixel(host, real.X - 3, 3);
                            if (!ColorsClose(clearColor, corner))
                            {
                                cornerMismatches.Add($"{steps[^1]}: 右下 {corner} ≠ 清屏色 {clearColor}");
                            }
                        }

                        observed = true;
                    }
                    finally
                    {
                        context.RequestExit(0);
                    }
                });
            }));

        Assert.Equal(0, result);
        Assert.True(observed, "未能在首帧后执行连续尺寸验证。");
        Assert.True(mismatches.Count == 0, $"以下步骤未按新尺寸绘制：{string.Join("；", mismatches)}");
        Assert.True(
            cornerMismatches.Count == 0,
            $"以下步骤的右下新区域不是清屏色：{string.Join("；", cornerMismatches)}");
        Assert.Equal(4, steps.Count);
    }

    [Fact]
    public void FrameRendered回调内触发的尺寸事件不递归且后续重绘不丢失()
    {
        GlOffscreenSurface.AllowNonMainThreadCreation();
        var framesAfterReentrantResize = -1;
        var immediateRedrawCount = -1;
        var resizeObserved = false;

        var result = UiApplication.Run(
            SmallSquare(),
            HostOptions("R18 回调重入", context =>
            {
                var host = UiApplication.CurrentHost!;
                if (context.RenderedFrameCount == 1)
                {
                    // 在用户 FrameRendered 回调（绘制尚未退出）内触发尺寸事件：不得递归绘制。
                    host.Window.ClientSize = new Vector2i(
                        host.Window.ClientSize.X + 60,
                        host.Window.ClientSize.Y + 40);
                    resizeObserved = true;
                }
                else if (context.RenderedFrameCount >= 2 && framesAfterReentrantResize < 0)
                {
                    framesAfterReentrantResize = context.RenderedFrameCount;
                    immediateRedrawCount = host.ImmediateRedrawCount;
                    context.RequestExit(0);
                }
            }));

        Assert.Equal(0, result);
        Assert.True(resizeObserved, "应在首帧回调内触发尺寸事件。");
        Assert.True(
            framesAfterReentrantResize >= 2,
            $"回调内触发的尺寸变化必须保留后续重绘，实际帧数 {framesAfterReentrantResize}。");
        Assert.Equal(0, immediateRedrawCount);
    }

    [Fact]
    public void 已请求退出后刷新回调不再提交帧()
    {
        GlOffscreenSurface.AllowNonMainThreadCreation();
        var framesBeforeExit = -1;
        var framesAfterRefresh = -1;
        var exitObserved = false;

        var result = UiApplication.Run(
            SmallSquare(),
            HostOptions("R18 退出保护", context =>
            {
                if (context.RenderedFrameCount != 1)
                {
                    return;
                }

                context.Dispatcher.Post(() =>
                {
                    var host = UiApplication.CurrentHost!;
                    framesBeforeExit = context.RenderedFrameCount;
                    context.RequestExit(0);
                    InvokeRefresh(host.Window);
                    framesAfterRefresh = context.RenderedFrameCount;
                    exitObserved = true;
                });
            }));

        Assert.Equal(0, result);
        Assert.True(exitObserved, "应在退出请求后执行刷新验证。");
        Assert.Equal(framesBeforeExit, framesAfterRefresh);
    }

    [Fact]
    public void 启动前窗口事件只保留重绘不提交帧()
    {
        GlOffscreenSurface.AllowNonMainThreadCreation();
        using var host = new OpenTkHost(
            SmallSquare(),
            new UiApplicationOptions
            {
                Title = "R18 启动前保护",
                Width = 200,
                Height = 150,
                VSync = false,
                StartVisible = false,
                TextService = new StubTextService(),
            });

        host.Window.MakeCurrent();
        var framesBefore = host.RenderedFrameCount;

        // 构造完成但尚未进入 Run：尺寸/刷新事件只能更新状态，不能提交帧。
        host.Window.ClientSize = new Vector2i(
            host.Window.ClientSize.X + 90,
            host.Window.ClientSize.Y + 70);
        InvokeRefresh(host.Window);

        Assert.Equal(framesBefore, host.RenderedFrameCount);
        Assert.Equal(0, host.ImmediateRedrawCount);
        Assert.True(host.Context.Runtime.NeedsDraw, "启动前的重绘请求必须保留，由正常循环补画。");
    }

    [Fact]
    public void 最小化期间刷新回调不提交帧恢复后重绘()
    {
        GlOffscreenSurface.AllowNonMainThreadCreation();
        var framesWhileMinimized = -1;
        var framesAfterMinimizedRefresh = -1;
        var framesAfterRestore = -1;
        var minimizedObserved = false;
        var driverStarted = false;

        var result = UiApplication.Run(
            SmallSquare(),
            new UiApplicationOptions
            {
                Title = "R18 最小化保护",
                Width = 200,
                Height = 150,
                VSync = false,
                // 原生最小化是窗口管理器能力：可见窗口才有机会生效。无窗口管理器的环境（例如裸 xvfb-run）
                // 无法进行原生最小化验证，用例会动态跳过而不是伪装通过。
                StartVisible = true,
                TextService = new StubTextService(),
                FrameRendered = context =>
                {
                    // 首帧之后再启动驱动线程，消除“最小化早于首帧”的调度竞态。
                    if (context.RenderedFrameCount != 1 || driverStarted)
                    {
                        return;
                    }

                    driverStarted = true;
                    var host = UiApplication.CurrentHost!;
                    StartBackgroundDriver(() =>
                    {
                        context.Dispatcher.Post(() => host.Window.WindowState = global::OpenTK.Windowing.Common.WindowState.Minimized);
                        Thread.Sleep(300);
                        context.Dispatcher.Post(() =>
                        {
                            minimizedObserved = context.IsMinimized;
                            framesWhileMinimized = context.RenderedFrameCount;
                            InvokeRefresh(host.Window);
                            framesAfterMinimizedRefresh = context.RenderedFrameCount;
                        });

                        Thread.Sleep(300);
                        context.Dispatcher.Post(() => host.Window.WindowState = global::OpenTK.Windowing.Common.WindowState.Normal);

                        Thread.Sleep(300);
                        context.Dispatcher.Post(() =>
                        {
                            framesAfterRestore = context.RenderedFrameCount;
                            context.RequestExit(0);
                        });
                    });
                },
            });

        Assert.Equal(0, result);
        Assert.True(framesWhileMinimized >= 1, "最小化前应至少渲染一帧。");
        if (!minimizedObserved)
        {
            Assert.Skip("当前环境未实际进入最小化（没有可用的窗口管理器），跳过原生最小化验证。");
        }

        Assert.Equal(framesWhileMinimized, framesAfterMinimizedRefresh);
        Assert.True(
            framesAfterRestore > framesAfterMinimizedRefresh,
            $"恢复后应重绘（最小化期间 {framesAfterMinimizedRefresh}，恢复后 {framesAfterRestore}）。");
    }

    [Fact]
    public void Build内触发的尺寸事件不会在重建返回前绘制()
    {
        GlOffscreenSurface.AllowNonMainThreadCreation();
        var probe = new BuildReentryProbe();
        var component = new ResizingOnRebuildComponent(probe);
        var framesAfterRebuild = -1;
        var triggered = false;

        var result = UiApplication.Run(
            component,
            HostOptions("R18 重建重入", context =>
            {
                if (probe.BuildDepth > 0)
                {
                    // 违反约定：Build 尚未返回时进入了 FrameRendered。
                    probe.FramesRenderedDuringBuild++;
                }

                if (context.RenderedFrameCount == 1 && !triggered)
                {
                    triggered = true;
                    var host = UiApplication.CurrentHost!;
                    probe.InBuild = () =>
                    {
                        // 病态场景：组件在 Build 内改变窗口尺寸并触发刷新回调。
                        host.Window.ClientSize = new Vector2i(
                            host.Window.ClientSize.X + 80,
                            host.Window.ClientSize.Y + 60);
                        InvokeRefresh(host.Window);
                    };

                    context.Dispatcher.Post(() => component.Trigger!.Value++);
                    return;
                }

                if (triggered && probe.BuildCount >= 2 && framesAfterRebuild < 0)
                {
                    framesAfterRebuild = context.RenderedFrameCount;
                    context.RequestExit(0);
                }
            }));

        Assert.Equal(0, result);
        Assert.True(probe.BuildCount >= 2, $"应至少完成两次组件重建，实际 {probe.BuildCount}。");
        Assert.True(framesAfterRebuild >= 2, $"重建后的重绘请求不得丢失，实际帧数 {framesAfterRebuild}。");
        Assert.Equal(0, probe.FramesRenderedDuringBuild);
    }

    [Fact]
    public void Run异常退出后窗口事件不再提交帧()
    {
        GlOffscreenSurface.AllowNonMainThreadCreation();
        var framesBeforeFailure = -1;
        var exceptionObserved = false;

        using var host = new OpenTkHost(
            SmallSquare(),
            new UiApplicationOptions
            {
                Title = "R18 异常退出保护",
                Width = 200,
                Height = 150,
                VSync = false,
                StartVisible = false,
                TextService = new StubTextService(),
                FrameRendered = context =>
                {
                    if (context.RenderedFrameCount != 1)
                    {
                        return;
                    }

                    context.Dispatcher.Post(() =>
                    {
                        framesBeforeFailure = context.RenderedFrameCount;
                        throw new InvalidOperationException("R18 受控异常：调度动作失败。");
                    });
                },
            });

        try
        {
            host.Run();
        }
        catch (InvalidOperationException)
        {
            exceptionObserved = true;
        }

        // Run 已异常退出但宿主尚未 Dispose：窗口事件仍不得提交帧。
        host.Window.MakeCurrent();
        InvokeRefresh(host.Window);

        Assert.True(exceptionObserved, "Run 应把调度动作中的异常抛出。");
        Assert.Equal(framesBeforeFailure, host.RenderedFrameCount);
        Assert.Equal(0, host.ImmediateRedrawCount);
    }

    [Fact]
    public void 清屏不受旧裁剪限制覆盖整个framebuffer()
    {
        GlOffscreenSurface.AllowNonMainThreadCreation();
        Assert.False(string.IsNullOrWhiteSpace(_fixture.ContextDescription));

        // 使用隔离的离屏表面（不共享集合夹具），避免其他图形测试改变 framebuffer 尺寸干扰本回归。
        using var surface = new GlOffscreenSurface(256, 256, "R18 清屏裁剪隔离");
        surface.MakeCurrent();
        using var textures = new GlTextureService();
        using var renderer = new GlRenderer(textures);

        // 先把离屏 framebuffer 全部涂成红色；旧 scissor/旧视口会让下面的清屏只覆盖左上角。
        surface.Clear(Color.Red);
        var redBeforeSubmit = surface.GetPixel(200, 200);

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, (int)surface.FramebufferName);
        try
        {
            // 帧只声明 32×32 的视口（模拟沿用旧逻辑尺寸：framebuffer 仍是 256×256）。
            renderer.Submit(new RenderFrame([], new Size(32, 32), 1.0, Color.White));
        }
        finally
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        }

        var rendererFramebuffer = renderer.FramebufferSize;
        var corner = surface.GetPixel(31, 31);
        var outside1 = surface.GetPixel(200, 200);
        var outside2 = surface.GetPixel(255, 0);
        var outside3 = surface.GetPixel(0, 255);
        var diagnostics =
            $"表面 {surface.Size}（提交前 (200,200)={redBeforeSubmit}），渲染器 {rendererFramebuffer}，" +
            $"提交后 (31,31)={corner}, (200,200)={outside1}, (255,0)={outside2}, (0,255)={outside3}";

        Assert.True(redBeforeSubmit.R == 255 && redBeforeSubmit.G == 0, $"测试前置条件不成立（表面不是红色）：{diagnostics}");
        Assert.Equal(new Size(32, 32), rendererFramebuffer);
        // 视口之外、以及视口内的区域都必须被清屏色覆盖，而不是保留上一帧的红色。
        Assert.True(ColorsClose(Color.White, corner), $"视口内未清屏：{diagnostics}");
        Assert.True(ColorsClose(Color.White, outside1), $"右下区域未清屏：{diagnostics}");
        Assert.True(ColorsClose(Color.White, outside2), $"右上区域未清屏：{diagnostics}");
        Assert.True(ColorsClose(Color.White, outside3), $"左下区域未清屏：{diagnostics}");
    }

    private static View SmallSquare()
        => new Rectangle(Color.Red).Frame(48, 48, Alignment.Start);

    private static UiApplicationOptions HostOptions(string title, Action<UiHostContext> frameRendered)
        => new()
        {
            Title = title,
            Width = 300,
            Height = 200,
            VSync = false,
            StartVisible = false,
            TextService = new StubTextService(),
            FrameRendered = frameRendered,
        };

    private static void InvokeRefresh(NativeWindow window) => OnRefreshMethod.Invoke(window, null);

    private static bool ColorsClose(Color expected, Color actual, int tolerance = AssertColor.DefaultTolerance)
        => Math.Abs(expected.R - actual.R) <= tolerance
        && Math.Abs(expected.G - actual.G) <= tolerance
        && Math.Abs(expected.B - actual.B) <= tolerance
        && Math.Abs(expected.A - actual.A) <= tolerance;

    private static unsafe Vector2i QueryFramebufferSize(OpenTkHost host)
    {
        GLFW.GetFramebufferSize(host.Window.WindowPtr, out var width, out var height);
        return new Vector2i(width, height);
    }

    /// <summary>从窗口默认 framebuffer 的 front buffer 读取一个像素（GL 坐标：原点左下角）。</summary>
    private static Color ReadFrontPixel(OpenTkHost host, int x, int y)
    {
        host.Window.MakeCurrent();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        var bytes = new byte[4];
        try
        {
            GL.ReadBuffer(ReadBufferMode.Front);
            GL.PixelStore(PixelStoreParameter.PackAlignment, 1);
            unsafe
            {
                fixed (byte* pixels = bytes)
                {
                    GL.ReadPixels(
                        x,
                        y,
                        1,
                        1,
                        global::OpenTK.Graphics.OpenGL4.PixelFormat.Rgba,
                        PixelType.UnsignedByte,
                        (IntPtr)pixels);
                }
            }
        }
        finally
        {
            GL.ReadBuffer(ReadBufferMode.Back);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        }

        return new Color(bytes[0], bytes[1], bytes[2], bytes[3]);
    }

    private static void StartBackgroundDriver(Action driver)
    {
        var thread = new Thread(() => driver()) { IsBackground = true, Name = "CSharp.UI 尺寸回归测试驱动" };
        thread.Start();
    }

    /// <summary>记录组件 Build 深度与 Build 期间是否进入过 FrameRendered。</summary>
    private sealed class BuildReentryProbe
    {
        internal int BuildDepth;
        internal int BuildCount;
        internal int FramesRenderedDuringBuild;
        internal Action? InBuild;
    }

    /// <summary>可在 Build 内改变窗口尺寸的病态组件，用于验证重建阶段的重入保护。</summary>
    private sealed class ResizingOnRebuildComponent : Component
    {
        private readonly BuildReentryProbe _probe;

        internal ResizingOnRebuildComponent(BuildReentryProbe probe) => _probe = probe;

        internal State<int>? Trigger { get; private set; }

        protected override View Build()
        {
            _probe.BuildDepth++;
            _probe.BuildCount++;
            try
            {
                Trigger = UseState("trigger", 0);
                _ = Trigger.Value;
                _probe.InBuild?.Invoke();
                return new Rectangle(Color.Red);
            }
            finally
            {
                _probe.BuildDepth--;
            }
        }
    }
}
