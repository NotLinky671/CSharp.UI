using CSharp.UI;
using Xunit;

namespace CSharp.UI.Tests;

/// <summary>
/// RL-01：测量提案（Probe / 有效）缓存隔离与作用域恢复、按轴尺寸特征传播、有界溢出诊断基础。
/// </summary>
public class LayoutProbeTests
{
    private static LayoutContext NewContext(TestApp app) => new(app.Runtime, app.Text, app.Images, 1.0);

    private static Rectangle FixedRectangle(double width, double height, Color? color = null)
        => new(color ?? Color.Red) { FixedSize = new Size(width, height) };

    [Fact]
    public void ProbeThenEffectiveMeasurement_SameConstraints_IsNotSkippedByCache()
    {
        using var app = new TestApp(new VStack(FixedRectangle(33.5, 12)), new Size(400, 300));
        app.Frame();

        var node = app.Runtime.Root.Require<Rectangle>();
        var originalConstraints = node.LastConstraints;
        var probeConstraints = new LayoutConstraints(0, 50, 0, 100);
        var context = NewContext(app);
        var measuresBefore = context.MeasureCount;

        using (context.BeginProbe())
        {
            context.Measure(node, probeConstraints);
            Assert.Equal(MeasureProposalKind.Probe, node.LastProposalKind);
            Assert.Equal(probeConstraints, node.LastConstraints);
        }

        // 探测退出后恢复进入前的有效提案，探测不被当作最终提案。
        Assert.Equal(MeasureProposalKind.Effective, node.LastProposalKind);
        Assert.Equal(originalConstraints, node.LastConstraints);
        Assert.Equal(33.5, node.DesiredSize.Width, 3);
        Assert.Equal(measuresBefore + 1, context.MeasureCount);

        // 相同约束下的有效测量必须重新执行并登记有效提案，不能被探测缓存跳过。
        context.Measure(node, probeConstraints);
        Assert.Equal(measuresBefore + 2, context.MeasureCount);
        Assert.Equal(MeasureProposalKind.Effective, node.LastProposalKind);
        Assert.Equal(probeConstraints, node.LastConstraints);
        Assert.Equal(33.5, node.DesiredSize.Width, 3);

        // 第二次相同约束的有效测量命中有效提案缓存，不再重新执行。
        context.Measure(node, probeConstraints);
        Assert.Equal(measuresBefore + 2, context.MeasureCount);
    }

    [Fact]
    public void ProbeAtDifferentConstraints_DoesNotOverwriteEffectiveProposal()
    {
        using var app = new TestApp(new VStack(FixedRectangle(33.5, 12)), new Size(400, 300));
        app.Frame();

        var node = app.Runtime.Root.Require<Rectangle>();
        var context = NewContext(app);
        var effectiveConstraints = new LayoutConstraints(0, 50, 0, 100);

        context.Measure(node, effectiveConstraints);
        Assert.Equal(MeasureProposalKind.Effective, node.LastProposalKind);
        var effectiveSize = node.DesiredSize;

        using (context.BeginProbe())
        {
            context.Measure(node, new LayoutConstraints(0, 10, 0, 100));
            Assert.Equal(10, node.DesiredSize.Width, 3);
        }

        // 有效提案完整恢复：尺寸、约束与提案性质都不被探测结果覆盖。
        Assert.Equal(effectiveSize, node.DesiredSize);
        Assert.Equal(effectiveConstraints, node.LastConstraints);
        Assert.Equal(MeasureProposalKind.Effective, node.LastProposalKind);
        Assert.True(node.MeasureValid);
    }

