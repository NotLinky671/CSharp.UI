# P-01 安全阶段返修记录（2026-10-29，safety）

任务：[P-01 UiRootLayerView + OverlayManager + PopupSurface](../archive/2026-10-31-popup-select-tasks/01-overlay-manager.md)
计划：[2026-10-29 Popup 覆盖层与 Select 实施计划](../plan/2026-10-29-popup-and-select-plan.md) §1/§4/§5
基线：`249ca96 docs: plan popup and select controls`（工作区含初版 P-01 实现，未提交）
状态：**返修完成，待主审 read + fast 复验；仍未勾选 P-01 验收，未提交/推送。**
前一轮记录：[2026-10-29-popup-select-p01-dev.md](2026-10-29-popup-select-p01-dev.md)（证据目录
`2026-10-29-popup-select-p01-evidence/`，本轮不改写）。

**验收状态如实记录**：本轮只修复主审/fast 实读确认的受审漏洞，并删除永久 smoke（其真实 Skia 结果只作
外部 evidence 保留）、清理内部 wiring 断言。新增的 A13–A16 是本轮**新增回归用例**，仅代表本机一次运行
通过，**不代表 P-01 验收通过**；前一轮的 A1–A12 也不能因为本机 green 就算验收通过。P-01 通过与否只由
主审复核 + fast 独立 probe 决定。

## 返修范围（只做安全根因 + 明确列出的测试卫生，不碰 P-02 定位算法）

主审/独立 probe 证实的 4 个安全缺口，全部在 `OverlayManager` 的安全阶段消费路径：

1. **OnMount 期嵌套打开漏判**（probe S6）：`Mount` 先 `ReconcileDescriptors` 再给 `_active` 赋值，
   内容 `OnMount` 中 `_active == null` → 嵌套判定只看 `_active.PopupNode`，请求被 accepted（返回 true），
   之后消费时关闭外层再挂载已卸载锚。
2. **挤占关闭后未复验资格**：`CloseEntry()` 可能连带卸载/禁用目标锚，随后仍挂载目标 popup。
3. **旧关闭请求误关新 entry**：旧 popup 内容在 `OnUnmount` 中请求关闭时，请求会被记入无代际的
   `_pendingClose`，之后消费就关掉刚刚挂载的新 entry。
4. **无界消费**：`ProcessPendingWorkCore` 对 overlay 消费没有预算；内容在 Mount/Unmount 回调中反复
   请求开合会自激（每轮布局 + reconcile），可挂死 UI。

## 实现（`src/CSharp.UI/Runtime/OverlayManager.cs`）

- **嵌套判定改为请求时 + 消费时双检**（`IsNestedAnchor`）：
  - 锚已在活动 popup 子树内（既有父链判定）；
  - **或** `_mounting`（正在协调本次 popup 子树）且锚 `Parent == null`——协调器先 `OnMount` 再挂父链，
    因此内容 `OnMount` 里请求的锚必然是未挂接节点，代表“内容自身请求嵌套打开”。返回 `false` +
    runtime 诊断，外层保持打开（不推进任何 entry 状态）。
  - 消费时再验一次（防请求登记后子树才挂接完成），同样拒绝 + 诊断、外层保持。
  - 取舍：不用“一律在 reconcile 期拒绝所有 Show/Close”的粗暴方案（主审已否决）——主树锚的
    `Show`（如旧 popup `OnUnmount` 请求打开另一个主树锚）仍然被接受并有序消费。
- **挂载代际 `_mountEpoch`**：`Mount` 开始与 `CloseEntry` 各推进一次；`Close()` 记录当时的代际，
  消费时 `_closeEpoch != _mountEpoch` 即视为**过时请求**丢弃。于是：
  - 内容 `OnMount` 主动 `Close()`（代际=本次挂载）→ 挂载完成后有序关闭（不被丢弃）；
  - 旧内容 `OnUnmount` 中的 `Close()`（代际=已结束 entry）→ 不会关掉随后挂载的新 entry。
- **同阶段请求按“最后一次请求”收敛**：`Show` 清除挂起的关闭，`Close` 清除挂起的打开。
  语义确定（同阶段内互相覆盖），跨阶段由代际保护；不引入通用事务抽象。
