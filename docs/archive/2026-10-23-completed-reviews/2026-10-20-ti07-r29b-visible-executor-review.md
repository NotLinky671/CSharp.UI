# TI-07 R29B 可见场景执行器审核

审核日期：2026-10-20

结论：**不通过，不放行真实按键实验。** DSH runner60815 已正常 end_turn/exit0，实现报告 Debug/Release 各 361 项正式测试及 82 项自检通过。Codex 对真实执行器的独立托管故障注入审核为 **7/22 通过**。生产源码与三份受保护文件 hash 未变化。

证据：[独立审核输出](2026-10-20-ti07-tsf-prototype-review-evidence/r29b-visible-executor-baseline/audit.log)。同目录保存审核源码、执行器源文件及所用 Debug DLL SHA256。审核调用真实 ChildSurfaceGeometryExecutor，替换原生宿主事实，不发送输入、不创建窗口。

## 必须修复

1. **[P1] 安全点失败仍可返回成功。** 三个场景的 `*-after` GuardedProbe 返回 null 后仍继续读几何、查询及截图，`ok` 仅检查后续 post 快照，没有检查失败的 after，也没有要求 Stop=None。只要下一次快照恢复，Run 就能返回 true，而 Stop 已为 SafePointLost。独立三个场景均复现。首个失败必须不可恢复成通过；停止后只允许安全恢复自有几何和测试结束清理。
2. **[P1] 缺少与动作同次 UI 调度的前置不变量检查。** 基线截图之后直接调用几何动作，再等待后才检查。独立基线截图返回后改变组合状态，三个场景仍执行了测量动作。必须通过真实与测试共用的 UI 派发动作体，先检查原生前台/焦点/拥有者、当前快照与基线，再决定动作，动作后复核；等待在 UI 线程外，前后均验证。
3. **[P1] 恢复截图失败不影响结果。** 三个 finally 内 Capture 返回值被忽略，独立注入恢复截图失败后三个场景仍通过。恢复的截图、等待及状态检查都属于验收，失败须使实验失败，并记录具体恢复失败，不能用先前局部成功覆盖。
4. **[P2] 恢复丢失原横向偏移。** scroll/clip 只保存 Y，随后 SetScrollOffset(0,savedY)。独立初始 X=5 时恢复成 0。保存、恢复并验证原始 X/Y、输入窗口、文本行、viewport 的精确几何；恢复失败不得通过。move-window 的无条件反向 +24 也会在原动作抛异常且未移动时错误地移动窗口，需以真实保存的原始根窗口位置恢复。
5. **[P2] 空矩形被误当全零矩形。** GetScreenExt 的 IsEmpty 判定接受 `{7,8,7,8}`。独立 clip 用该非零空矩形仍通过。应要求四边严格全零，GetTextExt 同理，并拒绝初始真实几何查询失败、零/倒置几何或无实际滚动。clip 还需确认固定 viewport 没有变化。
6. **[P1] 原生桥接的证据门禁仍不完整。** CaptureChildSurface 在 root crop 与 monitor 两次原生截图外只做一组前后检查；任务明确要求每张实际截图前后均验证，第一张失败后不能继续采集第二张。ProbeLifecycleSafePoint 的 child 模式只检查 root IsWindow 和输入焦点，不检查 input 的真实存活、PID/TID 和实际 UI 线程归属；应在 profile 查询前及查询后复核。原始矩形 getter 查询失败返回 default 容易掩盖失败，需要将失败显式传给执行器。

## 后续执行

先返修执行器及对应真实桥接方法，Codex 用独立审核重新验证；再完成截图/原生事实复核，检查默认与 child 隐藏路径回归，之后才运行真实微软拼音。完整目标仍为内部滚动候选跟随、全部裁出后候选隐藏、恢复时同一实时组合与零绑定写入。没有真实视觉结果前不得将 TI-07 标为完成，不提交或推送，不迁移生产、不自绘候选。

## 首轮执行器返修复核

