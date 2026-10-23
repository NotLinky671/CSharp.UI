# TI-07 P4 独立证据核对摘要（只读审计）

日期：2026-10-22
状态：**P4 / TI-07 尚未通过**。本记录只做只读核对，不验收、不修产品/测试/工具、不跑 dotnet/GUI。
依据原始材料：`README.md`、`docs/plan/2026-10-21-ti07-production-tsf-integration-plan.md`、`docs/return/2026-10-22-ti07-p4-product-boundary-review.md`、`docs/return/2026-10-22-ti07-p4-cancel-safety-review.md`、`docs/return/2026-10-22-ti07-p4-real-validation/README.md`、`artifacts/ime-work/p4-cancel-safety/test-opentk-all.stdout.txt`、并行/Collection 全量 grep（见 §4 增补）。
**Suite 并行/Collection 核对（全量 grep `tests/CSharp.UI.OpenTK.Tests`）**：

- 唯一 collection 是 `[CollectionDefinition]` `GraphicsCollection`（`GlContextFixture.cs:12`，名 "OpenTK 图形测试"），成员：`CandidateOverlayRenderingTests`、`GalleryGpuIntegrationTests`、`GalleryHostResizeTests`、`GlCommandRenderingTests`、`GlContextTests`、`GlShaderDiagnosticsTests`、`GlTextureTests`、`PreeditRenderingGpuTests`、`TextColorMaskTests`、`TextRasterSamplingTests`、`TextureEvictionRegressionTests`、`TsfHostIntegrationTests`、`TsfLayoutNotificationRegressionTests`、`UiApplicationTests`、`WindowResizeRedrawTests`。
- **`HostRoutingContractTests`、`WindowsImeSubclassRetirementTests`、`WindowsImeBridgeTests`、`TsfSessionLifetimeTests`、`TsfCancellationTests`、`TsfHookRetirementTests` 均无 `[Collection]` 属性** → xUnit 默认各为独立 collection、并行调度；`HostRoutingContractTests` 与 `WindowsImeSubclassRetirementTests` 分属不同 collection，确实可并行运行。
- `ResetForTesting` 全部调用点：`HostRoutingContractTests.cs:171,189,209,230,243,249`（`空句柄不能被占用` 一例在 `:249` 也 Clear）。这些是 5 个 `[Fact]` 同属一类、类内不并行，但**每个 fact 开头都 Clear 全局表**——任一 fact 与本测试窗口重叠即摘掉活租约。
- 其它 `HostWriterRegistry` 使用方只 `CurrentLease/CurrentOwner/Release/TryAcquire`，不 Clear（`TsfSessionLifetimeTests:380` 与 `WindowsImeBridgeTests:1490` 的 remarks 都明说不用 ResetForTesting 以防清掉并行测试持有者）——`ResetForTesting` 是唯一的全局清表者。
- 结论：跨测试 clear 证据链闭合，`ResetForTesting` 是唯一可达 culprit；不是"无交集即 flake"，而是共享静态表 + 并行类 + 无条件 Clear 的真实隔离缺陷。

## 1. 已完成场景（真实证据，来源注明）

以下均来自 `docs/return/2026-10-22-ti07-p4-real-validation/README.md` 记录，原始 stdout/stderr/exit/JSONL 落各场景目录：

| 场景 | 结果 | 来源（README 段） |
|---|---|---|
| focus-switch-cancel | PASS exit 0 | 首轮矩阵 |
| external-update-cancel | PASS exit 0 | 首轮矩阵 |
| breakpoint-reflow | PASS exit 0 | 首轮矩阵 |
| unicode-direct | PASS exit 0（合成通道，非 IME 证据） | 首轮矩阵 |
| undo-redo | PASS exit 0 | 首轮矩阵 |
| close-window | PASS exit 0 | 首轮矩阵 |
| anchor-follow-fixed | PASS exit 0（good 去重重置补丁后；同 CompositionId/SessionId/Epoch=1、移动零写、唯一"你好"+1，前后 PNG 主代理亲看） | 首轮矩阵/追加 |
| hide-restore（首轮） | PASS exit 0，但日志证隐藏期 nihao 1 写泄漏 → 后续复验目录如实暴露 | 追加执行 |
| english-uithread | PASS exit 0（UI 线程英文 profile 链全对、abc writes=3、profile/HKL 恢复） | 追加执行 |
| horizontal-scroll-v2 | PASS exit 0（真实 ScrollX=63.98>0、锚点有效、唯一提交） | 追加执行 2 |
| resize-samebranch-fixed | PASS exit 0（720→800 物理仍同分支、同组合身份 0 写） | 追加执行 4 |
| disabled-unmount-v1 | PASS exit 0（disable+unmount 两阶段 composition_ended+geometry_revoked+zero_writes+reestablished） | 追加执行 3 |
| window-deactivate-trace3 | PASS（按 fast IRC：drain suppressed×4+零写+恢复 epoch2；以 owner-diag 分类而非旧 bool） | fast IRC 通报 |

