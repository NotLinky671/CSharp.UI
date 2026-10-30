using CSharp.UI;
using Xunit;

namespace CSharp.UI.Tests;

/// <summary>
/// P-03：公开 <see cref="Popup"/> 的 IsOpen 双向纪律与生命周期。
/// 断言只走公开 API 与可观察面（HitTest、IsMounted、Frame、Binding 读写计数、Environment、
/// ActiveEntry 的节点身份），不 pin 内部字段名。
/// </summary>
public class PopupViewTests
{
    // C1 ------------------------------------------------------------------

    [Fact]
    public void C1_IsOpenBindingMountsAndUnmountsRealPopupSubtree()
    {
        var host = new PopupHost();
        using var app = new TestApp(host, new Size(320, 240));

        var anchor = app.Runtime.Root.FindDescendant<Button>()!;
        var anchorFrame = anchor.Frame;
        Assert.False(app.Runtime.Overlay.HasActiveEntry);

        host.Open!.Value = true;
        app.Runtime.ProcessPendingWork();

        Assert.True(app.Runtime.Overlay.HasActiveEntry);
        var popup = app.Runtime.Overlay.ActiveEntry!.PopupNode;
        Assert.NotNull(popup);
        Assert.True(popup!.IsMounted);
        Assert.False(popup.Frame.IsEmpty);

        // popup 内容真实参与命中（落在 popup 子树内）；锚布局与命中不受影响。
        var popupText = popup.FindDescendant<Text>()!;
        var hit = app.Runtime.HitTest(new Point(popupText.Frame.X + 2, popupText.Frame.Y + 2));
        Assert.NotNull(hit);
        Assert.True(IsSelfOrDescendant(popup, hit!));
        Assert.Same(anchor, app.Runtime.HitTest(new Point(anchorFrame.X + 2, anchorFrame.Y + 2)));
        Assert.Equal(anchorFrame, anchor.Frame);

        host.Open.Value = false;
        app.Runtime.ProcessPendingWork();

        Assert.False(app.Runtime.Overlay.HasActiveEntry);
        Assert.False(popup.IsMounted);
    }

    // C2 ------------------------------------------------------------------

    [Fact]
    public void C2_InternalCloseWritesFalseOnce_AndOnlyANewEdgeReopens()
    {
        var value = false;
        var writes = 0;
        var binding = new Binding<bool>(() => value, v =>
        {
            writes++;
            value = v;
        });
        var popup = new Popup(new Button("anchor", () => { }), new Text("popup"), binding);
        using var app = new TestApp(new VStack(popup), new Size(300, 220));

        value = true;
        app.Runtime.ProcessPendingWork();
        Assert.True(app.Runtime.Overlay.HasActiveEntry);
        Assert.Equal(0, writes);

        // 内部关闭（P-04 的 Esc/外点路径将来只调用同一入口）：写回 false 恰好一次。
        app.Runtime.Overlay.Close(OverlayCloseReason.Escape);
        app.Runtime.ProcessPendingWork();
        Assert.False(app.Runtime.Overlay.HasActiveEntry);
        Assert.Equal(1, writes);
        Assert.False(value);

        // 之后每轮都不再写、不再开（无每帧竞争）。
        for (var i = 0; i < 3; i++)
        {
            app.Runtime.ProcessPendingWork();
            Assert.False(app.Runtime.Overlay.HasActiveEntry);
            Assert.Equal(1, writes);
        }

        // 外部真正产生 false→true 边沿才重开。
        value = true;
        app.Runtime.ProcessPendingWork();
        Assert.True(app.Runtime.Overlay.HasActiveEntry);
        Assert.Equal(1, writes);
    }

    [Fact]
    public void C3_ConstantTrueBindingStaysClosedAfterInternalClose()
    {
        var writes = 0;
        var binding = new Binding<bool>(() => true, _ => writes++);
        var popup = new Popup(new Button("anchor", () => { }), new Text("popup"), binding);
        using var app = new TestApp(new VStack(popup), new Size(300, 220));

        Assert.True(app.Runtime.Overlay.HasActiveEntry);

        app.Runtime.Overlay.Close(OverlayCloseReason.OutsidePress);
        app.Runtime.ProcessPendingWork();
        Assert.False(app.Runtime.Overlay.HasActiveEntry);
        Assert.Equal(1, writes);

        // getter 恒 true：不得每帧重开，也不重复写 false。
        for (var i = 0; i < 3; i++)
        {
            app.Runtime.ProcessPendingWork();
            Assert.False(app.Runtime.Overlay.HasActiveEntry);
            Assert.Equal(1, writes);
        }
    }

