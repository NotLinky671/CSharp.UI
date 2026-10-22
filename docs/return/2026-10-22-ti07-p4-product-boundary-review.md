# TI-07 P4 产品边界/验收失败根因返修与独立审核记录（产品侧）

日期：2026-10-22
状态：审核中；**已确认并修复 anchor-follow 产品缺陷（客户端发布去重吞掉平台失效后的同值重发），代码修复+回归测试已落盘，待 fast 真实 anchor-follow 复验**；minimize-restore 的会话重建与 hide/minimize 预编辑写入正在按原生事实/顺序取证（见 §6）。**TI-07/P4 未通过前不写交付结论**。
依据：[正式接入计划](../plan/2026-10-21-ti07-production-tsf-integration-plan.md) P4 节、[P3b 交付记录](../delivery/2026-10-22-ti07-p3b-delivery.md) §5 未覆盖矩阵、[P3b 独立审核](2026-10-22-ti07-p3b-independent-review.md)。

分工（本轮）：本侧独占产品 `src/`、相关产品回归 `tests/`、`docs/plan|task|guide|delivery` 与本审核记录；p4-real-validation 独占 `tools/CSharp.UI.ImeHostCheck`、真实执行证据与 dotnet/可见运行。

## 1. 产品代码路径核对（只读，行号以当前工作区为准）

### 1.1 几何失效/重发（移动、resize、DPI、最小化）

- `OpenTkHost.cs:586-916`：`_window.Move`（`UpdateViewport` → `NotifyGeometryInvalidated`）、`Resize`/`FramebufferResize`（`UpdateViewport` → `NotifyGeometryInvalidated` → `TryRedrawImmediately`）、`Minimized`（`_router.NotifySurfaceUnavailable` / 恢复后 `NotifyGeometryInvalidated`）、DPI 变化（`UpdateViewport` scaleChanged → `NotifyGeometryInvalidated`）。
- `TsfInputBridge.NotifyGeometryInvalidated`（`TsfInputBridge.cs:491-505`）：清文本几何缓存 → `RefreshSurface`（真实 `ClientToScreen` 原点 × `PixelScale`，失败如实 `PublishSurfaceFailure`，不伪造原点）→ 清 overlay 放置 → `_geometryClearedCount++` → `_layoutChangePending = true`。显示表面与文本几何是**独立**缓存（`RefreshSurface` 只重查窗口客户区，不依赖文本布局）。
- `NotifySurfaceUnavailable`（`:508-520`）：`PublishSurfaceHidden`（零矩形 + S_OK）+ 文本几何撤销 + pending。
- 判据：移动/resize/DPI/最小化-恢复之后，下一安全布局必须看到**新**屏幕坐标重发；绝不能残留旧原点。`anchor-follow`/`minimize-restore`/`breakpoint-reflow` 的真实运行覆盖此项。

### 1.2 失焦/失活

- `OpenTkInputAdapter.HandleFocusChanged`（`OpenTkInputAdapter.cs:172-177`）→ `DeferredInputAction.ForFocus` → `UiRuntime.NotifyWindowActivation(false)`（`UiRuntime.cs:689-713`）→ `InputRouter.CancelTextInput`（`InputRouter.cs:100-105`，先 `ReleaseTextInputOwnershipFor` 再 `OnFocusChanged(false)`）→ `TextInputServiceSession.End` → `WindowsTextInputRouter.EndSession`（`WindowsTextInputRouter.cs:718-732`）→ `RetireTsfLocked` → `TsfInputBridge.Dispose`（`TsfInputBridge.cs:898-913`：`ClearPresentation` → `_hook.Dispose` → `_coordinator.Clear` → `TsfHostSession.Dispose`）。
- `TsfHostSession.Dispose`（`TsfHostSession.cs:944-1084`）：`BlurCore`（焦点切到自有 parking 空文档管理器，读回 `GetFocus` 核对；焦点已属他人时 foreign-reject 不清它）→ UIElement sink `Unadvise`+`Dispose` → 几何/surface/overlay 清 → `UnadviseEditSink` → owner/store Dispose → parking 释放 → `Deactivate`（仅配对真实成功的 Activate）→ `Pop(TF_POPF_ALL)` → 释放 RCW 计数 → STA 租约 + `HostWriterRegistry.Release`。
- 重新激活：`ResumeTextInput` 按**新 epoch** 重建会话（`UiRuntime.cs:707-710` → `TextField.OnFocusChanged(true)` → 新 `TextFieldCompositionClient`/`Session.SessionId`）。
- 判据：旧会话 UIElement/组合事实不得落到新会话；reactivate 后 IME 输入与候选呈现可恢复。`focus-switch-cancel`/`minimize-restore`/`hide-restore` 覆盖字段级与窗口级路径；真正的 **窗口级**失活（如 alt-tab）见 §3 风险 2。

