using CSharp.UI;
using Xunit;

namespace CSharp.UI.Tests;

/// <summary>
/// RL-02：Flex 与 FillWidth 的公开 API、分配算法（增长/压缩/冻结/再分配/无界回退）与旧语义保持。
/// </summary>
public class FlexLayoutTests
{
    private static View FlexItem(
        double? basis = null,
        double grow = 1,
        double shrink = 1,
        double min = 0,
        double max = double.PositiveInfinity,
        FlexAxis axis = FlexAxis.Main,
        Color? color = null)
        => new Rectangle(color ?? Color.Red).Flex(grow, shrink, basis, min, max, axis);

    private static Color OtherColor => Color.Blue;

    [Fact]
    public void Grow_DistributesFreeSpaceByWeight()
    {
        using var app = new TestApp(
            new HStack(
                FlexItem(basis: 100, grow: 1),
                FlexItem(basis: 100, grow: 3, color: OtherColor))
                .Align(Alignment.Stretch),
            new Size(500, 100));
        app.Frame();

        var stack = app.Runtime.Root;
        // 基准 100 + 100 = 200，剩余 300 按 1:3 分配 → 175 / 325。
        Assert.Equal(175, stack.Children[0].Frame.Width, 3);
        Assert.Equal(325, stack.Children[1].Frame.Width, 3);
        Assert.Equal(175, stack.Children[1].Frame.X, 3);
        Assert.Equal(500, stack.Frame.Width, 3);
    }

    [Fact]
    public void Shrink_RedistributesAfterMinFreeze()
    {
        using var app = new TestApp(
            new HStack(
                FlexItem(basis: 300, shrink: 2, min: 250),
                FlexItem(basis: 300, shrink: 1, color: OtherColor))
                .Align(Alignment.Stretch),
            new Size(300, 100));
        app.Frame();

        var stack = app.Runtime.Root;
        // 需要压缩 300：A 先按权重降到 250 冻结，剩余量继续分配给 B → 250 / 50。
        Assert.Equal(250, stack.Children[0].Frame.Width, 3);
        Assert.Equal(50, stack.Children[1].Frame.Width, 3);
        Assert.Equal(250, stack.Children[0].Frame.X + stack.Children[0].Frame.Width, 3);
    }

    [Fact]
    public void Grow_RedistributesAfterMaxFreeze()
    {
        using var app = new TestApp(
            new HStack(
                FlexItem(basis: 100, grow: 1, max: 150),
                FlexItem(basis: 100, grow: 1, color: OtherColor))
                .Align(Alignment.Stretch),
            new Size(500, 100));
        app.Frame();

        var stack = app.Runtime.Root;
        // 剩余 300：A 到 150 冻结，剩下的都给 B → 150 / 350。
        Assert.Equal(150, stack.Children[0].Frame.Width, 3);
        Assert.Equal(350, stack.Children[1].Frame.Width, 3);
    }

    [Fact]
    public void ZeroBasis_HasNoCompressibleAmount()
    {
        using var app = new TestApp(
            new HStack(
                FlexItem(basis: 0, shrink: 1),
                FlexItem(basis: 100, shrink: 1, color: OtherColor))
                .Align(Alignment.Stretch),
            new Size(50, 100));
        app.Frame();

        var stack = app.Runtime.Root;
        // 零 basis 的压缩权重为 0，不参与压缩；全部不足量由 B 承担。
        Assert.Equal(0, stack.Children[0].Frame.Width, 3);
        Assert.Equal(50, stack.Children[1].Frame.Width, 3);
    }

    [Fact]
    public void FractionalWidths_ArePreservedWithoutRounding()
    {
        using var app = new TestApp(
            new HStack(
                FlexItem(basis: 100, grow: 1),
                FlexItem(basis: 100, grow: 1, color: OtherColor))
                .Align(Alignment.Stretch),
            new Size(333.5, 100));
        app.Frame();

        var stack = app.Runtime.Root;
        Assert.Equal(166.75, stack.Children[0].Frame.Width, 3);
        Assert.Equal(166.75, stack.Children[1].Frame.Width, 3);
    }

