using CSharp.UI;
using Xunit;

namespace CSharp.UI.Tests;

/// <summary>
/// RL-04：AdaptiveStack 的公开 API、断点判定（含精确值/两侧/小数/DPI）、两模式间距与交叉轴对齐、
/// 复用 Stack 轴向特征（Main 随模式、显式轴不翻转）、身份/状态保持与探测缓存契约。
/// </summary>
public class AdaptiveStackTests
{
    private static View Item(double width, double height, Color? color = null)
        => new Rectangle(color ?? Color.Red) { FixedSize = new Size(width, height) };

    [Fact]
    public void Breakpoint_ChoosesModeBelowAtAndAbove()
    {
        var adaptive = new AdaptiveStack(300, Item(100, 20), Item(100, 20)).WideAlign(Alignment.Start);

        using var app = new TestApp(new VStack(adaptive), new Size(299, 200));
        app.Frame();

        var node = app.Runtime.Root.Require<AdaptiveStack>();

        // 299 < 300：纵向——子项上下排列，交叉轴默认 Stretch 占满宽度。
        Assert.False(node.AdaptiveIsHorizontal);
        Assert.Equal(0, node.Children[0].Frame.Y, 3);
        Assert.Equal(20, node.Children[1].Frame.Y, 3);
        Assert.Equal(0, node.Children[0].Frame.X, 3);
        Assert.Equal(299, node.Children[0].Frame.Width, 3);

        // 300 >= 300：横向——子项并排。
        app.Runtime.SetViewport(new Size(300, 200), 1.0);
        app.Runtime.ProcessPendingWork();
        app.Frame();

        Assert.True(node.AdaptiveIsHorizontal);
        Assert.Equal(0, node.Children[0].Frame.Y, 3);
        Assert.Equal(100, node.Children[1].Frame.X, 3);
        Assert.Equal(0, node.Children[1].Frame.Y, 3);

        // 阈值两侧小量与小数宽度：299.99 纵向、300.01 横向。
        app.Runtime.SetViewport(new Size(299.99, 200), 1.0);
        app.Runtime.ProcessPendingWork();
        app.Frame();
        Assert.False(node.AdaptiveIsHorizontal);

        app.Runtime.SetViewport(new Size(300.01, 200), 1.0);
        app.Runtime.ProcessPendingWork();
        app.Frame();
        Assert.True(node.AdaptiveIsHorizontal);
    }

    [Fact]
    public void UnboundedWidth_UsesHorizontalNaturalSize()
    {
        var adaptive = new AdaptiveStack(300, Item(100, 20), Item(100, 20))
            .Spacing(8)
            .WideAlign(Alignment.Start);

        using var app = new TestApp(
            new ScrollView(adaptive, ScrollAxis.Horizontal),
            new Size(150, 100));
        app.Frame();

        var node = app.Runtime.Root.Require<AdaptiveStack>();

        // 宽度无界：横向自然尺寸，不做无穷剩余空间分配。
        Assert.True(node.AdaptiveIsHorizontal);
        Assert.Equal(208, node.DesiredSize.Width, 3);
        Assert.Equal(new Rect(0, 0, 100, 20), node.Children[0].Frame);
        Assert.Equal(new Rect(108, 0, 100, 20), node.Children[1].Frame);
    }

    [Fact]
    public void TwoModeSpacingAndCrossAlignment_AreIndependent()
    {
        var adaptive = new AdaptiveStack(200, Item(100, 30), Item(50, 20, Color.Blue))
            .Spacing(10)
            .VerticalSpacing(6)
            .WideAlign(Alignment.Center)
            .Align(Alignment.End);

        using var app = new TestApp(new VStack(adaptive), new Size(300, 200));
        app.Frame();

        var node = app.Runtime.Root.Require<AdaptiveStack>();

        // 宽：横向 main，间距 10，交叉轴（垂直）Center。
        Assert.True(node.AdaptiveIsHorizontal);
        Assert.Equal(new Rect(0, 0, 100, 30), node.Children[0].Frame);
        Assert.Equal(new Rect(110, 5, 50, 20), node.Children[1].Frame);

        // 窄：纵向 main，间距 6，交叉轴（水平）End。
        app.Runtime.SetViewport(new Size(100, 200), 1.0);
        app.Runtime.ProcessPendingWork();
        app.Frame();

        Assert.False(node.AdaptiveIsHorizontal);
        Assert.Equal(new Rect(0, 0, 100, 30), node.Children[0].Frame);
        Assert.Equal(new Rect(50, 36, 50, 20), node.Children[1].Frame);
    }

