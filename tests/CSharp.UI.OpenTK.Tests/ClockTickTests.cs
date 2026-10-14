using CSharp.UI.Testing;
using Xunit;

namespace CSharp.UI.OpenTK.Tests;

/// <summary>
/// 时钟推进（<see cref="UiRuntime.OnClockTick"/>）与光标闪烁契约的单元测试（不需要 GL 上下文）。
/// </summary>
/// <remarks>
/// 宿主主循环在每次迭代调用 <c>OnClockTick()</c>：焦点视图声明“需要定时重绘”时才请求重绘。
/// 光标可见性由运行时时钟与闪烁周期推导，因此用可控时钟即可验证翻转。
/// </remarks>
public sealed class ClockTickTests
{
    [Fact]
    public void 没有焦点视图时不请求定时重绘()
    {
        var (runtime, _, _) = CreateRuntime(new Rectangle(Color.Red));

        Assert.Null(runtime.GetNextWakeup());
        runtime.BuildFrame();
        Assert.False(runtime.NeedsDraw);

        runtime.OnClockTick();

        Assert.False(runtime.NeedsDraw);
        Assert.Null(runtime.GetNextWakeup());
    }

    [Fact]
    public void 焦点输入框声明定时重绘并随时钟推进翻转光标()
    {
        var (runtime, clock, _) = CreateRuntime(new TextField(Binding<string>.Constant("abc")));
        var focusOrder = runtime.GetFocusOrder();
        Assert.NotEmpty(focusOrder);
        runtime.SetFocus(focusOrder[0]);
        Assert.NotNull(runtime.FocusedNode);

        var visibleFrame = runtime.BuildFrame();
        Assert.True(HasCaret(visibleFrame), "聚焦后应立即显示光标。");
        Assert.NotNull(runtime.GetNextWakeup());

        // 宿主语义：定时唤醒到期后调用 OnClockTick 请求重绘。
        clock.Advance(TimeSpan.FromMilliseconds(600));
        runtime.OnClockTick();
        Assert.True(runtime.NeedsDraw);

        var hiddenFrame = runtime.BuildFrame();
        Assert.False(HasCaret(hiddenFrame), "推进一个闪烁半周期后光标应隐藏。");

        clock.Advance(TimeSpan.FromMilliseconds(600));
        runtime.OnClockTick();
        var visibleAgainFrame = runtime.BuildFrame();
        Assert.True(HasCaret(visibleAgainFrame), "再推进一个半周期后光标应重新显示。");
    }

    [Fact]
    public void 清除焦点后不再请求定时重绘()
    {
        var (runtime, _, _) = CreateRuntime(new TextField(Binding<string>.Constant("abc")));
        runtime.SetFocus(runtime.GetFocusOrder()[0]);
        Assert.NotNull(runtime.GetNextWakeup());

        runtime.SetFocus(null);

        Assert.Null(runtime.GetNextWakeup());
        runtime.BuildFrame();
        runtime.OnClockTick();
        Assert.False(runtime.NeedsDraw);
    }

    /// <summary>光标是 TextField 输出的唯一窄矩形命令（无描边、宽度 ≤ 2）。</summary>
    private static bool HasCaret(RenderFrame frame)
        => frame.Commands
            .OfType<RectangleCommand>()
            .Any(command => command.Stroke is null && command.Bounds.Width <= 2 && command.Bounds.Height > 2);

    private static (UiRuntime Runtime, TestUiClock Clock, HeadlessUiDispatcher Dispatcher) CreateRuntime(View root)
    {
        var dispatcher = new HeadlessUiDispatcher();
        var clock = new TestUiClock();
        var runtime = new UiRuntime(root, new UiRuntimeServices
        {
            Dispatcher = dispatcher,
            TextService = new StubTextService(),
            Clock = clock,
        });
        runtime.SetViewport(new Size(200, 40), 1.0);
        runtime.ProcessPendingWork();
        return (runtime, clock, dispatcher);
    }
}
