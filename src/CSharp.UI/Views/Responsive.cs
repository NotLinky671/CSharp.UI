namespace CSharp.UI;

/// <summary>
/// <see cref="Responsive"/> 内容构建时提供的父级可用逻辑宽度信息。
/// </summary>
/// <remarks>
/// <para>
/// 宽度是父容器扣除 <c>Padding</c> 等修饰后、最终有效布局提案中的逻辑宽度，
/// 不读取窗口、屏幕分辨率或 DPI：同一逻辑宽度在 100%/125%/150%/200% 下的取值相同。
/// </para>
/// <para>
/// 宽度无界时以 <see cref="double.PositiveInfinity"/> 搭配 <see cref="IsWidthBounded"/>=false 明确表达，
/// 不使用 0、屏幕宽度或任意大常数冒充；应用必须能选择自然尺寸分支。
/// </para>
/// </remarks>
public readonly record struct ResponsiveInfo
{
    /// <summary>由可用逻辑宽度与是否有界创建；两者必须一致。</summary>
    /// <param name="availableWidth">父级可用的逻辑宽度；无界时必须是 <see cref="double.PositiveInfinity"/>。</param>
    /// <param name="isWidthBounded">宽度是否有界（有限）。</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// 宽度为 NaN/负数，或“有界”与“有限宽度”不一致时抛出。
    /// </exception>
    public ResponsiveInfo(double availableWidth, bool isWidthBounded)
    {
        if (double.IsNaN(availableWidth) || availableWidth < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(availableWidth),
                availableWidth,
                "可用宽度必须是有限的非负值或 PositiveInfinity（禁止 NaN 与负值）。");
        }

        if (isWidthBounded != double.IsFinite(availableWidth))
        {
            throw new ArgumentOutOfRangeException(
                nameof(isWidthBounded),
                isWidthBounded,
                "有界宽度必须用有限值表达；无界宽度必须以 PositiveInfinity 搭配 false 表达。");
        }

        AvailableWidth = availableWidth;
        IsWidthBounded = isWidthBounded;
    }

    /// <summary>父级最终有效提案提供的可用逻辑宽度；无界时为 <see cref="double.PositiveInfinity"/>。</summary>
    public double AvailableWidth { get; }

    /// <summary>可用宽度是否有限（有界）。</summary>
    public bool IsWidthBounded { get; }
}

/// <summary>
/// 任意内容分支的响应式容器：按父容器扣除修饰后的最终有效逻辑宽度选择内容结构。
/// </summary>
/// <remarks>
/// <para>
/// 用法：<c>new Responsive(info =&gt; info.IsWidthBounded &amp;&amp; info.AvailableWidth &lt; 560 ? narrow : wide)</c>。
/// 有界宽度下自身占满所分配宽度、高度由内容自然决定；无界宽度使用内容的自然尺寸。
/// </para>
/// <para>
/// <b>构建阶段契约</b>：测量（含探测与自然尺寸测量）只登记最终有效提案，不执行 <see cref="Builder"/>、
/// 不协调或卸载节点、不递归调用 <c>ProcessPendingWork</c>/<c>BuildFrame</c>；
/// 内容由 <see cref="UiRuntime"/> 在重建与布局之间的安全阶段构建，并重新布局到稳定后才绘制。
/// 同一节点一轮内的多次提案合并为最后一次有效提案；回到已稳定提案时撤销过时请求。
/// </para>
/// <para>
/// <b>描述版本</b>：协调器替换该节点的描述时内容会按新描述重建一次（即使宽度不变），
/// 因此同宽的新 builder 描述不会被误忽略；而普通重复测量、相同描述不会重建，
/// 也不会因为闭包身份差异反复触发布局。
/// </para>
/// <para>
/// 内容切换沿用现有协调规则：同父级同 Key/类型的子节点复用，类型或 Key 变化会卸载旧分支。
/// 跨分支保持输入焦点等编辑状态应使用 <see cref="AdaptiveStack"/>。本阶段不提供状态依赖能力。
/// </para>
/// </remarks>
public sealed class Responsive : View
{
    /// <summary>由内容构建函数创建。</summary>
    /// <param name="builder">接收父级有效宽度、返回内容描述的纯构建函数；不得写状态、执行 IO 或操作窗口。</param>
    public Responsive(Func<ResponsiveInfo, View> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        Builder = builder;
    }

    /// <summary>内容构建函数；只在运行时安全阶段执行。</summary>
    public Func<ResponsiveInfo, View> Builder { get; }

    /// <inheritdoc />
    public override string DebugName => "Responsive";

    /// <summary>最终布局依赖有效提案（动态内容需要父级最终宽度才能构建）。</summary>
    internal override bool RequiresEffectiveProposal => true;

    // 描述身份由协调器按引用替换；新描述必须让内容按新 builder 重建一次，因此不做布局等价判断。
    // 既有子树由 Reconciler 保留到安全阶段，不能在这里按空 Children 清空。
    internal override bool IsLayoutEquivalent(View other) => false;

    internal override void Measure(ViewNode node, LayoutConstraints constraints, LayoutContext context)
    {
        var info = new ResponsiveInfo(
            constraints.HasBoundedWidth ? constraints.MaxWidth : double.PositiveInfinity,
            constraints.HasBoundedWidth);

        // 只登记最终有效提案：探测与自然尺寸测量不激活候选分支。
        if (context.IsEffectiveProposal)
        {
            context.RequestResponsiveContent(node, info);
        }

        // 内容按父级宽度测量；高度留给内容自然决定（不把父级紧高度强加给内容）。
        var contentConstraints = new LayoutConstraints(
            constraints.MinWidth,
            constraints.MaxWidth,
            0,
            constraints.MaxHeight);

        var contentWidth = 0.0;
        var contentHeight = 0.0;
        if (node.ChildList.Count > 0)
        {
            context.Measure(node.ChildList[0], contentConstraints);
            contentWidth = node.ChildList[0].DesiredSize.Width;
            contentHeight = node.ChildList[0].DesiredSize.Height;
        }

        node.DesiredSize = constraints.HasBoundedWidth
            ? new Size(Math.Max(0, constraints.MaxWidth), Math.Max(0, contentHeight))
            : new Size(Math.Max(0, contentWidth), Math.Max(0, contentHeight));
    }

    internal override void Arrange(ViewNode node, Rect bounds, LayoutContext context)
    {
        node.Frame = bounds;
        if (node.ChildList.Count > 0)
        {
            context.Arrange(node.ChildList[0], bounds);
        }
    }
}
