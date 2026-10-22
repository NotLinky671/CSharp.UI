namespace CSharp.UI.TsfHostCheck;

/// <summary>
/// 生命周期安全点的纯门禁判定：把“能不能进入 profile 查询”的环境前置检查
/// 收敛成可测试的纯函数。生产 <see cref="ScenarioRunner.ProbeLifecycleSafePoint"/>
/// 在读取 Win32/store 状态后调用本判定；测试可直接覆盖每一个失败原因，
/// 无需 GUI。
/// </summary>
internal static class LifecycleSafePointGate
{
    /// <summary>
    /// 是否允许进入安全点后续动作（profile 查询、Show、截图）。
    /// 所有环境前置检查全部为真才放行：自有前台 + 键盘焦点在自有窗口 +
    /// 无未释放 store 锁 + 不在 OnLayoutChange 等通知重入栈内。
    /// <paramref name="ownsInputSurface"/> 为 opt-in child 模式的扩展门：
    /// root 与 input HWND 必须存活、属本进程、且 owning UI 线程同为当前线程；
    /// 默认 off 时调用方传 true，旧 4 参行为完全不变。
    /// </summary>
    internal static bool Evaluate(bool ownForeground, bool ownFocus, uint lockType, int layoutDepth,
        bool ownsInputSurface = true)
        => ownForeground && ownFocus && ownsInputSurface && lockType == 0 && layoutDepth == 0;
}
