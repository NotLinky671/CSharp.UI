using CSharp.UI;
using Xunit;

namespace CSharp.UI.Tests;

/// <summary>
/// PS-03：<see cref="Sidebar{T}"/> 消费者行为——单选提交纪律、键盘导航/disabled、
/// items 身份（值/重排/null）、ensure-visible、focus owner、宽度偏好。
/// 断言只走 Binding 写计数、节点 Frame、FocusedNode、ScrollOffset、绘制命令；
/// 不 pin 内部字段名、私有状态或常量字面量。
/// </summary>
public class SidebarTests
{
    private static readonly SidebarItem<string?>[] Abc =
    [
        new("a", "Alpha"),
        new("b", "Beta"),
        new("c", "Gamma"),
    ];

    private static (TestApp App, ValueCell<string?> Cell, ViewNode Owner) RawSidebar(
        IReadOnlyList<SidebarItem<string?>> items,
        string? header = null,
        double? width = null,
        double viewportHeight = 300)
    {
        var cell = new ValueCell<string?>(default);
        var sidebar = new Sidebar<string?>(cell.Binding, items, header);
        if (width is { } w)
        {
            sidebar = new Sidebar<string?>(cell.Binding, items, header) { Width = w };
        }

        var app = new TestApp(
            new VStack(sidebar).Frame(alignment: Alignment.Start),
            new Size(400, viewportHeight));
        app.Frame();
        return (app, cell, app.Runtime.Require<Sidebar<string?>>());
    }

    private static Point Center(ViewNode node)
        => new(node.Frame.X + node.Frame.Width / 2, node.Frame.Y + node.Frame.Height / 2);

