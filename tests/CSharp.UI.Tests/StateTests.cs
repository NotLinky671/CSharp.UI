using CSharp.UI;
using Xunit;

namespace CSharp.UI.Tests;

/// <summary>任务 04：状态、双向绑定与更新调度。</summary>
public class StateTests
{
    [Fact]
    public void NamedState_SurvivesRebuild_AndNewIdentityUsesInitialValue()
    {
        var component = new CountingComponent();
        using var app = new TestApp(component);
        var node = app.Runtime.Root.Require<CountingComponent>();

        component.Count!.Value = 7;
        app.Runtime.ProcessPendingWork();
        app.Frame();

        Assert.Equal(7, node.GetStateValue<int>("count"));
        Assert.Same(component.Count, node.FindState<int>("count"));

        // 用一个全新节点验证初始值（同一类型的新组件挂载到不同位置）。
        var first = new CountingComponent();
        var second2 = new CountingComponent();
        using var app2 = new TestApp(new VStack(new KeyedHost("first", first), new KeyedHost("second", second2)));
        var secondNode = app2.Runtime.All<CountingComponent>().First(n => ReferenceEquals(n.View, second2));
        Assert.Equal(0, secondNode.GetStateValue<int>("count"));
    }

    [Fact]
    public void SameValueAssignment_DoesNotTriggerRebuild()
    {
        var component = new CountingComponent();
        using var app = new TestApp(component);
        var before = component.BuildCount;

        component.Count!.Value = 0;
        app.Runtime.ProcessPendingWork();

        Assert.Equal(before, component.BuildCount);
    }

    [Fact]
    public void MultipleChangesInOneRound_AreCoalescedIntoOneRebuild()
    {
        var component = new CountingComponent();
        using var app = new TestApp(component);
        var before = component.BuildCount;

        component.Count!.Value = 1;
        component.Count.Value = 2;
        component.Count.Value = 3;
        component.Other!.Value = 5;

        // 变化只入队，直到调度阶段才执行构建。
        Assert.Equal(before, component.BuildCount);
        app.Runtime.ProcessPendingWork();

        Assert.Equal(before + 1, component.BuildCount);
        Assert.Equal(3, component.Count.Value);
    }

    [Fact]
    public void DependencyRemovedWhenNoLongerRead_StopsTriggeringComponent()
    {
        var component = new CountingComponent();
        using var app = new TestApp(component);

        component.ReadCount = false;
        component.Other!.Value = 1;
        app.Runtime.ProcessPendingWork();
        var builds = component.BuildCount;

        // 组件不再读取 Count，因此 Count 变化不应触发重建。
        component.Count!.Value = 99;
        app.Runtime.ProcessPendingWork();
        Assert.Equal(builds, component.BuildCount);

        // 仍然读取 Other，Other 变化会触发重建。
        component.Other.Value = 2;
        app.Runtime.ProcessPendingWork();
        Assert.Equal(builds + 1, component.BuildCount);
        Assert.Equal(99, component.Count.Value);
    }

    [Fact]
    public void UnmountedComponent_ReleasesSubscriptions()
    {
        var log = new List<string>();
        var component = new KeyedListComponent();
        using var app = new TestApp(component);

        var itemNode = app.Runtime.All<LifecycleComponent>().First(n => ((LifecycleComponent)n.View).Id == "a");
        var itemState = itemNode.FindState<int>("local")!;

        component.Items.Remove("a");
        component.Trigger!.Value++;
        app.Runtime.ProcessPendingWork();
        app.Frame();

        // 卸载后不再保留依赖关系：修改旧状态不应影响运行时。
        itemState.Value = 5;
        app.Runtime.ProcessPendingWork();
        Assert.False(itemNode.IsMounted);
        Assert.Empty(itemState.Dependents);
    }