    [Fact]
    public void NestedProbeScopes_PropagateProposalKindAndRestorePerLevel()
    {
        using var app = new TestApp(new VStack(FixedRectangle(20, 10)), new Size(400, 300));
        app.Frame();

        var node = app.Runtime.Root.Require<Rectangle>();
        var originalConstraints = node.LastConstraints;
        var context = NewContext(app);
        var outerConstraints = new LayoutConstraints(0, 60, 0, 100);
        var innerConstraints = new LayoutConstraints(0, 30, 0, 100);

        using (context.BeginProbe())
        {
            Assert.Equal(MeasureProposalKind.Probe, context.CurrentProposal);
            context.Measure(node, outerConstraints);
            Assert.Equal(MeasureProposalKind.Probe, node.LastProposalKind);

            using (context.BeginProbe())
            {
                Assert.Equal(MeasureProposalKind.Probe, context.CurrentProposal);
                context.Measure(node, innerConstraints);
                Assert.Equal(MeasureProposalKind.Probe, node.LastProposalKind);
                Assert.Equal(innerConstraints, node.LastConstraints);
            }

            // 内层退出恢复外层探测结果，而不是进入外层的状态。
            Assert.Equal(MeasureProposalKind.Probe, context.CurrentProposal);
            Assert.Equal(outerConstraints, node.LastConstraints);
        }

        Assert.Equal(MeasureProposalKind.Effective, context.CurrentProposal);
        Assert.False(context.IsProbing);
        Assert.Equal(originalConstraints, node.LastConstraints);
        Assert.Equal(MeasureProposalKind.Effective, node.LastProposalKind);
    }

    [Fact]
    public void ProbeScope_ExceptionInsideScope_StillRestoresProposalState()
    {
        using var app = new TestApp(new VStack(FixedRectangle(20, 10)), new Size(400, 300));
        app.Frame();

        var node = app.Runtime.Root.Require<Rectangle>();
        var originalConstraints = node.LastConstraints;
        var context = NewContext(app);

        Action probeThatThrows = () =>
        {
            using var scope = context.BeginProbe();
            context.Measure(node, new LayoutConstraints(0, 30, 0, 100));
            Assert.Equal(MeasureProposalKind.Probe, node.LastProposalKind);
            throw new InvalidOperationException("探测期间异常");
        };

        Assert.Throws<InvalidOperationException>(probeThatThrows);

        Assert.False(context.IsProbing);
        Assert.Equal(MeasureProposalKind.Effective, context.CurrentProposal);
        Assert.Equal(originalConstraints, node.LastConstraints);
        Assert.Equal(MeasureProposalKind.Effective, node.LastProposalKind);
    }

    [Fact]
    public void ProbeAlone_DoesNotRegisterLayoutProposal()
    {
        using var app = new TestApp(new VStack(FixedRectangle(10, 10)), new Size(200, 100));
        app.Frame();

        var node = app.Runtime.Root.Require<Rectangle>();
        var originalConstraints = node.LastConstraints;
        var context = NewContext(app);

        app.Runtime.InvalidateLayout(node);
        Assert.False(node.MeasureValid);

        using (context.BeginProbe())
        {
            context.Measure(node, LayoutConstraints.TightWidth(20));
            Assert.True(node.MeasureValid);
            Assert.Equal(MeasureProposalKind.Probe, node.LastProposalKind);
        }

        // 探测没有留下布局提案：测量缓存恢复为“未测量”。
        Assert.False(node.MeasureValid);
        Assert.Equal(originalConstraints, node.LastConstraints);

        context.Measure(node, LayoutConstraints.TightWidth(20));
        Assert.True(node.MeasureValid);
        Assert.Equal(MeasureProposalKind.Effective, node.LastProposalKind);
    }

    [Fact]
    public void Probe_DoesNotBuildMountOrChangeTree()
    {
        using var app = new TestApp(new ProbeGuardComponent(), new Size(300, 200));
        app.Frame();

        var builds = app.Runtime.BuildCount;
        var nodeCount = app.Runtime.Root.Descendants().Count();
        var context = NewContext(app);

        using (var scope = context.BeginProbe())
        {
            context.Measure(app.Runtime.Root, LayoutConstraints.Tight(new Size(250, 180)));
            Assert.Equal(MeasureProposalKind.Probe, app.Runtime.Root.LastProposalKind);
            Assert.True(scope.CapturedNodeCount > 0, "探测应记录被改写测量状态的节点。");
        }

        Assert.Equal(builds, app.Runtime.BuildCount);
        Assert.Equal(nodeCount, app.Runtime.Root.Descendants().Count());
        Assert.All(app.Runtime.Root.Descendants(), node => Assert.True(node.IsMounted));
        Assert.False(app.Runtime.HasPendingBuild);
        Assert.Equal(LayoutConstraints.Tight(new Size(300, 200)), app.Runtime.Root.LastConstraints);
        Assert.Equal(MeasureProposalKind.Effective, app.Runtime.Root.LastProposalKind);
    }