    [Fact]
    public void MainAxis_ResolvesToCurrentMode()
    {
        var adaptive = new AdaptiveStack(
                300,
                new Rectangle(Color.Red).Flex(grow: 1, basis: 50),
                new Rectangle(Color.Blue).Flex(grow: 1, basis: 50))
            .Spacing(8)
            .VerticalSpacing(8)
            .WideAlign(Alignment.Start);

        // 高度固定 100：窄模式下主轴（垂直）有界，才能观察 Main 随模式分配剩余空间。
        using var app = new TestApp(
            new FrameView(adaptive, height: 100, alignment: Alignment.Stretch),
            new Size(400, 100));
        app.Frame();

        var node = app.Runtime.Root.Require<AdaptiveStack>();

        // 宽：Main = 水平；两项各 50，剩余 292 平分 → 196。
        Assert.True(node.AdaptiveIsHorizontal);
        Assert.Equal(196, node.Children[0].Frame.Width, 3);
        Assert.Equal(196, node.Children[1].Frame.Width, 3);

        // 窄：Main = 垂直；100 高可容纳 50+50+8，本应压缩不足量 8 → 各 46，宽度交叉轴占满 200。
        app.Runtime.SetViewport(new Size(200, 100), 1.0);
        app.Runtime.ProcessPendingWork();
        app.Frame();

        Assert.False(node.AdaptiveIsHorizontal);
        Assert.Equal(200, node.Children[0].Frame.Width, 3);
        Assert.Equal(46, node.Children[0].Frame.Height, 3);
        Assert.Equal(46, node.Children[1].Frame.Height, 3);
    }

    [Fact]
    public void ExplicitHorizontalAxis_DoesNotBecomeHeightInVerticalMode()
    {
        var child = new Rectangle(Color.Red).Frame(height: 30).Flex(grow: 1, basis: 200, axis: FlexAxis.Horizontal);
        var adaptive = new AdaptiveStack(300, child);

        using var app = new TestApp(new VStack(adaptive), new Size(200, 200));
        app.Frame();

        var node = app.Runtime.Root.Require<AdaptiveStack>();
        var item = node.Children[0];
        var features = item.GetLayoutFeatures(LayoutAxis.Horizontal);

        // 窄模式：显式 Horizontal 的 basis 留在宽度轴，垂直轴仍是自然高度 30。
        Assert.False(node.AdaptiveIsHorizontal);
        Assert.Equal(30, item.Frame.Height, 3);
        Assert.Equal(200, item.Frame.Width, 3);
        Assert.Equal(200d, features.Horizontal.Basis!.Value);
        Assert.Null(features.Vertical.Basis);
    }

    [Fact]
    public void DpiScale_DoesNotChangeModeSelection()
    {
        foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
        {
            var adaptive = new AdaptiveStack(300, Item(100, 20), Item(100, 20));
            using var app = new TestApp(new VStack(adaptive), new Size(299, 200), pixelScale: scale);
            app.Frame();

            var node = app.Runtime.Root.Require<AdaptiveStack>();
            Assert.False(node.AdaptiveIsHorizontal);

            app.Runtime.SetViewport(new Size(300, 200), scale);
            app.Runtime.ProcessPendingWork();
            app.Frame();
            Assert.True(node.AdaptiveIsHorizontal);
        }
    }

    [Fact]
    public void SameModeContinuousResize_RelayoutsWithoutUnmountingChildren()
    {
        var adaptive = new AdaptiveStack(300, new CounterComponent(), Item(100, 20));
        using var app = new TestApp(new VStack(adaptive), new Size(500, 300));
        app.Frame();

        var node = app.Runtime.Root.Require<AdaptiveStack>();
        var componentNode = app.Runtime.Root.Require<CounterComponent>();
        Assert.Same(node.View, adaptive);
        var buildsBefore = CounterComponent.BuildCalls;

        for (var width = 500.0; width >= 320; width -= 45)
        {
            app.Runtime.SetViewport(new Size(width, 300), 1.0);
            app.Runtime.ProcessPendingWork();
            app.Frame();

            Assert.True(node.AdaptiveIsHorizontal);
            Assert.Same(componentNode, app.Runtime.Root.Require<CounterComponent>());
            Assert.Equal(2, node.Children.Count);
            Assert.Same(node.View, adaptive);
        }

        Assert.Equal(buildsBefore, CounterComponent.BuildCalls);
    }

