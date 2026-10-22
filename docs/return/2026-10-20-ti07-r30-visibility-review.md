# TI-07 R30 自有输入窗口可见性首审

审核日期：2026-10-20。DSH ACP `devin/swe-2` 首轮 runner73343 正常结束，报告每配置469正式测试及89自检。暂不运行真实输入。

Codex对实际执行器增加独立故障注入，原34项仍通过，新增3项全部失败，总计34/37：

1. hide已改变实际可见性但返回失败时，`hiddenApplied = op.Ok`为false，finally不恢复，输入窗口保持隐藏。
2. hide已改变可见性再抛异常时，原可见性还未记入局部变量，同样保持隐藏。必须在动作前单独读取并保存原可见性，将“动作可能已经发生”与“操作成功”分开。
3. 成功路径显式show恢复后，finally又show一次。独立统计为3次动作，应为hide/show各一次；最后一次动作位于恢复证据之后，未经检查，不应存在。

此外，hide后当前立即抓图，没有给原生候选UI异步变化沉降。需要在hide的前后检查通过后，增加UI线程之外600ms的WaitProbed，再捕获clipped证据。

证据：`artifacts/ime-work/r30-visibility-independent-before.log`；独立源码 `artifacts/ime-work/ti07-child-executor-codex-audit/Program.cs`。只返修隔离可见性执行器、原始状态读取接线及对应测试，不调整组合/焦点语义，不扩展生产。

## 返修及真实验收

返修 runner38780 正常结束。Codex 独立故障注入37/37通过，Debug/Release各473项正式测试及89项自检通过；生命周期38项、隐藏原生接线9项、真实DPI144窗口几何34项通过。默认及child隐藏宿主回归通过，受保护文件和生产代码哈希与基线一致。

真实R30结果仍不通过：`artifacts/ime-work/r30-child-hide-real/`。自有输入HWND在完全裁剪后实际可见性true→false，600ms沉降后微软拼音候选框依然可见。恢复false→true，同一ni'hao、span0..6、begin1/end0/session1/input_focus_events1、binding writes0保持。退出3仅表示待人工判图，不能算通过。

Codex复核baseline/clipped/restored的root及monitor截图。恢复后的root和monitor均可见ni'hao文字，没有确认恢复缺字问题；不能将早先的视觉误读当作代码缺陷。全屏图仅保存在私有artifacts目录，避免公开其他桌面内容。

R29B滚动跟随已真实通过；完全裁剪隐藏仍失败。下一步经用户允许研究临时挂起TSF文档焦点，必须独立记录TSF焦点改变，保持Win32焦点和同一次组合输入，不能以提交、取消、重输冒充恢复。