    [Fact]
    public void UnboundedMainAxis_UsesBasisOrNaturalSize()
    {
        using var app = new TestApp(
            new ScrollView(
                new HStack(
                    FlexItem(basis: 200, grow: 1),
                    FlexItem(grow: 1, color: OtherColor))
                    .Align(Alignment.Stretch),
                ScrollAxis.Horizontal),
            new Size(500, 100));
        app.Frame();

        var stack = app.Runtime.Root.Require<HStack>();
        // 主轴无界：不做无穷剩余空间分配；显式 basis 保留，自动 basis 回退自然尺寸（0）。
        Assert.Equal(200, stack.DesiredSize.Width, 3);
        Assert.Equal(200, stack.Children[0].Frame.Width, 3);
        Assert.Equal(0, stack.Children[1].Frame.Width, 3);
    }

    [Fact]
    public void FillWidth_UnboundedWidth_FallsBackToNaturalSize()
    {
        using var app = new TestApp(
            new ScrollView(
                new HStack(new Text("abcd").Wrap().FillWidth()).Align(Alignment.Stretch),
                ScrollAxis.Horizontal),
            new Size(500, 100));
        app.Frame();

        var text = app.Runtime.Root.Require<Text>();
        Assert.Equal(33.6, text.Frame.Width, 3);
        Assert.True(double.IsFinite(text.Frame.Width));
    }

    [Fact]
    public void Spacer_CoexistsWithDeclaredFlexItems()
    {
        using var app = new TestApp(
            new HStack(
                new Spacer(),
                FlexItem(basis: 100, grow: 1),
                new Rectangle(OtherColor) { FixedSize = new Size(100, 20) })
                .Align(Alignment.Stretch),
            new Size(400, 100));
        app.Frame();

        var stack = app.Runtime.Root;
        // 固定项 100 + Flex 基准 100 = 200，剩余 200 由 Spacer 与 Flex 各分一半。
        Assert.Equal(100, stack.Children[0].Frame.Width, 3);
        Assert.Equal(200, stack.Children[1].Frame.Width, 3);
        Assert.Equal(100, stack.Children[2].Frame.Width, 3);
    }

    [Fact]
    public void FillWidth_InHorizontalStack_FillsRemainingWidth()
    {
        using var app = new TestApp(
            new HStack(
                new Rectangle(OtherColor) { FixedSize = new Size(100, 20) },
                new Rectangle(Color.Red).FillWidth())
                .Align(Alignment.Stretch),
            new Size(400, 100));
        app.Frame();

        var stack = app.Runtime.Root;
        Assert.Equal(100, stack.Children[0].Frame.Width, 3);
        Assert.Equal(300, stack.Children[1].Frame.Width, 3);
    }

    [Fact]
    public void FillWidth_InVerticalStack_LimitsContentRectByFrameMaxWidth()
    {
        using var app = new TestApp(
            new VStack(
                new FrameView(new Rectangle(Color.Red) { FixedSize = new Size(360, 20) }, maxWidth: 300).FillWidth(),
                new Rectangle(OtherColor).FillWidth()),
            new Size(400, 100));
        app.Frame();

        var first = app.Runtime.Root.Children[0];
        var frame = first.Children[0];
        var content = frame.Children[0];

        // 交叉轴 FillWidth 占满可用宽度，但不得突破子树声明的 maxWidth；实际内容矩形也要受限。
        Assert.Equal(300, first.Frame.Width, 3);
        Assert.Equal(300, frame.Frame.Width, 3);
        Assert.Equal(300, content.Frame.Width, 3);
        Assert.Equal(300, content.DesiredSize.Width, 3);
        Assert.Equal(400, app.Runtime.Root.Children[1].Frame.Width, 3);
    }