### 1.3 外改/禁用/卸载/关闭

- 外改（绑定 setter 之外的真实写）：`TextField` 渲染/安全阶段读回外部文本 → 取消接缝 `CancelCompositionForHostAction` → `RequestCancel`（`TsfInputBridge.cs:600-652`）→ `Owner.RequestCancel` 意图 → 安全阶段 `TryTerminateNativeComposition`（`NativeCompositionOwner.cs:854-887`，真实 `ITfContextOwnerCompositionServices::TerminateComposition(null)`；`TF_E_NOLOCK`/`TF_E_DISCONNECTED`/`E_UNEXPECTED` 可重试并保留意图）。取消先终止原生再走核心取消，绝不直接 `client.CancelComposition`（会留原生活组合）。
- 禁用/卸载：`TextField.Measure`（`TextField.cs:104-109`）对 `IsEffectivelyDisabled` 的组合走同一取消接缝 + `RequestDeferredEndInputSession`；卸载经 `UiRuntime.OnNodeUnmounting` → `InputRouter.CancelTextInput`/`SetFocus(null)` → `EndSession`。
- 关闭：窗口 `Closing` → `RequestExit` → `UiApplication.Run` 退出 → 宿主 `Dispose` → `DetachDeferredInputSignals`/`DetachHostSafePhaseSignal` → `Router.Dispose` → `RetireTsfLocked`（同一 `bridge.Dispose` 链）。
- 判据：`external-update-cancel` 必须见证取消真实发生且 setter 无额外写；`close-window` 必须先见证 `IsComposing=true` 再关窗并确认销毁；迟到 End/完成编辑不得写回绑定。

### 1.4 待办唤醒

- `HasPendingSafeWork = _layoutChangePending || _session.Owner.HasPendingSafeWork`（`TsfInputBridge.cs:180-181`）。
- `OpenTkHost.RequestDeferredInputRecoveryWake`（`OpenTkHost.cs:508-519`）只在宿主工作结束且仍有延后输入/安全待办时 `RequestWakeup(Zero)` 一次，不周期轮询。
- `DrawCandidates`（`TsfInputBridge.cs:766-813`）帧内复核身份，失效立即 `ClearGeometry(markLayoutPending)`。

## 2. 已确认的产品风险（未定为缺陷，待真实观察）

1. **帧内失效后空闲 drain 滞留**：`DrawCandidates` 在帧内 `ClearGeometry(markLayoutPending:true)` 置 `_layoutChangePending`，但帧末不额外 `RequestDeferredInputRecoveryWake`；若宿主完全空闲且再无新事件，pending 可能等到下一次输入/事件才由 `DrainTsfSafePhase` 发送。正常发布路径（`ProcessPendingWork` 末尾）已有一次唤醒，因此该分支只在“发布后到绘制前又失效”的兜底情形有意义。真实观察点：最小化/恢复、外改、窗口几何失效后无后续按键时布局通知是否及时发出。
2. **窗口级失活无独立 UIElement 清理**：TextField 持逻辑焦点但窗口失活时，UIElement sink 撤销完全依赖 `CancelTextInput → EndSession → bridge.Dispose`（`Dispose` 内先 `BlurCore` + `Unadvise` + `sink.Dispose`）。没有单独“失活但会话仍活”的 `Clear` 路径。`hide-restore`/`minimize-restore` 观察真实收敛；工具默认矩阵没有真正的 alt-tab 窗口级失活场景。
3. **resize 的显示表面重发**：`Move` 走 `NotifyGeometryInvalidated` 清+重发 surface；`Resize`/`FramebufferResize`/`DPI` 同一路径。需真实确认 resize 后 `GetScreenExt` 返回新客户区（`RefreshSurface` 查询失败如实 `PublishSurfaceFailure`，不兜底旧值）。

## 3. 工具/环境边界（如实记录，不虚构通过）

