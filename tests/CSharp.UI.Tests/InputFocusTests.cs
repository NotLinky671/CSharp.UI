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

        // 真实不变量：内容 clip 比 frame 至少底缩 gutter 宽——gutter 区不绘制子项内容。
        var commands = app.Frame();
        var clips = commands.OfType<PushClipCommand>().ToList();
        var hasInnerClip = clips.Any(c =>
            c.Bounds != scroll.Frame
            && c.Bounds.Bottom <= scroll.Frame.Bottom - ScrollbarMetrics.Thickness);
        Assert.True(hasInnerClip,
            $"至少一条 clip 必须相对 frame 右缩 ≥ {ScrollbarMetrics.Thickness}px（gutter 区不绘制内容）。");
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

    [Fact]
    public void BlankLeftPress_ClearsButtonFocus_RingDropsToBorder()
    {
        using var app = new TestApp(
            new VStack(new Button("ok").Frame(width: 80, height: 30)).Align(Alignment.Start),
            new Size(300, 100));
        app.Frame();

        var button = app.Runtime.Root.Require<Button>();
        app.Click(button.Frame.Center);
        Assert.Same(button, app.Runtime.FocusedNode);
        Assert.Equal(Theme.Light.FocusRing,
            app.Frame().OfType<RectangleCommand>().First(c => c.Bounds == button.Frame).Stroke);

        // 左键点窗口内、控件外空白 → 焦点清空、环回 Border 色（消费者可见）。
        app.Click(new Point(280, 50));
        Assert.Null(app.Runtime.FocusedNode);
        Assert.False(button.IsFocused);
        Assert.Equal(Theme.Light.Border,
            app.Frame().OfType<RectangleCommand>().First(c => c.Bounds == button.Frame).Stroke);
    }

    [Fact]
    public void BlankLeftPress_ClearsFieldFocus_EndsTextInputSession()
    {
        var service = new BlankPressRecordingTextInputService();
        var host = new FieldHost();
        using var app = new TestApp(host, new Size(300, 120), textInputService: service);
        app.Frame();

        var field = app.Runtime.Root.Require<TextField>();
        app.Runtime.SetFocus(field);
        app.Runtime.ProcessPendingWork();
        Assert.NotNull(service.ActiveClient);

        // 点在窗口内、控件外空白（field 宽 200 + 宿主 VStack padding/对齐外即空白）。
        app.Runtime.DispatchPointer(new PointerEvent(
            PointerEventKind.Down, new Point(280, 100), PointerButton.Left));

        Assert.Null(app.Runtime.FocusedNode);
        Assert.False(field.IsFocused);
        Assert.Null(service.ActiveClient);
        Assert.Equal(1, service.EndCallCount);
    }

    [Fact]
    public void BlankLeftPress_KeepsFocus_WhenHitChainHasFocusableAncestor()
    {
        // deepest 命中不可聚焦 child Text；父 FocusablePassThroughHost 可聚焦但不消费
        // OnPointerDown（返回 false）→ 链上仍有可聚焦祖先，按下不清焦。
        var host = new FocusablePassThroughHost();
        using var app = new TestApp(host, new Size(300, 120));
        app.Frame();

        var container = app.Runtime.Root.Require<FocusablePassThrough>();
        var button = app.Runtime.Root.Require<Button>();
        app.Click(button.Frame.Center);
        Assert.Same(button, app.Runtime.FocusedNode);

        // 点 pass-through 容器内的 Text（deepest=Text，父=focusable）：焦点保留在 Button。
        var text = app.Runtime.Root.Require<Text>();
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Down, text.Frame.Center, PointerButton.Left));
        Assert.Same(button, app.Runtime.FocusedNode);
    }

    [Fact]
    public void NonLeftPress_DoesNotClearFocus()
    {
        using var app = new TestApp(new VStack(new Button("ok").Frame(width: 80, height: 30)).Align(Alignment.Start), new Size(300, 100));
        app.Frame();

        var button = app.Runtime.Root.Require<Button>();
        app.Click(button.Frame.Center);
        Assert.Same(button, app.Runtime.FocusedNode);

        var blank = new Point(280, 50);
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Down, blank, PointerButton.Right));
        Assert.Same(button, app.Runtime.FocusedNode);
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Down, blank, PointerButton.Middle));
        Assert.Same(button, app.Runtime.FocusedNode);
    }
    [Fact]
    public void ConsumedScrollbarPress_DoesNotClearFieldFocus()
    {
        // ScrollView standard 滚动条槽/区消费按下——属交互，不是空白 → 保留文本焦点。
        var host = new FocusedScrollFieldHost();
        using var app = new TestApp(host, new Size(200, 160));
        app.Frame();

        var field = app.Runtime.Root.Require<TextField>();
        var scroll = app.Runtime.Root.Require<ScrollView>();
        app.Runtime.SetFocus(field);
        app.Runtime.ProcessPendingWork();
        Assert.Same(field, app.Runtime.FocusedNode);

        // bar 区按下（standard 槽——消费返回 true）：焦点保留。
        var track = scroll.Frame;
        app.Runtime.DispatchPointer(new PointerEvent(
            PointerEventKind.Down, new Point(track.Right - 4, track.Y + 10), PointerButton.Left));
        Assert.Same(field, app.Runtime.FocusedNode);
    }

    [Fact]
    public void FocusTransfer_GoesDirectly_NoInterimClear()
    {
        // field 焦点 → 左键点 button：一次转移（old EndSession + new focused），无先清后转。
        var log = new List<string>();
        var service = new BlankPressRecordingTextInputService();
        var binding = Binding<string>.From(() => "", _ => { });
        using var app = new TestApp(
            new VStack(new TextField(binding), new Button("go", () => log.Add("go"))),
            new Size(300, 140),
            textInputService: service);
        app.Frame();

        var field = app.Runtime.Root.Require<TextField>();
        var button = app.Runtime.Root.Require<Button>();
        app.Runtime.SetFocus(field);
        app.Runtime.ProcessPendingWork();

        Assert.NotNull(service.ActiveClient);

        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Down, button.Frame.Center, PointerButton.Left));

        Assert.Same(button, app.Runtime.FocusedNode);
        Assert.Equal(1, service.EndCallCount);
        Assert.Null(service.ActiveClient);
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

