using System.Diagnostics;

namespace CSharp.UI;

/// <summary>运行时所需的服务集合；无窗口测试可以使用 <c>CSharp.UI.Testing</c> 中的替身。</summary>
public sealed record UiRuntimeServices
{
    /// <summary>UI 调度器（必需）。</summary>
    public required IUiDispatcher Dispatcher { get; init; }

    /// <summary>文本服务（必需）；无文本能力时使用 <c>StubTextService</c>。</summary>
    public required ITextService TextService { get; init; }

    /// <summary>纹理服务；无 GPU 能力时为 null（文本与图片命令会被跳过）。</summary>
    public ITextureService? TextureService { get; init; }

    /// <summary>图片解码服务；为 null 时 <c>Image</c> 视图不产生绘制命令。</summary>
    public IImageService? ImageService { get; init; }

    /// <summary>剪贴板服务；为 null 时 TextField 的复制/粘贴不可用。</summary>
    public IClipboardService? Clipboard { get; init; }

    /// <summary>时钟；默认使用 <see cref="StopwatchUiClock"/>。</summary>
    public IUiClock Clock { get; init; } = new StopwatchUiClock();

    /// <summary>
    /// 系统滚轮设置服务；默认每格 3 行/3 字符（非 Windows 或查询失败的回退值）。
    /// <see cref="ScrollView"/> 用它把归一化滚轮步数换算为逻辑像素距离。
    /// </summary>
    public IWheelScrollSettingsService WheelScrollSettings { get; init; } = DefaultWheelScrollSettingsService.Instance;

    /// <summary>
    /// 宿主文本输入服务（组合输入、候选锚点）；null 表示当前宿主只支持已提交文本。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 平台消息桥由 <c>CSharp.UI.OpenTK</c> 在 TI-07 提供；无窗口测试与只支持已提交文本的宿主保持 null。
    /// 消费方通过 <see cref="TextInputForwarder.EffectiveService"/> 把 null 归一化为
    /// <see cref="NoTextInputService"/>，因此不需要在每个调用点判空。
    /// </para>
    /// <para>
    /// 该服务不改变既有输入路径：已提交文本仍然走 <see cref="UiRuntime.DispatchTextInput"/>。
    /// </para>
    /// </remarks>
    public ITextInputService? TextInputService { get; init; }

    /// <summary>
    /// 每节点撤销/重做历史容量（TI-05 内部可配置）；默认 100 项 / 1,048,576 UTF-16 码元，0 禁用记录。
    /// </summary>
    /// <remarks>内部可配置只用于测试与宿主策略，不扩张公开编辑器 API。</remarks>
    internal TextHistoryCapacity HistoryCapacity { get; init; } = TextHistoryCapacity.Default;

    /// <summary>
    /// 编辑快捷键平台策略（TI-05 内部）。默认按当前操作系统映射；测试可注入以纯核心验证 mac/Linux 组合，
    /// 但不代表真机验收。原生宿主最终路由属 TI-07。
    /// </summary>
    internal TextEditPlatformKind EditPlatform { get; init; } = TextEditCommandResolver.DefaultPlatform;
}

/// <summary>
/// 声明式 UI 运行时：持有节点树，负责组件重建、布局、绘制命令生成、焦点与输入分发。
/// </summary>
/// <remarks>
/// <para>
/// 宿主（例如 OpenTK 宿主）在 UI 线程循环中调用 <see cref="ProcessPendingWork"/> 与 <see cref="BuildFrame"/>；
/// 无窗口测试直接在测试线程调用同名方法。
/// </para>
/// <para>所有公开成员都只能在 UI 线程调用，唯一例外是调度器自身的 Post / RequestRedraw。</para>
/// </remarks>
public sealed class UiRuntime : IDisposable
{
    private const int MaxRebuildsPerNode = 100;

    /// <summary>通用诊断文本的保留上限（保留最近的记录，避免长时间运行无界增长）。</summary>
    private const int MaxDiagnostics = 512;

    private readonly List<ViewNode> _componentQueue = [];
    private readonly HashSet<ViewNode> _componentSet = [];
    private readonly List<string> _diagnostics = [];
    private readonly LayoutDiagnostics _layoutDiagnostics;
    private readonly ResponsiveTransactionManager _responsiveTransactions = new();
    private readonly FocusManager _focus;
    private readonly InputRouter _router;
    private readonly TextInputServiceSession _textInput;
    private readonly List<TimedWake> _timedWakes = [];
    private bool _timedWakeDispatching;
    private bool _layoutDirty = true;
    private bool _renderDirty = true;
    private bool _needsDraw = true;
    private bool _disposed;
    private bool _registered;
    private bool _processingWork;
    private bool _pendingHoverRefresh;
    private bool _buildingFrame;
    private bool _windowActive = true;
    private int _nextTextInputEpoch;
    private long _workPassId;

    /// <summary>当前正在分发的原生按键身份 token；由 <see cref="DispatchKey(KeyEvent, object?)"/> 设置。</summary>
    private object? _keyOriginToken;

