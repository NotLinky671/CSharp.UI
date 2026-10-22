# TI-07 P4 窗口失活取消零写与会话结束同步重入安全修复记录（产品侧）

日期：2026-10-22
状态：`EndCore` 同步取消投递已修复并通过定向验证；待 fast 用真实 hide-restore/minimize-restore 复验“窗口失活零旧段写 + 恢复新 epoch 唯一提交”。
依据：[P4 产品边界审核](2026-10-22-ti07-p4-product-boundary-review.md) §6.2、[冻结计划](../plan/2026-10-17-text-input-and-editing-plan.md):116/118。

## 1. 问题

`RequestNativeCancel`（`TextInputServiceSession.cs`）只把取消意图登记到 `_cancelPending`，实际 `Service.RequestCancel` 延迟到安全阶段 `FlushPendingNotifications` 发出。但失焦/失活的生产序列是**同栈紧邻**：`ReleaseTextInputOwnershipFor` → `CancelCompositionForRelease`（本地取消+登记意图）→ `EndInputSession` → `EndForOwner` → `EndCore`。意图登记后同栈就结束会话，待 flush 时 `_owner==null` 的分支直接清 `_cancelPending` 不投递——**TIP 从未收到 `TerminateComposition`**，Deactivate 后自行决定提交/放弃。

## 2. 被否决的上一版补丁

上一会话在 `EndCore` **开头**（捕获 lease/generation、`IsReleased`/Revoke **之前**）同步调 `Service.RequestCancel`：

- 归属尚未切断时同步调服务，`RequestCancel` 内同步重入 `Start`/`End` 可递归，且外层随后 `++_generation`/`Revoke` 会波及重入新建的会话；
- 与 plan:116“先切断旧事件归属，再异步通知平台取消，防同步回调串写”冲突；
- “本地已 cancel”不能证明同步 Result 不写（无 `Start` 的 `Result` 也是合法事件）。

## 3. 正确顺序（已实现）

`EndCore`（`src/CSharp.UI/Editing/TextInputServiceSession.cs:214-303`）：

1. 入口处按 owner+generation **捕获**登记的取消意图（意图绑定旧归属）；
2. 照常先失效：`owner.IsReleased`/`MarkGeometryRevoked`/清 `_owner/_lease/_ownerNode/_factory/_serviceSessionStarted/_cancelPending`/`lease.Revoke`（plan:116 先切断）；
3. 归属切断**之后**把意图投递 `Service.RequestCancel`（此时迟到 Result 只会显式拒绝、不能写新客户端）；
4. 每次服务调用后复查 `_generation != generation || _owner is not null`：同步重入新建会话即返回，不再发旧会话的几何撤销/`EndSession` 覆盖新归属；
5. `EpochInvalidationRequested` 归属不投递（会话退役本身终结组合，与 flush 既有语义一致）；异常收敛 `ReportServiceFailure` 不吞。

`Service.RequestCancel` 是**同步**调用（冻结的“异步通知”指不在 Measure/Arrange/Emit 阶段、经安全阶段接缝；`EndCore` 本身就在服务调用边界）。投递仍在 `EndSession` 之前——真实路径里 `WindowsTextInputRouter.EndSession → RetireTsfLocked → bridge.Dispose` 才退役 owner/context，取消先于退役到达桥。

## 4. 回归

- `tests/CSharp.UI.Tests/TextFieldCompositionTests.cs`：`FakeTextInputService` 加 `OnRequestCancel` 同步回调钩子；新增 `EndDeliversPendingNativeCancelAfterOwnershipSevered`（投递+回调内迟到 Result 显式拒绝零写）、`RequestCancelSynchronousRefocusKeepsReentrantSessionAndServiceOwner`（取消内同步切焦点，新会话归新 owner、可写）、`RequestCancelSynchronousEndDoesNotRecurseOrClobber`（取消内同步 End 幂等不递归）、`CancelThenEndStillRejectsLateResultAndKeepsCommittedText`（迟到 Result 零写）；`PendingNativeCancelIsNotDeliveredToNewOwnerComposition` 断言对齐 `CancelCount==1`（旧归属取消必须送达，不投给新段）。
- `tests/CSharp.UI.OpenTK.Tests/TsfCancellationTests.cs`：新增 `失焦结束会话时登记的取消意图真正交给真实桥`——真实隐藏 HWND+真实 `TsfInputBridge`+**真实 `ITfComposition`**（`StartComposition`/`SetText` 写会话建立，非 `Owner.Capture` 假事实）：`SetFocus(null)` 走生产 `ReleaseTextInputOwnershipFor` 路径触发 `EndCore` 投递，断言 `Owner.LastTerminateAttempted` + `LastTerminateHResult>=0`（真实 `TerminateComposition` S_OK）+ `Sink.EndCallbacks` 真实增加（同步 `OnEndComposition` 到达）+ 预编辑零写。不以 `RequestCancelCalls`/`CancelApplied+Deferred` 计数当真实终止证据。

