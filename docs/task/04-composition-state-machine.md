# TI-04：组合会话与预编辑状态机

创建日期：2026-10-17

状态：已完成，Codex独立复审通过（2026-10-18；[审核及返修记录](../return/2026-10-18-ti04-composition-review.md)）

阶段：B

设计依据：[文本输入与编辑基础计划](../plan/2026-10-17-text-input-and-editing-plan.md)（接口冻结前均为设计草案）。

[返回任务索引](README.md)

## 目标与范围

预编辑不写Binding，分段结果只确认一次，过时或结束的会话不能串写新节点。

边界：核心状态机和模拟服务；原生消息接入由TI-07完成。

## 前置依赖

[TI-03：统一编辑事务与 Binding 同步](03-atomic-editing-and-binding.md)均须Codex复审通过后再实施；使用前置任务已冻结契约。

## 实施清单与接口接入

- [x] 实现Start/Update/Result/Cancel及重复End幂等，保留每段原替换区/选区、显示串到文档映射。
- [x] 处理结果+下一段预编辑同一事件批次、分段提交后重新定位；取消仅丢弃未提交段。
- [x] 实现焦点/禁用/卸载/窗口失活/外部绑定变更等取消规则和旧epoch事件隔离；同节点布局不取消。
- [x] 定义组合期间编辑命令、鼠标改选区、复制及Tab/Enter/Escape所有权接口，不能在unsafe回调里递归布局。

## 测试与验收

- [x] 长预编辑不写绑定、结果按MaxLength完整字素接受、一次结果只产生一次事务。
- [x] 结果/结束乱序、重复/过时事件、无Start的合法平台序列、分段结果及切焦点同步回调。
- [x] 外部修改/来源替换/真正卸载清理，Responsive重建与Adaptive保持场景按各自语义验收。

## 实施记录与审核门槛

实施时记录实际签名、涉及模块、命令/退出码/测试计数、证据路径及平台边界。DSH自测通过后标“待Codex复审”；Codex审核问题写docs/return带日期文件，DSH返修至通过后才英文提交并推送。状态改“已完成”须有Codex复审结论，不在Build/Measure中执行外部副作用。

本轮不修改既有archive文件；关键证据不只放可清理artifacts。后续交付记录由Codex创建，当前不预先标记验收通过。


## 2026-10-18 实施边界补充（Codex）

- 本任务实现核心真实TextField客户端、组合状态与焦点/生命周期接入，以模拟输入服务驱动实际运行时测试。TI06负责预编辑绘制与几何，TI07负责原生桥；本任务不声称已可在真实输入法中看到预编辑。
- 焦点epoch由运行时统一分配，同一运行时的不同节点不能各自复用epoch0。结束旧归属先使token失效，再通知服务；服务同步回调/延迟取消不得影响新客户端。
- 组合ID在epoch内单调递增，序号在同一组合内跨消息递增；使用有界游标/单调水位拒绝重复，不保留无界已结束ID集合，也不只保留最近若干ID后放过旧重放。
- Result带非null NextPreedit时，先用真实TI03事务确认当前段，再在相同组合ID的下一段重新锚定实际文档和选区，继续使用递增序号；Cancel只取消此未提交段。无NextPreedit的Result结束活动段，随后End/Cancel幂等。EndsComposition表示当前段结束，不能据此无条件丢弃NextPreedit。真实平台后续段若由独立Start描述，使用新的递增组合ID。
- 文档版本以真实回读为准，拒绝/净化后无变化/MaxLength全拒绝不预设版本+1；每个结果事件只执行一次。空或净化后为空的结果不能误删原选区。批次允许已处理前缀，后续拒绝时返回真实已执行动作和明确拒绝，不谎报整批未处理。
- 输入服务为null/仅提交文本时保持原路径。原生按键归属以明确输入token/处理结果表达（包括组合刚结束后同一按键），不按文本或时间窗猜测。只提供本阶段最小接口及模拟接入，原生消息去重与窗口退出路由联调留TI07。
- 用户免除微软拼音测试的范围继续有效；本任务不安装、启用或测试它，不更改系统输入法配置。