- **挤占后复验**：`ConsumePending` 在 `CloseEntry()` 之后重新 `IsShowEligible(show)` 才 `Mount`，
  绝不把已卸载/已禁用锚的 popup 挂上去。
- **有界安全阶段**（复用 `ResponsiveTransactionManager` 纪律）：`ConsumePending(long passId)` 按
  `ProcessPendingWork` 轮次计数，单轮实际消费超过 `MaxConsumptionRoundsPerPass = 16` →
  `ReportDiagnostic` + `throw InvalidOperationException`。`ProcessPendingWork` 既有 catch 路径
  `DropPendingRequests()` + 整树失效后重抛，运行时保持可用、已挂载 entry 稳定（不残留每帧自激）。
- 遗留说明：`IsEntryEligible` 仍每轮校验（锚 mounted/enabled/anchorVisible/popupBounds/窗口激活）；
  `OnNodeUnmounting` 的关闭请求也写入当前代际（不再产生永久过时请求）。
- 完整 owner `false→true` 边沿状态机仍属 P-03，本轮只保留 `AwaitingOpenEdge` 骨架与 internal 入口。

## 其他同批修改

- `src/CSharp.UI/Runtime/UiRootLayerView.cs`：宿主**仅在 `HasActiveEntry` 时** `MeasureFresh`，关闭状态
  走普通 `Measure` 缓存——不再为关闭状态每轮强制测量（无 P-01 之外的算法改动）。
- `src/CSharp.UI/Runtime/OverlayHostView.cs`：新增 internal `HasActiveEntry`（转发 manager）供上述判断。
- `src/CSharp.UI/Runtime/UiRuntime.cs`：`_overlay.ConsumePending(passId)`（预算按轮）。
- **删除永久 smoke**：`tests/CSharp.UI.Integration.Tests/OverlayLayerSmokeTests.cs`（只断言 Frame 非空/
  Texture 存在、无泄漏计数，价值不足）。真实 Skia 烟测改为本轮 throwaway 运行（日志见下方 evidence），
  独立真实 runtime probe 由 fast 承担；Integration 套件回到 **56**。
- **清内部 wiring 断言**（`tests/CSharp.UI.Tests/OverlayRootTests.cs`）：
  A1 删 `TraversalRoot` 同一/`Children.Count==2`/`Children[0]` pin（保留 public `Parent != null`/`Depth==1`
  与 ForEach 全内容布局）；A2 删转发 `ActiveEntry.AnchorNode` 与 `NotSame(TraversalRoot/Host, hit)`；
  A8/A12 删 `TraversalRoot.Children[1].Children` 内部结构断言；A9 删 `DoesNotContain(Host, focusOrder)`。
- **新增 4 个回归用例（待主审/fast 复验，非已验收）**（真实不确定边界，隔离且不无限循环）：
  | 测试 | 覆盖 |
  |---|---|
  | `A13_NestedShowFromContentOnMount_IsRejected_OuterPopupStaysOpen` | OnMount 嵌套返回 false、外层内容仍在、诊断含“嵌套”、下轮不替换外层 |
  | `A14_CloseFromContentOnMount_ClosesEntryAndStaysStable` | OnMount 主动关闭不被丢弃、内容节点卸载、稳定不复开 |
  | `A15_ShowFromOldPopupOnUnmount_OpensNewMainAnchorAndConverges` | 旧 popup OnUnmount 请求新主树锚 → 旧 entry 卸载、新 entry 活且稳定、不被旧关闭误关 |
  | `A16_ReopenFromContentOnMount_IsBounded_ReportsDiagnosticAndRecovers` | 每次挂载重开的自激 → `InvalidOperationException` + 诊断、挂起丢弃、运行时恢复、可正常关闭 |

## 验证（原始 stdout/stderr 与真实 exit code，UTF-8；新前缀不改写上一轮 evidence）

证据目录：`docs/return/2026-10-29-popup-select-p01-safety-evidence/`

