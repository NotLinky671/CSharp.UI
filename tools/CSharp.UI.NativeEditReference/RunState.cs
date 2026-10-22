namespace CSharp.UI.NativeEditReference;

/// <summary>
/// 单轮运行的统一状态：deadline、窗口关闭、中止。
/// <para>
/// 所有等待、消息派发、按键（down/up）、几何动作与截图前都检查同一份状态，
/// 因此超时或窗口关闭后不会再发送任何按键、也不会继续做动作。
/// 状态由 UI 线程自身推进（不使用并发 Timer）。
/// </para>
/// </summary>
internal sealed class RunState
{
    private readonly long _deadline;

    internal RunState(int timeoutMs) => _deadline = Environment.TickCount64 + timeoutMs;

    internal bool Aborted { get; private set; }

    internal bool WindowClosed { get; private set; }

    internal bool TimedOut => Environment.TickCount64 > _deadline;

    /// <summary>剩余时间（毫秒，最小 0）。供各等待点计算自己的上限。</summary>
    internal int RemainingMs => (int)Math.Max(0, _deadline - Environment.TickCount64);

    /// <summary>统一取消判定：任一成立即必须停止。</summary>
    internal bool ShouldStop => Aborted || WindowClosed || TimedOut;

    internal void MarkAborted() => Aborted = true;

    internal void MarkWindowClosed() => WindowClosed = true;

    /// <summary>停止原因（供日志）。</summary>
    internal string DescribeStop()
    {
        if (WindowClosed)
        {
            return "window-closed";
        }

        if (TimedOut)
        {
            return "timeout";
        }

        return Aborted ? "aborted" : "running";
    }
}