## 2026-10-18 DSH实施记录（待Codex复审）

### 实际接入（核心，无原生句柄/图形依赖）

- `internal sealed class TextFieldComposition`（`src/CSharp.UI/Editing/TextFieldComposition.cs`）：Start/Update/Result/Cancel/End 状态机；每段冻结替换区/组合前选区/预编辑/caret/不可变分段；显示串 `前缀+预编辑+后缀` 与显示→文档映射。
- `internal sealed class TextFieldCompositionClient : ITextInputClient`（`TextFieldCompositionClient.cs`）：真实节点客户端；epoch 在创建时固化，释放后只返回旧 epoch 与显式拒绝。
- `internal sealed class TextInputServiceSession` + `TextInputFocusLease`（`TextInputServiceSession.cs`）：运行时唯一接线，Start/End 代际号保护，按所有者结束，延迟通知。
- `TextField`/`TextFieldState`/`SyncEditor` 接入焦点、禁用、卸载、外部绑定/MaxLength、鼠标/命令、窗口激活接缝；`UiRuntime` 增加 `NotifyWindowActivation`、`NotifyCompositionKeyOrigin`、单调 epoch（不保留租约列表）、安全阶段 `FlushPendingNotifications`。
- 公开面只新增 `TextInputApplyResult.AppliedEventCount` 与 `HasRejection`；`TextInputEvent`/既有构造函数/仅提交文本回退不变。

### 返修决策（对应R1–R6）

- R1：下一段用真实 `EditResult.After.Selection.Range` 重基（归一化后也不会跳过实际 caret），已提交前缀/未选中后缀保留；Cancel 仅撤销未提交段并恢复组合前有方向选区。
- R2：无 Start 的 Result 精确要求 `e.DocumentVersion == 真实版本`，使用事件显式 `e.Replacement`，不再按最新 caret 猜测。
- R3：组合 ID 严格递增、允许跳号；`≤已见水位` 一律拒绝；删除16项环形缓冲。Cancel/End 校验 ID/活动段/序号/版本，仅“最近结束的同一 ID”幂等。所有校验先于状态提交，无效 Start 不消耗 ID。
- R4：空范围空结果=消费但不报 `ResultCommitted`；非空替换区的空/净化后空结果拒绝且绝不删除选区；容量/setter 拒绝只消费并退休序号。批次返回真实前缀动作数，`Handled = 已消费事件数 > 0`（部分批次不再谎报整批未处理）。
- R5：`OnUpdate` 只在真实外部变化时取消；布局/绘制只做本地取消并延迟通知平台；会话按所有者结束，旧节点卸载不影响新字段；重新聚焦创建新客户端/新 epoch；Start/End 重入由代际号保护；服务异常本地收敛并记录脱敏诊断。
- R6：每个事件验证前从真实 Binding 回读同步；实时回调不调用 `GetLayout`/`Frame`/待办；组合不调用 `ConsumeInvalidation()`（历史失效位保留），新增 `TextEditSession.ConsumeCompositionInvalidation` 供后续按位消费。

### 键盘归属

组合期间非命令按键（方向/Backspace/Delete/空格/Enter/Escape/Tab）由输入法消费；命令快捷键继续走既有编辑命令（Copy 只读已提交选区，Cut/Paste/SelectAll 先取消）。`ITextInputService` 之外的最小原生接缝是 `UiRuntime.NotifyCompositionKeyOrigin(token, consumed)`：显式 token 一次性吞掉“组合在同一按键内结束”的 KeyDown，不按文本/时间窗去重。

### 命令、退出码与计数（2026-10-18，Windows/.NET 10.0.303）

