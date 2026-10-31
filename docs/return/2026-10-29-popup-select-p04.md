# P-04 开发记录：输入语义（外点吞并、锚豁免、Esc、失活、Up-activation、滚轮）

任务：[P-04 输入语义](../archive/2026-10-31-popup-select-tasks/04-popup-input.md)
计划：[2026-10-29 Popup 覆盖层与 Select 实施计划](../plan/2026-10-29-popup-and-select-plan.md) §5
前置：P-03（主审通过：4C09 source + 独立 public probe M1..M14 PASS）
状态：**实施完成，冻结待主审 read + fast 独立 probe；未勾选验收、未提交。**
证据目录：`docs/return/2026-10-29-popup-select-p04-evidence/`（原始 stdout/stderr + 真实 exit code，UTF-8）。

## 实现

### 1. dismiss-target（外点吞并的真实节点）

- 新增 `src/CSharp.UI/Runtime/OverlayDismissTargetView.cs`（internal）：全屏命中
  （`IsHitTestVisible=true`、`HitTestSelf=true`）、`WantsPointerCapture=true`、Down/Move/Up/Cancel
  全部 `MarkHandled` 并返回 true；不绘制任何内容。
- `OverlayManager` 宿主子项改为 **`[surface?, dismiss-target?]`，两者用稳定 key 区分**
  （`"overlay:surface"` / `"overlay:dismiss"`）：
  - 关闭 entry 只移除 surface，**吞并期的 dismiss-target 保持挂载到 Up/Cancel**（关闭弹层不等于结束
    吞并）；带 key 的协调保证任意一方增删都不会误 remount 另一方（surface 的 content 状态不丢）。
  - 首次 Down 早于下一次布局：挂载后立即把目标节点 Frame 设为当前视口矩形，保证本轮 `HitTest`
    就能命中（空 Frame 会漏点）；`HitTestSelf` 不依赖 Frame，视口尺寸变化也不影响吞并。
- 生命周期：`TryBeginOutsidePress` → 关闭请求入队（`OutsidePress`）+ 挂载目标；Up/Cancel →
  `EndSwallowGesture()`（先路由 Up 再卸载目标、重算 hover）；窗口失活 → 关闭 + 结束吞并；
  安全阶段 `ValidateSwallowGesture()` 在目标失效（被卸载/宿主销毁）时结束吞并，不残留不可见输入拦截。
- `OverlayHostView` 命中纪律沿用 P-01：`IsHitTestVisible = (active || swallow)`、`HitTestSelf=false`。

### 2. 外点判定与锚豁免

- `InputRouter.DispatchPointer` 的 **Down 分支在任何 hover/press/capture 之前**调用
  `Overlay.TryBeginOutsidePress(HitTest(position))`：
  - 命中在 popup 子树内（`entry.PopupNode` 祖先链）→ 正常分发；
  - 命中在**当前锚子树**内（`entry.AnchorNode` 祖先链；P-03 起该节点是归一化后的锚子树根，
    ForEach 锚整体豁免）→ 正常分发、不关不吞；
  - 其余（含另一 popup/Select 的锚、scrollbar track、空白、`hit==null`）→ 外点：`Close(OutsidePress)`
    + 挂载 dismiss-target，本手势 Down/Move/Up/Cancel 只落在目标上。
- owner 的 `CloseOnOutsidePress=false` → 不判定外点、不关闭、不吞并（正常分发，P7 验证）。
- 各指针按钮一致（沿用既有 `OnPointerDown` 语义，不额外限定 Left），文档与测试一致。

### 3. Esc 与焦点恢复

- `DispatchKey` 顺序不变：先 `Route` 给 focused 节点（组合期取消 / 自处理 Esc 的控件先消费）；
  **仅未 handled && `Kind==Down` && `Key==Escape` && 存在 entry && `CloseOnEscape`** 时调用
  `Overlay.TryRequestEscapeClose(out restore)`：请求 `Close(Escape)`（owner 写回 false 一次），
  按 `restore` 执行 `SetFocus`，返回 handled；之后才是既有 Tab fallback。无 IME 特判。