    [Fact]
    public void Probe_DoesNotLeakMeasurementCacheIntoEffectiveProposal()
    {
        using var app = new TestApp(new VStack(new Text("abcdefghij").Wrap()), new Size(400, 300));
        app.Frame();

        var text = app.Runtime.Root.Require<Text>();
        var originalCache = text.ViewCache;
        Assert.IsType<TextLayoutResult>(originalCache);

        var context = NewContext(app);
        using (context.BeginProbe())
        {
            context.Measure(text, LayoutConstraints.BoundedWidth(20));
            Assert.NotEqual(((TextLayoutResult)originalCache!).Size, text.DesiredSize);
        }

        // 探测产生的排版缓存不得泄漏为最终提案的一部分。
        Assert.Same(originalCache, text.ViewCache);
        Assert.Equal(((TextLayoutResult)originalCache!).Size, text.DesiredSize);
        Assert.Equal(MeasureProposalKind.Effective, text.LastProposalKind);
    }

    [Fact]
    public void SizeDeclarations_PropagateThroughPaddingFrameAndPlainModifiers()
    {
        var view = new VStack(
            FixedRectangle(40, 20).Padding(10),
            FixedRectangle(40, 20).Padding(10).Background(Color.Blue),
            FixedRectangle(40, 20).Frame(width: 200),
            FixedRectangle(40, 20).Frame(width: 200).Padding(5),
            FixedRectangle(40, 20).Padding(5).Frame(width: 200));

        using var app = new TestApp(view, new Size(600, 400));
        app.Frame();

        var stack = app.Runtime.Root;
        Assert.Equal(5, stack.Children.Count);

        // Padding 在内容外侧：显式尺寸叠加内边距。
        var padded = stack.Children[0];
        Assert.Equal(new Size(60, 40), padded.DesiredSize);
        Assert.Equal(60d, padded.GetLayoutFeatures().Horizontal.ExplicitSize!.Value);
        Assert.Equal(40d, padded.GetLayoutFeatures().Vertical.ExplicitSize!.Value);

        // 普通修饰符（Background 等）透传内容的尺寸声明。
        var plainModifier = stack.Children[1];
        Assert.Equal(new Size(60, 40), plainModifier.DesiredSize);
        Assert.Equal(60d, plainModifier.GetLayoutFeatures().Horizontal.ExplicitSize!.Value);
        Assert.Equal(40d, plainModifier.GetLayoutFeatures().Vertical.ExplicitSize!.Value);

        // Frame 固定外框宽度，垂直方向透传内容声明。
        var frame = stack.Children[2];
        Assert.Equal(new Size(200, 20), frame.DesiredSize);
        Assert.Equal(200d, frame.GetLayoutFeatures().Horizontal.ExplicitSize!.Value);
        Assert.Equal(20d, frame.GetLayoutFeatures().Vertical.ExplicitSize!.Value);

        // 外层 Padding + 内层 Frame：固定宽度叠加内边距。
        var paddingOutsideFrame = stack.Children[3];
        Assert.Equal(new Size(210, 30), paddingOutsideFrame.DesiredSize);
        Assert.Equal(210d, paddingOutsideFrame.GetLayoutFeatures().Horizontal.ExplicitSize!.Value);
        Assert.Equal(30d, paddingOutsideFrame.GetLayoutFeatures().Vertical.ExplicitSize!.Value);

        // 外层 Frame + 内层 Padding：外层固定声明优先，不叠加内层。
        var frameOutsidePadding = stack.Children[4];
        Assert.Equal(new Size(200, 30), frameOutsidePadding.DesiredSize);
        Assert.Equal(200d, frameOutsidePadding.GetLayoutFeatures().Horizontal.ExplicitSize!.Value);
        Assert.Equal(30d, frameOutsidePadding.GetLayoutFeatures().Vertical.ExplicitSize!.Value);

        // 修饰符顺序有语义：外层与内层 Frame 得到不同的主轴声明。
        Assert.NotEqual(
            paddingOutsideFrame.GetLayoutFeatures().Horizontal.ExplicitSize,
            frameOutsidePadding.GetLayoutFeatures().Horizontal.ExplicitSize);
    }

