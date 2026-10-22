using System.Diagnostics;
using global::OpenTK.Windowing.Common;
using global::OpenTK.Windowing.GraphicsLibraryFramework;

namespace CSharp.UI.OpenTK;

/// <summary>
/// 把 OpenTK 窗口事件转换为框架输入事件并分发到 <see cref="UiRuntime"/>。
/// </summary>
/// <remarks>
/// <para>
/// GLFW 光标坐标是窗口内容区坐标（Windows/X11 为物理像素，macOS 为点），框架内部一律使用逻辑像素。
/// 宿主通过 <see cref="UpdatePointerTransform"/> 提供换算系数 R/D（framebuffer/窗口比 ÷ 内容缩放），
/// 因此 Windows 150% 下鼠标坐标除以 1.5，macOS Retina 下保持原值。
/// </para>
/// <para>
/// 适配器只缓存原始窗口坐标，逻辑坐标在每次使用时换算：系统缩放变化后，先前缓存的位置会按新比例重新解释，
/// 滚轮等不携带坐标的事件也不会拿到过期坐标。
/// </para>
/// <para>
/// 修饰键在每个键盘事件上刷新；鼠标事件沿用最近一次键盘事件的修饰键状态（GLFW 的鼠标移动事件不携带修饰键）。
/// 窗口失焦时派发 <see cref="PointerEventKind.Cancel"/> 清理指针捕获与按下状态。
/// </para>
/// </remarks>
internal sealed class OpenTkInputAdapter
{
    private const int DoubleClickMilliseconds = 400;
    private const double DoubleClickDistance = 4;

    private readonly UiRuntime _runtime;
    private readonly Action _requestExit;
    private readonly bool _exitOnEscape;
    private readonly bool _exitOnCloseShortcut;
    private readonly IWindowsInputBridge? _ime;

    /// <summary>
    /// 无路由器时的本地有序闸门（非 Windows / 未接线）。
    /// </summary>
    /// <remarks>
    /// 平台没有 TSF/COM 原生事实与锁，安全阶段恒为真；但**交付引擎只有一套**：
    /// 这里仍用同一个 <see cref="WindowsTsf.HostInputCoordinator"/> 与同一预算/守卫，
    /// 因此 setter 重入在无路由器时同样只能入队，不会退化成另一套直接派发语义。
    /// </remarks>
    private readonly WindowsTsf.HostInputCoordinator _fallbackCoordinator = new();

    private KeyModifiers _modifiers;
    private PointerButton _pressedButton;
    private PointerButton _lastDownButton;
    private Point _pointerWindowPosition;
    private double _pointerScaleX = 1.0;
    private double _pointerScaleY = 1.0;
    private Point _lastDownPosition;
    private long _lastDownTimestamp;
    private int _clickCount = 1;

    /// <summary>创建输入适配器。</summary>
    /// <param name="runtime">目标运行时。</param>
    /// <param name="requestExit">请求退出事件循环。</param>
    /// <param name="exitOnEscape">按 Esc 是否退出。</param>
    /// <param name="exitOnCloseShortcut">按 Ctrl+Q / Cmd+Q 是否退出。</param>
    /// <param name="ime">Windows 输入桥（非 Windows 或未安装子类化时为 null）；提供当前按键的 IME 归属 token。</param>
    internal OpenTkInputAdapter(
        UiRuntime runtime,
        Action requestExit,
        bool exitOnEscape = true,
        bool exitOnCloseShortcut = true,
        IWindowsInputBridge? ime = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(requestExit);
        _runtime = runtime;
        _requestExit = requestExit;
        _exitOnEscape = exitOnEscape;
        _exitOnCloseShortcut = exitOnCloseShortcut;
        _ime = ime;
    }

    /// <summary>最近一次分发到运行时的指针事件。</summary>
    internal PointerEvent? LastPointerEvent { get; private set; }

    /// <summary>最近一次分发到运行时的滚轮事件。</summary>
    internal WheelEvent? LastWheelEvent { get; private set; }

    /// <summary>最近一次分发到运行时的键盘事件。</summary>
    internal KeyEvent? LastKeyEvent { get; private set; }

    /// <summary>最近一次分发到运行时的文本输入事件。</summary>
    internal TextInputEvent? LastTextEvent { get; private set; }