    [Fact]
    public void FillWidth_DoesNotCancelHardFrame()
    {
        using var app = new TestApp(
            new HStack(
                new Rectangle(OtherColor) { FixedSize = new Size(80, 20) },
                new Rectangle(Color.Red).Frame(width: 120).FillWidth())
                .Align(Alignment.Stretch),
            new Size(400, 100));
        app.Frame();

        var stack = app.Runtime.Root;
        Assert.Equal(80, stack.Children[0].Frame.Width, 3);
        Assert.Equal(120, stack.Children[1].Frame.Width, 3);
    }

    [Fact]
    public void MaxWidthFrame_WithFillWidth_InEitherOrder_LimitsActualRect()
    {
        foreach (var fillOutside in new[] { true, false })
        {
            var rect = new Rectangle(Color.Red);
            View item = fillOutside
                ? new FrameView(rect, maxWidth: 300).FillWidth()
                : new FrameView(rect.FillWidth(), maxWidth: 300);

            using var app = new TestApp(
                new HStack(item, new Rectangle(OtherColor) { FixedSize = new Size(40, 20) })
                    .Align(Alignment.Stretch),
                new Size(400, 100));
            app.Frame();

            var node = app.Runtime.Root.Children[0];
            Assert.Equal(300, node.Frame.Width, 3);
        }
    }

    [Fact]
    public void SameAxisNestedFlex_OuterDeclarationWinsWithoutStacking()
    {
        using var app = new TestApp(
            new HStack(
                new Rectangle(Color.Red).Flex(basis: 100, grow: 1).Flex(basis: 100, grow: 3),
                FlexItem(basis: 100, grow: 1, color: OtherColor))
                .Align(Alignment.Stretch),
            new Size(500, 100));
        app.Frame();

        var stack = app.Runtime.Root;
        // 外层 grow=3 生效（若权重叠加成 4，A 会得到 340）。
        Assert.Equal(325, stack.Children[0].Frame.Width, 3);
        Assert.Equal(175, stack.Children[1].Frame.Width, 3);
    }

    [Fact]
    public void NestedFlex_InsidePlainContainer_ReceivesEffectiveAllocation()
    {
        // 普通 VStack 的子项只做自然尺寸测量：内部含 Flex 的 HStack 必须拿到有效提案并真正分配，
        // 同约束的自然测量缓存不得掩盖有效分配。
        using (var app = new TestApp(
            new VStack(new HStack(new Text("nested").Flex(basis: 80))),
            new Size(400, 600)))
        {
            app.Frame();
            var text = app.Runtime.Root.Require<Text>();
            Assert.Equal(400, text.Frame.Width, 3);
            Assert.Equal(MeasureProposalKind.Effective, text.LastProposalKind);
            Assert.True(text.LastConstraints.IsWidthTight);
            Assert.Equal(400, text.LastConstraints.MaxWidth, 3);
        }

        // ScrollView 的内容区 = frame − 右侧 gutter：嵌套 Flex 收到的是该 contentRect 宽，不是 frame 宽。
        using (var app = new TestApp(
            new ScrollView(new VStack(new HStack(new Text("nested").Flex(basis: 80)))),
            new Size(400, 600)))
        {
            app.Frame();
            var scroll = app.Runtime.Root.Require<ScrollView>();
            var text = app.Runtime.Root.Require<Text>();
            Assert.Equal(scroll.Frame.Width - ScrollbarMetrics.Thickness, text.Frame.Width, 3);
            Assert.Equal(scroll.Frame.Right - ScrollbarMetrics.Thickness, text.Frame.Right, 3);
        }
    }

