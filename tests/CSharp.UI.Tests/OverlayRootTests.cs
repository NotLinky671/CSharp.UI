using CSharp.UI;
using Xunit;

namespace CSharp.UI.Tests;

/// <summary>
/// P-01：根层（用户根 + overlay 宿主）、单活动 entry 的挂载/资格门/锚环境/裁剪/安全阶段。
/// 断言只走可观察面（Root/Parent/Depth、HitTest、FocusedNode、绘制命令序、Environment、IsMounted），
/// 不 pin 内部字段名。
/// </summary>
public class OverlayRootTests
{
    // A1 ------------------------------------------------------------------

    [Fact]
    public void A1_RootIsUserNode_WithInternalLayerParent()
    {
        using var app = new TestApp(new ScrollView(new Text("content")), new Size(300, 200));

        // 公共可观察行为：Root 仍是用户根视图对应的节点，但父级是内部根层（Parent 非 null、Depth 为 1）。
        Assert.IsType<ScrollView>(app.Runtime.Root.View);
        Assert.NotNull(app.Runtime.Root.Parent);
        Assert.Equal(1, app.Runtime.Root.Depth);
        Assert.NotNull(app.Runtime.Root.Require<ScrollView>());
    }

    [Fact]
    public void A1_ForEachRoot_KeepsAllItemsUnderUserNode()
    {
        View root = new ForEach<int>([1, 2, 3], (i, _) => new Text($"line {i}"), (i, _) => i);
        using var app = new TestApp(root, new Size(200, 120));
        app.Frame();

        Assert.Equal(3, app.Runtime.Root.FindDescendants<Text>().Count());
        Assert.All(app.Runtime.Root.FindDescendants<Text>(), node => Assert.True(node.Frame.Height > 0));
    }

    // A2 ------------------------------------------------------------------

    [Fact]
    public void A2_ShowMountsPopupAboveMainTree_AndParticipatesInInputAndFocus()
    {
        var anchorView = new Button("anchor", () => { });
        var popupContent = new VStack(new Text("popup text"), new Button("popup action", () => { }));
        using var app = new TestApp(new VStack(new Text("main"), anchorView), new Size(400, 300));

        var anchor = app.Runtime.Root.Require<Button>();
        Assert.True(app.Runtime.Overlay.Show(anchor, popupContent));
        app.Runtime.ProcessPendingWork();

        var entry = app.Runtime.Overlay.ActiveEntry;
        Assert.NotNull(entry);
        var popup = entry!.PopupNode;
        Assert.NotNull(popup);
        Assert.True(popup!.IsMounted);
        Assert.False(popup.Frame.IsEmpty);

        // 命中：popup 内容在 popup 子树内，根层/宿主自身不命中。
        var hit = app.Runtime.HitTest(new Point(popup.Frame.X + 8, popup.Frame.Y + 8));
        Assert.NotNull(hit);
        Assert.True(IsSelfOrDescendant(popup, hit!));

        // 焦点遍历：popup 内 focusable 参与，且在用户树之后。
        var popupButton = popup.FindDescendant<Button>();
        var order = app.Runtime.GetFocusOrder();
        Assert.Contains(popupButton!, order);
        Assert.True(IndexOf(order, popupButton!) > IndexOf(order, anchor));

        // 绘制序：主树纹理先于 popup 裁剪区，popup 纹理在裁剪区内。
        var frame = app.Frame();
        var clip = frame.OfType<PushClipCommand>().Last();
        Assert.Equal(popup.Frame, clip.Bounds);
        var clipIndex = IndexOfCommand(frame, c => ReferenceEquals(c, clip));
        var textureIndices = Enumerable.Range(0, frame.Count)
            .Where(i => frame[i] is TextureCommand)
            .ToArray();
        Assert.True(textureIndices.Length >= 2);
        Assert.True(textureIndices[0] < clipIndex);
        Assert.Contains(textureIndices, i => i > clipIndex);
    }

    // A3 ------------------------------------------------------------------