- `dotnet build CSharp.UI.slnx --no-restore -m:1 -nodeReuse:false` → 退出码 0（0 error）。
- 核心：`tests\CSharp.UI.Tests\bin\Debug\net10.0\CSharp.UI.Tests.exe` → 退出码 0，`Total: 451, Errors: 0, Failed: 0, Skipped: 0`（405 既有 + 46 新增，既有测试全部保留）。
- Text：`CSharp.UI.Text.Tests.exe` → `Total: 67, Errors: 0, Failed: 0`。
- Integration：`CSharp.UI.Integration.Tests.exe` → `Total: 30, Errors: 0, Failed: 0`。
- 证据：`artifacts/ime-work/ti04-core/`（`build-final.txt`、`core-final-debug.txt`、`text-final.txt`、`integration-final.txt`，含返修过程日志）。OpenTK 原生窗口/剪贴板测试本轮未运行（无GUI环境），不据此声称通过。

### 边界与未验证项

- 预编辑绘制/下划线/活动段几何属TI06；本任务只提供显示串与显示→文档映射，不声称界面已显示预编辑。
- 原生消息桥、Escape 退出窗口路由、候选锚点发布属TI07；核心只提供 `NotifyWindowActivation`/`NotifyCompositionKeyOrigin`/几何占位（`PublishGeometry` 返回 false）。
- 微软拼音仍未启用/未测试；未修改系统输入法配置、GUI 或 OpenTK 连接逻辑（仅窗口焦点回调转调 `NotifyWindowActivation`）。
- 本记录不标记验收通过；提交/推送等待Codex明确复审结论。

### 2026-10-18 第二轮返修记录（待Codex复审）

- 处理复审附录（r5）的两处生命周期边界：`FocusManager.SetFocus` 的焦点代际检查放在 `ReleaseTextInputOwnershipFor` 返回之后、`previous.OnFocusChanged(false)` 之前，并在该回调返回后再次检查；`OnNodeUnmounting` 同样保留重入建立的新焦点。窗口失活期间允许逻辑焦点更新，但 `Start`/`Resume`/`Rearm` 不得在重新激活前启动会话。
- 同步补充：Cancel/End 精确校验活动段文档版本；无 Start 空结果续段保留显式范围；绑定 getter 持续抛错返回拒绝并使用最后可靠快照；外部绑定观察立即撤销旧 epoch；组合回调成功/错误路径只用核心字素规则、不执行排版；公开 `DispatchKey(e, token)` 携带同一次按键的显式身份。
- 核心：`CSharp.UI.Tests.exe` → `Total: 467, Errors: 0, Failed: 0`（405 既有 + 46 首轮 + 16 本轮 TI04 回归）；Text 67、Integration 30 均 0 失败。
- 证据：`artifacts/ime-work/ti04-core/`（`dsh-build-final2.log`、`dsh-core-r5e.log`、`dsh-CSharp.UI.Text.Tests-r5e.log`、`dsh-CSharp.UI.Integration.Tests-r5e.log`，含返修过程日志）。未修改任何 return/evidence 审核文件，未提交/未推送。

## Codex最终验收（2026-10-18）

独立复审通过；[审核记录与持久证据](../return/2026-10-18-ti04-composition-review.md)记录全部返修及最终命令。缓存解决方案构建0警告/0错误，核心467、Text67、Integration30、OpenTK242合计806项全部通过，另有真实UiRuntime客户端独立验收41项通过。OpenTK测试实际运行了Windows窗口、OpenGL和剪贴板；微软拼音按用户要求免测。

最终公开按键接缝为NotifyCompositionKeyOrigin(token, consumed)配合DispatchKey(KeyEvent, object? nativeKeyOriginToken)，同一次按键传相同token；原有DispatchKey(KeyEvent)保持兼容。历史DSH自测记录中的“原生测试未运行”仅描述其自测阶段，最终兼容性测试由Codex执行并通过。

TI-04完成不表示已有真实预编辑绘制或原生输入法支持；后续仍按TI-05～TI-10推进。

实现提交：`af995d85dac292b2f676a278721af1dfb3479425`，已推送至远程main；凭据助手的沙箱限制及宿主推送过程见审核记录。
