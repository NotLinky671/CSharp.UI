using CSharp.UI;
using Xunit;

namespace CSharp.UI.Tests;

/// <summary>任务 09：输入分发、指针捕获与焦点。</summary>
public class InputFocusTests
{
    [Fact]
    public void OverlappingNodes_TopmostReceivesTheClick()
    {
        var log = new List<string>();
        var bottom = new Button("bottom", () => log.Add("bottom"));
        var top = new Button("top", () => log.Add("top"));

        using var app = new TestApp(new ZStack(bottom, top), new Size(200, 100));
        app.Click(new Point(100, 50));

        Assert.Equal(["top"], log);
    }

    [Fact]
    public void ClippedContent_HiddenPartsAreNotClickable()
    {
        var log = new List<string>();
        var button = new Button(new string('x', 40), () => log.Add("clicked"));
        var view = new VStack(new ScrollView(new VStack(button), ScrollAxis.Horizontal));

        using var app = new TestApp(view, new Size(200, 100));
        app.Frame();

        var buttonNode = app.Runtime.Root.Require<Button>();
        var scroll = app.Runtime.Root.Require<ScrollView>();
        Assert.Equal(200, scroll.Frame.Width, 3);
        Assert.True(buttonNode.Frame.Width > 200);

        // 可见部分可以点击。
        app.Click(scroll.Frame.Center);
        Assert.Equal(["clicked"], log);

        // 裁剪之外的按钮区域不可点击。
        var outside = new Point(scroll.Frame.Right + 60, scroll.Frame.Center.Y);
        app.Click(outside);
        Assert.Equal(["clicked"], log);
        Assert.NotSame(buttonNode, app.Runtime.HitTest(outside));

        // 滚动容器的绘制命令被限制在视口内（存在对应的裁剪入栈）。
        var commands = app.Frame();
        var clip = Assert.Single(commands.OfType<PushClipCommand>());
        Assert.Equal(scroll.Frame, clip.Bounds);
    }

    [Fact]
    public void DisabledSubtree_IsNotClickableNorFocusable()
    {
        var log = new List<string>();
        using var app = new TestApp(
            new VStack(
                new Button("disabled", () => log.Add("disabled")).Disabled(),
                new Button("enabled", () => log.Add("enabled"))),
            new Size(200, 100));
        app.Frame();

        var disabledButton = app.Runtime.Root.Require<Button>();
        Assert.True(disabledButton.IsEffectivelyDisabled);
        Assert.NotSame(disabledButton, app.Runtime.HitTest(disabledButton.Frame.Center));

        app.Click(disabledButton.Frame.Center);
        Assert.Empty(log);

        // 禁用节点不参与 Tab 遍历。
        var order = app.Runtime.GetFocusOrder();
        Assert.DoesNotContain(disabledButton, order);

        app.Runtime.SetFocus(null);
        app.Key(UiKey.Tab);
        Assert.NotNull(app.Runtime.FocusedNode);
        Assert.NotSame(disabledButton, app.Runtime.FocusedNode);
    }

