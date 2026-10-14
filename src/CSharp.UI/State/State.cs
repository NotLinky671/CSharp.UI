namespace CSharp.UI;

/// <summary>
/// 可观察状态：由运行时组件节点持有，通过 <see cref="Value"/> 读写。
/// </summary>
/// <typeparam name="T">值类型。</typeparam>
/// <remarks>
/// <para>构建期间读取 <see cref="Value"/> 会自动把当前组件登记为依赖；写入仅在值实际变化时通知依赖组件。</para>
/// <para>状态只能在 UI 线程读写；后台线程应先通过 <see cref="UiDispatcher.Post"/> 回到 UI 线程。</para>
/// </remarks>
public sealed class State<T> : StateBase
{
    private T _value;

    internal State(string name, T initialValue, ViewNode owner)
        : base(name, owner)
    {
        _value = initialValue;
    }

    /// <summary>当前值。读取会登记构建依赖，写入会触发依赖组件更新。</summary>
    public T Value
    {
        get
        {
            RecordRead();
            return _value;
        }

        set
        {
            EnsureUiThread();
            if (EqualityComparer<T>.Default.Equals(_value, value))
            {
                return;
            }

            _value = value;
            NotifyDependents();
        }
    }

    /// <summary>不登记依赖地读取当前值（用于诊断与测试）。</summary>
    public T PeekValue() => _value;

    internal override void RecordRead() => BuildTracker.RecordRead(this);

    /// <summary>转换为读写该状态的绑定。</summary>
    public Binding<T> ToBinding()
    {
        // 构建期间创建绑定即视为读取该状态：让“new Toggle(isOn)”这类写法自动建立依赖。
        RecordRead();
        return new Binding<T>(() => Value, value => Value = value, this);
    }

    /// <summary>隐式把状态当作双向绑定使用。</summary>
    public static implicit operator Binding<T>(State<T> state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.ToBinding();
    }

    public override string ToString() => $"{Name} = {_value}";
}

/// <summary>
/// 双向绑定：连接状态与输入控件。控件通过 <see cref="Value"/> 读显示值、写回用户输入。
/// </summary>
/// <typeparam name="T">值类型。</typeparam>
public sealed class Binding<T>
{
    private readonly Func<T> _getter;
    private readonly Action<T> _setter;

    /// <summary>由读写委托创建。</summary>
    public Binding(Func<T> getter, Action<T> setter)
        : this(getter, setter, null)
    {
    }

    internal Binding(Func<T> getter, Action<T> setter, StateBase? source)
    {
        ArgumentNullException.ThrowIfNull(getter);
        ArgumentNullException.ThrowIfNull(setter);
        _getter = getter;
        _setter = setter;
        Source = source;
    }

    /// <summary>绑定的来源状态（由 <see cref="State{T}.ToBinding"/> 创建时非 null）。</summary>
    internal StateBase? Source { get; }

    /// <summary>
    /// 读取当前值，并在需要时把来源状态补登记为控件所属组件节点的依赖。
    /// 用于绑定被保存在字段中、构建期没有显式读取状态的场景。
    /// </summary>
    public T Read(ViewNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (Source is { } state)
        {
            node.Runtime.RegisterBindingDependency(node, state);
        }

        return _getter();
    }

    /// <summary>当前值；读取委托 getter，写入委托 setter。</summary>
    public T Value
    {
        get => _getter();
        set => _setter(value);
    }

    /// <summary>由读写委托创建绑定。</summary>
    public static Binding<T> From(Func<T> getter, Action<T> setter) => new(getter, setter);

    /// <summary>由固定值创建只读绑定（写入被忽略）。</summary>
    public static Binding<T> Constant(T value) => new(() => value, _ => { });

    /// <summary>返回把值映射到新类型的绑定（单向投影，写入按逆映射处理）。</summary>
    public Binding<TTarget> Select<TTarget>(Func<T, TTarget> forward, Func<TTarget, T> backward)
    {
        ArgumentNullException.ThrowIfNull(forward);
        ArgumentNullException.ThrowIfNull(backward);
        return new Binding<TTarget>(() => forward(Value), value => Value = backward(value));
    }
}

/// <summary>状态基类：保存名称、所属节点与依赖它的组件节点集合。</summary>
public abstract class StateBase
{
    private readonly List<ViewNode> _dependents = [];

    internal StateBase(string name, ViewNode owner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(owner);
        Name = name;
        Owner = owner;
    }

    /// <summary>状态名（在所属组件内唯一）。</summary>
    public string Name { get; }

    /// <summary>持有该状态的组件节点。</summary>
    public ViewNode Owner { get; }

    internal IReadOnlyList<ViewNode> Dependents => _dependents;

    internal void AddDependent(ViewNode node)
    {
        if (!_dependents.Contains(node))
        {
            _dependents.Add(node);
        }
    }

    internal void RemoveDependent(ViewNode node) => _dependents.Remove(node);

    internal void ClearDependents() => _dependents.Clear();

    internal abstract void RecordRead();

    internal void EnsureUiThread()
    {
        var dispatcher = Owner.Runtime.Dispatcher;
        if (!dispatcher.IsOnUiThread)
        {
            throw UiThreadViolationException.ForPost();
        }
    }

    internal void NotifyDependents()
    {
        // 复制一份，避免通知过程中依赖集合被修改。
        var snapshot = _dependents.ToArray();
        foreach (var node in snapshot)
        {
            if (node.IsMounted)
            {
                node.Runtime.InvalidateComponent(node);
            }
        }
    }
}
