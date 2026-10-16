namespace CSharp.UI;

/// <summary>
/// 测量/排列阶段上下文：提供服务访问、失效标记与子节点递归入口。
/// </summary>
internal sealed class LayoutContext
{
    private readonly List<MeasureProbeScope> _probeScopes = [];
    private readonly Dictionary<ViewNode, bool> _flexSubtreeCache = new(ReferenceEqualityComparer.Instance);
    private readonly LayoutDiagnostics? _diagnostics;
    private int _intrinsicDepth;

    public LayoutContext(
        UiRuntime runtime,
        ITextService textService,
        IImageService? imageService,
        double pixelScale,
        LayoutDiagnostics? diagnostics = null)
    {
        Runtime = runtime;
        TextService = textService;
        ImageService = imageService;
        PixelScale = pixelScale;
        _diagnostics = diagnostics;
        MeasureCount = 0;
        ArrangeCount = 0;
    }

    /// <summary>所属运行时，用于在布局阶段申请失效（例如图片尺寸变化）。</summary>
    public UiRuntime Runtime { get; }

    /// <summary>文本服务。</summary>
    public ITextService TextService { get; }

    /// <summary>图片服务；无图片能力时为 null。</summary>
    public IImageService? ImageService { get; }

    /// <summary>DPI 缩放。</summary>
    public double PixelScale { get; }

    /// <summary>本次布局中实际执行的 Measure 次数（含探测测量，用于验证增量布局与缓存）。</summary>
    public int MeasureCount { get; internal set; }

    /// <summary>本次布局中实际执行的 Arrange 次数。</summary>
    public int ArrangeCount { get; internal set; }

    /// <summary>
    /// 当前测量提案性质：探测作用域内为 <see cref="MeasureProposalKind.Probe"/>；
    /// 自然尺寸测量期间为 <see cref="MeasureProposalKind.Intrinsic"/>（视图在 Measure 内读取到的也是它）；
    /// 其余为 <see cref="MeasureProposalKind.Effective"/>。
    /// </summary>
    public MeasureProposalKind CurrentProposal
        => _probeScopes.Count > 0
            ? MeasureProposalKind.Probe
            : _intrinsicDepth > 0 ? MeasureProposalKind.Intrinsic : MeasureProposalKind.Effective;

    /// <summary>
    /// 当前测量是否是最终有效布局提案。动态内容（挂载/卸载/内容选择）只能在这里为真时执行：
    /// 探测与自然尺寸测量都不得被当作有效布局。
    /// </summary>
    public bool IsEffectiveProposal => CurrentProposal == MeasureProposalKind.Effective;

    /// <summary>是否处于探测作用域。探测期间不得挂载/卸载节点或选择动态内容。</summary>
    public bool IsProbing => _probeScopes.Count > 0;

    /// <summary>
    /// 是否处于自然尺寸（intrinsic）测量：堆叠容器在此模式下只求和自然尺寸，不执行最终弹性分配与复测，
    /// 避免嵌套容器逐层触发完整分配造成指数级重测。
    /// </summary>
    public bool IsIntrinsicMeasure => _intrinsicDepth > 0;

    /// <summary>进入自然尺寸测量；调用方必须用 finally 保证配对的 <see cref="PopIntrinsicMeasure"/>。</summary>
    public void PushIntrinsicMeasure() => _intrinsicDepth++;

    /// <summary>退出自然尺寸测量。</summary>
    public void PopIntrinsicMeasure()
    {
        if (_intrinsicDepth > 0)
        {
            _intrinsicDepth--;
        }
    }

    /// <summary>
    /// 进入探测作用域：内部所有测量按探测处理，作用域以 finally 语义退出时恢复进入前的测量状态。
    /// </summary>
    public MeasureProbeScope BeginProbe()
    {
        var scope = new MeasureProbeScope(this);
        _probeScopes.Add(scope);
        return scope;
    }

    /// <summary>
    /// 在探测作用域内测量节点并返回结果。结果只在作用域内可见：退出后节点恢复此前的有效提案。
    /// </summary>
    public Size Probe(ViewNode node, LayoutConstraints constraints)
    {
        using var scope = BeginProbe();
        Measure(node, constraints);
        return node.DesiredSize;
    }

    /// <summary>在探测作用域内测量节点并返回其按轴尺寸特征；退出后恢复此前的有效提案。</summary>
    public LayoutFeatures ProbeFeatures(ViewNode node, LayoutConstraints constraints)
    {
        using var scope = BeginProbe();
        Measure(node, constraints);
        return node.GetLayoutFeatures();
    }

    /// <summary>测量子节点（遵循测量缓存）；提案性质与算法模式取当前作用域。</summary>
    public void Measure(ViewNode node, LayoutConstraints constraints)
        => Measure(node, constraints, CurrentProposal, IsIntrinsicMeasure);