    // C6 ------------------------------------------------------------------

    [Fact]
    public void C6_DisabledGateRejectsOpenWithoutWritingBack_AndOpensOnceEligible()
    {
        var host = new PopupHost { StartDisabled = true };
        using var app = new TestApp(host, new Size(300, 220));

        host.Open!.Value = true;
        app.Runtime.ProcessPendingWork();

        // 资格门不满足：不挂载、不回写 binding（仍为 true）。
        Assert.False(app.Runtime.Overlay.HasActiveEntry);
        Assert.True(host.Open.PeekValue());

        host.Disabled!.Value = false;
        app.Runtime.ProcessPendingWork();

        // 资格恢复后首次评估即打开一次。
        Assert.True(app.Runtime.Overlay.HasActiveEntry);
    }

    [Fact]
    public void C6_PopupContentUsesAnchorLocalEnvironment()
    {
        var popup = new Popup(new Text("anchor"), new Text("popup"), Binding<bool>.Constant(true))
            .Theme(Theme.Dark);
        using var app = new TestApp(new VStack(popup, new Text("plain")), new Size(300, 200));

        var popupNode = app.Runtime.Overlay.ActiveEntry!.PopupNode;
        Assert.NotNull(popupNode);
        Assert.Same(Theme.Dark, popupNode!.FindDescendant<Text>()!.Environment.Theme);

        // 主树中未继承 Dark 的同级文本仍是 runtime 默认主题。
        var plain = app.Runtime.Root.FindDescendants<Text>().Last();
        Assert.Same(Theme.Light, plain.Environment.Theme);
    }

    // C7 ------------------------------------------------------------------

    [Fact]
    public void C7_ContentDescriptionUpdateReconcilesWithoutRemount()
    {
        var host = new PopupHost();
        using var app = new TestApp(host, new Size(320, 240));

        host.Open!.Value = true;
        app.Runtime.ProcessPendingWork();

        var popup = app.Runtime.Overlay.ActiveEntry!.PopupNode;
        Assert.NotNull(popup);
        var text = popup!.FindDescendant<Text>();
        Assert.NotNull(text);
        Assert.Equal("popup 0", ((Text)text!.View).Content);

        host.Version!.Value = 1;
        app.Runtime.ProcessPendingWork();

        // surface 节点与 content 子树节点都被复用（状态不丢、不 unmount/remount）。
        Assert.Same(popup, app.Runtime.Overlay.ActiveEntry!.PopupNode);
        var updated = popup.FindDescendant<Text>();
        Assert.Same(text, updated);
        Assert.True(text.IsMounted);
        Assert.Equal("popup 1", ((Text)updated!.View).Content);
    }

    // C8 ------------------------------------------------------------------

    [Fact]
    public void C8_PreferenceForcesPlacementSide()
    {
        var belowPopup = new Popup(
            new Button("below anchor", () => { }),
            new Text("popup"),
            Binding<bool>.Constant(true),
            PopupPlacement.Below);
        using (var belowApp = new TestApp(new VStack(new Spacer().Frame(height: 140), belowPopup), new Size(300, 300)))
        {
            var anchor = belowApp.Runtime.Root.FindDescendant<Button>()!;
            var popup = belowApp.Runtime.Overlay.ActiveEntry!.PopupNode!;
            Assert.Equal(anchor.Frame.Bottom, popup.Frame.Y, 3);
        }

        var abovePopup = new Popup(
            new Button("above anchor", () => { }),
            new Text("popup"),
            Binding<bool>.Constant(true),
            PopupPlacement.Above);
        using (var aboveApp = new TestApp(new VStack(new Spacer().Frame(height: 140), abovePopup), new Size(300, 300)))
        {
            var anchor = aboveApp.Runtime.Root.FindDescendant<Button>()!;
            var popup = aboveApp.Runtime.Overlay.ActiveEntry!.PopupNode!;
            Assert.Equal(anchor.Frame.Y, popup.Frame.Bottom, 3);
        }
    }

    // C9 ------------------------------------------------------------------

    [Fact]
    public void C9_SecondOwnerWins_FirstStaysClosedWithoutPerFrameCompetition()
    {
        var first = new Popup(
            new Button("first", () => { }),
            new Text("first popup"),
            Binding<bool>.Constant(true));
        var second = new Popup(
            new Button("second", () => { }),
            new Text("second popup"),
            Binding<bool>.Constant(true));
        using var app = new TestApp(new VStack(first, second), new Size(300, 240));

        var anchors = app.Runtime.Root.FindDescendants<Button>().ToArray();
        Assert.Equal(2, anchors.Length);
        Assert.Same(anchors[1], app.Runtime.Overlay.ActiveEntry?.AnchorNode);

        for (var i = 0; i < 3; i++)
        {
            app.Runtime.ProcessPendingWork();
            Assert.Same(anchors[1], app.Runtime.Overlay.ActiveEntry?.AnchorNode);
            Assert.NotNull(app.Runtime.Overlay.ActiveEntry?.PopupNode);
        }
    }

