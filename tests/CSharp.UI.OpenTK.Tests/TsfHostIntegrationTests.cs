using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using CSharp.UI.OpenTK;
using CSharp.UI.OpenTK.WindowsTsf;
using CSharp.UI.Testing;
using Xunit;

namespace CSharp.UI.OpenTK.Tests;

/// <summary>
/// TI-07 P3b：<b>正式 <see cref="UiApplication.Run"/> + OpenGL 宿主</b> 的 TSF 接线 smoke。
/// </summary>
/// <remarks>
/// 不是受控 owner 或合成事件：真实 STA 线程、真实窗口/GL 上下文、真实 TextField 焦点，
/// 由路由器惰性建立真实 TSF 会话（ActivateEx + flags + UIElement sink）。只验证接线与能力事实，
/// 候选文字/选词由 P4 的真实微软拼音场景负责。
/// </remarks>
[Collection(GraphicsCollection.Name)]
[Trait("Category", "Graphics")]
public sealed class TsfHostIntegrationTests
{
    [Fact]
    public void FormalHost_StartsTsfWithUiLessCapability_AndRetiresOnExit()
    {
        if (!OperatingSystem.IsWindows())
        {
            // 非 Windows：没有 TSF/msctf，真实宿主接线不适用。
            return;
        }

        GlOffscreenSurface.AllowNonMainThreadCreation();
        var errors = new ConcurrentQueue<string>();
        var frames = 0;
        TsfInputBridge? bridge = null;
        var activeBackend = HostInputBackendKind.Imm;
        var reason = string.Empty;
        uint activeFlags = 0;
        var advised = false;
        var candidateAvailable = false;

        var thread = new Thread(() =>
        {
            var result = UiApplication.Run(
                new TextField(Binding<string>.Constant("nihao")),
                new UiApplicationOptions
                {
                    Title = "CSharp.UI TI-07 P3b host smoke",
                    Width = 360,
                    Height = 120,
                    VSync = false,
                    StartVisible = false,
                    TextService = new StubTextService(),
                    UseTsfInputBridge = true,
                    Started = context =>
                    {
                        context.Runtime.ProcessPendingWork();
                        context.Runtime.DispatchPointer(
                            new PointerEvent(PointerEventKind.Down, new Point(20, 30)));
                        context.Runtime.ProcessPendingWork();
                        if (context.Runtime.FocusedNode is null)
                        {
                            errors.Enqueue("TextField 未获得焦点。");
                        }
                    },
                    FrameRendered = context =>
                    {
                        frames++;
                        if (frames > 1)
                        {
                            return;
                        }

                        var host = UiApplication.CurrentHost;
                        if (host?.Router is not { } router)
                        {
                            errors.Enqueue("正式宿主没有稳定输入路由器。");
                            context.RequestExit(2);
                            return;
                        }

                        activeBackend = router.ActiveBackend;
                        reason = router.BackendReason;
                        bridge = router.TsfBridge;
                        if (bridge is null)
                        {
                            errors.Enqueue($"TSF 未建立：{reason}");
                            context.RequestExit(3);
                            return;
                        }

                        activeFlags = bridge.Session.ActiveFlags;
                        advised = bridge.Session.UiElementAdvised;
                        candidateAvailable = bridge.Session.CandidatePresentationAvailable;
                        context.RequestExit(0);
                    },
                });

            if (result != 0)
            {
                errors.Enqueue($"UiApplication.Run 退出码 {result}。");
            }
        })
        {
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(120)), "正式宿主 smoke 超时。");

        Assert.Empty(errors);
        Assert.True(frames > 0, "正式宿主未渲染任何帧。");
        Assert.Equal(HostInputBackendKind.Tsf, activeBackend);
        Assert.NotNull(bridge);
        Assert.True((activeFlags & TsfFlags.TF_TMF_ACTIVATED) != 0, $"flags=0x{activeFlags:X8}");
        Assert.True((activeFlags & TsfFlags.TF_TMAE_UIELEMENTENABLEDONLY) != 0,
            $"flags=0x{activeFlags:X8}");
        Assert.True(advised, "UIElement sink 未注册。");
        Assert.True(candidateAvailable, $"UI-less 候选通路不可用：{reason}");
        Assert.True(bridge!.Session.IsDisposed, "宿主退出后 TSF 会话未释放。");
    }
}