    /// <summary>测量子节点，但忽略缓存（用于需要重新测量的场景）。</summary>
    public void MeasureFresh(ViewNode node, LayoutConstraints constraints)
    {
        // 先记录探测快照，再清空缓存，否则探测退出时无法恢复进入前的有效状态。
        CaptureForProbes(node);
        node.MeasureValid = false;
        Measure(node, constraints, CurrentProposal, IsIntrinsicMeasure);
    }

    /// <summary>报告某轴的显式固定/最小尺寸被父级可用空间截断（按节点去重、按轮次限量）。</summary>
    public void ReportOverflow(ViewNode node, LayoutAxis axis, double required, double available)
    {
        _diagnostics?.Report(
            new LayoutDiagnosticKey(node, LayoutDiagnosticKind.Overflow, axis),
            $"布局溢出：节点 {node.DebugPath} 在{Describe(axis)}轴声明至少 {required:0.###}，父级可用 {available:0.###}；" +
            "显式固定/最小尺寸不会被父约束隐式压缩。");
    }

    /// <summary>报告某轴无界导致需要有限空间的操作回退（供后续弹性/换行消费者使用）。</summary>
    public void ReportUnbounded(ViewNode node, LayoutAxis axis, string operation)
    {
        _diagnostics?.Report(
            new LayoutDiagnosticKey(node, LayoutDiagnosticKind.Unbounded, axis),
            $"无界{Describe(axis)}轴：节点 {node.DebugPath} 的 {operation} 需要有限可用空间，已回退自然尺寸。");
    }

    private void Measure(ViewNode node, LayoutConstraints constraints, MeasureProposalKind kind, bool intrinsic)
    {
        var parentEnvironment = node.Parent?.Environment ?? Runtime.Environment;
        var environment = node.View.ResolveEnvironment(node, parentEnvironment);

        if (CanReuseMeasurement(node, constraints, kind, intrinsic))
        {
            // 缓存命中也可能改写环境（父级环境/字体变化）：必须先拍快照再赋值，
            // 否则探测期间解析出的环境会残留在作用域之外。
            if (!Equals(environment, node.Environment))
            {
                CaptureForProbes(node);
                node.Environment = environment;
            }

            return;
        }

        // 快照必须在改写任何测量状态（环境、期望尺寸、排版缓存、弹性分配结果）之前记录。
        CaptureForProbes(node);

        if (!Equals(environment, node.Environment))
        {
            node.Environment = environment;
        }

        MeasureCount++;
        node.View.Measure(node, constraints, this);

        // 只按上限收缩：最小值由各视图自己决定（例如 Frame 的 MinWidth / Stack 填满父级），
        // 否则父级的紧约束会掩盖子项的自然尺寸。
        node.DesiredSize = new Size(
            Math.Clamp(node.DesiredSize.Width, 0, constraints.MaxWidth),
            Math.Clamp(node.DesiredSize.Height, 0, constraints.MaxHeight));
        node.LastConstraints = constraints;
        node.LastProposalKind = kind;
        node.LastMeasureIsIntrinsic = intrinsic;
        node.MeasureValid = true;

        if (kind != MeasureProposalKind.Probe)
        {
            ReportTruncatedDeclarations(node, constraints);
        }
    }

    /// <summary>
    /// 测量缓存复用规则：约束、算法模式（自然尺寸/有效分配）必须一致。
    /// 探测可复用同模式同约束的任意已有结果；实际测量只复用非探测结果，
    /// 因此同约束的自然测量不会掩盖后续必须执行弹性分配的有效测量，探测也不会复用到分配算法产生的缓存。
    /// </summary>
    private static bool CanReuseMeasurement(ViewNode node, LayoutConstraints constraints, MeasureProposalKind kind, bool intrinsic)
    {
        if (!node.MeasureValid || node.LastConstraints != constraints || node.LastMeasureIsIntrinsic != intrinsic)
        {
            return false;
        }

        return kind == MeasureProposalKind.Probe || node.LastProposalKind == kind;
    }

    /// <summary>
    /// 子树中是否存在显式 <c>.Flex()</c>/<c>.FillWidth()</c> 声明；按轮次记忆，避免重复遍历。
    /// 用于把有效布局提案传递到普通容器内部的弹性子树（自然测量本身不做最终分配）。
    /// </summary>
    public bool SubtreeHasFlexDeclaration(ViewNode node)
    {
        if (_flexSubtreeCache.TryGetValue(node, out var cached))
        {
            return cached;
        }

        var found = node.View is FlexView or FillWidthView;
        if (!found)
        {
            foreach (var child in node.ChildList)
            {
                if (SubtreeHasFlexDeclaration(child))
                {
                    found = true;
                    break;
                }
            }
        }

        _flexSubtreeCache[node] = found;
        return found;
    }

