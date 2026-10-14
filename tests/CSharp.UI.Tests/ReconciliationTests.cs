using CSharp.UI;
using Xunit;

namespace CSharp.UI.Tests;

/// <summary>任务 03：视图描述、组件与节点协调更新。</summary>
public class ReconciliationTests
{
    [Fact]
    public void Build_ProducesStableNodeInstancesForSameIdentity()
    {
        var component = new CountingComponent();
        using var app = new TestApp(component);

        var first = app.Runtime.Root.Require<CountingComponent>();
        var firstChildren = first.Children.ToArray();

        component.Count!.Value = 1;
        app.Runtime.ProcessPendingWork();

        var second = app.Runtime.Root.Require<CountingComponent>();
        Assert.Same(first, second);
        Assert.Equal(2, component.BuildCount);

        // 同类型同位置的子节点被复用。
        Assert.Equal(firstChildren.Length, second.Children.Count);
        for (var i = 0; i < firstChildren.Length; i++)
        {
            Assert.Same(firstChildren[i], second.Children[i]);
        }
    }

    [Fact]
    public void KeyedList_KeepsStateWithOriginalItem_OnReorderAndInsert()
    {
        var component = new KeyedListComponent();
        using var app = new TestApp(component);

        var itemA = app.Runtime.All<LifecycleComponent>().First(n => ((LifecycleComponent)n.View).Id == "a");
        itemA.GetStateValue<int>("local");
        var stateA = itemA.FindState<int>("local")!;
        stateA.Value = 42;
        app.Runtime.ProcessPendingWork();

        // 重排：c, a, b —— a 的节点与状态必须跟随原项目。
        component.Items.Clear();
        component.Items.AddRange(["c", "a", "b"]);
        component.Trigger!.Value++;
        app.Runtime.ProcessPendingWork();
        app.Frame();

        var afterReorder = app.Runtime.All<LifecycleComponent>().First(n => ((LifecycleComponent)n.View).Id == "a");
        Assert.Same(itemA, afterReorder);
        Assert.Equal(42, afterReorder.GetStateValue<int>("local"));

        // 头部插入新项：已有项的身份不变。
        component.Items.Insert(0, "z");
        component.Trigger!.Value++;
        app.Runtime.ProcessPendingWork();
        app.Frame();

        var ids = ItemIds(app);
        Assert.Equal(["z", "c", "a", "b"], ids);
        Assert.Same(itemA, app.Runtime.All<LifecycleComponent>().First(n => ((LifecycleComponent)n.View).Id == "a"));
        Assert.Equal(42, itemA.GetStateValue<int>("local"));

        // 删除中间项：a 仍在，且其状态保留。
        component.Items.Remove("c");
        component.Trigger!.Value++;
        app.Runtime.ProcessPendingWork();
        app.Frame();

        Assert.Equal(["z", "a", "b"], ItemIds(app));
        Assert.Same(itemA, app.Runtime.All<LifecycleComponent>().First(n => ((LifecycleComponent)n.View).Id == "a"));
        Assert.Equal(42, itemA.GetStateValue<int>("local"));
        Assert.Contains("unmount:c", component.Log);
    }

    [Fact]
    public void SameKeyDifferentType_ReplacesNode()
    {
        var component = new SameKeyComponent();
        using var app = new TestApp(component);

        var textNode = app.Runtime.Root.Require<Text>();
        Assert.Equal("text", ((Text)textNode.View).Content);

        component.AsButton = true;
        component.Trigger!.Value++;
        app.Runtime.ProcessPendingWork();
        app.Frame();

        Assert.Null(app.Runtime.Root.FindDescendant<Text>());
        var buttonNode = app.Runtime.Root.Require<Button>();
        Assert.NotSame(textNode, buttonNode);
    }

