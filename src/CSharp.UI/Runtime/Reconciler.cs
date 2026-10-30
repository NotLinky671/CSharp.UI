namespace CSharp.UI;

/// <summary>同级重复键诊断。</summary>
public sealed class DuplicateKeyException : InvalidOperationException
{
    internal DuplicateKeyException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// 协调器：把不可变视图描述树映射到持久运行时节点树。
/// </summary>
/// <remarks>
/// 匹配规则：
/// <list type="bullet">
/// <item>指定 <c>Key</c> 的同级条目按 (键, 视图类型) 匹配；类型不同则替换节点。</item>
/// <item>未指定键的条目按视图类型与结构位置顺序匹配。</item>
/// <item>匹配成功的节点复用同一实例，只更新属性、事件处理器与子项。</item>
/// </list>
/// </remarks>
internal static class Reconciler
{
    /// <summary>创建根节点并挂载。</summary>
    public static ViewNode MountRoot(UiRuntime runtime, View root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var descriptors = Expand(root);
        if (IsCollectionView(root) || descriptors.Count != 1)
        {
            // 根为集合视图（ForEach）或多条目：包一层隐式容器，保证只有一个根节点。
            // 集合视图即使只有 0/1 个条目也包裹，避免条目数量变化时改变节点层级。
            var container = new ImplicitRootContainer(descriptors);
            descriptors = Expand(container);
        }

        return CreateNode(runtime, descriptors[0]);
    }

    /// <summary>
    /// 按根语义归一化用户根描述：集合视图（<c>ForEach</c>）或多条目仍包一层隐式容器，
    /// 保证用户根只有一个节点、全部条目参与布局（与 <see cref="MountRoot"/> 的根规则一致）。
    /// </summary>
    internal static View NormalizeRootView(View root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var descriptors = Expand(root);
        if (IsCollectionView(root) || descriptors.Count != 1)
        {
            return new ImplicitRootContainer(descriptors);
        }

        return descriptors[0].View;
    }

    /// <summary>把视图（可能已被 Key / ForEach 包装）展开为同级条目。</summary>
    public static List<ChildDescriptor> Expand(View view)
    {
        var output = new List<ChildDescriptor>(1);
        Expand(view, null, false, output);
        return output;
    }

    private static void Expand(View view, object? prefixKey, bool hasPrefixKey, List<ChildDescriptor> output)
    {
        ArgumentNullException.ThrowIfNull(view);
        var current = view;
        var key = prefixKey;
        var hasKey = hasPrefixKey;

        while (true)
        {
            switch (current)
            {
                case KeyedView keyed:
                    key = hasKey && prefixKey is not null ? (prefixKey, keyed.Key) : keyed.Key;
                    hasKey = true;
                    current = keyed.Child;
                    continue;
                case ForEachView many:
                    many.Expand(key, hasKey, output);
                    return;
                default:
                    output.Add(new ChildDescriptor(current, key, hasKey));
                    return;
            }
        }
    }

    /// <summary>创建并初始化节点（不加入父节点列表）。</summary>
    public static ViewNode CreateNode(UiRuntime runtime, ChildDescriptor descriptor)
    {
        var node = new ViewNode(descriptor.View, runtime)
        {
            IdentityKey = descriptor.Key,
            HasIdentityKey = descriptor.HasKey,
            IdentityType = descriptor.View.GetType(),
            IsMounted = true,
        };

        node.View.OnMount(node);
        InitializeContent(node);
        return node;
    }

    /// <summary>初始化节点内容：组件执行 Build，其他视图协调子项。</summary>
    private static void InitializeContent(ViewNode node)
    {
        switch (node.View)
        {
            case Component component:
                BuildComponent(node, component);
                return;
            case Responsive responsive:
                // 响应式内容由运行时在安全阶段构建。描述更新时保留既有子树（不能先按空 Children 清空），
                // 只递增描述版本，使同宽的新描述也重建一次；首次挂载由随后的有效测量登记请求。
                node.Runtime.MarkResponsiveDescription(node);
                return;
            case ImplicitRootContainer container:
                ReconcileDescriptors(node, [.. container.Descriptors]);
                return;
            default:
                ReconcileChildren(node, node.View.Children);
                return;
        }
    }