    [Fact]
    public void ArrangeFlex_AddsParentCrossAxisOrigin_AcrossAxesAndAlignments()
    {
        // 纵向容器：交叉轴偏移是 X，必须叠加父容器原点（Padding 20 → VStack 内容区 X=20）。
        using (var app = new TestApp(
            new VStack(Item(30, 20, basis: 40)).Padding(20),
            new Size(400, 600)))
        {
            app.Frame();
            var item = app.Runtime.Root.Children[0].Children[0];
            Assert.Equal(20, item.Frame.X, 3);
            Assert.Equal(20, item.Frame.Y, 3);
        }

        // 纵向容器 + End 对齐：偏移量本身也要按内容区计算后再叠加原点。
        using (var app = new TestApp(
            new VStack(Item(30, 20, basis: 40)).Align(Alignment.End).Padding(20),
            new Size(400, 600)))
        {
            app.Frame();
            var item = app.Runtime.Root.Children[0].Children[0];
            Assert.Equal(350, item.Frame.X, 3);
            Assert.Equal(20, item.Frame.Y, 3);
        }

        // 横向容器：交叉轴偏移是 Y，Center 与 End 都要叠加父容器原点。
        using (var app = new TestApp(
            new HStack(Item(30, 20, basis: 40)).Padding(20),
            new Size(400, 600)))
        {
            app.Frame();
            var item = app.Runtime.Root.Children[0].Children[0];
            Assert.Equal(20, item.Frame.X, 3);
            Assert.Equal(290, item.Frame.Y, 3);
        }

        using (var app = new TestApp(
            new HStack(Item(30, 20, basis: 40)).Align(Alignment.End).Padding(20),
            new Size(400, 600)))
        {
            app.Frame();
            var item = app.Runtime.Root.Children[0].Children[0];
            Assert.Equal(20, item.Frame.X, 3);
            Assert.Equal(560, item.Frame.Y, 3);
        }
    }

    [Fact]
    public void ArrangeFlex_DrawAndHitTestUseSameActualPosition()
    {
        var clicks = 0;
        using var app = new TestApp(
            new VStack(new Button("b", () => clicks++).Flex(basis: 40, grow: 0)).Padding(20),
            new Size(400, 600));

        var button = app.Runtime.Root.Require<Button>();
        Assert.Equal(20, button.Frame.X, 3);

        // 绘制与命中共用节点 Frame：背景矩形命令的 bounds 必须与实际矩形一致。
        Assert.Contains(
            app.Frame(),
            command => command is RectangleCommand rectangle && rectangle.Bounds == button.Frame);

        // 左侧内边距不属于按钮：点击不能命中（旧实现会把子项放回 X=0）。
        app.Click(new Point(10, button.Frame.Y + (button.Frame.Height / 2)));
        Assert.Equal(0, clicks);

        app.Click(new Point(button.Frame.X + (button.Frame.Width / 2), button.Frame.Y + (button.Frame.Height / 2)));
        Assert.Equal(1, clicks);
    }

    [Fact]
    public void Shrink_WeightsUseInitiallyConstrainedBasis()
    {
        using var app = new TestApp(
            new HStack(
                FlexItem(basis: 1000, max: 100),
                FlexItem(basis: 100, color: OtherColor))
                .Align(Alignment.Stretch),
            new Size(100, 100));
        app.Frame();

        var stack = app.Runtime.Root;
        // 初始尺寸都被夹取到 100：压缩权重必须按 100:100 分配成 50/50，
        // 而不是按原始 basis 1000:100 得到约 9.09/90.91。
        Assert.Equal(50, stack.Children[0].Frame.Width, 3);
        Assert.Equal(50, stack.Children[1].Frame.Width, 3);
    }

