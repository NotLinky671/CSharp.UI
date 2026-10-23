using CSharp.UI.Testing;
using Xunit;

namespace CSharp.UI.Tests;

/// <summary>
/// RL-06：<see cref="Responsive"/> builder 的状态依赖与生命周期 —— 独立读取作用域、
/// 状态失效派发到动态内容目标、条件依赖替换、异常/循环清理、卸载与绑定依赖释放。
/// </summary>
public class ResponsiveStateTests
{
    // ------------------------------------------------- builder 直接读取状态（Component 不读取）

    [Fact]
    public void BuilderOnlyStateRead_UpdatesContentWithoutRebuildingOwner()
    {
        var host = new OwnerHost();
        using var app = new TestApp(host, new Size(300, 200));

        var node = app.Runtime.Root.Require<Responsive>();
        var child = node.Children[0];
        var builds = host.Builds;

        Assert.Equal("A", Assert.IsType<Text>(child.View).Content);

        // 依赖登记在 Responsive 节点自身，而不是外层组件节点。
        Assert.Contains(node, host.A!.Dependents);
        Assert.DoesNotContain(app.Runtime.Root, host.A.Dependents);

        host.A.Value = "new A";
        app.Runtime.ProcessPendingWork();

        // 状态变化必须重建动态内容：不能因为节点不是 Component 就丢弃失效。
        Assert.Equal("new A", Assert.IsType<Text>(node.Children[0].View).Content);
        Assert.Same(child, node.Children[0]);
        Assert.Equal(builds, host.Builds);
        Assert.Equal(2, host.BuilderCalls);
        Assert.Equal(0, app.Runtime.PendingResponsiveRequestCount);
        Assert.Null(BuildTracker.Current);
    }

    [Fact]
    public void ConditionalRead_RemovesDependencyAfterBranchSwitch()
    {
        var host = new OwnerHost();
        using var app = new TestApp(host, new Size(300, 200));
        var node = app.Runtime.Root.Require<Responsive>();

        host.ChooseA!.Value = false;
        app.Runtime.ProcessPendingWork();

        Assert.Equal("B", Assert.IsType<Text>(node.Children[0].View).Content);
        Assert.DoesNotContain(node, host.A!.Dependents);
        Assert.Contains(node, host.B!.Dependents);

        // 依赖集合被整体替换为当前实际读取的状态：只剩 ChooseA + B，没有累积 A。
        Assert.Equal(2, node.ResponsiveContent!.ReadDependencyCount);

        // 条件依赖移除后，不再读取的状态变化不应触发任何构建。
        var calls = host.BuilderCalls;
        host.A.Value = "inactive";
        app.Runtime.ProcessPendingWork();

        Assert.Equal(calls, host.BuilderCalls);
        Assert.Equal("B", Assert.IsType<Text>(node.Children[0].View).Content);
    }

    [Fact]
    public void BurstStateAndWidthChanges_CoalesceIntoOneBuild()
    {
        var host = new OwnerHost();
        using var app = new TestApp(host, new Size(300, 200));
        var node = app.Runtime.Root.Require<Responsive>();

        var calls = host.BuilderCalls;
        host.A!.Value = "first";
        host.A.Value = "second";
        host.A.Value = "third";
        app.Runtime.SetViewport(new Size(250, 200), 1.0);
        app.Runtime.SetViewport(new Size(180, 200), 1.0);
        app.Runtime.ProcessPendingWork();

        // 短时间的多次状态与宽度变化在同一轮合并为一次最终有效提案。
        Assert.Equal(calls + 1, host.BuilderCalls);
        Assert.Equal("third", Assert.IsType<Text>(node.Children[0].View).Content);
        Assert.Equal(180, node.Frame.Width, 3);
        Assert.Equal(0, app.Runtime.PendingResponsiveRequestCount);
        Assert.Null(BuildTracker.Current);
    }

    // ------------------------------------------------------------ 异常路径与 tracker 恢复