    [Fact]
    public void FixedFrameWidth_SurvivesTruncatedDesiredSize()
    {
        var view = new VStack(FixedRectangle(40, 20).Frame(width: 240).Padding(10).Background(Color.Blue));
        using var app = new TestApp(view, new Size(100, 300));
        app.Frame();

        var background = app.Runtime.Root.Children[0];

        // 父约束（100）已经截断了 DesiredSize，但固定声明必须仍然可见。
        Assert.Equal(100, background.DesiredSize.Width, 3);
        Assert.Equal(260d, background.GetLayoutFeatures().Horizontal.ExplicitSize!.Value);

        var frame = background.Children[0].Children[0];
        Assert.True(frame.DesiredSize.Width <= 90);
        Assert.True(frame.GetLayoutFeatures().Horizontal.IsFixed);
        Assert.Equal(240d, frame.GetLayoutFeatures().Horizontal.ExplicitSize!.Value);
        Assert.Equal(240d, frame.GetLayoutFeatures().Horizontal.EffectiveMin);
        Assert.Equal(240d, frame.GetLayoutFeatures().Horizontal.EffectiveMax);
    }

    [Fact]
    public void FractionalZeroAndUnboundedConstraints_ProduceFiniteGeometry()
    {
        var view = new VStack(
            FixedRectangle(33.5, 0),
            new Text("wrap me please").Wrap(),
            new Spacer())
            .Padding(2.5);

        using var app = new TestApp(view, new Size(200, 150));
        app.Frame();

        var rectangle = app.Runtime.Root.Require<Rectangle>();
        var rectangleFeatures = rectangle.GetLayoutFeatures();
        Assert.Equal(33.5, rectangleFeatures.Horizontal.ExplicitSize!.Value);
        Assert.Equal(0d, rectangleFeatures.Vertical.ExplicitSize!.Value);
        Assert.True(double.IsFinite(rectangleFeatures.Horizontal.NaturalSize));
        Assert.True(double.IsFinite(rectangleFeatures.Vertical.NaturalSize));

        // 未声明上限的自然尺寸项：下限 0、上限正无穷，而不是 NaN。
        var textFeatures = app.Runtime.Root.Require<Text>().GetLayoutFeatures();
        Assert.Equal(0d, textFeatures.Horizontal.EffectiveMin);
        Assert.Equal(double.PositiveInfinity, textFeatures.Horizontal.EffectiveMax);
        Assert.True(double.IsFinite(textFeatures.Horizontal.NaturalSize));
        Assert.True(double.IsFinite(textFeatures.Vertical.NaturalSize));

        // 弹性空白的增长权重沿用旧 FlexGrow 语义，且被规范化为有限值。
        Assert.Equal(1d, app.Runtime.Root.Require<Spacer>().GetLayoutFeatures().Vertical.Grow);

        foreach (var node in app.Runtime.Root.Descendants())
        {
            Assert.True(double.IsFinite(node.DesiredSize.Width) && double.IsFinite(node.DesiredSize.Height));
            Assert.True(double.IsFinite(node.Frame.X) && double.IsFinite(node.Frame.Y));
            Assert.True(node.Frame.Width >= 0 && node.Frame.Height >= 0);
            Assert.True(double.IsFinite(node.Frame.Width) && double.IsFinite(node.Frame.Height));
        }

        // 无界探测不得产生 NaN/无穷几何，且退出后恢复有效提案。
        var context = NewContext(app);
        var probed = context.Probe(rectangle, LayoutConstraints.Unbounded);
        Assert.True(double.IsFinite(probed.Width) && double.IsFinite(probed.Height));
        Assert.True(rectangle.MeasureValid);
        Assert.Equal(MeasureProposalKind.Effective, rectangle.LastProposalKind);

        var probedFeatures = context.ProbeFeatures(rectangle, LayoutConstraints.Unbounded);
        Assert.Equal(33.5, probedFeatures.Horizontal.ExplicitSize!.Value);
        Assert.Equal(MeasureProposalKind.Effective, rectangle.LastProposalKind);
    }

