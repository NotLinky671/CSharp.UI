using CSharp.UI.Testing;
using Xunit;

namespace CSharp.UI.Tests;

/// <summary>
/// RL-05：<see cref="Responsive"/> 的安全内容构建事务 —— 最终有效宽度提案、
/// Measure 只登记不构建、同轮合并/撤销、描述版本、收敛预算、异常与卸载恢复、
/// 普通 VStack/ScrollView 中无 <c>.Flex</c>/<c>.FillWidth</c> 时的首帧收敛。
/// </summary>
public class ResponsiveTests
{
    // ------------------------------------------------------------------ 公开参数契约

    [Fact]
    public void ResponsiveInfo_RequiresBoundsConsistentWithFiniteWidth()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ResponsiveInfo(double.NaN, true));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ResponsiveInfo(-1, true));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ResponsiveInfo(100, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ResponsiveInfo(double.PositiveInfinity, true));

        var bounded = new ResponsiveInfo(100, true);
        Assert.Equal(100, bounded.AvailableWidth, 3);
        Assert.True(bounded.IsWidthBounded);

        var unbounded = new ResponsiveInfo(double.PositiveInfinity, false);
        Assert.True(double.IsPositiveInfinity(unbounded.AvailableWidth));
        Assert.False(unbounded.IsWidthBounded);
    }

    // -------------------------------------------------- 普通容器中的最终提案与首帧（RL-05 关键陷阱）

    [Fact]
    public void BareResponsive_InsidePlainVStackAndScrollView_ReceivesEffectiveProposalAndBuildsFirstFrame()
    {
        var widths = new List<double>();
        using var app = new TestApp(
            new ScrollView(new VStack(
                new Responsive(info => Builder(info, widths, narrowHeight: 120, wideHeight: 30)).Padding(20),
                new Text("tail"))),
            new Size(300, 400));

        var responsive = app.Runtime.Root.Require<Responsive>();
        var tail = app.Runtime.Root.Require<Text>();

        // 没有 .Flex/.FillWidth 也不能停在自然测量：必须收到扣除 Padding 后的 300-40=260。
        Assert.Equal<double>(new[] { 260d }, widths);

        // 有界宽度占满分配宽度，高度由内容自然决定。
        Assert.Equal(260, responsive.Frame.Width, 3);
        Assert.Equal(30, responsive.Frame.Height, 3);
        Assert.Single(responsive.Children);

        // 内容高度向上汇总：尾部位于 30+40 之后，滚动内容高度覆盖它。
        Assert.Equal(70, tail.Frame.Y, 3);
        var content = app.Runtime.Root.Children[0];
        Assert.Equal(content.DesiredSize.Height, tail.Frame.Bottom, 3);

        // 首帧必须包含真实内容命令，不是空帧；初次空内容已收敛，没有剩余请求。
        var commands = app.Frame();
        Assert.Contains(commands, command => command is RectangleCommand);
        Assert.Equal(0, app.Runtime.PendingResponsiveRequestCount);
    }

    [Fact]
    public void WidthChange_UsesFinalParentWidth_AndReAggregatesNaturalHeight()
    {
        var widths = new List<double>();
        using var app = new TestApp(
            new ScrollView(new VStack(
                new Responsive(info => Builder(info, widths, narrowHeight: 150, wideHeight: 40)).Padding(20),
                new Text("tail"))),
            new Size(300, 120));

        var responsive = app.Runtime.Root.Require<Responsive>();
        var tail = app.Runtime.Root.Require<Text>();
        var content = app.Runtime.Root.Children[0];

        Assert.Equal(80, tail.Frame.Y, 3);
        Assert.Equal(260, responsive.Frame.Width, 3);

        app.Runtime.SetViewport(new Size(180, 120), 1.0);
        app.Runtime.ProcessPendingWork();

        // 变窄：builder 以最终父宽度 180-40=140 重建，内容变高并向上重新汇总自然高度。
        Assert.Equal(140, widths[^1], 3);
        Assert.Equal(140, responsive.Frame.Width, 3);
        Assert.Equal(150, responsive.Frame.Height, 3);
        Assert.Equal(190, tail.Frame.Y, 3);
        Assert.Equal(content.DesiredSize.Height, tail.Frame.Bottom, 3);
        Assert.True(content.DesiredSize.Height > 120, $"内容高度 {content.DesiredSize.Height} 应超出视口。");

        // 滚动范围必须沿用新尺寸，而不是旧的 80 高度。
        app.Runtime.DispatchWheel(new WheelEvent(new Point(90, 60), 0, -3, KeyModifiers.None, WheelDeltaMode.Steps));
        Assert.True(app.Runtime.Root.ScrollOffsetY > 0, "内容变高后应能真正滚动到新范围。");
    }

    [Fact]
    public void UnboundedWidth_UsesNaturalContentSizeAndExplicitInfinityInfo()
    {
        var observed = new List<ResponsiveInfo>();
        using var app = new TestApp(
            new ScrollView(
                new Responsive(info =>
                {
                    observed.Add(info);
                    return new Rectangle(Color.Red) { FixedSize = new Size(48, 12) };
                }),
                ScrollAxis.Horizontal),
            new Size(100, 80));

        var responsive = app.Runtime.Root.Require<Responsive>();

        Assert.Single(observed);
        Assert.False(observed[0].IsWidthBounded);
        Assert.True(double.IsPositiveInfinity(observed[0].AvailableWidth));

        // 无界宽度回退自然尺寸，不能用任意大常数冒充（水平滚动容器会把内容拉伸到至少视口宽）。
        Assert.Equal(48, responsive.DesiredSize.Width, 3);
        Assert.Equal(12, responsive.DesiredSize.Height, 3);
    }

    // ------------------------------------------------------------- Measure 只登记，不构建

    [Fact]
    public void AncestorsWithoutDirectFlex_StillDeliverEffectiveProposal()
    {
        var cases = new (string Name, Func<View, View> Wrap)[]
        {
            ("VStack", child => new VStack(child)),
            ("ZStack", child => new ZStack(child)),
            ("Flow", child => new Flow(child)),
            ("AdaptiveStack", child => new AdaptiveStack(600, child)),
        };

        foreach (var (name, wrap) in cases)
        {
            var widths = new List<double>();
            var responsive = new Responsive(info =>
            {
                widths.Add(info.AvailableWidth);
                return new Rectangle(Color.Red) { FixedSize = new Size(8, 8) };
            });

            // 外层 VStack 没有直接弹性声明：有效提案必须穿过普通祖先到达动态内容。
            using var runtime = CreateRuntime(new VStack(wrap(responsive)), new Size(300, 200));

            Assert.True(widths.Count == 1, $"{name}: 应恰好构建一次，实际 {widths.Count} 次。");
            Assert.Equal(300, widths[0], 3);
            Assert.Single(runtime.Root.FindDescendant<Responsive>()!.Children);
        }
    }

    [Fact]
    public void BuilderReturningForEach_WrapsEntriesInImplicitContainer()
    {
        using var app = new TestApp(
            new VStack(new Responsive(_ => new ForEach<int>(
                [1, 2, 3],
                _ => new Rectangle(Color.Red) { FixedSize = new Size(10, 10) }))),
            new Size(300, 200));

        var responsive = app.Runtime.Root.Require<Responsive>();

        // 多条目内容包裹隐式容器（垂直堆叠），三项都参与布局。
        Assert.Single(responsive.Children);
        Assert.Equal(3, responsive.Children[0].Children.Count);
        Assert.Equal(30, responsive.Children[0].DesiredSize.Height, 3);
    }

    [Fact]
    public void InvalidateResponsiveContent_ForcesOneRebuildPerRequestAtSameWidth()
    {
        var calls = 0;
        var responsive = new Responsive(_ =>
        {
            calls++;
            return new Text($"v{calls}");
        });

        using var runtime = CreateRuntime(new VStack(responsive), new Size(300, 200));
        var node = runtime.Root.Require<Responsive>();

        Assert.Equal(1, calls);

        // RL-06 接入点：显式失效在同一宽度下也只重建一次，不改变状态语义。
        runtime.InvalidateResponsiveContent(node);
        runtime.ProcessPendingWork();
        Assert.Equal(2, calls);
        Assert.Equal("v2", Assert.IsType<Text>(node.Children[0].View).Content);

        runtime.InvalidateResponsiveContent(node);
        runtime.ProcessPendingWork();
        Assert.Equal(3, calls);
    }

    [Fact]
    public void EffectiveMeasureOnly_RegistersRequestWithoutBuildingOrReconciling()
    {
        var calls = 0;
        var responsive = new Responsive(_ =>
        {
            calls++;
            return new Rectangle(Color.Red) { FixedSize = new Size(10, 10) };
        });

        using var runtime = CreateRuntime(new VStack(responsive), new Size(300, 200), process: false);
        var node = runtime.Root.Require<Responsive>();

        var layout = new LayoutContext(runtime, runtime.Services.TextService, null, runtime.PixelScale);
        layout.Measure(runtime.Root, LayoutConstraints.Tight(new Size(300, 200)));

        // 只有测量：登记了最终有效提案，但没有执行 builder、没有协调子项、没有卸载。
        Assert.Equal(0, calls);
        Assert.Empty(node.Children);
        Assert.Equal(1, runtime.PendingResponsiveRequestCount);
        Assert.Equal(0, runtime.ResponsiveBuildCount);

        // 安全阶段消费请求并完成布局，首帧内容完整。
        runtime.ProcessPendingWork();
        Assert.Equal(1, calls);
        Assert.Single(node.Children);
        Assert.Equal(0, runtime.PendingResponsiveRequestCount);
    }

    [Fact]
    public void IntrinsicAndProbeMeasure_DoNotActivateCandidateContent()
    {
        var calls = 0;
        var responsive = new Responsive(_ =>
        {
            calls++;
            return new Rectangle(Color.Red) { FixedSize = new Size(10, 10) };
        });

        using var runtime = CreateRuntime(new VStack(responsive), new Size(300, 200), process: false);
        var node = runtime.Root.Require<Responsive>();
        var layout = new LayoutContext(runtime, runtime.Services.TextService, null, runtime.PixelScale);

        layout.PushIntrinsicMeasure();
        try
        {
            layout.Measure(runtime.Root, LayoutConstraints.Tight(new Size(300, 200)));
        }
        finally
        {
            layout.PopIntrinsicMeasure();
        }

        Assert.Equal(0, calls);
        Assert.Equal(0, runtime.PendingResponsiveRequestCount);

        using (layout.BeginProbe())
        {
            layout.MeasureFresh(node, new LayoutConstraints(0, 400, 0, 600));
        }

        Assert.Equal(0, calls);
        Assert.Equal(0, runtime.PendingResponsiveRequestCount);
        Assert.Empty(node.Children);
    }

    // -------------------------------------------------------------------- 同轮提案合并与撤销

    [Fact]
    public void SamePassProposals_LastEffectiveProposalWins()
    {
        var observed = new List<double>();
        var responsive = new Responsive(info =>
        {
            observed.Add(info.AvailableWidth);
            return new Rectangle(Color.Red) { FixedSize = new Size(10, 10) };
        });

        var host = new ProposalHost(responsive, _ => [300, 120]);
        using var runtime = CreateRuntime(host, new Size(400, 200));

        // 同轮先 300 后 120：以最后一次 120 为准，builder 只构建一次。
        Assert.Equal<double>(new[] { 120d }, observed);
        Assert.Equal(1, runtime.ResponsiveBuildCount);
    }

    [Fact]
    public void ReturnToStableProposal_CancelsStaleRequest()
    {
        var observed = new List<double>();
        var responsive = new Responsive(info =>
        {
            observed.Add(info.AvailableWidth);
            return new Rectangle(Color.Red) { FixedSize = new Size(info.AvailableWidth < 150 ? 10 : 20, 10) };
        });

        // 第一轮 100 稳定；之后一轮先提出 200 再回到 100：过时请求必须被撤销，不再构建。
        var host = new ProposalHost(responsive, index => index == 1 ? [100] : [200, 100]);
        using var runtime = CreateRuntime(host, new Size(400, 200));

        Assert.Equal<double>(new[] { 100d }, observed);
        Assert.Equal(1, runtime.ResponsiveBuildCount);
        Assert.Equal(0, runtime.PendingResponsiveRequestCount);
    }

    [Fact]
    public void UnboundedProbeThenFiniteAllocation_KeepsOnlyFiniteRequest()
    {
        var observed = new List<ResponsiveInfo>();
        var responsive = new Responsive(info =>
        {
            observed.Add(info);
            return new Rectangle(Color.Red) { FixedSize = new Size(10, 10) };
        });

        var host = new ProposalHost(responsive, _ => [double.PositiveInfinity, 120]);
        using var runtime = CreateRuntime(host, new Size(400, 200));

        // 初始无界探测被同轮的有限分配覆盖：builder 只拿到有限最终提案。
        Assert.Single(observed);
        Assert.True(observed[0].IsWidthBounded);
        Assert.Equal(120, observed[0].AvailableWidth, 3);
        Assert.Equal(1, runtime.ResponsiveBuildCount);
    }

    // --------------------------------------------------------------------- 描述版本与身份复用

    [Fact]
    public void SameWidthRepeatLayout_DoesNotRebuildButNewDescriptionDoesOnce()
    {
        var host = new DescriptorHost();

        using var app = new TestApp(host, new Size(300, 240));
        var responsive = app.Runtime.Root.Require<Responsive>();
        var child = responsive.Children[0];
        var initialCalls = host.Calls;

        Assert.Equal(1, initialCalls);
        Assert.Equal("version 0", Assert.IsType<Text>(child.View).Content);

        // 同宽重复布局：不重建。
        app.Runtime.SetViewport(new Size(300, 240), 1.0);
        app.Runtime.ProcessPendingWork();
        app.Runtime.BuildFrame();
        Assert.Equal(initialCalls, host.Calls);

        // 同宽的新 builder 描述：必须按新捕获值重建一次，且同父级同类型子节点复用。
        host.Version!.Value = 1;
        app.Runtime.ProcessPendingWork();

        Assert.Equal(initialCalls + 1, host.Calls);
        Assert.Same(child, responsive.Children[0]);
        Assert.Equal("version 1", Assert.IsType<Text>(responsive.Children[0].View).Content);

        // 更新后再次同宽重复布局仍不重建（不能因为闭包身份差异反复构建）。
        app.Runtime.SetViewport(new Size(300, 240), 1.0);
        app.Runtime.ProcessPendingWork();
        Assert.Equal(initialCalls + 1, host.Calls);
    }

    [Fact]
    public void DescriptionUpdateWithResize_BuildsOnceAtFinalWidth()
    {
        var host = new ResizingDescriptorHost();
        using var app = new TestApp(host, new Size(300, 240));
        var before = host.Calls;

        // 同一次更新里既替换描述又改变宽度：只能按最终宽度构建一次，不能先用过时宽度构建。
        host.Version!.Value = 1;
        app.Runtime.SetViewport(new Size(180, 240), 1.0);
        app.Runtime.ProcessPendingWork();

        Assert.Equal(before + 1, host.Calls);
        Assert.Equal(180, host.LastWidth, 3);
    }

    [Fact]
    public void ObsoleteWidthBranch_NeverUnmountsFinalCompatibleChild()
    {
        var host = new TransientBranchHost();
        using var app = new TestApp(host, new Size(300, 240));

        var field = app.Runtime.Root.Require<TextField>();
        app.Runtime.SetFocus(field);
        app.Runtime.DispatchKey(new KeyEvent(KeyEventKind.Down, UiKey.End));
        app.Runtime.DispatchKey(new KeyEvent(KeyEventKind.Down, UiKey.Left, KeyModifiers.Shift));
        var state = (TextFieldState)field.InteractionState!;
        Assert.True(state.HasSelection);

        // 新描述在最终宽度 180 下仍是 TextField；若先用过时宽度 300 构建会得到 Rectangle，
        // 从而卸载输入框并丢失焦点/选区。
        host.Version!.Value = 1;
        app.Runtime.SetViewport(new Size(180, 240), 1.0);
        app.Runtime.ProcessPendingWork();

        Assert.Same(field, app.Runtime.Root.Require<TextField>());
        Assert.Same(field, app.Runtime.FocusedNode);
        Assert.True(state.HasSelection);
    }

    [Fact]
    public void ExplicitInvalidationWithResize_UsesOnlyFinalProposalAndPreservesField()
    {
        var host = new TransientBranchHost();
        using var app = new TestApp(host, new Size(300, 240));

        var field = app.Runtime.Root.Require<TextField>();
        app.Runtime.SetFocus(field);
        app.Runtime.DispatchKey(new KeyEvent(KeyEventKind.Down, UiKey.End));
        app.Runtime.DispatchKey(new KeyEvent(KeyEventKind.Down, UiKey.Left, KeyModifiers.Shift));
        var state = (TextFieldState)field.InteractionState!;
        var before = host.Calls;

        // 显式内容失效与变宽同批：不能先用上一轮宽度挂载过时分支，只能按最终宽度构建一次。
        host.Version!.Value = 1;
        app.Runtime.SetViewport(new Size(180, 240), 1.0);
        app.Runtime.InvalidateResponsiveContent(app.Runtime.Root.Require<Responsive>());
        app.Runtime.ProcessPendingWork();

        Assert.Equal(before + 1, host.Calls);
        Assert.Equal(180, host.LastWidth, 3);
        Assert.Same(field, app.Runtime.Root.Require<TextField>());
        Assert.Same(field, app.Runtime.FocusedNode);
        Assert.True(state.HasSelection);
    }

    [Fact]
    public void ExplicitInvalidationWithParentConstraintChange_UsesOnlyFinalProposal()
    {
        var host = new ParentFrameHost();
        using var app = new TestApp(host, new Size(400, 240));

        var field = app.Runtime.Root.Require<TextField>();
        app.Runtime.SetFocus(field);
        app.Runtime.DispatchKey(new KeyEvent(KeyEventKind.Down, UiKey.End));
        app.Runtime.DispatchKey(new KeyEvent(KeyEventKind.Down, UiKey.Left, KeyModifiers.Shift));
        var state = (TextFieldState)field.InteractionState!;
        var before = host.Calls;

        // 视口不变、只收窄嵌套父 Frame 的宽度并显式失效：同样只能按最终 120 构建一次。
        host.FrameWidth = 120;
        host.Version!.Value = 1;
        app.Runtime.InvalidateResponsiveContent(app.Runtime.Root.Require<Responsive>());
        app.Runtime.ProcessPendingWork();

        Assert.Equal(before + 1, host.Calls);
        Assert.Equal(120, host.LastWidth, 3);
        Assert.Same(field, app.Runtime.Root.Require<TextField>());
        Assert.Same(field, app.Runtime.FocusedNode);
        Assert.True(state.HasSelection);
    }

    [Fact]
    public void DescriptionUpdateWithFailingBuilder_PreservesPreviousSubtree()
    {
        var host = new DescriptorHost();

        using var runtime = CreateRuntime(host, new Size(300, 240));
        var responsive = runtime.Root.Require<Responsive>();
        var child = responsive.Children[0];

        // 新描述构建失败：不能先用空 Children 清空既有子树。
        host.ThrowOnBuild = true;
        host.Version!.Value = 1;
        var exception = Assert.Throws<InvalidOperationException>(() => runtime.ProcessPendingWork());

        Assert.Equal("descriptor builder failed", exception.Message);
        Assert.Contains(runtime.Diagnostics, diagnostic => diagnostic.Contains("构建响应式内容失败"));
        Assert.Single(responsive.Children);
        Assert.Same(child, responsive.Children[0]);
        Assert.Equal("version 0", Assert.IsType<Text>(child.View).Content);

        // 修复 builder 后合法更新可恢复：内容按新描述重建，子节点身份保持。
        host.ThrowOnBuild = false;
        host.Version.Value = 2;
        runtime.ProcessPendingWork();

        Assert.Same(child, responsive.Children[0]);
        Assert.Equal("version 2", Assert.IsType<Text>(responsive.Children[0].View).Content);
        Assert.Equal(0, runtime.PendingResponsiveRequestCount);
    }

    // ------------------------------------------------------------------------ 收敛预算与诊断

    [Fact]
    public void DeepNestingAndManyIndependentNodes_ConvergeWithoutGlobalRoundFalsePositive()
    {
        var nestedCalls = 0;
        View Deep(int depth)
        {
            if (depth == 0)
            {
                return new Text("leaf");
            }

            return new Responsive(_ =>
            {
                nestedCalls++;
                return Deep(depth - 1);
            });
        }

        using (var app = new TestApp(Deep(16), new Size(300, 300)))
        {
            Assert.Equal(16, nestedCalls);
            Assert.NotNull(app.Runtime.Root.FindDescendant<Text>());
        }

        var siblingCalls = 0;
        using (var app = new TestApp(
            new VStack([.. Enumerable.Range(0, 40).Select(i => (View)new Responsive(_ =>
            {
                siblingCalls++;
                return new Text($"item {i}");
            }))]),
            new Size(300, 600)))
        {
            Assert.Equal(40, siblingCalls);
            Assert.Equal(40, app.Runtime.Root.FindDescendants<Text>().Count());
        }
    }

    [Fact]
    public void PerNodeOscillation_FailsControlledWithPathAndInputThenRecovers()
    {
        var responsive = new Responsive(info => new Rectangle(Color.Red)
        {
            FixedSize = new Size(10, info.AvailableWidth < 150 ? 10 : 20),
        });

        var host = new ProposalHost(responsive, index => index % 2 == 0 ? [100, 200] : [200, 100]);
        using var runtime = CreateRuntime(host, new Size(400, 200), process: false);

        // 该节点每轮都拿到不同宽度：真正振荡必须带节点路径与输入诊断受控失败。
        var exception = Assert.Throws<InvalidOperationException>(() => runtime.ProcessPendingWork());
        Assert.Contains("响应式内容循环", exception.Message);
        Assert.Contains("Responsive", exception.Message);
        Assert.Contains(ResponsiveTransactionManager.MaxRebuildsPerNodePerPass.ToString(), exception.Message);
        Assert.Contains(runtime.Diagnostics, diagnostic => diagnostic.Contains("响应式内容循环"));

        // 异常后队列与阶段标志被清理，不再残留请求。
        Assert.Equal(0, runtime.PendingResponsiveRequestCount);

        // 循环路径也不会把状态读取作用域泄漏到构建之外。
        Assert.Null(BuildTracker.Current);

        // 稳定下来的合法更新可恢复。
        host.StopAlternating();
        runtime.ProcessPendingWork();
        Assert.Equal(0, runtime.PendingResponsiveRequestCount);
        Assert.Single(runtime.Root.Require<Responsive>().Children);
    }

    [Fact]
    public void NonConvergentNewNodeTree_IsStoppedByOverallWorkBudget()
    {
        var builds = 0;

        View Explode() => new VStack([.. Enumerable.Range(0, 4).Select(_ => (View)new Responsive(_ =>
        {
            builds++;
            return Explode();
        }))]);

        using var runtime = CreateRuntime(Explode(), new Size(300, 300), process: false);

        var exception = Assert.Throws<InvalidOperationException>(() => runtime.ProcessPendingWork());
        Assert.Contains("整体工作预算", exception.Message);
        Assert.Contains(ResponsiveTransactionManager.MaxTransactionsPerPass.ToString(), exception.Message);
        Assert.Contains(runtime.Diagnostics, diagnostic => diagnostic.Contains("整体工作预算"));

        // 预算是本轮所有构建总数的固定上限，与现存节点数量无关：不能无限扩张。
        Assert.Equal(ResponsiveTransactionManager.MaxTransactionsPerPass, builds);
        Assert.Equal(0, runtime.PendingResponsiveRequestCount);
    }

    [Fact]
    public void IncreasingDepthDescription_IsStoppedByDepthGuardBeforeStackOverflow()
    {
        View Next() => new Responsive(_ => Next());

        using var runtime = CreateRuntime(Next(), new Size(300, 300), process: false);

        // 每轮新增下一层 Responsive 的描述会加深测量递归：必须受控失败，不能依赖栈溢出终止。
        // LayoutDepthLimitException 继承 InvalidOperationException，调用方按普通可捕获异常处理。
        var exception = Assert.Throws<LayoutDepthLimitException>(() => runtime.ProcessPendingWork());
        Assert.IsAssignableFrom<InvalidOperationException>(exception);
        Assert.Contains("递归深度", exception.Message);
        Assert.Contains($"上限 {LayoutContext.MaxMeasureDepth}", exception.Message);
        Assert.Contains(runtime.Diagnostics, diagnostic => diagnostic.Contains("递归深度"));
        Assert.Equal(0, runtime.PendingResponsiveRequestCount);
    }

    [Fact]
    public void DepthGuard_RepeatedRetriesStaySafe_AndFiniteContentRecoversInPlace()
    {
        var host = new GrowingHost();
        using var runtime = CreateRuntime(host, new Size(300, 300), process: false);

        // 同一错误描述重复处理：每次都在深度边界受控失败，不继续加深、不栈溢出。
        var first = Assert.Throws<LayoutDepthLimitException>(() => runtime.ProcessPendingWork());
        var buildsAfterFirstFailure = runtime.ResponsiveBuildCount;
        var second = Assert.Throws<LayoutDepthLimitException>(() => runtime.ProcessPendingWork());
        Assert.Contains("递归深度", first.Message);
        Assert.Contains("递归深度", second.Message);
        Assert.Equal(0, runtime.PendingResponsiveRequestCount);
        Assert.Equal(buildsAfterFirstFailure, runtime.ResponsiveBuildCount);

        // 合法切回有限内容：显式失效让顶层 builder 先于重新测量执行，过深子树被替换后布局恢复。
        host.Stop = true;
        var root = runtime.Root.FindDescendant<Responsive>()!;
        runtime.InvalidateResponsiveContent(root);
        runtime.ProcessPendingWork();

        Assert.Equal("recovered", Assert.IsType<Text>(runtime.Root.FindDescendant<Text>()!.View).Content);
        Assert.Equal(0, runtime.PendingResponsiveRequestCount);
    }

    // --------------------------------------------------------------------- 异常与生命周期

    [Fact]
    public void FailingBuilder_IsControlledAndRecoversOnNextLegalUpdate()
    {
        var shouldThrow = true;
        var calls = 0;
        var responsive = new Responsive(_ =>
        {
            calls++;
            if (shouldThrow)
            {
                throw new InvalidOperationException("boom");
            }

            return new Text("ok");
        });

        using var runtime = CreateRuntime(new VStack(responsive), new Size(300, 200), process: false);
        var node = runtime.Root.Require<Responsive>();

        var exception = Assert.Throws<InvalidOperationException>(() => runtime.ProcessPendingWork());
        Assert.Equal("boom", exception.Message);
        Assert.Empty(node.Children);
        Assert.Equal(0, runtime.PendingResponsiveRequestCount);

        shouldThrow = false;
        runtime.ProcessPendingWork();

        Assert.Equal(2, calls);
        Assert.Single(node.Children);
        Assert.IsType<Text>(node.Children[0].View);
    }

    [Fact]
    public void UnmountedResponsiveWithStaleRequest_IsNotBuilt()
    {
        var calls = 0;
        var host = new ConditionalResponsiveHost(() =>
        {
            calls++;
            return new Text("live");
        });

        using var runtime = CreateRuntime(host, new Size(300, 200), process: false);
        runtime.ProcessPendingWork();

        var responsive = runtime.Root.Require<Responsive>();
        var callsBefore = calls;

        // 手工登记一个尚未消费的过时请求。
        var layout = new LayoutContext(runtime, runtime.Services.TextService, null, runtime.PixelScale);
        layout.Measure(responsive, new LayoutConstraints(0, 123, 0, 200));
        Assert.Equal(1, runtime.PendingResponsiveRequestCount);

        // 卸载该节点：挂起请求必须被丢弃，不能对已卸载节点执行 builder。
        host.Show = false;
        host.Tick!.Value = 1;
        runtime.ProcessPendingWork();

        Assert.Equal(callsBefore, calls);
        Assert.Null(runtime.Root.FindDescendant<Responsive>());
        Assert.Equal(0, runtime.PendingResponsiveRequestCount);
    }

    [Fact]
    public void ReentrantBuildOrFrame_IsRejectedWithControlledDiagnostic()
    {
        var reentry = true;
        UiRuntime? runtimeRef = null;
        var responsive = new Responsive(_ =>
        {
            if (reentry)
            {
                runtimeRef!.ProcessPendingWork();
            }

            return new Text("ok");
        });

        using var runtime = CreateRuntime(new VStack(responsive), new Size(300, 200), process: false);
        runtimeRef = runtime;

        var processException = Assert.Throws<InvalidOperationException>(() => runtime.ProcessPendingWork());
        Assert.Contains("不可重入", processException.Message);

        // 解除重入后合法更新可恢复。
        reentry = false;
        runtime.ProcessPendingWork();
        Assert.Single(runtime.Root.Require<Responsive>().Children);

        // builder 内生成帧同样必须被阻止，不递归泵事件、不提交半成品。
        var frameReentry = true;
        UiRuntime? runtime2Ref = null;
        var second = new Responsive(_ =>
        {
            if (frameReentry)
            {
                runtime2Ref!.BuildFrame();
            }

            return new Text("ok");
        });

        using var runtime2 = CreateRuntime(new VStack(second), new Size(300, 200), process: false);
        runtime2Ref = runtime2;
        var frameException = Assert.Throws<InvalidOperationException>(() => runtime2.ProcessPendingWork());
        Assert.Contains("不可重入", frameException.Message);
    }

    // ----------------------------------------------------------------------------- 测试辅助

    private static View Builder(ResponsiveInfo info, List<double> observed, double narrowHeight, double wideHeight)
    {
        observed.Add(info.AvailableWidth);
        var narrow = info.IsWidthBounded && info.AvailableWidth < 200;
        return new Rectangle(Color.Red) { FixedSize = new Size(10, narrow ? narrowHeight : wideHeight) };
    }

    private static UiRuntime CreateRuntime(View root, Size viewport, bool process = true)
    {
        var runtime = new UiRuntime(root, new UiRuntimeServices
        {
            Dispatcher = new HeadlessUiDispatcher(),
            TextService = new StubTextService(),
            TextureService = new RecordingTextureService(),
            Clipboard = new StubClipboardService(),
            Clock = new TestUiClock(),
        });

        runtime.SetEnvironment(new EnvironmentValues { Theme = Theme.Light });
        runtime.SetViewport(viewport, 1.0);
        runtime.Start();
        if (process)
        {
            runtime.ProcessPendingWork();
        }

        return runtime;
    }

    /// <summary>按调用序号向单个子项提议一组宽度，用于验证同轮合并、覆盖与撤销。</summary>
    private sealed class ProposalHost : View
    {
        private readonly View[] _children;
        private readonly Func<int, double[]> _widths;
        private bool _stable;

        internal ProposalHost(View child, Func<int, double[]> widths)
        {
            _children = [child];
            _widths = widths;
        }

        internal int Measures { get; private set; }

        public override string DebugName => nameof(ProposalHost);

        internal override IReadOnlyList<View> Children => _children;

        internal override void Measure(ViewNode node, LayoutConstraints constraints, LayoutContext context)
        {
            Measures++;
            var child = node.ChildList[0];
            var widths = _stable ? new[] { child.DesiredSize.Width } : _widths(Measures);
            foreach (var width in widths)
            {
                context.Measure(child, new LayoutConstraints(0, width, 0, constraints.MaxHeight));
            }

            node.DesiredSize = child.DesiredSize;
        }

        internal override void Arrange(ViewNode node, Rect bounds, LayoutContext context)
        {
            node.Frame = bounds;
            context.Arrange(node.ChildList[0], bounds);
        }

        /// <summary>让后续测量固定在当前宽度，验证振荡受控失败后的恢复。</summary>
        internal void StopAlternating() => _stable = true;

        internal override bool LayoutPropertiesEquivalent(View other) => false;
    }

    /// <summary>用组件状态替换 Responsive 描述；可选在构建时抛错，验证描述更新路径。</summary>
    private sealed class DescriptorHost : Component
    {
        public State<int>? Version { get; private set; }

        public int Calls { get; private set; }

        public bool ThrowOnBuild { get; set; }

        protected override View Build()
        {
            Version = UseState("version", 0);
            var snapshot = Version.Value;
            return new Responsive(_ =>
            {
                Calls++;
                if (ThrowOnBuild)
                {
                    throw new InvalidOperationException("descriptor builder failed");
                }

                return new Text($"version {snapshot}");
            });
        }
    }

    /// <summary>记录构建时最终宽度的描述组件，用于验证“描述替换 + 变宽”只按最终宽度构建一次。</summary>
    private sealed class ResizingDescriptorHost : Component
    {
        public State<int>? Version { get; private set; }

        public int Calls { get; private set; }

        public double LastWidth { get; private set; }

        protected override View Build()
        {
            Version = UseState("version", 0);
            var snapshot = Version.Value;
            return new Responsive(info =>
            {
                Calls++;
                LastWidth = info.AvailableWidth;
                return new Text($"version {snapshot}");
            });
        }
    }

    /// <summary>在新描述下仅在宽布局返回 Rectangle 的组件，验证过时宽度分支不会卸载最终可复用子项。</summary>
    private sealed class TransientBranchHost : Component
    {
        private string _text = "中文 selection";

        public State<int>? Version { get; private set; }

        public int Calls { get; private set; }

        public double LastWidth { get; private set; }

        protected override View Build()
        {
            Version = UseState("version", 0);
            var snapshot = Version.Value;
            return new Responsive(info =>
            {
                Calls++;
                LastWidth = info.AvailableWidth;
                return snapshot == 1 && info.AvailableWidth > 200
                    ? new Rectangle(Color.Red)
                    : new TextField(new Binding<string>(() => _text, value => _text = value));
            });
        }
    }

    /// <summary>嵌套父 Frame 宽度可变的组件，验证显式失效与父约束变化同批时只按最终提案构建。</summary>
    private sealed class ParentFrameHost : Component
    {
        private string _text = "中文 selection";

        public State<int>? Version { get; private set; }

        public double FrameWidth { get; set; } = 300;

        public int Calls { get; private set; }

        public double LastWidth { get; private set; }

        protected override View Build()
        {
            Version = UseState("version", 0);
            var snapshot = Version.Value;
            return new FrameView(
                new Responsive(info =>
                {
                    Calls++;
                    LastWidth = info.AvailableWidth;
                    return snapshot == 1 && info.AvailableWidth > 200
                        ? new Rectangle(Color.Red)
                        : new TextField(new Binding<string>(() => _text, value => _text = value));
                }),
                width: FrameWidth);
        }
    }

    /// <summary>逐层增长、可在运行时切换到有限内容的组件，用于验证深度边界后的原地恢复。</summary>
    private sealed class GrowingHost : Component    {
        internal bool Stop { get; set; }

        protected override View Build() => new VStack(Next());

        private View Next() => new Responsive(_ => Stop ? new Text("recovered") : Next());
    }

    /// <summary>可在运行时移除 Responsive 子项的组件，验证卸载后的过时请求。</summary>
    private sealed class ConditionalResponsiveHost : Component    {
        private readonly Func<View> _builder;

        internal ConditionalResponsiveHost(Func<View> builder) => _builder = builder;

        internal bool Show { get; set; } = true;

        internal State<int>? Tick { get; private set; }

        protected override View Build()
        {
            Tick = UseState("tick", 0);
            _ = Tick.Value;
            return Show
                ? new VStack(new Responsive(_ => _builder()))
                : new VStack(new Text("empty"));
        }
    }
}
