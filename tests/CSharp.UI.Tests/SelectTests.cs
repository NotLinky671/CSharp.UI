using CSharp.UI;
using Xunit;

namespace CSharp.UI.Tests;

/// <summary>
/// P-05：<see cref="Select{T}"/> 消费者行为（打开/键选/提交/取消/disabled/placeholder/限高滚动/回调次数）。
/// 断言只走可观察面：Binding 写计数、弹层挂载与 Frame、trigger 排版文本（实际绘制输入）、FocusedNode、
/// ScrollOffset；不 pin 内部行视图类型、私有字段或默认常量字面量。
/// </summary>
public class SelectTests
{
    private static readonly SelectItem<string?>[] Abc =
    [
        new("a", "Alpha"),
        new("b", "Beta"),
        new("c", "Gamma"),
    ];

    // E1 / E11 ------------------------------------------------------------

    [Fact]
    public void S1_PointerUpOpensAndRowClickCommitsExactlyOnce()
    {
        var (app, cell, trigger) = RawSelect(Abc);
        using (app)
        {
            Assert.Equal("choose", TriggerText(trigger));

            var point = Center(trigger.Frame);
            app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Move, point));
            app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Down, point, PointerButton.Left));
            app.Runtime.ProcessPendingWork();
            Assert.False(app.Runtime.Overlay.HasActiveEntry); // Down 本身不打开

            app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Up, point, PointerButton.Left));
            app.Runtime.ProcessPendingWork();
            Assert.True(app.Runtime.Overlay.HasActiveEntry);
            Assert.Equal(0, cell.Writes);

            var popup = app.Runtime.Overlay.ActiveEntry!.PopupNode!;
            ClickRow(app, popup, 1, Abc.Length);
            app.Runtime.ProcessPendingWork();

            Assert.Equal("b", cell.Current);
            Assert.Equal(1, cell.Writes);
            Assert.False(app.Runtime.Overlay.HasActiveEntry);
            Assert.Equal("Beta", TriggerText(trigger));
            Assert.Same(trigger, app.Runtime.FocusedNode);
        }
    }

    // E2 ------------------------------------------------------------------

    [Fact]
    public void S2_KeyboardNavigationSkipsDisabledAndCommitsActive()
    {
        SelectItem<string?>[] items =
        [
            new("a", "Alpha"),
            new("b", "Beta", Disabled: true),
            new("c", "Gamma"),
            new("d", "Delta"),
        ];
        var (app, cell, trigger) = RawSelect(items);
        using (app)
        {
            app.Runtime.SetFocus(trigger);
            app.Key(UiKey.Space);
            app.Runtime.ProcessPendingWork();
            Assert.True(app.Runtime.Overlay.HasActiveEntry);

            // 初始 active = 首个可用项；Down 跳过 disabled。
            app.Key(UiKey.Down);
            Assert.Equal(0, cell.Writes);
            app.Key(UiKey.Enter);
            app.Runtime.ProcessPendingWork();

            Assert.Equal("c", cell.Current);
            Assert.Equal(1, cell.Writes);
            Assert.False(app.Runtime.Overlay.HasActiveEntry);
            Assert.Same(trigger, app.Runtime.FocusedNode);

            // Home / End 定位首末可用项。
            app.Key(UiKey.Space);
            app.Runtime.ProcessPendingWork();
            app.Key(UiKey.End);
            app.Key(UiKey.Enter);
            app.Runtime.ProcessPendingWork();
            Assert.Equal("d", cell.Current);
            Assert.Equal(2, cell.Writes);
        }
    }

    [Fact]
    public void S3_KeyRepeatDoesNotOpenThenImmediatelyCommit()
    {
        var (app, cell, trigger) = RawSelect(Abc);
        using (app)
        {
            app.Runtime.SetFocus(trigger);
            app.Runtime.DispatchKey(new KeyEvent(KeyEventKind.Down, UiKey.Enter));
            app.Runtime.ProcessPendingWork();
            Assert.True(app.Runtime.Overlay.HasActiveEntry);

            // 同一次物理按键的重复 Down 不得提交。
            app.Runtime.DispatchKey(new KeyEvent(KeyEventKind.Down, UiKey.Enter, Repeat: true));
            app.Runtime.DispatchKey(new KeyEvent(KeyEventKind.Down, UiKey.Enter, Repeat: true));
            app.Runtime.ProcessPendingWork();
            Assert.True(app.Runtime.Overlay.HasActiveEntry);
            Assert.Equal(0, cell.Writes);

            // KeyUp 不提交；新的按键才提交。
            app.Runtime.DispatchKey(new KeyEvent(KeyEventKind.Up, UiKey.Enter));
            Assert.Equal(0, cell.Writes);
            app.Runtime.DispatchKey(new KeyEvent(KeyEventKind.Down, UiKey.Enter));
            app.Runtime.ProcessPendingWork();
            Assert.Equal("a", cell.Current);
            Assert.Equal(1, cell.Writes);
        }
    }

    // E3 / E12 ------------------------------------------------------------

    [Fact]
    public void S4_EscapeClosesWithoutWritingAndKeepsFocusOnTrigger()
    {
        var (app, cell, trigger) = RawSelect(Abc);
        using (app)
        {
            app.Runtime.SetFocus(trigger);
            app.Key(UiKey.Space);
            app.Runtime.ProcessPendingWork();
            Assert.True(app.Runtime.Overlay.HasActiveEntry);

            app.Key(UiKey.Escape);
            app.Runtime.ProcessPendingWork();

            Assert.False(app.Runtime.Overlay.HasActiveEntry);
            Assert.Equal(0, cell.Writes);
            Assert.Null(cell.Current);
            Assert.Same(trigger, app.Runtime.FocusedNode);
        }
    }

    // E4 ------------------------------------------------------------------

    [Fact]
    public void S5_TabClosesWithoutConsumingAndPopupAddsNoFocusTargets()
    {
        var cell = new ValueCell<string?>(null);
        var select = new Select<string?>(cell.Binding, Abc, "choose");
        var after = new Button("after", () => { });
        using var app = new TestApp(
            new VStack(select, after, new Spacer().Frame(height: 160)),
            new Size(360, 300));

        var trigger = app.Runtime.Root.FindDescendant<Select<string?>>()!;
        var orderBefore = app.Runtime.GetFocusOrder();
        app.Runtime.SetFocus(trigger);
        app.Key(UiKey.Space);
        app.Runtime.ProcessPendingWork();
        Assert.True(app.Runtime.Overlay.HasActiveEntry);

        // 弹层行不进入 Tab 序列：打开前后焦点序一致。
        Assert.Equal(orderBefore, app.Runtime.GetFocusOrder());

        app.Key(UiKey.Tab);
        app.Runtime.ProcessPendingWork();

        Assert.False(app.Runtime.Overlay.HasActiveEntry);
        Assert.Equal(0, cell.Writes);
        Assert.NotSame(trigger, app.Runtime.FocusedNode);
        Assert.True(app.Runtime.FocusedNode!.View is Button, "Tab 应移到主树的下一个 focusable。");
    }

    // E5 / E7 / E9 --------------------------------------------------------

    [Fact]
    public void S6_MissingValueShowsPlaceholder_AllDisabledShowsLabelButDoesNotOpen()
    {
        var (app, cell, trigger) = RawSelect(Abc, initial: "missing");
        using (app)
        {
            Assert.Equal("choose", TriggerText(trigger));
            app.Runtime.SetFocus(trigger);
            app.Key(UiKey.Space);
            app.Key(UiKey.Down);
            app.Runtime.ProcessPendingWork();
            Assert.True(app.Runtime.Overlay.HasActiveEntry);
            app.Key(UiKey.Escape);
            app.Runtime.ProcessPendingWork();
            Assert.Equal(0, cell.Writes);
            Assert.Equal("missing", cell.Current);
        }

        // 全 disabled：当前值仍回显该项 Label，但打不开（pointer / 键盘都不开）。
        SelectItem<string?>[] allDisabled =
        [
            new("a", "Alpha", Disabled: true),
            new("b", "Beta", Disabled: true),
        ];
        var (app2, cell2, trigger2) = RawSelect(allDisabled, initial: "a");
        using (app2)
        {
            Assert.Equal("Alpha", TriggerText(trigger2));

            var point = Center(trigger2.Frame);
            app2.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Down, point, PointerButton.Left));
            app2.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Up, point, PointerButton.Left));
            app2.Runtime.SetFocus(trigger2);
            app2.Key(UiKey.Space);
            app2.Key(UiKey.Down);
            app2.Runtime.ProcessPendingWork();

            Assert.False(app2.Runtime.Overlay.HasActiveEntry);
            Assert.Equal(0, cell2.Writes);
        }

        // 空列表：placeholder，不打开。
        var (app3, cell3, trigger3) = RawSelect([], initial: "x");
        using (app3)
        {
            Assert.Equal("choose", TriggerText(trigger3));
            app3.Runtime.SetFocus(trigger3);
            app3.Key(UiKey.Space);
            app3.Runtime.ProcessPendingWork();
            Assert.False(app3.Runtime.Overlay.HasActiveEntry);
            Assert.Equal(0, cell3.Writes);
        }

        // 重复值：显式抛出。
        var cell4 = new ValueCell<string?>(null);
        Assert.Throws<ArgumentException>(() => new Select<string?>(
            cell4.Binding,
            [new SelectItem<string?>("a", "One"), new SelectItem<string?>("a", "Two")]));
    }

    [Fact]
    public void S7_SameValueReselectionStillWritesOnce_NullValueIsLegal()
    {
        SelectItem<string?>[] items =
        [
            new(null, "(none)"),
            new("a", "Alpha"),
        ];
        var (app, cell, trigger) = RawSelect(items, initial: "a");
        using (app)
        {
            app.Runtime.SetFocus(trigger);
            app.Key(UiKey.Space);
            app.Runtime.ProcessPendingWork();
            var popup = app.Runtime.Overlay.ActiveEntry!.PopupNode!;
            ClickRow(app, popup, 1, items.Length); // 选回同一项 "a"
            app.Runtime.ProcessPendingWork();

            Assert.Equal("a", cell.Current);
            Assert.Equal(1, cell.Writes);
            Assert.False(app.Runtime.Overlay.HasActiveEntry);

            // null 值合法：选它写入 null 并回显其 Label。
            app.Key(UiKey.Space);
            app.Runtime.ProcessPendingWork();
            popup = app.Runtime.Overlay.ActiveEntry!.PopupNode!;
            ClickRow(app, popup, 0, items.Length);
            app.Runtime.ProcessPendingWork();

            Assert.Null(cell.Current);
            Assert.Equal(2, cell.Writes);
            Assert.Equal("(none)", TriggerText(trigger));
        }
    }

    // E6 ------------------------------------------------------------------

    [Fact]
    public void S8_DisabledItemIgnoresPointerAndKeyboard_DisabledSelectDoesNotOpen()
    {
        SelectItem<string?>[] items =
        [
            new("a", "Alpha"),
            new("b", "Beta", Disabled: true),
            new("c", "Gamma"),
        ];
        var (app, cell, trigger) = RawSelect(items);
        using (app)
        {
            app.Runtime.SetFocus(trigger);
            app.Key(UiKey.Space);
            app.Runtime.ProcessPendingWork();
            var popup = app.Runtime.Overlay.ActiveEntry!.PopupNode!;

            // 点 disabled 行：不提交、弹层保持开、焦点仍在 trigger（不能落到空白清焦）。
            ClickRow(app, popup, 1, items.Length);
            app.Runtime.ProcessPendingWork();
            Assert.Equal(0, cell.Writes);
            Assert.True(app.Runtime.Overlay.HasActiveEntry);
            Assert.Same(trigger, app.Runtime.FocusedNode);

            // 点列表滚动条槽（容器/滚动条消费）：同样不夺焦、不提交、不关。
            var gutter = new Point(popup.Frame.Right - 4, popup.Frame.Y + (popup.Frame.Height / 2));
            app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Move, gutter));
            app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Down, gutter, PointerButton.Left));
            app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Up, gutter, PointerButton.Left));
            app.Runtime.ProcessPendingWork();
            Assert.Equal(0, cell.Writes);
            Assert.True(app.Runtime.Overlay.HasActiveEntry);
            Assert.Same(trigger, app.Runtime.FocusedNode);

            // 键盘也不落到 disabled（Enter 提交的是跳过后的 active），且无需手动重新聚焦。
            app.Key(UiKey.Enter);
            app.Runtime.ProcessPendingWork();
            Assert.NotEqual("b", cell.Current);
            Assert.Equal(1, cell.Writes);
        }

        // 整体 disabled：pointer / 键盘都不开；Tab 序列不含它。
        var host = new SelectHost { InitialItems = Abc, StartDisabled = true };
        using var app2 = new TestApp(host, new Size(360, 300));
        var trigger2 = app2.Runtime.Root.FindDescendant<Select<string?>>()!;
        Assert.DoesNotContain(app2.Runtime.GetFocusOrder(), n => ReferenceEquals(n, trigger2));

        var point = Center(trigger2.Frame);
        app2.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Down, point, PointerButton.Left));
        app2.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Up, point, PointerButton.Left));
        app2.Runtime.SetFocus(trigger2);
        app2.Key(UiKey.Space);
        app2.Runtime.ProcessPendingWork();

        Assert.False(app2.Runtime.Overlay.HasActiveEntry);
        Assert.Equal(0, host.Writes);
    }

    // E8 / E10 ------------------------------------------------------------

    [Fact]
    public void S9_LongListScrollsWithinMaxHeightAndEnsureVisibleFollowsActive()
    {
        var items = Enumerable.Range(0, 30)
            .Select(i => new SelectItem<string?>($"v{i}", $"Item {i}"))
            .ToArray();
        var (app, cell, trigger) = RawSelect(items, maxHeight: 120, triggerWidth: 140);
        using (app)
        {
            app.Runtime.SetFocus(trigger);
            app.Key(UiKey.Space);
            app.Runtime.ProcessPendingWork();
            var popup = app.Runtime.Overlay.ActiveEntry!.PopupNode!;
            Assert.True(popup.Frame.Height <= 120.001, $"弹层高度应被 MaxPopupHeight 限制，实际 {popup.Frame.Height}。");
            Assert.True(popup.Frame.Width >= trigger.Frame.Width - 0.001, "弹层宽度不小于 trigger 宽度。");

            var scroll = popup.FindDescendant<ScrollView>();
            Assert.NotNull(scroll);
            Assert.True(scroll!.ScrollOffsetY <= 0.001);

            app.Key(UiKey.End);
            app.Runtime.ProcessPendingWork(); // 单次 Process 内必须收敛

            Assert.True(scroll.ScrollOffsetY > 0, "active 移到底部之外应触发内部 ensure-visible 滚动。");
            Assert.Equal(0, cell.Writes);
            Assert.True(app.Runtime.Overlay.HasActiveEntry);

            app.Key(UiKey.Enter);
            app.Runtime.ProcessPendingWork();
            Assert.Equal("v29", cell.Current);
            Assert.Equal(1, cell.Writes);
        }
    }

    [Fact]
    public void S10_NarrowViewportClampsPopupWithinUsableBounds()
    {
        var items = new[]
        {
            new SelectItem<string?>("a", "A very long option label that cannot fit the viewport"),
            new SelectItem<string?>("b", "Beta"),
        };
        var cell = new ValueCell<string?>(null);
        var select = new Select<string?>(cell.Binding, items, "choose");
        using var app = new TestApp(new VStack(select, new Spacer().Frame(height: 200)), new Size(200, 320));

        var trigger = app.Runtime.Root.FindDescendant<Select<string?>>()!;
        app.Runtime.SetFocus(trigger);
        app.Key(UiKey.Space);
        app.Runtime.ProcessPendingWork();

        var popup = app.Runtime.Overlay.ActiveEntry?.PopupNode;
        Assert.NotNull(popup);
        Assert.True(popup!.Frame.Right <= app.Runtime.ViewportSize.Width + 0.001);
        Assert.True(popup.Frame.X >= 0);
        Assert.True(popup.Frame.Height > 0);
    }

    // E9 / E13 ------------------------------------------------------------

    [Fact]
    public void S11_ExternalValueChangeUpdatesDisplayWithoutLosingOpenState()
    {
        var host = new SelectHost { InitialItems = Abc };
        using var app = new TestApp(host, new Size(360, 320));

        var trigger = app.Runtime.Root.FindDescendant<Select<string?>>()!;
        Assert.Equal("choose", TriggerText(trigger));

        app.Runtime.SetFocus(trigger);
        app.Key(UiKey.Space);
        app.Runtime.ProcessPendingWork();
        Assert.True(app.Runtime.Overlay.HasActiveEntry);

        // 外部 State 变化（不改 items/open）：经 Binding.Read 依赖驱动回显，open 不丢。
        host.Value!.Value = "b";
        app.Runtime.ProcessPendingWork();

        Assert.Equal("Beta", TriggerText(trigger));
        Assert.True(app.Runtime.Overlay.HasActiveEntry);
        Assert.Equal(0, host.Writes);

        // 同 key/类型的描述重建（State.ToBinding 新实例）后仍可提交：active 移到 "b" 再确认。
        app.Key(UiKey.Down);
        app.Key(UiKey.Enter);
        app.Runtime.ProcessPendingWork();
        Assert.Equal(1, host.Writes);
        Assert.Equal("b", host.Value.Value);
        Assert.False(app.Runtime.Overlay.HasActiveEntry);
    }

    [Fact]
    public void S12_ItemsReorderWhilePressedCommitsThePressedValueNotTheNewIndex()
    {
        var host = new SelectHost { InitialItems = Abc };
        using var app = new TestApp(host, new Size(360, 320));

        var trigger = app.Runtime.Root.FindDescendant<Select<string?>>()!;
        app.Runtime.SetFocus(trigger);
        app.Key(UiKey.Space);
        app.Runtime.ProcessPendingWork();
        var popup = app.Runtime.Overlay.ActiveEntry!.PopupNode!;

        // 在第二行（"b"）按下但不释放。
        var rowHeight = popup.Frame.Height / Abc.Length;
        var point = new Point(popup.Frame.X + 20, popup.Frame.Y + (rowHeight * 1.5));
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Move, point));
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Down, point, PointerButton.Left));

        // 打开状态下列表重排：同一位置现在是另一项（"a"）。
        host.Items!.Value =
        [
            new("b", "Beta"),
            new("a", "Alpha"),
            new("c", "Gamma"),
        ];
        app.Runtime.ProcessPendingWork();

        Assert.True(app.Runtime.Overlay.HasActiveEntry);

        // 释放仍路由到按下时的行（value 快照）：指针已不在该项上 → 不提交，更不能提交现在位于该位置的新索引项。
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Up, point, PointerButton.Left));
        app.Runtime.ProcessPendingWork();

        Assert.Equal(0, host.Writes);
        Assert.Null(host.Value!.Value);
        Assert.True(app.Runtime.Overlay.HasActiveEntry);
    }

    // E9 补充：重建后提交必须走当前绑定 --------------------------------------

    [Fact]
    public void S13_RowCommitAfterReplacementUsesCurrentBindingInstance()
    {
        var host = new SelectHost { InitialItems = Abc };
        using var app = new TestApp(host, new Size(360, 320));

        var trigger = app.Runtime.Root.FindDescendant<Select<string?>>()!;
        app.Runtime.SetFocus(trigger);
        app.Key(UiKey.Space);
        app.Runtime.ProcessPendingWork();
        Assert.True(app.Runtime.Overlay.HasActiveEntry);

        // 父组件重建：同 key/类型、新的 Binding 实例；open/active 不丢。
        host.Revision!.Value = 1;
        app.Runtime.ProcessPendingWork();
        Assert.True(app.Runtime.Overlay.HasActiveEntry);

        var popup = app.Runtime.Overlay.ActiveEntry!.PopupNode!;
        ClickRow(app, popup, 0, Abc.Length);
        app.Runtime.ProcessPendingWork();

        Assert.Equal("a", host.Value!.Value);
        Assert.Equal(1, host.WritesByGeneration.GetValueOrDefault(1));
        Assert.Equal(0, host.WritesByGeneration.GetValueOrDefault(0));
    }

    // 首次打开 / 重排后的 ensure-visible 与预算守护 -------------------------

    [Fact]
    public void S14_FirstOpenWithSelectedLastItemScrollsAndRowIsClickable()
    {
        var items = Enumerable.Range(0, 30)
            .Select(i => new SelectItem<string?>($"v{i}", $"Item {i}"))
            .ToArray();
        var (app, cell, trigger) = RawSelect(items, initial: "v29", maxHeight: 120, triggerWidth: 140);
        using (app)
        {
            app.Runtime.SetFocus(trigger);
            app.Key(UiKey.Space);
            app.Runtime.ProcessPendingWork(); // 单次 Process 内必须收敛到“末项可见”

            Assert.True(app.Runtime.Overlay.HasActiveEntry);
            var popup = app.Runtime.Overlay.ActiveEntry!.PopupNode!;
            var scroll = popup.FindDescendant<ScrollView>()!;
            Assert.True(scroll.ScrollOffsetY > 0, "首次打开已选末项应自动滚入可视区（不能停在顶部）。");

            // 无需 End 预热：弹层底部就是已选末项。
            var click = new Point(scroll.Frame.X + 20, scroll.Frame.Bottom - 4);
            app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Move, click));
            app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Down, click, PointerButton.Left));
            app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Up, click, PointerButton.Left));
            app.Runtime.ProcessPendingWork();

            Assert.Equal("v29", cell.Current);
            Assert.Equal(1, cell.Writes);
            Assert.False(app.Runtime.Overlay.HasActiveEntry);
        }
    }

    [Fact]
    public void S15_ReorderMovingActiveToLastScrollsItIntoViewWithoutChangingSelection()
    {
        var items = Enumerable.Range(0, 10)
            .Select(i => new SelectItem<string?>($"v{i}", $"Item {i}"))
            .ToArray();
        var host = new SelectHost
        {
            InitialItems = items,
            InitialValue = "v0",
            MaxPopupHeight = 100,
        };
        using var app = new TestApp(host, new Size(360, 320));

        var trigger = app.Runtime.Root.FindDescendant<Select<string?>>()!;
        app.Runtime.SetFocus(trigger);
        app.Key(UiKey.Space);
        app.Runtime.ProcessPendingWork();
        var popup = app.Runtime.Overlay.ActiveEntry!.PopupNode!;
        var scroll = popup.FindDescendant<ScrollView>()!;
        Assert.True(scroll.ScrollOffsetY <= 0.001);

        // 把 active 项（v0）移到末尾：选择不变，但必须重新滚到可见。
        host.Items!.Value = [.. items.Skip(1), items[0]];
        app.Runtime.ProcessPendingWork(); // 单次 Process 内收敛

        Assert.Equal("v0", host.Value!.Value);
        Assert.Equal(0, host.Writes);
        Assert.True(app.Runtime.Overlay.HasActiveEntry);
        Assert.True(scroll.ScrollOffsetY > 0, "重排后 active 换到末项，应重新 ensure-visible。");
    }

    [Fact]
    public void S16_OwnerInvalidatingEveryPollIsBoundedAndRecovers()
    {
        var reads = 0;
        var flipping = false;
        var binding = new Binding<string?>(() => flipping ? $"v{reads++}" : "stable", _ => { });
        var items = new[]
        {
            new SelectItem<string?>("v0", "Item 0"),
            new SelectItem<string?>("stable", "Stable"),
        };
        var select = new Select<string?>(binding, items, "choose");
        using var app = new TestApp(new VStack(select, new Spacer().Frame(height: 160)), new Size(360, 300));

        var trigger = app.Runtime.Root.FindDescendant<Select<string?>>()!;
        app.Runtime.SetFocus(trigger);
        app.Key(UiKey.Space);
        flipping = true;

        // 每轮都换值的 getter → owner 每轮请求失效：必须被单轮预算拦住（有限、报告诊断），不能无限 layout。
        var error = Assert.Throws<InvalidOperationException>(() => app.Runtime.ProcessPendingWork());
        Assert.Contains("overlay", error.Message);

        // 运行时恢复：getter 稳定后继续可用，弹层状态不丢。
        flipping = false;
        app.Runtime.ProcessPendingWork();
        Assert.True(app.Runtime.Overlay.HasActiveEntry);
        app.Key(UiKey.Escape);
        app.Runtime.ProcessPendingWork();
        Assert.False(app.Runtime.Overlay.HasActiveEntry);
    }

    // helpers -------------------------------------------------------------

    private static (TestApp App, ValueCell<string?> Cell, ViewNode Trigger) RawSelect(
        IReadOnlyList<SelectItem<string?>> items,
        string? initial = null,
        double maxHeight = 240,
        double? triggerWidth = null)
    {
        var cell = new ValueCell<string?>(initial);
        View select = new Select<string?>(cell.Binding, items, "choose") { MaxPopupHeight = maxHeight };
        if (triggerWidth is { } width)
        {
            select = select.Frame(width: width);
        }

        var app = new TestApp(new VStack(select, new Spacer().Frame(height: 200)), new Size(360, 320));
        var trigger = app.Runtime.Root.FindDescendant<Select<string?>>()!;
        return (app, cell, trigger);
    }

    private static void ClickRow(TestApp app, ViewNode popup, int index, int count)
    {
        var rowHeight = popup.Frame.Height / count;
        var point = new Point(popup.Frame.X + 20, popup.Frame.Y + (rowHeight * (index + 0.5)));
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Move, point));
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Down, point, PointerButton.Left));
        app.Runtime.DispatchPointer(new PointerEvent(PointerEventKind.Up, point, PointerButton.Left));
    }

    /// <summary>trigger 实际排版文本（绘制输入，不是私有字段）。</summary>
    private static string TriggerText(ViewNode trigger)
        => Assert.IsType<TextLayoutResult>(trigger.ViewCache).Text;

    private static Point Center(Rect rect)
        => new(rect.X + (rect.Width / 2), rect.Y + (rect.Height / 2));

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

    /// <summary>组件宿主：真实 State 绑定、可替换 items、可切换 disabled（验证依赖与状态保留）。</summary>
    private sealed class SelectHost : Component
    {
        internal IReadOnlyList<SelectItem<string?>> InitialItems { get; init; } = [];

        internal string? InitialValue { get; init; }

        internal bool StartDisabled { get; init; }

        internal double MaxPopupHeight { get; init; } = 240;

        internal State<IReadOnlyList<SelectItem<string?>>>? Items { get; private set; }

        internal State<string?>? Value { get; private set; }

        internal State<bool>? Disabled { get; private set; }

        internal State<int>? Revision { get; private set; }

        internal Dictionary<int, int> WritesByGeneration { get; } = [];

        internal int Writes { get; private set; }

        protected override View Build()
        {
            var items = UseState("items", InitialItems);
            var value = UseState("value", InitialValue);
            var disabled = UseState("disabled", StartDisabled);
            var revision = UseState("revision", 0);
            var generation = revision.Value;
            Items = items;
            Value = value;
            Disabled = disabled;
            Revision = revision;

            // ToBinding() 登记构建期依赖（外部 State 变化驱动重建）；包装绑定按“重建代际”计数写入，
            // 便于验证提交走的是当前描述的绑定而不是旧 setter。
            _ = value.ToBinding();
            var binding = new Binding<string?>(() => value.Value, v =>
            {
                Writes++;
                WritesByGeneration[generation] = WritesByGeneration.GetValueOrDefault(generation) + 1;
                value.Value = v;
            });

            return new VStack(
                new Text("main"),
                new Select<string?>(binding, items.Value, "choose")
                {
                    MaxPopupHeight = MaxPopupHeight,
                }.Disabled(disabled.Value),
                new Spacer().Frame(height: 160));
        }
    }
}