    [Fact]
    public void Binding_RoundTripsThroughState()
    {
        var component = new CountingComponent();
        using var app = new TestApp(component);

        Binding<int> binding = component.Count!;
        Assert.Equal(0, binding.Value);

        binding.Value = 11;
        app.Runtime.ProcessPendingWork();
        Assert.Equal(11, component.Count!.Value);
        Assert.Equal(11, binding.Value);
    }

    [Fact]
    public void BackgroundThread_StateWriteIsDiagnosed_AndPostRunsOnUiThread()
    {
        var component = new CountingComponent();
        using var app = new TestApp(component);

        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try
            {
                component.Count!.Value = 1;
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });
        thread.Start();
        thread.Join();

        Assert.IsType<UiThreadViolationException>(captured);
        Assert.Contains("UiDispatcher.Post", captured!.Message);

        // 通过调度器投递的动作在 UI 线程队列中执行。
        UiDispatcher.Post(() => component.Count!.Value = 2);
        Assert.Equal(1, app.Dispatcher.PendingCount);
        app.Dispatcher.RunPending();
        app.Runtime.ProcessPendingWork();

        Assert.Equal(2, component.Count!.Value);
        Assert.True(app.Dispatcher.IsOnUiThread);
    }

    [Fact]
    public void UseState_WithDifferentTypeForSameName_ThrowsDiagnostic()
    {
        var component = new TypeMismatchComponent();
        var exception = Assert.Throws<InvalidOperationException>(() => new TestApp(component));
        Assert.Contains("状态", exception.Message);
    }

    [Fact]
    public void BuildModifyingState_IsDetectedAsUpdateLoop()
    {
        var component = new LoopingComponent();
        using var app = new TestApp(component);

        // 首次挂载时依赖尚未提交，因此从第二轮起才会出现“构建期间修改状态”的循环。
        component.External!.Value = 1;
        var exception = Assert.Throws<InvalidOperationException>(() => app.Runtime.ProcessPendingWork());
        Assert.Contains("更新循环", exception.Message);
    }

    [Fact]
    public void ToggleAndSliderBindings_RegisterDependencyForControls()
    {
        var component = new ControlBindingComponent();
        using var app = new TestApp(component);

        var builds = component.BuildCount;

        // 通过控件交互修改状态（不是构建期读取），依赖由绑定补登记。
        var toggle = app.Runtime.Root.Require<Toggle>();
        app.Click(toggle.Frame.Center);

        Assert.True(component.Flag!.Value);
        app.Runtime.ProcessPendingWork();
        Assert.True(component.BuildCount > builds);
    }
}

/// <summary>在键下挂载组件的宿主。</summary>
internal sealed class KeyedHost : Component
{
    private readonly string _key;
    private readonly Component _child;

    public KeyedHost(string key, Component child)
    {
        _key = key;
        _child = child;
    }

    protected override View Build() => _child.Key(_key);
}

/// <summary>同名状态类型不一致的组件。</summary>
internal sealed class TypeMismatchComponent : Component
{
    public State<string>? Text { get; private set; }

    protected override View Build()
    {
        if (Text is null)
        {
            UseState("value", 1);
            Text = UseState("value", "one");
        }
        else
        {
            UseState("value", 1);
        }

        return new Text(Text.Value);
    }
}

/// <summary>在 Build 中修改状态的组件（用于验证更新循环诊断）。</summary>
internal sealed class LoopingComponent : Component
{
    public State<int>? External { get; private set; }

    protected override View Build()
    {
        External = UseState("external", 0);
        var counter = UseState("counter", 0);
        if (External.Value > 0)
        {
            counter.Value++;
        }

        return new Text(counter.Value.ToString());
    }
}

/// <summary>使用控件绑定的组件。</summary>
internal sealed class ControlBindingComponent : Component
{
    public int BuildCount;

    public State<bool>? Flag { get; private set; }

    protected override View Build()
    {
        BuildCount++;
        Flag = UseState("flag", false);
        var binding = Flag.ToBinding();
        return new VStack(new Toggle(binding), new Text($"flag:{binding.Value}"));
    }
}