    /// <summary>让所有活动探测作用域在节点被改写前记录原状态（同一作用域只记录一次）。</summary>
    private void CaptureForProbes(ViewNode node)
    {
        foreach (var scope in _probeScopes)
        {
            scope.Capture(node);
        }
    }

    private void ReportTruncatedDeclarations(ViewNode node, LayoutConstraints constraints)
    {
        if (_diagnostics is null || (!constraints.HasBoundedWidth && !constraints.HasBoundedHeight))
        {
            return;
        }

        var features = node.GetLayoutFeatures();
        ReportTruncatedAxis(node, LayoutAxis.Horizontal, features.Horizontal, constraints.MaxWidth, constraints.HasBoundedWidth);
        ReportTruncatedAxis(node, LayoutAxis.Vertical, features.Vertical, constraints.MaxHeight, constraints.HasBoundedHeight);
    }

    private void ReportTruncatedAxis(
        ViewNode node,
        LayoutAxis axis,
        AxisLayoutFeatures features,
        double available,
        bool bounded)
    {
        // 自身边界逆序（min > max）：声明互相矛盾，以显式下界为准并诊断，不能让 Math.Clamp 抛异常。
        if (features.EffectiveMin > features.EffectiveMax)
        {
            ReportOverflow(node, axis, features.EffectiveMin, features.EffectiveMax);
            return;
        }

        // 内容硬要求超过盒子可能的最大尺寸：保留上限并诊断“内容仍需 X”。
        if (features.ContentMinSize is { } contentMin && contentMin > features.EffectiveMax + 0.0001)
        {
            ReportOverflow(node, axis, contentMin, features.EffectiveMax);
            return;
        }

        if (!bounded || !double.IsFinite(available))
        {
            return;
        }

        var declared = features.EffectiveMin;
        if (!double.IsFinite(declared) || declared <= available + 0.0001)
        {
            return;
        }

        ReportOverflow(node, axis, declared, available);
    }

    private static string Describe(LayoutAxis axis) => axis == LayoutAxis.Horizontal ? "水平" : "垂直";

    /// <summary>退出探测作用域并恢复被改写的测量状态（容错：非栈序退出时逐层恢复）。</summary>
    internal void EndProbe(MeasureProbeScope scope)
    {
        var index = _probeScopes.LastIndexOf(scope);
        if (index < 0)
        {
            return;
        }

        for (var i = _probeScopes.Count - 1; i >= index; i--)
        {
            _probeScopes[i].Restore();
            _probeScopes.RemoveAt(i);
        }
    }

    /// <summary>排列子节点到指定绝对矩形。</summary>
    public void Arrange(ViewNode node, Rect bounds)
    {
        ArrangeCount++;
        node.ArrangeValid = true;
        node.View.Arrange(node, bounds, this);
    }
}

/// <summary>
/// 探测作用域：内部测量只产生临时结果，退出时恢复进入前的测量状态，保证探测不被当作最终布局提案。
/// </summary>
/// <remarks>必须通过 <c>using</c> 释放（<see cref="IDisposable.Dispose"/> 在异常路径下同样由 finally 执行）。</remarks>
internal sealed class MeasureProbeScope : IDisposable
{
    private readonly LayoutContext _context;
    private readonly Dictionary<ViewNode, MeasureSnapshot> _snapshots = new(ReferenceEqualityComparer.Instance);
    private bool _disposed;

    internal MeasureProbeScope(LayoutContext context) => _context = context;

    /// <summary>本作用域记录的节点数量（有界于作用域内实际测量的节点数）。</summary>
    internal int CapturedNodeCount => _snapshots.Count;

    /// <summary>在首次真正改写节点测量状态之前记录原状态。</summary>
    internal void Capture(ViewNode node)
    {
        if (!_disposed && !_snapshots.ContainsKey(node))
        {
            _snapshots[node] = MeasureSnapshot.Capture(node);
        }
    }

    /// <summary>恢复全部被改写节点的测量状态；可重复调用。</summary>
    internal void Restore()
    {
        foreach (var pair in _snapshots)
        {
            pair.Value.Restore(pair.Key);
        }

        _snapshots.Clear();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _context.EndProbe(this);
    }

