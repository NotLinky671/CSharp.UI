using CSharp.UI.Testing;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Xunit;
using GlfwKeyModifiers = OpenTK.Windowing.GraphicsLibraryFramework.KeyModifiers;

namespace CSharp.UI.OpenTK.Tests;

/// <summary>GLFW 修饰键没有 None 成员，用 0 表示无修饰键。</summary>
internal static class GlfwNoModifiers
{
    internal const GlfwKeyModifiers Value = (GlfwKeyModifiers)0;
}

/// <summary>
/// 调度器与输入适配的纯 CPU 单元测试：不需要窗口与 GL 上下文。
/// </summary>
public sealed class UiDispatcherTests
{
    [Fact]
    public void 后台线程投递动作会入队并唤醒事件循环()
    {
        var wakeCount = 0;
        var dispatcher = new OpenTkUiDispatcher(() => Interlocked.Increment(ref wakeCount));
        var executed = false;

        var thread = new Thread(() => dispatcher.Post(() => executed = true));
        thread.Start();
        thread.Join();

        Assert.True(dispatcher.HasPendingActions);
        Assert.Equal(1, dispatcher.PendingActionCount);
        Assert.True(Volatile.Read(ref wakeCount) >= 1, "后台投递必须唤醒正在等待事件的主循环。");
        Assert.False(executed);

        Assert.Equal(1, dispatcher.ProcessPostedActions());
        Assert.True(executed);
        Assert.False(dispatcher.HasPendingActions);
        Assert.Equal(1, dispatcher.ExecutedActionCount);
    }

    [Fact]
    public void 同一线程投递不唤醒且按顺序执行()
    {
        var wakeCount = 0;
        var dispatcher = new OpenTkUiDispatcher(() => wakeCount++);
        var order = new List<int>();

        dispatcher.Post(() => order.Add(1));
        dispatcher.Post(() => order.Add(2));
        dispatcher.Post(() => order.Add(3));

        Assert.True(dispatcher.IsOnUiThread);
        Assert.Equal(0, wakeCount);
        Assert.Equal(3, dispatcher.ProcessPostedActions());
        Assert.Equal(new[] { 1, 2, 3 }, order);
    }

    [Fact]
    public void 重绘请求与定时唤醒按最早时间合并()
    {
        var wakeCount = 0;
        var dispatcher = new OpenTkUiDispatcher(() => wakeCount++);

        Assert.Null(dispatcher.NextWakeupDelay);
        Assert.False(dispatcher.ConsumeDueWakeup());

        dispatcher.RequestRedraw();
        Assert.Equal(1, dispatcher.RedrawRequestCount);
        Assert.True(dispatcher.ConsumeRedrawRequest());
        Assert.False(dispatcher.ConsumeRedrawRequest());

        dispatcher.RequestWakeup(TimeSpan.FromSeconds(30));
        var later = dispatcher.NextWakeupDelay!.Value;
        dispatcher.RequestWakeup(TimeSpan.FromSeconds(0.5));
        var earlier = dispatcher.NextWakeupDelay!.Value;
        Assert.True(earlier < later, "多次唤醒请求必须取最早时间。");
        Assert.InRange(earlier.TotalSeconds, 0, 0.5);

        // 未到期时不消费。
        Assert.False(dispatcher.ConsumeDueWakeup());

        dispatcher.RequestWakeup(TimeSpan.Zero);
        Assert.True(dispatcher.ConsumeDueWakeup());
        Assert.Null(dispatcher.NextWakeupDelay);

        dispatcher.ClearWakeup();
        Assert.True(wakeCount > 0);
    }
}

/// <summary>输入映射表测试。</summary>
public sealed class OpenTkInputMappingTests
{
    [Theory]
    [InlineData(Keys.Escape, UiKey.Escape)]
    [InlineData(Keys.Enter, UiKey.Enter)]
    [InlineData(Keys.KeyPadEnter, UiKey.Enter)]
    [InlineData(Keys.Tab, UiKey.Tab)]
    [InlineData(Keys.Space, UiKey.Space)]
    [InlineData(Keys.Backspace, UiKey.Backspace)]
    [InlineData(Keys.Delete, UiKey.Delete)]
    [InlineData(Keys.Insert, UiKey.Insert)]
    [InlineData(Keys.Home, UiKey.Home)]
    [InlineData(Keys.End, UiKey.End)]
    [InlineData(Keys.PageUp, UiKey.PageUp)]
    [InlineData(Keys.PageDown, UiKey.PageDown)]
    [InlineData(Keys.Left, UiKey.Left)]
    [InlineData(Keys.Right, UiKey.Right)]
    [InlineData(Keys.Up, UiKey.Up)]
    [InlineData(Keys.Down, UiKey.Down)]
    [InlineData(Keys.A, UiKey.A)]
    [InlineData(Keys.Q, UiKey.Q)]
    [InlineData(Keys.Z, UiKey.Z)]
    public void 控制与字母键映射到UiKey(Keys source, UiKey expected)
        => Assert.Equal(expected, OpenTkInputMapping.ToUiKey(source));