- 恢复规则（`TryResolveRestoreFocus` 抽出，P-05 选后关闭复用同一内部机制）：
  `SavedFocus`（`IsMounted && !IsEffectivelyDisabled && IsFocusable`，且**不在即将卸载的 popup 子树内**）
  → 锚子树 DFS 首个可用 focusable → null。`SetFocus` 自身仍做最终校验，不会 restore 到死节点。
- 窗口失活**不**主动改焦（只关闭）；`OnNodeUnmounting` 的焦点清理路径不自动回锚。

### 4. 滚轮、Up-activation、capture

- 滚轮不参与吞并：`DispatchWheel` 若命中落在 dismiss-target 上，跳过它按同一位置在真实树重新命中
  （`HitTest(position, skip)` 新增私有重载）——弹层外滚轮照常滚内容、不关不吞（P11）。
- Up-activation 不需要新机制：trigger 的 Down 只标按下，Up 同点由控件自身回调写 binding；打开发生在本轮
  安全阶段，当前 gesture 仍按既有 `_captured` 原目标路由（Up 不会重放到新 popup 内容，P10）。
- 关闭时未结束 gesture 的 capture 由既有 `OnNodeUnmounting` 清理；吞并目标的卸载同样走该路径。

## 测试（`tests/CSharp.UI.Tests/PopupInputTests.cs`，12 个消费者用例）

| 用例 | 覆盖 |
|---|---|
| `P1_OutsidePress_ClosesPopupAndSwallowsWholeGesture` | 外点关闭（写回 false 恰一次）、吞并目标接管命中、entry 关闭后目标仍在、Up 不激活下层、下一手势正常 |
| `P2_OutsidePress_OnAnotherPopupAnchor_OnlyClosesTheFirst` | 另一锚=外点：只关旧、吞并；第二次按下才作用新锚 |
| `P3_OutsidePress_OnScrollbarTrack_ClosesWithoutStartingDrag` | 轨道按下关闭且 `ScrollOffsetY` 不变（含 Move 拖动） |
| `P4_AnchorSubtreePress_DoesNotCloseNorSwallow` | 锚内普通视图不关不吞；锚内 trigger 照常可达并 toggle |
| `P5_Escape_ClosesAndRestoresFocus` | Esc 关闭 + SavedFocus 恢复；SavedFocus 缺失 → 锚子树首 focusable |
| `P6_Escape_FocusedControlConsumesFirst` | 焦点控件先消费（弹层保持开）；无自处理控件时第二次 Esc 才关闭 |
| `P7_CloseOnOutsidePressFalse_DoesNotCloseAndPassesThrough` | flag=false：不关、不吞、下层正常收到点击、零写回 |
| `P8_CloseOnEscapeFalse_DoesNotCloseNorHandleEscape` | flag=false：Esc 不关、不写回 |
| `P9_DeactivationDuringSwallow_EndsGestureAndClearsInput` | 失活关闭 + 结束吞并，激活后新手势正常（无不可见拦截） |
| `P10_UpActivation_OpensOnUpWithoutLeakingIntoPopup` | Up 同点打开；弹层内容未被该 Up 激活 |
| `P11_WheelOutside_ScrollsBackground_WheelInside_ScrollsPopupContent` | 外滚轮滚背景且不关；内滚轮只滚弹层内容、背景不动 |
| `P12_CompositionOwnsFirstEscape_ThenExplicitCancelAndSecondEscapeCloses` | 真实 TextField 组合期：首次 Esc 由控件消费（弹层开、零写回、会话不变）；显式服务 Cancel 后第二次 Esc 关闭并恢复焦点 |

断言只用 HitTest 归属/IsMounted/Binding 写计数/FocusedNode/ScrollOffset/控件回调，不 pin
dismiss-target 类型、私有字段、子项数量或默认字面量。Core 计数 758 → **770**（+12）。

## 验证（原始 stdout/stderr 与真实 exit code；UTF-8）