## 5. 验证（最终冻结 hash `58626b17` 上实测；原始 stdout/stderr/exit 在 artifacts/ime-work/p4-cancel-safety/）

**Release 全量（2053 过 / 0 失败）**：

- `CSharp.UI.Tests` 全量 549/549；`CSharp.UI.OpenTK.Tests` 全量 666/666（含 `TsfCancellationTests` 10/10）；
- `CSharp.UI.TsfHostCheck.Tests` 581/581；`CSharp.UI.Integration.Tests` 53/53；`CSharp.UI.NativeEditReference.Tests` 41/41；
- `CSharp.UI.Text.Tests` 67/67 + `CSharp.UI.ImeProbe.Tests` 96/96——两项目写 `%TEMP%` 对 .NET 子进程受安全策略拦截（powershell/cmd 同路径可写，fresh console 实测被拒）；进程级 `TEMP/TMP` 重定向到工作区临时根后全绿，非代码缺陷。

**Debug 配置（同 hash）**：`CSharp.UI.Tests` 549/549、`CSharp.UI.OpenTK.Tests` 666/666（含 `TsfCancellationTests` 10/10）、`CSharp.UI.TsfHostCheck.Tests` 581/581、`TextFieldCompositionTests` 66/66。

**真实场景（fast postclean，新目录，同 hash）**：minimize/hide/deactivate 三场景各满足 a)失活段 0 旧写 b)恢复后新 epoch(session_id=2) 唯一"你好"提交 c)真实候选 overlay PNG；证据 `docs/return/2026-10-22-ti07-p4-real-validation/postclean2-*/`。

## 6. 遗留边界（非本修复引入）

- `RequestCancel` 因 `HostWorkGuard`/排空中不满足安全前置时只**登记** owner 意图（`RequestCancel(executeNow:false)`）并返回 false；意图有界（`MaxCancelIntents`），由下一次真实安全阶段 `DrainSafePhase` 执行。**若随后同栈 `EndSession→RetireTsfLocked→Dispose` 而再无安全阶段，意图随桥销毁——这是 minimize/deactivate 1 写的候选根因（c）**：意图登记了但 `TerminateComposition` 从未执行，`Deactivate` 让 TIP 提交。不能把“Dispose 销毁 context 即无残余组合”当取消已完成的证据；待 fast native 分支数据（`HostWorkGuard()`/`_draining`/`store.IsSafePhase`/`Owner.RequestCancel` 返回/`LastTerminate*`）定性后按实际消息/作用域设计最小实现（更早捕获失活意图或安全排空），不以字符串/时间窗去重、不改公开 API。
- **窗口失活零写修复（focus/fg 归因已实现，三场景真实验收通过）**：deactivate/minimize/hide 全 PASS（fixed2 新目录，主审亲读 JSONL）。判据 `deactivated = !focusIsHwnd || !fgIsHwnd`（capture 冻结 `CapturedDeactivated` 三元）——minimize TIP 经 TSF focus 通道在 KILLFOCUS 前 capture（focusIsHwnd=False）；hide 的 SHOWWINDOW/NCACTIVATE 先于 KILLFOCUS，TIP capture 时 GetFocus 仍 hwnd 但前台已 0（fgIsHwnd=False）——OR 互补，单一顶层窗契约下 fg!=hwnd 即失活（含 fg=0/前台被接管，不做 child/辅助窗特判，多窗口宿主需重审）。suppression：fence 应用后 `deactivated==true || ticket>fence` 一律拒（false 只豁免 fence 前已确认独立结果 ticket<fence）；drain 遇首个 deactivated==true fact 先 `ExecuteDeactivationFences`（fence ticket 可晚于失活批）。协议测试注入 `()=>SimulatedDeactivated` 确定性语义。

### 6.1 工程清扫（恢复文件 + 接缝收口，已完成）