    [Theory]
    [InlineData(Keys.F1)]
    [InlineData(Keys.D1)]
    [InlineData(Keys.LeftShift)]
    public void 未映射的按键返回Unknown(Keys source)
        => Assert.Equal(UiKey.Unknown, OpenTkInputMapping.ToUiKey(source));

    [Fact]
    public void 修饰键映射到框架修饰键()
    {
        var modifiers = OpenTkInputMapping.ToKeyModifiers(
            GlfwKeyModifiers.Control | GlfwKeyModifiers.Shift | GlfwKeyModifiers.Alt | GlfwKeyModifiers.Super | GlfwKeyModifiers.CapsLock);

        Assert.True(modifiers.HasFlag(KeyModifiers.Control));
        Assert.True(modifiers.HasFlag(KeyModifiers.Shift));
        Assert.True(modifiers.HasFlag(KeyModifiers.Alt));
        // macOS 的 Command（Super）以及 Windows/Linux 的 Super 都映射为 Meta。
        Assert.True(modifiers.HasFlag(KeyModifiers.Meta));
        Assert.True(modifiers.HasFlag(KeyModifiers.CapsLock));
        Assert.Equal(KeyModifiers.None, OpenTkInputMapping.ToKeyModifiers(GlfwKeyModifiers.NumLock));
    }

    [Fact]
    public void 鼠标按钮映射()
    {
        Assert.Equal(PointerButton.Left, OpenTkInputMapping.ToPointerButton(MouseButton.Left));
        Assert.Equal(PointerButton.Right, OpenTkInputMapping.ToPointerButton(MouseButton.Right));
        Assert.Equal(PointerButton.Middle, OpenTkInputMapping.ToPointerButton(MouseButton.Middle));
        Assert.Equal(PointerButton.None, OpenTkInputMapping.ToPointerButton(MouseButton.Button4));
    }
}

/// <summary>输入适配器到运行时事件的转换测试（不创建窗口）。</summary>
public sealed class OpenTkInputAdapterTests
{    [Fact]
    public void 鼠标事件转换为指针事件()
    {
        var (adapter, runtime, _) = CreateAdapter();
        runtime.SetViewport(new Size(100, 100), 1.0);
        runtime.ProcessPendingWork();

        adapter.HandleMouseMove(new MouseMoveEventArgs(10, 20, 0, 0));
        var move = Assert.IsType<PointerEvent>(adapter.LastPointerEvent);
        Assert.Equal(PointerEventKind.Move, move.Kind);
        Assert.Equal(new Point(10, 20), move.Position);

        // 悬停应命中根视图（Rectangle 默认按排列矩形命中测试）。
        Assert.NotNull(runtime.HoveredNode);

        adapter.HandleMouseDown(new MouseButtonEventArgs(MouseButton.Left, InputAction.Press, GlfwNoModifiers.Value));
        var down = Assert.IsType<PointerEvent>(adapter.LastPointerEvent);
        Assert.Equal(PointerEventKind.Down, down.Kind);
        Assert.Equal(PointerButton.Left, down.Button);
        Assert.Equal(1, down.ClickCount);

        adapter.HandleMouseUp(new MouseButtonEventArgs(MouseButton.Left, InputAction.Release, GlfwNoModifiers.Value));
        var up = Assert.IsType<PointerEvent>(adapter.LastPointerEvent);
        Assert.Equal(PointerEventKind.Up, up.Kind);
        Assert.Equal(3, adapter.PointerEventCount);
    }