    /// <summary>创建运行时并挂载根视图。</summary>
    public UiRuntime(View root, UiRuntimeServices services)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(services);
        Services = services;
        Dispatcher = services.Dispatcher;
        _focus = new FocusManager(this);
        _router = new InputRouter(this, _focus);
        _textInput = new TextInputServiceSession(this);
        _layoutDiagnostics = new LayoutDiagnostics(ReportDiagnostic);
        Root = Reconciler.MountRoot(this, root);
        Root.Parent = null;
    }

    /// <summary>运行时的服务集合。</summary>
    public UiRuntimeServices Services { get; }

    /// <summary>UI 调度器。</summary>
    public IUiDispatcher Dispatcher { get; }

    /// <summary>时钟。</summary>
    public IUiClock Clock => Services.Clock;

    /// <summary>根节点。根视图为 <c>ForEach</c> 时会包一层隐式容器。</summary>
    public ViewNode Root { get; }

    /// <summary>根环境（主题、默认字体、默认前景色）。修改后需要重新布局。</summary>
    public EnvironmentValues Environment { get; private set; } = new() { Theme = Theme.Light };

    /// <summary>逻辑像素视口尺寸。</summary>
    public Size ViewportSize { get; private set; } = new(800, 600);

    /// <summary>DPI 缩放（逻辑像素 → framebuffer 像素）。</summary>
    public double PixelScale { get; private set; } = 1.0;

    /// <summary>清屏色；默认使用根环境主题的背景色。</summary>
    public Color? ExplicitClearColor { get; set; }

    /// <summary>
    /// 帧末追加绘制命令的内部入口（TI-07 候选 overlay）：内容 Emit 之后、命令快照之前调用。
    /// 核心不依赖候选类型；回调不得再次生成帧、重开纹理帧或抛出异常。
    /// </summary>
    internal Action<DrawContext>? FrameOverlay { get; set; }

    /// <summary>是否需要重绘。宿主据此决定是否交换缓冲。</summary>
    public bool NeedsDraw => _needsDraw;

    /// <summary>当前获得焦点的节点。</summary>
    public ViewNode? FocusedNode => _focus.Focused;

    /// <summary>最近一次布局实际执行的 Measure / Arrange 次数（用于验证增量布局与缓存）。</summary>
    public int LastMeasureCount { get; private set; }

    /// <summary>最近一次布局实际执行的 Arrange 次数。</summary>
    public int LastArrangeCount { get; private set; }

    /// <summary>累计执行的布局轮数；用于验证纯视觉变化不触发重新布局。</summary>
    public int LayoutPassCount { get; private set; }

    /// <summary>组件 Build 累计次数。</summary>
    public int BuildCount { get; private set; }

    /// <summary>框架诊断信息（资源释放失败、组件异常等）；保留最近的 <see cref="MaxDiagnostics"/> 条。</summary>
    public IReadOnlyList<string> Diagnostics => _diagnostics;

    /// <summary>有界、按节点去重的布局诊断收集器（溢出/无界基础，供后续弹性与换行消费者使用）。</summary>
    internal LayoutDiagnostics LayoutDiagnostics => _layoutDiagnostics;

    /// <summary>是否存在待处理的组件重建。</summary>
    public bool HasPendingBuild => _componentQueue.Count > 0;

    /// <summary>当前挂起、尚未在安全阶段消费的响应式内容请求数量（诊断用）。</summary>
    internal int PendingResponsiveRequestCount => _responsiveTransactions.PendingCount;

    /// <summary>累计执行的响应式内容构建次数（诊断用）。</summary>
    internal int ResponsiveBuildCount { get; private set; }

    /// <summary>最近一轮 <see cref="ProcessPendingWork"/> 实际消费的响应式内容构建次数（诊断用）。</summary>
    internal int LastResponsiveTransactionCount { get; private set; }

    /// <summary>设置根环境（主题 / 默认字体 / 默认前景色），并使布局失效。</summary>
    public void SetEnvironment(EnvironmentValues environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        Environment = environment;
        InvalidateLayout(Root);
    }

    /// <summary>更新视口尺寸与 DPI；尺寸变化触发布局失效，DPI 变化同时使文本与纹理缓存失效。</summary>
    public void SetViewport(Size viewportSize, double pixelScale)
    {
        var scaleChanged = Math.Abs(pixelScale - PixelScale) > 0.0001;
        ViewportSize = new Size(Math.Max(0, viewportSize.Width), Math.Max(0, viewportSize.Height));
        PixelScale = pixelScale <= 0 ? 1.0 : pixelScale;
        InvalidateLayout(Root);
        if (scaleChanged)
        {
            Services.TextService.ClearCaches();
            Services.TextureService?.Clear();
        }
    }

    /// <summary>
    /// 布局失效序号（R17 字符几何快照的失效键）：每次 <see cref="InvalidateLayout"/> 单调递增。
    /// 只随失效请求推进、与实际是否完成一次布局无关——失效到重建之间的窗口天然区分新旧快照。
    /// </summary>
    internal int LayoutSerial { get; private set; }

    /// <summary>注册为全局 UI 调度器目标并请求首帧。</summary>
    public void Start()
    {
        if (!_registered)
        {
            UiDispatcher.Register(Dispatcher);
            _registered = true;
        }

        InvalidateLayout(Root);
        Dispatcher.RequestRedraw();
    }

    /// <summary>标记节点、其祖先与整个子树需要重新测量与重绘。</summary>
    public void InvalidateLayout(ViewNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        for (var current = node; current is not null; current = current.Parent)
        {
            current.MeasureValid = false;
            current.ArrangeValid = false;
        }

        InvalidateSubtree(node);
        _layoutDirty = true;
        LayoutSerial++;
        InvalidateRender();
    }

    private static void InvalidateSubtree(ViewNode node)
    {
        // 显式栈：动态内容可能形成很深的链，失效遍历不能依赖递归调用栈。
        var pending = new Stack<ViewNode>();
        pending.Push(node);
        while (pending.Count > 0)
        {
            foreach (var child in pending.Pop().ChildList)
            {
                child.MeasureValid = false;
                child.ArrangeValid = false;
                pending.Push(child);
            }
        }
    }

    /// <summary>标记需要重绘（不需要重新布局）。</summary>
    public void InvalidateRender()
    {
        _renderDirty = true;
        _needsDraw = true;
        if (!_disposed)
        {
            Dispatcher.RequestRedraw();
        }
    }

    /// <summary>把读取过某个状态的组件节点排入重建队列（同一轮内合并去重）。</summary>
    public void InvalidateComponent(ViewNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!node.IsMounted || node.View is not Component)
        {
            return;
        }

        if (_componentSet.Add(node))
        {
            _componentQueue.Add(node);
        }

        InvalidateRender();
    }

    /// <summary>
    /// 把状态登记为“距离 <paramref name="node"/> 最近的组件节点”的长期依赖。
    /// 控件在测量/绘制阶段通过 <see cref="Binding{T}.Read"/> 读取绑定时使用，
    /// 覆盖绑定被保存在字段、构建期未显式读取状态的场景。
    /// </summary>
    /// <remarks>
    /// 登记同时记录在读取它的 <paramref name="node"/> 上：动态内容切换移除该控件时，
    /// 对应订阅随节点卸载释放，不会残留到已经无关的组件或响应式内容上。
    /// </remarks>
    public void RegisterBindingDependency(ViewNode node, StateBase state)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(state);
        for (var current = node; current is not null; current = current.Parent)
        {
            if (current.ComponentState is { } componentState)
            {
                var registrations = node.BindingDependencyRegistrations ??= [];
                if (registrations.Add((componentState, state)))
                {
                    componentState.AddExtraDependency(state);
                }

                return;
            }
        }
    }

    /// <summary>
    /// 节点卸载或描述被替换前，释放它在布局/绘制阶段补登记的绑定依赖。
    /// 控件换用新的绑定后不能继续持有旧来源状态的订阅。
    /// </summary>
    internal void ReleaseBindingDependencies(ViewNode node)
    {
        if (node.BindingDependencyRegistrations is not { Count: > 0 } registrations)
        {
            return;
        }

        foreach (var (owner, state) in registrations)
        {
            owner.RemoveExtraDependency(state);
        }

        registrations.Clear();
    }

    /// <summary>
    /// 登记 <see cref="Responsive"/> 节点的最终有效宽度提案（由测量阶段调用）。
    /// 只记录输入，不执行 builder、不修改节点树，也不会触发绘制。
    /// </summary>
    internal void RequestResponsiveContent(ViewNode node, ResponsiveInfo info)
        => _responsiveTransactions.Register(node, info);

    /// <summary>标记 <see cref="Responsive"/> 描述被替换：同宽的新 builder 描述也需要重建一次内容。</summary>
    internal void MarkResponsiveDescription(ViewNode node)
        => _responsiveTransactions.MarkDescriptionChanged(node);

    /// <summary>
    /// 请求重新构建某个 <see cref="Responsive"/> 节点的内容，即使宽度不变。
    /// 状态依赖（builder 读取的 <see cref="State{T}"/> 变化）与显式失效共用该入口；
    /// 只登记内容请求并使布局失效，不在通知路径中同步执行 builder。
    /// </summary>
    internal void InvalidateResponsiveContent(ViewNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!node.IsMounted || node.View is not Responsive)
        {
            return;
        }

        _responsiveTransactions.InvalidateContent(node);
        InvalidateLayout(node);
    }

    /// <summary>执行一次响应式内容构建并协调结果；只在安全阶段由事务消费者调用。</summary>
    /// <remarks>
    /// builder 有一个独立的、栈式的状态读取作用域：成功返回后把本次读取替换为该节点的依赖集合
    /// （条件读取被移除后不再触发构建），抛出异常时保留上一次成功内容的依赖并恢复此前的 tracker。
    /// </remarks>
    internal void ExecuteResponsiveContent(ViewNode node, Responsive responsive, ResponsiveRequest request)
    {
        ResponsiveBuildCount++;
        var state = ResponsiveContentState.GetOrCreate(node);
        View built;
        using (state.BeginReads())
        {
            try
            {
                built = responsive.Builder(request.Info)
                    ?? throw new InvalidOperationException(
                        $"Responsive 在节点 {node.ShortDebugPath()} 返回了 null 视图；builder 必须返回内容描述。");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                ReportDiagnostic(
                    $"构建响应式内容失败：节点 {node.ShortDebugPath()}（可用宽度 " +
                    $"{(request.Info.IsWidthBounded ? request.Info.AvailableWidth.ToString("0.###") : "无界")}）：{ex.Message}");
                throw;
            }

            state.CommitReads();
        }

        Reconciler.ReconcileDynamicContent(node, built);

        state.HasContent = true;
        state.StableInfo = request.Info;
        state.StableRevision = request.Revision;
    }

    /// <summary>处理待办的组件重建、响应式内容构建与布局：宿主每轮循环调用。</summary>
    /// <remarks>
    /// 顺序按“重建 → 布局 → 消费响应式请求 → 再布局到稳定”循环，绘制只发生在返回之后的 <see cref="BuildFrame"/>。
    /// 组件重建与响应式 builder 都不允许重入本方法（例如在 Build/builder 内调用 <see cref="BuildFrame"/>）。
    /// </remarks>
    public void ProcessPendingWork()
    {
        if (_disposed)
        {
            return;
        }

        if (_processingWork)
        {
            throw new InvalidOperationException(
                "UiRuntime.ProcessPendingWork 不可重入：不能在组件 Build、布局测量或响应式 builder 内再次处理待办工作或生成帧。");
        }

        _processingWork = true;
        try
        {
            ProcessPendingWorkCore();
        }
        catch
        {
            // 异常后清理队列与阶段标志：丢弃未消费的响应式请求与待重建组件，并强制下一次合法更新
            // 重新测量整棵树，使 builder 抛错、描述循环等受控失败之后仍能恢复，不残留半成品队列。
            _componentQueue.Clear();
            _componentSet.Clear();
            _responsiveTransactions.Clear();
            InvalidateLayout(Root);
            throw;
        }
        finally
        {
            _processingWork = false;
        }
    }

    private void ProcessPendingWorkCore()
    {
        // 循环检测按“同一组件在一轮调度内被重复重建的次数”计算，而不是按待更新组件总数：
        // 批量刷新任意数量的组件都是正常场景，只有在 Build() 期间修改状态才会让同一节点反复入队。
        var rebuildCounts = new Dictionary<ViewNode, int>();
        var passId = ++_workPassId;
        var responsiveBudget = ResponsiveTransactionManager.MaxTransactionsPerPass;
        LastResponsiveTransactionCount = 0;

        while (true)
        {
            DrainComponentQueue(rebuildCounts);

            var progressed = false;

            if (_layoutDirty)
            {
                try
                {
                    RunLayout();
                }
                catch (LayoutDepthLimitException) when (_responsiveTransactions.HasPending)
                {
                    // 旧内容过深导致测量受限：先消费显式失效请求（复用最近有效提案）替换过深内容，再重试布局。
                    // 描述更新本身不直接排队，因此不会用过时宽度激活分支；只有显式失效走这条恢复路径。
                    var recovered = _responsiveTransactions.Consume(this, passId, ref responsiveBudget);
                    LastResponsiveTransactionCount += recovered;
                    if (recovered == 0)
                    {
                        throw;
                    }

                    progressed = true;
                    _layoutDirty = true;
                    InvalidateRender();
                    continue;
                }
            }

            if (_responsiveTransactions.HasPending)
            {
                var afterLayout = _responsiveTransactions.Consume(this, passId, ref responsiveBudget);
                LastResponsiveTransactionCount += afterLayout;
                progressed |= afterLayout > 0;
            }

            if (!progressed)
            {
                break;
            }

            // 内容变化后必须重新测量、排列到稳定，之后才允许绘制（不提交半成品布局）。
            _layoutDirty = true;
            InvalidateRender();
        }

        // 环境（Disable 等）在布局阶段解析，因此布局完成后校验焦点与指针交互状态。
        _focus.Validate();
        _router.ValidateInteractionState();

        // 布局期几何边界排队的 hover 刷新在此安全阶段统一 flush：此刻 node.Frame、祖先裁剪
        // 与排版缓存已最终确定，HitTest 不再读半成品布局。
        if (_pendingHoverRefresh)
        {
            _pendingHoverRefresh = false;
            _router.RefreshHover();
        }

        // 文本输入的平台通知只在安全阶段发出（不在 Measure/Arrange/Emit 内调用外部服务）：
        // 外部绑定/来源变化换新 epoch，或把本地已取消的组合通知给平台。
        _textInput.FlushPendingNotifications();

        // 候选锚点几何同样只在最终布局与焦点校验之后发布（TI-06）：此时 node.Frame、滚动偏移、
        // 祖先裁剪与排版缓存都已稳定；发布入口不得触发重排或递归生成帧。
        // 发布前先同步真实文档：纯重绘发现外部 Binding 变化时会取消组合并请求换 epoch，
        // 这里立即在安全阶段消费该请求，保证服务收到的几何与实际这一帧显示同版本（不保留陈旧组合锚点）。
        if (_focus.Focused is { IsMounted: true } focused)
        {
            focused.View.PrepareTextInputGeometry(focused);
            _textInput.FlushPendingNotifications();
            if (_focus.Focused is { IsMounted: true } current)
            {
                current.View.PublishTextInputGeometry(current);
            }
        }

        if (_renderDirty)
        {
            _needsDraw = true;
            _renderDirty = false;
        }
    }

    private void DrainComponentQueue(Dictionary<ViewNode, int> rebuildCounts)
    {
        while (_componentQueue.Count > 0)
        {
            var node = _componentQueue[0];
            _componentQueue.RemoveAt(0);
            _componentSet.Remove(node);

            if (!node.IsMounted || node.View is not Component component)
            {
                continue;
            }

            var rebuilds = rebuildCounts.TryGetValue(node, out var count) ? count + 1 : 1;
            rebuildCounts[node] = rebuilds;
            if (rebuilds > MaxRebuildsPerNode)
            {
                ReportDiagnostic(
                    $"检测到更新循环：组件 {node.DebugPath} 在单轮更新中被重复重建 {rebuilds} 次，已中止以避免无限更新。");
                throw new InvalidOperationException(
                    $"组件 {node.DebugPath} 在单轮更新中被重复重建 {rebuilds} 次（上限 {MaxRebuildsPerNode}），" +
                    "可能存在“构建期间修改状态”的更新循环；请在事件处理中修改状态，而不是在 Build() 内。");
            }

            BuildCount++;
            try
            {
                Reconciler.BuildComponent(node, component);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                ReportDiagnostic($"重建组件 {node.DebugPath} 失败：{ex.Message}");
                throw;
            }
        }
    }

    /// <summary>确保待办工作已处理，并生成一帧绘制命令。</summary>
    public RenderFrame BuildFrame()
    {
        ProcessPendingWork();

        if (_buildingFrame)
        {
            throw new InvalidOperationException(
                "UiRuntime.BuildFrame 不可重入：不能在绘制命令生成（Emit）期间再次生成帧。");
        }

        _buildingFrame = true;
        try
        {
            // 帧边界：纹理服务据此保护本帧引用的纹理，避免生成后续命令时淘汰前面命令仍在引用的纹理。
            Services.TextureService?.BeginFrame();

            var options = new RenderFrameOptions(ViewportSize, PixelScale, ClearColor);
            var context = new DrawContext(this, Services.TextService, Services.TextureService, Services.ImageService, PixelScale, options);
            Root.View.Emit(Root, context);
            // 帧末追加内容（TI-07 候选 overlay）：在内容之后、命令快照之前执行。
            // 核心不认识候选类型；回调必须只追加绘制命令，不得再次生成帧或重开纹理帧。
            FrameOverlay?.Invoke(context);
            _needsDraw = false;
            return new RenderFrame(context.ToArray(), ViewportSize, PixelScale, ClearColor);
        }
        finally
        {
            _buildingFrame = false;
        }
    }

    /// <summary>当前清屏色。</summary>
    public Color ClearColor => ExplicitClearColor ?? Environment.ResolvedTheme.Background;

    /// <summary>
    /// 下一次需要唤醒重绘的相对延迟（光标闪烁等）；返回 null 表示可以无限等待事件。
    /// 宿主用它计算事件等待超时。
    /// </summary>
    public TimeSpan? GetNextWakeup()
    {
        var now = Clock.Now;
        var focused = _focus.Focused;
        var focusedDelay = focused is { IsMounted: true }
            ? focused.View.GetNextWakeup(focused, now)
            : null;
        var timed = NextTimedWakeDelay(now);
        if (focusedDelay is null)
        {
            return timed;
        }
        return timed is null || focusedDelay.Value < timed.Value ? focusedDelay : timed;
    }

    /// <summary>
    /// 时钟推进后的回调：宿主每轮循环调用。
    /// 当焦点视图声明需要定时重绘（例如 TextField 光标闪烁）时请求重绘；否则不产生额外重绘。
    /// </summary>
    public void OnClockTick()
    {
        var now = Clock.Now;

        // 1. 到期 TimedWake 槽位回调（Floating scrollbar 隐藏等）；回调内结构修改经标记/压缩保证安全。
        DispatchDueTimedWakes(now);

        // 2. 焦点视图的安全阶段时钟回调：拖选边缘滚动等有界内部推进在此执行
        //    （不写绑定、不递归布局、不调平台服务）；默认无操作。
        var focused = _focus.Focused;
        if (focused is { IsMounted: true })
        {
            focused.View.OnClockTick(focused, now);
        }

        // 3. 重绘判据只看 focused 原语义：future TimedWake deadline 不产生每 tick 重绘，
        //    到期隐藏由 OnTimedWake 回调自行 InvalidateRender。
        if (focused is { IsMounted: true } && focused.View.GetNextWakeup(focused, now) is not null)
        {
            InvalidateRender();
        }
    }

    // ------------------------------------------------------------------ TimedWakes（节点级定时槽位）

    /// <summary>
    /// 节点级定时唤醒槽位（Floating scrollbar 隐藏 deadline 等）：值类型 entry，
    /// 重挂即原地改字段（零分配）；<see cref="Armed"/> 在回调期作迭代安全标记。
    /// </summary>
    private struct TimedWake
    {
        internal ViewNode Node;
        internal TimeSpan Deadline;
        internal bool Armed;
    }

    /// <summary>
    /// 为节点挂/重挂一个到期回调：同节点覆盖既有 deadline（节点级单槽位）。
    /// 到期时由 <see cref="OnClockTick"/> 调 <see cref="View.OnTimedWake"/> 一次；不承诺毫秒精度。
    /// </summary>
    internal void RequestTimedWake(ViewNode node, TimeSpan deadline)
    {
        for (var i = 0; i < _timedWakes.Count; i++)
        {
            if (ReferenceEquals(_timedWakes[i].Node, node))
            {
                var entry = _timedWakes[i];
                entry.Deadline = deadline;
                entry.Armed = true;
                _timedWakes[i] = entry;
                return;
            }
        }

        _timedWakes.Add(new TimedWake { Node = node, Deadline = deadline, Armed = true });
    }

    /// <summary>
    /// 取消节点的定时槽位：非回调期直接移除；due 批次中只标记 <see cref="TimedWake.Armed"/>=false
    /// （批次结束后统一压缩——回调期删低 index 会让逆序遍历跳项）。
    /// </summary>
    internal void CancelTimedWake(ViewNode node)
    {
        for (var i = _timedWakes.Count - 1; i >= 0; i--)
        {
            if (!ReferenceEquals(_timedWakes[i].Node, node))
            {
                continue;
            }

            if (_timedWakeDispatching)
            {
                var entry = _timedWakes[i];
                entry.Armed = false;
                _timedWakes[i] = entry;
            }
            else
            {
                _timedWakes.RemoveAt(i);
            }

            return;
        }
    }

    /// <summary>距最近 armed 到期槽的剩余延迟；遍历中惰性剔除失效节点（本方法不触发回调，无重入）。</summary>
    private TimeSpan? NextTimedWakeDelay(TimeSpan now)
    {
        TimeSpan? best = null;
        for (var i = 0; i < _timedWakes.Count; i++)
        {
            var entry = _timedWakes[i];
            if (!entry.Armed || !entry.Node.IsMounted || entry.Node.IsEffectivelyDisabled)
            {
                // 回调期（due 批次的 OnTimedWake 重入查询）只标记不删——删除会让外层逆序遍历跳项。
                if (_timedWakeDispatching)
                {
                    entry.Armed = false;
                    _timedWakes[i] = entry;
                }
                else
                {
                    _timedWakes.RemoveAt(i);
                    i--;
                }
                continue;
            }

            var delay = entry.Deadline - now;
            if (delay < TimeSpan.Zero)
            {
                delay = TimeSpan.Zero;
            }

            if (best is null || delay < best.Value)
            {
                best = delay;
            }
        }

        return best;
    }

    /// <summary>到期槽位回调并压缩：回调期取消只标记不删除（逆序遍历安全）；新注册不回看本批次。</summary>
    private void DispatchDueTimedWakes(TimeSpan now)
    {
        _timedWakeDispatching = true;
        try
        {
            var bound = _timedWakes.Count;    // 本批次上限：回调内新注册落尾部不回看
            for (var i = bound - 1; i >= 0; i--)
            {
                var entry = _timedWakes[i];
                if (!entry.Armed || !entry.Node.IsMounted || entry.Node.IsEffectivelyDisabled)
                {
                    // 失效槽：标记待压缩（回调期不删）。
                    if (entry.Armed)
                    {
                        entry.Armed = false;
                        _timedWakes[i] = entry;
                    }
                    continue;
                }

                if (now < entry.Deadline)
                {
                    continue;
                }

                entry.Armed = false;
                _timedWakes[i] = entry;
                entry.Node.View.OnTimedWake(entry.Node, now);
            }
        }
        finally
        {
            _timedWakeDispatching = false;

            // 批次后单次压缩：移除全部未 armed 槽。
            for (var i = _timedWakes.Count - 1; i >= 0; i--)
            {
                if (!_timedWakes[i].Armed)
                {
                    _timedWakes.RemoveAt(i);
                }
            }
        }
    }

    /// <summary>owner 命中结构自发变化（Floating bar 显隐）后按最近指针位置立即重算 hover（owner 自身 overlay 已可用）。</summary>
    internal void RefreshHover() => _router.RefreshHover();

    /// <summary>
    /// 布局期几何边界（track 移位/overflow·disabled 翻转）后排队一次 hover 刷新：
    /// Arrange 期间 node.Frame 未最终确定，直接 HitTest 会读到半成品布局——
    /// 本方法只标记，由 <see cref="ProcessPendingWork"/> 在全部布局+交互校验后的安全阶段统一 flush 一次。
    /// </summary>
    internal void QueueHoverRefresh() => _pendingHoverRefresh = true;


    /// <summary>分发指针事件。</summary>
    public void DispatchPointer(PointerEvent e) => _router.DispatchPointer(e);

    /// <summary>分发滚轮事件。</summary>
    public void DispatchWheel(WheelEvent e) => _router.DispatchWheel(e);

    /// <summary>分发键盘事件。</summary>
    public void DispatchKey(KeyEvent e) => DispatchKey(e, nativeKeyOriginToken: null);

    /// <summary>
    /// 分发键盘事件，并把<b>同一次原生按键</b>的显式身份 token 一起传入（TI-07 原生桥接缝）。
    /// </summary>
    /// <param name="e">按键事件。</param>
    /// <param name="nativeKeyOriginToken">
    /// 该次按键出现的身份 token；必须与 <see cref="NotifyCompositionKeyOrigin"/> 记录的是同一个实例。
    /// 传 null 时回退到控件自己的稳定 token，保证没有原生信息的既有分发路径完全兼容。
    /// </param>
    /// <remarks>
    /// 组合在同一原生按键内结束、随后仍进入 <c>KeyDown</c> 时，归属判定必须比较同一个显式 token，
    /// 而不是控件内部常量；本重载让宿主不必取得任何内部对象即可正确声明与消费该按键。
    /// </remarks>
    public void DispatchKey(KeyEvent e, object? nativeKeyOriginToken)
        => _ = DispatchKeyHandled(e, nativeKeyOriginToken);

    /// <summary>
    /// 宿主接缝（TI-07，internal）：分发键盘事件并返回焦点控件/焦点遍历是否已消费。
    /// </summary>
    /// <remarks>
    /// 公开 <see cref="DispatchKey(KeyEvent)"/> 与 <see cref="DispatchKey(KeyEvent, object?)"/> 保持 void 签名不变；
    /// OpenTK 宿主经 InternalsVisibleTo 调用本入口，把消费结果回传输入适配器（IME/控件已消费的按键
    /// 不触发宿主级快捷键）。
    /// </remarks>
    internal bool DispatchKeyHandled(KeyEvent e, object? nativeKeyOriginToken)
    {
        _keyOriginToken = nativeKeyOriginToken;
        try
        {
            return _router.DispatchKey(e);
        }
        finally
        {
            _keyOriginToken = null;
        }
    }

    /// <summary>当前正在分发的原生按键 token；没有显式 token 时为 null。</summary>
    internal object? KeyOriginToken => _keyOriginToken;

    /// <summary>编辑快捷键平台策略（宿主最终路由属 TI-07；核心只解析命令）。</summary>
    internal TextEditPlatformKind EditPlatform => Services.EditPlatform;

    /// <summary>分发已提交文本输入。</summary>
    public void DispatchTextInput(TextInputEvent e) => _router.DispatchTextInput(e);

    /// <summary>按当前布局与层叠顺序做命中测试，返回最上层可见目标（诊断与测试用）。</summary>
    public ViewNode? HitTest(Point position) => _router.HitTest(position);

    /// <summary>设置焦点节点；传入 null 表示清除焦点。</summary>
    public void SetFocus(ViewNode? node) => _focus.SetFocus(node);

    /// <summary>
    /// 通知运行时窗口激活状态变化（宿主窗口失活/重新激活时调用）。
    /// </summary>
    /// <param name="active">窗口是否处于激活状态。</param>
    /// <remarks>
    /// <para>
    /// 失活时：先在本地取消焦点控件未提交的组合，再撤销文本输入会话归属并通知服务（先失效、后通知，避免服务的
    /// 同步回调串写到新客户端）。重新激活时，若焦点仍在可编辑控件上，用<b>新的 epoch</b> 恢复输入会话
    /// （不会恢复任何旧组合）。
    /// </para>
    /// <para>
    /// 这是给宿主窗口循环的最小接缝（TI-07 使用）；核心不实现窗口循环，也不引入任何平台类型。
    /// </para>
    /// </remarks>
    public void NotifyWindowActivation(bool active)
    {
        if (_windowActive == active)
        {
            return;
        }

        _windowActive = active;
        if (!active)
        {
            _focus.CancelTextInput();

            // 窗口失活是分组边界：打断合并，但不清空历史（重新激活后仍可撤销）。
            if (_focus.Focused?.InteractionState is ITextEditSessionOwner state)
            {
                state.History.BreakMerge();
            }
        }
        else
        {
            _focus.ResumeTextInput();
        }

        InvalidateRender();
    }

    /// <summary>窗口当前是否处于激活状态；无窗口测试默认为 true。</summary>
    public bool IsWindowActive => _windowActive;

    /// <summary>
    /// 声明一次原生按键的归属（TI-07 原生桥接缝）：把显式 token 交给当前焦点文本控件，
    /// 覆盖“组合在同一原生按键内结束、随后仍进入 KeyDown”的归属判定。
    /// </summary>
    /// <returns>控件是否应放弃该按键（不再执行编辑命令）。没有焦点文本控件时返回 false。</returns>
    public bool NotifyCompositionKeyOrigin(object nativeKeyToken, bool consumedByInputMethod)
    {
        ArgumentNullException.ThrowIfNull(nativeKeyToken);
        if (_focus.Focused is not { } focused || focused.InteractionState is not ITextEditSessionOwner state)
        {
            return false;
        }

        return state.TrackOriginatingKey(nativeKeyToken, consumedByInputMethod);
    }

    /// <summary>
    /// 分配一个新的焦点文本输入 epoch 并创建租约；同一运行时内 epoch 单调递增，不同节点不会复用 epoch 0。
    /// </summary>
    /// <remarks>
    /// 应用不调用本方法；TextField 通过 <c>TextFieldState.InputSession</c> 建立会话。运行时只保留当前归属，
    /// 不持有租约列表，因此焦点切换不会无界增长。
    /// </remarks>
    internal TextInputFocusLease AcquireTextInputLease()
        => new(this, ++_nextTextInputEpoch);

    /// <summary>该运行时的文本输入会话接线（TextField 焦点/生命周期使用）。</summary>
    internal TextInputServiceSession TextInputSession => _textInput;

    /// <summary>撤销当前文本输入归属并结束会话（运行时释放时调用，先失效、后通知）。</summary>
    internal void ReleaseTextInputLeases() => _textInput.End();

    /// <summary>
    /// 撤销一个节点已建立的文本输入归属：只有该节点仍是当前所有者时才结束会话（先失效、后通知服务）。
    /// </summary>
    /// <remarks>
    /// 焦点切换与节点卸载都会调用；节点从未建立会话或会话已属于其他节点时是无操作——
    /// 旧节点卸载不会结束新字段的会话。
    /// </remarks>
    internal void ReleaseTextInputOwnershipFor(ViewNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node.InteractionState is not ITextEditSessionOwner state)
        {
            return;
        }

        state.CancelCompositionForRelease();
        state.EndInputSession();
    }

    /// <summary>按 Tab 顺序移动焦点。<paramref name="backward"/> 为 true 时反向。</summary>
    public void MoveFocus(bool backward = false) => _focus.Move(backward);

    /// <summary>返回当前可参与 Tab 遍历的焦点节点（深度优先顺序）。</summary>
    public IReadOnlyList<ViewNode> GetFocusOrder() => _focus.BuildOrder();

    /// <summary>指针当前悬停的节点。</summary>
    public ViewNode? HoveredNode => _router.Hovered;

    /// <summary>记录诊断信息；超过上限时丢弃最早记录。</summary>
    public void ReportDiagnostic(string message)
    {
        _diagnostics.Add(message);
        if (_diagnostics.Count > MaxDiagnostics)
        {
            _diagnostics.RemoveRange(0, _diagnostics.Count - MaxDiagnostics);
        }

        Debug.WriteLine($"[CSharp.UI] {message}");
    }

    /// <summary>清空诊断信息（含布局诊断去重记录）。</summary>
    public void ClearDiagnostics()
    {
        _diagnostics.Clear();
        _layoutDiagnostics.Clear();
    }

    /// <summary>节点卸载前由协调器调用，清理焦点、交互状态与未消费的响应式请求。</summary>
    internal void OnNodeUnmounting(ViewNode node)
    {
        _focus.OnNodeUnmounting(node);
        _router.OnNodeUnmounting(node);
        CancelTimedWake(node);
        _responsiveTransactions.Remove(node);
        ReleaseBindingDependencies(node);
        node.ResponsiveContent?.ReleaseDependencies();
        node.ResponsiveContent = null;
    }

    private void RunLayout()
    {
        LastMeasureCount = 0;
        LastArrangeCount = 0;
        _layoutDiagnostics.BeginPass();
        var context = new LayoutContext(this, Services.TextService, Services.ImageService, PixelScale, _layoutDiagnostics);
        context.Measure(Root, LayoutConstraints.Tight(ViewportSize));
        context.Arrange(Root, new Rect(0, 0, ViewportSize.Width, ViewportSize.Height));
        LastMeasureCount = context.MeasureCount;
        LastArrangeCount = context.ArrangeCount;
        LayoutPassCount++;
        _layoutDirty = false;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // 先撤销文本输入归属（旧 epoch/旧客户端立即失效），再卸载树并通知服务，避免服务回调串写到已移除节点。
        ReleaseTextInputLeases();
        Reconciler.Unmount(Root);
        _componentQueue.Clear();
        _componentSet.Clear();
        if (_registered)
        {
            UiDispatcher.Unregister(Dispatcher);
            _registered = false;
        }

        Services.TextureService?.Clear();
        Services.ImageService?.Clear();
    }
}
