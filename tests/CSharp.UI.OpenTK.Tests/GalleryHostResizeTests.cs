using CSharp.UI.Samples.ControlGallery;
using CSharp.UI.Testing;
using global::OpenTK.Graphics.OpenGL4;
using global::OpenTK.Mathematics;
using global::OpenTK.Windowing.Desktop;
using global::OpenTK.Windowing.GraphicsLibraryFramework;
using System.Reflection;
using Xunit;

namespace CSharp.UI.OpenTK.Tests;

/// <summary>
/// RL-08 宿主集成：在真实 OpenTkHost + 真实 GL 上下文 + 真实 Skia 文本上，让 <c>GalleryApp</c>
/// 连续跨断点 resize/refresh，验证每次回调返回前已按新尺寸完成重建、布局并提交完整帧。
/// </summary>
/// <remarks>
/// <para>
/// 这里是<b>程序化</b> resize（改变 ClientSize 同步派发 Resize/FramebufferResize）+ 反射触发 <c>NativeWindow.OnRefresh</c>，
/// 与 R18 的路径一致；<b>不是</b> Windows 原生 SC_SIZE 模态循环，也<b>不是</b>人工鼠标拖拽。
/// </para>
/// <para>
/// 每个步骤都与“同一逻辑视口尺寸下的无窗口真实布局”逐节点对照，确认没有半成品或过时布局。
/// </para>
/// </remarks>
[Collection(GraphicsCollection.Name)]
[Trait("Category", "Graphics")]
public sealed class GalleryHostResizeTests
{
    private static readonly MethodInfo OnRefreshMethod = typeof(NativeWindow).GetMethod(
        "OnRefresh",
        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
        ?? throw new InvalidOperationException("找不到 NativeWindow.OnRefresh。");

    private static readonly double[] TargetLogicalWidths = [300, 520, 620, 900, 1120, 340, 820];

    private readonly GlContextFixture _fixture;

    /// <summary>注入共享 GL 上下文夹具。</summary>
    public GalleryHostResizeTests(GlContextFixture fixture) => _fixture = fixture;

    [Fact]
    public void 真实宿主连续跨断点resize取得稳定完整布局()
    {
        GlOffscreenSurface.AllowNonMainThreadCreation();
        Assert.False(string.IsNullOrWhiteSpace(_fixture.ContextDescription));

        var steps = new List<string>();
        var failures = new List<string>();
        var observed = false;
        var idleBuilds = -1;
        var idleLayouts = -1;
        var idleFrames = -1;

        var result = UiApplication.Run(
            new GalleryApp(),
            new UiApplicationOptions
            {
                Title = "RL-08 Gallery 连续跨断点 resize",
                Width = 360,
                Height = 760,
                VSync = false,
                StartVisible = false,
                ImageService = CreateImages(),
                FrameRendered = context =>
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
                            var scale = context.PixelScale > 0 ? context.PixelScale : 1.0;
                            var clientY = Math.Max(64, (int)Math.Round(760 * scale));

                            foreach (var logicalWidth in TargetLogicalWidths)
                            {
                                var before = context.RenderedFrameCount;
                                host.Window.ClientSize = new Vector2i(
                                    Math.Max(64, (int)Math.Round(logicalWidth * scale)),
                                    clientY);

                                // Resize 回调已即时绘制；再显式触发刷新等价于 WM_PAINT → GLFW damage 通知。
                                InvokeRefresh(host.Window);

                                var frames = context.RenderedFrameCount;
                                var logical = context.LogicalSize;
                                var real = QueryFramebufferSize(host);
                                var renderer = host.Renderer.FramebufferSize;
                                var pending = context.Runtime.PendingResponsiveRequestCount;
                                var needsDraw = context.Runtime.NeedsDraw;
                                // 右上角现在被 persistent 滚动条轨道占据（非清屏色）：读轨道左侧 1 像素仍属页面背景。
                                var corner = ReadFrontPixel(host, real.X - 3 - (int)Math.Round(ScrollbarMetrics.Thickness * scale), 3);
                                var background = context.Runtime.ClearColor;

                                steps.Add(
                                    $"{logicalWidth}→逻辑 {logical.Width:0.##}×{logical.Height:0.##} fb{real} renderer{renderer} " +
                                    $"frames{before}->{frames} pending{pending}");

                                if (frames <= before)
                                {
                                    failures.Add($"{steps[^1]}：尺寸/刷新回调内未提交新帧。");
                                }

                                if (renderer != new Size(real.X, real.Y) || context.FramebufferSize != new Size(real.X, real.Y))
                                {
                                    failures.Add($"{steps[^1]}：渲染器/上下文 framebuffer 与真实 framebuffer 不一致。");
                                }

                                if (pending != 0)
                                {
                                    failures.Add($"{steps[^1]}：仍有未消费的响应式请求，布局未收敛。");
                                }

                                if (needsDraw)
                                {
                                    failures.Add($"{steps[^1]}：即时绘制后仍处于需要重绘状态。");
                                }

                                if (!ColorsClose(background, corner))
                                {
                                    failures.Add($"{steps[^1]}：右上角背景像素 {corner} ≠ 清屏色 {background}（可能残留黑边）。");
                                }

                                foreach (var node in context.Runtime.Root.Descendants())
                                {
                                    if (!double.IsFinite(node.Frame.X) || !double.IsFinite(node.Frame.Y)
                                        || !double.IsFinite(node.Frame.Width) || !double.IsFinite(node.Frame.Height))
                                    {
                                        failures.Add($"{steps[^1]}：{node.DebugPath} 几何非有限：{node.Frame}。");
                                        break;
                                    }

                                    if (node.Frame.Width < 0 || node.Frame.Height < 0)
                                    {
                                        failures.Add($"{steps[^1]}：{node.DebugPath} 出现负尺寸：{node.Frame}。");
                                        break;
                                    }

                                    // 横向滚动容器内容可宽于视口（由 ScrollView 裁剪）；其他节点不得越过。
                                    var inHorizontalScroll = false;
                                    for (var p = node.Parent; p is not null; p = p.Parent)
                                    {
                                        if (p.View is ScrollView s && s.Axis == ScrollAxis.Horizontal)
                                        {
                                            inHorizontalScroll = true;
                                            break;
                                        }
                                    }
                                    if (!inHorizontalScroll && node.Frame.Right > logical.Width + 0.01)
                                    {
                                        failures.Add($"{steps[^1]}：{node.DebugPath} 越过视口宽度：{node.Frame}。");
                                        break;
                                    }
                                }

                                // 与同逻辑尺寸的无窗口真实布局逐节点对照：宿主布局必须与确定性布局一致。
                                using var reference = new GalleryIntegrationHost(new GalleryApp(), logical, 1.0);
                                var actualSignature = GalleryLayoutSnapshot.Capture(context.Runtime);
                                var expectedSignature = GalleryLayoutSnapshot.Capture(reference.Runtime);
                                if (actualSignature != expectedSignature)
                                {
                                    failures.Add($"{steps[^1]}：宿主布局与同尺寸无窗口布局不一致。");
                                }
                            }

                            // 静止检查：没有新事件时计数保持不变（不空转重建/重排/重绘）。
                            idleBuilds = context.Runtime.BuildCount;
                            idleLayouts = context.Runtime.LayoutPassCount;
                            idleFrames = context.RenderedFrameCount;
                            if (context.Runtime.NeedsDraw)
                            {
                                failures.Add("静止时不应仍处于需要重绘状态。");
                            }

                            observed = true;
                        }
                        finally
                        {
                            context.RequestExit(0);
                        }
                    });
                },
            });

        Assert.Equal(0, result);
        Assert.True(observed, "未能在首帧后执行连续跨断点 resize 验证。");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        Assert.Equal(TargetLogicalWidths.Length, steps.Count);
        Assert.True(idleLayouts >= 1 && idleFrames >= TargetLogicalWidths.Length,
            $"宿主应完成布局并提交至少 {TargetLogicalWidths.Length} 帧，实际布局轮 {idleLayouts}、帧 {idleFrames}。");
        Console.WriteLine($"[RL-08] 宿主连续 resize {steps.Count} 步全部一致；构建 {idleBuilds}，布局轮 {idleLayouts}，帧 {idleFrames}");
        foreach (var step in steps)
        {
            Console.WriteLine($"[RL-08]   {step}");
        }
    }

    private static StubImageService CreateImages()
    {
        var images = new StubImageService();
        images.Register(Path.Combine(AppContext.BaseDirectory, "shared", "Assets", "logo.png"), 64, 64);
        return images;
    }

    private static void InvokeRefresh(NativeWindow window) => OnRefreshMethod.Invoke(window, null);

    private static bool ColorsClose(Color expected, Color actual, int tolerance = 3)
        => Math.Abs(expected.R - actual.R) <= tolerance
        && Math.Abs(expected.G - actual.G) <= tolerance
        && Math.Abs(expected.B - actual.B) <= tolerance;

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
}