    /// <summary>累计指针事件数量。</summary>
    internal int PointerEventCount { get; private set; }

    /// <summary>累计滚轮事件数量。</summary>
    internal int WheelEventCount { get; private set; }

    /// <summary>累计键盘事件数量。</summary>
    internal int KeyEventCount { get; private set; }

    /// <summary>累计文本输入事件数量。</summary>
    internal int TextInputEventCount { get; private set; }

    /// <summary>累计退出请求次数（Esc / Ctrl+Q）。</summary>
    internal int ExitRequestCount { get; private set; }

    /// <summary>当前修饰键状态。</summary>
    internal KeyModifiers Modifiers => _modifiers;

    /// <summary>最近一次鼠标移动的原始窗口内容区坐标（未换算）。</summary>
    internal Point PointerWindowPosition => _pointerWindowPosition;

    /// <summary>最近一次鼠标位置换算得到的逻辑坐标。</summary>
    internal Point PointerPosition => LogicalPointerPosition;

    /// <summary>当前指针换算系数（窗口坐标 → 逻辑坐标）。</summary>
    internal (double X, double Y) PointerScale => (_pointerScaleX, _pointerScaleY);

    /// <summary>
    /// 更新指针换算系数（窗口坐标 × 系数 = 逻辑坐标，即 R/D）。
    /// </summary>
    /// <remarks>非有限或非正的系数视为 1（等价于窗口坐标即逻辑坐标），不把无效值传入布局与命中测试。</remarks>
    internal void UpdatePointerTransform(double scaleX, double scaleY)
    {
        _pointerScaleX = NormalizeScale(scaleX);
        _pointerScaleY = NormalizeScale(scaleY);
    }

    /// <summary>处理鼠标移动。</summary>
    internal void HandleMouseMove(MouseMoveEventArgs e)
    {
        _pointerWindowPosition = new Point(e.X, e.Y);
        var logical = LogicalPointerPosition;
        DispatchPointer(new PointerEvent(PointerEventKind.Move, logical, _pressedButton, _modifiers, _clickCount));
    }

    /// <summary>处理鼠标按下。</summary>
    internal void HandleMouseDown(MouseButtonEventArgs e)
    {
        var button = OpenTkInputMapping.ToPointerButton(e.Button);
        var logical = LogicalPointerPosition;
        var now = Stopwatch.GetTimestamp();
        var isDoubleClick = button != PointerButton.None
            && button == _lastDownButton
            && (now - _lastDownTimestamp) <= DoubleClickMilliseconds * (Stopwatch.Frequency / 1000)
            && logical.DistanceTo(_lastDownPosition) <= DoubleClickDistance;

        _clickCount = isDoubleClick ? Math.Min(_clickCount + 1, 3) : 1;
        _lastDownButton = button;
        _lastDownTimestamp = now;
        _lastDownPosition = logical;
        _pressedButton = button;
        DispatchPointer(new PointerEvent(PointerEventKind.Down, logical, button, _modifiers, _clickCount));
    }

    /// <summary>处理鼠标释放。</summary>
    internal void HandleMouseUp(MouseButtonEventArgs e)
    {
        var button = OpenTkInputMapping.ToPointerButton(e.Button);
        DispatchPointer(new PointerEvent(PointerEventKind.Up, LogicalPointerPosition, button, _modifiers, _clickCount));
        if (button == _pressedButton)
        {
            _pressedButton = PointerButton.None;
        }
    }

    /// <summary>处理指针离开窗口。</summary>
    internal void HandleMouseLeave()
    {
        DispatchPointer(new PointerEvent(PointerEventKind.Cancel, LogicalPointerPosition, PointerButton.None, _modifiers));
    }

    /// <summary>处理窗口焦点变化；失去焦点时取消指针捕获与按下状态，并取消未提交的文本组合。</summary>
    internal void HandleFocusChanged(bool focused)
    {
        // 失活清理所需的坐标/修饰键在捕获时冻结；立即与延后共用同一交付实现。
        Submit(WindowsTsf.DeferredInputAction.ForFocus(
            ++_orderedSequence, focused, LogicalPointerPosition, _modifiers));
    }