    /// <summary>把响应式 builder 返回的描述协调为节点内容；多条目时包裹隐式容器。</summary>
    /// <remarks>
    /// 与 <see cref="BuildComponent"/> 相同的结构规则：单视图保持无额外层级；
    /// <c>ForEach</c> 或多条目包裹隐式容器（按垂直堆叠排列），保证节点只有一个内容子项并获得布局。
    /// </remarks>
    public static void ReconcileDynamicContent(ViewNode node, View content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var descriptors = Expand(content);
        if (!IsCollectionView(content) && descriptors.Count == 1)
        {
            ReconcileDescriptors(node, descriptors);
            return;
        }

        ReconcileDescriptors(node, [new ChildDescriptor(new ImplicitRootContainer(descriptors), null, false)]);
    }

    /// <summary>执行组件 Build 并把结果协调为组件的子节点。</summary>
    /// <remarks>
    /// 组件返回的描述若展开为多个同级条目（例如直接返回 <c>ForEach</c>），会包裹一层隐式容器，
    /// 保证组件的子项同样被测量与排列（否则只有第一个子项参与布局）。
    /// </remarks>
    public static void BuildComponent(ViewNode node, Component component)
    {
        var state = node.ComponentState ??= new ComponentState(node);
        node.ComponentInstance = component;

        state.BeginBuild();
        View built;
        try
        {
            built = component.BuildFrom(node);
        }
        finally
        {
            state.EndBuild();
        }

        var descriptors = Expand(built);
        if (!IsCollectionView(built) && descriptors.Count == 1)
        {
            // 普通单视图组件：保持既有行为，不引入额外层级。
            ReconcileDescriptors(node, descriptors);
            return;
        }

        // 集合视图（ForEach）或多条目：包裹隐式容器，保证组件只有一个子项并获得布局。
        // 集合视图在 0/1/多项时都包裹，使节点层级稳定；否则数量跨越 1 的边界会卸载整棵子树，
        // 带键项目的状态、焦点与编辑状态都会丢失（稳定键只在同一父级内匹配）。
        ReconcileDescriptors(node, [new ChildDescriptor(new ImplicitRootContainer(descriptors), null, false)]);
    }

    /// <summary>取出被 Key 包装的最内层视图（<c>KeyedView</c> 只影响身份，不影响结构判定）。</summary>
    private static View UnwrapKey(View view)
    {
        var current = view;
        while (current is KeyedView keyed)
        {
            current = keyed.Child;
        }

        return current;
    }

    /// <summary>是否为集合视图（<c>ForEach</c>）：这类视图的条目数量可变，需要稳定的容器边界。</summary>
    private static bool IsCollectionView(View view) => UnwrapKey(view) is ForEachView;

    /// <summary>用新描述更新已存在的节点。</summary>
    public static void UpdateNode(ViewNode node, ChildDescriptor descriptor)
    {
        var previous = node.View;
        var next = descriptor.View;
        node.View = next;

        // 描述被替换：旧绑定在布局/绘制阶段补登记的长期依赖必须先释放，
        // 否则复用同一节点换用新绑定后仍会残留旧来源状态的订阅。
        node.Runtime.ReleaseBindingDependencies(node);

        if (!next.IsLayoutEquivalent(previous))
        {
            node.InvalidateLayout();
        }
        else
        {
            node.InvalidateRender();
        }

        next.OnUpdate(node, previous);
        InitializeContent(node);
    }

    /// <summary>协调子节点列表：复用、创建、卸载并重新排序。</summary>
    /// <remarks>展开结果多于一个同级条目时由调用方（<see cref="BuildComponent"/>、<see cref="MountRoot"/>）包裹隐式容器。</remarks>
    public static void ReconcileChildren(ViewNode parent, IReadOnlyList<View> children)
    {
        var descriptors = new List<ChildDescriptor>(children.Count);
        foreach (var child in children)
        {
            Expand(child, null, false, descriptors);
        }

        ReconcileDescriptors(parent, descriptors);
    }