/// <summary>一个 TextField + 溢出 ScrollView：用于验证滚动条区按下不清焦。</summary>
internal sealed class FocusedScrollFieldHost : Component
{
    public State<string>? Text { get; private set; }

    protected override View Build()
    {
        Text = UseState("scroll-text", "");
        var rows = Enumerable.Range(0, 60).Select(i => (View)new Text($"row {i}")).ToArray();
        return new VStack(new TextField(Text.ToBinding()), new ScrollView(new VStack(rows), ScrollAxis.Vertical));
    }
}

/// <summary>Focusable 但不消费 OnPointerDown 的裸容器：用于验证链上 focusable 祖先保留焦点。</summary>
internal sealed class FocusablePassThrough : View
{
    public View Child { get; }

    public FocusablePassThrough(View child) => Child = child;

    public override bool IsFocusable => true;

    internal override IReadOnlyList<View> Children => [Child];

    internal override void Measure(ViewNode node, LayoutConstraints constraints, LayoutContext context)
    {
        context.Measure(node.ChildList[0], constraints);
        node.DesiredSize = node.ChildList[0].DesiredSize;
    }

    internal override void Arrange(ViewNode node, Rect bounds, LayoutContext context)
        => context.Arrange(node.ChildList[0], bounds);

    internal override void Emit(ViewNode node, DrawContext context) => context.EmitChild(node.ChildList[0]);

    internal override bool OnPointerDown(InputContext context, ViewNode node, PointerEvent e) => false;
}

/// <summary>宿主：一个 Button + 一个 FocusablePassThrough(Text)。</summary>
internal sealed class FocusablePassThroughHost : Component
{
    protected override View Build()
        => new VStack(new Button("b"), new FocusablePassThrough(new Text("inside")));
}

/// <summary>最小假服务：记录 Start/EndSession 调用与活动客户端，不模拟 IME 细节。</summary>
internal sealed class BlankPressRecordingTextInputService : ITextInputService
{
    public TextInputCapabilities Capabilities => TextInputCapabilities.Full;

    public ITextInputClient? ActiveClient { get; private set; }

    public int StartCount { get; private set; }

    public int EndCallCount { get; private set; }

    public bool StartSession(ITextInputClient client)
    {
        StartCount++;
        ActiveClient = client;
        return true;
    }

    public bool EndSession()
    {
        EndCallCount++;
        if (ActiveClient is null)
        {
            return false;
        }

        ActiveClient = null;
        return true;
    }

    public TextInputApplyResult ApplyComposition(IReadOnlyList<CompositionEvent> events)
        => ActiveClient?.ApplyComposition(events) ?? TextInputApplyResult.NotHandled(TextSnapshot.Empty);

    public bool RequestCancel() => ActiveClient?.CancelComposition() ?? false;

    public bool PublishGeometry(TextInputGeometry geometry) => false;
}