    [Fact]
    public void A3_AnchorScrolledOutCloses_AndPopupEscapesAnchorScrollClip()
    {
        var lines = Enumerable.Range(0, 20).Select(i => (View)new Text($"row {i}")).ToArray();
        var scroll = new ScrollView(new VStack(lines).Spacing(4)).Frame(height: 120);
        using var app = new TestApp(new VStack(new Text("header"), scroll), new Size(320, 240));

        var scrollNode = app.Runtime.Root.Require<ScrollView>();
        var texts = app.Runtime.Root.FindDescendants<Text>()
            .Where(n => n.View is Text text && text.Content.StartsWith("row ", StringComparison.Ordinal))
            .ToArray();
        var anchor = texts.Last(n => n.Frame.Bottom <= scrollNode.Frame.Bottom - 1);
        Assert.False(anchor.Frame.IsEmpty);

        var popupContent = new Rectangle(Color.Parse("#3366FF")) { FixedSize = new Size(120, 80) };
        Assert.True(app.Runtime.Overlay.Show(anchor, popupContent));
        app.Runtime.ProcessPendingWork();

        var popup = app.Runtime.Overlay.ActiveEntry?.PopupNode;
        Assert.NotNull(popup);
        Assert.True(popup!.IsMounted);

        // popup 可用边界是视口（内缩 margin），不受锚所在 ScrollView 的 clip 限制。
        Assert.True(popup.Frame.Bottom > scrollNode.Frame.Bottom);
        Assert.True(popup.Frame.Bottom <= app.Runtime.ViewportSize.Height);

        // 部分可见：跟随锚滚动，保持打开。
        var before = popup.Frame;
        scrollNode.ScrollOffsetY = 24;
        app.Runtime.InvalidateLayout(scrollNode);
        app.Runtime.ProcessPendingWork();
        popup = app.Runtime.Overlay.ActiveEntry?.PopupNode;
        Assert.NotNull(popup);
        Assert.Equal(anchor.Frame.Bottom, popup!.Frame.Y, 3);
        Assert.True(popup.Frame.Y < before.Y);

        // 完全滚出：关闭并卸载 popup 子树。
        scrollNode.ScrollOffsetY = 400;
        app.Runtime.InvalidateLayout(scrollNode);
        app.Runtime.ProcessPendingWork();
        Assert.False(app.Runtime.Overlay.HasActiveEntry);
        Assert.False(popup.IsMounted);
    }

    // A4 ------------------------------------------------------------------

    [Fact]
    public void A4_PopupUsesAnchorCurrentEnvironment_NotSnapshot()
    {
        var host = new ThemedHost();
        using var app = new TestApp(host, new Size(300, 220));
        app.Runtime.ProcessPendingWork();

        var anchor = app.Runtime.Root.Require<Text>();
        var popupText = new Text("popup");
        Assert.True(app.Runtime.Overlay.Show(anchor, popupText));
        app.Runtime.ProcessPendingWork();

        var popupNode = app.Runtime.Overlay.ActiveEntry?.PopupNode;
        Assert.NotNull(popupNode);
        var node = popupNode!.FindDescendant<Text>();
        Assert.NotNull(node);
        Assert.Same(Theme.Light, node!.Environment.Theme);

        host.ThemeState!.Value = Theme.Dark;
        app.Runtime.ProcessPendingWork();

        Assert.Same(Theme.Dark, anchor.Environment.Theme);
        Assert.Same(Theme.Dark, node.Environment.Theme);
        Assert.True(app.Runtime.Overlay.HasActiveEntry);
    }

    // A5 ------------------------------------------------------------------