    [Fact]
    public void OverflowDiagnostic_IncludesNodePath_AndIsNotRepeatedPerLayoutPass()
    {
        using var app = new TestApp(
            new VStack(FixedRectangle(900, 10), FixedRectangle(800, 10, Color.Blue)),
            new Size(100, 300));
        app.Frame();

        var overflow = app.Runtime.Diagnostics.Where(d => d.Contains("布局溢出")).ToList();
        Assert.Equal(2, overflow.Count);
        Assert.All(overflow, message => Assert.Contains("Rectangle", message));
        Assert.All(overflow, message => Assert.Contains("VStack", message));

        var suppressedBefore = app.Runtime.LayoutDiagnostics.SuppressedCount;

        // 重新布局同一批稳定溢出：不逐帧重复记录，只计入抑制计数。
        app.Runtime.InvalidateLayout(app.Runtime.Root);
        app.Frame();

        Assert.Equal(2, app.Runtime.Diagnostics.Count(d => d.Contains("布局溢出")));
        Assert.True(app.Runtime.LayoutDiagnostics.SuppressedCount > suppressedBefore);
    }

    [Fact]
    public void LayoutDiagnostics_AreBoundedByCapacityAndPerPassBudget()
    {
        var items = Enumerable.Range(0, 200)
            .Select(index => (View)FixedRectangle(500 + index, 10))
            .ToArray();

        using var app = new TestApp(new VStack(items), new Size(100, 300));
        app.Frame();

        var diagnostics = app.Runtime.LayoutDiagnostics;
        Assert.True(
            app.Runtime.Diagnostics.Count(d => d.Contains("布局溢出")) <= LayoutDiagnostics.MaxReportsPerPass,
            "单轮布局输出的新诊断条数必须受配额限制。");

        for (var pass = 0; pass < 12; pass++)
        {
            app.Runtime.InvalidateLayout(app.Runtime.Root);
            app.Frame();
        }

        Assert.True(diagnostics.ReportedCount <= diagnostics.Capacity, "去重表必须有界。");
        Assert.True(diagnostics.SuppressedCount > 0);
    }

    [Fact]
    public void UnboundedDiagnostic_IsKeyedByNodePathAndDeduplicated()
    {
        using var app = new TestApp(new VStack(new Spacer()), new Size(200, 100));
        app.Frame();

        var spacer = app.Runtime.Root.Require<Spacer>();
        var diagnostics = app.Runtime.LayoutDiagnostics;
        var context = new LayoutContext(app.Runtime, app.Text, app.Images, 1.0, diagnostics);
        diagnostics.BeginPass();

        context.ReportUnbounded(spacer, LayoutAxis.Horizontal, "FillWidth");
        context.ReportUnbounded(spacer, LayoutAxis.Horizontal, "FillWidth");

        var unbounded = Assert.Single(app.Runtime.Diagnostics, d => d.Contains("无界水平轴"));
        Assert.Contains("Spacer", unbounded);
        Assert.True(diagnostics.SuppressedCount > 0);
    }

    [Fact]
    public void Probe_RestoresEnvironmentCapturedBeforeMeasurement()
    {
        using var app = new TestApp(new VStack(new Text("x").Foreground(Color.Red)), new Size(200, 100));
        app.Frame();

        var text = app.Runtime.Root.Require<Text>();
        Assert.Equal(Color.Red, text.Environment.ResolvedForeground);

        // 人为把环境标记值打回默认：探测必须在改写环境之前拍快照，退出时完整恢复（而不是恢复成探测期间解析的值）。
        text.Environment = EnvironmentValues.Default;
        var context = NewContext(app);

        using (context.BeginProbe())
        {
            context.Measure(text, LayoutConstraints.BoundedWidth(60));
            Assert.Equal(Color.Red, text.Environment.ResolvedForeground);
        }

        Assert.Same(EnvironmentValues.Default, text.Environment);
    }