- 无真实窗口级失活（alt-tab 类）场景；`focus-switch-cancel` 是字段级，`hide-restore`/`minimize-restore` 覆盖窗口级失活但经 `WindowState`/`IsVisible`。
- 无 DPI 变化场景（单显示或工具未注）；无横向 `ScrollX` 场景（工具滚动均为垂直 `ScrollOffsetY`）；无禁用/卸载场景。
- 英文普通输入（本轮执行判据，沿用既有“自有线程选择已启用 profile 并恢复”范围；非用户原话或新授权）：不重跑已知无效的微软拼音 IMM 模式位/注入 Shift。先以本轮 GetProfiles/GetActiveProfile 确证存在**已启用**英文键盘 profile，再 FORPROCESS 临时选择并恢复后真实扫描码 `abc`，记录为“既有英文 profile 普通输入”；无可用英文 profile 时记环境边界。

## 4. 根因返修准则（本轮）

- 首个真实失败先读原始 `ime-host-check.jsonl`/PNG/stdout/exit，从源码定位；同一版本不重跑确认。
- 只修确证缺陷；保持核心唯一提交/取消/epoch 与真实几何语义；不抑制异常、不丢意图、不弱化测试、不造 fallback。
- 每项实际验证保留 stdout/stderr/exit 原文；单元测试不代替真实输入/画面证据。

## 5. 执行跟进（2026-10-22，首轮 P4 真实矩阵）

- 已执行：focus-switch-cancel PASS、external-update-cancel PASS、breakpoint-reflow PASS、hide-restore PASS（但见 §6.2 预编辑写入疑点）、unicode-direct PASS、undo-redo PASS、close-window PASS；**anchor-follow FAIL、minimize-restore FAIL、ascii-plain FAIL（复读后定性为 IMM 后端 + IME-backed HKL 环境路径，非 TSF 桥缺陷，见 §6.4）**。
- 环境只读采集（`artifacts/ime-work/p4-env/`，含 stdout/stderr/exit）：单显示器 1707×1067@150%（PixelScale=1.5）；已加载 HKL 0x08040804、0x04090409。DPI 变化场景**环境不可达**（单屏、不可改系统 DPI），记环境边界。

## 6. 已确认缺陷与返修

### 6.1 anchor-follow：平台清几何后客户端同值去重吞掉重发（已修复，待真实复验）

**真实失败**：移动前候选 9 项可见；移动后预编辑 `ni'hao` 仍在但候选消失；`anchor-follow/ime-host-check.jsonl` seq26 `anchor_after=(none)`、`publish_count_delta=0`，exit 1。

**源码链**：

1. `_window.Move` → `OpenTkHost.cs:593` `NotifyGeometryInvalidated` → `TsfInputBridge.cs:491-505`：`GeometryProvider.Clear()` + `RefreshSurface()` + overlay 置空 + `_layoutChangePending=true`。同一事件循环迭代内 `DrainTsfSafePhase` 发送 `OnLayoutChange`（TSF `GetTextExt` → 缓存空 → 如实失败），随后 `ProcessPendingWork` → `PublishTextInputGeometry`（`UiRuntime.cs:499-505` 无条件执行）。
2. `TextFieldCompositionClient.PublishGeometry`（`TextFieldCompositionClient.cs:297`）按**逻辑** `TextInputGeometry` 同值去重：纯窗口移动不改变逻辑 caret/composition/client/scale（屏幕原点不在其中），重算结果逐位相等 → `return false`，**根本不到** `InputSession.PublishGeometry → Service.PublishGeometry → TsfInputBridge.PublishGeometry` → `GeometryProvider` 永远保持清空。

**修复（已落盘）**：新增内部可选接缝 `IGeometryPublishInvalidationSink`（`CompositionCharGeometry.cs:125-149`），只重置 `_hasPublishedGeometry`/`_lastPublishedGeometry` 去重状态，**不动**当前逻辑几何与组合身份；`TextFieldCompositionClient:26,307-317` 实现；`TsfInputBridge.NotifyGeometryInvalidated`/`NotifySurfaceUnavailable`（`TsfInputBridge.cs:506-508,525-526`）对 `_activeClient` 调用。重发路径不变：同一迭代安全阶段 `PublishTextInputGeometry` 用**真实客户区 + 同帧有效 char snapshot** 重发；成功后 `PublishGeometry`（`:756-757`）再置 `_layoutChangePending` → `RequestDeferredInputRecoveryWake` 一次唤醒 → 下一 drain 发新 `OnLayoutChange` → IME 重查已填充缓存。有界、仅真实转移、不周期刷新、不伪造原点、不在原生回调排版。

