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
    private bool _layoutDirty = true;
    private bool _renderDirty = true;
    private bool _needsDraw = true;
    private bool _disposed;
    private bool _registered;
    private bool _processingWork;
    private bool _buildingFrame;
    private long _workPassId;

    /// <summary>创建运行时并挂载根视图。</summary>
    public UiRuntime(View root, UiRuntimeServices services)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(services);
        Services = services;
        Dispatcher = services.Dispatcher;
        _focus = new FocusManager(this);
        _router = new InputRouter(this, _focus);
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
        var focused = _focus.Focused;
        if (focused is null || !focused.IsMounted)
        {
            return null;
        }

        return focused.View.GetNextWakeup(focused, Clock.Now);
    }

    /// <summary>
    /// 时钟推进后的回调：宿主每轮循环调用。
    /// 当焦点视图声明需要定时重绘（例如 TextField 光标闪烁）时请求重绘；否则不产生额外重绘。
    /// </summary>
    public void OnClockTick()
    {
        if (GetNextWakeup() is not null)
        {
            InvalidateRender();
        }
    }

    /// <summary>分发指针事件。</summary>
    public void DispatchPointer(PointerEvent e) => _router.DispatchPointer(e);

    /// <summary>分发滚轮事件。</summary>
    public void DispatchWheel(WheelEvent e) => _router.DispatchWheel(e);

    /// <summary>分发键盘事件。</summary>
    public void DispatchKey(KeyEvent e) => _router.DispatchKey(e);

    /// <summary>分发已提交文本输入。</summary>
    public void DispatchTextInput(TextInputEvent e) => _router.DispatchTextInput(e);

    /// <summary>按当前布局与层叠顺序做命中测试，返回最上层可见目标（诊断与测试用）。</summary>
    public ViewNode? HitTest(Point position) => _router.HitTest(position);

    /// <summary>设置焦点节点；传入 null 表示清除焦点。</summary>
    public void SetFocus(ViewNode? node) => _focus.SetFocus(node);

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