    [Fact]
    public void Probe_CacheHitThatResolvesEnvironment_IsRestored()
    {
        using var app = new TestApp(new VStack(new Text("x").Foreground(Color.Red)), new Size(200, 100));
        app.Frame();

        var text = app.Runtime.Root.Require<Text>();
        var resolved = text.Environment;
        Assert.Equal(Color.Red, resolved.ResolvedForeground);

        // 模拟旧视觉环境：探测使用相同约束命中测量缓存，但环境仍需重新解析为父级继承值。
        text.Environment = resolved with { Foreground = Color.Blue };
        var saved = text.Environment;
        var context = NewContext(app);

        using (context.BeginProbe())
        {
            context.Measure(text, text.LastConstraints);
            Assert.Equal(Color.Red, text.Environment.ResolvedForeground);
        }

        // 缓存命中路径同样必须先拍快照再改写环境，退出后旧环境完整恢复。
        Assert.Same(saved, text.Environment);
        Assert.True(text.MeasureValid);
    }

    [Fact]
    public void Probe_MeasureFresh_RestoresEffectiveCacheEntry()
    {
        using var app = new TestApp(new VStack(new Text("abcdefghij").Wrap()), new Size(400, 300));
        app.Frame();

        var text = app.Runtime.Root.Require<Text>();
        var originalCache = text.ViewCache;
        var originalConstraints = text.LastConstraints;
        Assert.True(text.MeasureValid);

        var context = NewContext(app);
        using (context.BeginProbe())
        {
            context.MeasureFresh(text, LayoutConstraints.BoundedWidth(40));
            Assert.Equal(MeasureProposalKind.Probe, text.LastProposalKind);
        }

        Assert.True(text.MeasureValid);
        Assert.Equal(MeasureProposalKind.Effective, text.LastProposalKind);
        Assert.Equal(originalConstraints, text.LastConstraints);
        Assert.Same(originalCache, text.ViewCache);
    }

    [Fact]
    public void ConflictingFrameBounds_AreRetainedAndVisibleToConsumers()
    {
        // 外框分配特征与内容/子树固定要求分开：外层框的基准是外框自身自然尺寸，固定 240 的硬要求单独保留。
        var view = new VStack(
            new FrameView(FixedRectangle(40, 20).Frame(width: 240), maxWidth: 100),
            new FrameView(FixedRectangle(40, 20).Frame(width: 240), minWidth: 300),
            FixedRectangle(40, 20).Frame(240, null, 300, null, null, null),
            new FrameView(FixedRectangle(40, 20).Frame(width: 240), maxWidth: 100).Padding(5));

        using var app = new TestApp(view, new Size(600, 400));
        app.Frame();

        var stack = app.Runtime.Root;

        // maxWidth:100 包裹固定 240：分配基准/上限是 100，子树仍需 240（矛盾且可诊断）；默认布局不变。
        var capped = stack.Children[0];
        Assert.Equal(100, capped.DesiredSize.Width, 3);
        var cappedFeatures = capped.GetLayoutFeatures().Horizontal;
        Assert.Null(cappedFeatures.ExplicitSize);
        Assert.Equal(100d, cappedFeatures.MaxSize!.Value);
        Assert.Equal(100d, cappedFeatures.BaseSize);
        Assert.Equal(100d, cappedFeatures.EffectiveMax);
        Assert.Equal(240d, cappedFeatures.ContentMinSize!.Value);
        Assert.True(cappedFeatures.HasConflictingBounds);

        // minWidth:300 包裹固定 240：基准/下限是 300，内容可居中放置，不伪造矛盾区间。
        var raised = stack.Children[1];
        Assert.Equal(300, raised.DesiredSize.Width, 3);
        var raisedFeatures = raised.GetLayoutFeatures().Horizontal;
        Assert.Null(raisedFeatures.ExplicitSize);
        Assert.Equal(300d, raisedFeatures.MinSize!.Value);
        Assert.Equal(300d, raisedFeatures.BaseSize);
        Assert.Equal(300d, raisedFeatures.EffectiveMin);
        Assert.True(double.IsPositiveInfinity(raisedFeatures.EffectiveMax));
        Assert.False(raisedFeatures.HasConflictingBounds);

        // 同一个 Frame 同时声明固定宽度与较大最小值：自身盒子的下限大于上限。
        var sameFrame = stack.Children[2].GetLayoutFeatures().Horizontal;
        Assert.Equal(240d, sameFrame.ExplicitSize!.Value);
        Assert.Equal(300d, sameFrame.MinSize!.Value);
        Assert.Equal(300d, sameFrame.EffectiveMin);
        Assert.Equal(240d, sameFrame.EffectiveMax);
        Assert.True(sameFrame.HasConflictingBounds);

        // Padding 在外层：外框上限与子树硬要求都叠加内边距。
        var padded = stack.Children[3].GetLayoutFeatures().Horizontal;
        Assert.Null(padded.ExplicitSize);
        Assert.Equal(110d, padded.MaxSize!.Value);
        Assert.Equal(110d, padded.EffectiveMax);
        Assert.Equal(250d, padded.ContentMinSize!.Value);
        Assert.True(padded.HasConflictingBounds);
    }