    [Fact]
    public void OuterSameAxisFlex_ReplacesInnerElasticQuotaButKeepsHardFrame()
    {
        // 内层弹性 min90 必须被外层同轴 Flex 完整覆盖：外层 grow0/basis10/max20 时宽度为 10。
        using (var app = new TestApp(
            new HStack(new Text("a").Flex(basis: 100, min: 90).Flex(grow: 0, basis: 10, max: 20)),
            new Size(50, 100)))
        {
            app.Frame();
            Assert.Equal(10, app.Runtime.Root.Children[0].Frame.Width, 3);
        }

        // 硬 Frame 声明不属于弹性配额：外层覆盖弹性边界时仍保留固定宽度。
        using (var app = new TestApp(
            new HStack(new Text("a").Frame(width: 120).Flex(basis: 100, min: 90).Flex(grow: 0, basis: 10, max: 20)),
            new Size(50, 100)))
        {
            app.Frame();
            Assert.Equal(120, app.Runtime.Root.Children[0].Frame.Width, 3);
            Assert.Contains(app.Runtime.Diagnostics, message => message.Contains("布局溢出"));
        }

        // 外层 FillWidth 与内层 Flex 交叠时规则一致：内层 basis/min 不再生效，填充按可用宽度分配。
        using (var app = new TestApp(
            new HStack(new Text("a").Flex(basis: 100, min: 90).FillWidth()),
            new Size(50, 100)))
        {
            app.Frame();
            Assert.Equal(50, app.Runtime.Root.Children[0].Frame.Width, 3);
        }
    }

    [Fact]
    public void FillWidth_ResetsInnerElasticBasisAndClearsInnerFill()
    {
        // 横向无界：外层 FillWidth 覆盖内层 Flex basis 后按契约回退自然文字宽度（4 字符 × 8.4）。
        using (var app = new TestApp(
            new ScrollView(new HStack(new Text("abcd").Flex(basis: 300).FillWidth()), ScrollAxis.Horizontal),
            new Size(400, 100)))
        {
            app.Frame();
            Assert.Equal(33.6, app.Runtime.Root.Require<Text>().Frame.Width, 3);
        }

        // 有界宽度：FillWidth 的基准是自然尺寸，与兄弟弹性项按权重分配剩余空间。
        using (var app = new TestApp(
            new HStack(
                new Text("abcd").Flex(basis: 300).FillWidth(),
                new Text("ef").Flex(basis: 100))
                .Align(Alignment.Stretch),
            new Size(400, 100)))
        {
            app.Frame();
            var stack = app.Runtime.Root;
            Assert.Equal(166.8, stack.Children[0].Frame.Width, 3);
            Assert.Equal(233.2, stack.Children[1].Frame.Width, 3);
        }

        // 外层普通 Flex 覆盖内层 FillWidth：同一轴上不再保留填充标记（交叉轴按对齐而非占满）。
        using (var app = new TestApp(
            new VStack(new Text("abcd").FillWidth()).Align(Alignment.Center),
            new Size(400, 600)))
        {
            app.Frame();
            Assert.Equal(400, app.Runtime.Root.Require<Text>().Frame.Width, 3);
        }

        using (var app = new TestApp(
            new VStack(new Text("abcd").FillWidth().Flex(grow: 0, basis: 20, axis: FlexAxis.Horizontal))
                .Align(Alignment.Center),
            new Size(400, 600)))
        {
            app.Frame();
            var text = app.Runtime.Root.Require<Text>();
            Assert.Equal(33.6, text.Frame.Width, 3);
            Assert.Equal(183.2, text.Frame.X, 3);
        }
    }

    private static View Item(double width, double height, double basis)
        => new Rectangle(Color.Red) { FixedSize = new Size(width, height) }.Flex(grow: 0, basis: basis);

    [Fact]
    public void DeepPlainStackChain_WithNestedFlex_KeepsMeasureWorkBounded()
    {
        // 普通容器需要把有效提案传到嵌套 Flex；这条链路不得退化为逐层指数级重测。
        View tree = new Text("leaf").Flex(basis: 80, axis: FlexAxis.Horizontal);
        for (var i = 0; i < 12; i++)
        {
            tree = new VStack(tree);
        }

        using var app = new TestApp(tree, new Size(400, 600));
        app.Frame();

        var text = app.Runtime.Root.Require<Text>();
        var nodeCount = app.Runtime.Root.Descendants().Count();
        Assert.Equal(400, text.Frame.Width, 3);
        Assert.True(
            app.Runtime.LastMeasureCount <= (4 * nodeCount) + 64,
            $"节点 {nodeCount} 个，测量 {app.Runtime.LastMeasureCount} 次，超出线性上限。");
    }