    /// <summary>处理滚轮；位置使用最近一次鼠标移动的位置换算后的逻辑坐标（GLFW 滚轮回调不提供坐标）。</summary>
    internal void HandleWheel(MouseWheelEventArgs e)
    {
        // 滚轮增量是“行/格”语义，不随 DPI 缩放；坐标/修饰键在捕获时冻结。
        var captured = new WindowsTsf.DeferredWheelInput(
            LogicalPointerPosition, e.OffsetX, e.OffsetY, _modifiers);

        Submit(WindowsTsf.DeferredInputAction.ForWheel(++_orderedSequence, captured));
    }

    /// <summary>处理按键按下。</summary>
    internal void HandleKeyDown(KeyboardKeyEventArgs e)
    {
        _modifiers = OpenTkInputMapping.ToKeyModifiers(e.Modifiers);
        var key = OpenTkInputMapping.ToUiKey(e.Key);

        // 归属 token 与消费标志在**捕获时**冻结（重放不得再读可变当前值）。
        var keyToken = _ime?.CurrentKeyToken;
        var consumedByIme = _ime?.CurrentKeyConsumedByIme == true;

        var captured = new WindowsTsf.DeferredKeyInput(
            KeyEventKind.Down, key, _modifiers, e.IsRepeat, keyToken, consumedByIme);

        // 唯一提交入口（退出语义在 DeliverCaptured 内统一处理）。
        Submit(WindowsTsf.DeferredInputAction.ForKey(++_orderedSequence, captured));
    }

    /// <summary>处理按键释放。</summary>
    internal void HandleKeyUp(KeyboardKeyEventArgs e)
    {
        _modifiers = OpenTkInputMapping.ToKeyModifiers(e.Modifiers);

        // 捕获时冻结 token/消费标志。
        var keyToken = _ime?.CurrentKeyToken;
        var consumedByIme = _ime?.CurrentKeyConsumedByIme == true;

        var captured = new WindowsTsf.DeferredKeyInput(
            KeyEventKind.Up, OpenTkInputMapping.ToUiKey(e.Key), _modifiers, e.IsRepeat, keyToken, consumedByIme);

        Submit(WindowsTsf.DeferredInputAction.ForKey(++_orderedSequence, captured));
    }

    /// <summary>处理已提交的字符输入；控制字符（Enter/Tab/Backspace 等）由键盘事件处理，这里忽略。</summary>
    internal void HandleTextInput(TextInputEventArgs e)
    {
        var text = e.AsString;
        if (string.IsNullOrEmpty(text) || (text.Length == 1 && char.IsControl(text[0])))
        {
            return;
        }

        // 唯一提交入口：立即与延后共用同一交付实现（含重入保护）。
        Submit(WindowsTsf.DeferredInputAction.ForText(++_orderedSequence, text));
    }

    /// <summary>
    /// <b>唯一</b>输入提交入口（TI-07 P3a R10）：立即与延后路径共用同一交付引擎与同一交付期守卫。
    /// </summary>
    /// <param name="action">本条输入的完整不可变捕获。</param>
    /// <remarks>
    /// <para>
    /// <b>真正的重入保护：</b>只要 <see cref="_delivering"/> 为 true（正在交付/重放）或当前不在安全阶段，
    /// 本条输入<b>只入队</b>，绝不递归进入 <see cref="DeliverCaptured"/>，也绝不在外层 setter 内改写核心。
    /// 立即路径同样在 <see cref="DeliverTurn"/> 的守卫内交付，因此 R10 独立复现的
    /// “立即交付期间 setter 重入可嵌套写入”不可能再发生。
    /// </para>
    /// <para>
    /// <b>共享语义：</b>立即路径不再自己 dispatch，而是与延后路径一样经
    /// <see cref="DeliverCaptured"/>，因此 Esc/Ctrl+Q 退出与失活清理完全一致。
    /// </para>
    /// <para>
    /// <b>顺序与有界：</b>不安全或正在交付 → FIFO 入队；安全时若已有更早排队项，本条<b>追加到队尾</b>
    /// 后与旧项统一有界 drain（按到达序号交付，旧项 setter 追加的新项也不会越过本条），
    /// 队列真正清空时才直接交付本条；整轮交付受 <see cref="MaxOrderedDeliveriesPerTurn"/> 约束，
    /// 预算耗尽即把剩余项按 FIFO 留给正常 loop（<see cref="RequestWake"/> 单次唤醒），不在本条调用栈内自旋。
    /// </para>
    /// </remarks>
    private void Submit(WindowsTsf.DeferredInputAction action)
    {
        var coordinator = ActiveCoordinator;

        // 1) 正在交付/重放：只入队，绝不递归。
        if (_delivering)
        {
            Enqueue(coordinator, action);
            return;
        }

        // 2) 不在安全阶段（锁/通知/重入/宿主工作中）：入队并请求一次唤醒。
        if (!TryEnterSafePhase())
        {
            Enqueue(coordinator, action);
            RequestOneWake();
            return;
        }

        // 3) 安全：整轮（更早排队项 → 本条 → 追加项）在同一守卫与同一预算内交付。
        DeliverTurn(coordinator, action, phaseVerified: true);
    }