| 命令 | 结果 | 证据（`2026-10-29-popup-select-p04-evidence/`） |
|---|---|---|
| `dotnet build CSharp.UI.slnx -c Debug --no-incremental` | 0 警告 0 错误，exit 0 | `p04-build.*` |
| `dotnet run --project tests/CSharp.UI.Tests -c Debug` | Total **769**, Failed 0，exit 0 | `p04-CSharp.UI.Tests.*` |
| `dotnet run --project tests/CSharp.UI.Integration.Tests -c Debug` | Total **56**, Failed 0，exit 0 | `p04-CSharp.UI.Integration.Tests.*` |
| 同上（含 throwaway 真实 Skia 输入 smoke） | Total **57**, Failed 0，exit 0 | `p04-input-smoke.*` |
| `dotnet run --project tests/CSharp.UI.Markdown.Tests -c Debug` | Total **50**, Failed 0，exit 0 | `p04-CSharp.UI.Markdown.Tests.*` |

- throwaway smoke（真实 Skia 文本 + headless runtime，`pixelScale=1.5`）：Up-activation 打开 → 外点
  Down 关闭且中段命中不再是下层按钮、Up 不触发下层、下一手势触发；Esc 关闭并把焦点恢复到 SavedFocus。
  运行后删除（`git status` 无残留），仅保留日志。
（下表为 post-anchor / 首轮 P-04 运行；组合期批次的最终计数见下一节。）

- 本轮 `InputRouter` 变更面：（Down 前置吞并判定、Up/Cancel 结束吞并、wheel 命中跳过 dismiss-target、
  Esc 拦截、HitTest 增加 skip 重载）。Core 全量含既有输入/焦点/滚动测试均通过。
- **OpenTK 套件不跑**：已知 `GalleryIdleAndBoundsTests.极窄视口…`（Markdown Copy 80px）为基线 preexisting，
  与本任务无关；P07 按已定最小 general fix（header 内 copy 按钮 `Flex(shrink:1,min:0)+Clip`）处理。

## 已知权衡 / 边界

- dismiss-target 是真实节点（满足“全屏命中/可捕/HitTest 归它”的验收），因此它必须由 manager 与
  surface 一起按 key 协调；关闭只摘 surface，不能连带摘目标。
- 目标 Frame 在挂载时先置为视口矩形（初 Down 立即命中），后续布局不再调整它；`HitTestSelf` 不读 Frame，
  视口缩放不影响吞并。
- 吞并期滚轮按“真实 hover”处理（跳过目标重新命中）；吞并手势进行中滚轮会滚到下层内容，这是刻意的
  “滚轮不吞并”语义。
- 外点关闭在安全阶段消费（不在 Down 内 reconcile）；因此 Down 之后、`ProcessPendingWork` 之前
  `HasActiveEntry` 仍为 true，测试以 `ProcessPendingWork` 返回后状态为准。
- 选后关闭的焦点恢复路径留给 P-05，复用 `TryResolveRestoreFocus` + 内部 `Close`，不引入过期全局状态。

## 返修批次：组合期 Esc 真实回归 + 宿主子项单次快照（2026-10-29 post-composition）

主审读 P6（合成 `EscSinkView`）后指出它不等于计划的 composition case，并要求把宿主子项构建从
`new List<ChildDescriptor>(2)`（list + backing array 两份分配）改成一份不可变快照。

- **新增 1 个消费者永久回归** `P12_CompositionOwnsFirstEscape_ThenExplicitCancelAndSecondEscapeCloses`
  （`PopupInputTests`）：真实 `TextField` 在打开的 `Popup` 内获焦，用注入的输入服务替身投递
  `CompositionEvent.Start`（模拟平台消息）→
  首次 `Escape`：**由组合中的 TextField 自己消费**（`TextField.HandleKeyDown` 的组合分支），
  弹层保持打开、只读绑定写入 0 次、`service.StartCount` 仍为 1、活动客户端实例不变；
  随后**显式** `service.RequestCancel()`（注释写明这是模拟 IME/系统取消响应，**不是框架自 Cancel**），
  第二次 `Escape` 才关闭弹层、owner 写回 false 一次、焦点恢复到 `SavedFocus`。
  本测试不代表原生 IME 实测，也没有引入新 API/假后端回显。
