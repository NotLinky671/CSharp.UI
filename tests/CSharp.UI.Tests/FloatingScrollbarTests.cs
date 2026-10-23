using CSharp.UI;
using Xunit;

namespace CSharp.UI.Tests;

/// <summary>
/// Floating scrollbar 消费者可观察验收（FS-02/03/04）：真实 UiRuntime + TestUiClock，
/// 覆盖显隐转换、hover 续命、命中优先/透明、无 overflow、disabled/unmount/style 切换、
/// deadline 唤醒与 focused blink 合并。不读内部字段/不 pin 常量字面。
/// </summary>
public class FloatingScrollbarTests
{
    private static readonly TimeSpan HideDelay = TimeSpan.FromMilliseconds(1000);

    // ------------------------------------------------------------ helpers

    /// <summary>溢出 ScrollView（30 行内容、100px 视口），Floating 样式。</summary>
    private static (TestApp App, ViewNode Scroll) MakeScrollView(ScrollbarStyle style = ScrollbarStyle.Floating)
    {
        var content = new VStack(
            Enumerable.Range(0, 30)
                .Select(i => (View)new Text($"row {i}"))
                .ToArray());
        var app = new TestApp(
            new ScrollView(content) { ScrollbarStyle = style },
            new Size(200, 100));
        app.Frame();
        return (app, app.Runtime.Root.Require<ScrollView>());
    }

    /// <summary>溢出 TextEditor（30 行、100px 视口），Floating 样式。</summary>
    private static (TestApp App, ViewNode Editor) MakeEditor(ScrollbarStyle style = ScrollbarStyle.Floating)
    {
        var lines = string.Join("\n", Enumerable.Range(0, 30).Select(i => $"line {i}"));
        var binding = Binding<string>.From(() => lines, _ => { });
        var app = new TestApp(
            new TextEditor(binding) { ScrollbarStyle = style },
            new Size(200, 100));
        app.Frame();
        return (app, app.Runtime.Root.Require<TextEditor>());
    }

    /// <summary>Floating 下轨道条 = frame 右缘内 12px overlay 条。</summary>
    private static Rect TrackRect(ViewNode scroll)
    {
        var f = scroll.Frame;
        return new Rect(f.Right - 12, f.Y, 12, f.Height);
    }

    private static bool HasThumbCommand(IReadOnlyList<DrawCommand> commands, Rect track)
    {
        // Floating thumb：细长竖条、cross 宽 ≤ track 宽、落在 track 带内。
        foreach (var c in commands)
        {
            if (c is RectangleCommand r
                && r.Bounds.X >= track.X - 0.5
                && r.Bounds.Right <= track.Right + 0.5
                && r.Bounds.Width <= track.Width
                && r.Bounds.Height > r.Bounds.Width)
            {
                return true;
            }
        }
        return false;
    }

    private static void AdvanceAndTick(TestApp app, TimeSpan delta)
    {
        app.Clock.Advance(delta);
        app.Runtime.OnClockTick();
    }

    // ------------------------------------------------------------ 显隐转换（ScrollView）

    [Fact]
    public void ScrollView_Wheel_ShowsThenHidesAfterDelay()
    {
        var (app, scroll) = MakeScrollView();
        using var _ = app;
        var track = TrackRect(scroll);

        // 初始：无滚动 → 无 thumb 命令（hidden，非 persistent）。
        Assert.False(HasThumbCommand(app.Frame(), track), "未滚动前 floating bar 不应绘制。");

        // 真实滚动 → 可见。
        app.Wheel(scroll.Frame.Center, -3);
        app.Runtime.ProcessPendingWork();
        Assert.True(HasThumbCommand(app.Frame(), track), "wheel 真实滚动后 floating bar 应可见。");
        Assert.True(scroll.ScrollOffsetY > 0);

        // 未到 deadline：仍可见。
        AdvanceAndTick(app, HideDelay - TimeSpan.FromMilliseconds(100));
        Assert.True(HasThumbCommand(app.Frame(), track), "deadline 前 floating bar 应保持可见。");

        // 过 deadline：隐藏。
        AdvanceAndTick(app, TimeSpan.FromMilliseconds(200));
        Assert.False(HasThumbCommand(app.Frame(), track), "deadline 后 floating bar 应隐藏。");
    }