    [Fact]
    public void 双击累计连击次数()
    {
        var (adapter, _, _) = CreateAdapter();
        adapter.HandleMouseMove(new MouseMoveEventArgs(5, 5, 0, 0));
        adapter.HandleMouseDown(new MouseButtonEventArgs(MouseButton.Left, InputAction.Press, GlfwNoModifiers.Value));
        Assert.Equal(1, adapter.LastPointerEvent!.Value.ClickCount);
        adapter.HandleMouseUp(new MouseButtonEventArgs(MouseButton.Left, InputAction.Release, GlfwNoModifiers.Value));
        adapter.HandleMouseDown(new MouseButtonEventArgs(MouseButton.Left, InputAction.Press, GlfwNoModifiers.Value));
        Assert.Equal(2, adapter.LastPointerEvent!.Value.ClickCount);
    }

    [Fact]
    public void 焦点丢失派发Cancel清理捕获()
    {
        var (adapter, _, _) = CreateAdapter();
        adapter.HandleMouseDown(new MouseButtonEventArgs(MouseButton.Left, InputAction.Press, GlfwNoModifiers.Value));

        adapter.HandleFocusChanged(false);

        var cancel = Assert.IsType<PointerEvent>(adapter.LastPointerEvent);
        Assert.Equal(PointerEventKind.Cancel, cancel.Kind);
        Assert.Equal(PointerButton.Left, cancel.Button);

        // 重新获得焦点不产生新的指针事件，但会请求重绘。
        adapter.HandleFocusChanged(true);
        Assert.Equal(PointerEventKind.Cancel, adapter.LastPointerEvent!.Value.Kind);
    }

    [Fact]
    public void 指针离开窗口派发Cancel()
    {
        var (adapter, _, _) = CreateAdapter();
        adapter.HandleMouseMove(new MouseMoveEventArgs(3, 4, 0, 0));
        adapter.HandleMouseLeave();

        var cancel = Assert.IsType<PointerEvent>(adapter.LastPointerEvent);
        Assert.Equal(PointerEventKind.Cancel, cancel.Kind);
        Assert.Equal(new Point(3, 4), cancel.Position);
    }

    [Fact]
    public void 滚轮事件使用最近的指针位置与修饰键()
    {
        var (adapter, _, _) = CreateAdapter();
        adapter.HandleMouseMove(new MouseMoveEventArgs(7, 8, 0, 0));
        adapter.HandleKeyDown(new KeyboardKeyEventArgs(Keys.LeftShift, 0, GlfwKeyModifiers.Shift, false));

        adapter.HandleWheel(new MouseWheelEventArgs(0.5f, -1f));

        var wheel = Assert.IsType<WheelEvent>(adapter.LastWheelEvent);
        Assert.Equal(new Point(7, 8), wheel.Position);
        Assert.True(Math.Abs(wheel.DeltaX - 0.5) < 0.0001, $"DeltaX 应为 0.5，实际 {wheel.DeltaX}");
        Assert.True(Math.Abs(wheel.DeltaY - -1.0) < 0.0001, $"DeltaY 应为 -1，实际 {wheel.DeltaY}");
    }

    [Fact]
    public void 键盘事件映射按键与修饰键()
    {
        var (adapter, _, _) = CreateAdapter();
        adapter.HandleKeyDown(new KeyboardKeyEventArgs(Keys.C, 0, GlfwKeyModifiers.Control | GlfwKeyModifiers.Shift, false));

        var keyEvent = Assert.IsType<KeyEvent>(adapter.LastKeyEvent);
        Assert.Equal(KeyEventKind.Down, keyEvent.Kind);
        Assert.Equal(UiKey.C, keyEvent.Key);
        Assert.True(keyEvent.IsCommandPressed);
        Assert.True(keyEvent.Modifiers.HasFlag(KeyModifiers.Shift));
        Assert.False(keyEvent.Repeat);

        adapter.HandleKeyUp(new KeyboardKeyEventArgs(Keys.C, 0, GlfwKeyModifiers.Control | GlfwKeyModifiers.Shift, false));
        Assert.Equal(KeyEventKind.Up, adapter.LastKeyEvent!.Value.Kind);
        Assert.Equal(2, adapter.KeyEventCount);
    }

