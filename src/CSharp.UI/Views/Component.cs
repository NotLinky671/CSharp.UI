namespace CSharp.UI;

/// <summary>
/// 可组合组件：通过 <see cref="Build"/> 返回视图描述树。
/// </summary>
/// <remarks>
/// <para>
/// 组件是普通 C# 类，状态由运行时组件节点按名称持有，因此重新构建描述不会丢失状态。
/// </para>
/// <para>
/// 构建期间读取 <see cref="State{T}.Value"/> 会自动登记依赖；状态变化后框架只重建读取该状态的组件。
/// </para>
/// </remarks>
public abstract class Component : View
{
    private ViewNode? _buildNode;

    /// <summary>组件实例的调试名称（默认为类型名）。</summary>
    public override string DebugName => GetType().Name;

    /// <summary>构建视图描述树。可以在其中使用条件表达式、<c>ForEach</c> 与 <see cref="UseState{T}"/>。</summary>
    protected abstract View Build();

    /// <summary>
    /// 声明一个按名称定位的状态。同一组件节点的同名同类型状态在重建时复用，类型不一致时抛出诊断。
    /// </summary>
    /// <typeparam name="T">状态值类型。</typeparam>
    /// <param name="name">组件内唯一的名称。</param>
    /// <param name="initialValue">新节点首次使用时的初始值。</param>
    protected State<T> UseState<T>(string name, T initialValue)
    {
        var node = _buildNode ?? throw new InvalidOperationException(
            $"组件 {DebugName} 的 UseState 只能在 Build() 执行期间调用。");
        return node.ComponentState!.GetOrCreate(name, initialValue, node);
    }

    /// <summary>声明一个按名称定位的状态，并直接返回其绑定。</summary>
    protected Binding<T> UseBinding<T>(string name, T initialValue) => UseState(name, initialValue).ToBinding();

    /// <summary>组件节点的构建入口；由协调器调用。</summary>
    internal View BuildFrom(ViewNode node)
    {
        _buildNode = node;
        try
        {
            return Build() ?? throw new InvalidOperationException($"组件 {DebugName} 的 Build() 返回 null。");
        }
        finally
        {
            _buildNode = null;
        }
    }

    internal sealed override void Measure(ViewNode node, LayoutConstraints constraints, LayoutContext context)
    {
        if (node.Children.Count == 0)
        {
            node.DesiredSize = constraints.Constrain(Size.Zero);
            return;
        }

        var child = node.Children[0];
        context.Measure(child, constraints);
        node.DesiredSize = child.DesiredSize;
    }

    internal sealed override void Arrange(ViewNode node, Rect bounds, LayoutContext context)
    {
        node.Frame = bounds;
        if (node.Children.Count > 0)
        {
            context.Arrange(node.Children[0], bounds);
        }
    }

    internal sealed override void Emit(ViewNode node, DrawContext context)
    {
        foreach (var child in node.Children)
        {
            context.EmitChild(child);
        }
    }

    // 组件不声明尺寸：特征透传 Build() 结果的根节点，使组件内的固定 Frame 仍能被上层识别。
    internal override LayoutFeatures GetLayoutFeatures(ViewNode node, LayoutAxis mainAxis)
        => node.Children.Count > 0
            ? node.Children[0].GetLayoutFeatures(mainAxis)
            : LayoutFeatures.FromNatural(node.DesiredSize);

    /// <inheritdoc />
    internal sealed override bool IsLayoutEquivalent(View other) => false;
}

/// <summary>
/// 组件节点持有的状态存储：状态字典、构建依赖集合与构建期读取记录。
/// </summary>
internal sealed class ComponentState
{
    private readonly Dictionary<string, StateBase> _states = [];
    private HashSet<StateBase> _dependencies = [];
    private readonly HashSet<StateBase> _extraDependencies = [];
    private readonly List<StateBase> _reads = [];
    private readonly ViewNode _owner;

    public ComponentState(ViewNode owner) => _owner = owner;

    /// <summary>当前是否正在构建。</summary>
    public bool IsBuilding { get; private set; }

    /// <summary>已声明的状态数量。</summary>
    public int StateCount => _states.Count;

    /// <summary>当前登记的状态依赖数量。</summary>
    public int DependencyCount => _dependencies.Count;

    /// <summary>取回或创建命名状态。</summary>
    public State<T> GetOrCreate<T>(string name, T initialValue, ViewNode owner)
    {
        if (_states.TryGetValue(name, out var existing))
        {
            if (existing is State<T> typed)
            {
                return typed;
            }

            throw new InvalidOperationException(
                $"节点 {_owner.DebugPath} 上名为 \"{name}\" 的状态已以类型 {existing.GetType().Name} 声明，" +
                $"不能再以 State<{typeof(T).Name}> 复用；同一名称的状态类型必须保持一致。");
        }

        var state = new State<T>(name, initialValue, owner);
        _states[name] = state;
        return state;
    }

    /// <summary>查找指定名称的状态。</summary>
    public State<T>? FindState<T>(string name)
        => _states.TryGetValue(name, out var state) ? state as State<T> : null;

    /// <summary>开始一次构建，清空读取记录。</summary>
    public void BeginBuild()
    {
        IsBuilding = true;
        _reads.Clear();
        BuildTracker.Current = this;
    }

    /// <summary>结束构建：把本次读取集合替换为新的依赖集合，移除不再读取的订阅。</summary>
    public void EndBuild()
    {
        BuildTracker.Current = null;
        IsBuilding = false;

        var next = new HashSet<StateBase>(_reads);
        foreach (var state in _dependencies)
        {
            if (!next.Contains(state))
            {
                state.RemoveDependent(_owner);
            }
        }

        foreach (var state in next)
        {
            state.AddDependent(_owner);
        }

        _dependencies = next;
    }

    /// <summary>记录一次状态读取。</summary>
    public void RecordRead(StateBase state)
    {
        if (IsBuilding && !_reads.Contains(state))
        {
            _reads.Add(state);
        }
    }

    /// <summary>
    /// 补登记一个长期依赖：控件在测量/绘制时通过绑定读取到来源状态时使用。
    /// 这类依赖与构建期读取集合分开保存，不会在重新构建时被清除。
    /// </summary>
    public void AddExtraDependency(StateBase state)
    {
        if (_extraDependencies.Add(state))
        {
            state.AddDependent(_owner);
        }
    }

    /// <summary>节点卸载：释放全部依赖订阅与状态。</summary>
    public void Release()
    {
        foreach (var state in _dependencies)
        {
            state.RemoveDependent(_owner);
        }

        foreach (var state in _extraDependencies)
        {
            state.RemoveDependent(_owner);
        }

        foreach (var state in _states.Values)
        {
            state.ClearDependents();
        }

        _dependencies.Clear();
        _extraDependencies.Clear();
        _reads.Clear();
        _states.Clear();
        if (ReferenceEquals(BuildTracker.Current, this))
        {
            BuildTracker.Current = null;
        }
    }
}

/// <summary>构建期状态读取跟踪；只在 UI 线程使用。</summary>
internal static class BuildTracker
{
    [ThreadStatic]
    private static ComponentState? _current;

    /// <summary>当前正在构建的组件状态存储。</summary>
    public static ComponentState? Current
    {
        get => _current;
        set => _current = value;
    }

    /// <summary>记录一次状态读取（构建期之外忽略）。</summary>
    public static void RecordRead(StateBase state) => _current?.RecordRead(state);
}
