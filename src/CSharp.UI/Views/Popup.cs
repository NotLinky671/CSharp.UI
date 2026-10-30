namespace CSharp.UI;

/// <summary>弹层相对锚的首选方向；<see cref="Auto"/> 允许按可用空间上下翻转。</summary>
public enum PopupPlacement
{
    /// <summary>下方优先；下方放不下时选择空间更大的一侧（可上翻）。</summary>
    Auto,

    /// <summary>强制贴锚上方；空间不足时按该侧可用高度截断，不翻转。</summary>
    Above,

    /// <summary>强制贴锚下方；空间不足时按该侧可用高度截断，不翻转。</summary>
    Below,
}

/// <summary>
/// 锚定弹层：<see cref="Content"/> 作为真实 view 子树挂到窗口根级 overlay 层
/// （完整测量/排列/绘制/命中/焦点遍历与 mount-unmount 生命周期），不受任何中间祖先 clip 约束。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Popup"/> 本身是主树的普通节点：布局只透传 <see cref="Anchor"/>，
/// <see cref="Content"/> 不经主树布局/绘制。弹层由运行时根级 overlay 宿主承载（internal，
/// 不是公开类型）。
/// </para>
/// <para>
/// <see cref="IsOpen"/> 是双向绑定：owner 观察到 false→true 且资格门通过时打开；
/// 外点/Esc/锚失效/失活/挤占等内部关闭会由 overlay manager 把 <see cref="IsOpen"/> 写回 false
/// <b>恰好一次</b>；binding 恒 true 时不会每帧重开，必须出现新的 false→true 边沿才会再次打开。
/// 注册与状态机在安全阶段（<c>ProcessPendingWork</c>）执行，不在 Measure/Arrange/Emit 内写状态。
/// </para>
/// <para>
/// 嵌套打开（popup 内容内再请求打开）不受支持：请求被拒绝并输出运行时诊断，外层弹层保持打开。
/// </para>
/// </remarks>
public sealed class Popup : View, IOverlayOwner
{
    private readonly View[] _children;
    private PopupPlacement _preference = PopupPlacement.Auto;
    private double _margin = 4;
    private double _maxHeight = double.PositiveInfinity;

    /// <summary>由锚、弹层内容与开合绑定创建。</summary>
    /// <param name="anchor">锚视图：作为主树中的普通子项参与布局与命中。</param>
    /// <param name="content">弹层内容描述（挂到根级 overlay 层，不参与主树布局）。</param>
    /// <param name="isOpen">开合绑定；内部关闭会写回 false 一次。</param>
    /// <param name="preference">首选方向；也可经 <see cref="Preference"/> 覆盖。</param>
    public Popup(View anchor, View content, Binding<bool> isOpen, PopupPlacement preference = PopupPlacement.Auto)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(isOpen);
        Anchor = anchor;
        Content = content;
        IsOpen = isOpen;
        _preference = ValidatePreference(preference);