    [Fact]
    public void Esc与命令键加Q请求退出()
    {
        var exitRequests = 0;
        var (adapter, _, _) = CreateAdapter(exitOnEscape: true, requestExit: () => exitRequests++);

        adapter.HandleKeyDown(new KeyboardKeyEventArgs(Keys.Escape, 0, GlfwNoModifiers.Value, false));
        Assert.Equal(1, exitRequests);

        adapter.HandleKeyDown(new KeyboardKeyEventArgs(Keys.Q, 0, GlfwKeyModifiers.Control, false));
        Assert.Equal(2, exitRequests);

        adapter.HandleKeyDown(new KeyboardKeyEventArgs(Keys.Q, 0, GlfwKeyModifiers.Super, false));
        Assert.Equal(3, exitRequests);

        // 单独的 Q 不退出。
        adapter.HandleKeyDown(new KeyboardKeyEventArgs(Keys.Q, 0, GlfwNoModifiers.Value, false));
        Assert.Equal(3, exitRequests);
    }

    [Fact]
    public void AltGr加Q不退出且字符落地_CtrlQ仍退出()
    {
        // TI-08 §5.1b：AltGr（常见布局 Ctrl+Alt）Q 不得误匹配 Ctrl+Q 退出键；
        // '@' 由 GLFW char 通道落地（真实生产 adapter+runtime，非 mock）。
        var exits = 0;
        var value = "";
        var binding = new Binding<string>(() => value, v => value = v);
        using var runtime = new UiRuntime(new TextField(binding), new UiRuntimeServices
        {
            Dispatcher = new HeadlessUiDispatcher(),
            TextService = new StubTextService(),
            RichTextService = new StubTextService() as IRichTextService,
        });
        runtime.SetViewport(new Size(400, 60), 1);
        runtime.Start();
        runtime.ProcessPendingWork();
        runtime.BuildFrame();
        var adapter = new OpenTkInputAdapter(runtime, () => exits++, exitOnEscape: true, exitOnCloseShortcut: true);
        runtime.SetFocus(runtime.Root);
        runtime.ProcessPendingWork();

        // AltGr+Q：Control|Alt key-down + '@' char 交付 → 不退出、字符恰一次落地。
        adapter.HandleKeyDown(new KeyboardKeyEventArgs(Keys.Q, 0, GlfwKeyModifiers.Control | GlfwKeyModifiers.Alt, false));
        adapter.HandleTextInput(new TextInputEventArgs('@'));
        runtime.ProcessPendingWork();
        runtime.BuildFrame();

        Assert.Equal(0, exits);
        Assert.Equal("@", value);

        // Ctrl+Q：退出边界保留。
        adapter.HandleKeyDown(new KeyboardKeyEventArgs(Keys.Q, 0, GlfwKeyModifiers.Control, false));
        Assert.Equal(1, exits);

    }

    [Fact]
    public void 关闭退出快捷键后Esc不再退出()
    {
        var exitRequests = 0;
        var (adapter, _, _) = CreateAdapter(exitOnEscape: false, requestExit: () => exitRequests++);

        adapter.HandleKeyDown(new KeyboardKeyEventArgs(Keys.Escape, 0, GlfwNoModifiers.Value, false));
        adapter.HandleKeyDown(new KeyboardKeyEventArgs(Keys.Q, 0, GlfwKeyModifiers.Alt, false));

        Assert.Equal(0, exitRequests);
    }

    [Fact]
    public void 文本输入过滤控制字符()
    {
        var (adapter, _, _) = CreateAdapter();

        adapter.HandleTextInput(new TextInputEventArgs('\n'));
        adapter.HandleTextInput(new TextInputEventArgs('\t'));
        Assert.Null(adapter.LastTextEvent);
        Assert.Equal(0, adapter.TextInputEventCount);

        adapter.HandleTextInput(new TextInputEventArgs('中'));
        Assert.Equal("中", adapter.LastTextEvent!.Value.Text);

        adapter.HandleTextInput(new TextInputEventArgs('a'));
        Assert.Equal("a", adapter.LastTextEvent!.Value.Text);
        Assert.Equal(2, adapter.TextInputEventCount);
    }

    private static (OpenTkInputAdapter Adapter, UiRuntime Runtime, HeadlessUiDispatcher Dispatcher) CreateAdapter(
        bool exitOnEscape = true,
        Action? requestExit = null)
    {
        var dispatcher = new HeadlessUiDispatcher();
        var runtime = new UiRuntime(new Rectangle(Color.Red), new UiRuntimeServices
        {
            Dispatcher = dispatcher,
            TextService = new StubTextService(),
            RichTextService = new StubTextService() as IRichTextService,
        });
        var adapter = new OpenTkInputAdapter(runtime, requestExit ?? (() => { }), exitOnEscape, exitOnCloseShortcut: true);
        return (adapter, runtime, dispatcher);
    }
}