    // C10 -----------------------------------------------------------------

    [Fact]
    public void C10_NestedPopupInsideContentIsRejected_OuterStaysOpen()
    {
        var inner = new Popup(
            new Button("inner anchor", () => { }),
            new Text("inner popup"),
            Binding<bool>.Constant(true));
        var outer = new Popup(
            new Button("outer anchor", () => { }),
            new VStack(new Text("outer content"), inner),
            Binding<bool>.Constant(true));
        using var app = new TestApp(new VStack(outer), new Size(300, 240));

        app.Runtime.ProcessPendingWork();

        Assert.True(app.Runtime.Overlay.HasActiveEntry);
        var popup = app.Runtime.Overlay.ActiveEntry!.PopupNode;
        Assert.NotNull(popup);
        Assert.True(popup!.IsMounted);
        Assert.Contains(app.Runtime.Diagnostics, d => d.Contains("嵌套"));
        Assert.DoesNotContain(popup.FindDescendants<Text>(), t => ((Text)t.View).Content == "inner popup");

        // 诊断只报一次：重复安全阶段不会刷屏，外层继续打开。
        var nestedDiagnostics = app.Runtime.Diagnostics.Count(d => d.Contains("嵌套"));
        app.Runtime.ProcessPendingWork();
        app.Runtime.ProcessPendingWork();
        Assert.Equal(nestedDiagnostics, app.Runtime.Diagnostics.Count(d => d.Contains("嵌套")));
        Assert.True(app.Runtime.Overlay.HasActiveEntry);
    }

    // ForEach 锚（多条目锚）-------------------------------------------------

    [Fact]
    public void C13_ForEachAnchorStacksAllRows_AnchorsToWholeSubtree_AndRowsStayClickable()
    {
        var clicks = new List<string>();
        View rows = new ForEach<int>(
            [1, 2],
            (i, _) => new Button($"row {i}", () => clicks.Add($"row {i}")),
            (i, _) => i);
        var popup = new Popup(rows, new Text("popup"), Binding<bool>.Constant(true));
        using var app = new TestApp(new VStack(popup), new Size(300, 240));

        var buttons = app.Runtime.Root.FindDescendants<Button>().ToArray();
        Assert.Equal(2, buttons.Length);

        // 多条目锚的每一项都参与布局且互不重叠（消费者可见几何）。
        Assert.True(buttons[0].Frame.Height > 0);
        Assert.True(buttons[0].Frame.Bottom <= buttons[1].Frame.Y + 0.001);

        // 定位以整棵锚子树的底边为准（下方空间充足 → 贴在最后一行之下）。
        var anchorBottom = Math.Max(buttons[0].Frame.Bottom, buttons[1].Frame.Bottom);
        var popupNode = app.Runtime.Overlay.ActiveEntry?.PopupNode;
        Assert.NotNull(popupNode);
        Assert.Equal(anchorBottom, popupNode!.Frame.Y, 3);

        // 第二行仍在主树正常接收输入（没有被归一化后的锚子树或弹层吞掉）。
        var second = buttons[1];
        app.Click(new Point(second.Frame.X + 4, second.Frame.Y + (second.Frame.Height / 2)));
        Assert.Contains("row 2", clicks);
    }

    // portal 内容反应性（缓存绑定）------------------------------------------

    [Fact]
    public void C14_PopupContentCachedBindingDrivesRedrawAndUpdatesDisplayedText()
    {
        var host = new CachedBindingHost();
        using var app = new TestApp(host, new Size(360, 300));

        host.Open!.Value = true;
        app.Runtime.ProcessPendingWork();
        Assert.True(app.Runtime.Overlay.HasActiveEntry);

        var field = app.Runtime.Overlay.ActiveEntry!.PopupNode!.FindDescendant<TextField>()!;
        _ = app.Frame();
        app.Runtime.ProcessPendingWork();
        Assert.False(app.Runtime.NeedsDraw, "稳定后不应有脏帧。");
        Assert.Equal("before", FieldText(field));

        // 绑定保存在字段、Build 不直接读值：依赖靠 portal 内容的 Component 归属登记。
        host.Text!.Value = "after";
        app.Runtime.ProcessPendingWork();

        Assert.True(app.Runtime.NeedsDraw, "外部状态变化应经稳定依赖登记请求重绘。");
        _ = app.Frame();
        Assert.Equal("after", FieldText(field));

        // 关闭后卸载释放订阅：再改状态不得让已卸载内容请求重绘。
        host.Open.Value = false;
        app.Runtime.ProcessPendingWork();
        _ = app.Frame();
        app.Runtime.ProcessPendingWork();
        Assert.False(app.Runtime.NeedsDraw, "关闭稳定后不应有脏帧。");

        host.Text.Value = "unmounted";
        app.Runtime.ProcessPendingWork();
        Assert.False(app.Runtime.NeedsDraw, "弹层内容卸载后不得再为旧依赖请求重绘。");
    }