## 2. 未完成 / 暴露中的缺陷项

- **窗口失活零写（核心缺陷，未闭合）**：hide-restore-cancel-fixed、minimize-restore-cancel-fixed、window-deactivate-trace 均证失活期间 nihao 1 写泄漏。trace 取证根因：同 DrainSafePhase 迭代内 native WM_KILLFOCUS → drain 应用已 capture End/Result → 写 nihao → 然后才 deliver Focus(false)，cancel-intent 从未生成。good 已实施失活栅栏（`TsfHostSession.OnDeactivationWndProc`，WM_ACTIVATE 登记意图）+ `_cancelSuppressionTicket`；trace2 证 ticket12 意图早于 Update13/End15、terminate S_OK，但 Update13/EditCompleted16 仍被应用（终结分类错误，good 返修中）。fast 新证据：minimize 路径 WM_KILLFOCUS 先于 WM_ACTIVATE，fence 需挂更早边界。**待 good 稳定补丁后 fast 新目录严格复验（0 旧段写+恢复唯一提交）**。
- **minimize-restore 会话重建**：restore 后 5s 未重建 + `GetForegroundWindow()!=hwnd`，部分为工具前台恢复前置边界；待 fast 区分环境/产品。
- **ascii-plain**：首轮 FAIL 后复读证据——该运行 `input-backend` 记 `backend=Imm`/`session_present=false`（seq3 惰性建立前值），整个 abc 序列并未建立 TSF 桥；en-US HKL `0x4090409` 本机 `ImmIsIME=True`（IME-backed 键盘 TIP）。**注意**：这里的 `backend=Imm` 取自旧 IMM bridge 自身能力报告（`bridge.Capabilities`），不是当前 Router 实际路径判定，不可据它宣称"真实 IMM 后备"事实。english-uithread 已按"既有英文 profile 普通输入"判据 PASS（UI 线程快照 profile 切换+读回+abc writes=3+恢复），该 PASS 覆盖 TSF 路径普通英文输入；`tsf_opt_in=true` 却未建 TSF 会话的原因仍待如实记录。
- **NRE 首个全量失败**：`WindowsImeSubclassRetirementTests` 见 §4。
- **临时接缝未清理**：`NativeCompositionOwner.DiagnosticProbe`（产品侧 good）、工具 wnd-tap/owner-diag 挂接（fast）——最终交付前必须全移除并恢复路径 smoke。

## 3. 环境/工具边界（如实，不虚构）

- DPI 变化：单显示器 1707×1067@150%（PixelScale=1.5），系统 DPI 不可改 → 环境边界。
- 横向 ScrollX、禁用/卸载、真实窗口级失活：本轮新场景已实现并各自通过/取证中；不能因工具旧默认矩阵缺场景就记不可达。
- 英文普通输入：按既有英文 profile 判据已 PASS（english-uithread）；微软拼音内部英文模式未覆盖，如实边界。
- `hide-restore` 首轮 exit 0 是工具旧判据 PASS；失活零写判据已加严，后续再跑将如实失败——旧 PASS 不作数。

## 4. 首轮全量 NRE 分析（`artifacts/ime-work/p4-cancel-safety/test-opentk-all.stdout.txt`，stderr 空）

### 4.1 原文

- L60-65：`WindowsImeSubclassRetirementTests.外部后装过程不被覆盖且回到顶层后才恢复并释放租约 [FAIL]`；`System.NullReferenceException`；栈 `tests\CSharp.UI.OpenTK.Tests\WindowsImeSubclassRetirementTests.cs(116,0)`。
- L111-112：合计 `Total: 664, Errors: 0, Failed: 1`，仅这一个失败。

### 4.2 精确源码行

`tests/CSharp.UI.OpenTK.Tests/WindowsImeSubclassRetirementTests.cs:116`：

```csharp
Assert.Equal(HostWriterKind.Imm, HostWriterRegistry.CurrentLease(hwnd)!.Kind);
```

NRE 是 `CurrentLease(hwnd)` 返回 `null` 后 `!` 解引用，**不是** `Kind` 成员访问（`Kind` 是值类型属性）。

### 4.3 判定路径（只读代码核对）

`HostWriterRegistry`（`src/CSharp.UI.OpenTK/WindowsTsf/HostWriterRegistry.cs:68-179`）中，`Owners[hwnd]` 条目只有三条移除路径：