    /// <summary>
    /// 单轮交付预算（TI-07 P3a R10）：一次 <see cref="Submit"/> 或一次
    /// <see cref="DeliverDeferredAtSafePhase"/> 最多交付的输入条数。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么必须有预算：</b>真实 setter 每写一次就追加一条新输入时，队列长度可以一直不超过容量，
    /// 但“排空”循环永不为空——上一版会无限占用 UI 线程（R10 已实测定向测试进程挂起、CPU 累计数百秒）。
    /// 预算把<b>单轮工作量</b>变成与来源增长率无关的常数。
    /// </para>
    /// <para>
    /// <b>预算覆盖范围：</b>覆盖“<see cref="Submit"/> 之前的更早排队项 + 当前 action +
    /// 交付期间追加项”三段的<b>总和</b>；新输入不能靠分段获得额外份额而打破公平性。
    /// </para>
    /// <para>
    /// <b>预算耗尽不等于丢弃：</b>剩余项按 FIFO 保留，并通过现有
    /// <see cref="RequestWake"/> 让出给正常 loop 再安排一次。
    /// </para>
    /// </remarks>
    internal const int MaxOrderedDeliveriesPerTurn = 64;

    /// <summary>当前交付闸门的协调器（路由器或本地闸门；两者是同一类型、同一语义）。</summary>
    private WindowsTsf.HostInputCoordinator ActiveCoordinator
        => (_ime as WindowsTsf.WindowsTextInputRouter)?.Coordinator ?? _fallbackCoordinator;

    /// <summary>
    /// 尝试进入安全阶段：有路由器时按真实 store 状态判定；无路由器时没有原生事实，恒为安全。
    /// </summary>
    private bool TryEnterSafePhase()
        => _ime is WindowsTsf.WindowsTextInputRouter router ? router.DrainSafePhase() : true;