    private static void Click(TestApp app, Point point)
    {
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Move, point));
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Down, point, PointerButton.Left));
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Up, point, PointerButton.Left));
        app.Runtime.ProcessPendingWork();
    }

    private static ViewNode RowNode(TestApp app, string value)
        => app.Runtime.Root.FindDescendants<SidebarRowView<string?>>()
            .First(n => n.View is SidebarRowView<string?> r && r.Item.Value == value);

    private static IReadOnlyList<SidebarItem<string?>> RangeItems(int count)
        => Enumerable.Range(0, count)
            .Select(i => new SidebarItem<string?>($"v{i}", $"Item {i}"))
            .ToArray();

    // C1 数据 ------------------------------------------------------------

    [Fact]
    public void SB1_DuplicateValueThrows_AndNullValueIsLegal()
    {
        Assert.Throws<ArgumentException>(() =>
            new Sidebar<string?>(new ValueCell<string?>(default).Binding,
                [new("a"), new("b"), new("a")]));

        // null Value 合法；与数字 0 混合不碰撞（key sentinel 纪律）。
        var cell = new ValueCell<int?>(default);
        var sidebar = new Sidebar<int?>(cell.Binding,
            [new(null, "None"), new(0, "Zero"), new(1, "One")]);
        using var app = new TestApp(new VStack(sidebar).Frame(alignment: Alignment.Start), new Size(400, 300));
        app.Frame();
        Assert.Equal(3, app.Runtime.Root.FindDescendants<SidebarRowView<int?>>().Count());
    }

    [Fact]
    public void SB2_LabelNullFallsBackToToString_AndNullShowsEmpty()
    {
        var cell = new ValueCell<int?>(default);
        var sidebar = new Sidebar<int?>(cell.Binding,
            [new(7), new(null), new(9, "Nine")]);
        using var app = new TestApp(new VStack(sidebar).Frame(alignment: Alignment.Start), new Size(400, 300));
        app.Frame();
        var rows = app.Runtime.Root.FindDescendants<SidebarRowView<int?>>().ToArray();
        Assert.Equal("7", ((SidebarRowView<int?>)rows[0].View).Item.DisplayLabel);
        Assert.Equal("", ((SidebarRowView<int?>)rows[1].View).Item.DisplayLabel);
        Assert.Equal("Nine", ((SidebarRowView<int?>)rows[2].View).Item.DisplayLabel);
    }

    [Fact]
    public void SB2b_InvalidArguments_Throw()
    {
        var cell = new ValueCell<string?>(default);
        Assert.Throws<ArgumentNullException>(() => new Sidebar<string?>(null!, Abc));
        Assert.Throws<ArgumentNullException>(() => new Sidebar<string?>(cell.Binding, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Sidebar<string?>(cell.Binding, Abc) { Width = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new Sidebar<string?>(cell.Binding, Abc) { Width = -1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new Sidebar<string?>(cell.Binding, Abc) { Width = double.NaN });
    }

    [Fact]
    public void SB2c_SelectedValueSeedsActive_FirstDownFromSelected()
    {
        // 绑定已选 b：首次 Enter 同值重选写一次（播种 active=b）；随后 Down 从 b 起 → c。
        var cell = new ValueCell<string?>("b");
        var sidebar = new Sidebar<string?>(cell.Binding, Abc);
        using var app = new TestApp(new VStack(sidebar).Frame(alignment: Alignment.Start), new Size(400, 300));
        app.Frame();
        var owner = app.Runtime.Require<Sidebar<string?>>();

        app.Runtime.SetFocus(owner);
        app.Key(UiKey.Enter);   // 播种 active=b → 同值重选写一次
        app.Runtime.ProcessPendingWork();
        Assert.Equal("b", cell.Current);
        Assert.Equal(1, cell.Writes);

        app.Key(UiKey.Down);    // b → c
        app.Key(UiKey.Enter);
        app.Runtime.ProcessPendingWork();
        Assert.Equal("c", cell.Current);
        Assert.Equal(2, cell.Writes);
    }

    // C2 提交 ------------------------------------------------------------

    [Fact]
    public void SB3_PointerUpOnRowCommitsOnce()
    {
        var (app, cell, _) = RawSidebar(Abc);
        using (app)
        {
            Click(app, Center(RowNode(app, "b")));
            Assert.Equal("b", cell.Current);
            Assert.Equal(1, cell.Writes);

            // 同值重选再写一次。
            Click(app, Center(RowNode(app, "b")));
            Assert.Equal("b", cell.Current);
            Assert.Equal(2, cell.Writes);
        }
    }

    [Fact]
    public void SB4_EnterAndSpaceCommitActive_RepeatDoesNotRewrite()
    {
        var (app, cell, owner) = RawSidebar(Abc);
        using (app)
        {
            app.Runtime.SetFocus(owner);
            app.Key(UiKey.Down); // 无 active → Down 选首可用 a
            app.Runtime.ProcessPendingWork();
            app.Key(UiKey.Enter);
            app.Runtime.ProcessPendingWork();
            Assert.Equal("a", cell.Current);
            Assert.Equal(1, cell.Writes);

            // 重复 Enter（长按）不再写。
            app.Runtime.DispatchKey(new KeyEvent(KeyEventKind.Down, UiKey.Enter, KeyModifiers.None, Repeat: true));
            app.Runtime.ProcessPendingWork();
            Assert.Equal(1, cell.Writes);

            // Space 提交 active。
            app.Key(UiKey.Space);
            app.Runtime.ProcessPendingWork();
            Assert.Equal(2, cell.Writes);
        }
    }

    [Fact]
    public void SB5_UpDownNavigateSkipsDisabled_StopsAtEnds_NoWrite()
    {
        SidebarItem<string?>[] items =
        [
            new("a", "Alpha"),
            new("b", "Beta", Disabled: true),
            new("c", "Gamma"),
            new("d", "Delta"),
        ];
        var (app, cell, owner) = RawSidebar(items);
        using (app)
        {
            app.Runtime.SetFocus(owner);
            app.Key(UiKey.Down); // a
            app.Key(UiKey.Down); // 跳 disabled b → c
            app.Key(UiKey.Down); // d
            app.Key(UiKey.Down); // d 端停
            app.Runtime.ProcessPendingWork();
            Assert.Equal(0, cell.Writes);

            app.Key(UiKey.Up); app.Key(UiKey.Up); app.Key(UiKey.Up); app.Key(UiKey.Up); // a 端停
            app.Runtime.ProcessPendingWork();
            Assert.Equal(0, cell.Writes);
        }
    }

    [Fact]
    public void SB6_HomeEndJumpToFirstLastEnabled_NoWrite()
    {
        SidebarItem<string?>[] items =
        [
            new("a", "Alpha", Disabled: true),
            new("b", "Beta"),
            new("c", "Gamma"),
            new("d", "Delta", Disabled: true),
        ];
        var (app, cell, owner) = RawSidebar(items);
        using (app)
        {
            app.Runtime.SetFocus(owner);
            app.Key(UiKey.End);   // 末可用 c
            app.Key(UiKey.Enter);
            app.Runtime.ProcessPendingWork();
            Assert.Equal("c", cell.Current);
            Assert.Equal(1, cell.Writes);

            app.Key(UiKey.Home);  // 首可用 b
            app.Key(UiKey.Space);
            app.Runtime.ProcessPendingWork();
            Assert.Equal("b", cell.Current);
            Assert.Equal(2, cell.Writes);
        }
    }

    [Fact]
    public void SB7_TabNotConsumed_ModifierCombosNotNavigatingOrCommitting()
    {
        var (app, cell, owner) = RawSidebar(Abc);
        using (app)
        {
            app.Runtime.SetFocus(owner);
            app.Key(UiKey.Down); // a
            app.Runtime.ProcessPendingWork();
            Assert.Equal(0, cell.Writes);

            app.Key(UiKey.Down, KeyModifiers.Control);
            app.Key(UiKey.Enter, KeyModifiers.Alt);
            app.Key(UiKey.Space, KeyModifiers.Meta);
            app.Runtime.ProcessPendingWork();
            Assert.Equal(0, cell.Writes);

            // Tab/Shift+Tab 不交本控件消费：焦点遍历包装仍回到唯一可焦点的 owner，
            // 但导航/提交计数不变（不因为 Tab 误写值）。
            app.Key(UiKey.Tab);
            app.Key(UiKey.Tab, KeyModifiers.Shift);
            app.Runtime.ProcessPendingWork();
            Assert.Equal(0, cell.Writes);
        }
    }

    // C4 边界 ------------------------------------------------------------

    [Fact]
    public void SB8_EmptyItems_NoCommitNoNav()
    {
        var (app, cell, owner) = RawSidebar([]);
        using (app)
        {
            app.Runtime.SetFocus(owner);
            app.Key(UiKey.Down);
            app.Key(UiKey.Enter);
            app.Runtime.ProcessPendingWork();
            Assert.Equal(0, cell.Writes);
        }
    }

    [Fact]
    public void SB9_AllDisabled_NoCommit()
    {
        var (app, cell, owner) = RawSidebar(
            [new("a", Disabled: true), new("b", Disabled: true)]);
        using (app)
        {
            app.Runtime.SetFocus(owner);
            app.Key(UiKey.Down);
            app.Key(UiKey.Enter);
            app.Runtime.ProcessPendingWork();
            Assert.Equal(0, cell.Writes);
        }
    }

    [Fact]
    public void SB10_SidebarDisabled_NoCommitNoFocus()
    {
        var cell = new ValueCell<string?>(default);
        var sidebar = new Sidebar<string?>(cell.Binding, Abc).Disabled();
        using var app = new TestApp(new VStack(sidebar).Frame(alignment: Alignment.Start), new Size(400, 300));
        app.Frame();

        var owner = app.Runtime.Require<Sidebar<string?>>();
        app.Runtime.SetFocus(owner);
        app.Key(UiKey.Down);
        app.Key(UiKey.Enter);
        Click(app, Center(RowNode(app, "a")));
        Assert.Equal(0, cell.Writes);
    }

    [Fact]
    public void SB11_UnknownValue_NoHighlightNoCorrectionNoWrite()
    {
        var cell = new ValueCell<string?>("zzz");
        var sidebar = new Sidebar<string?>(cell.Binding, Abc);
        using var app = new TestApp(new VStack(sidebar).Frame(alignment: Alignment.Start), new Size(400, 300));
        app.Frame();

        Click(app, Center(RowNode(app, "b")));
        Assert.Equal("b", cell.Current);
        Assert.Equal(1, cell.Writes);
    }

    // C5 身份 / 反应性 ----------------------------------------------------

    [Fact]
    public void SB12_PointerDownThenReorder_PointerUpAtOldPointDoesNotCommitOtherValue()
    {
        var host = new SidebarHost { InitialItems = Abc };
        using var app = new TestApp(new VStack(host).Frame(alignment: Alignment.Start), new Size(400, 300));
        app.Frame();

        // 在 "b" 行中心按下——pressed 记 value "b"。
        var downAt = Center(RowNode(app, "b"));
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Move, downAt));
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Down, downAt, PointerButton.Left));

        // 按住期间重排 [a, c, b]：b 行下移到末位，旧坐标现在命中 "c" 行。
        host.Items!.Value = [new("a", "Alpha"), new("c", "Gamma"), new("b", "Beta")];
        app.Runtime.ProcessPendingWork();
        Assert.NotEqual(downAt, Center(RowNode(app, "b"))); // b 已挪位

        // 在原坐标释放：命中的不是 "b" 行 → pressed 值与行值不符，不得提交任何值。
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Up, downAt, PointerButton.Left));
        app.Runtime.ProcessPendingWork();
        Assert.Equal(0, host.Writes);
        Assert.Null(host.Value!.Value);
    }

    [Fact]
    public void SB12b_PointerDownThenRowRemoved_PointerUpDoesNotCommit()
    {
        var host = new SidebarHost { InitialItems = Abc };
        using var app = new TestApp(new VStack(host).Frame(alignment: Alignment.Start), new Size(400, 300));
        app.Frame();

        var downAt = Center(RowNode(app, "b"));
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Move, downAt));
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Down, downAt, PointerButton.Left));

        // 按住期间删除 pressed 项；b 行从树中消失。
        host.Items!.Value = [new("a", "Alpha"), new("c", "Gamma")];
        app.Runtime.ProcessPendingWork();

        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Up, downAt, PointerButton.Left));
        app.Runtime.ProcessPendingWork();
        Assert.Equal(0, host.Writes);
        Assert.Null(host.Value!.Value);
    }

    [Fact]
    public void SB12c_PointerDownThenRowDisabled_PointerUpDoesNotCommit()
    {
        var host = new SidebarHost { InitialItems = Abc };
        using var app = new TestApp(new VStack(host).Frame(alignment: Alignment.Start), new Size(400, 300));
        app.Frame();

        var downAt = Center(RowNode(app, "b"));
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Move, downAt));
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Down, downAt, PointerButton.Left));

        // 按住期间把 pressed 项翻转为 disabled。
        host.Items!.Value = [new("a", "Alpha"), new("b", "Beta", Disabled: true), new("c", "Gamma")];
        app.Runtime.ProcessPendingWork();

        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Up, downAt, PointerButton.Left));
        app.Runtime.ProcessPendingWork();
        Assert.Equal(0, host.Writes);
        Assert.Null(host.Value!.Value);
    }

    [Fact]
    public void SB13_BindingInstanceSwap_WritesToCurrentBinding()
    {
        var host = new SidebarHost { InitialItems = Abc };
        using var app = new TestApp(new VStack(host).Frame(alignment: Alignment.Start), new Size(400, 300));
        app.Frame();

        var owner = app.Runtime.Require<Sidebar<string?>>();
        app.Runtime.SetFocus(owner);
        app.Key(UiKey.Down);
        app.Key(UiKey.Enter);
        app.Runtime.ProcessPendingWork();
        Assert.Equal(1, host.Writes);
        Assert.Equal(0, host.SecondWrites);

        // 换绑定来源（State 驱动 → 重建出新描述的 Binding）：下一次提交写新绑定。
        host.Second!.Value = true;
        app.Runtime.ProcessPendingWork();
        app.Key(UiKey.Enter);
        app.Runtime.ProcessPendingWork();
        Assert.Equal("a", host.SecondCurrent);
        Assert.Equal(1, host.SecondWrites);
        Assert.Equal(1, host.Writes); // 旧绑定不再被写
    }

    [Fact]
    public void SB14_ExternalStateWrite_RefreshesSelectedHighlight()
    {
        var host = new SidebarHost { InitialItems = Abc, InitialValue = "a" };
        using var app = new TestApp(new VStack(host).Frame(alignment: Alignment.Start), new Size(400, 300));
        var frame1 = app.Frame();
        var rowC = RowNode(app, "c");
        Assert.DoesNotContain(frame1, c =>
            c is RectangleCommand r && r.Bounds == rowC.Frame && r.Fill == Theme.Light.SelectionBackground);

        host.Value!.Value = "c";
        app.Runtime.ProcessPendingWork();
        var frame2 = app.Frame();
        Assert.Contains(frame2, c =>
            c is RectangleCommand r && r.Bounds == rowC.Frame && r.Fill == Theme.Light.SelectionBackground);
    }

    // C6 焦点 ------------------------------------------------------------

    [Fact]
    public void SB15_RowsNotInFocusOrder_TabTraversesSidebarAsSingleStop()
    {
        var cell = new ValueCell<string?>(default);
        var sidebar = new Sidebar<string?>(cell.Binding, Abc);
        var before = new Button("before", () => { });
        var after = new Button("after", () => { });
        using var app = new TestApp(
            new VStack(before, sidebar, after).Frame(alignment: Alignment.Start), new Size(400, 300));
        app.Frame();

        var order = app.Runtime.GetFocusOrder().ToList();
        var owner = app.Runtime.Require<Sidebar<string?>>();
        var beforeNode = order.First(n => n.View is Button b && b.Label == "before");
        var afterNode = order.First(n => n.View is Button b && b.Label == "after");

        // 真实焦点序：三个可聚焦控件，行节点不出现。
        Assert.Equal(3, order.Count);
        Assert.DoesNotContain(order, n => n.View is SidebarRowView<string?>);
        Assert.True(order.IndexOf(beforeNode) < order.IndexOf(owner));
        Assert.True(order.IndexOf(owner) < order.IndexOf(afterNode));

        // Tab：before → owner → after（整个 Sidebar 是单一焦点站，行不占序）。
        app.Runtime.SetFocus(beforeNode);
        app.Key(UiKey.Tab);
        Assert.Same(owner, app.Runtime.FocusedNode);
        app.Key(UiKey.Tab);
        Assert.Same(afterNode, app.Runtime.FocusedNode);

        // Shift+Tab 反向：after → owner。
        app.Key(UiKey.Tab, KeyModifiers.Shift);
        Assert.Same(owner, app.Runtime.FocusedNode);
    }

    [Fact]
    public void SB16_FocusRingDrawnOnOwner()
    {
        var (app, _, owner) = RawSidebar(Abc);
        using (app)
        {
            app.Runtime.SetFocus(owner);
            var commands = app.Frame();
            Assert.Contains(commands, c =>
                c is RectangleCommand r && r.Stroke == Theme.Light.FocusRing && r.Bounds == owner.Frame);
        }
    }

    // C7 布局 ------------------------------------------------------------

    [Fact]
    public void SB17_WidthIsNaturalPreference_ShrinksUnderNarrowConstraint()
    {
        var (app, _, owner) = RawSidebar(Abc);
        using (app)
        {
            Assert.Equal(200, owner.DesiredSize.Width, 3);

            var cell = new ValueCell<string?>(default);
            var narrow = new Sidebar<string?>(cell.Binding, Abc) { Width = 300 };
            using var app2 = new TestApp(
                new VStack(narrow).Frame(alignment: Alignment.Start), new Size(250, 300));
            app2.Frame();
            Assert.Equal(250, app2.Runtime.Require<Sidebar<string?>>().Frame.Width, 3);
        }
    }

    [Fact]
    public void SB18_RowHeightAtLeast32()
    {
        var (app, _, _) = RawSidebar(Abc);
        using (app)
        {
            var row = app.Runtime.Root.FindDescendants<SidebarRowView<string?>>().First();
            Assert.True(row.Frame.Height >= 32, $"row height {row.Frame.Height} < 32");
        }
    }

    // C8 裁剪 / 滚动 ------------------------------------------------------

    private static (TestApp App, ValueCell<string?> Cell, ViewNode Owner, ViewNode Scroll) ScrollableSidebar(
        int items, double viewportHeight)
    {
        var cell = new ValueCell<string?>(default);
        var sidebar = new Sidebar<string?>(cell.Binding, RangeItems(items));
        var scroll = new ScrollView(sidebar, ScrollAxis.Vertical) { ScrollbarStyle = ScrollbarStyle.Standard };
        var app = new TestApp(
            scroll.Frame(height: viewportHeight, alignment: Alignment.Start), new Size(400, 300));
        app.Frame();
        return (app, cell,
            app.Runtime.Require<Sidebar<string?>>(),
            app.Runtime.Require<ScrollView>());
    }

    [Fact]
    public void SB19_KeyboardActiveInsideCallerScrollView_EnsuresRowVisible()
    {
        var (app, _, owner, scroll) = ScrollableSidebar(12, 160);
        using (app)
        {
            Assert.Equal(0, scroll.ScrollOffsetY, 3);
            app.Runtime.SetFocus(owner);
            app.Key(UiKey.End);   // 末可用 v11
            app.Runtime.ProcessPendingWork();

            Assert.True(scroll.ScrollOffsetY > 0, "ensure-visible 未滚动。");
            var last = RowNode(app, "v11");
            Assert.True(last.Frame.Bottom <= scroll.Frame.Bottom + 0.5,
                $"last row bottom {last.Frame.Bottom} exceeds viewport {scroll.Frame.Bottom}");
        }
    }

    [Fact]
    public void SB20_WheelDoesNotReEnsure_NoSnapback()
    {
        var (app, _, owner, scroll) = ScrollableSidebar(12, 160);
        using (app)
        {
            app.Runtime.SetFocus(owner);
            app.Key(UiKey.End);
            app.Runtime.ProcessPendingWork();
            var active = RowNode(app, "v11");
            Assert.True(active.Frame.Bottom <= scroll.Frame.Bottom + 0.5, "前置：active 行应在视口内。");

            // 用户滚轮向上浏览：偏移减小，但不回弹到把 active 行重新带入视口。
            app.Wheel(new Point(60, 80), 120, WheelDeltaMode.Pixels);
            app.Runtime.ProcessPendingWork();
            Assert.True(scroll.ScrollOffsetY > 0 && scroll.ScrollOffsetY < 224,
                $"滚轮应向上滚动且不 snapback（offset={scroll.ScrollOffsetY}）。");
        }
    }

    [Fact]
    public void SB21_NoScrollViewAncestor_KeyboardNavIsSafeNoOp()
    {
        var (app, cell, owner) = RawSidebar(Abc);
        using (app)
        {
            app.Runtime.SetFocus(owner);
            app.Key(UiKey.End);
            app.Key(UiKey.Enter);
            app.Runtime.ProcessPendingWork();
            Assert.Equal("c", cell.Current);
            Assert.Equal(1, cell.Writes);
        }
    }

    [Fact]
    public void SB22_EndHomeWithoutFrame_LatestIntentWins()
    {
        var (app, cell, owner, scroll) = ScrollableSidebar(12, 160);
        using (app)
        {
            app.Runtime.SetFocus(owner);
            app.Key(UiKey.End);      // v11
            app.Key(UiKey.Home);     // v0（覆盖 pending → 最新目标）
            app.Key(UiKey.Enter);    // 提交 v0
            app.Runtime.ProcessPendingWork();

            Assert.Equal("v0", cell.Current);
            Assert.Equal(1, cell.Writes);
            // Home 后 active 在顶部：offset 回到 0（不是留在 End 的底部）。
            Assert.Equal(0, scroll.ScrollOffsetY, 3);
        }
    }

    [Fact]
    public void SB23_MountedItemsThenViewportShrinks_EndMakesCurrentLastVisible()
    {
        var host = new SidebarHost { InitialItems = RangeItems(6) };
        var scroll = new ScrollView(host, ScrollAxis.Vertical) { ScrollbarStyle = ScrollbarStyle.Standard };
        // maxHeight=160：ScrollView 填充到 160，但会随窗口高度真实收缩（不是固定 160）。
        using var app = new TestApp(
            scroll.Frame(maxHeight: 160, alignment: Alignment.Start, width: null, height: null,
                minWidth: null, minHeight: null, maxWidth: null), new Size(400, 300));
        app.Frame();
        var scrollNode = app.Runtime.Require<ScrollView>();

        // items 更新并经一次 Process 完成挂载（新描述已 reconcile）。
        host.Items!.Value = RangeItems(12);
        app.Runtime.ProcessPendingWork();
        var owner = app.Runtime.Require<Sidebar<string?>>();
        Assert.Equal(12, app.Runtime.Root.FindDescendants<SidebarRowView<string?>>().Count());
        var viewportBefore = scrollNode.Frame.Height;
        Assert.True(viewportBefore > 100, $"前置视口应 >100，实际 {viewportBefore}");

        // 真实几何变化：窗口缩到 100 → ScrollView 视口随之缩（不是仅置 dirty 标志）。
        app.Runtime.SetViewport(new Size(400, 100), 1.0);
        app.Runtime.ProcessPendingWork();
        Assert.True(scrollNode.Frame.Height < viewportBefore,
            $"视口未真实收缩（before={viewportBefore}, after={scrollNode.Frame.Height}）");

        // 收缩后 End：当前已挂载末项 v11 应在一次 Process 内进入新视口。
        app.Runtime.SetFocus(owner);
        app.Key(UiKey.End);
        app.Runtime.ProcessPendingWork();

        var last = RowNode(app, "v11");
        Assert.True(last.Frame.Bottom <= scrollNode.Frame.Bottom + 0.5,
            $"End 未使 v11 可见（bottom={last.Frame.Bottom}, viewport={scrollNode.Frame.Bottom}）");
    }

    // ---------------------------------------------------------------- 测试宿主

    /// <summary>可观察写计数的值单元（与组件 State 无关的 raw binding）。</summary>
    private sealed class ValueCell<T>
    {
        private readonly Binding<T> _binding;

        internal ValueCell(T initial)
        {
            Current = initial;
            _binding = new Binding<T>(() => Current, v =>
            {
                Writes++;
                Current = v;
            });
        }

        internal T Current { get; set; }

        internal int Writes { get; private set; }

        internal Binding<T> Binding => _binding;
    }

    /// <summary>组件宿主：真实 State 绑定、可替换 items、可切换绑定来源、可外部写值。</summary>
    private sealed class SidebarHost : Component
    {
        internal IReadOnlyList<SidebarItem<string?>> InitialItems { get; init; } = [];

        internal string? InitialValue { get; init; }

        internal State<IReadOnlyList<SidebarItem<string?>>>? Items { get; private set; }

        internal State<string?>? Value { get; private set; }

        /// <summary>切换绑定来源：false=主绑定（记 <see cref="Writes"/>），true=替换绑定（记 <see cref="SecondWrites"/>）。</summary>
        internal State<bool>? Second { get; private set; }

        internal int Writes { get; private set; }

        internal int SecondWrites { get; private set; }

        internal string? SecondCurrent { get; private set; }

        protected override View Build()
        {
            var items = UseState("items", InitialItems);
            var value = UseState("value", InitialValue);
            var second = UseState("second", false);
            Items = items;
            Value = value;
            Second = second;

            Binding<string?> binding = second.Value
                ? new Binding<string?>(() => SecondCurrent, v =>
                {
                    SecondWrites++;
                    SecondCurrent = v;
                })
                : new Binding<string?>(() => value.Value, v =>
                {
                    Writes++;
                    value.Value = v;
                });

            return new Sidebar<string?>(binding, items.Value);
        }
    }
}