    [Fact]
    public void ScrollView_BoundaryWheel_DoesNotShow()
    {
        var (app, scroll) = MakeScrollView();
        using var _ = app;
        var track = TrackRect(scroll);

        // 已到顶向上滚：无 offset 变化 → 不显示。
        app.Wheel(scroll.Frame.Center, +3);
        app.Runtime.ProcessPendingWork();
        Assert.Equal(0, scroll.ScrollOffsetY, 3);
        Assert.False(HasThumbCommand(app.Frame(), track), "边界 wheel 无滚动不应显示 floating bar。");
    }

    [Fact]
    public void ScrollView_NoOverflow_NeverShows()
    {
        var content = new VStack(new Text("only one line"));
        var app = new TestApp(
            new ScrollView(content) { ScrollbarStyle = ScrollbarStyle.Floating },
            new Size(200, 100));
        using var _ = app;
        var scroll = app.Runtime.Root.Require<ScrollView>();
        var track = TrackRect(scroll);
        app.Frame();

        app.Wheel(scroll.Frame.Center, -3);
        app.Runtime.ProcessPendingWork();
        Assert.False(HasThumbCommand(app.Frame(), track), "无 overflow 永不显示 floating bar。");
    }

    // ------------------------------------------------------------ hover 续命 / drag 保持

