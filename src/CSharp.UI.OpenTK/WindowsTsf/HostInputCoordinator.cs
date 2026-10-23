namespace CSharp.UI.OpenTK.WindowsTsf;

/// <summary>
/// 安全阶段不可用时，一条“属于旧归属”的后续输入被延后的原因（TI-07 P3a）。
/// </summary>
internal enum HostInputDeferralReason
{
    /// <summary>没有延后（正常路径）。</summary>
    None = 0,

    /// <summary>store 正处于 <c>RequestLock</c> 作用域内（不能重入应用）。</summary>
    InsideLock = 1,

    /// <summary>正在发送/处理 store 通知，不能在其中改文档。</summary>
    InsideNotification = 2,

    /// <summary>正在应用一个已排队的原生事实（重入保护）。</summary>
    InsideApply = 3,

    /// <summary>当前不在所有者线程。</summary>
    ForeignThread = 4,

    /// <summary>宿主正在处理重建/布局/帧，不允许在此写入。</summary>
    InsideHostWork = 5,
}

/// <summary>
/// 一次安全阶段尝试的结果（TI-07 P3a）。
/// </summary>
/// <param name="Applied">本次是否真的进入了安全阶段并应用了排队事实。</param>
/// <param name="Deferral">未进入安全阶段的原因；<see cref="HostInputDeferralReason.None"/> 表示成功。</param>
internal readonly record struct SafePhaseAttempt(bool Applied, HostInputDeferralReason Deferral)
{
    internal static SafePhaseAttempt Success { get; } = new(true, HostInputDeferralReason.None);

    internal static SafePhaseAttempt Deferred(HostInputDeferralReason reason) => new(false, reason);
}

/// <summary>
/// 宿主输入/事实的<b>有序协调器</b>（TI-07 P3a 纯托管核心）。
/// </summary>
/// <remarks>
/// <para>
/// <b>要解决的问题（接入审核要点已明确指出）：</b>一次窗口消息泵可能连续处理
/// “TSF 确认 → 普通 GLFW 字符 → 点击另一个 TextField”。若把 TSF 事实统一留到整个泵返回后才应用，
/// 而普通字符/焦点立刻改核心，就会<b>倒置原生顺序</b>：旧版本被拒、文字进错输入框。
/// </para>
/// <para>
/// <b>本类型的契约：</b>
/// </para>
/// <list type="bullet">
/// <item><description>
/// COM 回调只<b>捕获事实</b>（不写文档、不排版、不生成帧），由调用方在安全阶段调用 <see cref="TryBeginSafePhase"/>。
/// </description></item>
/// <item><description>
/// 任何“后续普通输入”（字符/指针/焦点）在<b>交付</b>前必须先 <see cref="TryBeginSafePhase"/> 成功；
/// 若当前不安全（锁/通知/重入/非所有者线程/宿主工作中），该输入被<b>按序延后</b>，
/// 绝不越过尚未应用的旧事实。
/// </description></item>
/// <item><description>
/// 延后队列有界；溢出时明确失败（<see cref="Overflowed"/>），不静默丢弃也不乱序重排。
/// </description></item>
/// </list>
/// <para>
/// <b>不做什么：</b>不递归泵 GLFW、不在 COM 回调里调用、不按文本/时间去重、不清空当前客户端。
/// 本类型不持有任何 COM 对象，也不写 Binding。
/// </para>
/// </remarks>
internal sealed class HostInputCoordinator
{
    /// <summary>延后输入队列上限（有界；溢出即明确失败，不静默丢弃）。</summary>
    internal const int MaxDeferredInputs = 256;

    private readonly object _gate = new();
    private readonly Queue<DeferredInputAction> _deferred = [];

    private bool _insideApply;
    private int _overflowCount;
    private int _deferredTotal;
    private int _appliedTotal;

    /// <summary>当前是否位于安全阶段应用内部（重入保护）。</summary>
    internal bool IsInsideApply
    {
        get
        {
            lock (_gate)
            {
                return _insideApply;
            }
        }
    }

    /// <summary>被延后的输入总数（诊断）。</summary>
    internal int DeferredTotal
    {
        get
        {
            lock (_gate)
            {
                return _deferredTotal;
            }
        }
    }

    /// <summary>成功应用的安全阶段次数（诊断）。</summary>
    internal int AppliedTotal
    {
        get
        {
            lock (_gate)
            {
                return _appliedTotal;
            }
        }
    }

    /// <summary>溢出次数（&gt;0 表示历史不再完整）。</summary>
    internal int OverflowCount
    {
        get
        {
            lock (_gate)
            {
                return _overflowCount;
            }
        }
    }

    /// <summary>当前延后队列长度。</summary>
    internal int DeferredCount
    {
        get
        {
            lock (_gate)
            {
                return _deferred.Count;
            }
        }
    }

    /// <summary>是否发生过溢出（历史不完整）。</summary>
    internal bool Overflowed => OverflowCount > 0;

