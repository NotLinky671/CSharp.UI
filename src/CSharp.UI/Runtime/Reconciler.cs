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
        if (descriptors.Count != 1)
        {
            // 根视图展开为多个同级条目（例如根为 ForEach）：包一层隐式容器，保证只有一个根节点。
            var container = new ImplicitRootContainer(descriptors);
            descriptors = Expand(container);
        }

        return CreateNode(runtime, descriptors[0]);
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
            case ImplicitRootContainer container:
                ReconcileDescriptors(node, [.. container.Descriptors]);
                return;
            default:
                ReconcileChildren(node, node.View.Children);
                return;
        }
    }

    /// <summary>执行组件 Build 并把结果协调为组件的唯一子节点。</summary>
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

        ReconcileChildren(node, [built]);
    }

    /// <summary>用新描述更新已存在的节点。</summary>
    public static void UpdateNode(ViewNode node, ChildDescriptor descriptor)
    {
        var previous = node.View;
        var next = descriptor.View;
        node.View = next;

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
    public static void ReconcileChildren(ViewNode parent, IReadOnlyList<View> children)
    {
        var descriptors = new List<ChildDescriptor>(children.Count);
        foreach (var child in children)
        {
            Expand(child, null, false, descriptors);
        }

        ReconcileDescriptors(parent, descriptors);
    }

    /// <summary>按展开后的条目协调子节点。</summary>
    public static void ReconcileDescriptors(ViewNode parent, List<ChildDescriptor> descriptors)
    {
        ValidateKeys(parent, descriptors);

        var runtime = parent.Runtime;
        var oldNodes = parent.ChildList.ToArray();
        var used = new bool[oldNodes.Length];
        var result = new ViewNode?[descriptors.Count];

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
        for (var i = 0; i < descriptors.Count; i++)
        {
            var descriptor = descriptors[i];
            if (descriptor.HasKey || result[i] is not null)
            {
                continue;
            }

            for (var j = 0; j < oldNodes.Length; j++)
            {
                if (used[j] || oldNodes[j].HasIdentityKey || oldNodes[j].IdentityType != descriptor.View.GetType())
                {
                    continue;
                }

                used[j] = true;
                result[i] = oldNodes[j];
                break;
            }
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
            }
        }

        // 重新排序并修正父子关系。
        parent.ChildList.Clear();
        foreach (var node in result)
        {
            var child = node!;
            child.Parent = parent;
            parent.ChildList.Add(child);
        }
    }

    private static void ValidateKeys(ViewNode parent, List<ChildDescriptor> descriptors)
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
    public static void Unmount(ViewNode node)
    {
        if (!node.IsMounted || node.IsUnmounting)
        {
            return;
        }

        node.IsUnmounting = true;
        foreach (var child in node.ChildList.ToArray())
        {
            Unmount(child);
        }

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
/// 隐式根容器：当根视图展开为多个同级条目（例如根为 <c>ForEach</c>）时包裹它们，
/// 直接携带已展开的子项描述，避免丢失键信息。
/// </summary>
internal sealed class ImplicitRootContainer : View
{
    private readonly List<ChildDescriptor> _descriptors;

    public ImplicitRootContainer(List<ChildDescriptor> descriptors) => _descriptors = descriptors;

    public override string DebugName => "Root";

    internal List<ChildDescriptor> Descriptors => _descriptors;
}