    [Fact]
    public void A5_DisabledAnchorRejectsShow_AndClosesOpenPopup()
    {
        var host = new DisableHost();
        using var app = new TestApp(host, new Size(300, 220));
        app.Runtime.ProcessPendingWork();

        var anchor = app.Runtime.Root.Require<Text>();
        Assert.False(anchor.IsEffectivelyDisabled);
        Assert.True(app.Runtime.Overlay.Show(anchor, new Text("popup")));
        app.Runtime.ProcessPendingWork();
        Assert.True(app.Runtime.Overlay.HasActiveEntry);

        host.DisabledState!.Value = true;
        app.Runtime.ProcessPendingWork();
        Assert.False(app.Runtime.Overlay.HasActiveEntry);

        // 锚已 disabled：新的打开请求被资格门拒绝。
        Assert.True(app.Runtime.Overlay.Show(anchor, new Text("again")));
        app.Runtime.ProcessPendingWork();
        Assert.False(app.Runtime.Overlay.HasActiveEntry);
    }

    // A6 ------------------------------------------------------------------

    [Fact]
    public void A6_AnchorUnmountClosesPopup_AndReleasesFocus()
    {
        var host = new AnchorHost();
        using var app = new TestApp(host, new Size(300, 220));
        app.Runtime.ProcessPendingWork();

        var anchor = app.Runtime.Root.Require<Button>();
        Assert.True(app.Runtime.Overlay.Show(anchor, new VStack(new Button("inside", () => { }))));
        app.Runtime.ProcessPendingWork();

        var popup = app.Runtime.Overlay.ActiveEntry?.PopupNode;
        Assert.NotNull(popup);
        var inside = popup!.FindDescendant<Button>();
        Assert.NotNull(inside);
        app.Runtime.SetFocus(inside);
        Assert.Same(inside, app.Runtime.FocusedNode);

        host.ShowAnchor!.Value = false;
        app.Runtime.ProcessPendingWork();

        Assert.False(app.Runtime.Overlay.HasActiveEntry);
        Assert.False(popup.IsMounted);
        Assert.Null(app.Runtime.FocusedNode);
    }

    // A7 ------------------------------------------------------------------

    [Fact]
    public void A7_WindowDeactivationClosesPopup()
    {
        using var app = new TestApp(new VStack(new Text("anchor")), new Size(300, 220));
        var anchor = app.Runtime.Root.Require<Text>();
        Assert.True(app.Runtime.Overlay.Show(anchor, new Text("popup")));
        app.Runtime.ProcessPendingWork();
        Assert.True(app.Runtime.Overlay.HasActiveEntry);

        app.Runtime.NotifyWindowActivation(false);
        app.Runtime.ProcessPendingWork();
        Assert.False(app.Runtime.Overlay.HasActiveEntry);
    }

    // A8 ------------------------------------------------------------------

    [Fact]
    public void A8_SecondShowDisplacesFirst_AndFirstOwnerWaitsForEdge()
    {
        using var app = new TestApp(new VStack(new Text("first"), new Text("second")), new Size(300, 240));
        var texts = app.Runtime.Root.FindDescendants<Text>().ToArray();
        var first = texts[0];
        var second = texts[1];

        Assert.True(app.Runtime.Overlay.Show(first, new Text("one"), owner: first));
        app.Runtime.ProcessPendingWork();
        var firstPopup = app.Runtime.Overlay.ActiveEntry?.PopupNode;
        Assert.NotNull(firstPopup);

        Assert.True(app.Runtime.Overlay.Show(second, new Text("two"), owner: second));
        app.Runtime.ProcessPendingWork();

        Assert.Equal(second, app.Runtime.Overlay.ActiveEntry?.AnchorNode);
        Assert.False(firstPopup!.IsMounted);
        Assert.True(app.Runtime.Overlay.IsOwnerAwaitingOpenEdge(first));
        Assert.False(app.Runtime.Overlay.IsOwnerAwaitingOpenEdge(second));

        // 无每帧竞争：重复处理不改变活动 entry。
        app.Runtime.ProcessPendingWork();
        app.Runtime.ProcessPendingWork();
        Assert.Equal(second, app.Runtime.Overlay.ActiveEntry?.AnchorNode);
    }

    // A9 ------------------------------------------------------------------

    [Fact]
    public void A9_NoEntry_HostIsSkippedByHitTestAndFocusTraversal()
    {
        var background = new Rectangle(Color.Parse("#112233"));
        using var app = new TestApp(background, new Size(200, 120));
        app.Frame();

        var hit = app.Runtime.HitTest(new Point(50, 50));
        Assert.Same(app.Runtime.Root, hit);
        Assert.Empty(app.Runtime.GetFocusOrder());
    }