    [Fact]
    public void ModeSwitch_PreservesIdentityStateFocusCaretSelectionAndTabOrder()
    {
        var value = "中文abc";
        var binding = Binding<string>.From(() => value, v => value = v);
        var adaptive = new AdaptiveStack(
                300,
                new Text("label"),
                new CounterComponent(),
                new TextField(binding).Key("field"))
            .WideAlign(Alignment.Start);

        using var app = new TestApp(new VStack(adaptive), new Size(500, 400));
        app.Frame();

        var node = app.Runtime.Root.Require<AdaptiveStack>();
        Assert.True(node.AdaptiveIsHorizontal);

        var fieldNode = app.Runtime.Root.Require<TextField>();
        var componentNode = app.Runtime.Root.Require<CounterComponent>();
        var componentView = (CounterComponent)componentNode.View;
        var addButton = componentNode.Require<Button>();

        Assert.True(fieldNode.HasIdentityKey);
        Assert.Equal("field", fieldNode.IdentityKey);

        app.Click(new Point(addButton.Frame.Center.X, addButton.Frame.Center.Y));
        app.Frame();
        Assert.Equal(1, componentNode.GetStateValue<int>("count"));

        app.Runtime.SetFocus(fieldNode);
        app.Runtime.DispatchTextInput(new TextInputEvent("中文"));
        app.Runtime.DispatchKey(new KeyEvent(KeyEventKind.Down, UiKey.Home, KeyModifiers.None));
        app.Runtime.DispatchKey(new KeyEvent(KeyEventKind.Down, UiKey.End, KeyModifiers.Shift));

        var state = Assert.IsType<TextFieldState>(fieldNode.InteractionState);
        Assert.True(state.HasSelection);
        var caret = state.Caret;
        var anchor = state.Anchor;
        var valueAfterTyping = value;
        var orderBefore = app.Runtime.GetFocusOrder().ToList();

        // 宽 → 窄：模式切换必须保持同一批节点与全部交互状态。
        app.Runtime.SetViewport(new Size(200, 400), 1.0);
        app.Runtime.ProcessPendingWork();
        app.Frame();

        Assert.False(node.AdaptiveIsHorizontal);
        Assert.Equal(valueAfterTyping, value);
        AssertModeSwitchState(app, node, adaptive, fieldNode, componentNode, componentView, addButton, state, caret, anchor, orderBefore);

        // 窄 → 宽：再次切换仍然保持。
        app.Runtime.SetViewport(new Size(500, 400), 1.0);
        app.Runtime.ProcessPendingWork();
        app.Frame();

        Assert.True(node.AdaptiveIsHorizontal);
        Assert.Equal(valueAfterTyping, value);
        AssertModeSwitchState(app, node, adaptive, fieldNode, componentNode, componentView, addButton, state, caret, anchor, orderBefore);
    }

    [Fact]
    public void ModeSwitch_MovesHitTargets_AndOldCoordinatesDoNotHit()
    {
        var clicksA = 0;
        var clicksB = 0;
        var adaptive = new AdaptiveStack(
                300,
                new Button("a", () => clicksA++).Frame(width: 80, height: 30),
                new Button("b", () => clicksB++).Frame(width: 80, height: 30))
            .Spacing(8)
            .VerticalSpacing(6)
            .WideAlign(Alignment.Start);

        using var app = new TestApp(new VStack(adaptive), new Size(400, 100));
        app.Frame();

        var node = app.Runtime.Root.Require<AdaptiveStack>();
        var frameA = node.Children[0];
        var frameB = node.Children[1];
        var buttons = node.FindDescendants<Button>().ToList();
        var buttonB = buttons[1];

        Assert.True(node.AdaptiveIsHorizontal);
        Assert.Equal(88, frameB.Frame.X, 3);
        app.Click(buttonB.Frame.Center);
        Assert.Equal(1, clicksB);

        // 缩窄到 200：纵向，B 换到 A 下方。
        app.Runtime.SetViewport(new Size(200, 100), 1.0);
        app.Runtime.ProcessPendingWork();
        app.Frame();

        Assert.False(node.AdaptiveIsHorizontal);
        Assert.Equal(0, frameA.Frame.Y, 3);
        Assert.Equal(36, frameB.Frame.Y, 3);

        // 宽布局下 B 的旧坐标（128,15）现在是空白：不能命中任何按钮。
        clicksA = 0;
        clicksB = 0;
        app.Click(new Point(128, 15));
        Assert.Equal(0, clicksA);
        Assert.Equal(0, clicksB);

        app.Click(buttonB.Frame.Center);
        Assert.Equal(1, clicksB);
    }