    [Fact]
    public void OuterFrameBounds_DefineBoxBasisSeparateFromInnerHardSize()
    {
        // 与独立复查探针一致：外层框的基准/边界来自外框自身，内层硬尺寸只进入 ContentMinSize。
        var cases = new (string Name, View View, double Basis, double? Min, double? Max, double ContentMin)[]
        {
            ("fixed+outerMax", new FrameView(FixedRectangle(40, 20).Frame(width: 240), maxWidth: 100), 100, null, 100, 240),
            ("fixed+outerMin", new FrameView(FixedRectangle(40, 20).Frame(width: 240), minWidth: 300), 300, 300, null, 300),
            ("padding+outerMax", new FrameView(FixedRectangle(40, 20).Frame(width: 240).Padding(10), maxWidth: 100), 100, null, 100, 260),
            ("padding+outerMin", new FrameView(FixedRectangle(40, 20).Frame(width: 240).Padding(10), minWidth: 300), 300, 300, null, 300),
            ("innerPadding", FixedRectangle(40, 20).Padding(10).Frame(width: 240), 240, 240, 240, 240),
            ("outerPadding", FixedRectangle(40, 20).Frame(width: 240).Padding(10), 260, 260, 260, 260),
            ("outerFixed", FixedRectangle(40, 20).Frame(width: 240).Frame(width: 100), 100, 100, 100, 240),
        };

        foreach (var item in cases)
        {
            using var app = new TestApp(item.View, new Size(400, 300));
            app.Frame();

            var features = app.Runtime.Root.GetLayoutFeatures().Horizontal;
            Assert.Equal(item.Basis, features.BaseSize);
            if (item.Min is { } min)
            {
                Assert.Equal(min, features.EffectiveMin);
            }

            if (item.Max is { } max)
            {
                Assert.Equal(max, features.EffectiveMax);
            }

            Assert.Equal(item.ContentMin, features.ContentMinSize!.Value);
        }
    }

    [Fact]
    public void Component_ForwardsContentSizeFeatures()
    {
        using var app = new TestApp(new FixedWidthComponent(), new Size(400, 300));
        app.Frame();

        // 单内容组件不引入额外层级：Build() 结果的固定尺寸必须能被上层识别。
        var features = app.Runtime.Root.GetLayoutFeatures().Horizontal;
        Assert.Equal(180d, features.ExplicitSize!.Value);
        Assert.Equal(180d, features.BaseSize);
        Assert.Equal(180d, features.ContentMinSize!.Value);
    }
}

/// <summary>RL-01 测试用组件：Build() 返回单内容固定尺寸视图，用于验证组件特征透传。</summary>
internal sealed class FixedWidthComponent : Component
{
    protected override View Build() => new Rectangle(Color.Red) { FixedSize = new Size(180, 24) };
}

/// <summary>RL-01 测试用组件：用于验证探测不会触发组件重建或改变挂载状态。</summary>
internal sealed class ProbeGuardComponent : Component
{
    protected override View Build()
    {
        var count = UseState("count", 0);
        return new VStack(
            new Text($"count={count.Value}"),
            new Button("add", () => count.Value++));
    }
}