| 命令 | 结果 | 证据 |
|---|---|---|
| `dotnet build CSharp.UI.slnx -c Debug` | 0 警告 0 错误，exit 0 | `build-debug.log`、`build-debug-2.log`、`build-debug-final.log` |
| `dotnet run --project tests/CSharp.UI.Tests -c Debug` | Total **732**, Failed 0，exit 0（728 + 4 新回归） | `core-tests-final.stdout.txt`（终态）、`CSharp.UI.Tests.final.*` |
| `dotnet run --project tests/CSharp.UI.Integration.Tests -c Debug`（含 throwaway smoke） | Total **57**, Failed 0，exit 0 | `smoke-runtime.stdout/stderr/exit.txt` |
| 同上（删除 throwaway 后） | Total **56**, Failed 0，exit 0 | `integration-tests.*`、`CSharp.UI.Integration.Tests.final.*` |
| `dotnet run --project tests/CSharp.UI.Markdown.Tests -c Debug` | Total **50**, Failed 0，exit 0 | `markdown-tests.*`、`CSharp.UI.Markdown.Tests.final.*` |

- throwaway smoke（真实 Skia 文本 + headless 运行时，`pixelScale=1.5`）覆盖：OnMount 嵌套拒绝且外层弹层
  真实文本排版非空、PushClip 等于 popup Frame；OnMount 主动关闭后卸载；自激重开诊断 + 运行时恢复 + 可关闭。
  运行后已删除该临时文件（`git status` 无残留）。
- **OpenTK 套件本轮不重跑**：主审已用基线 worktree 取得缺失数据——基线 `249ca96` 上同一用例
  `GalleryIdleAndBoundsTests.极窄视口有确定几何与溢出诊断` 失败 1（Markdown Copy 按钮
  `(32, 17565.747, 51.883x24.621)` 越界，几何断言数值与当前树完全相同，仅诊断前缀/measure 计数 539 vs 541 不同），
  确认为 preexisting、与本计划无关。按 P-07 报告该已知边界，本阶段不修 80px Copy 样式、不删该测试。
- 未做任何 `git add`/commit/push；未改写上一轮 evidence 与 `2026-10-29-popup-select-p01-dev.md`。

## 接口/权衡记录（供主审与后续任务）

- internal 签名变化：`OverlayManager.ConsumePending(long passId)`（原无参）；新增
  `OverlayManager.MaxConsumptionRoundsPerPass`、`OverlayHostView.HasActiveEntry`。**无公开 API 变化。**
- 请求合并语义（同阶段最后者胜）是有意选择：`Show`/`Close` 都是“意图”而非命令队列；需要保序的
  合法场景（OnMount 关闭、OnUnmount 打开、挤占后再打开）都已被 A14/A15 与实际 runtime smoke 覆盖。
- 自激预算耗尽走**异常**而非静默丢弃：与 `ResponsiveTransactionManager` 一致（诊断 + 抛出），
  运行时 catch 丢弃挂起请求后恢复；正常收敛（1–3 次消费）远低于上限 16。
- 未引入 `FocusManager` 私有状态；代际/请求序列由 manager 自己持有。

## Freeze 声明

P-01 代码、测试与本记录已冻结：**停止 build/edit**，等待主审 read 与 fast 独立复验（S6 应得到
`Show == false` 且外层 popup 仍存在；新增 safe-consume edge 按上述“最后者胜 + 代际 + 有限预算”约定）。
主审通过后才进入 P-02；未通过则在 `docs/return/` 继续新增返修记录。

## 复审结果（append-only，2026-10-29）

- **主审 4F45：P-01 通过。** 实读 `safeConsume` / 回调期代际 / 嵌套 guard 后未见新阻断；合法回调
  （OnMount Close、旧 popup OnUnmount Show）与有限预算异常恢复均判定有效。
- **独立 probe（ACBE）S1–S9 全部 PASS，结果已 read**：S6 现为 `Show == false` 且外层 popup 仍存在，
  与本记录约定一致。
- 因此上一节“Freeze 声明”的等待条件已满足；P-02 由 fast 独占实施（纯几何 calculator + manager
  `ArrangeEntry` 有限 measure 小范围），本工作流在此期间不改 `OverlayManager`/`UiRuntime`、不 build。
- 本记录是 P-01 安全返修的评审文件，**不是** P-07 最终交付记录；验收勾选/交付文档在 P-07 统一生成。