    [Fact]
    public void FailingBuilder_KeepsCommittedDependenciesAndRestoresTracker()
    {
        var host = new ThrowingBuilderHost();
        using var app = new TestApp(host, new Size(300, 200));
        var node = app.Runtime.Root.Require<Responsive>();

        Assert.Contains(node, host.B!.Dependents);
        Assert.DoesNotContain(node, host.A!.Dependents);

        // 让 builder 在读取 A 之后抛出：这次部分读取不得提交为依赖。
        host.ThrowOnBuild = true;
        app.Runtime.InvalidateResponsiveContent(node);
        var exception = Assert.Throws<InvalidOperationException>(() => app.Runtime.ProcessPendingWork());
        Assert.Equal("builder failed on purpose", exception.Message);

        // 异常后 tracker 归位，旧内容依赖保留，失败构建读取的状态没有订阅。
        Assert.Null(BuildTracker.Current);
        Assert.DoesNotContain(node, host.A.Dependents);
        Assert.Contains(node, host.B.Dependents);
        Assert.Equal("B", Assert.IsType<Text>(node.Children[0].View).Content);

        // 下一次合法更新重试该描述（与 RL-05 失败描述恢复一致），此后依赖集合稳定。
        host.ThrowOnBuild = false;
        app.Runtime.ProcessPendingWork();
        var calls = host.BuilderCalls;

        // 失败构建读到的 A 没有被提交为依赖：变化不再触发任何构建。
        host.A.Value = "unused";
        app.Runtime.ProcessPendingWork();
        Assert.Equal(calls, host.BuilderCalls);

        // 已提交的 B 才是真正依赖：变化按新内容重建一次。
        host.B.Value = "after fix";
        app.Runtime.ProcessPendingWork();

        Assert.Equal(calls + 1, host.BuilderCalls);
        Assert.Equal("after fix", Assert.IsType<Text>(node.Children[0].View).Content);
        Assert.Null(BuildTracker.Current);
    }

    [Fact]
    public void BuilderWritingStateOrCreatingState_IsRejectedWithExplicitDiagnostics()
    {
        var host = new BuilderSideEffectHost();
        using var app = new TestApp(host, new Size(300, 200));

        // 写相同值同样违反“builder 只做纯描述构建”，不能被相等性短路掩盖。
        Assert.Contains("不能写状态", host.WriteError);
        Assert.Contains("Responsive builder", host.UseStateError);
        Assert.Equal(7, host.Value!.PeekValue());
        Assert.Equal("ok", Assert.IsType<Text>(app.Runtime.Root.Require<Responsive>().Children[0].View).Content);
        Assert.Null(BuildTracker.Current);
    }

    // ------------------------------------------------------------ 嵌套 Responsive 与组件

    [Fact]
    public void NestedResponsiveAndComponent_KeepIndependentDependencies()
    {
        var host = new NestedResponsiveHost();
        using var app = new TestApp(host, new Size(300, 300));

        var outerNode = app.Runtime.Root.Require<Responsive>();
        var innerComponentNode = app.Runtime.Root.Require<InnerResponsiveComponent>();
        var innerNode = innerComponentNode.Require<Responsive>();
        var detail = innerComponentNode.FindState<string>("inner-detail")!;
        var outerCalls = host.BuilderCalls;
        var innerBuilds = ((InnerResponsiveComponent)innerComponentNode.View).Builds;

        detail.Value = "detail-1";
        app.Runtime.ProcessPendingWork();

        // 内层 builder 的依赖只重建内层内容：外层 builder 与内层组件都不重建。
        Assert.Equal("detail-1", Assert.IsType<Text>(innerNode.Children[0].View).Content);
        Assert.Equal(outerCalls, host.BuilderCalls);
        Assert.Same(innerNode, innerComponentNode.Require<Responsive>());
        Assert.Equal(innerBuilds, ((InnerResponsiveComponent)innerComponentNode.View).Builds);

        // 组件读取的状态重建组件本身，外层 builder 仍不参与。
        var label = innerComponentNode.FindState<string>("inner-label")!;
        label.Value = "label-1";
        app.Runtime.ProcessPendingWork();

        Assert.Equal(outerCalls, host.BuilderCalls);
        Assert.True(((InnerResponsiveComponent)innerComponentNode.View).Builds > innerBuilds);
        Assert.Equal("label-1", Assert.IsType<Text>(innerComponentNode.Children[0].Children[0].View).Content);

        // 外层状态只重建外层内容；内层组件在同父级同类型下复用同一节点。
        host.Outer!.Value = "outer-1";
        app.Runtime.ProcessPendingWork();

        Assert.Equal(outerCalls + 1, host.BuilderCalls);
        Assert.Same(innerComponentNode, app.Runtime.Root.Require<InnerResponsiveComponent>());
        Assert.Equal("outer-1", Assert.IsType<Text>(outerNode.Children[0].Children[0].View).Content);
        Assert.Null(BuildTracker.Current);
    }