        // 锚在主树中始终只占一个布局子项：ForEach/多条目锚按根语义归一化为一棵锚子树
        // （与 P01 用户根的处理一致），避免多个锚节点互相重叠、定位只取首项。
        // 单视图（最常见）归一化后仍是同一描述实例，不引入额外层级。
        _children = [Reconciler.NormalizeRootView(anchor)];
    }

    /// <summary>
    /// 锚视图（调用方传入的原描述）：作为主树中的普通子项参与布局与命中。
    /// <c>ForEach</c>/多条目锚在主树里被归一化为一棵锚子树（定位矩形与锚豁免都以该子树根为准）。
    /// </summary>
    public View Anchor { get; }

    /// <summary>弹层内容描述（由 overlay 层承载，不经主树布局/绘制）。</summary>
    public View Content { get; }

    /// <summary>开合绑定（双向：读取登记依赖，内部关闭写回一次 false）。</summary>
    public Binding<bool> IsOpen { get; }

    /// <summary>首选方向；<see cref="PopupPlacement.Auto"/> 可翻转，其余强制该侧。</summary>
    public PopupPlacement Preference
    {
        get => _preference;
        init => _preference = ValidatePreference(value);
    }

    /// <summary>弹层与视口边缘的间距（有限、非负，默认 4）。</summary>
    public double Margin
    {
        get => _margin;
        init
        {
            if (!double.IsFinite(value) || value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(Margin), value, "popup 边距必须是有限非负数。");
            }

            _margin = value;
        }
    }

    /// <summary>弹层高度上限（正数或 <see cref="double.PositiveInfinity"/>，默认无上限）。</summary>
    public double MaxHeight
    {
        get => _maxHeight;
        init
        {
            if (double.IsNaN(value) || value <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(MaxHeight), value, "popup 高度上限必须是正数或正无穷。");
            }

            _maxHeight = value;
        }
    }

    /// <summary>
    /// 弹层外按下是否关闭弹层。为 true 时该次手势整体被吞并（下层不收到 Down/Move/Up/Cancel）；
    /// 为 false 时外点照常分发、弹层保持打开。
    /// </summary>
    public bool CloseOnOutsidePress { get; init; } = true;

    /// <summary>
    /// Esc 是否关闭弹层：焦点控件先路由（组合期由输入法拥有/自处理 Esc 的控件先消费），
    /// 仅在无人消费时关闭并按 SavedFocus → 锚子树首个 focusable 恢复焦点。
    /// </summary>
    public bool CloseOnEscape { get; init; } = true;

    /// <inheritdoc />
    public override string DebugName => "Popup";

    internal override IReadOnlyList<View> Children => _children;

    internal override void Measure(ViewNode node, LayoutConstraints constraints, LayoutContext context)
    {
        double width = 0;
        double height = 0;
        foreach (var child in node.ChildList)
        {
            context.Measure(child, constraints);
            width = Math.Max(width, child.DesiredSize.Width);
            height = Math.Max(height, child.DesiredSize.Height);
        }

        // 弹层不影响主布局：自身尺寸完全由锚决定。
        node.DesiredSize = new Size(width, height);
    }

    internal override void Arrange(ViewNode node, Rect bounds, LayoutContext context)
    {
        node.Frame = bounds;
        foreach (var child in node.ChildList)
        {
            context.Arrange(child, bounds);
        }
    }

    // 尺寸特征透传锚：固定大小/弹性声明继续对上层容器可见。
    internal override LayoutFeatures GetLayoutFeatures(ViewNode node, LayoutAxis mainAxis)
        => node.ChildList.Count > 0
            ? node.ChildList[0].GetLayoutFeatures(mainAxis)
            : LayoutFeatures.FromNatural(node.DesiredSize);

    internal override void OnMount(ViewNode node) => node.Runtime.Overlay.RegisterOwner(node);

    /// <inheritdoc />
    bool IOverlayOwner.TryGetOpenRequest(ViewNode node, out OverlayOpenRequest request)
    {
        request = default;

        // 读绑定即登记来源状态依赖（State → 所属组件重建/重排），每轮安全阶段只读一次。
        var open = IsOpen.Read(node);
        if (!open || node.ChildList.Count == 0)
        {
            return false;
        }

        // 锚节点是本节点唯一的布局子项（多条目锚已在构造期归一化为一棵锚子树），
        // 定位矩形与锚豁免共用该子树根的 Frame。
        request = new OverlayOpenRequest(node.ChildList[0], Content, Margin, MaxHeight, Preference);
        return true;
    }

    /// <inheritdoc />
    void IOverlayOwner.OnOverlayClosed(ViewNode node, OverlayCloseReason reason)
    {
        // owner 主动关闭时 binding 已是 false，不回写；其余内部关闭恰好写回一次。
        if (reason == OverlayCloseReason.OwnerRequest)
        {
            return;
        }

        IsOpen.Value = false;
    }

    private static PopupPlacement ValidatePreference(PopupPlacement preference)
        => preference is PopupPlacement.Auto or PopupPlacement.Above or PopupPlacement.Below
            ? preference
            : throw new ArgumentOutOfRangeException(nameof(preference), preference, "未定义的首选方向。");
}