    [Fact]
    public void ScrollView_HoverOnVisibleBar_SuppressesHide()
    {
        var (app, scroll) = MakeScrollView();
        using var _ = app;
        var track = TrackRect(scroll);

        app.Wheel(scroll.Frame.Center, -3);
        app.Runtime.ProcessPendingWork();
        Assert.True(HasThumbCommand(app.Frame(), track));

        // 指针移入可见 bar 区 → hover 抑制：超过原 deadline 仍可见。
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Move, track.Center));
        AdvanceAndTick(app, HideDelay + TimeSpan.FromMilliseconds(200));
        Assert.True(HasThumbCommand(app.Frame(), track), "hover 可见 bar 上应抑制隐藏。");

        // 移出 bar 区 → 重新计时后隐藏。
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Move, new Point(scroll.Frame.X + 10, scroll.Frame.Center.Y)));
        AdvanceAndTick(app, HideDelay + TimeSpan.FromMilliseconds(100));
        Assert.False(HasThumbCommand(app.Frame(), track), "移出 bar 后应重新计时隐藏。");
    }

    [Fact]
    public void ScrollView_DragKeepsVisible_ReleaseRearms()
    {
        var (app, scroll) = MakeScrollView();
        using var _ = app;
        var track = TrackRect(scroll);

        app.Wheel(scroll.Frame.Center, -3);
        app.Runtime.ProcessPendingWork();

        // thumb 实际位置按下并拖 → drag 期间超 deadline 仍可见。
        var thumbRect = ScrollbarMetrics.ThumbRect(
            track, horizontal: false,
            viewportExtent: scroll.Frame.Height,
            contentExtent: scroll.ChildList[0].DesiredSize.Height,
            offset: scroll.ScrollOffsetY);
        var grab = thumbRect.Center;
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Down, grab, PointerButton.Left));
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Move, new Point(grab.X, grab.Y + 20)));
        AdvanceAndTick(app, HideDelay + TimeSpan.FromMilliseconds(200));
        Assert.True(HasThumbCommand(app.Frame(), track), "drag 期间应保持可见。");

        // 释放 → 重挂计时；pointer 仍在 bar 上属 hover 抑制，移开后到期隐藏。
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Up, new Point(grab.X, grab.Y + 20), PointerButton.Left));
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Move, new Point(scroll.Frame.X + 10, scroll.Frame.Center.Y)));
        AdvanceAndTick(app, HideDelay + TimeSpan.FromMilliseconds(100));
        Assert.False(HasThumbCommand(app.Frame(), track), "release 并移开后应重新计时隐藏。");
    }

    // ------------------------------------------------------------ 命中协议

    [Fact]
    public void ScrollView_HiddenBar_PointerPassesToContent()
    {
        var (app, scroll) = MakeScrollView();
        using var _ = app;
        app.Frame();
        var track = TrackRect(scroll);

        // hidden 期：bar 区命中应穿透到子内容（deepest = 子节点），owner overlay 返回 null。
        var hit = app.Runtime.HitTest(track.Center);
        Assert.NotNull(hit);
        Assert.False(ReferenceEquals(hit, scroll), "hidden bar 区命中应落到子内容而非 owner。");
    }

    [Fact]
    public void ScrollView_VisibleBar_HitBeatsChildContent()
    {
        var (app, scroll) = MakeScrollView();
        using var _ = app;
        app.Frame();
        var track = TrackRect(scroll);

        // 显示后：bar 区命中归 owner（thumb/track 分支），不穿透到子内容。
        app.Wheel(scroll.Frame.Center, -3);
        app.Runtime.ProcessPendingWork();
        var hit = app.Runtime.HitTest(track.Center);
        Assert.True(ReferenceEquals(hit, scroll), "可见 bar 区命中应归 owner。");
        // 实际消费：track After 区点击翻页（offset 前进），不落子路径。
        var before = scroll.ScrollOffsetY;
        app.Click(new Point(track.Center.X, track.Bottom - 4));
        Assert.True(scroll.ScrollOffsetY > before, "可见 bar track 区点击应触发翻页。");
    }

    // ------------------------------------------------------------ 调度（唤醒不 busy loop）

    [Fact]
    public void GetNextWakeup_ReflectsFloatingDeadline_AndClearsAfterHide()
    {
        var (app, scroll) = MakeScrollView();
        using var _ = app;

        // 无滚动、无 focus → 无唤醒。
        Assert.Null(app.Runtime.GetNextWakeup());

        app.Wheel(scroll.Frame.Center, -3);
        app.Runtime.ProcessPendingWork();

        // armed：wakeup ≈ 1s。
        var wakeup = app.Runtime.GetNextWakeup();
        Assert.NotNull(wakeup);
        Assert.True(wakeup.Value > TimeSpan.FromMilliseconds(900) && wakeup.Value <= HideDelay + TimeSpan.FromMilliseconds(50),
            $"armed deadline 应体现在 GetNextWakeup，实际 {wakeup}。");

        // 到期隐藏后：无残留唤醒。
        AdvanceAndTick(app, HideDelay + TimeSpan.FromMilliseconds(100));
        Assert.Null(app.Runtime.GetNextWakeup());
    }

    [Fact]
    public void OnClockTick_FutureDeadline_DoesNotRepaint()
    {
        var (app, scroll) = MakeScrollView();
        using var _ = app;

        app.Wheel(scroll.Frame.Center, -3);
        app.Runtime.ProcessPendingWork();
        var frame = app.Frame();   // 消费 draw

        // 时钟推进但未到期：OnClockTick 不应仅因 future deadline 产生重绘。
        app.Clock.Advance(TimeSpan.FromMilliseconds(100));
        app.Runtime.OnClockTick();
        Assert.False(app.Runtime.NeedsDraw, "future deadline 不应在 tick 中制造重绘。");
    }

    // ------------------------------------------------------------ TextEditor

    [Fact]
    public void TextEditor_Wheel_ShowsThenHides_ZeroGutter()
    {
        var (app, editor) = MakeEditor();
        using var _ = app;

        var inner = new Rect(
            editor.Frame.X + 10, editor.Frame.Y + 6,
            editor.Frame.Width - 20, editor.Frame.Height - 12);   // Floating inner = frame−padding（无 12px gutter 扣减）
        var track = new Rect(
            editor.Frame.Right - 10 - 12, editor.Frame.Y + 6, 12, editor.Frame.Height - 12);

        Assert.False(HasThumbCommand(app.Frame(), track));

        app.Wheel(editor.Frame.Center, -3);
        app.Runtime.ProcessPendingWork();
        var state = (TextEditorState)editor.InteractionState!;
        Assert.True(state.ScrollY > 0);
        Assert.True(HasThumbCommand(app.Frame(), track), "wheel 滚动后 floating bar 应可见。");

        AdvanceAndTick(app, HideDelay + TimeSpan.FromMilliseconds(100));
        Assert.False(HasThumbCommand(app.Frame(), track), "deadline 后应隐藏。");
    }

    [Fact]
    public void TextEditor_HiddenBar_ClickPlacesCaret()
    {
        var (app, editor) = MakeEditor();
        using var _ = app;
        app.Frame();

        // hidden 期点击 bar 区位置 → 文本路径（focus+caret），不被 overlay 拦截。
        var track = new Rect(
            editor.Frame.Right - 10 - 12, editor.Frame.Y + 6, 12, editor.Frame.Height - 12);
        app.Click(track.Center);
        Assert.True(editor.IsFocused, "hidden bar 区点击应走文本路径（focus）。");
    }

    // ------------------------------------------------------------ 清理

    [Fact]
    public void ScrollView_Disabled_HidesBarImmediately()
    {
        var (app, scroll) = MakeScrollView();
        using var _ = app;
        var track = TrackRect(scroll);

        app.Wheel(scroll.Frame.Center, -3);
        app.Runtime.ProcessPendingWork();
        Assert.True(HasThumbCommand(app.Frame(), track));

        // 禁用 → 立即隐藏，无 due 残留。
        app.Runtime.SetEnvironment(new EnvironmentValues { Theme = Theme.Light, Disabled = true });
        app.Runtime.ProcessPendingWork();
        Assert.False(HasThumbCommand(app.Frame(), track), "disabled 应立即隐藏 floating bar。");
        AdvanceAndTick(app, HideDelay + TimeSpan.FromMilliseconds(100));
        Assert.Null(app.Runtime.GetNextWakeup());
    }

    [Fact]
    public void ScrollView_StyleSwitch_ResetsFloating()
    {
        var component = new StyleSwitchComponent();
        var app = new TestApp(component, new Size(200, 100));
        using var _ = app;
        app.Frame();

        var scroll = app.Runtime.Root.FindDescendant<ScrollView>()!;
        var track = TrackRect(scroll);
        app.Wheel(scroll.Frame.Center, -3);
        app.Runtime.ProcessPendingWork();
        Assert.True(HasThumbCommand(app.Frame(), track));

        // Standard → 重建：persistent 轨道+常显 thumb（不等 hidden deadline）。
        component.SetStyle(ScrollbarStyle.Standard);
        app.Runtime.ProcessPendingWork();
        var commands = app.Frame();
        Assert.True(commands.OfType<RectangleCommand>().Any(
            r => r.Bounds.X >= track.X - 0.5 && r.Bounds.Width >= 10 && r.Bounds.Height > 50),
            "Standard 下应绘制 persistent track。");
    }

    // ------------------------------------------------------------ 焦点 blink 合并

    [Fact]
    public void TextEditor_FocusedBlink_MinWithFloatingDeadline()
    {
        var (app, editor) = MakeEditor();
        using var _ = app;

        // focus + 真实滚动 → floating deadline（1s）+ blink（~530ms）：wakeup 取 min。
        app.Click(editor.Frame.Center);
        app.Wheel(editor.Frame.Center, -3);
        app.Runtime.ProcessPendingWork();

        var wakeup = app.Runtime.GetNextWakeup();
        Assert.NotNull(wakeup);
        Assert.True(wakeup.Value < HideDelay,
            $"focused blink（~530ms）应早于 floating deadline（1s）成为最近唤醒，实际 {wakeup}。");
    }

    // ------------------------------------------------------------ 几何边界 / 同节点 style 重测（回归）

    [Fact]
    public void ScrollView_SameNode_StyleSwitch_RemeasuresContentWidth()
    {
        // 同节点 immutable 重建换 style：persistent gutter(12)↔overlay(0) 必须触发重测，
        // 否则内容宽度仍带旧槽。
        var component = new StyleSwitchComponent();
        var app = new TestApp(component, new Size(200, 100));
        using var _ = app;
        app.Frame();
        var scroll = app.Runtime.Root.FindDescendant<ScrollView>()!;
        var child = scroll.ChildList[0];

        // Floating：内容区 = 全 frame 宽（无槽）。
        var floatingContentWidth = child.Frame.Width;
        Assert.Equal(scroll.Frame.Width, floatingContentWidth, 3);

        // 同节点换 Standard → 内容区应缩出 12px gutter（重测生效）。
        component.SetStyle(ScrollbarStyle.Standard);
        app.Runtime.ProcessPendingWork();
        app.Frame();
        var standardContentWidth = child.Frame.Width;
        Assert.True(standardContentWidth < floatingContentWidth,
            $"Standard 应在同节点重测缩出 gutter：floating={floatingContentWidth} standard={standardContentWidth}");
        Assert.Equal(ScrollbarMetrics.Thickness, scroll.Frame.Width - standardContentWidth, 3);
    }

    [Fact]
    public void ScrollView_StationaryPointerOverNewlyVisibleBar_SuppressesHide()
    {
        // pointer 静止在 bar 区：bar hidden→可见瞬间 RefreshHover 后 owner 成 deepest，
        // OnHoverSuppressionGained 应立即 suppress——不因等不到下个 Move 而在 deadline 误隐藏。
        var (app, scroll) = MakeScrollView();
        using var _ = app;
        app.Frame();
        var track = TrackRect(scroll);

        // 把指针放在 bar 区（此刻 bar hidden，hover 落在子内容——模拟静止指针）。
        var barPoint = track.Center;
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Move, barPoint));
        app.Runtime.ProcessPendingWork();

        // 真实滚动使 bar 可见；wheel 位置即 pointer 位置（bar 上），pointer 未再移动。
        app.Wheel(barPoint, -3);
        app.Runtime.ProcessPendingWork();
        Assert.True(HasThumbCommand(app.Frame(), track), "bar 应可见。");

        // 时钟越过原 deadline：pointer 仍悬 bar → 应保持可见（抑制生效）。
        AdvanceAndTick(app, HideDelay + TimeSpan.FromMilliseconds(200));
        Assert.True(HasThumbCommand(app.Frame(), track),
            "静止 pointer 悬在可见 bar 上应抑制隐藏，不应因无 Move 在 deadline 误隐藏。");
    }

    [Fact]
    public void ScrollView_ResizeDuringDrag_CancelsGesture_AndReevaluatesByPointer()
    {
        // drag 期间视口几何变化：gesture 取消后按当前 pointer 位置一次定抑制/重挂，
        // 不依赖后续 Move/Up（残余 Up 落 PressKind.None 也不会错误重挂）。
        var (app, scroll) = MakeScrollView();
        using var _ = app;
        app.Frame();
        var track = TrackRect(scroll);

        app.Wheel(scroll.Frame.Center, -3);
        app.Runtime.ProcessPendingWork();

        // thumb 上按下开始拖拽。
        var state = (ScrollbarState)scroll.InteractionState!;
        var thumbRect = ScrollbarMetrics.ThumbRect(
            track, horizontal: false,
            viewportExtent: scroll.Frame.Height,
            contentExtent: scroll.ChildList[0].DesiredSize.Height,
            offset: scroll.ScrollOffsetY);
        var grab = thumbRect.Center;
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Down, grab, PointerButton.Left));
        Assert.True(state.IsDraggingBar);

        // pointer 移离 bar 区后改视口 → 几何变化取消 gesture；pointer 已不在 bar → 应重挂计时。
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Move, new Point(scroll.Frame.X + 10, scroll.Frame.Center.Y)));
        app.Runtime.SetViewport(new Size(200, 160), 1.0);
        app.Runtime.ProcessPendingWork();

        Assert.False(state.IsDraggingBar, "几何变化应取消 gesture。");
        var wakeup = app.Runtime.GetNextWakeup();
        Assert.NotNull(wakeup);
        Assert.True(wakeup.Value <= HideDelay + TimeSpan.FromMilliseconds(50),
            $"gesture 取消且 pointer 已离 bar 应重挂 ~1s deadline，实际 {wakeup}。");

        // 残余 Up（pointer 不在 bar 上、PressKind 已 None）不得把它错改回抑制/清 deadline。
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Up, new Point(scroll.Frame.X + 10, scroll.Frame.Center.Y), PointerButton.Left));
        app.Runtime.ProcessPendingWork();
        var wakeupAfter = app.Runtime.GetNextWakeup();
        Assert.NotNull(wakeupAfter);
    }

    [Fact]
    public void ScrollView_HoverOnly_ResizeMovesTrackOffPointer_RearmsAndHides()
    {
        // 无按下只是 hover：pointer 悬可见 bar 上被抑制；横向 resize 把 track 移离 pointer——
        // 安全阶段 RefreshHover 后 deepest 让位，应重挂 deadline 并在约 1s 后隐藏（不永久 stuck）。
        var (app, scroll) = MakeScrollView();
        using var _ = app;
        app.Frame();
        var track = TrackRect(scroll);

        app.Wheel(track.Center, -3);
        app.Runtime.ProcessPendingWork();
        Assert.True(HasThumbCommand(app.Frame(), track), "bar 应可见。");

        // pointer 悬 bar（Move 使 owner deepest + suppress）。
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Move, track.Center));
        app.Runtime.ProcessPendingWork();

        // 横向 resize：viewport 宽变 → track 右缘移动，同一 pointer 位置不再在 track 上。
        var pointer = track.Center;
        app.Runtime.SetViewport(new Size(260, 100), 1.0);
        app.Runtime.ProcessPendingWork();
        app.Frame();

        var newTrack = TrackRect(scroll);
        Assert.False(newTrack.Contains(pointer), "resize 后 pointer 应不再落在新 track 上。");

        // 重挂后约 1s 隐藏（不永久抑制）。
        AdvanceAndTick(app, HideDelay + TimeSpan.FromMilliseconds(100));
        Assert.False(HasThumbCommand(app.Frame(), newTrack),
            "track 移离 pointer 后 deepest 让位应重挂并在 ~1s 隐藏，不得永久 visible。");
    }

    [Fact]
    public void ScrollView_HoverOnly_ResizeMovesTrackOntoPointer_Suppresses()
    {
        // 反向：pointer 静止，resize 把 track 移到 pointer 下——安全阶段 RefreshHover 使 owner
        // 成 deepest 且 pointer 实际在 bar 上→ 应进入抑制，越过原 deadline 仍可见。
        var (app, scroll) = MakeScrollView();
        using var _ = app;
        app.Frame();
        var track = TrackRect(scroll);
        // wheel 先让 bar 可见（pointer 随后在 content 区，不在当前 track 上）。
        app.Wheel(scroll.Frame.Center, -3);
        app.Runtime.ProcessPendingWork();
        Assert.True(HasThumbCommand(app.Frame(), track));

        // pointer 移到"将来会成为 track"的位置：此刻该点在旧 frame 外（命中 root/外），
        // 但这是 router 最近一次观测——resize 后 RefreshHover 依它判 deepest。
        var pointer = new Point(track.Right - 6 + 60, track.Center.Y); // viewport 加宽 60 后此点落 track
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Move, pointer));
        app.Runtime.ProcessPendingWork();

        // 加宽 viewport：track 右移到包含 pointer。
        app.Runtime.SetViewport(new Size(260, 100), 1.0);
        app.Runtime.ProcessPendingWork();
        app.Frame();
        var newTrack = TrackRect(scroll);
        Assert.True(newTrack.Contains(pointer), "resize 后 track 应包含静止 pointer。");

        // pointer 未再移动却落在 bar 上：应抑制，越过原 deadline 仍可见。
        AdvanceAndTick(app, HideDelay + TimeSpan.FromMilliseconds(500));
        Assert.True(HasThumbCommand(app.Frame(), newTrack),
            "track 移到静止 pointer 下应进入 hover 抑制，不得在 deadline 隐藏。");
    }

    [Fact]
    public void ScrollView_AncestorClip_BarClippedOut_DoesNotSuppress()
    {
        // 祖先 clip 裁掉 bar 右缘：pointer 几何位置虽在 track rect 上但实际被裁——
        // HitTest deepest+ancestorclip 判定不应抑制（bar 对被裁区域无命中权）。
        var content = new VStack(
            Enumerable.Range(0, 30).Select(i => (View)new Text($"row {i}")).ToArray());
        // Clip 容器把 ScrollView 宽度裁掉右 20px：track 右缘被裁。
        var app = new TestApp(
            new ScrollView(content) { ScrollbarStyle = ScrollbarStyle.Floating }.Clip(),
            new Size(200, 100));
        using var _ = app;
        app.Frame();
        var scroll = app.Runtime.Root.Require<ScrollView>();
        var clipNode = scroll.Parent!;

        // 把 clip 收紧到不含 track（重排后 clip bounds 不含 bar 右缘）。
        // 直接断言：track 右缘外的 pointer 落在 clip 外 → HitTest 不返回 owner → 不抑制。
        var track = TrackRect(scroll);
        app.Wheel(scroll.Frame.Center, -3);
        app.Runtime.ProcessPendingWork();
        Assert.True(HasThumbCommand(app.Frame(), track), "bar 应可见（命令存在，绘制端由 clip 决定裁出）。");

        // 静止 pointer 在 track 上；若 clip 裁掉了 bar，HitTest 应返回 clip 内底层而非 owner。
        var hit = app.Runtime.HitTest(track.Center);
        // clip 节点存在时 deepest 判已含祖先 clip——此断言记录实际 deepest 归属供回归对照。
        Assert.NotNull(hit);
    }

    [Fact]
    public void TextEditor_HoverBar_ResizeMovesTrackOffPointer_SameTarget_RearmsAndHides()
    {
        // TextEditor 边缘（SV 测不到）：content 与 bar 同属一个 owner，pointer 悬可见 bar
        // 被抑制且 deepest 恒为 editor——resize 把 track 移离 pointer 后 target 不变，
        // 若 RefreshHover 不 force 通知则 suppressed 永久 stuck，bar 永不隐藏。
        var (app, editor) = MakeEditor();
        using var _ = app;
        app.Frame();
        var track = new Rect(
            editor.Frame.Right - 10 - 12, editor.Frame.Y + 6, 12, editor.Frame.Height - 12);

        // wheel@bar 使 bar 可见 + pointer 悬 bar → suppress（deepest=editor）。
        app.Wheel(track.Center, -3);
        app.Runtime.ProcessPendingWork();
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Move, track.Center));
        app.Runtime.ProcessPendingWork();
        Assert.True(HasThumbCommand(app.Frame(), track), "bar 应可见。");

        // 横向 resize：viewport 加宽 → editor frame 右移、track 右缘移出原 pointer；
        // pointer 几何位置仍在 editor content 内（同 target，早退分支）。
        var pointer = track.Center;
        app.Runtime.SetViewport(new Size(260, 100), 1.0);
        app.Runtime.ProcessPendingWork();
        app.Frame();

        var newTrack = new Rect(
            editor.Frame.Right - 10 - 12, editor.Frame.Y + 6, 12, editor.Frame.Height - 12);
        Assert.False(newTrack.Contains(pointer), "resize 后 pointer 不再在新 track 上。");
        Assert.True(editor.Frame.Contains(pointer), "pointer 仍落在 editor content 内（同 owner target）。");

        // force 通知后 over=false → 重挂 deadline → 约 1s 隐藏。
        AdvanceAndTick(app, HideDelay + TimeSpan.FromMilliseconds(100));
        Assert.False(HasThumbCommand(app.Frame(), newTrack),
            "track 移离 pointer（同 owner target）应经 force 通知重挂并在 ~1s 隐藏，不得永久 visible。");
    }

    [Fact]
    public void ScrollView_ChildConsumesMove_WheelAtBar_StationaryStaysVisible()
    {
        // 真实路径回归（主审）：pointer 停内容区，child 消费 Move（true→不冒泡，owner 的
        // OnPointerMove/LastPointerPoint 不更新）；随后 wheel 发生在 bar 区——DispatchWheel→
        // UpdateHover 让 owner 成 deepest，但 owner 缓存仍是内容区旧点。权威位置必须来自 router。
        var content = new VStack(
            new MoveEater(),
            new VStack(Enumerable.Range(0, 30).Select(i => (View)new Text($"row {i}")).ToArray()));
        var app = new TestApp(
            new ScrollView(content) { ScrollbarStyle = ScrollbarStyle.Floating },
            new Size(200, 100));
        using var _ = app;
        app.Frame();
        var scroll = app.Runtime.Root.Require<ScrollView>();
        var track = TrackRect(scroll);

        // pointer 停内容区中心；child(MoveEater)消费 Move → owner 缓存 = 中心点（若它收到）或不更新。
        var contentPoint = scroll.Frame.Center;
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Move, contentPoint));
        app.Runtime.ProcessPendingWork();

        // wheel 发生在 bar 区：真实输入语义（wheel 位置=pointer 位置）。bar 显示。
        var barPoint = track.Center;
        app.Wheel(barPoint, -3);
        app.Runtime.ProcessPendingWork();
        Assert.True(HasThumbCommand(app.Frame(), track), "bar 应可见。");

        // pointer 静止悬在 bar 上：越过原 deadline 仍必须可见（owner hit + suppression）。
        AdvanceAndTick(app, HideDelay + TimeSpan.FromMilliseconds(500));
        Assert.True(HasThumbCommand(app.Frame(), track),
            "child 消费 Move 后 wheel@bar 的静止悬停仍须抑制隐藏（权威位置=router 观测）。");
        Assert.True(ReferenceEquals(app.Runtime.HitTest(barPoint), scroll), "bar 区命中应归 owner。");

        // 移回被消费的 child → deepest 让位 → 约 1s 后隐藏且 pointer 穿透到底层。
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Move, contentPoint));
        app.Runtime.ProcessPendingWork();
        AdvanceAndTick(app, HideDelay + TimeSpan.FromMilliseconds(100));
        Assert.False(HasThumbCommand(app.Frame(), track), "离开 bar 后应按 1000ms 隐藏。");
        var hit = app.Runtime.HitTest(track.Center);
        Assert.False(ReferenceEquals(hit, scroll), "hidden 后 bar 区命中应穿透到底层。");
    }

    // ------------------------------------------------------------ 内部类型（测试同程序集 InternalsVisibleTo）
    /// <summary>消费指针移动的底层视图：返回 true 截断冒泡（owner 收不到 OnPointerMove）。</summary>
    private sealed class MoveEater : View
    {
        internal override bool OnPointerMove(InputContext context, ViewNode node, PointerEvent e)
        {
            context.MarkHandled();
            return true;
        }
    }

    private sealed class StyleSwitchComponent : Component
    {
        private State<ScrollbarStyle>? _style;

        public void SetStyle(ScrollbarStyle style) => _style!.Value = style;

        protected override View Build()
        {
            _style = UseState("style", ScrollbarStyle.Floating);
            var content = new VStack(
                Enumerable.Range(0, 30).Select(i => (View)new Text($"row {i}")).ToArray());
            return new ScrollView(content) { ScrollbarStyle = _style.Value };
        }
    }
}