**回归测试**：`tests/CSharp.UI.OpenTK.Tests/TsfCancellationTests.cs` 新增 `平台失效后消费者看到新屏幕原点且逻辑几何不变`——真实隐藏窗口 + 真实 `TsfInputBridge` + 真实 `TextField` 客户端：未失效时同值发布保持同一 `HostGeometryCache` 实例（去重生效）；`MoveWindow` 真实移动 + `NotifyGeometryInvalidated` 后，下一安全阶段重发让消费者看到**按 (dx,dy)×PixelScale 移动的新 `ClientScreen`**，逻辑几何逐位相同（fail-before/pass-after，消费者可见坐标，非计数/标志断言）。首版基于 `FakeTextInputService` 计数断言的弱测试已按审核要求删除。

**复验要求**（fast）：anchor-follow 真实重跑必须见 `anchor_after` 为**新屏幕坐标**的 `HostGeometryCache`、同一组合身份（epoch/compositionId）、0 预编辑写、唯一提交。

### 6.2 hide-restore / minimize-restore：预编辑 `nihao` 写入绑定（取证中，未定性）

**证据**：hide-restore seq26 `revoked_while_hidden=true, composing_while_hidden=false`，seq30 `final_text="nihao", binding_writes=1`；minimize-restore finish 同样 `nihao`/1 写。**已冻结契约**（`docs/plan/2026-10-17-text-input-and-editing-plan.md:118`）：窗口失活**先取消未提交组合**——本侧发起的取消要求零写；但 TIP 在焦点丢失时**自行完成并提交** `nihao` 是合法已确认结果（`binding_writes=1` 只能经 `ApplyValidated` 落到**未释放**客户端，证明真实 Result 在归属撤销前已应用）。两种时序皆与现有日志相容，不能凭结果字符串定泄漏：

- (a) 动作前已确认：原生焦点丢失/隐藏使 MS Pinyin 先结束组合并提交 result，在我们的延迟 `Focus(false)/CancelTextInput` 交付前已应用——真实事实不可丢弃，但按 plan:118 我方动作为先取消，该写是否算“合法”须经 owner ticket 归属论证（native Result ticket 是否早于我方取消意图 ticket），不能仅凭“先提交”就叫合法；
- (b) 缺陷：`RequestNativeCancel` 只入队待安全阶段，`EndCore` 清 `_cancelPending` **不发送**，随后 `Deactivate` 让 TIP 提交——但释放后提交无法写绑定，write=1 指向 (a) 或“取消已发出但 TIP 仍提交”；
- (c) 缺陷（待 native 消息序证伪/证实）：`bridge.RequestCancel`/`Owner.ExecutePendingCancels` 某安全前置（`_disposed`/owner 线程/`HostWorkGuard`/`_draining`/`store.IsSafePhase`/`_needsResync`/意图队列）不满足而只登记不执行，随后同栈 `EndSession→RetireTsfLocked→Dispose→Deactivate` 销毁意图，TIP 在 Deactivate 时提交。**注**：`SW_HIDE`/`SW_MINIMIZE` 官方明文“activates another window”，不能断言窗口隐藏不产 `FocusedChanged`/`WM_*`；hide 与 minimize 是否都真走失活取消路径待 native `WM_ACTIVATE/WM_SHOWWINDOW/WM_KILLFOCUS` 到达序与 owner 排空顺序证实。

**代码侧定性（2026-10-22，本侧修复已落盘）**：`EndCore` 确曾静默丢弃 pending native cancel——失焦/失活同栈 `CancelCompositionForRelease`（登记 `_cancelPending`）→ `EndForOwner` 时 `_owner==null` 的分支在 flush 里不投递，意图随状态清空丢失，TIP 从未收到 `TerminateComposition`。上一会话的 prefix 修复（捕获 lease/generation **之前**调 `Service.RequestCancel`）方向错误：在切断归属前同步调服务，`RequestCancel` 内同步重入 Start/End 可递归并串到新 owner，与 plan:116“先切断旧事件归属、再异步通知平台取消”冲突。**正确顺序（已实现）**：`EndCore` 先按 owner+generation 捕获意图 → 照常先失效（`IsReleased`/`MarkGeometryRevoked`/清字段/`lease.Revoke`）→ 归属切断后把绑定**旧** owner+旧 generation 的意图投递 `Service.RequestCancel` → 每次服务调用后复查 `_generation`/`_owner`，同步重入新建会话即返回（不误发旧 EndSession/几何撤销覆盖新归属）。`EpochInvalidationRequested` 的归属不投递（会话退役本身终结组合，与 flush 既有语义一致）。回归：`TextFieldCompositionTests` 新增 4 例（EndCore 投递+迟到 Result 拒绝、RequestCancel 内同步 refocus/同步 `EndForOwner` 幂等、取消后迟到 Result 零写），并把 `PendingNativeCancelIsNotDeliveredToNewOwnerComposition` 断言对齐为 `CancelCount==1`（旧归属取消必须送达，只是不投给新段）；`TsfCancellationTests` 新增 `失焦结束会话时登记的取消意图真正交给真实桥`——真实隐藏窗口+真实 `TsfInputBridge`+**真实 `ITfComposition`**（`StartComposition`/`SetText` 写会话建立）：`SetFocus(null)` 后 `Owner.LastTerminateAttempted` + `LastTerminateHResult>=0`（真实 `TerminateComposition` S_OK）+ `Sink.EndCallbacks` 真实增加 + 零写。验证（Release，exit 0，原始日志 `docs/return/2026-10-22-ti07-p4-real-validation/cancel-safety-fix/`）：core 组合类 66/66、相邻 InputFocus+UndoRedo 138/138、TsfCancellation 6/6、新集成 smoke 单方法 1/1。

