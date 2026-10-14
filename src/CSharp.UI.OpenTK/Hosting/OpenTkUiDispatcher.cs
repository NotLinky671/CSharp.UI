using System.Collections.Concurrent;
using System.Diagnostics;

namespace CSharp.UI.OpenTK;

/// <summary>
/// 接入 OpenTK 主循环的 UI 调度器。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Post"/> 可以从任意线程投递动作：动作进入无锁队列后立即唤醒事件循环
/// （默认使用 <c>GLFW.PostEmptyEvent</c>，即使循环正阻塞在 <c>GLFW.WaitEvents</c> 也能被唤醒）。
/// </para>
/// <para>
/// <see cref="RequestWakeup"/> 记录最早的唤醒时刻，宿主用它计算事件等待的最长超时；
/// 光标闪烁一类定时重绘因此不需要忙等。
/// </para>
/// </remarks>
internal sealed class OpenTkUiDispatcher : IUiDispatcher
{
    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly Action? _wakeUp;
    private readonly int _uiThreadId;
    private readonly Lock _wakeupGate = new();
    private long _wakeupDeadline = long.MaxValue;
    private int _redrawPending;
    private int _redrawRequestCount;
    private int _postedCount;
    private int _executedCount;
    private int _wakeUpCount;

    /// <summary>创建调度器。</summary>
    /// <param name="wakeUp">唤醒事件循环的动作；null 表示不唤醒（无窗口测试）。</param>
    internal OpenTkUiDispatcher(Action? wakeUp = null)
    {
        _wakeUp = wakeUp;
        _uiThreadId = Environment.CurrentManagedThreadId;
    }

    /// <inheritdoc />
    public bool IsOnUiThread => Environment.CurrentManagedThreadId == _uiThreadId;

    /// <summary>当前待执行的动作数量。</summary>
    internal int PendingActionCount => _queue.Count;

    /// <summary>是否存在待执行的动作。</summary>
    internal bool HasPendingActions => !_queue.IsEmpty;

    /// <summary>累计投递动作数量。</summary>
    internal int PostedActionCount => Volatile.Read(ref _postedCount);

    /// <summary>累计执行动作数量。</summary>
    internal int ExecutedActionCount => Volatile.Read(ref _executedCount);

    /// <summary>累计重绘请求次数。</summary>
    internal int RedrawRequestCount => Volatile.Read(ref _redrawRequestCount);

    /// <summary>累计唤醒事件循环次数。</summary>
    internal int WakeUpCount => Volatile.Read(ref _wakeUpCount);

    /// <summary>下一次定时唤醒的剩余延迟；没有请求时返回 null。</summary>
    internal TimeSpan? NextWakeupDelay
    {
        get
        {
            long deadline;
            lock (_wakeupGate)
            {
                deadline = _wakeupDeadline;
            }

            if (deadline == long.MaxValue)
            {
                return null;
            }

            var remaining = (deadline - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency;
            return remaining <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(remaining);
        }
    }

    /// <inheritdoc />
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _queue.Enqueue(action);
        Interlocked.Increment(ref _postedCount);
        if (!IsOnUiThread)
        {
            // 后台线程投递：立即唤醒可能正在阻塞等待事件的主循环。
            WakeUp();
        }
    }

    /// <inheritdoc />
    public void RequestRedraw()
    {
        Volatile.Write(ref _redrawPending, 1);
        Interlocked.Increment(ref _redrawRequestCount);
        WakeUp();
    }

    /// <inheritdoc />
    public void RequestWakeup(TimeSpan delay)
    {
        var ticks = (long)(Math.Max(0, delay.TotalSeconds) * Stopwatch.Frequency);
        var deadline = Stopwatch.GetTimestamp() + ticks;
        lock (_wakeupGate)
        {
            if (deadline < _wakeupDeadline)
            {
                _wakeupDeadline = deadline;
            }
        }

        // 让主循环立刻重算等待超时；否则新的更早唤醒要等到上一次超时才生效。
        WakeUp();
    }

    /// <summary>执行全部已投递的动作；返回执行数量。</summary>
    internal int ProcessPostedActions()
    {
        var count = 0;
        while (_queue.TryDequeue(out var action))
        {
            count++;
            Interlocked.Increment(ref _executedCount);
            action();
        }

        return count;
    }

    /// <summary>取出并清除“已请求重绘”的标记。</summary>
    internal bool ConsumeRedrawRequest() => Interlocked.Exchange(ref _redrawPending, 0) != 0;

    /// <summary>取出并清除一个已经到期的定时唤醒；未到期时返回 false 且保留请求。</summary>
    internal bool ConsumeDueWakeup()
    {
        lock (_wakeupGate)
        {
            if (_wakeupDeadline == long.MaxValue || Stopwatch.GetTimestamp() < _wakeupDeadline)
            {
                return false;
            }

            _wakeupDeadline = long.MaxValue;
            return true;
        }
    }

    /// <summary>清除全部定时唤醒请求（测试用）。</summary>
    internal void ClearWakeup()
    {
        lock (_wakeupGate)
        {
            _wakeupDeadline = long.MaxValue;
        }
    }

    private void WakeUp()
    {
        Interlocked.Increment(ref _wakeUpCount);
        _wakeUp?.Invoke();
    }
}
