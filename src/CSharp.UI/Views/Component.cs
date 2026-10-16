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
    /// <exception cref="InvalidOperationException">
    /// 在 <see cref="Build"/> 之外调用，或在 <see cref="Responsive"/> builder 内调用（状态所有权属于组件）时抛出。
    /// </exception>
    protected State<T> UseState<T>(string name, T initialValue)
    {
        if (BuildTracker.IsDynamicContentBuild)
        {
            throw new InvalidOperationException(
                "Responsive builder 内不能创建 State：UseState 的所有权只属于外层 Component.Build。" +
                "请在外层组件的 Build() 中声明状态，再作为纯数据传给 builder。");
        }

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

    // 状态依赖失效对组件意味着重建 Build（动态内容见 Responsive 的对应实现）。
    internal sealed override void OnStateDependencyChanged(ViewNode node)
        => node.Runtime.InvalidateComponent(node);

    /// <inheritdoc />
    internal sealed override bool IsLayoutEquivalent(View other) => false;
}

/// <summary>
/// 组件节点持有的状态存储：状态字典、构建依赖集合与构建期读取记录。
/// </summary>
internal sealed class ComponentState : IStateReadScope
{
    private readonly Dictionary<string, StateBase> _states = [];
    private HashSet<StateBase> _dependencies = [];
    private readonly HashSet<StateBase> _extraDependencies = [];
    private readonly Dictionary<StateBase, int> _extraDependencyRefs = [];
    private readonly List<StateBase> _reads = [];
    private readonly ViewNode _owner;
    private BuildTracker.Scope _buildScope;

    public ComponentState(ViewNode owner) => _owner = owner;

    /// <summary>当前是否正在构建。</summary>
    public bool IsBuilding { get; private set; }

    /// <summary>已声明的状态数量。</summary>
    public int StateCount => _states.Count;

    /// <summary>当前登记的状态依赖数量。</summary>
    public int DependencyCount => _dependencies.Count;

    /// <summary>当前登记的长期附加依赖数量（布局/绘制阶段通过绑定读取来源状态）。</summary>
    public int ExtraDependencyCount => _extraDependencies.Count;

    /// <inheritdoc />
    bool IStateReadScope.IsDynamicContentBuild => false;

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

    /// <summary>开始一次构建，清空读取记录并栈式推入读取作用域。</summary>
    public void BeginBuild()
    {
        IsBuilding = true;
        _reads.Clear();
        _buildScope = BuildTracker.Push(this);
    }

    /// <summary>结束构建：弹出读取作用域并把本次读取集合替换为新的依赖集合，移除不再读取的订阅。</summary>
    /// <remarks>依赖按节点引用计数：只对新增/移除的状态调用登记与释放，避免重复构建累积引用。</remarks>
    public void EndBuild()
    {
        _buildScope.Dispose();
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
            if (!_dependencies.Contains(state))
            {
                state.AddDependent(_owner);
            }
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
    /// 这类依赖与构建期读取集合分开保存，不会在重新构建时被清除，但会随读取它的节点卸载而释放。
    /// </summary>
    public void AddExtraDependency(StateBase state)
    {
        var count = _extraDependencyRefs.TryGetValue(state, out var existing) ? existing : 0;
        _extraDependencyRefs[state] = count + 1;
        if (count == 0)
        {
            _extraDependencies.Add(state);
            state.AddDependent(_owner);
        }
    }

    /// <summary>
    /// 释放一个长期依赖引用（读取它的节点卸载时调用）。
    /// 同一状态可能被多个节点读取，只有最后一个引用释放后才会真正移除订阅。
    /// </summary>
    public void RemoveExtraDependency(StateBase state)
    {
        if (!_extraDependencyRefs.TryGetValue(state, out var count))
        {
            return;
        }

        if (count <= 1)
        {
            _extraDependencyRefs.Remove(state);
            _extraDependencies.Remove(state);
            state.RemoveDependent(_owner);
            return;
        }

        _extraDependencyRefs[state] = count - 1;
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
        _extraDependencyRefs.Clear();
        _reads.Clear();
        _states.Clear();
        BuildTracker.ClearIfCurrent(this);
    }
}

/// <summary>
/// 状态读取作用域：一次构建期间记录读取的状态，用于在构建结束后替换依赖集合。
/// </summary>
/// <remarks>
/// 组件构建（<see cref="ComponentState"/>）与响应式内容构建（<c>ResponsiveContentState</c>）
/// 各有独立实现，但共用同一套栈式跟踪。
/// </remarks>
internal interface IStateReadScope
{
    /// <summary>
    /// 该作用域是否属于 <see cref="Responsive"/> 的动态内容构建。
    /// 动态内容 builder 必须是纯描述构建：不允许创建 <c>UseState</c>、写状态、执行 IO 或操作窗口。
    /// </summary>
    bool IsDynamicContentBuild { get; }

    /// <summary>记录一次状态读取。</summary>
    void RecordRead(StateBase state);
}

/// <summary>构建期状态读取跟踪；只在 UI 线程使用。</summary>
/// <remarks>
/// 作用域按栈管理：推入时保存上一个作用域，释放时恢复，因此嵌套构建（组件内嵌组件、
/// 响应式内容中挂载组件）与异常路径都不会把 tracker 泄漏到作用域之外。
/// </remarks>
internal static class BuildTracker
{
    [ThreadStatic]
    private static IStateReadScope? _current;

    /// <summary>当前正在收集读取的作用域；作用域之外为 null。</summary>
    public static IStateReadScope? Current => _current;

    /// <summary>是否处于动态内容（<see cref="Responsive"/> builder）构建作用域。</summary>
    public static bool IsDynamicContentBuild => _current is { IsDynamicContentBuild: true };

    /// <summary>推入一个读取作用域；返回的令牌释放时栈式恢复上一个作用域。</summary>
    public static Scope Push(IStateReadScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var previous = _current;
        _current = scope;
        return new Scope(previous);
    }

    /// <summary>记录一次状态读取（作用域之外忽略，例如缓存的绑定在 Build 之外创建）。</summary>
    public static void RecordRead(StateBase state) => _current?.RecordRead(state);

    /// <summary>若当前作用域正好是被释放的组件，则清空跟踪（卸载防御）。</summary>
    public static void ClearIfCurrent(IStateReadScope scope)
    {
        if (ReferenceEquals(_current, scope))
        {
            _current = null;
        }
    }

    /// <summary>恢复令牌：释放时把当前作用域还原为推入前的值。</summary>
    internal readonly struct Scope : IDisposable
    {
        private readonly IStateReadScope? _previous;

        internal Scope(IStateReadScope? previous) => _previous = previous;

        /// <inheritdoc />
        public void Dispose() => _current = _previous;
    }
}