    /// <summary>
    /// 按展开后的条目协调子节点。
    /// </summary>
    /// <remarks>
    /// 描述集合按<b>只读快照</b>读取：调用方可以传数组/集合表达式（单次分配、协调期间不可变）。
    /// 协调期间触发的 OnMount/OnUnmount/owner 回调可能在别处改树，不能用复用的可变缓冲。
    /// </remarks>
    public static void ReconcileDescriptors(ViewNode parent, IReadOnlyList<ChildDescriptor> descriptors)
    {
        ValidateKeys(parent, descriptors);

        var runtime = parent.Runtime;
        var oldNodes = parent.ChildList.ToArray();
        var used = new bool[oldNodes.Length];
        var result = new ViewNode?[descriptors.Count];
        var structureChanged = oldNodes.Length != descriptors.Count;

        // 第一遍：按 (键, 类型) 匹配显式键条目。
        for (var i = 0; i < descriptors.Count; i++)
        {
            var descriptor = descriptors[i];
            if (!descriptor.HasKey)
            {
                continue;
            }

            for (var j = 0; j < oldNodes.Length; j++)
            {
                if (used[j] || !oldNodes[j].HasIdentityKey || oldNodes[j].IdentityType != descriptor.View.GetType())
                {
                    continue;
                }

                if (Equals(oldNodes[j].IdentityKey, descriptor.Key))
                {
                    used[j] = true;
                    result[i] = oldNodes[j];
                    break;
                }
            }
        }

        // 第二遍：未指定键的条目按类型与结构位置顺序匹配。
        // 第二遍：未指定键的条目按“结构位置 + 类型”匹配。
        // 只与同一索引上的旧节点匹配：跨位置保留身份必须使用显式 Key，否则状态会迁移到不对应的视图上。
        for (var i = 0; i < descriptors.Count; i++)
        {
            var descriptor = descriptors[i];
            if (descriptor.HasKey || result[i] is not null || i >= oldNodes.Length)
            {
                continue;
            }

            var candidate = oldNodes[i];
            if (used[i] || candidate.HasIdentityKey || candidate.IdentityType != descriptor.View.GetType())
            {
                continue;
            }

            used[i] = true;
            result[i] = candidate;
        }

        // 更新或创建。
        for (var i = 0; i < descriptors.Count; i++)
        {
            var descriptor = descriptors[i];
            var node = result[i];
            if (node is null)
            {
                node = CreateNode(runtime, descriptor);
                result[i] = node;
                structureChanged = true;
            }
            else if (!ReferenceEquals(node.View, descriptor.View))
            {
                node.IdentityKey = descriptor.Key;
                node.HasIdentityKey = descriptor.HasKey;
                UpdateNode(node, descriptor);
            }
        }

        // 卸载不再需要的旧节点。
        for (var j = 0; j < oldNodes.Length; j++)
        {
            if (!used[j])
            {
                Unmount(oldNodes[j]);
                structureChanged = true;
            }
        }

        // 重新排序并修正父子关系。
        for (var i = 0; i < result.Length; i++)
        {
            if (!ReferenceEquals(i < oldNodes.Length ? oldNodes[i] : null, result[i]))
            {
                structureChanged = true;
                break;
            }
        }

        parent.ChildList.Clear();
        foreach (var node in result)
        {
            var child = node!;
            child.Parent = parent;
            parent.ChildList.Add(child);
        }

        // 子项增删或顺序变化时父级的测量缓存必然失效：新增子项不会经过 UpdateNode，
        // 若不在这里失效，父级会命中旧的测量缓存，新子项停留在 0×0。
        if (structureChanged)
        {
            parent.InvalidateLayout();
        }
    }

