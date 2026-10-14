using CSharp.UI;
using Xunit;

namespace CSharp.UI.Tests;

/// <summary>任务 10：Button、Toggle 与 Slider。</summary>
public class ControlTests
{
    [Fact]
    public void Button_MouseClickInvokesExactlyOnce()
    {
        var clicks = 0;
        using var app = new TestApp(new VStack(new Button("ok", () => clicks++)), new Size(200, 100));
        app.Frame();

        var button = app.Runtime.Root.Require<Button>();
        app.Click(button.Frame.Center);
        Assert.Equal(1, clicks);

        // 移动但不按下不触发。
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Move, button.Frame.Center));
        Assert.Equal(1, clicks);
    }

    [Fact]
    public void Button_ReleaseOutsideOrCancel_DoesNotInvoke()
    {
        var clicks = 0;
        using var app = new TestApp(new VStack(new Button("ok", () => clicks++)), new Size(200, 100));
        app.Frame();

        var button = app.Runtime.Root.Require<Button>();
        var outside = new Point(button.Frame.Right + 40, button.Frame.Center.Y);

        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Down, button.Frame.Center));
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Move, outside));
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Up, outside));
        Assert.Equal(0, clicks);

        // 按下后取消（例如窗口失焦）不触发。
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Down, button.Frame.Center));
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Cancel, button.Frame.Center));
        Assert.Equal(0, clicks);
    }

    [Fact]
    public void Button_KeyboardActivation_InvokesOnce()
    {
        var clicks = 0;
        using var app = new TestApp(new VStack(new Button("ok", () => clicks++)), new Size(200, 100));
        app.Frame();

        var button = app.Runtime.Root.Require<Button>();
        app.Runtime.SetFocus(button);

        app.Key(UiKey.Enter);
        Assert.Equal(1, clicks);

        app.Key(UiKey.Space);
        Assert.Equal(2, clicks);

        // 重复触发（长按）不重复激活。
        app.Runtime.DispatchKey(new KeyEvent(KeyEventKind.Down, UiKey.Space, KeyModifiers.None, Repeat: true));
        Assert.Equal(2, clicks);
        app.Runtime.DispatchKey(new KeyEvent(KeyEventKind.Up, UiKey.Space));
        Assert.Equal(3, clicks);
    }

    [Fact]
    public void Button_VisualStatesAreDistinguishable()
    {
        using var app = new TestApp(new VStack(new Button("ok")), new Size(200, 100));
        app.Frame();

        var button = app.Runtime.Root.Require<Button>();
        var normal = RectFill(app, button);

        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Move, button.Frame.Center));
        var hovered = RectFill(app, button);
        Assert.NotEqual(normal, hovered);

        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Down, button.Frame.Center));
        var pressed = RectFill(app, button);
        Assert.NotEqual(hovered, pressed);

        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Up, button.Frame.Center));
        app.Runtime.SetFocus(button);
        var focused = app.Frame().OfType<RectangleCommand>().First(c => c.Bounds == button.Frame);
        Assert.Equal(Theme.Light.FocusRing, focused.Stroke);
    }

    [Fact]
    public void DisabledButton_ShowsDisabledVisualAndCannotActivate()
    {
        var clicks = 0;
        using var app = new TestApp(new VStack(new Button("ok", () => clicks++).Disabled()), new Size(200, 100));
        app.Frame();

        var button = app.Runtime.Root.Require<Button>();
        var fill = RectFill(app, button);

        Assert.Equal(Theme.Light.DisabledBackground, fill);
        app.Click(button.Frame.Center);
        Assert.Equal(0, clicks);

        app.Runtime.SetFocus(button);
        Assert.Null(app.Runtime.FocusedNode);
    }

    [Fact]
    public void Toggle_MouseAndKeyboardUpdateBinding_AndExternalChangeUpdatesVisual()
    {
        var component = new ToggleHost();
        using var app = new TestApp(component, new Size(200, 100));
        app.Frame();

        var toggle = app.Runtime.Root.Require<Toggle>();
        Assert.False(component.Flag!.PeekValue());

        var knobOff = KnobX(app, toggle);

        app.Click(toggle.Frame.Center);
        app.Runtime.ProcessPendingWork();
        app.Frame();
        Assert.True(component.Flag.PeekValue());

        var knobOn = KnobX(app, toggle);
        Assert.True(knobOn > knobOff);

        // 键盘切换。
        app.Runtime.SetFocus(toggle);
        app.Key(UiKey.Space);
        app.Runtime.ProcessPendingWork();
        app.Frame();
        Assert.False(component.Flag.PeekValue());

        // 外部状态变化更新显示。
        component.Flag.Value = true;
        app.Runtime.ProcessPendingWork();
        app.Frame();
        Assert.True(knobOn == KnobX(app, toggle));
    }

    [Fact]
    public void Slider_ClampsToRange_AndStepsWithKeyboard()
    {
        var component = new SliderHost();
        using var app = new TestApp(component, new Size(200, 60));
        app.Frame();

        var slider = app.Runtime.Root.Require<Slider>();

        // 拖动捕获后越界仍然更新，但数值被限制。
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Down, slider.Frame.Center));
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Move, new Point(slider.Frame.Right + 1000, slider.Frame.Center.Y)));
        Assert.Equal(1.0, component.Value!.PeekValue(), 3);
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Up, new Point(slider.Frame.Right + 1000, slider.Frame.Center.Y)));

        // 键盘步进并按范围限制。
        app.Runtime.SetFocus(slider);
        for (var i = 0; i < 200; i++)
        {
            app.Key(UiKey.Left);
        }

        Assert.Equal(0.0, component.Value.PeekValue(), 3);
        app.Key(UiKey.Right);
        Assert.Equal(0.01, component.Value.PeekValue(), 3);

        // 外部状态变化同步显示。
        component.Value.Value = 0.5;
        app.Runtime.ProcessPendingWork();
        app.Frame();
        Assert.Equal(0.5, component.Value.PeekValue(), 3);
    }

    [Fact]
    public void Rebuild_ReplacesLabelAndCallbackAndKeepsFocus()
    {
        var component = new MutableButtonHost();
        using var app = new TestApp(component, new Size(200, 100));
        app.Frame();

        var button = app.Runtime.Root.Require<Button>();
        app.Runtime.SetFocus(button);
        app.Click(button.Frame.Center);
        Assert.Equal(["first"], component.Log);

        component.Label = "second";
        component.Handler = () => component.Log.Add("second");
        component.Trigger!.Value++;
        app.Runtime.ProcessPendingWork();
        app.Frame();

        Assert.Same(button, app.Runtime.FocusedNode);
        Assert.Equal("second", ((Button)button.View).Label);

        app.Click(button.Frame.Center);
        Assert.Equal(["first", "second"], component.Log);
    }

    [Fact]
    public void CounterIntegration_UpdatesTextWithoutManualRedraw()
    {
        var component = new CounterComponent();
        using var app = new TestApp(component, new Size(300, 200));
        app.Frame();

        var text = app.Runtime.Root.Require<Text>();
        Assert.Equal("count=0", ((Text)text.View).Content);

        var button = app.Runtime.Root.Require<Button>();
        app.Click(button.Frame.Center);
        app.Runtime.ProcessPendingWork();
        app.Frame();

        var updated = app.Runtime.Root.Require<Text>();
        Assert.Equal("count=1", ((Text)updated.View).Content);
        Assert.Same(text, updated);
    }

    private static Color RectFill(TestApp app, ViewNode node)
    {
        var command = app.Frame().OfType<RectangleCommand>().FirstOrDefault(c => c.Bounds == node.Frame);
        Assert.NotNull(command);
        return command!.Fill;
    }

    private static double KnobX(TestApp app, ViewNode toggle)
    {
        // 开关的最后一条矩形命令是滑块圆点。
        var commands = app.Frame().OfType<RectangleCommand>().Where(c => c.Bounds.X >= toggle.Frame.X).ToArray();
        return commands[^1].Bounds.X;
    }
}

/// <summary>带布尔状态的组件。</summary>
internal sealed class ToggleHost : Component
{
    public State<bool>? Flag { get; private set; }

    protected override View Build()
    {
        Flag = UseState("flag", false);
        return new VStack(new Text($"on:{Flag.Value}"), new Toggle(Flag));
    }
}

/// <summary>标签与回调可变的按钮组件。</summary>
internal sealed class MutableButtonHost : Component
{
    public string Label { get; set; } = "first";

    public Action? Handler { get; set; }

    public List<string> Log { get; } = [];

    public State<int>? Trigger { get; private set; }

    protected override View Build()
    {
        Trigger = UseState("trigger", 0);
        _ = Trigger.Value;
        if (Handler is null)
        {
            Handler = () => Log.Add("first");
        }

        var handler = Handler;
        return new VStack(new Button(Label, handler));
    }
}