    [Fact]
    public void PointerCapture_KeepsDraggingOutsideAndReleasesCleanly()
    {
        var component = new SliderHost();
        using var app = new TestApp(component, new Size(200, 60));
        app.Frame();

        var slider = app.Runtime.Root.Require<Slider>();
        var start = new Point(slider.Frame.X + 10, slider.Frame.Center.Y);

        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Down, start));
        Assert.True(slider.IsPointerCaptured);
        Assert.True(slider.IsPressed);

        // 拖到控件外：仍然接收移动事件，并且数值被限制在范围内。
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Move, new Point(slider.Frame.Right + 500, start.Y)));
        Assert.Equal(1.0, component.Value!.PeekValue(), 3);

        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Move, new Point(slider.Frame.X - 500, start.Y)));
        Assert.Equal(0.0, component.Value!.PeekValue(), 3);

        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Up, new Point(slider.Frame.X - 500, start.Y)));
        Assert.False(slider.IsPointerCaptured);
        Assert.False(slider.IsPressed);
    }

    [Fact]
    public void Cancel_ClearsHoverPressAndCapture()
    {
        using var app = new TestApp(new VStack(new Button("b")), new Size(200, 100));
        app.Frame();

        var button = app.Runtime.Root.Require<Button>();
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Down, button.Frame.Center));
        Assert.True(button.IsPressed);

        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Cancel, button.Frame.Center));
        Assert.False(button.IsPressed);
        Assert.False(button.IsPointerCaptured);
        Assert.False(button.IsHovered);
        Assert.Null(app.Runtime.HoveredNode);
    }

    [Fact]
    public void Unmount_ClearsCaptureAndFocus()
    {
        var component = new ConditionalFocusHost();
        using var app = new TestApp(component, new Size(200, 100));
        app.Frame();

        var button = app.Runtime.Root.Require<Button>();
        app.Runtime.SetFocus(button);
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Down, button.Frame.Center));
        Assert.Same(button, app.Runtime.FocusedNode);
        Assert.True(button.IsPointerCaptured);

        component.Show = false;
        component.Trigger!.Value++;
        app.Runtime.ProcessPendingWork();
        app.Frame();

        Assert.Null(app.Runtime.FocusedNode);
        Assert.Null(app.Runtime.Root.FindDescendant<Button>());
        Assert.False(button.IsPointerCaptured);
        Assert.False(button.IsPressed);
    }

    [Fact]
    public void TabTraversal_IsStableAndWraps()
    {
        var log = new List<string>();
        using var app = new TestApp(
            new VStack(
                new Button("a", () => log.Add("a")),
                new Toggle(false, _ => { }, "t"),
                new Slider(Binding<double>.Constant(0.5))),
            new Size(300, 200));
        app.Frame();

        var order = app.Runtime.GetFocusOrder();
        Assert.Equal(3, order.Count);
        Assert.IsType<Button>(order[0].View);
        Assert.IsType<Toggle>(order[1].View);
        Assert.IsType<Slider>(order[2].View);

        app.Runtime.SetFocus(null);
        app.Key(UiKey.Tab);
        Assert.Same(order[0], app.Runtime.FocusedNode);
        app.Key(UiKey.Tab);
        Assert.Same(order[1], app.Runtime.FocusedNode);
        app.Key(UiKey.Tab);
        Assert.Same(order[2], app.Runtime.FocusedNode);
        app.Key(UiKey.Tab);
        Assert.Same(order[0], app.Runtime.FocusedNode);

        app.Key(UiKey.Tab, KeyModifiers.Shift);
        Assert.Same(order[2], app.Runtime.FocusedNode);
    }

    [Fact]
    public void Focus_IsPreservedWhenNodeIsReused_AndClearedWhenRemoved()
    {
        var component = new ConditionalFocusHost();
        using var app = new TestApp(component, new Size(200, 100));
        app.Frame();

        var button = app.Runtime.Root.Require<Button>();
        app.Runtime.SetFocus(button);

        component.Label = "renamed";
        component.Trigger!.Value++;
        app.Runtime.ProcessPendingWork();
        app.Frame();

        Assert.Same(button, app.Runtime.FocusedNode);
        Assert.Equal("renamed", ((Button)button.View).Label);

        component.Show = false;
        component.Trigger!.Value++;
        app.Runtime.ProcessPendingWork();
        app.Frame();
        Assert.Null(app.Runtime.FocusedNode);
    }

    [Fact]
    public void ControlKeys_AreNotTreatedAsTextInput()
    {
        var component = new FieldHost();
        using var app = new TestApp(component, new Size(300, 60));
        app.Frame();

        var field = app.Runtime.Root.Require<TextField>();
        app.Runtime.SetFocus(field);

        // 控制键不产生字符。
        app.Key(UiKey.A);
        app.Runtime.ProcessPendingWork();
        Assert.Equal(string.Empty, component.Text!.PeekValue());

        // 已提交文本事件才写入字符。
        app.Runtime.DispatchTextInput(new TextInputEvent("a"));
        app.Runtime.ProcessPendingWork();
        Assert.Equal("a", component.Text.PeekValue());
    }

    [Fact]
    public void HitTest_UsesLogicalCoordinatesUnderDpi()
    {
        var log = new List<string>();
        using var app = new TestApp(
            new HStack(new Button("b", () => log.Add("hit")).Frame(width: 80, height: 40)),
            new Size(400, 300),
            pixelScale: 2.0);
        app.Frame();

        var frame = app.Runtime.Root.Require<FrameView>();
        Assert.Equal(80, frame.Frame.Width, 3);
        Assert.Equal(40, frame.Frame.Height, 3);

        app.Click(frame.Frame.Center);
        Assert.Equal(["hit"], log);

        // 逻辑坐标之外（framebuffer 坐标的一半处）不会命中。
        log.Clear();
        app.Click(new Point(frame.Frame.Right + 10, frame.Frame.Center.Y));
        Assert.Empty(log);
    }
}

/// <summary>带滑块的组件。</summary>
internal sealed class SliderHost : Component
{
    public State<double>? Value { get; private set; }

    protected override View Build()
    {
        Value = UseState("value", 0.5);
        return new VStack(new Slider(Value.ToBinding(), 0, 1, 0.01, 200));
    }
}

/// <summary>可条件显示按钮、可改标签的组件。</summary>
internal sealed class ConditionalFocusHost : Component
{
    public bool Show { get; set; } = true;

    public string Label { get; set; } = "go";

    public State<int>? Trigger { get; private set; }

    protected override View Build()
    {
        Trigger = UseState("trigger", 0);
        _ = Trigger.Value;
        return Show
            ? new VStack(new Button(Label))
            : new VStack(new Text("gone"));
    }
}