    /// <summary>
    /// 单轮有序交付（TI-07 P3a R10）：更早排队项 → 当前 action → 交付期间追加项，
    /// 全程持有 <see cref="_delivering"/> 并共用同一预算。
    /// </summary>
    /// <param name="coordinator">当前闸门的协调器。</param>
    /// <param name="immediate">触发本轮的当前输入；宿主主动交付时为 null。</param>
    /// <param name="phaseVerified">
    /// 调用方是否已在<b>本调用栈内</b>确认过安全阶段（<see cref="Submit"/> 的预检查），
    /// 且此后还没有发生任何交付。为 true 且队列为空时无需对当前 action 重复探测。
    /// </param>
    /// <returns>本轮实际交付条数。</returns>
    /// <remarks>
    /// <para>
    /// <b>立即与延后同守卫：</b>立即路径的 <see cref="DeliverCaptured"/> 现在也在这一个守卫内执行，
    /// setter 重入的新输入只能入队，不会再嵌套写入。
    /// </para>
    /// <para>
    /// <b>FIFO 公平（到达序号，而不是“先前队列/当前输入”两类）：</b>若进入本轮时已有更早排队项，
    /// 当前 action 先<b>追加到队尾</b>再统一有界 drain——否则旧项交付期间由 setter 追加的新项
    /// （到达序号晚于当前 action）会先于当前 action 交付，破坏真实到达顺序。
    /// </para>
    /// <para>
    /// <b>不越过被暂停的更早项：</b>当前 action 只有在<b>队列真正清空</b>（没有因不安全被放回队首的
    /// 更早项）且仍有预算、仍安全时才直接交付；否则只能按 FIFO 入队等下一轮。
    /// </para>
    /// <para>
    /// <b>不递归、不忙等、不在锁内应用：</b>每次真实交付前都确认安全阶段（<b>不是</b>在 COM 锁/通知内
    /// 应用）；队列项逐条重新确认，当前 action 只在同一调用栈的预检查仍然有效时复用该结果。
    /// 预算耗尽且仍有排队项时只请求<b>一次</b> <see cref="RequestWake"/>，由正常 loop 的下一轮继续。
    /// </para>
    /// </remarks>
    private int DeliverTurn(
        WindowsTsf.HostInputCoordinator coordinator,
        WindowsTsf.DeferredInputAction? immediate,
        bool phaseVerified = false)
    {
        if (_delivering)
        {
            // 已在外层交付中（防御性；正常路径由 Submit 拦截）：只入队，绝不嵌套。
            if (immediate is not null)
            {
                Enqueue(coordinator, immediate);
            }

            return 0;
        }

        // 已有更早排队项：当前 action 的到达序号晚于它们，必须先排到队尾，
        // 再让**同一个**有界 drain 按到达顺序统一交付（含旧项 setter 追加的新项）。
        if (immediate is not null && coordinator.DeferredCount > 0)
        {
            Enqueue(coordinator, immediate);
            immediate = null;
        }

        var budget = MaxOrderedDeliveriesPerTurn;
        var delivered = 0;
        _delivering = true;
        try
        {
            // 1) 更早的排队项优先（本条输入不得越过）。
            var drained = DrainWithinBudget(coordinator, ref budget);
            delivered += drained;

            // 2) 当前 action：只有队列真正清空（没有被暂停放回的更早项）、仍有预算且仍安全时才立即交付。
            if (immediate is not null)
            {
                if (budget <= 0 || coordinator.DeferredCount != 0)
                {
                    // 队列仍有更早项（不安全暂停或预算耗尽）：按 FIFO 入队，绝不绕过。
                    Enqueue(coordinator, immediate);
                }
                else
                {
                    // 预检查已确认安全且本轮尚未交付任何项 → 安全状态未被改写，不必重复探测
                    // （重复探测会对真实安全边界产生多余通知）；否则交付前重新确认。
                    var needRecheck = drained > 0 || !phaseVerified;
                    if (!needRecheck || TryEnterSafePhase())
                    {
                        DeliverCaptured(immediate);
                        budget--;
                        delivered++;
                    }
                    else
                    {
                        Enqueue(coordinator, immediate);
                    }
                }
            }

            // 3) 交付期间由 setter 追加的项：共用同一轮剩余预算，保持 FIFO。
            if (budget > 0)
            {
                delivered += DrainWithinBudget(coordinator, ref budget);
            }
        }
        finally
        {
            _delivering = false;
        }

        if (delivered > 0)
        {
            _runtime.InvalidateRender();
        }

        // 预算耗尽且仍有排队项：让出给正常 loop 再安排一次（单次信号，不是周期轮询）。
        if (budget == 0 && coordinator.DeferredCount > 0)
        {
            RequestOneWake();
        }

        return delivered;
    }

    /// <summary>
    /// 在剩余预算内按 FIFO 交付排队项；交付前重新确认安全阶段，不安全则放回队首并停止（保持顺序）。
    /// </summary>
    /// <param name="coordinator">当前闸门的协调器。</param>
    /// <param name="budget">剩余预算（按引用递减）。</param>
    /// <returns>本次实际交付条数。</returns>
    private int DrainWithinBudget(WindowsTsf.HostInputCoordinator coordinator, ref int budget)
    {
        var delivered = 0;
        while (budget > 0 && coordinator.TryDequeue(out var pending) && pending is not null)
        {
            if (!TryEnterSafePhase())
            {
                // 不安全：放回队首并停止，等真实安全边界再继续（不越过、不丢弃）。
                coordinator.RequeueFront(pending);
                break;
            }

            DeliverCaptured(pending);
            budget--;
            delivered++;
            OrderedReplayedCount++;
        }

        return delivered;
    }