    [Fact]
    public void ExplicitHorizontalAxis_DoesNotBecomeHeightInVerticalStack()
    {
        using var app = new TestApp(
            new VStack(
                new Rectangle(Color.Red)
                    .Frame(height: 30)
                    .Flex(grow: 1, basis: 200, axis: FlexAxis.Horizontal)),
            new Size(300, 200));
        app.Frame();

        var child = app.Runtime.Root.Children[0];
        var features = child.GetLayoutFeatures(LayoutAxis.Vertical);

        // 宽度 basis 200 不得变成高度：垂直轴仍为自然尺寸 30，水平轴保留 basis。
        Assert.Equal(30, child.Frame.Height, 3);
        Assert.Equal(300, child.Frame.Width, 3);
        Assert.Equal(200d, features.Horizontal.Basis!.Value);
        Assert.Null(features.Vertical.Basis);
    }

    [Fact]
    public void ExplicitVerticalAxis_DoesNotBecomeWidthInHorizontalStack()
    {
        using var app = new TestApp(
            new HStack(
                new Rectangle(Color.Red)
                    .Frame(width: 40)
                    .Flex(grow: 1, basis: 80, axis: FlexAxis.Vertical))
                .Align(Alignment.Stretch),
            new Size(400, 100));
        app.Frame();

        var child = app.Runtime.Root.Children[0];
        Assert.Equal(40, child.Frame.Width, 3);
        Assert.Equal(80d, child.GetLayoutFeatures(LayoutAxis.Horizontal).Vertical.Basis!.Value);
    }

    [Fact]
    public void FixedFrame_IsNotCompressedByFlexAllocation()
    {
        using var app = new TestApp(
            new HStack(new Rectangle(Color.Red).Frame(width: 240).Flex(shrink: 1))
                .Align(Alignment.Stretch),
            new Size(100, 100));
        app.Frame();

        var child = app.Runtime.Root.Children[0];
        Assert.Equal(240, child.Frame.Width, 3);
        Assert.Contains(app.Runtime.Diagnostics, d => d.Contains("布局溢出"));
    }

    [Fact]
    public void UnsatisfiableMins_ReportOverflowWithoutNegativeSizes()
    {
        using var app = new TestApp(
            new HStack(
                FlexItem(basis: 200, min: 200),
                FlexItem(basis: 200, min: 150, color: OtherColor))
                .Align(Alignment.Stretch),
            new Size(200, 100));
        app.Frame();

        var stack = app.Runtime.Root;
        Assert.Equal(200, stack.Children[0].Frame.Width, 3);
        Assert.Equal(150, stack.Children[1].Frame.Width, 3);
        Assert.All(stack.Children, child => Assert.True(child.Frame.Width >= 0));
        Assert.Contains(app.Runtime.Diagnostics, d => d.Contains("布局溢出"));
    }

    [Fact]
    public void NonFlexChildren_ReceiveFinalTightProposal()
    {
        using var app = new TestApp(
            new HStack(
                FlexItem(basis: 100, grow: 1),
                new Text("abcd").Wrap())
                .Align(Alignment.Stretch),
            new Size(500, 100));
        app.Frame();

        var text = app.Runtime.Root.Require<Text>();
        Assert.Equal(MeasureProposalKind.Effective, text.LastProposalKind);
        Assert.True(text.LastConstraints.IsWidthTight);
        Assert.Equal(text.Frame.Width, text.LastConstraints.MaxWidth, 3);
        Assert.Equal(33.6, text.Frame.Width, 3);
    }