    // ------------------------------------------------------------ 卸载、过时请求与数据保留

    [Fact]
    public void UnmountedResponsive_ReleasesDependenciesAndIgnoresStaleRequests()
    {
        var host = new ToggleResponsiveHost();
        using var app = new TestApp(host, new Size(300, 200));

        var node = app.Runtime.Root.Require<Responsive>();
        Assert.Contains(node, host.Text!.Dependents);

        host.Visible!.Value = false;
        app.Runtime.ProcessPendingWork();

        Assert.Null(app.Runtime.Root.FindDescendant<Responsive>());
        Assert.DoesNotContain(node, host.Text.Dependents);

        var calls = host.BuilderCalls;
        host.Text.Value = "after unmount";
        app.Runtime.ProcessPendingWork();

        Assert.Equal(calls, host.BuilderCalls);
        Assert.Null(app.Runtime.Root.FindDescendant<Responsive>());

        // 外层状态数据跨分支保留：重新挂载后读到卸载期间写入的值。
        host.Visible.Value = true;
        app.Runtime.ProcessPendingWork();

        Assert.Equal("after unmount", Assert.IsType<Text>(app.Runtime.Root.Require<Responsive>().Children[0].View).Content);
        Assert.Null(BuildTracker.Current);
    }

    // ------------------------------------------------------------ 同父级 Key 复用与跨父级卸载

    [Fact]
    public void SameParentKeyedChild_IsReusedAcrossResponsiveBranchRebuild()
    {
        var host = new KeyedBranchHost();
        using var app = new TestApp(host, new Size(300, 240));

        var responsive = app.Runtime.Root.Require<Responsive>();
        var field = app.Runtime.Root.Require<TextField>();
        app.Runtime.SetFocus(field);
        app.Runtime.DispatchKey(new KeyEvent(KeyEventKind.Down, UiKey.End));
        var state = (TextFieldState)field.InteractionState!;

        app.Runtime.SetViewport(new Size(180, 240), 1.0);
        app.Runtime.ProcessPendingWork();

        // 同父级同 Key/类型：节点复用，焦点与本地编辑状态保持。
        Assert.Same(field, app.Runtime.Root.Require<TextField>());
        Assert.Same(field, app.Runtime.FocusedNode);
        Assert.Same(state, field.InteractionState);
        Assert.Equal("narrow", Assert.IsType<Text>(responsive.Children[0].Children[1].View).Content);
    }

    [Fact]
    public void CrossParentBranchSwitch_UnmountsBranchAndKeepsOuterBindingData()
    {
        var host = new CrossParentHost();
        using var app = new TestApp(host, new Size(300, 240));

        var field = app.Runtime.Root.Require<TextField>();
        app.Runtime.SetFocus(field);
        app.Runtime.DispatchKey(new KeyEvent(KeyEventKind.Down, UiKey.End));
        Assert.Same(field, app.Runtime.FocusedNode);

        app.Runtime.SetViewport(new Size(180, 240), 1.0);
        app.Runtime.ProcessPendingWork();

        // Key 只在同父级内匹配：父级类型变化是真正的卸载，不迁移焦点或本地编辑状态。
        Assert.False(field.IsMounted);
        Assert.Null(field.InteractionState);
        Assert.NotSame(field, app.Runtime.Root.Require<TextField>());
        Assert.Null(app.Runtime.FocusedNode);

        // 外层绑定数据跨分支保留：新输入框显示同一份文本。
        Assert.Equal("persisted", ((TextField)app.Runtime.Root.Require<TextField>().View).Text.Value);
    }