    [Fact]
    public void ConditionalBranch_TriggersUnmountAndResourceDisposal()
    {
        var component = new KeyedListComponent();
        using var app = new TestApp(component);

        var conditional = app.Runtime.All<LifecycleComponent>().FirstOrDefault(n => ((LifecycleComponent)n.View).Id == "cond");
        Assert.NotNull(conditional);
        Assert.Contains("mount:cond", component.Log);

        component.Log.Clear();
        component.ShowConditional = false;
        component.Trigger!.Value++;
        app.Runtime.ProcessPendingWork();
        app.Frame();

        Assert.DoesNotContain("cond", app.Runtime.All<LifecycleComponent>().Select(n => ((LifecycleComponent)n.View).Id));
        Assert.Contains("unmount:cond", component.Log);
        Assert.Equal(1, component.Log.Count(l => l == "unmount:cond"));
    }

    [Fact]
    public void Unmount_DisposesResourcesExactlyOnce()
    {
        var log = new List<string>();
        var app = new TestApp(new VStack(new ResourceComponent("r", log)));
        Assert.Contains("create:res:r", log);

        // 卸载整棵树：资源必须在卸载时释放一次。
        app.Runtime.Dispose();
        Assert.Contains("unmount:r", log);
        Assert.Contains("dispose:res:r", log);
        Assert.Equal(1, log.Count(l => l == "dispose:res:r"));

        // 重复释放是幂等的。
        app.Runtime.Dispose();
        Assert.Equal(1, log.Count(l => l == "dispose:res:r"));
    }

    [Fact]
    public void EventHandler_IsReplacedAndOldHandlerNotInvoked()
    {
        var component = new CallbackComponent();
        using var app = new TestApp(component);

        var invoked = new List<string>();
        component.Current = () => invoked.Add("old");
        component.Trigger!.Value++;
        app.Runtime.ProcessPendingWork();
        app.Frame();

        // 替换回调后重新构建。
        component.Current = () => invoked.Add("new");
        component.Trigger!.Value++;
        app.Runtime.ProcessPendingWork();
        app.Frame();

        var button = app.Runtime.Root.Require<Button>();
        app.Click(button.Frame.Center);

        Assert.Equal(["new"], invoked);
    }

    [Fact]
    public void DuplicateKeys_ThrowDiagnosticWithLocation()
    {
        var log = new List<string>();
        var view = new VStack(
            new LifecycleComponent("dup", log).Key("same"),
            new LifecycleComponent("dup", log).Key("same"));

        var exception = Assert.Throws<DuplicateKeyException>(() => new TestApp(view));
        Assert.Contains("same", exception.Message);
        Assert.Contains("重复键", exception.Message);
    }

    [Fact]
    public void ForEach_WithoutKeys_MatchesByPosition()
    {
        var component = new KeyedListComponent { UseKeys = false };
        using var app = new TestApp(component);

        var first = app.Runtime.All<LifecycleComponent>().ToArray();
        component.Trigger!.Value++;
        app.Runtime.ProcessPendingWork();
        app.Frame();

        var second = app.Runtime.All<LifecycleComponent>().ToArray();
        Assert.Equal(first.Length, second.Length);
        for (var i = 0; i < first.Length; i++)
        {
            Assert.Same(first[i], second[i]);
        }
    }

    private static string[] ItemIds(TestApp app)
        => [.. app.Runtime.All<LifecycleComponent>()
            .Select(n => ((LifecycleComponent)n.View).Id)
            .Where(id => id != "cond")];

    [Fact]
    public void ViewDescriptions_AreNotMutatedByReconciliation()
    {
        var shared = new Text("shared");
        var component = new CountingComponent();
        var view = new VStack(shared, component);
        using var app = new TestApp(view);

        component.Count!.Value++;
        app.Runtime.ProcessPendingWork();
        app.Frame();

        // 描述对象可重复使用且未被协调器修改。
        Assert.Equal("shared", shared.Content);
        Assert.Same(shared, ((VStack)app.Runtime.Root.View).Items[0]);
    }
}

/// <summary>同一键但类型变化的组件。</summary>
internal sealed class SameKeyComponent : Component
{
    public bool AsButton { get; set; }

    public State<int>? Trigger { get; private set; }

    protected override View Build()
    {
        Trigger = UseState("trigger", 0);
        _ = Trigger.Value;
        return AsButton ? new Button("b").Key("k") : new Text("text").Key("k");
    }
}
