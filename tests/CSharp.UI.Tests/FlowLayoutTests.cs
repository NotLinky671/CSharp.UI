using CSharp.UI;
using Xunit;

namespace CSharp.UI.Tests;

/// <summary>
/// RL-03：Flow 自动换行容器的公开 API、贪心分行几何、独立项/行间距、行内对齐、
/// FillWidth 整行、超宽项处理、无界与零宽边界、诊断与探测缓存契约。
/// </summary>
public class FlowLayoutTests
{
    private static View Item(double width, double height, Color? color = null)
        => new Rectangle(color ?? Color.Red) { FixedSize = new Size(width, height) };

    private static Flow FlowOf(params View[] children) => new(children);

    [Fact]
    public void GreedyLines_UseItemAndLineSpacing_AndPreserveFractions()
    {
        var flow = FlowOf(Item(100, 20), Item(100, 20), Item(100, 20), Item(100, 20))
            .Spacing(8)
            .LineSpacing(6);

        using var app = new TestApp(new VStack(flow), new Size(320, 400));
        app.Frame();

        var node = app.Runtime.Root.Require<Flow>();
        var cache = node.FlowCache!;

        // 320 宽：100 + 8 + 100 + 8 + 100 = 316 放 3 项；下一项 316 + 8 + 100 > 320 换行。
        Assert.Equal(2, cache.Lines.Length);
        Assert.Equal(316, cache.ContentWidth, 3);
        Assert.Equal(46, node.DesiredSize.Height, 3);

        Assert.Equal(new Rect(0, 0, 100, 20), node.Children[0].Frame);
        Assert.Equal(new Rect(108, 0, 100, 20), node.Children[1].Frame);
        Assert.Equal(new Rect(216, 0, 100, 20), node.Children[2].Frame);
        Assert.Equal(new Rect(0, 26, 100, 20), node.Children[3].Frame);
    }

    [Fact]
    public void ExactFit_StaysOnSameLine_AndOneFractionBelowBreaks()
    {
        // 100 + 8 + 100 = 208 正好放满。
        using (var app = new TestApp(new VStack(FlowOf(Item(100, 20), Item(100, 20)).Spacing(8)), new Size(208, 200)))
        {
            app.Frame();
            var node = app.Runtime.Root.Require<Flow>();
            Assert.Single(node.FlowCache!.Lines);
            Assert.Equal(new Rect(108, 0, 100, 20), node.Children[1].Frame);
        }

        // 207.9 比整行少 0.1：必须换行。
        using (var app = new TestApp(new VStack(FlowOf(Item(100, 20), Item(100, 20)).Spacing(8)), new Size(207.9, 200)))
        {
            app.Frame();
            var node = app.Runtime.Root.Require<Flow>();
            Assert.Equal(2, node.FlowCache!.Lines.Length);
            Assert.Equal(0, node.Children[1].Frame.X, 3);
            Assert.Equal(20, node.Children[1].Frame.Y, 3);
        }
    }

    [Fact]
    public void EmptyFlow_IsZeroAndDoesNotFail()
    {
        using var app = new TestApp(new VStack(new Flow()), new Size(200, 100));
        app.Frame();

        var node = app.Runtime.Root.Require<Flow>();
        Assert.Empty(node.Children);
        Assert.Equal(0, node.DesiredSize.Width, 3);
        Assert.Equal(0, node.DesiredSize.Height, 3);
        Assert.Empty(node.FlowCache!.Lines);
    }

    [Fact]
    public void ZeroWidthItems_DoNotLoopAndUseItemSpacing()
    {
        var flow = FlowOf(Item(0, 10), Item(0, 10), Item(0, 10)).Spacing(4);

        using var app = new TestApp(new VStack(flow), new Size(12, 100));
        app.Frame();

        var node = app.Runtime.Root.Require<Flow>();
        Assert.Single(node.FlowCache!.Lines);
        Assert.Equal(new Rect(0, 0, 0, 10), node.Children[0].Frame);
        Assert.Equal(new Rect(4, 0, 0, 10), node.Children[1].Frame);
        Assert.Equal(new Rect(8, 0, 0, 10), node.Children[2].Frame);
    }