- `Capture` 双构造合单（NEVER avoidable alloc）；`ExecutePendingCancelsUpTo`/`ExecuteDeactivationFences` 的意图批次驻留**方法栈帧** `Span<CancelIntent> = stackalloc[MaxCancelIntents]`（锁内 copy due+原地 compact、锁外按 index 执行）——`CancelIntent` 为 unmanaged readonly record struct、有界 4 项、栈帧对同步重入天然不可达；无共享 List/无 ToArray/无池。
- `goto IL_*`/`end_IL_*` 清零（`CompleteEdit` 合成分支 if/else 化、`SynchronizeCommittedBaseline` 前置条件合并）；drain 局部 `array/list/num/flag` 改语义命名（`pendingFacts/results/appliedActions/rejectedCount/factTicket/isReplay/suppressed/edit/action/result`）。
- `DiagnosticProbe` 静态接缝 + `Trace` 方法 + 全部 12 个 Trace 调用点移除（owner/session/bridge/router/adapter 五文件）；`CaptureDeactivationProbe`/`CaptureDeactivationState`/`SafeDeactivationProbe` 为正式语义名。
- 回归测试新增：`失活栅栏后焦点恢复不解除旧owner写入零`（SimulatedDeactivated=false 反例，ticket>fence 不豁免）；late 批补 `NativeInsert`+`CaptureCompletedEdit` 真实提交路径。

### 6.2 恢复完整性声明（如实）

行为等价核实项（非逐字 IL 对照——无机械 IL-diff 记录）：nested 类型签名（`NativeCompositionDrainResult`/`NativeCommittedBaseline`/`NativeCompositionContext`/`NativeDocumentDelta`）、init-only 初始化器、nullable 注解（CS86 清零）、取消流水/epoch/ticket 归因协议逻辑、`ExecutePendingCancels*`/`ApplyCancelIntent`/`CompleteEdit`/`Classify`/`Apply`——佐证为**双配置构建 0 错 + Release/Debug 全量测试通过 + fast 真实三场景验收**，非源码逐字等价声明。命名/注释复原项：字段声明顺序、局部变量名（已语义化整理）、XML/中文注释原文（按语义重建非逐字）。dll 21:29 之后未 build 的快照方案（drain-start `onlyDeactivationFence`/`suppressionBound`/`Start` 例外）**有意不恢复**——该方案已被 focus 归因取代。

## 7. 事故记录：`NativeCompositionOwner.cs` 误删与恢复（2026-10-22）

- **事故**：`edit` 锚定失败时误发文件级 `REM`，删除未跟踪的 `src/CSharp.UI.OpenTK/WindowsTsf/NativeCompositionOwner.cs`（~2180 行，无 git 历史、无回收站副本）。
- **恢复**：从 21:29 Release `CSharp.UI.OpenTK.dll`（最后一次含全部变更的成功构建）ilspycmd 反编译重建 + 手工补回 nested 类型（`NativeCompositionDrainResult`/`NativeCommittedBaseline`/`NativeCompositionContext`/`NativeDocumentDelta`）、init-only 初始化器、nullable 注解（CS86 清零）、关键 XML 语义注释（类级/取消流水/epoch/ticket 归因）。
- **备份**：`artifacts/ime-work/p4-cancel-safety/backup-2129/`（dll SHA256=`b7141d2501da416a4e1916c985ee92348054b28491fd57fd1433b9ab1623a5ee`、pdb=`949eb3539f4910f1eb438231f69ad76612bad765c2b78e8fb8bedc1207c65c30`）。
- **已验证等价（仅限以下声明）**：Release+Debug 构建 0 错、本文件 CS86 0 警告、`TsfCancellationTests` 9/9、`HostRoutingContractTests` 22/22——行为佐证，**不声明源码逐字等价**。
- **不可恢复项（如实）**：全部 XML/中文注释原文（已按语义重建，非逐字）、字段声明顺序、局部变量名（num/flag/list 命名已部分整理）、file-scoped 格式。dll 21:29 之后未 build 的编辑（drain-start `onlyDeactivationFence`/`suppressionBound`/`<=bound`/`Start` 例外移除的快照语义组）**有意不恢复**——该方案已被否决。
- **edit-safety 规则已收紧**：任何工具失败/文件外部变化后必须先 `read` 再 `edit`；禁止文件级 `REM`。
- fast 的 aux-window v4 `foreground_left=false` 曾确认为脚本线程调用 `SetActiveWindow`（不跨输入队列授前台）；已重写为 UI 线程 `Query(ctx)` 内调用并**经 postclean 三场景实测**（恢复后新 epoch 唯一提交过）。
- **临时取证接缝（已移除收口）**：`NativeCompositionOwner.DiagnosticProbe` 属性+`Trace` 方法+全部 12 个调用点（owner/session/bridge/router/adapter）已删除；fast 侧 wnd-tap/`owner.CaptureFocusProbe` 接线由 fast 自行清理。取证结论已固化进正式 `CaptureDeactivationProbe` 注入点（生产语义，非临时）。
