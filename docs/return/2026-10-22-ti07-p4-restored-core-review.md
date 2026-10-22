# TI-07 P4 恢复后核心实现独立审核（进行中）

日期：2026-10-22 · 状态：审核中（不声称通过）
范围：`NativeCompositionOwner.cs`（21:29 Release DLL 反编译恢复）、`NativeCompositionFact.cs`、`TsfHostSession.cs`、`TextInputServiceSession.EndCore`；只读 src/tests，不跑 dotnet/GUI、不提交。

## 1. 恢复文件状态

- 误删后从 `bin/Release/net10.0/CSharp.UI.OpenTK.dll`（21:29, md5 `2a7f7052…`）反编译恢复，fast 已核验 md5 链。
- 当前源 1529 行：XML 文档注释已补回（`///` 存在，含不变量 remarks）；四个 nested record struct 存在；`CancelIntent.DeactivationFence`/`_cancelSuppressionTicket`/`ExecuteDeactivationFences`/`CaptureFocusProbe`/`DiagnosticProbe` 均在。
- **残留反编译痕迹（维护性，非功能）**：`Capture`（:365-431）与 drain（:944-1038）内有明显 IL 风格：`nativeCompositionFact`/`nativeCompositionFact2` 两次构造事实再赋值、`num/num2/flag/flag2` 数字命名、`goto IL_0379`（:1226/1243-1244）、`(uint)kind <= 1u`、双 `lock(_gate)` 相邻块（:664-679）。编译 0 错但可读性差；`SynchronizeCommittedBaseline` 的 `goto IL_0073`/`end_IL_0025`（:1361-1379）是反编译直接产物。**不凭测试绿宣称源码等价；建议 good 在功能冻结后做一轮语义保持的重构清扫（去 goto/重命名/合并双构造），或如实记录保留理由。**

## 2. 已核对的协议不变量（静态亲读）

- 捕获时冻结：`CapturedNativeText`/`CapturedActiveRange`/`CapturedTerminal`/`CapturedSelection`/`CapturedDeactivated` 均 init-only 快照；`End` 事实 `CapturedNativeText` 回退读 store 全文（:386-389）。`CapturedDeactivated` 在 capture 时经 `SafeFocusProbe` 冻结（:391、:778），非 drain 时读取——符合要求。
- 归因→应用序：drain 每 fact 前 `deactivated==true → ExecuteDeactivationFences()`（:971-974，fence 意图不受 ticket 分界）→ `ExecutePendingCancelsUpTo(num2)`（ticket 分界）→ suppression 判据。前置 `TsfHostSession.DrainSafePhase:1093` 先消费 ticket<首 pending fact 的意图。**独立 pending（fence 前 capture 且未 drain）会先被应用——fa2→fa4 修复已验证该时序保留（测试 B :189-201 保持"capture→fence→deactivated 批→同批 drain"）。**
- epoch 契约：fact/intent 都绑 `_ownerEpoch`；drain `OwnerEpoch != _ownerEpoch` 直接拒（:952-955）；`ApplyCancelIntent` 同样校验（:630）。fence 标记（`_cancelSuppressionTicket`）不随 Start 清除——owner 生命周期边界（:1032-1036）。
## 3. 缺陷/必修项与落地状态（已 relay good，均已实现并复核源码）

1. **`deactivated==false && ticket>fence` 放行缝——已修**：判据改 `_cancelSuppressionTicket>=0 && (deactivated==true || num2>fence)`（owner:986-988）；false 只豁免 fence 前独立结果。新反例 `失活栅栏后焦点恢复不解除旧owner写入零`（TsfCancellationTests.cs:253-281）亲读已含 SimulatedDeactivated=false + NativeInsert+Start+End+EditCompleted 全链零写。
2. **late 测试空洞——已修**：`失活栅栏后同段迟到的Start与结果零写`（:236-239）已补 `NativeInsert(4,"hao")`+`CaptureCompletedEdit(textChanged:true)`，走 CompleteEdit 路径断言零写。
3. **GetFocus 单判假阴（hide 实证）——已修**：`CaptureDeactivationState`（TsfHostSession.cs:939-951）改 `focus != hwnd || foreground != hwnd`；注释写死单顶层宿主契约（fg!=hwnd 含 fg=0/被接管即失活，多窗口宿主需重审）。minimize 走 focus 侧、hide 走 fg 侧互补。
4. **接缝正式化——已修**：`CaptureFocusProbe→CaptureDeactivationProbe`、`SafeFocusProbe→SafeDeactivationProbe`、session `CaptureFocusState→CaptureDeactivationState`（:449/:939）；注释去"临时/根因证明后移除"。`DiagnosticProbe` 仍保留（good 声明取证接缝，统一移除属最终清理非本轮）。
5. **不可预设环境问题——已 relay fast**：restore 链（fg/SETFOCUS/WA_ACTIVE/新 epoch）须实测分项报告。主审另发现**既定验收缺项**：min/hide/deactivate 恢复后须真实 nihao→Space→精确你好 setter+1 + 新 epoch/CompositionId 证据（非合成 Result、非替代场景、各场景新目录）——已 relay fast；三场景 exit0 不视为完整通过。