- **宿主子项改为单次不可变快照**：`Reconciler.ReconcileDescriptors`/`ValidateKeys` 参数放宽为
  `IReadOnlyList<ChildDescriptor>`（只读使用，无行为变化）；`OverlayManager.ReconcileHostChildren`
  按 0/1/2 项传一份数组/集合表达式（空用 `[]`，即 `Array.Empty`），不再使用可变 `List` 缓冲——
  协调期间回调触发的 EndSwallow/失活请求不会影响正在枚举的快照（保持 P-01/P-04 的重入安全）。
  未加任何池化/重入缓冲池。
- **`Popup` XML**：`CloseOnOutsidePress`/`CloseOnEscape` 注释改为描述已实现行为，去掉“P-04 未来实现”
  之类的阶段措辞（最终阶段语句在 P-07 统一清理）。

| 命令 | 结果 | 证据（`2026-10-29-popup-select-p04-post-composition-evidence/`） |
|---|---|---|
| `dotnet build CSharp.UI.slnx -c Debug --no-incremental` | 0 警告 0 错误，exit 0 | `p04-post-composition-build.*` |
| `dotnet run --project tests/CSharp.UI.Tests -c Debug` | Total **770**, Failed 0，exit 0（769 + 1） | `p04-post-composition-CSharp.UI.Tests.*` |
| `dotnet run --project tests/CSharp.UI.Integration.Tests -c Debug` | Total **56**, Failed 0，exit 0 | `p04-post-composition-CSharp.UI.Integration.Tests.*` |
| `dotnet run --project tests/CSharp.UI.Markdown.Tests -c Debug` | Total **50**, Failed 0，exit 0 | `p04-post-composition-CSharp.UI.Markdown.Tests.*` |

真实 Skia 输入 smoke 沿用上一批的 `2026-10-29-popup-select-p04-evidence/p04-input-smoke.*`
（本次源码变更为分配/签名与文档，不改输入行为，未重复跑大套件）。

## 文档与任务状态

- `docs/task/04-popup-input.md`：更新状态为“实施完成待复审”；按最终 plan 更正两句过时摘要——
  宿主命中纪律改为 `IsHitTestVisible = (entry || swallow)` + 恒 `HitTestSelf=false`；
  锚身份改为 `entry.AnchorNode` = 真实锚子树根（ForEach 在 P-03 已归一化），不是 `Popup` 包装节点。
- `docs/task/README.md`：勾选 P-02（主审 read 接受）与 P-03（主审通过 + 独立 probe M1..M14），
  P-04 保持未勾选并注明“实施完成待复审”；顶层状态行同步。

## Freeze

P-04 源码/测试/文档已冻结（含组合期返修批次）：**停止 build/edit**。独立 probe 已全量 PASS（见下方复审结果），
本批次只补了主审要求的真实 TextField 组合期回归与宿主子项单次快照；等主审 read 关键测试/array 接口后
gate P-05。若不通过则在 `docs/return/` 继续新增返修记录。

## 复审结果（append-only，2026-10-29）

- **独立 probe（fast）P-04 全量 PASS，主审 348B 实读 D1–D10 有效**：真实外点 Down 写回恰一次 + 底层
  click 0/下一次 1、flags=false 外点照常派发且不关、另一 popup 锚为外点且第二次手势才生效、scrollbar track
  外点不滚动且下一次 thumb 拖动正常、锚子树豁免、Esc 回到 SavedFocus 且自处理控件先消费、失活结束吞并后
  输入恢复、mid-gesture capture 不重路由、dismiss-target 无残留、外部 wheel 保持弹层可见。
- 首个 probe 之后的唯一必要返修就是本节上方的“组合期 Esc 真实回归 + 宿主子项单次快照”批次；
  未重写输入状态设计，独立 PASS 路径不变。
- 本记录为 P-04 评审文件，**不是** P-07 最终交付记录；验收勾选/交付文档在 P-07 统一生成。
