namespace CSharp.UI.TsfHostCheck;

/// <summary>纯托管退出判定：runner 正常完成、看门狗超时与提前关闭不能混为成功。</summary>
internal static class ProgramOutcome
{
    internal static int Resolve(int runnerExit, bool completedBeforeLoopExit, bool watchdogFired, bool joined)
    {
        if (!joined)
        {
            return 1;
        }

        if (watchdogFired || !completedBeforeLoopExit)
        {
            // 超时或窗口提前关闭属于失败；pending(3) 也不能当作成功。
            return runnerExit is 1 or 2 ? runnerExit : 1;
        }

        return runnerExit;
    }
}