runner89948 正常 end_turn/exit0，报告 Debug/Release 各 390 项正式测试、82 项自检通过。原有 22 项独立行为审核现已全部通过；其中瞬时失败注入明确发生于至少一次测量动作之后，而不是初始非变更读取之后。原始审核只适配新增根窗口绝对位置接口，保留原有行为断言。

但扩展异常审核仍为 **22/31**，新增 9 项全部失败：三个场景在测量截图抛 IOException、测量动作后的首次 Wait 抛异常、恢复截图抛异常时都直接逃出 Run。前两类还没有恢复几何：scroll Y=20（原9）、clip Y=150（原9）、move root Y=76（原100）。这证明手工返回分支不等于 finally 保证；截图输出路径的真实 I/O 失败即可触发此缺陷。

证据：[异常审核输出](2026-10-20-ti07-tsf-prototype-review-evidence/r29b-executor-exception-review/audit.log)，同目录保存独立源码、最终执行器源码、DLL SHA256。保护文件/生产源码 hash 再次确认未变。

必须补上覆盖测量期间所有退出和异常的真实 try/finally 几何回滚，使异常返回失败且不逃逸；恢复验证失败后禁止继续截图；首次失败后的 BestEffort 恢复不得额外调用会查询活动 profile 的 Probe。等待需要前后验证，clip/restore 的既定 600ms 沉降时间应保留。具体小范围任务：`artifacts/ime-work/ti07-dsh-r29b-exception-fix-task.md`。

第 6 项逐张截图及原生 safe-point 拥有者检查仍待后续修正。没有放行真实按键，没有候选行为通过结论，TI-07 继续未完成。

## 原生窗口方法的独立回归

等待异常返修期间，Codex 单独重建原生几何审核器并直接运行其带 PMv2 manifest 的 EXE，使用 runner89948 已完成的宿主方法：实际 DPI144 下 **34/34 通过**。确认完整父子链、GetWnd、裁剪交集、精确 11px 内部移动、全零裁剪、恢复、resize，以及根/viewport/input 全部销毁；外部前台与焦点保持。没有注入输入，没有运行真实中文候选场景。

证据：`2026-10-20-ti07-tsf-prototype-review-evidence/r29b-host-geometry-review/`，包含原始输出、独立几何源码和 Win32 oracle、宿主/显示表面源码及 DLL SHA256。此结果只确认已改动的窗口方法没有几何回归，不能替代执行器异常回滚或候选框视觉验收。

## 异常回滚返修独立复核

runner90681 正常结束。Codex 独立执行器审核现为 **31/31**，三场景测量截图、等待、恢复截图异常均返回失败且精确恢复几何。证据位于 r29b-executor-exception-fixed。仍需补齐正常恢复等待前后检查、部分恢复失败后的最终回滚，以及第6项真实桥接逐张截图/原生归属门禁；之后才运行真实候选测试。

追加部分恢复故障注入：三场景恢复函数写入部分坐标后抛异常时，当前 finally 因 restoreAttempted 已置 true 而跳过回滚。独立扩展为31/34，Y=12而非9、rootY=103而非100。已纳入 native-capture-fix-task 的窄范围返修，要求实际恢复确认成功后才能跳过最终回滚。原31项保持通过。输出 artifacts/ime-work/r29b-executor-partial-restore-before.log。

## 最终桥接与执行器复核

runner90603 正常结束。原生归属现在于实际 profile 查询前后读取 root/input IsWindow、真实PID/TID、当前UI线程、前台/焦点及锁/重入状态；逐张截图通过实际共享 RunStage 串行执行。源代码核对了 root失败立即return、monitor不执行的真实接入。需要区分：托管归属测试覆盖共享判定函数；并没有通过可注入整个COM查询路径来证明重入期间失效，不能把这部分说成完整故障注入。真实微软拼音运行则记录每阶段全部原生事实。

独立完整回归448/82每配置、执行器34/34、原生34/34、生命周期38+9、默认4child4隐藏均通过。后续真实结果见 `2026-10-20-ti07-r29b-real-candidate-review.md`：scroll视觉通过，clip隐藏失败，整体仍未通过。