    private static string FieldText(ViewNode node)
        => Assert.IsType<TextFieldState>(node.InteractionState).Editor.Text;

    /// <summary>
    /// 绑定保存在字段、构建期只读取一次的组件：内容位于 popup（portal）内，
    /// 验证外部状态变化仍能驱动重绘与真实文本更新。
    /// </summary>
    private sealed class CachedBindingHost : Component
    {
        private Binding<string>? _cached;

        internal State<string>? Text { get; private set; }

        internal State<bool>? Open { get; private set; }

        protected override View Build()
        {
            var text = UseState("text", "before");
            var open = UseState("open", false);
            Text = text;
            Open = open;

            _cached ??= text.ToBinding();

            return new VStack(
                new Text("main"),
                new Popup(
                    new Button("anchor", () => open.Value = !open.Value),
                    new TextField(_cached),
                    open.ToBinding()));
        }
    }

    // owner 卸载 -----------------------------------------------------------

    [Fact]
    public void C12_OwnerUnmountClosesPopupAndWritesFalseOnce()
    {
        var host = new PopupHost();
        using var app = new TestApp(host, new Size(320, 240));

        host.Open!.Value = true;
        app.Runtime.ProcessPendingWork();
        Assert.True(app.Runtime.Overlay.HasActiveEntry);
        var popup = app.Runtime.Overlay.ActiveEntry!.PopupNode;
        Assert.NotNull(popup);

        // 主树描述移除 Popup 节点（同位置换成别的类型）：关闭 + 写回 false 一次。
        host.ShowPopup!.Value = false;
        app.Runtime.ProcessPendingWork();

        Assert.False(app.Runtime.Overlay.HasActiveEntry);
        Assert.False(popup!.IsMounted);
        Assert.False(host.Open.PeekValue());
    }

    // 空闲稳定性 -----------------------------------------------------------
    [Fact]
    public void C0_StableOpenPopupDoesNotProduceExtraLayoutWork()
    {
        var host = new PopupHost();
        using var app = new TestApp(host, new Size(320, 240));

        host.Open!.Value = true;
        app.Runtime.ProcessPendingWork();

        var passes = app.Runtime.LayoutPassCount;
        host.Version!.Value = 0; // 同值写入：不重建、不刷新

        for (var i = 0; i < 3; i++)
        {
            app.Runtime.ProcessPendingWork();
            Assert.Equal(passes, app.Runtime.LayoutPassCount);
        }

        // 关闭后同样保持空闲：不再为 overlay 付布局代价，也不会重新挂载。
        host.Open.Value = false;
        app.Runtime.ProcessPendingWork();
        Assert.False(app.Runtime.Overlay.HasActiveEntry);

        passes = app.Runtime.LayoutPassCount;
        for (var i = 0; i < 3; i++)
        {
            app.Runtime.ProcessPendingWork();
            Assert.Equal(passes, app.Runtime.LayoutPassCount);
        }
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

    private sealed class PopupHost : Component
    {
        internal State<bool>? Open { get; private set; }

        internal State<bool>? Disabled { get; private set; }

        internal State<int>? Version { get; private set; }

        internal State<bool>? ShowPopup { get; private set; }

        internal bool StartDisabled { get; init; }

        protected override View Build()
        {
            var open = UseState("open", false);
            var disabled = UseState("disabled", StartDisabled);
            var version = UseState("version", 0);
            var showPopup = UseState("showPopup", true);
            Open = open;
            Disabled = disabled;
            Version = version;
            ShowPopup = showPopup;

            View popup = showPopup.Value
                ? new Popup(
                    anchor: new Button("anchor", () => { }),
                    content: new VStack(new Text($"popup {version.Value}"), new Button("popup action", () => { })),
                    isOpen: open.ToBinding())
                    .Disabled(disabled.Value)
                : new Text("no popup");

            return new VStack(new Text("main"), popup);
        }
    }
}