    private static void ValidateKeys(ViewNode parent, IReadOnlyList<ChildDescriptor> descriptors)
    {
        if (descriptors.Count < 2)
        {
            return;
        }

        var seen = new Dictionary<(object? Key, Type Type), ChildDescriptor>();
        foreach (var descriptor in descriptors)
        {
            if (!descriptor.HasKey)
            {
                continue;
            }

            var identity = (descriptor.Key, descriptor.View.GetType());
            if (seen.TryGetValue(identity, out var existing))
            {
                throw new DuplicateKeyException(
                    $"同级节点出现重复键 \"{descriptor.Key}\"（类型 {descriptor.View.GetType().Name}，位于 {parent.DebugPath}）。" +
                    $"重复键会让协调器无法区分两个条目，请为每个项目提供唯一键；相同键的类型 {existing.View.GetType().Name} 与 " +
                    $"{descriptor.View.GetType().Name} 也会被视为冲突。");
            }

            seen[identity] = descriptor;
        }
    }

    /// <summary>卸载节点及其子树，触发清理并释放资源与订阅。</summary>
    /// <remarks>
    /// 使用显式后序遍历（先子后父）而不是递归：动态内容可能形成很深的链，
    /// 卸载不能在耗尽调用栈时失败。兄弟节点的清理顺序与原递归实现一致。
    /// </remarks>
    public static void Unmount(ViewNode node)
    {
        if (!node.IsMounted || node.IsUnmounting)
        {
            return;
        }

        var order = new List<ViewNode>();
        var pending = new Stack<ViewNode>();
        pending.Push(node);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!current.IsMounted || current.IsUnmounting)
            {
                continue;
            }

            current.IsUnmounting = true;
            order.Add(current);
            foreach (var child in current.ChildList)
            {
                pending.Push(child);
            }
        }

        for (var i = order.Count - 1; i >= 0; i--)
        {
            CleanupNode(order[i]);
        }
    }

    private static void CleanupNode(ViewNode node)
    {
        node.Runtime.OnNodeUnmounting(node);

        try
        {
            node.View.OnUnmount(node);
        }
        finally
        {
            if (node.Resources is { Count: > 0 } resources)
            {
                foreach (var resource in resources)
                {
                    try
                    {
                        resource.Dispose();
                    }
                    catch (Exception ex)
                    {
                        node.Runtime.ReportDiagnostic($"释放节点 {node.DebugPath} 的资源时出错：{ex.Message}");
                    }
                }

                resources.Clear();
            }

            node.ComponentState?.Release();
            node.ComponentState = null;
            node.ComponentInstance = null;
            node.ResetInteractionState();
            node.ChildList.Clear();
            node.IsMounted = false;
            node.IsUnmounting = false;
            node.Parent = null;
        }
    }

    private static string Describe(ChildDescriptor descriptor) => descriptor.Key?.ToString() ?? Guid.NewGuid().ToString("N");
}

/// <summary>
/// 隐式根容器：当根视图展开为多个同级条目（例如根为 <c>ForEach</c>）时包裹它们。
/// </summary>
/// <remarks>
/// 布局语义：按垂直堆叠排列子项（间距 0、交叉轴拉伸），与 <see cref="VStack"/> 一致，
/// 因此根 <c>ForEach</c> 的多个子项都会获得合理布局而不是静默变成空界面。
/// </remarks>
internal sealed class ImplicitRootContainer : View
{
    private readonly List<ChildDescriptor> _descriptors;

    public ImplicitRootContainer(List<ChildDescriptor> descriptors) => _descriptors = descriptors;

    public override string DebugName => "Root";

    internal List<ChildDescriptor> Descriptors => _descriptors;

    // 内容由描述列表携带（不经过 View.Children），无法参与结构比较：保守地要求重新布局。
    internal sealed override bool IsLayoutEquivalent(View other) => false;

    internal override void Measure(ViewNode node, LayoutConstraints constraints, LayoutContext context)
        => StackLayout.Measure(node, constraints, context, StackOrientation.Vertical, 0, Alignment.Stretch);

    internal override void Arrange(ViewNode node, Rect bounds, LayoutContext context)
    {
        node.Frame = bounds;
        StackLayout.Arrange(node, bounds, context, StackOrientation.Vertical, 0, Alignment.Stretch);
    }
}