    /// <summary>按 FIFO 入队；溢出显式失败（不静默丢弃）。</summary>
    private void Enqueue(WindowsTsf.HostInputCoordinator coordinator, WindowsTsf.DeferredInputAction action)
    {
        var wasEmpty = coordinator.DeferredCount == 0;
        if (!coordinator.Defer(action))
        {
            OrderedOverflowCount++;
            _runtime.ReportDiagnostic($"有序输入队列溢出（{action}）：本条输入未交付。");
            return;
        }

        OrderedDeferredCount++;

        // 队列由空变非空 = 一次真实转移：请求**一次**唤醒（不是周期性轮询）。
        if (wasEmpty)
        {
            DeferredInputQueued?.Invoke();
        }

        _runtime.InvalidateRender();
    }

    /// <summary>
    /// <b>宿主安全阶段主动交付入口</b>（TI-07 P3a R10）。
    /// </summary>
    /// <returns>本次实际交付的条数。</returns>
    /// <remarks>
    /// <para>
    /// <b>为什么需要：</b>安全恢复后若没有新用户输入，已排队字符不能永久滞留。
    /// </para>
    /// <para>
    /// <b>由谁调用：</b>宿主事件循环在每轮 pump 之后、且不在重建/布局/帧期间调用一次。
    /// 因此不递归 pump、不忙等、不依赖用户再次输入。
    /// </para>
    /// <para>
    /// <b>单轮预算：</b>本次最多交付 <see cref="MaxOrderedDeliveriesPerTurn"/> 条；若预算耗尽仍有
    /// FIFO 剩余项，通过 <see cref="RequestWake"/> 再安排一次，由正常 loop 的下一轮继续，不自旋。
    /// </para>
    /// <para>
    /// <b>不做重复探测：</b>这里<b>不</b>额外预探测一次安全阶段——每条交付前的重新确认已经覆盖，
    /// 且避免对真实安全边界产生与交付条数无关的额外通知。
    /// </para>
    /// <para><b>重入：</b>若已在交付中则直接返回 0（由外层循环统一处理）。</para>
    /// </remarks>
    internal int DeliverDeferredAtSafePhase()
    {
        var coordinator = ActiveCoordinator;
        if (coordinator.DeferredCount == 0)
        {
            return 0;
        }

        // 不安全时 DeliverTurn 的第一条会放回队首并返回 0（顺序与队列状态不变）。
        return DeliverTurn(coordinator, immediate: null);
    }

    /// <summary>当前是否有待交付的排队输入（宿主据此决定是否继续循环）。</summary>
    internal bool HasDeferredInput => ActiveCoordinator.DeferredCount > 0;
    /// <summary>队列由空变非空时触发一次（宿主据此请求单次唤醒）。</summary>
    internal event Action? DeferredInputQueued;

    /// <summary>请求一次唤醒（由宿主注入；无窗口测试为 null）。</summary>
    internal Action? RequestWake { get; set; }

    private void RequestOneWake() => RequestWake?.Invoke();

    /// <summary>交付是否正在进行（重入保护状态，供宿主/测试观察）。</summary>
    internal bool IsDelivering => _delivering;

    /// <summary>交付/重放进行中标志：为 true 时新输入只入队。</summary>
    private bool _delivering;

    /// <summary>
    /// 交付一条已捕获的输入（<b>唯一</b>事件派发实现：立即路径与延后路径共用）。
    /// </summary>
    /// <param name="action">已捕获的不可变输入。</param>
    /// <remarks>
    /// <para>
    /// <b>只读捕获值：</b>坐标/修饰键/token/消费标志全部来自捕获快照，不读可变当前状态。
    /// </para>
    /// <para>
    /// <b>不递归：</b>本方法不再调用 <see cref="Submit"/>；交付期间到达的输入由
    /// <see cref="_delivering"/> 拦在队列里，等外层返回后按 FIFO 处理。
    /// </para>
    /// <para>
    /// <b>副作用一致：</b>Esc/Ctrl+Q 退出与失活清理在这里统一处理，立即与延后语义相同。
    /// </para>
    /// </remarks>
    private void DeliverCaptured(WindowsTsf.DeferredInputAction action)
    {
        DeliverCapturedCore(action);
        _ime?.NotifyCommittedBaselineChanged();
    }