    // A10 -----------------------------------------------------------------

    [Fact]
    public void A10_PopupContentIsClippedToFinalFrame()
    {
        var content = new VStack(
            new Text("one"),
            new Text("two"),
            new Text("three"),
            new Text("four")).Spacing(40);
        using var app = new TestApp(new VStack(new Text("anchor")), new Size(300, 260));
        var anchor = app.Runtime.Root.Require<Text>();

        Assert.True(app.Runtime.Overlay.Show(anchor, content, maxHeight: 50));
        app.Runtime.ProcessPendingWork();

        var popup = app.Runtime.Overlay.ActiveEntry?.PopupNode;
        Assert.NotNull(popup);
        Assert.Equal(50, popup!.Frame.Height, 3);

        var texts = popup.FindDescendants<Text>().ToArray();
        Assert.True(texts[^1].Frame.Bottom > popup.Frame.Bottom);

        var frame = app.Frame();
        var clip = frame.OfType<PushClipCommand>().Last();
        Assert.Equal(popup.Frame, clip.Bounds);
        var popIndex = IndexOfCommand(frame, c => c is PopClipCommand);
        Assert.True(popIndex > IndexOfCommand(frame, c => ReferenceEquals(c, clip)));

        // 超出最终 Frame 的内容不命中（clip 约束命中测试）。
        var overflowPoint = new Point(popup.Frame.X + 2, popup.Frame.Bottom + 6);
        var hit = app.Runtime.HitTest(overflowPoint);
        Assert.False(hit is not null && IsSelfOrDescendant(popup, hit));
    }

    // A11 -----------------------------------------------------------------

    [Fact]
    public void A11_ShowInsideLayout_IsDeferredAndConverges()
    {
        var view = new LayoutShowHost { PopupContent = new Rectangle(Color.Parse("#00AA55")) { FixedSize = new Size(50, 30) } };
        using var app = new TestApp(view, new Size(300, 200));

        app.Runtime.ProcessPendingWork();

        Assert.True(view.ShowAttempted);
        Assert.False(view.ActiveDuringFirstArrange);
        Assert.True(app.Runtime.Overlay.HasActiveEntry);
        Assert.NotNull(app.Runtime.Overlay.ActiveEntry?.PopupNode);
        Assert.True(app.Runtime.Overlay.ActiveEntry!.PopupNode!.IsMounted);
    }

    [Fact]
    public void A11_CloseInsideLayout_IsDeferredAndConverges()
    {
        var view = new LayoutCloseHost { PopupContent = new Rectangle(Color.Parse("#00AA55")) { FixedSize = new Size(50, 30) } };
        using var app = new TestApp(view, new Size(300, 200));

        app.Runtime.ProcessPendingWork();

        Assert.True(view.ShowAttempted);
        Assert.True(view.CloseAttempted);
        Assert.False(app.Runtime.Overlay.HasActiveEntry);
    }

    // A12 -----------------------------------------------------------------

    [Fact]
    public void A12_NestedShowIsRejected_OuterPopupStaysOpen()
    {
        var nested = new NestedShowView { Inner = new Text("inner popup") };
        using var app = new TestApp(new VStack(new Text("anchor")), new Size(300, 220));
        var anchor = app.Runtime.Root.Require<Text>();

        Assert.True(app.Runtime.Overlay.Show(anchor, new VStack(new Text("outer"), nested)));
        app.Runtime.ProcessPendingWork();

        Assert.True(nested.Attempted);
        Assert.False(nested.LastResult);
        Assert.True(app.Runtime.Overlay.HasActiveEntry);
        Assert.NotNull(app.Runtime.Overlay.ActiveEntry?.PopupNode);
        Assert.Contains(app.Runtime.Diagnostics, d => d.Contains("嵌套"));
    }

    // A13–A16：生命周期回调（OnMount/OnUnmount）中的打开/关闭请求 --------------------