    [Fact]
    public void HardFixedOverflow_InNarrowMode_IsDiagnosedWithoutCrash()
    {
        var adaptive = new AdaptiveStack(300, Item(300, 20));

        using var app = new TestApp(new VStack(adaptive), new Size(200, 200));
        app.Frame();

        var node = app.Runtime.Root.Require<AdaptiveStack>();
        Assert.False(node.AdaptiveIsHorizontal);
        Assert.True(double.IsFinite(node.Children[0].Frame.Width));
        Assert.Contains(app.Runtime.Diagnostics, d => d.Contains("布局溢出"));
    }

    [Fact]
    public void Probe_DoesNotLeakAdaptiveMode()
    {
        var adaptive = new AdaptiveStack(300, Item(100, 20), Item(100, 20));
        using var app = new TestApp(new VStack(adaptive), new Size(400, 200));
        app.Frame();

        var node = app.Runtime.Root.Require<AdaptiveStack>();
        var originalSize = node.DesiredSize;
        Assert.True(node.AdaptiveIsHorizontal);

        var context = new LayoutContext(app.Runtime, app.Text, app.Images, 1.0);
        using (context.BeginProbe())
        {
            context.Measure(node, new LayoutConstraints(0, 150, 0, 200));
            Assert.False(node.AdaptiveIsHorizontal);
        }

        // 探测退出后模式与期望尺寸完整恢复。
        Assert.True(node.AdaptiveIsHorizontal);
        Assert.Equal(originalSize, node.DesiredSize);
        Assert.Equal(MeasureProposalKind.Intrinsic, node.LastProposalKind);
    }

    [Fact]
    public void AdaptiveStackParameters_AreValidatedAtDeclaration()
    {
        var view = Item(10, 10);

        Assert.Throws<ArgumentOutOfRangeException>(() => new AdaptiveStack(double.NaN, view));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AdaptiveStack(double.PositiveInfinity, view));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AdaptiveStack(0, view));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AdaptiveStack(-1, view));
        Assert.Throws<ArgumentNullException>(() => new AdaptiveStack(300, (IReadOnlyList<View>)null!));

        Assert.Throws<ArgumentOutOfRangeException>(() => new AdaptiveStack(300, view) { Spacing = double.NaN });
        Assert.Throws<ArgumentOutOfRangeException>(() => new AdaptiveStack(300, view) { Spacing = -1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new AdaptiveStack(300, view) { VerticalSpacing = double.PositiveInfinity });
        Assert.Throws<ArgumentOutOfRangeException>(() => new AdaptiveStack(300, view) { VerticalSpacing = -0.5 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new AdaptiveStack(300, view) { WideAlignment = (Alignment)99 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new AdaptiveStack(300, view) { NarrowAlignment = (Alignment)99 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new AdaptiveStack(300, view) { Breakpoint = double.NaN });
        Assert.Throws<ArgumentOutOfRangeException>(() => new AdaptiveStack(300, view).Spacing(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AdaptiveStack(300, view).VerticalSpacing(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AdaptiveStack(300, view).WideAlign((Alignment)99));

        // 合法边界：零间距与合法对齐都能声明。
        var valid = new AdaptiveStack(1, view) { Spacing = 0, VerticalSpacing = 0, WideAlignment = Alignment.End, NarrowAlignment = Alignment.Center };
        Assert.Equal(1, valid.Breakpoint);
        Assert.Equal(Alignment.End, valid.WideAlignment);
        Assert.Equal(Alignment.Center, valid.NarrowAlignment);
    }

    private static void AssertModeSwitchState(
        TestApp app,
        ViewNode node,
        AdaptiveStack description,
        ViewNode fieldNode,
        ViewNode componentNode,
        CounterComponent componentView,
        ViewNode addButton,
        TextFieldState state,
        int caret,
        int anchor,
        IReadOnlyList<ViewNode> orderBefore)
    {
        Assert.Same(description, node.View);
        Assert.Same(fieldNode, app.Runtime.Root.Require<TextField>());
        Assert.Same(componentNode, app.Runtime.Root.Require<CounterComponent>());
        Assert.Same(componentView, componentNode.View);
        Assert.Same(addButton, componentNode.Require<Button>());
        Assert.Equal(1, componentNode.GetStateValue<int>("count"));
        Assert.Equal("field", fieldNode.IdentityKey);

        Assert.Same(fieldNode, app.Runtime.FocusedNode);
        Assert.Same(state, fieldNode.InteractionState);
        Assert.Equal(caret, state.Caret);
        Assert.Equal(anchor, state.Anchor);
        Assert.True(state.HasSelection);

        var orderAfter = app.Runtime.GetFocusOrder().ToList();
        Assert.Equal(orderBefore.Count, orderAfter.Count);
        for (var i = 0; i < orderBefore.Count; i++)
        {
            Assert.Same(orderBefore[i], orderAfter[i]);
        }
    }
}
