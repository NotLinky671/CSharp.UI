using CSharp.UI;
using Xunit;

namespace CSharp.UI.Tests;

/// <summary>
/// P-04：输入语义（外点吞并 + 锚子树豁免、Esc、失活、Up-activation、滚轮）。
/// 断言只走消费者可观察面：HitTest 归属、IsMounted、Binding 写计数、FocusedNode、ScrollOffset、
/// 下层控件回调计数；不 pin dismiss-target 类型/字段/子项数量。
/// </summary>
public class PopupInputTests
{
    // D2/D9/D13 -----------------------------------------------------------

    [Fact]
    public void P1_OutsidePress_ClosesPopupAndSwallowsWholeGesture()
    {
        var clicks = new List<string>();
        var toggle = new Toggle();
        var anchor = new Button("anchor", () => clicks.Add("anchor"));
        var popup = new Popup(anchor, PopupContent(clicks), toggle.Binding);
        var under = new Button("under", () => clicks.Add("under"));
        using var app = new TestApp(
            new VStack(popup, new Spacer().Frame(height: 120), under),
            new Size(320, 260));

        var underNode = app.Runtime.Root.FindDescendants<Button>().Last();
        toggle.Value = true;
        app.Runtime.ProcessPendingWork();
        Assert.True(app.Runtime.Overlay.HasActiveEntry);
        var popupNode = app.Runtime.Overlay.ActiveEntry!.PopupNode!;

        var point = Center(underNode.Frame);
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Move, point));
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Down, point, PointerButton.Left));

        // 吞并目标接管命中：不再是主树里的节点（下层用户树完全收不到事件）。
        var midHit = app.Runtime.HitTest(point);
        Assert.NotNull(midHit);
        Assert.False(IsSelfOrDescendant(app.Runtime.Root, midHit!));

        // 关闭在安全阶段消费；entry 关闭不影响吞并目标继续存在。
        app.Runtime.ProcessPendingWork();
        Assert.False(app.Runtime.Overlay.HasActiveEntry);
        Assert.False(popupNode.IsMounted);
        Assert.False(toggle.Value);
        Assert.Equal(1, toggle.Writes);
        Assert.False(IsSelfOrDescendant(app.Runtime.Root, app.Runtime.HitTest(point)!));

        // 同一个手势的 Up 也被吞并：下层 Button 不被激活。
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Up, point, PointerButton.Left));
        Assert.Empty(clicks);
        Assert.Same(underNode, app.Runtime.HitTest(point));

        // 下一次完整 gesture 正常作用到下层。
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Down, point, PointerButton.Left));
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Up, point, PointerButton.Left));
        Assert.Equal(["under"], clicks);
    }

    // D3 ------------------------------------------------------------------

    [Fact]
    public void P2_OutsidePress_OnAnotherPopupAnchor_OnlyClosesTheFirst()
    {
        var clicks = new List<string>();
        var first = new Toggle();
        var second = new Toggle();
        var firstPopup = new Popup(
            new Button("first anchor", () => clicks.Add("first-anchor")),
            new Text("first popup"),
            first.Binding);
        var secondPopup = new Popup(
            new Button("second anchor", () => clicks.Add("second-anchor")),
            new Text("second popup"),
            second.Binding);
        using var app = new TestApp(
            new VStack(firstPopup, new Spacer().Frame(height: 120), secondPopup),
            new Size(320, 260));

        var secondAnchor = app.Runtime.Root.FindDescendants<Button>().Last();
        first.Value = true;
        app.Runtime.ProcessPendingWork();
        Assert.True(app.Runtime.Overlay.HasActiveEntry);

        var point = Center(secondAnchor.Frame);
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Down, point, PointerButton.Left));
        app.Runtime.ProcessPendingWork();
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Up, point, PointerButton.Left));

        // 只关闭旧弹层；第二个锚的点击回调没有被这次被吞并的手势触发。
        Assert.False(app.Runtime.Overlay.HasActiveEntry);
        Assert.Empty(clicks);

        // 第二次按下才作用到新锚。
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Down, point, PointerButton.Left));
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Up, point, PointerButton.Left));
        Assert.Equal(["second-anchor"], clicks);
    }

    // D4 ------------------------------------------------------------------

    [Fact]
    public void P3_OutsidePress_OnScrollbarTrack_ClosesWithoutStartingDrag()
    {
        var toggle = new Toggle();
        var popup = new Popup(new Button("anchor", () => { }), new Text("popup"), toggle.Binding);
        var rows = new VStack(
            Enumerable.Range(0, 30).Select(i => (View)new Text($"row {i}")).ToArray());
        var scroll = new ScrollView(rows) { ScrollbarStyle = ScrollbarStyle.Floating };
        using var app = new TestApp(
            new VStack(popup, new Spacer().Frame(height: 120), scroll.Frame(height: 100)),
            new Size(320, 300));

        var scrollNode = app.Runtime.Root.Require<ScrollView>();
        toggle.Value = true;
        app.Runtime.ProcessPendingWork();
        Assert.True(app.Runtime.Overlay.HasActiveEntry);

        var track = new Rect(scrollNode.Frame.Right - 12, scrollNode.Frame.Y, 12, scrollNode.Frame.Height);
        var start = Center(track);
        var before = scrollNode.ScrollOffsetY;

        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Move, start));
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Down, start, PointerButton.Left));
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Move, new Point(start.X, start.Y + 40), PointerButton.Left));
        app.Runtime.ProcessPendingWork();
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Up, new Point(start.X, start.Y + 40), PointerButton.Left));

        Assert.False(app.Runtime.Overlay.HasActiveEntry);
        Assert.Equal(before, scrollNode.ScrollOffsetY, 3);
    }

    // D5 ------------------------------------------------------------------

    [Fact]
    public void P4_AnchorSubtreePress_DoesNotCloseNorSwallow()
    {
        var clicks = new List<string>();
        var toggle = new Toggle();
        var trigger = new Button("trigger", () =>
        {
            clicks.Add("trigger");
            toggle.Value = false;
        });
        var anchor = new VStack(trigger, new Text("anchor label"));
        var popup = new Popup(anchor, new Text("popup"), toggle.Binding);
        using var app = new TestApp(new VStack(popup, new Spacer().Frame(height: 150)), new Size(320, 260));

        var label = app.Runtime.Root.FindDescendants<Text>().First();
        toggle.Value = true;
        app.Runtime.ProcessPendingWork();
        Assert.True(app.Runtime.Overlay.HasActiveEntry);

        // 锚子树内的普通视图按下：不判定外点、不吞并、弹层保持开。
        var labelPoint = Center(label.Frame);
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Down, labelPoint, PointerButton.Left));
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Up, labelPoint, PointerButton.Left));
        app.Runtime.ProcessPendingWork();
        Assert.True(app.Runtime.Overlay.HasActiveEntry);
        Assert.True(IsSelfOrDescendant(app.Runtime.Root, app.Runtime.HitTest(labelPoint)!));

        // 锚内 trigger 照常可达（自身 toggle 关闭弹层）。
        var triggerNode = app.Runtime.Root.FindDescendants<Button>().First();
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Down, Center(triggerNode.Frame), PointerButton.Left));
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Up, Center(triggerNode.Frame), PointerButton.Left));
        app.Runtime.ProcessPendingWork();

        Assert.Equal(["trigger"], clicks);
        Assert.False(toggle.Value);
        Assert.False(app.Runtime.Overlay.HasActiveEntry);
    }

    // D6 ------------------------------------------------------------------

    [Fact]
    public void P5_Escape_ClosesAndRestoresFocus()
    {
        var clicks = new List<string>();
        var toggle = new Toggle();
        var focusTarget = new Button("focus target", () => clicks.Add("focus"));
        var anchor = new Button("anchor", () => { });
        var popup = new Popup(anchor, PopupContent(clicks), toggle.Binding);
        using var app = new TestApp(
            new VStack(new Text("main"), focusTarget, popup),
            new Size(320, 260));

        var nodes = app.Runtime.Root.FindDescendants<Button>().ToArray();
        var focusNode = nodes[0];
        var anchorNode = nodes[1];

        app.Runtime.SetFocus(focusNode);
        toggle.Value = true;
        app.Runtime.ProcessPendingWork();
        Assert.True(app.Runtime.Overlay.HasActiveEntry);

        var inside = app.Runtime.Overlay.ActiveEntry!.PopupNode!.FindDescendant<Button>()!;
        app.Runtime.SetFocus(inside);

        app.Runtime.DispatchKey(new KeyEvent(KeyEventKind.Down, UiKey.Escape));
        Assert.Same(focusNode, app.Runtime.FocusedNode);

        app.Runtime.ProcessPendingWork();
        Assert.False(app.Runtime.Overlay.HasActiveEntry);
        Assert.False(toggle.Value);
        Assert.Equal(1, toggle.Writes);
        Assert.Same(focusNode, app.Runtime.FocusedNode);

        // SavedFocus 缺失/失效 → 回落到锚子树首个 focusable。
        app.Runtime.SetFocus(null);
        toggle.Value = true;
        app.Runtime.ProcessPendingWork();
        Assert.True(app.Runtime.Overlay.HasActiveEntry);

        app.Runtime.DispatchKey(new KeyEvent(KeyEventKind.Down, UiKey.Escape));
        Assert.Same(anchorNode, app.Runtime.FocusedNode);
        app.Runtime.ProcessPendingWork();
        Assert.False(app.Runtime.Overlay.HasActiveEntry);
    }

    [Fact]
    public void P6_Escape_FocusedControlConsumesFirst()
    {
        var clicks = new List<string>();
        var toggle = new Toggle();
        var sink = new EscSinkView();
        var popup = new Popup(
            new Button("anchor", () => { }),
            new VStack(new Text("popup"), sink),
            toggle.Binding);
        using var app = new TestApp(new VStack(popup, new Spacer().Frame(height: 140)), new Size(320, 260));

        toggle.Value = true;
        app.Runtime.ProcessPendingWork();
        Assert.True(app.Runtime.Overlay.HasActiveEntry);

        var sinkNode = app.Runtime.Overlay.ActiveEntry!.PopupNode!.FindDescendant<EscSinkView>()!;
        app.Runtime.SetFocus(sinkNode);

        // 焦点控件先路由并消费：弹层保持开、overlay 不介入。
        app.Runtime.DispatchKey(new KeyEvent(KeyEventKind.Down, UiKey.Escape));
        Assert.True(sink.Consumed);
        app.Runtime.ProcessPendingWork();
        Assert.True(app.Runtime.Overlay.HasActiveEntry);

        // 焦点不在自处理控件上时，第二次 Esc 才由 overlay 关闭。
        app.Runtime.SetFocus(null);
        app.Runtime.DispatchKey(new KeyEvent(KeyEventKind.Down, UiKey.Escape));
        app.Runtime.ProcessPendingWork();
        Assert.False(app.Runtime.Overlay.HasActiveEntry);
    }

    // flags（真实行为）-----------------------------------------------------

    [Fact]
    public void P7_CloseOnOutsidePressFalse_DoesNotCloseAndPassesThrough()
    {
        var clicks = new List<string>();
        var toggle = new Toggle();
        var anchor = new Button("anchor", () => { });
        var popup = new Popup(anchor, PopupContent(clicks), toggle.Binding) { CloseOnOutsidePress = false };
        var under = new Button("under", () => clicks.Add("under"));
        using var app = new TestApp(
            new VStack(popup, new Spacer().Frame(height: 120), under),
            new Size(320, 260));

        toggle.Value = true;
        app.Runtime.ProcessPendingWork();
        Assert.True(app.Runtime.Overlay.HasActiveEntry);

        var point = Center(app.Runtime.Root.FindDescendants<Button>().Last().Frame);
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Down, point, PointerButton.Left));
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Up, point, PointerButton.Left));

        Assert.True(app.Runtime.Overlay.HasActiveEntry);
        Assert.Equal(["under"], clicks);
        Assert.Equal(0, toggle.Writes);
    }

    [Fact]
    public void P8_CloseOnEscapeFalse_DoesNotCloseNorHandleEscape()
    {
        var toggle = new Toggle();
        var popup = new Popup(new Button("anchor", () => { }), new Text("popup"), toggle.Binding)
        {
            CloseOnEscape = false,
        };
        using var app = new TestApp(new VStack(popup, new Spacer().Frame(height: 140)), new Size(320, 240));

        toggle.Value = true;
        app.Runtime.ProcessPendingWork();
        Assert.True(app.Runtime.Overlay.HasActiveEntry);

        app.Runtime.DispatchKey(new KeyEvent(KeyEventKind.Down, UiKey.Escape));
        app.Runtime.ProcessPendingWork();
        Assert.True(app.Runtime.Overlay.HasActiveEntry);
        Assert.True(toggle.Value);
        Assert.Equal(0, toggle.Writes);
    }

    // D7 ------------------------------------------------------------------

    [Fact]
    public void P9_DeactivationDuringSwallow_EndsGestureAndClearsInput()
    {
        var clicks = new List<string>();
        var toggle = new Toggle();
        var popup = new Popup(new Button("anchor", () => { }), new Text("popup"), toggle.Binding);
        var under = new Button("under", () => clicks.Add("under"));
        using var app = new TestApp(
            new VStack(popup, new Spacer().Frame(height: 120), under),
            new Size(320, 260));

        toggle.Value = true;
        app.Runtime.ProcessPendingWork();
        var point = Center(app.Runtime.Root.FindDescendants<Button>().Last().Frame);

        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Down, point, PointerButton.Left));
        app.Runtime.NotifyWindowActivation(false);
        app.Runtime.ProcessPendingWork();

        Assert.False(app.Runtime.Overlay.HasActiveEntry);
        Assert.False(toggle.Value);

        // 失活不抢主树焦点、也不留不可见输入拦截：激活后新手势正常。
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Up, point, PointerButton.Left));
        app.Runtime.NotifyWindowActivation(true);
        app.Runtime.ProcessPendingWork();

        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Down, point, PointerButton.Left));
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Up, point, PointerButton.Left));
        Assert.Equal(["under"], clicks);
    }

    // D8 / C11 ------------------------------------------------------------

    [Fact]
    public void P10_UpActivation_OpensOnUpWithoutLeakingIntoPopup()
    {
        var clicks = new List<string>();
        var toggle = new Toggle();
        var popup = new Popup(
            new Button("anchor", () =>
            {
                clicks.Add("anchor");
                toggle.Value = true;
            }),
            PopupContent(clicks),
            toggle.Binding);
        using var app = new TestApp(new VStack(popup, new Spacer().Frame(height: 140)), new Size(320, 260));

        var anchorNode = app.Runtime.Root.FindDescendants<Button>().First();
        var point = Center(anchorNode.Frame);

        // Down 只标按下；Up 同点才由 trigger 自身打开（opening gesture 完整落 trigger）。
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Down, point, PointerButton.Left));
        Assert.False(app.Runtime.Overlay.HasActiveEntry);
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Up, point, PointerButton.Left));
        app.Runtime.ProcessPendingWork();

        Assert.True(app.Runtime.Overlay.HasActiveEntry);
        Assert.Equal(["anchor"], clicks);

        // 弹层内控件没有被这次 gesture 的 Up 误激活。
        var inside = app.Runtime.Overlay.ActiveEntry!.PopupNode!.FindDescendant<Button>()!;
        Assert.False(inside.IsPressed);
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Move, Center(inside.Frame), PointerButton.None));
        Assert.Equal(["anchor"], clicks);
    }

    // D10 -----------------------------------------------------------------

    [Fact]
    public void P11_WheelOutside_ScrollsBackground_WheelInside_ScrollsPopupContent()
    {
        var toggle = new Toggle();
        var backgroundRows = new VStack(
            Enumerable.Range(0, 40).Select(i => (View)new Text($"bg {i}")).ToArray());
        var background = new ScrollView(backgroundRows).Frame(height: 90);
        var popupRows = new VStack(
            Enumerable.Range(0, 40).Select(i => (View)new Text($"pop {i}")).ToArray());
        var popup = new Popup(
            new Button("anchor", () => { }),
            new ScrollView(popupRows).Frame(height: 60),
            toggle.Binding);
        using var app = new TestApp(
            new VStack(popup, new Spacer().Frame(height: 110), background),
            new Size(320, 300));

        var backgroundNode = app.Runtime.Root.Require<ScrollView>();
        toggle.Value = true;
        app.Runtime.ProcessPendingWork();
        Assert.True(app.Runtime.Overlay.HasActiveEntry);

        // 弹层外滚轮：不关闭、不吞并，真实 hover 分发到下层 ScrollView。
        var bgPoint = Center(backgroundNode.Frame);
        app.Wheel(bgPoint, -1);
        app.Runtime.ProcessPendingWork();
        Assert.True(app.Runtime.Overlay.HasActiveEntry);
        Assert.True(backgroundNode.ScrollOffsetY > 0);

        var bgOffset = backgroundNode.ScrollOffsetY;
        var popupNode = app.Runtime.Overlay.ActiveEntry!.PopupNode!;
        var popupScroll = popupNode.FindDescendant<ScrollView>()!;
        app.Wheel(Center(popupScroll.Frame), -1);
        app.Runtime.ProcessPendingWork();

        Assert.True(popupScroll.ScrollOffsetY > 0);
        Assert.Equal(bgOffset, backgroundNode.ScrollOffsetY, 3);
        Assert.True(app.Runtime.Overlay.HasActiveEntry);
    }

    // 组合期 Esc（真实 TextField，非合成控件）--------------------------------

    [Fact]
    public void P12_CompositionOwnsFirstEscape_ThenExplicitCancelAndSecondEscapeCloses()
    {
        // 真实 TextField + 注入的输入服务替身：组合事件由测试显式投递（模拟平台侧消息），
        // 不是框架自己取消组合，也不代表做过原生 IME 实测。
        var text = "abc";
        var writes = 0;
        var binding = new Binding<string>(() => text, v =>
        {
            writes++;
            text = v;
        });
        var service = new FakeTextInputService();
        var toggle = new Toggle();
        var focusTarget = new Button("focus target", () => { });

        var popup = new Popup(
            new Button("anchor", () => { }),
            new VStack(new Text("popup"), new TextField(binding)),
            toggle.Binding);
        using var app = new TestApp(
            new VStack(focusTarget, popup, new Spacer().Frame(height: 120)),
            new Size(360, 280),
            textInputService: service);

        var focusNode = app.Runtime.Root.FindDescendants<Button>().First();
        app.Runtime.SetFocus(focusNode);

        toggle.Value = true;
        app.Runtime.ProcessPendingWork();
        Assert.True(app.Runtime.Overlay.HasActiveEntry);

        var fieldNode = app.Runtime.Overlay.ActiveEntry!.PopupNode!.FindDescendant<TextField>()!;
        app.Runtime.SetFocus(fieldNode);
        app.Runtime.ProcessPendingWork();
        Assert.Equal(1, service.StartCount);

        var client = service.ActiveClient;
        Assert.NotNull(client);
        var started = service.Apply(CompositionEvent.Start(
            client!.Session.SessionId, 0, 0, 0, new TextRange(3, 3), "ni"));
        Assert.False(started.HasRejection, started.RejectionReason);
        Assert.True(client.IsComposing);

        // 首次 Escape：组合期归输入法所有 → 真实 TextField 自己消费；框架不取消组合、弹层保持开、
        // 文本与会话所有权都不变。
        app.Runtime.DispatchKey(new KeyEvent(KeyEventKind.Down, UiKey.Escape));
        app.Runtime.ProcessPendingWork();

        Assert.True(app.Runtime.Overlay.HasActiveEntry);
        Assert.True(client.IsComposing);
        Assert.Equal(0, writes);
        Assert.Equal(1, service.StartCount);
        Assert.Same(client, service.ActiveClient);

        // 平台侧显式取消（模拟 IME/系统响应，不是框架自 Cancel）；之后第二次 Escape 才关闭并恢复焦点。
        Assert.True(service.RequestCancel());
        app.Runtime.ProcessPendingWork();
        Assert.False(client.IsComposing);

        app.Runtime.DispatchKey(new KeyEvent(KeyEventKind.Down, UiKey.Escape));
        app.Runtime.ProcessPendingWork();

        Assert.False(app.Runtime.Overlay.HasActiveEntry);
        Assert.False(toggle.Value);
        Assert.Equal(1, toggle.Writes);
        Assert.Same(focusNode, app.Runtime.FocusedNode);
    }

    // helpers -------------------------------------------------------------

    private static VStack PopupContent(List<string> clicks)
        => new VStack(new Text("popup text"), new Button("inside", () => clicks.Add("inside")));

    private static Point Center(Rect rect)
        => new(rect.X + (rect.Width / 2), rect.Y + (rect.Height / 2));

    private static bool IsSelfOrDescendant(ViewNode ancestor, ViewNode node)
    {
        for (var current = node; current is not null; current = current.Parent)
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    private sealed class Toggle
    {
        private readonly Binding<bool> _binding;

        internal Toggle()
        {
            _binding = new Binding<bool>(() => Value, v =>
            {
                Writes++;
                Value = v;
            });
        }

        internal bool Value { get; set; }

        internal int Writes { get; private set; }

        internal Binding<bool> Binding => _binding;
    }

    /// <summary>可控的“自处理 Esc”焦点控件（代表弹出层内先消费 Esc 的控件）。</summary>
    private sealed class EscSinkView : View
    {
        internal bool Consumed { get; private set; }

        public override bool IsFocusable => true;

        internal override void Measure(ViewNode node, LayoutConstraints constraints, LayoutContext context)
            => node.DesiredSize = constraints.Constrain(new Size(90, 24));

        internal override bool OnKeyDown(InputContext context, ViewNode node, KeyEvent e)
        {
            if (e.Key != UiKey.Escape || e.Kind != KeyEventKind.Down)
            {
                return false;
            }

            Consumed = true;
            context.MarkHandled();
            return true;
        }
    }
}