1. `Release(lease)`（`:121-145`）——必须传入**本租约实例**且 `ReferenceEquals` 匹配才移除。本测试中租约只被 `WindowsImeBridge._writerLease` 私有持有；`Dispose`（`src/CSharp.UI.OpenTK/Hosting/WindowsImeBridge.cs:1184`）仅在 `!_subclassInstalled` 时释放，而测试 `:112` 刚断言 `SubclassInstalled=true`（外部 externalPtr 在顶层、恢复失败如实保留），故本路径不可能移除。
2. `TryAcquire`（`:95-100`）——只在 `existing.IsReleased` 时清陈旧条目；`IsReleased` 只能由 `Release` 的 `MarkReleased` 置位，回到路径 1。
3. **`ResetForTesting()`（`:172-178`）——`Owners.Clear()` 无条件清空全部条目**，调用方为 `tests/CSharp.UI.OpenTK.Tests/HostRoutingContractTests.cs:171,189,209,230,243,249`（6 处）。

xUnit v3 默认按测试类分 collection 并行执行；`HostRoutingContractTests` 与本测试不同类，可在 NRE 断言前并行执行 `ResetForTesting` 清空全局登记表。本测试的 remarks（`:21-24`）本身就写明"不调用 `ResetForTesting`（那会清掉其它并行测试的真实持有者）"——即该竞态在夹具设计时已被认知但未隔离（共享静态 `HostWriterRegistry` + 并行类 + 全局 Clear）。

### 4.4 结论

**确证为夹具/测试隔离缺陷，不是产品代码缺陷，也不是新 WndProc hook 干扰**：

- `CurrentLease==null` 的唯一可达路径是 `Owners.Clear()`/`Release`；`Release` 已被测试内断言排除，只剩 `ResetForTesting` 并行清空。
- 新失活栅栏 `TsfHostSession.InstallDeactivationHook`/`OnDeactivationWndProc`（`TsfHostSession.cs:790-899`）只在 `TryCreate` 成功后对**本会话的宿主 HWND** `SetWindowProc`；本测试创建自己的 STATIC 隐藏 HWND 且全程只走 IMM `WindowsImeBridge`，与 `TsfHostSession` 无交点——hook 不可能是本 hwnd 的干扰源。
- 竞态是**既有夹具风险**（测试类设计时已声明），但**不能定性为可忽略的 flake**：它是真实隔离缺陷，同版重跑可能绿也可能再失败，取决于并行调度。首错必须保留。

### 4.5 建议处理（交给 good 定夺，本审计不改测试/产品）

- 最小修复方向：`HostRoutingContractTests` 改用非共享假句柄段（已经是 `0x1234/0x9999/0x7777` 假 HWND）改为**不复用全局表**或可注入的隔离表，或 `ResetForTesting` 仅清自己注册的句柄；不可放宽本测试的租约断言。
- 或者把 `ResetForTesting` 调用点集中到无并行冲突的集合（`[Collection]` 串行化），但这改变既有并行度，属测试夹具决策。

## 5. 对主代理 / good 的要点

1. **P4/TI-07 未通过**，阻塞项：窗口失活零写（fence 分类/边界返修中）、minimize 重建边界、DiagnosticProbe/wnd-tap 清理、最终全量+真实 smoke。
2. 首轮全量唯一失败 = 上述夹具竞态 NRE；产品侧无指向缺陷；new hook 已排除嫌疑；**不得靠重跑绿了就关掉**，应先修 `ResetForTesting` 隔离。
3. fast IRC 新证据（WM_KILLFOCUS 先于 WM_ACTIVATE）已由主代理转达 good：suppression 应限定在 window-deactivation fence 生命周期内、关联 native generation/被取消组合，不泛化为所有取消的永久黑名单。

## 6. 追加：good 汇报的 fence 收窄范围（peer IRC，2026-10-22）

p4-cancel-safety 已把 suppression 收窄到 window-deactivation 生命周期（未独立复核代码，转述其行号供主代理审）：

- `CancelIntent.DeactivationFence` 标志（`NativeCompositionOwner.cs:222`）；`RequestCancel(executeNow, deactivationFence=false)`（`:733`）。
- `ApplyCancelIntent` 成功时仅 `DeactivationFence` 才置 `_cancelSuppressionTicket`（`:914-917`）；判定 drain `:1391-1410`（非 Start 且 ticket>边界 → rejected+AdvanceWatermark）；新 `Start` 成功清 `-1`（`:1460`）。普通取消后无-Start 合法结果不受影响。
- `TsfHostSession.cs:891-893`：`WM_ACTIVATE(WA_INACTIVE)` 或 `WM_KILLFOCUS` 任一先到即登记 fence——覆盖 minimize 的 KILLFOCUS-先序。
- `ExecutePendingCancelsUpTo` 前置（`TsfHostSession.cs:1054`）+ drain 循环内每 fact 前（`NativeCompositionOwner.cs:1379`）。
- 负 HRESULT/拒绝语义声称未动；分配小修（`:815-831` new List+Insert+ToArray）按主代理指示后置。
- 待 fast 新目录 minimize/hide 严格 0 旧段写+恢复唯一提交复验。