    [Fact]
    public void LineHeight_IsMaxItemHeight_AndAlignmentOffsetsWithinLine()
    {
        // 同一行内 40 高与 20 高：行高取 40，第二项按对齐在行内偏移。
        (Alignment Alignment, double ExpectedY)[] cases =
        [
            (Alignment.Start, 0),
            (Alignment.Center, 10),
            (Alignment.End, 20),
        ];

        foreach (var item in cases)
        {
            using var app = new TestApp(
                new VStack(FlowOf(Item(100, 40), Item(100, 20, Color.Blue)).Spacing(8).Align(item.Alignment)),
                new Size(300, 200));
            app.Frame();

            var node = app.Runtime.Root.Require<Flow>();
            Assert.Single(node.FlowCache!.Lines);
            Assert.Equal(40, node.DesiredSize.Height, 3);
            Assert.Equal(item.ExpectedY, node.Children[1].Frame.Y, 3);
            Assert.Equal(20, node.Children[1].Frame.Height, 3);
        }
    }

    [Fact]
    public void UnboundedWidth_ArrangesSingleLine()
    {
        // 水平 ScrollView 释放宽度：Flow 必须排成单行并使用自然尺寸。
        using var app = new TestApp(
            new ScrollView(FlowOf(Item(100, 20), Item(100, 20), Item(100, 20)).Spacing(8), ScrollAxis.Horizontal),
            new Size(224, 100));
        app.Frame();

        var node = app.Runtime.Root.Require<Flow>();
        Assert.False(node.FlowCache!.Bounded);
        Assert.Single(node.FlowCache.Lines);
        Assert.All(node.Children, child => Assert.Equal(0, child.Frame.Y, 3));
        Assert.Equal(new Rect(0, 0, 100, 20), node.Children[0].Frame);
        Assert.Equal(new Rect(108, 0, 100, 20), node.Children[1].Frame);
        Assert.Equal(new Rect(216, 0, 100, 20), node.Children[2].Frame);
    }

    [Fact]
    public void OversizedRemeasurableItem_IsRemeasuredToLineWidth_AloneOnLine()
    {
        // 内层固定 240、外框 maxWidth300 的普通项：可重新测量，独占一行并收缩到整行 200。
        var frame = new FrameView(Item(240, 20), maxWidth: 300);
        using var app = new TestApp(new VStack(FlowOf(frame)), new Size(200, 300));
        app.Frame();

        var flow = app.Runtime.Root.Require<Flow>();
        var item = flow.Children[0];

        Assert.Single(flow.FlowCache!.Lines);
        Assert.Equal(200, item.Frame.Width, 3);
        Assert.Equal(0, item.Frame.X, 3);
        Assert.Equal(200, item.DesiredSize.Width, 3);
        Assert.True(item.LastConstraints.IsWidthTight);
        Assert.Equal(200, item.LastConstraints.MaxWidth, 3);
    }

    [Fact]
    public void HardFixedOversizedItem_OccupiesOwnLine_AndReportsOverflow()
    {
        var flow = FlowOf(Item(300, 20), Item(100, 20, Color.Blue)).Spacing(8);

        using var app = new TestApp(new VStack(flow), new Size(200, 300));
        app.Frame();

        var node = app.Runtime.Root.Require<Flow>();
        var cache = node.FlowCache!;

        // 硬固定 300 无法压缩：独占一行、保持声明宽度、报告可定位溢出；下一项另起一行。
        Assert.Equal(2, cache.Lines.Length);
        Assert.Equal(300, node.Children[0].Frame.Width, 3);
        Assert.Equal(0, node.Children[1].Frame.X, 3);
        Assert.Equal(20, node.Children[1].Frame.Y, 3);

        var overflow = Assert.Single(
            app.Runtime.Diagnostics,
            d => d.Contains("布局溢出") && !d.Contains("Rectangle"));
        Assert.Contains("300", overflow);
        Assert.Contains("200", overflow);
    }

    [Fact]
    public void FillWidth_OccupiesWholeLine_AndDoesNotJoinLineDistribution()
    {
        var fillText = new Text("x").FillWidth();
        var flow = FlowOf(Item(100, 20), fillText, Item(100, 20, Color.Blue)).Spacing(8).LineSpacing(6);

        using var app = new TestApp(new VStack(flow), new Size(300, 300));
        app.Frame();

        var node = app.Runtime.Root.Require<Flow>();
        var cache = node.FlowCache!;

        // FillWidth 独占整行：前一项、填充项、后一项各成一行；填充项占满 300。
        Assert.Equal(3, cache.Lines.Length);
        Assert.Equal(300, node.Children[1].Frame.Width, 3);
        Assert.Equal(0, node.Children[1].Frame.X, 3);
        Assert.Equal(0, node.Children[2].Frame.X, 3);
        Assert.True(node.Children[2].Frame.Y > node.Children[1].Frame.Y);

        var textNode = app.Runtime.Root.Require<Text>();
        Assert.Equal(300, textNode.Frame.Width, 3);
    }