    private void DeliverCapturedCore(WindowsTsf.DeferredInputAction action)
    {
        switch (action.Kind)
        {
            case WindowsTsf.HostDeferredInputKind.Character when action.Text is { Length: > 0 } text:
                var textEvent = new TextInputEvent(text);
                OrderedDeliveredCount++;
                LastTextEvent = textEvent;
                TextInputEventCount++;
                _runtime.DispatchTextInput(textEvent);
                _runtime.InvalidateRender();
                break;

            case WindowsTsf.HostDeferredInputKind.Key when action.Key is { } key:
                var keyEvent = new KeyEvent(key.Kind, key.Key, key.Modifiers, key.IsRepeat);
                OrderedDeliveredCount++;
                LastKeyEvent = keyEvent;
                KeyEventCount++;

                var handled = _runtime.DispatchKeyHandled(keyEvent, key.KeyToken);

                // 与立即路径完全一致的退出语义（使用捕获时的消费标志）。
                if (!handled
                    && !key.ConsumedByIme
                    && ((key.Key == UiKey.Escape && _exitOnEscape)
                        || (key.Key == UiKey.Q && _exitOnCloseShortcut && keyEvent.IsCommandPressed
                            && !keyEvent.Modifiers.HasFlag(KeyModifiers.Alt))))
                {
                    RequestExit();
                    return;
                }

                _runtime.InvalidateRender();
                break;

            case WindowsTsf.HostDeferredInputKind.Pointer when action.Pointer is { } pointer:
                var pointerEvent = new PointerEvent(
                    pointer.Kind, pointer.Position, pointer.Button, pointer.Modifiers, pointer.ClickCount);
                OrderedDeliveredCount++;
                LastPointerEvent = pointerEvent;
                PointerEventCount++;
                _runtime.DispatchPointer(pointerEvent);
                break;

            case WindowsTsf.HostDeferredInputKind.Wheel when action.Wheel is { } wheel:
                var wheelEvent = new WheelEvent(wheel.Position, wheel.OffsetX, wheel.OffsetY, wheel.Modifiers);
                OrderedDeliveredCount++;
                LastWheelEvent = wheelEvent;
                WheelEventCount++;
                _runtime.DispatchWheel(wheelEvent);
                _runtime.InvalidateRender();
                break;

            case WindowsTsf.HostDeferredInputKind.Focus when action.FocusActivated is { } activated:
                OrderedDeliveredCount++;
                _runtime.NotifyWindowActivation(activated);

                if (activated)
                {
                    _runtime.InvalidateRender();
                    break;
                }

                // 失活清理：使用捕获时冻结的坐标/修饰键。
                var cancel = new PointerEvent(
                    PointerEventKind.Cancel,
                    action.FocusPointerPosition ?? LogicalPointerPosition,
                    _pressedButton == PointerButton.None ? PointerButton.Left : _pressedButton,
                    action.FocusModifiers ?? _modifiers);
                LastPointerEvent = cancel;
                PointerEventCount++;
                _runtime.DispatchPointer(cancel);
                _pressedButton = PointerButton.None;
                _modifiers = KeyModifiers.None;
                _clickCount = 1;
                break;
        }
    }

    /// <summary>已按序重放的输入数量（诊断）。</summary>
    internal int OrderedReplayedCount { get; private set; }

    /// <summary>队列溢出次数（诊断；&gt;0 表示有输入未交付）。</summary>
    internal int OrderedOverflowCount { get; private set; }
    /// <summary>已按序延后的输入数量（诊断）。</summary>
    internal int OrderedDeferredCount { get; private set; }

    /// <summary>已按序交付的输入数量（诊断）。</summary>
    internal int OrderedDeliveredCount { get; private set; }

    private long _orderedSequence;

    private Point LogicalPointerPosition
        => new(_pointerWindowPosition.X * _pointerScaleX, _pointerWindowPosition.Y * _pointerScaleY);

    private static double NormalizeScale(double scale)
        => double.IsFinite(scale) && scale > 0 ? scale : 1.0;

    private void DispatchPointer(PointerEvent pointerEvent)
    {
        // 捕获完整指针负载（坐标/按键/修饰键/点击计数均为捕获时的值）。
        var captured = new WindowsTsf.DeferredPointerInput(
            pointerEvent.Kind, pointerEvent.Position, pointerEvent.Button,
            pointerEvent.Modifiers, pointerEvent.ClickCount);

        Submit(WindowsTsf.DeferredInputAction.ForPointer(++_orderedSequence, captured));
    }

    private void RequestExit()
    {
        ExitRequestCount++;
        _requestExit();
    }
}