    [Fact]
    public void A13_NestedShowFromContentOnMount_IsRejected_OuterPopupStaysOpen()
    {
        var nested = new OnMountNestedView { Inner = new Text("inner popup") };
        using var app = new TestApp(new VStack(new Text("anchor")), new Size(300, 220));
        var anchor = app.Runtime.Root.Require<Text>();

        Assert.True(app.Runtime.Overlay.Show(anchor, new VStack(new Text("outer"), nested)));
        app.Runtime.ProcessPendingWork();

        // 内容 OnMount 时挂载尚未完成（无活动 entry）：请求仍必须判为嵌套并拒绝，外层保持打开。
        Assert.True(nested.Attempted);
        Assert.False(nested.LastResult);
        Assert.True(app.Runtime.Overlay.HasActiveEntry);
        var popup = app.Runtime.Overlay.ActiveEntry!.PopupNode;
        Assert.NotNull(popup);
        Assert.True(popup!.IsMounted);
        Assert.Contains(popup.FindDescendants<Text>(), n => ((Text)n.View).Content == "outer");
        Assert.Contains(app.Runtime.Diagnostics, d => d.Contains("嵌套"));

        // 稳定：外层 popup 不会被延迟消费的嵌套请求替换。
        app.Runtime.ProcessPendingWork();
        Assert.Same(popup, app.Runtime.Overlay.ActiveEntry!.PopupNode);
        Assert.True(popup.IsMounted);
    }

    [Fact]
    public void A14_CloseFromContentOnMount_ClosesEntryAndStaysStable()
    {
        var closer = new OnMountCloseView();
        using var app = new TestApp(new VStack(new Text("anchor")), new Size(300, 220));
        var anchor = app.Runtime.Root.Require<Text>();

        Assert.True(app.Runtime.Overlay.Show(anchor, closer));
        app.Runtime.ProcessPendingWork();

        // 内容 OnMount 的关闭请求不能被丢弃：挂载完成后即关闭。
        Assert.True(closer.Attempted);
        Assert.NotNull(closer.Node);
        Assert.False(app.Runtime.Overlay.HasActiveEntry);
        Assert.False(closer.Node!.IsMounted);

        // 稳定：关闭后的处理不会重新打开。
        app.Runtime.ProcessPendingWork();
        Assert.False(app.Runtime.Overlay.HasActiveEntry);
    }

    [Fact]
    public void A15_ShowFromOldPopupOnUnmount_OpensNewMainAnchorAndConverges()
    {
        using var app = new TestApp(new VStack(new Text("first"), new Text("second")), new Size(300, 240));
        var anchors = app.Runtime.Root.FindDescendants<Text>().ToArray();
        var first = anchors[0];
        var second = anchors[1];

        var oldContent = new OnUnmountShowView();
        Assert.True(app.Runtime.Overlay.Show(first, oldContent));
        app.Runtime.ProcessPendingWork();
        var oldPopup = app.Runtime.Overlay.ActiveEntry?.PopupNode;
        Assert.NotNull(oldPopup);

        // 旧 popup 被挤占卸载时请求打开主树另一个锚：请求保留，最终稳定在新 entry，不被旧关闭请求误关。
        oldContent.AnchorToOpen = second;
        oldContent.Content = new Text("after unmount");
        Assert.True(app.Runtime.Overlay.Show(first, new Text("replacement")));
        app.Runtime.ProcessPendingWork();

        Assert.True(app.Runtime.Overlay.HasActiveEntry);
        Assert.Same(second, app.Runtime.Overlay.ActiveEntry!.AnchorNode);
        Assert.False(oldPopup!.IsMounted);
        var popup = app.Runtime.Overlay.ActiveEntry.PopupNode;
        Assert.NotNull(popup);
        Assert.True(popup!.IsMounted);
        Assert.Contains(popup.FindDescendants<Text>(), n => ((Text)n.View).Content == "after unmount");

        // 稳定：继续处理不改变活动 entry。
        app.Runtime.ProcessPendingWork();
        Assert.Same(second, app.Runtime.Overlay.ActiveEntry!.AnchorNode);
        Assert.True(popup.IsMounted);
    }