    [Fact]
    public void DirectFlexChild_ReportsLocatableUnsupportedDiagnostic()
    {
        using var app = new TestApp(
            new VStack(FlowOf(new Text("a").Flex(basis: 100), new Text("b"))),
            new Size(300, 200));
        app.Frame();

        var diagnostic = Assert.Single(app.Runtime.Diagnostics, d => d.Contains("Flow 不支持直接子项的 Flex 权重分配"));
        Assert.Contains("Flex(", diagnostic);
        Assert.Contains("Flow(", diagnostic);
        Assert.Contains("FillWidth", diagnostic);

        // 不支持的弹性权重不参与分配：该项按自然尺寸参与普通分行。
        var node = app.Runtime.Root.Require<Flow>();
        Assert.Equal(8.4, node.Children[0].Frame.Width, 3);
        Assert.Single(node.FlowCache!.Lines);
    }

    [Fact]
    public void PaddingInflatesItem_AndContentKeepsInset()
    {
        var padded = Item(100, 20).Padding(10);
        var flow = FlowOf(padded, Item(100, 20, Color.Blue).Padding(10)).Spacing(8);

        using var app = new TestApp(new VStack(flow), new Size(300, 200));
        app.Frame();

        var node = app.Runtime.Root.Require<Flow>();
        Assert.Single(node.FlowCache!.Lines);
        Assert.Equal(new Rect(0, 0, 120, 40), node.Children[0].Frame);
        Assert.Equal(new Rect(128, 0, 120, 40), node.Children[1].Frame);

        // 内边距区域不属于内容：实际矩形从 (10,10) 开始。
        var content = node.Children[0].Children[0];
        Assert.Equal(new Rect(10, 10, 100, 20), content.Frame);
    }

    [Fact]
    public void NestedContainers_WithDifferentWidths_BreakDifferently()
    {
        static View[] Items() => [Item(100, 20), Item(100, 20), Item(100, 20), Item(100, 20)];

        var narrow = FlowOf(Items()).Spacing(8).LineSpacing(6);
        var wide = FlowOf(Items()).Spacing(8).LineSpacing(6);

        using var app = new TestApp(
            new VStack(
                new VStack(narrow).Frame(width: 150),
                new VStack(wide).Frame(width: 350)),
            new Size(400, 400));
        app.Frame();

        var flows = app.Runtime.Root.FindDescendants<Flow>().ToList();
        var narrowNode = flows[0];
        var wideNode = flows[1];

        // 同一窗口宽度下，内层容器 150 放 1 项/行（4 行），350 放 3 项/行（2 行）。
        Assert.Equal(4, narrowNode.FlowCache!.Lines.Length);
        Assert.Equal(2, wideNode.FlowCache!.Lines.Length);
        Assert.Equal(98, narrowNode.DesiredSize.Height, 3);
        Assert.Equal(46, wideNode.DesiredSize.Height, 3);
    }

    [Fact]
    public void WidthChange_RecomputesLinesAndHeight()
    {
        var flow = FlowOf(Item(100, 20), Item(100, 20), Item(100, 20), Item(100, 20))
            .Spacing(8)
            .LineSpacing(6);

        using var app = new TestApp(new ScrollView(flow), new Size(400, 400));
        app.Frame();

        var node = app.Runtime.Root.Require<Flow>();
        Assert.Equal(2, node.FlowCache!.Lines.Length);
        Assert.Equal(46, node.DesiredSize.Height, 3);

        app.Runtime.SetViewport(new Size(200, 400), 1.0);
        app.Runtime.ProcessPendingWork();
        app.Frame();

        Assert.Equal(4, node.FlowCache!.Lines.Length);
        Assert.Equal(98, node.DesiredSize.Height, 3);
    }

