namespace CSharp.UI;

/// <summary>
/// 持久运行时节点：与视图描述一一对应，保存身份、布局结果、交互状态、组件状态与资源生命周期。
/// </summary>
/// <remarks>
/// 协调更新在相同身份上复用同一节点实例，因此节点的引用可以作为“该项的运行时状态”的身份凭据。
/// 应用不直接创建节点；通过 <see cref="UiRuntime.Root"/> 及诊断辅助方法读取。
/// </remarks>
public sealed class ViewNode
{
    private readonly List<ViewNode> _children = [];

    internal ViewNode(View view, UiRuntime runtime)
    {
        View = view;
        Runtime = runtime;
    }

    /// <summary>当前视图描述。协调更新后会指向最新的描述对象。</summary>
    public View View { get; internal set; }

    /// <summary>父节点；根节点为 null。</summary>
    public ViewNode? Parent { get; internal set; }

    /// <summary>子节点（按描述顺序，也是绘制顺序）。</summary>
    public IReadOnlyList<ViewNode> Children => _children;

    internal List<ViewNode> ChildList => _children;

    /// <summary>所属运行时。</summary>
    public UiRuntime Runtime { get; }

    /// <summary>排列后的绝对矩形（逻辑像素，相对窗口内容区左上角）。</summary>
    public Rect Frame { get; internal set; }

    /// <summary>测量得到的期望尺寸。</summary>
    public Size DesiredSize { get; internal set; }

    /// <summary>该节点生效的环境值（字体、前景色、主题、圆角、透明度、禁用）。</summary>
    public EnvironmentValues Environment { get; internal set; } = EnvironmentValues.Default;

    /// <summary>指针是否悬停在该节点上。</summary>
    public bool IsHovered { get; internal set; }

    /// <summary>该节点是否处于按下状态。</summary>
    public bool IsPressed { get; internal set; }

    /// <summary>该节点是否拥有焦点。</summary>
    public bool IsFocused { get; internal set; }

    /// <summary>该节点是否持有指针捕获（按下后拖出仍接收事件）。</summary>
    public bool IsPointerCaptured { get; internal set; }

    /// <summary>控件自定义的交互状态（例如 Slider 拖动起点）。卸载时清空。</summary>
    public object? InteractionState { get; set; }

    /// <summary>视图在测量阶段写入的缓存（例如文本排版结果、图片自然尺寸）。</summary>
    internal object? ViewCache { get; set; }

    /// <summary>祖先或自身处于 Disabled 状态。</summary>
    public bool IsEffectivelyDisabled => Environment.Disabled;

    /// <summary>节点是否仍挂载在运行时树上。</summary>
    public bool IsMounted { get; internal set; }

    /// <summary>节点深度（根为 0）。</summary>
    public int Depth => Parent is null ? 0 : Parent.Depth + 1;

    internal bool MeasureValid { get; set; }

    internal bool ArrangeValid { get; set; }

    internal LayoutConstraints LastConstraints { get; set; }

    /// <summary>最近一次测量缓存的提案性质（有效提案或探测结果）。</summary>
    internal MeasureProposalKind LastProposalKind { get; set; } = MeasureProposalKind.Effective;

    /// <summary>
    /// 读取该节点当前对外暴露的按轴尺寸特征（显式固定/最小/最大与弹性配额）。
    /// 特征来自视图声明，不从被父约束截断的 <see cref="DesiredSize"/> 反推。
    /// </summary>
    internal LayoutFeatures GetLayoutFeatures() => View.GetLayoutFeatures(this);

    /// <summary>同级身份键（来自 Key 修饰符或 ForEach 键选择器）。</summary>
    internal object? IdentityKey { get; set; }

    /// <summary>是否指定了显式身份键。</summary>
    internal bool HasIdentityKey { get; set; }

    /// <summary>节点创建时的视图类型，用于按类型匹配。</summary>
    internal Type IdentityType { get; set; } = typeof(View);

    internal ComponentState? ComponentState { get; set; }

    internal Component? ComponentInstance { get; set; }

    internal List<IDisposable>? Resources { get; set; }

    /// <summary>ScrollView 的水平滚动偏移（逻辑像素）。</summary>
    public double ScrollOffsetX { get; internal set; }

    /// <summary>ScrollView 的垂直滚动偏移（逻辑像素）。</summary>
    public double ScrollOffsetY { get; internal set; }

    internal bool IsUnmounting { get; set; }

    /// <summary>在自身与后代中查找第一个匹配的节点。</summary>
    public ViewNode? FindDescendant<TView>()
        where TView : View
    {
        if (View is TView)
        {
            return this;
        }

        foreach (var child in _children)
        {
            var found = child.FindDescendant<TView>();
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>在自身与后代中查找所有匹配的节点（深度优先、描述顺序）。</summary>
    public IEnumerable<ViewNode> FindDescendants<TView>()
        where TView : View
    {
        if (View is TView)
        {
            yield return this;
        }

        foreach (var child in _children)
        {
            foreach (var node in child.FindDescendants<TView>())
            {
                yield return node;
            }
        }
    }

    /// <summary>按深度优先、描述顺序枚举自身与全部后代。</summary>
    public IEnumerable<ViewNode> Descendants()
    {
        yield return this;
        foreach (var child in _children)
        {
            foreach (var node in child.Descendants())
            {
                yield return node;
            }
        }
    }

    /// <summary>查找该节点上按名称存储的组件状态；不存在或类型不符时返回 null。</summary>
    public State<T>? FindState<T>(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return ComponentState?.FindState<T>(name);
    }

    /// <summary>读取该节点上按名称存储的状态值；不存在时抛出 <see cref="KeyNotFoundException"/>。</summary>
    public T GetStateValue<T>(string name)
    {
        var state = FindState<T>(name) ?? throw new KeyNotFoundException($"节点 {DebugPath} 上没有名为 \"{name}\" 的 State<{typeof(T).Name}>。");
        return state.Value;
    }

    /// <summary>注册一个随节点卸载而释放的资源。</summary>
    public void AddResource(IDisposable resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        (Resources ??= []).Add(resource);
    }

    /// <summary>从根到当前的调试路径（类型名与键）。</summary>
    public string DebugPath
    {
        get
        {
            var parts = new List<string>();
            for (var node = this; node is not null; node = node.Parent)
            {
                var key = node.View switch
                {
                    KeyedView keyed => $"[{keyed.Key}]",
                    Component component => $"[{component.DebugName}]",
                    _ => string.Empty,
                };
                parts.Add(node.View.DebugName + key);
            }

            parts.Reverse();
            return string.Join(" / ", parts);
        }
    }

    /// <summary>标记该节点需要重新布局（并重绘）。</summary>
    public void InvalidateLayout()
    {
        Runtime.InvalidateLayout(this);
    }

    /// <summary>标记该节点只需要重绘。</summary>
    public void InvalidateRender()
    {
        Runtime.InvalidateRender();
    }

    public override string ToString() => $"{View.DebugName} {Frame}";

    internal void ResetInteractionState()
    {
        IsHovered = false;
        IsPressed = false;
        IsFocused = false;
        IsPointerCaptured = false;
        InteractionState = null;
    }
}