    [Fact]
    public void UnusedFlexCapability_KeepsLegacyStackSemantics()
    {
        // 未使用 Flex/FillWidth 的堆叠保持旧语义：Spacer 吸收全部剩余空间。
        using var app = new TestApp(
            new HStack(
                new Spacer(),
                new Rectangle(Color.Red) { FixedSize = new Size(100, 20) }),
            new Size(400, 100));
        app.Frame();

        var stack = app.Runtime.Root;
        Assert.Equal(300, stack.Children[0].Frame.Width, 3);
        Assert.Equal(100, stack.Children[1].Frame.Width, 3);
        Assert.Null(stack.ChildMainAllocation);
    }

    [Fact]
    public void OuterPadding_AddsToFlexibleBasis()
    {
        using var app = new TestApp(
            new HStack(
                new Rectangle(Color.Red).Flex(basis: 100, grow: 1).Padding(10),
                FlexItem(basis: 100, grow: 1, color: OtherColor))
                .Align(Alignment.Stretch),
            new Size(500, 100));
        app.Frame();

        var stack = app.Runtime.Root;
        // Padding 在外层把基准撑到 120：总基准 220，剩余 280 平分 → 260 / 240。
        Assert.Equal(260, stack.Children[0].Frame.Width, 3);
        Assert.Equal(240, stack.Children[1].Frame.Width, 3);
    }

    [Fact]
    public void MediumDepthAlternatingStacks_KeepMeasureWorkBounded()
    {
        var root = BuildAlternating(depth: 7, breadth: 2, level: 0);
        using var app = new TestApp(root, new Size(600, 400));
        app.Frame();

        var nodeCount = app.Runtime.Root.Descendants().Count();
        var measures = app.Runtime.LastMeasureCount;

        // 自然尺寸测量禁止嵌套完整分配：工作量应接近线性，而不是按深度指数增长。
        // 自然尺寸测量禁止嵌套完整分配：工作量应接近线性（实测约 3×节点数），而不是按深度指数增长。
        Assert.True(
            measures <= (4 * nodeCount) + 64,
            $"节点 {nodeCount} 个，测量 {measures} 次，超出线性上限。");
    }

    private static View BuildAlternating(int depth, int breadth, int level)
    {
        if (depth == 0)
        {
            return new Rectangle(Color.Red).Flex(basis: 20, grow: 1, shrink: 1);
        }

        var children = new View[breadth];
        for (var i = 0; i < breadth; i++)
        {
            children[i] = BuildAlternating(depth - 1, breadth, level + 1);
        }

        View stack = level % 2 == 0 ? new HStack(children) : new VStack(children);
        return stack.Flex(basis: 40, grow: 1, shrink: 1);
    }

    [Fact]
    public void Slider_FlexWidth_KeepsHitTestAndDragAccurate()
    {
        var value = 0.25;
        using var app = new TestApp(
            new HStack(new Slider(Binding<double>.From(() => value, v => value = v), 0, 1, 0.01).Flex(basis: 100, grow: 1, shrink: 1))
                .Align(Alignment.Stretch),
            new Size(400, 100));
        app.Frame();

        var slider = app.Runtime.Root.Require<Slider>();
        // 滑块默认宽度 160，弹性后占满整个 400 宽的主轴。
        Assert.Equal(400, slider.Frame.Width, 3);

        app.Click(TrackPoint(slider, 0.75));
        Assert.Equal(0.75, value, 3);

        // 缩窄后命中坐标必须按新的实际矩形换算，而不是沿用旧宽度。
        app.Runtime.SetViewport(new Size(200, 100), 1.0);
        app.Runtime.ProcessPendingWork();
        app.Frame();
        Assert.Equal(200, slider.Frame.Width, 3);

        app.Click(TrackPoint(slider, 0.25));
        Assert.Equal(0.25, value, 3);
    }

    private static Point TrackPoint(ViewNode slider, double ratio)
    {
        // Slider 的轨道左右各内缩一个圆点半径（8），命中换算与绘制共用同一矩形。
        var trackX = slider.Frame.X + 8;
        var trackWidth = slider.Frame.Width - 16;
        return new Point(trackX + (trackWidth * ratio), slider.Frame.Y + (slider.Frame.Height / 2));
    }