    [Fact]
    public void SameLogicalWidth_ProducesSameLayoutAcrossDpiScales()
    {
        (double, int, double, double)[] observed = [];

        foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
        {
            var flow = FlowOf(Item(100, 20), Item(100, 20), Item(100, 20), Item(100, 20))
                .Spacing(8)
                .LineSpacing(6);
            using var app = new TestApp(new VStack(flow), new Size(320, 400), pixelScale: scale);
            app.Frame();

            var node = app.Runtime.Root.Require<Flow>();
            observed = [.. observed, (scale, node.FlowCache!.Lines.Length, node.DesiredSize.Height, node.Children[3].Frame.Y)];
        }

        Assert.Equal(4, observed.Length);
        Assert.All(observed, item => Assert.Equal(2, item.Item2));
        Assert.All(observed, item => Assert.Equal(46, item.Item3, 3));
        Assert.All(observed, item => Assert.Equal(26, item.Item4, 3));
    }

    [Fact]
    public void Probe_DoesNotLeakFlowLineCache()
    {
        var flow = FlowOf(Item(100, 20), Item(100, 20), Item(100, 20)).Spacing(8).LineSpacing(6);
        using var app = new TestApp(new VStack(flow), new Size(320, 400));
        app.Frame();

        var node = app.Runtime.Root.Require<Flow>();
        var originalCache = node.FlowCache;
        var originalSize = node.DesiredSize;
        Assert.Single(originalCache!.Lines);

        var context = new LayoutContext(app.Runtime, app.Text, app.Images, 1.0);
        using (context.BeginProbe())
        {
            context.Measure(node, new LayoutConstraints(0, 60, 0, 400));
            Assert.Equal(3, node.FlowCache!.Lines.Length);
            Assert.NotSame(originalCache, node.FlowCache);
        }

        // 探测退出后行缓存与期望尺寸完整恢复，不遗留探测宽度下的分行结果。
        Assert.Same(originalCache, node.FlowCache);
        Assert.Equal(originalSize, node.DesiredSize);
        Assert.Single(node.FlowCache!.Lines);
    }

    [Fact]
    public void ScrollViewContentHeightAndScrollBounds_FollowWidthChanges()
    {
        var flow = FlowOf(
                Item(100, 20), Item(100, 20), Item(100, 20),
                Item(100, 20), Item(100, 20), Item(100, 20))
            .Spacing(8)
            .LineSpacing(6);

        using var app = new TestApp(new ScrollView(flow), new Size(200, 100));
        app.Frame();

        var node = app.Runtime.Root.Require<Flow>();

        // 200 宽：每行 1 项 → 6 行，高度 6×20 + 5×6 = 150，可滚动。
        Assert.Equal(6, node.FlowCache!.Lines.Length);
        Assert.Equal(150, node.DesiredSize.Height, 3);
        app.Wheel(new Point(50, 50), -3);
        Assert.True(app.Runtime.Root.ScrollOffsetY > 0, "内容高于视口时应能滚动。");

        // 展开到 500 宽：4 项/行 → 2 行，高度 46；滚动偏移必须回落到 0。
        app.Runtime.SetViewport(new Size(500, 100), 1.0);
        app.Runtime.ProcessPendingWork();
        app.Frame();

        Assert.Equal(2, node.FlowCache!.Lines.Length);
        Assert.Equal(46, node.DesiredSize.Height, 3);
        Assert.Equal(0, app.Runtime.Root.ScrollOffsetY, 3);
    }

    [Fact]
    public void Reflow_PreservesKeyComponentStateTextFieldStateFocusAndTabOrder()
    {
        var value = "中文abc";
        var binding = Binding<string>.From(() => value, v => value = v);
        var flow = new Flow(
                new Text("label"),
                new CounterComponent(),
                new TextField(binding).Key("field"))
            .Spacing(8);

        using var app = new TestApp(new VStack(flow), new Size(400, 300));
        app.Frame();

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
        var wideX = fieldNode.Frame.X;

        // 缩窄到 150：分行结果改变，输入框重新排列。
        app.Runtime.SetViewport(new Size(150, 300), 1.0);
        app.Runtime.ProcessPendingWork();
        app.Frame();

        Assert.NotEqual(wideX, fieldNode.Frame.X);

        // 节点身份、Key、组件实例与 UseState 全部保持。
        Assert.Same(fieldNode, app.Runtime.Root.Require<TextField>());
        Assert.Same(componentNode, app.Runtime.Root.Require<CounterComponent>());
        Assert.Same(componentView, componentNode.View);
        Assert.Same(addButton, componentNode.Require<Button>());
        Assert.True(CounterComponent.BuildCalls >= 1);
        Assert.Equal(1, componentNode.GetStateValue<int>("count"));

        // 中文数据、焦点、光标与选区保持。
        Assert.Equal(valueAfterTyping, value);
        Assert.Same(fieldNode, app.Runtime.FocusedNode);
        Assert.Same(state, fieldNode.InteractionState);
        Assert.Equal(caret, state.Caret);
        Assert.Equal(anchor, state.Anchor);
        Assert.True(state.HasSelection);

        // Tab 顺序按同一批节点实例保持。
        var orderAfter = app.Runtime.GetFocusOrder().ToList();
        Assert.Equal(orderBefore.Count, orderAfter.Count);
        for (var i = 0; i < orderBefore.Count; i++)
        {
            Assert.Same(orderBefore[i], orderAfter[i]);
        }
    }