    // ------------------------------------------------- 布局/绘制阶段补登记的长期绑定依赖

    [Fact]
    public void ExtraBindingDependency_IsReleasedWhenReadingControlUnmounts()
    {
        var host = new CachedBindingHost();
        using var app = new TestApp(host, new Size(300, 200));
        var node = app.Runtime.Root.Require<Responsive>();

        // 绑定在任何 Build 之外缓存：构建期不读取来源状态，只有控件在测量/绘制时补登记长期依赖。
        host.CacheBinding();
        app.Runtime.InvalidateResponsiveContent(node);
        app.Runtime.ProcessPendingWork();
        app.Runtime.BuildFrame();

        var componentNode = app.Runtime.Root;
        Assert.Contains(componentNode, host.Source!.Dependents);
        Assert.Equal(1, componentNode.ComponentState!.ExtraDependencyCount);

        // 条件切换移除输入框：长期依赖随被移除控件释放，不残留到外层组件。
        host.Show!.Value = false;
        app.Runtime.ProcessPendingWork();

        Assert.DoesNotContain(componentNode, host.Source.Dependents);
        Assert.Equal(0, componentNode.ComponentState.ExtraDependencyCount);
        Assert.Equal("no field", Assert.IsType<Text>(node.Children[0].View).Content);

        // 之后修改来源状态不会再重建已经无关的组件或响应式内容。
        var builds = host.Builds;
        var calls = host.BuilderCalls;
        host.Source.Value = "after";
        app.Runtime.ProcessPendingWork();

        Assert.Equal(builds, host.Builds);
        Assert.Equal(calls, host.BuilderCalls);
    }

    [Fact]
    public void RemovingExtraBinding_DoesNotDropLiveBuildDependency()
    {
        var host = new OverlapHost();
        using var runtime = CreateBareRuntime(host, new Size(300, 400));
        host.CachedBinding = host.Source!.ToBinding();
        runtime.ProcessPendingWork();
        runtime.BuildFrame();

        var componentNode = runtime.Root;
        Assert.Contains(componentNode, host.Source.Dependents);

        // 隐藏输入框：布局期补登记的附加依赖被释放，但构建期读取仍然有效。
        host.Show!.Value = false;
        runtime.ProcessPendingWork();

        Assert.Single(host.Source.Dependents);

        var before = host.Builds;
        host.Source.Value = "new direct value";
        runtime.ProcessPendingWork();

        Assert.Equal(before + 1, host.Builds);
        Assert.Contains(
            runtime.Root.FindDescendants<Text>(),
            node => Assert.IsType<Text>(node.View).Content == "new direct value");
    }

    [Fact]
    public void RemovingBuildRead_KeepsActiveBindingDependency()
    {
        var host = new OverlapHost();
        using var runtime = CreateBareRuntime(host, new Size(300, 400));
        host.CachedBinding = host.Source!.ToBinding();
        runtime.ProcessPendingWork();
        runtime.BuildFrame();

        // 构建期不再读取来源状态，但活跃输入框仍通过绑定读取它：订阅必须保留。
        host.ReadDirect!.Value = false;
        runtime.ProcessPendingWork();
        runtime.BuildFrame();

        Assert.Single(host.Source.Dependents);

        var before = host.Builds;
        host.Source.Value = "bound value still active";
        runtime.ProcessPendingWork();

        Assert.Equal(before + 1, host.Builds);
    }

