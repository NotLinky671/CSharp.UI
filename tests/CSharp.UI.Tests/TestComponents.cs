using CSharp.UI;

namespace CSharp.UI.Tests;

/// <summary>记录挂载/卸载与自身状态的组件，用于验证节点身份与生命周期。</summary>
internal sealed class LifecycleComponent : Component
{
    private readonly List<string> _log;

    public LifecycleComponent(string id, List<string> log, bool withState = true)
    {
        Id = id;
        _log = log;
        WithState = withState;
    }

    public string Id { get; }

    public bool WithState { get; }

    public State<int>? Local { get; private set; }

    protected override View Build()
    {
        _log.Add($"build:{Id}");
        if (WithState)
        {
            Local = UseState("local", Id.GetHashCode() & 0xFF);
        }

        return new Text($"{Id}={Local?.Value}");
    }

    internal override void OnMount(ViewNode node) => _log.Add($"mount:{Id}");

    internal override void OnUnmount(ViewNode node) => _log.Add($"unmount:{Id}");
}

/// <summary>资源持有组件：验证卸载时释放资源。</summary>
internal sealed class ResourceComponent : Component
{
    private readonly List<string> _log;

    public ResourceComponent(string id, List<string> log)
    {
        Id = id;
        _log = log;
    }

    public string Id { get; }

    protected override View Build() => new Text(Id);

    internal override void OnMount(ViewNode node) => node.AddResource(new LoggingDisposable($"res:{Id}", _log));

    internal override void OnUnmount(ViewNode node) => _log.Add($"unmount:{Id}");
}

/// <summary>可释放对象，记录释放顺序。</summary>
internal sealed class LoggingDisposable : IDisposable
{
    private readonly List<string> _log;

    public LoggingDisposable(string name, List<string> log)
    {
        Name = name;
        _log = log;
        _log.Add($"create:{name}");
    }

    public string Name { get; }

    public bool Disposed { get; private set; }

    public void Dispose()
    {
        Disposed = true;
        _log.Add($"dispose:{Name}");
    }
}

/// <summary>可由测试驱动重建的列表组件（键来自项目本身）。</summary>
internal sealed class KeyedListComponent : Component
{
    public List<string> Items { get; } = ["a", "b", "c"];

    public List<string> Log { get; } = [];

    public bool UseKeys { get; set; } = true;

    public bool ShowConditional { get; set; } = true;

    public State<int>? Trigger { get; private set; }

    protected override View Build()
    {
        Trigger = UseState("trigger", 0);
        _ = Trigger.Value;

        var children = new List<View>();
        if (ShowConditional)
        {
            children.Add(new LifecycleComponent("cond", Log));
        }

        Func<string, int, object>? keySelector = UseKeys ? (item, _) => item : null;
        children.Add(new ForEach<string>(Items, (item, _) => BuildItem(item), keySelector));

        return new VStack([.. children]);
    }

    private View BuildItem(string item)
    {
        var component = new LifecycleComponent(item, Log);
        return UseKeys ? component.Key(item) : component;
    }
}

/// <summary>回调可替换的按钮组件。</summary>
internal sealed class CallbackComponent : Component
{
    public Action? Current { get; set; }

    public State<int>? Trigger { get; private set; }

    protected override View Build()
    {
        Trigger = UseState("trigger", 0);
        _ = Trigger.Value;

        // 捕获构建时的回调：若协调更新未替换事件处理器，旧回调仍会被触发。
        var handler = Current;
        return new Button("go", () => handler?.Invoke());
    }
}

/// <summary>计数器组件，用于验证状态、依赖与合并更新。</summary>
internal sealed class CountingComponent : Component
{
    public static int GlobalBuildCount;

    public int BuildCount;

    public bool ReadCount { get; set; } = true;

    public State<int>? Count { get; private set; }

    public State<int>? Other { get; private set; }

    protected override View Build()
    {
        BuildCount++;
        GlobalBuildCount++;
        Count = UseState("count", 0);
        Other = UseState("other", 0);

        var value = ReadCount ? Count.Value : 0;
        return new VStack(new Text($"count:{value}"), new Text($"other:{Other.Value}"));
    }
}

/// <summary>带单行文本框的组件。</summary>
internal sealed class FieldHost : Component
{
    private readonly string _initial;

    public FieldHost(string initial = "") => _initial = initial;

    public State<string>? Text { get; private set; }

    public State<int>? ExternalTick { get; private set; }

    protected override View Build()
    {
        Text = UseState("text", _initial);
        ExternalTick = UseState("tick", 0);
        _ = ExternalTick.Value;
        return new VStack(new TextField(Text.ToBinding()) { Width = 200 });
    }
}