    [Fact]
    public void FillWidth_WithOuterMinFrame_ExpandsLegallyWithoutConflict()
    {
        using var app = new TestApp(
            new VStack(
                new FrameView(new Rectangle(Color.Red) { FixedSize = new Size(120, 20) }, minWidth: 300).FillWidth()),
            new Size(400, 100));
        app.Frame();

        var fill = app.Runtime.Root.Children[0];
        var frame = fill.Children[0];

        // 外层合法 min 扩张不是矛盾：FillWidth 继续占满 400，不受 min 300 限制。
        Assert.Equal(400, fill.Frame.Width, 3);
        Assert.Equal(400, frame.Frame.Width, 3);
        Assert.False(frame.GetLayoutFeatures(LayoutAxis.Vertical).Horizontal.HasConflictingBounds);
    }

    [Fact]
    public void ReverseOrConflictingFrameBounds_AreDiagnosedWithoutCrash()
    {
        // min > max 的逆序边界：不能触发 Math.Clamp 异常。
        using (var app = new TestApp(
            new VStack(new FrameView(new Rectangle(Color.Red), minWidth: 200, maxWidth: 100)),
            new Size(400, 100)))
        {
            app.Frame();
            var frame = app.Runtime.Root.Require<FrameView>();
            Assert.True(double.IsFinite(frame.Frame.Width));
            Assert.Contains(app.Runtime.Diagnostics, d => d.Contains("布局溢出"));
        }

        // 内容硬要求 360 超过外框上限 100：保留上限并诊断，不谎称内容已压缩。
        using (var app = new TestApp(
            new VStack(new FrameView(new Rectangle(Color.Red) { FixedSize = new Size(360, 20) }, maxWidth: 100)),
            new Size(400, 100)))
        {
            app.Frame();
            var frame = app.Runtime.Root.Require<FrameView>();
            Assert.Equal(100, frame.DesiredSize.Width, 3);
            Assert.Contains(app.Runtime.Diagnostics, d => d.Contains("布局溢出") && d.Contains("360"));
        }
    }

    [Fact]
    public void FlexParameters_AreValidatedAtDeclaration()
    {
        var view = new Rectangle(Color.Red);

        Assert.Throws<ArgumentOutOfRangeException>(() => view.Flex(grow: double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => view.Flex(grow: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => view.Flex(grow: double.PositiveInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() => view.Flex(shrink: double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => view.Flex(shrink: -0.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => view.Flex(basis: double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => view.Flex(basis: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => view.Flex(basis: double.PositiveInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() => view.Flex(min: double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => view.Flex(min: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => view.Flex(max: double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => view.Flex(max: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => view.Flex(min: 200, max: 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => view.Flex(axis: (FlexAxis)99));

        // 合法边界：零权重、显式 basis 0 与正无穷 max 都可以声明。
        var flexible = view.Flex(grow: 0, shrink: 0, basis: 0, min: 0, max: double.PositiveInfinity);
        Assert.IsType<FlexView>(flexible);
    }

    [Fact]
    public void FlexView_ExposesValidatedImmutableParameters()
    {
        var flex = new FlexView(new Rectangle(Color.Red), grow: 2, shrink: 3, basis: 120, min: 60, max: 300, axis: FlexAxis.Horizontal);

        Assert.Equal(2, flex.Grow);
        Assert.Equal(3, flex.Shrink);
        Assert.Equal(120, flex.Basis);
        Assert.Equal(60, flex.Min);
        Assert.Equal(300, flex.Max);
        Assert.Equal(FlexAxis.Horizontal, flex.Axis);
        Assert.Throws<ArgumentOutOfRangeException>(() => new FlexView(new Rectangle(Color.Red), min: 10, max: 5));
    }
}