## 4. 本轮新必修（已 relay good，待实现）

- **每事实可避免分配**：`Capture` 对每条事实 `new` 两次 NativeCompositionFact 再赋值（先基础构造读属性再构造带 init 字段的覆盖）——违反 NEVER avoidable allocation，必须合并单次构造+初始化器，保持冻结值/锁序。同类自查：drain 内 `ExecutePendingCancelsUpTo`/`ExecuteDeactivationFences` 每 fact 分配 `CancelIntent[]`（原地 partition 可避免）。`goto IL_0379`/`end_IL_0025`、`num/flag2` 命名属纯可读性，功能冻结后同批最小清扫。
- 修后同批：构建 + TsfCancellationTests/NativeCompositionOwner/邻域 + TextFieldCompositionTests 全量 + fast 真实 smoke；主审不以 9/9 替代全量。

## 5. 证据链（亲读）

- `artifacts/ime-work/p4-cancel-safety/test-fa6.stdout.txt` 10/10、`test-fa6-neighbors` 51/51、`test-ui-fa6` 66/66；`build-fa6` 0 错（10 既有警告，无 owner 新增）。
- `hide-restore-fixed/stdout.txt` seq181-203：hide 泄漏机理（fg=0 唯一早信号；fence intent ticket16 晚于 facts → EditCompleted t15 Commit → WRITE nihao）。
- `minimize-restore-fixed/stdout.txt` seq181-197：minimize 批 deactivated=True 抑制零写；但 seq244-248 `final_text=""`/`writes=0`——只证零写+恢复，**未证恢复后唯一提交**（§3.5 缺项）。

## 6. 功能验收与最终复核结论（2026-10-22 晚）

- **fixed2 三场景亲读通过**：minimize/hide/deactivate 均 zero_writes、epoch1→2、restore_commit 你好×1、exit0、callback_exceptions=0。
- **清理冻结版定点复核（最终 hash 抽验一致 owner=30e8879f/session=bfbd342c）**：Capture 单次构造+初始化器；两执行器锁内 partition→`_intentScratch.ToArray()` 快照→锁外执行（**重入问题按 (a) 修复**，注释如实写"正确性所需分配"）；`goto IL_*` 清零；语义命名；`DiagnosticProbe`+`Trace` 产品侧全移除；`CaptureDeactivationProbe`/`CaptureDeactivationState` 正式化含单顶层宿主契约注释；`CompositionSegment` 残留注释已改。
- **判据最终态**：`deactivated==true → ExecuteDeactivationFences()` → `ExecutePendingCancelsUpTo(ticket)` → suppress 当 `fence>=0 && (deactivated==true || ticket>fence)`；probe=`focus!=hwnd || foreground!=hwnd`（minimize/hide 两路径互补）。
- **全量验证（同一最终源）**：slnx Rel+Dbg 0 错；Release 全量 549+666+581+53+41+67+96=2053 过 0 失败；Debug 抽样 10+66 过。**TEMP 权限闭合**：%TEMP% 对 .NET 子进程写保护（powershell/cmd 可写、fresh dotnet 被拒）→进程级 TEMP/TMP 重定向 proc-temp 后 Text.Tests 67/67、ImeProbe.Tests 96/96（修前 8+15 fail），非环境借口；HostRoutingSpike 编译错=fast 未跟踪文件。

## 7. 审核结论：通过

- **核心实现审核：通过**。postclean smoke 亲读核实（postclean-minimize-restore/hide-restore/window-deactivate 三新目录）：exit0、JSONL 零 owner-diag/wnd-msg 残留（工具+产品接缝清理实际生效）、zero_writes_while_hidden/away=true、new_epoch_session_id=2、restore_commit_text=你好/writes=1、final_text=你好 binding_writes=1；候选 PNG（s8a2/s8c/s8d）落盘。**最终 executor 形态（owner=58626b17）**：`_intentScratch` 字段已删——两执行器改 `stackalloc CancelIntent[MaxCancelIntents]` 方法栈帧批次（CancelIntent 为 unmanaged readonly record struct、有界 4），锁内按谓词原地 compact 收集、锁外按索引执行；同步重入拿不到本帧 span，重入安全性优于共享 List/ToArray 快照且零分配，注释如实。我复核确认正确。
- 恢复文件经类型/nullable/nested/不变量/双配置定向核实判恢复完整（非逐字等价边界由 good 侧声明）；失活归因链（capture 冻结 `focus!=hwnd||foreground!=hwnd` + fence owner 生命周期 + ticket 分界）经真实三场景反例收敛；所有我侧必修项闭合；全量 2053/0 失败（同一最终源）；TEMP 权限经进程级根闭合非借口。
- **放行**：good 可按 repo 惯例英文 commit+push。交付文档由 delivery-record 归档。
