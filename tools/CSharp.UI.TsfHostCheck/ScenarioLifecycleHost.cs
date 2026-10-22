namespace CSharp.UI.TsfHostCheck;

/// <summary>
/// <see cref="ILifecycleHost"/> 的真实实现：把安全点检查、同线程快照、Show 调用
/// 全部投递到场景窗口的 UI 线程执行。
/// <para>
/// 关键约束（R27 返修）：<c>GetForegroundWindow</c> 与 <c>GetFocus</c> **必须**在实际
/// UI 线程读取；组合/写入快照必须与该检查在**同一**安全点内取得。
/// </para>
/// </summary>
internal sealed class ScenarioLifecycleHost : ILifecycleHost
{
    private readonly ScenarioRunner _runner;
    private readonly TsfUiElementObserver _observer;
    private readonly int _writesBefore;
    private readonly Func<bool> _shouldStop;

    /// <summary>当前被操作候选的实例身份（同 id 被 End 后复用不算存活）。</summary>
    private ElementInfo? _target;

    internal ScenarioLifecycleHost(ScenarioRunner runner, TsfUiElementObserver observer,
        int writesBefore, Func<bool>? shouldStop = null)
    {
        _runner = runner;
        _observer = observer;
        _writesBefore = writesBefore;
        _shouldStop = shouldStop ?? (() => false);
    }

    public bool InvokeOnUiThread(Func<bool> action)
    {
        var result = false;
        _runner.Window.Invoke(() =>
        {
            result = action();
        });
        return result;
    }

    /// <summary>
    /// UI 线程内的一次性安全点：检查窗口/运行状态/自有前台/焦点归属/无读锁/无通知重入，
    /// 然后**在同一临界点**读取文本、组合范围、writes 与完整 profile 身份。
    /// 所有探针都经 <see cref="ScenarioRunner.ProbeLifecycleSafePointOnUiThread"/> 投递，
    /// 没有绕过窗口派发器的读取路径。
    /// </summary>
    public SafePointProbe Probe(string stage)
    {
        var snapshot = _runner.ProbeLifecycleSafePointOnUiThread(_writesBefore, stage,
            _shouldStop(), out var ok, out var detail);
        return new SafePointProbe(ok, detail, snapshot.Text, snapshot.SpanStart,
            snapshot.SpanEnd, snapshot.Writes, snapshot.Composing, snapshot.Profile,
            snapshot.CompositionBeginCount, snapshot.CompositionEndCount,
            snapshot.CompositionSessionId, snapshot.InputFocusChangeCount);
    }

    public void Record(string kind, IDictionary<string, object?> fields)
        => _runner.TraceLifecycle(kind, fields);

    /// <summary>
    /// 截图：整个前后安全点 + 抓图 + 不变量复核由 <see cref="ScenarioRunner.CaptureLifecycle"/>
    /// 投递到 UI 线程完成，不在调用线程读取 focus/store/profile。
    /// </summary>
    public bool Capture(string name) => _runner.CaptureLifecycle(name);

    public void Wait(int milliseconds) => Thread.Sleep(Math.Max(1, milliseconds));

    public uint? LiveCandidateId()
    {
        uint? id = null;
        _runner.Window.Invoke(() =>
        {
            var live = _observer.LiveCandidates();
            _target = live.Count == 0 ? null : live[0];
            id = _target?.Id;
        });
        return id;
    }

    public UiElementShowResult? ShowOnCandidate(uint id, bool show)
    {
        UiElementShowResult? result = null;
        _runner.Window.Invoke(() =>
        {
            // 必须命中**同一实例**：同 id 被 End 后复用是另一个对象，不得当作原对象。
            var live = _observer.LiveCandidates().FirstOrDefault(e => ReferenceEquals(e, _target));
            if (live is null || live.Id != id)
            {
                return;
            }

            result = live.TryShow(show);
        });
        return result;
    }

    public bool IsLive(uint id)
    {
        var live = false;
        _runner.Window.Invoke(() =>
        {
            live = _target is not null && _target.Id == id
                && _observer.LiveCandidates().Any(e => ReferenceEquals(e, _target));
        });
        return live;
    }
}
