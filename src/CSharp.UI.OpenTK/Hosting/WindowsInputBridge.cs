namespace CSharp.UI.OpenTK;

/// <summary>
/// 宿主 Windows 文本输入桥契约（TI-07 P1）：在 <see cref="ITextInputService"/> 之上补上宿主接线真正需要的
/// 窗口生命周期与按键归属操作。
/// </summary>
/// <remarks>
/// <para>
/// 宿主（<see cref="OpenTkHost"/>）与输入适配器（<see cref="OpenTkInputAdapter"/>）只依赖本契约：
/// 生产实现是 <see cref="WindowsImeBridge"/>（自有 HWND 子类化 + IMM32），后续 TSF 文档适配器同样实现它，
/// 宿主接线不需要随实现切换而改写。
/// </para>
/// <para>
/// <b>不扩大产品公开 API：</b>契约本身是 internal。验收工具继续通过 <c>InternalsVisibleTo</c> 观察具体实现
/// （子类化状态、锚点计数、追踪回调），<c>OpenTkHost.ImeBridge</c> 仍返回具体类型 <see cref="WindowsImeBridge"/>。
/// </para>
/// <para>
/// <b>生命周期不因契约而改变：</b>实例由宿主创建并持有，<see cref="IDisposable.Dispose"/> 恰好释放一次原生资源
/// （恢复窗口过程、钉住仍被原生引用的委托），实现负责幂等；契约不引入任何新的释放顺序要求。
/// </para>
/// <para>
/// <b>实现方契约：</b>所有成员只在创建窗口的 UI 线程上被调用；<see cref="UpdatePointerTransform"/> 与
/// <see cref="NotifyGeometryInvalidated"/> 可能在窗口事件回调内被调用，因此不得在内部重建组件、排版或生成帧。
/// </para>
/// </remarks>
internal interface IWindowsInputBridge : ITextInputService, IDisposable
{
    /// <summary>
    /// 当前正在分发的原生按键身份 token；非按键派发期间为 null。
    /// </summary>
    /// <remarks>
    /// 只在“本条原生按键消息的链式调用内”有效：输入适配器把它原样交给运行时，使组合在同一原生按键内结束后
    /// 仍能按同一身份判定归属，而不是按文本或时间窗猜测。
    /// </remarks>
    object? CurrentKeyToken { get; }

    /// <summary>当前原生按键是否已被输入法消费（例如 <c>VK_PROCESSKEY</c>）；非按键派发期间为 false。</summary>
    bool CurrentKeyConsumedByIme { get; }

    /// <summary>绑定运行时（服务组装完成后调用一次）：按键归属声明与脱敏诊断的落脚点。</summary>
    /// <param name="runtime">宿主运行时。</param>
    void AttachRuntime(UiRuntime runtime);

    /// <summary>
    /// 窗口位置/尺寸/可见性等使已发布锚点失效时的重发入口（宿主窗口回调调用）。
    /// </summary>
    /// <remarks>失效意味着平台侧状态不再可信：实现应先清掉“已发布值”缓存再按缓存逻辑几何重发。</remarks>
    void NotifyGeometryInvalidated();

    /// <summary>
    /// 更新“窗口内容区坐标 → 逻辑像素”换算系数（与输入适配器同源，随 DPI/内容缩放变化更新）。
    /// </summary>
    /// <param name="scaleX">X 方向系数（窗口坐标 × 系数 = 逻辑坐标）。</param>
    /// <param name="scaleY">Y 方向系数。</param>
    void UpdatePointerTransform(double scaleX, double scaleY);

    /// <summary>普通核心输入安全交付后，同步非组合文档及选区。</summary>
    void NotifyCommittedBaselineChanged() { }
}