    /// <summary>
    /// 尝试进入安全阶段：满足全部前置条件时返回 <see cref="SafePhaseAttempt.Success"/>。
    /// </summary>
    /// <param name="isOnOwnerThread">是否位于所有者 UI 线程。</param>
    /// <param name="storeSafe">store 是否处于可安全应用的阶段（无锁、无通知）。</param>
    /// <param name="hostWorking">宿主是否正在重建/布局/生成帧。</param>
    /// <remarks>
    /// 判定顺序按“最不可能恢复”到“最可能立即恢复”排列，便于诊断。
    /// <b>不</b>在此执行任何应用动作：调用方拿到成功后再应用并调用 <see cref="ExitSafePhase"/>。
    /// </remarks>
    internal SafePhaseAttempt TryBeginSafePhase(bool isOnOwnerThread, bool storeSafe, bool hostWorking)
    {
        lock (_gate)
        {
            if (!isOnOwnerThread)
            {
                return SafePhaseAttempt.Deferred(HostInputDeferralReason.ForeignThread);
            }

            if (_insideApply)
            {
                return SafePhaseAttempt.Deferred(HostInputDeferralReason.InsideApply);
            }

            if (hostWorking)
            {
                return SafePhaseAttempt.Deferred(HostInputDeferralReason.InsideHostWork);
            }

            if (!storeSafe)
            {
                return SafePhaseAttempt.Deferred(HostInputDeferralReason.InsideLock);
            }

            _insideApply = true;
            _appliedTotal++;
            return SafePhaseAttempt.Success;
        }
    }

    /// <summary>退出安全阶段（必须与成功的 <see cref="TryBeginSafePhase"/> 成对）。</summary>
    internal void ExitSafePhase()
    {
        lock (_gate)
        {
            _insideApply = false;
        }

        // 真实安全边界离开：这是“可能可以交付”的唯一自然时机。
        // 只通知一次（由宿主据此请求单次唤醒/重试），不做周期性轮询。
        SafePhaseExited?.Invoke();
    }

    /// <summary>
    /// 安全阶段退出通知（真实安全边界；宿主据此<b>单次</b>重新安排交付尝试）。
    /// </summary>
    /// <remarks>
    /// 只在 <see cref="ExitSafePhase"/> 真正离开安全阶段时触发，
    /// 因此不构成周期性轮询；没有延后输入时宿主忽略即可。
    /// </remarks>
    internal event Action? SafePhaseExited;

    /// <summary><b>仅测试用</b>：模拟一次真实安全边界退出通知。</summary>
    internal void RaiseSafePhaseExitedForTesting() => SafePhaseExited?.Invoke();

    /// <summary>
    /// 登记一条“必须排在已捕获事实之后”的后续输入。
    /// </summary>
    /// <param name="input">后续输入的完整不可变捕获（含重放负载）。</param>
    /// <returns>true 表示已入队；false 表示队列溢出（调用方必须如实失败，不得静默丢弃）。</returns>
    internal bool Defer(DeferredInputAction input)
    {
        lock (_gate)
        {
            if (_deferred.Count >= MaxDeferredInputs)
            {
                _overflowCount++;
                return false;
            }

            _deferred.Enqueue(input);
            _deferredTotal++;
            return true;
        }
    }

    /// <summary>取出下一条应交付的延后输入（FIFO，保持原生顺序；含完整负载）。</summary>
    internal bool TryDequeue(out DeferredInputAction? input)
    {
        lock (_gate)
        {
            if (_deferred.Count == 0)
            {
                input = null;
                return false;
            }

            input = _deferred.Dequeue();
            return true;
        }
    }

    /// <summary>
    /// 把一项放回队首（重放期间再次进入不安全阶段时保持顺序）。
    /// </summary>
    /// <param name="input">要放回的项。</param>
    internal void RequeueFront(DeferredInputAction input)
    {
        lock (_gate)
        {
            // Queue 没有 O(1) 队首插入；用「出队+重排」保持 FIFO 语义与有界性。
            var existing = _deferred.ToArray();
            _deferred.Clear();
            _deferred.Enqueue(input);
            foreach (var item in existing)
            {
                _deferred.Enqueue(item);
            }
        }
    }

    /// <summary>清空延后队列（归属退役/释放）。</summary>
    internal void Clear()
    {
        lock (_gate)
        {
            _deferred.Clear();
            _insideApply = false;
        }
    }

    /// <summary>只输出计数，不含用户内容。</summary>
    public override string ToString()
        => $"coordinator applied={_appliedTotal} deferred={_deferredTotal} "
            + $"pending={_deferred.Count} overflow={_overflowCount}";
}

/// <summary>延后交付的一条后续输入（TI-07 P3a）。</summary>
/// <param name="Kind">输入类别。</param>
/// <param name="Sequence">原生到达序号（同一来源内单调，用于证明顺序）。</param>
/// <param name="Payload">类别相关负载（字符码元/指针坐标索引等；不含用户文本内容日志）。</param>
internal readonly record struct HostDeferredInput(HostDeferredInputKind Kind, long Sequence, int Payload)
{
    /// <summary>只输出类别与序号，不输出用户内容。</summary>
    public override string ToString() => $"{Kind}#{Sequence}";
}

/// <summary>延后输入的类别（TI-07 P3a）。</summary>
internal enum HostDeferredInputKind
{
    /// <summary>普通字符输入（GLFW char 回调）。</summary>
    Character = 0,

    /// <summary>指针输入（点击/移动）。</summary>
    Pointer = 1,

    /// <summary>焦点变化（切换到另一控件）。</summary>
    Focus = 2,

    /// <summary>按键（按下/抬起，含重复与修饰键）。</summary>
    Key = 3,

    /// <summary>滚轮。</summary>
    Wheel = 4,
}
