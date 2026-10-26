using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using CSharp.UI.OpenTK;
using CSharp.UI.OpenTK.WindowsTsf;
using CSharp.UI.Testing;
using Xunit;

namespace CSharp.UI.OpenTK.Tests;

/// <summary>
/// TI-07 P3b：正式宿主上的布局通知去重回归（消费者可见计数，不测私有布尔/源文本）。
/// </summary>
/// <remarks>
/// <para>
/// 真实 <see cref="UiApplication.Run"/> + TSF 会话 + 真实 TextField：
/// 有效布局 → 1×1 不可布局，等待“几何缓存为空且通知恰好 +1”；
/// 随后跨固定数个正常安全阶段/帧确认计数稳定；恢复视口，等待“几何缓存非空且通知再 +1”，
/// 再确认稳定。
/// </para>
/// <para>
/// <b>不 pin 帧号：</b>通知发生在 drain/publish/frame 的真实时序里，测试只观察状态转移；
/// 每帧显式请求重绘驱动，总帧数有上限，超时失败，不做无限轮询。
/// </para>
/// </remarks>
[Collection(GraphicsCollection.Name)]
[Trait("Category", "Graphics")]
public sealed class TsfLayoutNotificationRegressionTests
{
    private const int MaxDriveFrames = 120;
    private const int StableFramesRequired = 5;

    [Fact]
    public void LayoutNotification_CoalescesOnLossAndRecovery_WithStablePlateaus()
    {
        if (!OperatingSystem.IsWindows())
        {
            // 非 Windows：没有 TSF/msctf，真实宿主布局通知回归不适用。
            return;
        }

        GlOffscreenSurface.AllowNonMainThreadCreation();
        var errors = new ConcurrentQueue<string>();
        var frames = 0;
        var attempts = 0;
        var phase = 0;
        var stableFrames = 0;
        TsfInputBridge? bridge = null;
        var baseline = -1;
        var lossCount = -1;
        var restoreCount = -1;

        var thread = new Thread(() =>
        {
            var result = UiApplication.Run(
                new TextField(Binding<string>.Constant("nihao")),
                new UiApplicationOptions
                {
                    Title = "CSharp.UI TI-07 layout notification regression",
                    Width = 360,
                    Height = 120,
                    VSync = false,
                    StartVisible = false,
                    TextService = new StubTextService(),
            RichTextService = new StubTextService() as IRichTextService,
                    UseTsfInputBridge = true,
                    Started = context =>
                    {
                        context.Runtime.ProcessPendingWork();
                        context.Runtime.DispatchPointer(
                            new PointerEvent(PointerEventKind.Down, new Point(20, 30)));
                        context.Runtime.ProcessPendingWork();

                        if (UiApplication.CurrentHost?.Router?.TsfBridge is not { } created)
                        {
                            errors.Enqueue("正式宿主未建立 TSF 桥。");
                            context.RequestExit(2);
                            return;
                        }

                        bridge = created;
                        baseline = bridge.LayoutNotificationCount;

                        // 真实消费者行为：最终布局缩到 1×1，caret/组合几何必然不可用。
                        context.Runtime.SetViewport(new Size(1, 1), context.PixelScale);
                        context.Runtime.ProcessPendingWork();
                    },
                    FrameRendered = context =>
                    {
                        frames++;
                        var count = bridge?.LayoutNotificationCount ?? -1;
                        var cache = bridge?.Session.GeometryProvider.Current;

                        if (frames > MaxDriveFrames || attempts > MaxDriveFrames)
                        {
                            errors.Enqueue(
                                $"超时未完成状态转移：phase={phase} frames={frames} attempts={attempts} "
                                + $"count={count} baseline={baseline} "
                                + $"sinkMask=0x{bridge?.Session.Store.SinkMask ?? 0:X} "
                                + $"cache={(cache is null ? "null" : "present")}。");
                            context.RequestExit(6);
                            return;
                        }

                        switch (phase)
                        {
                            case 0: // 等待不可布局 + 恰好一次撤销通知
                                if (cache is null && count == baseline + 1)
                                {
                                    lossCount = count;
                                    attempts = 0;
                                    stableFrames = 0;
                                    phase = 1;
                                }
                                else
                                {
                                    attempts++;
                                }

                                break;

                            case 1: // 不可布局稳定平台期
                                if (count != lossCount || cache is not null)
                                {
                                    errors.Enqueue(
                                        $"不可布局平台期漂移：count={count} expected={lossCount} "
                                        + $"cache={(cache is null ? "null" : "present")}。");
                                    context.RequestExit(3);
                                    return;
                                }

                                if (++stableFrames >= StableFramesRequired)
                                {
                                    attempts = 0;
                                    phase = 2;
                                }

                                break;

                            case 2: // 恢复真实视口，等待重新发布
                                context.Runtime.SetViewport(new Size(360, 120), context.PixelScale);
                                phase = 3;
                                break;

                            case 3: // 等待几何恢复 + 恰好再一次通知（publish 在 drain 之后）
                                if (cache is not null && count == lossCount + 1)
                                {
                                    restoreCount = count;
                                    attempts = 0;
                                    stableFrames = 0;
                                    phase = 4;
                                }
                                else if (count < lossCount || count > lossCount + 1)
                                {
                                    errors.Enqueue(
                                        $"恢复期间通知计数越界：count={count} loss={lossCount}。");
                                    context.RequestExit(4);
                                    return;
                                }
                                else
                                {
                                    attempts++;
                                }

                                break;

                            case 4: // 恢复后的稳定平台期
                                if (count != restoreCount || cache is null)
                                {
                                    errors.Enqueue(
                                        $"恢复平台期漂移：count={count} expected={restoreCount} "
                                        + $"cache={(cache is null ? "null" : "present")}。");
                                    context.RequestExit(5);
                                    return;
                                }

                                if (++stableFrames >= StableFramesRequired)
                                {
                                    if (bridge?.HasPendingSafeWork == true)
                                    {
                                        errors.Enqueue("稳定后仍有待处理安全阶段工作。");
                                    }

                                    context.RequestExit(errors.IsEmpty ? 0 : 7);
                                    return;
                                }

                                break;
                        }

                        // 显式驱动下一帧，不依赖光标闪烁/定时器。
                        context.RequestRedraw();
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(120)), "布局通知回归超时。");

        Assert.Empty(errors);
        Assert.True(frames > 0);
        Assert.Equal(baseline + 1, lossCount);
        Assert.Equal(lossCount + 1, restoreCount);
    }
}