### 6.3 minimize-restore：会话未重建（部分为工具前置边界）

restore 后 `reestablished=false` 且 `GetForegroundWindow()!=hwnd` → `EnsureFieldReady` 失败前置；GLFW 程序化 un-minimize 不一定恢复前台/`FocusedChanged(true)`，疑似环境前置而非产品缺陷（与脚本注释一致）。**待 fast 用前台恢复手段区分**：若真实 refocus 后 `ResumeTextInput`/`Start` 仍未建新会话，再回到产品侧 `IsWindowActive`/`StartSession` 链定位。

### 6.4 ascii-plain：英文 profile 下按键仍进组合（复读证据后定性：IMM 后备 + IME-backed HKL，非 TSF 桥缺陷）

`IsKeyConsumedByTsf`（`TsfInputBridge.cs:294-374`）**不按 profile/HKL 门控**，官方 `TestKeyDown→KeyDown` 完全由真实 keystroke manager 判定 eaten——此为冻结契约。

**2026-10-22 复读 `ascii-plain-english-keyboard/ime-host-check.jsonl` 全量 32 行后的定性**：

- seq3 `input-backend`：`tsf_opt_in=true` 但 `backend="Imm"`、`session_present=false`——**该运行从未建立 TSF 桥**，seq13 `uninstall-ok trigger=dispose` 在 abc 之前。整条按键路径走的是 IMM 后备（`WindowsImeBridge` 对 `WM_KEYDOWN` 只 `BeginKeyDispatch` 标记即返回 null，不消费）。
- seq18 `english-keyboard-selected` 内部自洽：`ActivateProfile(FORPROCESS)` S_OK、二次 `GetActiveProfile` 读回 `TF_PROFILETYPE_KEYBOARDLAYOUT 0x0409`（`verified_active_english=true`）——活动 TSF profile 确实是英文键盘 TIP；`activate_result_hkl=0x8040804`（ActivateKeyboardLayout 返回旧 HKL）+ `readback_hkl=0x4090409` 反映 HKL 层与 TSF 层分离，已如实记录。
- 疑点归位：本机只读探测 `ImmIsIME(0x4090409)=True`（`artifacts/ime-work/p4-env/keyboard-layouts.stdout.txt`）——该“英文键盘”HKL 在 IMM 层是 **IME-backed**（TSF 包装的键盘 TIP 在 IMM API 下同样报 IME）。在 IMM 后备下，`abc` 若触发真实 `WM_IME_COMPOSITION`（GcsAnyMask），`OnCompositionMessage` 隐式绑定 → `IsComposing=true` 是**真实 IME 行为**，不是 TSF 桥吞键。
- **尚需的证据**（交 fast）：该次运行 stdout/stderr 中 seq19-24 期间是否有 `ime-request`/`ime-char-swallowed`/composition trace（确认 WM_IME_* 确实到达）；以及 `tsf_opt_in=true` 却 `backend=Imm` 的原因（TryCreate 失败原因或配置默认）——若 TSF 应建立而未建立，属独立发现。若全程无 WM_IME_* 而按键仍消失，才回到 IMM 路径更深的缺陷定位。
- 英文普通输入若确实没有非 IME-backed 键盘 HKL 可用，如实记**环境边界**，不以该场景判 TSF 路由缺陷。