    [Fact]
    public void Reflow_MovesHitTargets_AndOldCoordinatesDoNotHit()
    {
        var clicksA = 0;
        var clicksB = 0;
        var flow = new Flow(
                new Button("a", () => clicksA++).Frame(width: 80, height: 30),
                new Button("b", () => clicksB++).Frame(width: 80, height: 30))
            .Spacing(8);

        using var app = new TestApp(new VStack(flow), new Size(200, 100));
        app.Frame();

        var flowNode = app.Runtime.Root.Require<Flow>();
        var buttonB = flowNode.Children[1];
        Assert.Equal(88, buttonB.Frame.X, 3);

        app.Click(new Point(buttonB.Frame.Center.X, buttonB.Frame.Center.Y));
        Assert.Equal(1, clicksB);

        // 缩窄到 100：A、B 各占一行。
        app.Runtime.SetViewport(new Size(100, 100), 1.0);
        app.Runtime.ProcessPendingWork();
        app.Frame();

        Assert.Equal(0, buttonB.Frame.X, 3);
        Assert.Equal(30, buttonB.Frame.Y, 3);

        // 宽布局下 B 的旧坐标不再是任何按钮：不能命中。
        clicksA = 0;
        clicksB = 0;
        app.Click(new Point(128, 15));
        Assert.Equal(0, clicksA);
        Assert.Equal(0, clicksB);

        // 新位置命中 B。
        app.Click(new Point(buttonB.Frame.Center.X, buttonB.Frame.Center.Y));
        Assert.Equal(1, clicksB);
    }

    [Fact]
    public void FlowParameters_AreValidatedAtDeclaration()
    {
        var view = Item(10, 10);

        Assert.Throws<ArgumentOutOfRangeException>(() => new Flow(view) { Spacing = double.NaN });
        Assert.Throws<ArgumentOutOfRangeException>(() => new Flow(view) { Spacing = double.PositiveInfinity });
        Assert.Throws<ArgumentOutOfRangeException>(() => new Flow(view) { Spacing = -1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new Flow(view) { LineSpacing = double.NaN });
        Assert.Throws<ArgumentOutOfRangeException>(() => new Flow(view) { LineSpacing = double.NegativeInfinity });
        Assert.Throws<ArgumentOutOfRangeException>(() => new Flow(view) { LineSpacing = -0.5 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new Flow(view) { LineAlignment = (Alignment)99 });
        Assert.Throws<ArgumentOutOfRangeException>(() => FlowOf(view).Spacing(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => FlowOf(view).LineSpacing(-1));
        Assert.Throws<ArgumentNullException>(() => new Flow((IReadOnlyList<View>)null!));

        // 合法边界：零间距与合法对齐都能声明。
        var valid = new Flow(view) { Spacing = 0, LineSpacing = 0, LineAlignment = Alignment.Center };
        Assert.Equal(Alignment.Center, valid.LineAlignment);
    }

    [Fact]
    public void ExactWidthThreshold_IsDeterministic_AtBoundary()
    {
        // 210 = 100 + 8 + 100 + 8 - 6? 直接以“两项 + 间距”为阈值：208 放两项，208 少 0.01 放一项。
        foreach (var (width, expectedLines) in new[] { (207.99, 2), (208.0, 1), (208.01, 1) })
        {
            using var app = new TestApp(
                new VStack(FlowOf(Item(100, 20), Item(100, 20)).Spacing(8)),
                new Size(width, 200));
            app.Frame();

            var node = app.Runtime.Root.Require<Flow>();
            Assert.Equal(expectedLines, node.FlowCache!.Lines.Length);
        }
    }
}