    [Fact]
    public void ReusedField_ReplacesOldBindingDependency()
    {
        var host = new RebindHost();
        using var runtime = CreateBareRuntime(host, new Size(300, 400));
        host.CachedBinding = host.A!.ToBinding();
        runtime.ProcessPendingWork();
        runtime.BuildFrame();

        var field = runtime.Root.FindDescendant<TextField>()!;

        // 复用同一输入框节点换用新的绑定：旧来源的订阅必须被替换。
        host.CachedBinding = host.B!.ToBinding();
        host.Tick!.Value = 1;
        runtime.ProcessPendingWork();
        runtime.BuildFrame();

        Assert.Same(field, runtime.Root.FindDescendant<TextField>());
        Assert.DoesNotContain(runtime.Root, host.A.Dependents);

        var before = host.Builds;
        host.A.Value = "old source changed";
        runtime.ProcessPendingWork();
        Assert.Equal(before, host.Builds);

        host.B.Value = "new source changed";
        runtime.ProcessPendingWork();
        Assert.Equal(before + 1, host.Builds);
    }

    // --------------------------------------------------------------------- 后台投递
    [Fact]
    public void BackgroundDispatcherPost_AppliesStateChangeOnUiThread()
    {
        var host = new OwnerHost();
        using var app = new TestApp(host, new Size(300, 200));
        var node = app.Runtime.Root.Require<Responsive>();

        var worker = new Thread(() => app.Dispatcher.Post(() => host.A!.Value = "from background"));
        worker.Start();
        worker.Join();

        // 尚未在 UI 线程执行：内容保持原值。
        Assert.Equal("A", Assert.IsType<Text>(node.Children[0].View).Content);

        app.Dispatcher.RunPending();
        app.Runtime.ProcessPendingWork();

        Assert.Equal("from background", Assert.IsType<Text>(node.Children[0].View).Content);
    }

    // ----------------------------------------------------------------------------- 测试辅助