    [Fact]
    public void A16_ReopenFromContentOnMount_IsBounded_ReportsDiagnosticAndRecovers()
    {
        using var app = new TestApp(new VStack(new Text("anchor")), new Size(300, 220));
        var anchor = app.Runtime.Root.Require<Text>();

        Assert.True(app.Runtime.Overlay.Show(anchor, new ReopenOnMountView(anchor)));
        var error = Assert.Throws<InvalidOperationException>(() => app.Runtime.ProcessPendingWork());
        Assert.Contains("overlay", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(app.Runtime.Diagnostics, d => d.Contains("overlay"));

        // 隔离恢复：挂起请求已丢弃，已挂载 entry 稳定，运行时继续可用（不是无限 drain，也不残留每帧自激）。
        app.Runtime.ProcessPendingWork();
        Assert.True(app.Runtime.Overlay.HasActiveEntry);
        var popup = app.Runtime.Overlay.ActiveEntry!.PopupNode;
        Assert.NotNull(popup);
        Assert.True(popup!.IsMounted);

        app.Runtime.Overlay.Close();
        app.Runtime.ProcessPendingWork();
        Assert.False(app.Runtime.Overlay.HasActiveEntry);
        Assert.False(popup.IsMounted);
    }

    // helpers -------------------------------------------------------------

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

    private static int IndexOf(IReadOnlyList<ViewNode> nodes, ViewNode target)
    {
        for (var i = 0; i < nodes.Count; i++)
        {
            if (ReferenceEquals(nodes[i], target))
            {
                return i;
            }
        }

        return -1;
    }

    private static int IndexOfCommand(IReadOnlyList<DrawCommand> commands, Func<DrawCommand, bool> predicate)
    {
        for (var i = 0; i < commands.Count; i++)
        {
            if (predicate(commands[i]))
            {
                return i;
            }
        }

        return -1;
    }

    private sealed class ThemedHost : Component
    {
        public State<Theme>? ThemeState { get; private set; }

        protected override View Build()
        {
            var theme = UseState("theme", Theme.Light);
            ThemeState = theme;
            return new VStack(new Text("anchor")).Theme(theme.Value);
        }
    }

    private sealed class DisableHost : Component
    {
        public State<bool>? DisabledState { get; private set; }

        protected override View Build()
        {
            var disabled = UseState("disabled", false);
            DisabledState = disabled;
            return new VStack(new Text("anchor").Disabled(disabled.Value));
        }
    }

    private sealed class AnchorHost : Component
    {
        public State<bool>? ShowAnchor { get; private set; }

        protected override View Build()
        {
            var show = UseState("show", true);
            ShowAnchor = show;
            return new VStack(show.Value ? new Button("anchor", () => { }) : new Text("other"));
        }
    }

    private sealed class LayoutShowHost : View
    {
        internal View? PopupContent { get; init; }

        internal bool ShowAttempted { get; private set; }

        internal bool ActiveDuringFirstArrange { get; private set; }

        private bool _arranged;

        internal override void Measure(ViewNode node, LayoutConstraints constraints, LayoutContext context)
        {
            node.DesiredSize = constraints.Constrain(new Size(120, 40));
            if (!ShowAttempted)
            {
                ShowAttempted = true;
                node.Runtime.Overlay.Show(node, PopupContent!);
            }
        }

        internal override void Arrange(ViewNode node, Rect bounds, LayoutContext context)
        {
            // P-02：零可用空间的弹层会在安全阶段关闭；A11 只验证“布局内 Show 被延迟消费”，
            // 因此让宿主锚占据一个留出下方空间的小矩形（300×200 视口下 120×40 @ 20,20）。
            node.Frame = new Rect(
                bounds.X + 20,
                bounds.Y + 20,
                Math.Max(0, Math.Min(120, bounds.Width - 40)),
                Math.Max(0, Math.Min(40, bounds.Height - 40)));
            if (!_arranged)
            {
                _arranged = true;
                ActiveDuringFirstArrange = node.Runtime.Overlay.HasActiveEntry;
            }
        }
    }

    private sealed class LayoutCloseHost : View
    {
        internal View? PopupContent { get; init; }

        internal bool ShowAttempted { get; private set; }

        internal bool CloseAttempted { get; private set; }

        internal override void Measure(ViewNode node, LayoutConstraints constraints, LayoutContext context)
        {
            node.DesiredSize = constraints.Constrain(new Size(120, 40));
            if (!ShowAttempted)
            {
                ShowAttempted = true;
                node.Runtime.Overlay.Show(node, PopupContent!);
            }
        }

        internal override void Arrange(ViewNode node, Rect bounds, LayoutContext context)
        {
            node.Frame = bounds;
            if (!CloseAttempted && node.Runtime.Overlay.HasActiveEntry)
            {
                CloseAttempted = true;
                node.Runtime.Overlay.Close();
            }
        }
    }

    private sealed class NestedShowView : View
    {
        internal View? Inner { get; init; }

        internal bool Attempted { get; private set; }

        internal bool? LastResult { get; private set; }

        internal override void Measure(ViewNode node, LayoutConstraints constraints, LayoutContext context)
        {
            node.DesiredSize = constraints.Constrain(new Size(80, 24));
            if (!Attempted)
            {
                Attempted = true;
                LastResult = node.Runtime.Overlay.Show(node, Inner!);
            }
        }
    }

    /// <summary>内容 OnMount 中请求打开自身（嵌套）：必须被拒绝且外层保持打开。</summary>
    private sealed class OnMountNestedView : View
    {
        internal View? Inner { get; init; }

        internal bool Attempted { get; private set; }

        internal bool? LastResult { get; private set; }

        internal override void Measure(ViewNode node, LayoutConstraints constraints, LayoutContext context)
            => node.DesiredSize = constraints.Constrain(new Size(80, 24));

        internal override void OnMount(ViewNode node)
        {
            Attempted = true;
            LastResult = node.Runtime.Overlay.Show(node, Inner!);
        }
    }

    /// <summary>内容 OnMount 中主动关闭自身 entry：合法请求，必须有序关闭而不是被丢弃。</summary>
    private sealed class OnMountCloseView : View
    {
        internal bool Attempted { get; private set; }

        internal ViewNode? Node { get; private set; }

        internal override void Measure(ViewNode node, LayoutConstraints constraints, LayoutContext context)
            => node.DesiredSize = constraints.Constrain(new Size(80, 24));

        internal override void OnMount(ViewNode node)
        {
            Attempted = true;
            Node = node;
            node.Runtime.Overlay.Close();
        }
    }

    /// <summary>旧 popup 内容在 OnUnmount 中请求打开另一个主树锚：合法请求，必须收敛到新 entry。</summary>
    private sealed class OnUnmountShowView : View
    {
        internal ViewNode? AnchorToOpen { get; set; }

        internal View? Content { get; set; }

        internal override void Measure(ViewNode node, LayoutConstraints constraints, LayoutContext context)
            => node.DesiredSize = constraints.Constrain(new Size(80, 24));

        internal override void OnUnmount(ViewNode node)
        {
            if (AnchorToOpen is { } anchor && Content is { } content)
            {
                node.Runtime.Overlay.Show(anchor, content);
            }
        }
    }

    /// <summary>每次挂载都请求重开同一个主树锚（自激）：安全阶段必须有限预算内诊断并中止。</summary>
    private sealed class ReopenOnMountView : View
    {
        private readonly ViewNode _anchor;

        internal ReopenOnMountView(ViewNode anchor) => _anchor = anchor;

        internal override void Measure(ViewNode node, LayoutConstraints constraints, LayoutContext context)
            => node.DesiredSize = constraints.Constrain(new Size(60, 24));

        internal override void OnMount(ViewNode node)
            => node.Runtime.Overlay.Show(_anchor, new ReopenOnMountView(_anchor));
    }
}