    private readonly record struct MeasureSnapshot(
        Size DesiredSize,
        object? ViewCache,
        EnvironmentValues Environment,
        bool MeasureValid,
        LayoutConstraints LastConstraints,
        MeasureProposalKind LastProposalKind,
        bool LastMeasureIsIntrinsic,
        double[]? ChildMainAllocation,
        double AllocatedMainExtent)
    {
        public static MeasureSnapshot Capture(ViewNode node)
            => new(
                node.DesiredSize,
                node.ViewCache,
                node.Environment,
                node.MeasureValid,
                node.LastConstraints,
                node.LastProposalKind,
                node.LastMeasureIsIntrinsic,
                node.ChildMainAllocation,
                node.AllocatedMainExtent);

        public void Restore(ViewNode node)
        {
            node.DesiredSize = DesiredSize;
            node.ViewCache = ViewCache;
            node.Environment = Environment;
            node.MeasureValid = MeasureValid;
            node.LastConstraints = LastConstraints;
            node.LastProposalKind = LastProposalKind;
            node.LastMeasureIsIntrinsic = LastMeasureIsIntrinsic;
            node.ChildMainAllocation = ChildMainAllocation;
            node.AllocatedMainExtent = AllocatedMainExtent;
        }
    }
}

/// <summary>
/// 绘制命令生成上下文：维护当前裁剪区、透明度与应用服务，并按顺序输出命令。
/// </summary>
internal sealed class DrawContext
{
    private readonly DrawCommandList _commands = new();

    public DrawContext(UiRuntime runtime, ITextService textService, ITextureService? textureService, IImageService? imageService, double pixelScale, RenderFrameOptions options)
    {
        Runtime = runtime;
        TextService = textService;
        TextureService = textureService;
        ImageService = imageService;
        PixelScale = pixelScale;
        Options = options;
        CurrentClip = new Rect(0, 0, options.ViewportSize.Width, options.ViewportSize.Height);
    }

    /// <summary>所属运行时。</summary>
    public UiRuntime Runtime { get; }

    /// <summary>文本服务。</summary>
    public ITextService TextService { get; }

    /// <summary>纹理服务；无 GPU 能力时（无窗口测试）为 null，文本与图片会被跳过。</summary>
    public ITextureService? TextureService { get; }

    /// <summary>图片服务。</summary>
    public IImageService? ImageService { get; }

    /// <summary>DPI 缩放。</summary>
    public double PixelScale { get; }

    /// <summary>本帧配置。</summary>
    public RenderFrameOptions Options { get; }

    /// <summary>当前有效裁剪区（逻辑像素，已与祖先裁剪区相交）。</summary>
    public Rect CurrentClip { get; private set; }

    /// <summary>已输出的命令。</summary>
    public DrawCommandList Commands => _commands;

    /// <summary>被裁剪剔除的节点数量（诊断用）。</summary>
    public int CulledNodeCount { get; private set; }

    /// <summary>节点矩形是否与当前裁剪区相交。</summary>
    public bool IsVisible(ViewNode node)
    {
        var bounds = node.View.GetClipBounds(node);
        var visible = bounds.IntersectsWith(CurrentClip);
        if (!visible)
        {
            CulledNodeCount++;
        }

        return visible;
    }

    /// <summary>递归输出子节点命令。</summary>
    public void EmitChild(ViewNode child) => child.View.Emit(child, this);

    /// <summary>按绝对矩形压入裁剪区；返回 false 表示裁剪区为空，调用方应跳过内容。</summary>
    public bool PushClip(Rect bounds)
    {
        var intersection = bounds.Intersect(CurrentClip);
        if (intersection.IsEmpty)
        {
            return false;
        }

        _commands.Add(new PushClipCommand(intersection));
        CurrentClip = intersection;
        return true;
    }

    /// <summary>弹出最近压入的裁剪区。</summary>
    public void PopClip(Rect restoreTo)
    {
        _commands.Add(new PopClipCommand());
        CurrentClip = restoreTo;
    }

    /// <summary>取回本帧命令快照。</summary>
    public IReadOnlyList<DrawCommand> ToArray() => _commands.ToArray();
}

/// <summary>一帧的生成配置。</summary>
/// <param name="ViewportSize">逻辑像素视口尺寸。</param>
/// <param name="PixelScale">DPI 缩放。</param>
/// <param name="ClearColor">清屏色。</param>
public readonly record struct RenderFrameOptions(Size ViewportSize, double PixelScale, Color ClearColor);

/// <summary>输入分发上下文：向视图暴露运行时能力并记录事件是否被消费。</summary>
internal sealed class InputContext
{
    public InputContext(UiRuntime runtime)
    {
        Runtime = runtime;
    }

    /// <summary>所属运行时。</summary>
    public UiRuntime Runtime { get; }

    /// <summary>本次事件是否已被处理。</summary>
    public bool Handled { get; private set; }

    /// <summary>标记事件已处理。</summary>
    public void MarkHandled() => Handled = true;

    /// <summary>请求重绘。</summary>
    public void RequestRedraw() => Runtime.InvalidateRender();
}