    /// <summary>
    /// 构造运行时但不在构造期间处理待办：用于在首次布局之前设置构建之外缓存的绑定。
    /// </summary>
    private static UiRuntime CreateBareRuntime(View root, Size viewport)
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
        return runtime;
    }

    /// <summary>Build 不读取任何状态值；只有 builder 读取三个状态。</summary>
    private sealed class OwnerHost : Component
    {
        public State<string>? A { get; private set; }

        public State<string>? B { get; private set; }

        public State<bool>? ChooseA { get; private set; }

        public int Builds { get; private set; }

        public int BuilderCalls { get; private set; }

        protected override View Build()
        {
            Builds++;
            A = UseState("a", "A");
            B = UseState("b", "B");
            ChooseA = UseState("choose", true);
            return new Responsive(_ =>
            {
                BuilderCalls++;
                return new Text(ChooseA.Value ? A.Value : B.Value);
            });
        }
    }

    /// <summary>成功时只读 B，抛错前读取 A，用于验证异常路径的部分读取不会提交。</summary>
    private sealed class ThrowingBuilderHost : Component
    {
        public State<string>? A { get; private set; }

        public State<string>? B { get; private set; }

        public bool ThrowOnBuild { get; set; }

        public int BuilderCalls { get; private set; }

        protected override View Build()
        {
            A = UseState("a", "A");
            B = UseState("b", "B");
            return new Responsive(info =>
            {
                BuilderCalls++;
                if (ThrowOnBuild)
                {
                    _ = A.Value;
                    throw new InvalidOperationException("builder failed on purpose");
                }

                return new Text(B.Value);
            });
        }
    }

    /// <summary>builder 内尝试写状态与创建状态，捕获异常消息用于断言。</summary>
    private sealed class BuilderSideEffectHost : Component
    {
        public State<int>? Value { get; private set; }

        public string? WriteError { get; private set; }

        public string? UseStateError { get; private set; }

        protected override View Build()
        {
            Value = UseState("value", 7);
            return new Responsive(_ =>
            {
                try
                {
                    // 写相同值同样是副作用，不能被相等性短路掩盖。
                    Value.Value = 7;
                }
                catch (Exception ex)
                {
                    WriteError = ex.Message;
                }

                try
                {
                    UseState("inner", 1);
                }
                catch (Exception ex)
                {
                    UseStateError = ex.Message;
                }

                return new Text("ok");
            });
        }
    }

    private sealed class InnerResponsiveComponent : Component
    {
        public int Builds { get; private set; }

        protected override View Build()
        {
            Builds++;
            var label = UseState("inner-label", "inner-0");
            var detail = UseState("inner-detail", "detail-0");
            return new VStack(
                new Text(label.Value),
                new Responsive(_ => new Text(detail.Value)));
        }
    }

    private sealed class NestedResponsiveHost : Component
    {
        public State<string>? Outer { get; private set; }

        public int BuilderCalls { get; private set; }

        protected override View Build()
        {
            Outer = UseState("outer", "outer-0");
            return new Responsive(_ =>
            {
                BuilderCalls++;
                return new VStack(new Text(Outer.Value), new InnerResponsiveComponent());
            });
        }
    }

    /// <summary>可整体移除 Responsive 的组件；Visible 由组件读取，Text 只由 builder 读取。</summary>
    private sealed class ToggleResponsiveHost : Component
    {
        public State<bool>? Visible { get; private set; }

        public State<string>? Text { get; private set; }

        public int BuilderCalls { get; private set; }

        protected override View Build()
        {
            Visible = UseState("visible", true);
            Text = UseState("text", "hello");
            return Visible.Value
                ? new Responsive(_ =>
                {
                    BuilderCalls++;
                    return new Text(Text.Value);
                })
                : new Text("hidden");
        }
    }

    private sealed class KeyedBranchHost : Component
    {
        private string _text = "editable";

        protected override View Build()
            => new Responsive(info => new VStack(
                new TextField(new Binding<string>(() => _text, value => _text = value)).Key("field"),
                new Text(info.IsWidthBounded && info.AvailableWidth < 200 ? "narrow" : "wide")));
    }

    private sealed class CrossParentHost : Component
    {
        private string _text = "persisted";

        protected override View Build()
            => new Responsive(info => info.IsWidthBounded && info.AvailableWidth < 200
                ? new HStack(new TextField(new Binding<string>(() => _text, value => _text = value)).Key("field"))
                : new VStack(new TextField(new Binding<string>(() => _text, value => _text = value)).Key("field")));
    }

    /// <summary>绑定在任何 Build 之外缓存，只有活跃 TextField 在布局/绘制时读取它。</summary>
    private sealed class CachedBindingHost : Component
    {
        private Binding<string>? _binding;

        public State<bool>? Show { get; private set; }

        public State<string>? Source { get; private set; }

        public int Builds { get; private set; }

        public int BuilderCalls { get; private set; }

        public void CacheBinding() => _binding = Source!.ToBinding();

        protected override View Build()
        {
            Builds++;
            Show = UseState("show", true);
            Source = UseState("source", "before");
            return new Responsive(_ =>
            {
                BuilderCalls++;
                if (_binding is null)
                {
                    return new Text("binding pending");
                }

                return Show.Value ? new TextField(_binding) : (View)new Text("no field");
            });
        }
    }

    /// <summary>同一状态既有构建期读取、又有布局期绑定读取，验证两种来源分别结算。</summary>
    private sealed class OverlapHost : Component
    {
        public State<string>? Source { get; private set; }

        public State<bool>? Show { get; private set; }

        public State<bool>? ReadDirect { get; private set; }

        public Binding<string>? CachedBinding { get; set; }

        public int Builds { get; private set; }

        protected override View Build()
        {
            Builds++;
            Source = UseState("source", "direct value");
            Show = UseState("show", true);
            ReadDirect = UseState("readDirect", true);
            return new VStack(
                new Text(ReadDirect.Value ? Source.Value : "no direct read"),
                new Responsive(_ => Show.Value
                    ? new TextField(CachedBinding!)
                    : (View)new Text("hidden")));
        }
    }

    /// <summary>复用同一输入框节点、在运行时换用新绑定的宿主。</summary>
    private sealed class RebindHost : Component
    {
        public State<string>? A { get; private set; }

        public State<string>? B { get; private set; }

        public State<int>? Tick { get; private set; }

        public Binding<string>? CachedBinding { get; set; }

        public int Builds { get; private set; }

        protected override View Build()
        {
            Builds++;
            A = UseState("a", "source A");
            B = UseState("b", "source B");
            Tick = UseState("tick", 0);
            _ = Tick.Value;
            return new Responsive(_ => new TextField(CachedBinding!));
        }
    }
}
