# TI-05：有界撤销重做与编辑命令

创建日期：2026-10-17

状态：已完成，Codex独立复审通过（2026-10-19；[审核与验收记录](../return/2026-10-19-ti05-undo-redo-review.md)）

阶段：C

设计依据：[文本输入与编辑基础计划](../plan/2026-10-17-text-input-and-editing-plan.md)（接口冻结前均为设计草案）。

[返回任务索引](README.md)

## 目标与范围

撤销重做恢复准确文本和选区，历史不会无限增长或记录预编辑更新。

边界：统一命令与内部容量配置，不引入全局应用历史。

## 前置依赖

[TI-04：组合会话与预编辑状态机](04-composition-state-machine.md)均须Codex复审通过后再实施；使用前置任务已冻结契约。

## 实施清单与接口接入

- [x] 实现每节点undo/redo栈、100条及1,048,576 UTF-16保留量双上限、完整事务淘汰和超大单项策略。
- [x] 实现500ms单调时钟合并，仅合并同源相邻键入/同向删除；剪贴板/组合结果/选区/焦点/外部更新打断。
- [x] Undo/Redo前取消预编辑，再经统一事务写Binding，恢复前后选区方向并滚动可见；新编辑清redo。
- [x] 添加平台正确快捷键映射，保留AltGr和IME优先级；宿主最终路由在TI-07接通。

## 测试与验收

- [x] 注入时钟验证合并边界而非sleep；一段IME结果一步撤销、分段结果各自可撤销。
- [x] 空栈、redo分叉、容量淘汰/大粘贴、setter异常/拒绝/转换、外部Binding更新与来源变更。
- [x] Adaptive/Flow重排保留历史，Responsive真正卸载不迁移历史；普通字符无回归。

## 实施记录与审核门槛

实施时记录实际签名、涉及模块、命令/退出码/测试计数、证据路径及平台边界。DSH自测通过后标“待Codex复审”；Codex审核问题写docs/return带日期文件，DSH返修至通过后才英文提交并推送。状态改“已完成”须有Codex复审结论，不在Build/Measure中执行外部副作用。

本轮不修改既有archive文件；关键证据不只放可清理artifacts。后续交付记录由Codex创建，当前不预先标记验收通过。

## 2026-10-19 实施语义补充（Codex）

本任务在已完成TI-03原子事务、TI-04真实TextField客户端之上实现；不提前实现TI-06绘制、TI-07原生桥或全局历史。使用既有IUiClock.Now注入时间，核心不得依赖OpenTK/Skia/原生平台类型，不为测试扩张公共编辑器API。

### 历史内容与内存预算

- 每个运行时节点一份历史；undo与redo合计最多100项，保留的删除片段与插入片段UTF-16长度之和最多1,048,576。移动栈只转移同一项，不重复计数。内部可配置较小容量便于测试；0容量禁用记录，不禁用编辑。
- 持久历史保存替换起点、实际删除/插入片段、前后有方向选区和必要身份/分组信息；不要隐藏保存整份Before/After文档快照，使预算失去意义。当前编辑器文档不计入历史，但历史持有的文本都必须计入。
- 历史逆操作的范围必须在编辑前后两份文档都具有合法字素边界；不能假定请求插入范围在提交后仍是字素边界。不同时间分别输入`e`与组合音标、emoji与ZWJ续接等会跨事务形成字素，必要时扩大保存的受影响片段并计入预算，Undo仍经统一事务恢复原文，不能拆字或静默丢失撤销能力。
- 超限淘汰最老完整事务。单次结果的片段总量超过预算时仍执行编辑，但清空undo/redo且不保存该项；不能保留跨越不可达文档的旧历史。合并将超限时允许停止合并、按完整事务入栈/淘汰，不能留下部分可撤销字符。计数需防整数溢出。

### 记录、合并与失效

- 只在真实事务成功、实际回读与请求候选一致、节点仍挂载且来源/MaxLength一致时记录；按真实接受的完整字素片段记录容量截断，不按原始请求猜测。
- 500ms按相邻两次成功编辑之间的单调时间间隔计算，0～500ms包含边界；负间隔或超过500ms不合并。仅同来源、同类型且范围连续、前后选区连续的折叠光标普通键入/同向逐字素删除可合并。选区替换独立成项；Backward/Forward删除不互相合并。
- 光标/选区操作（即使移动后又回原处）、指针改选区、焦点/窗口激活变化、剪贴板命令、Start/Update/Result/Cancel组合阶段、Undo/Redo及外部变化都打断合并。Copy不写历史、不取消预编辑，但仍构成分组边界。布局/同来源重建/Flow/Adaptive重排不打断或清历史；真正卸载清理。
- 预编辑不入栈；每个真实组合Result独立一项，分段提交分别撤销；取消只丢弃尚未提交段。同值回读不能每帧清历史。历史失效位必须独立消费，不清除组合消费者的标志。
- 外部文本、来源、MaxLength变化清空两栈。setter转换、setter明确拒绝写入、setter/getter异常或事务重入换源/卸载时按实际回读同步并清理不可信历史，不记录错误逆操作。容量全拒绝、空操作、净化后为空不新增历史，也不凭空清redo；不能用诊断字符串判断失败种类。

### Undo/Redo及统一命令

- 统一内部派发Undo/Redo/Cut/Copy/Paste/SelectAll，优先复用既有TextField入口。Undo/Redo先取消预编辑，即使栈为空也如此；空栈不调用setter、不推进版本、不制造事务。
- 逆向/正向操作均通过既有TextEditSession.Apply，一次写Binding并真实回读；成功后才移动栈并恢复前/后选区方向及caret可见。版本保持单调，不能把历史快照版本倒回去；撤销/重做本身不作为新普通历史项入栈。实际接受的新编辑才清redo。
- 文档不再符合历史上下文时拒绝旧记录并清理；setter拒绝/转换/抛错不得假装已撤销或恢复错误选区。setter中同步重建、换绑定/MaxLength、切焦点、卸载不能串写其他字段或复活旧历史。保留TI-04原生组合回调不排版、不调用平台服务的边界；历史记录只是核心内存操作。
- Windows/Linux使用Ctrl+Z、Ctrl+Y、Ctrl+Shift+Z；macOS使用Command+Z、Command+Shift+Z（Command+Y不作本任务Redo）。统一剪贴板命令同样遵守平台Control/Command；Alt/AltGr、错误平台Meta/Control以及不支持的组合不能误触命令。CapsLock不改变含义。
- 显式IME已消费的key token始终优先，包括带命令修饰符的按键及组合结束后同一KeyDown。没有被IME声明消费的Undo/Redo/Cut/Paste/SelectAll仍按既定策略先取消预编辑再执行。原生宿主最后路由留TI-07，不安装、启用或测试微软拼音，也不改系统输入法配置。

### 本轮复审要求

用真实UiRuntime+TextField+注入时钟/输入服务验证，而非只测独立历史helper：中英文、代理对/组合字符/ZWJ、方向选区、分段IME与取消、500ms边界、移动后返回、剪贴板边界、redo分叉、100项及文本预算淘汰、超大编辑、合并跨预算、setter拒绝/转换/写后抛错、外部变化与同步重入、Responsive卸载/Adaptive保留、平台快捷键与AltGr/IME token。跨平台映射可用内部策略注入或纯解析器测试；不得将模拟macOS/Linux记作真机验收。

启动基线：TI-04后四套测试806项（核心467、Text67、Integration30、OpenTK242），独立运行时41项已通过。保留原有测试；新增测试与实现同步，自测后等待Codex独立复审，未经批准不提交实现。关键验收证据最终保存到docs/return的带日期审核文件及同名-evidence目录，不只放artifacts。

实施中独立检查已发现实际失败，完成前须阅读并返修[2026-10-19 TI-05审核记录](../return/2026-10-19-ti05-undo-redo-review.md)全部问题及后续补充；最终以Codex复测为准，不仅以DSH自测计数放行。

## DSH 实施记录（2026-10-19，待Codex复审）

### 变更模块与内部签名

未新增公共编辑器 API；以下类型/成员均为 `internal`（测试程序集经既有 `InternalsVisibleTo` 观察）。

- 新增 `src/CSharp.UI/Editing/TextEditHistory.cs`：
  - `TextEditPlatformKind`（Windows/Linux/MacOS）、`TextEditCommand`（Undo/Redo/Cut/Copy/Paste/SelectAll）。
  - `TextEditCommandResolver.DefaultPlatform` / `Resolve(KeyEvent, TextEditPlatformKind)`：Win/Linux 仅 Ctrl+Z/Ctrl+Y/Ctrl+Shift+Z，macOS 仅 Cmd+Z/Cmd+Shift+Z；错误平台主修饰键、Alt/AltGr（Ctrl+Alt）、多余修饰键不执行；CapsLock 先剔除；不使用 `KeyEvent.IsCommandPressed` 的 Control∪Meta 并集。
  - `TextHistoryCapacity(int MaxEntries, long MaxTextLength)`：`Default=(100, 1_048_576)`、`Disabled=(0,0)`；任一项为 0 即禁用记录。
  - `TextEditMergeKind`（None/TypingAppend/BackspaceDelete/ForwardDelete）。
  - `TextHistoryEntry`：`ReplacementStart`、`Deleted`、`Inserted`、`SelectionBefore`、`SelectionAfter`、`Kind`、`MergeKind`、`FragmentLength`；不持有整份文档快照。
  - `TextEditHistory`：`Record(entry, now)`、`CommitUndo/CommitRedo`、`BreakMerge`、`Clear`、`UndoPeek/RedoPeek`、`UndoCount/RedoCount/RetainedTextLength`；双上限整事务淘汰、超大单项清栈、合并超限停止合并、`long` 计数。
- `Editing/TextEditSession.cs`：新增 `TextEditTransactionOutcome` 与逐事务 `LastTransaction`、`LastTransactionSawExternalChange`、`ConsumeHistoryInvalidation()`；`Apply` 在每条返回/异常路径记录结构化结论（Applied/Normalized/SetterRefused/NoChange/Rejected/WriteFailed/ReadFailed）。
- `Views/TextField.cs`：
  - `TextFieldState.History`（每节点一份，随节点状态卸载销毁）。
  - `ApplyEdit`/`ApplyCompositionCommit`：仅在精确确认、仍挂载、来源/MaxLength 一致时 `Record`；规范化/setter 拒绝/写读异常/外部变化清空两栈；容量拒绝/空操作不新增、不清 redo。
  - `BuildHistoryEntry`：跨事务字素安全差量（前后双文档共同安全边界，见 R1）。
  - `ReplayHistory`：空栈无副作用；先同步取消预编辑；同一 `TextEditSession.Apply` 写一次/回读；仅 `Applied` 移动栈并恢复 `SelectionBefore/After` 方向选区与 caret 可见；重放不记录新历史；上下文不符/setter 拒绝/异常清栈。
  - `OnKeyDown`→`HandleKeyDown`：显式 IME 消费 token（含命令修饰键、组合结束同 KeyDown）优先；未消费命令先取消预编辑再执行；命令路径读取/事务异常清历史并沿用既有上抛契约。
  - 指针按下/拖动/释放、方向/Home/End、焦点变化、窗口失活、全部组合阶段、剪贴板命令（含 Copy）、Undo/Redo 均为合并边界；布局/同来源重建/Flow/Adaptive 不打断。
- `Editing/TextInputContracts.cs`：新增 `TextGraphemeBoundaries.EnumerateBoundaries`（一次枚举字素边界，供前后双文档共同边界查找，避免循环内反复整段扫描）。
- `Editing/TextFieldCompositionClient.cs`：组合批次 `BreakMerge`；`IsExplicitlyConsumedKey`；`CurrentDocument`/`ApplyValidated` 读/写异常清历史。
- `Editing/TextFieldKeyOwnership.cs`：`IsExplicitlyConsumed(token)`（一次性、不把“正在组合”当显式消费）。
- `Runtime/UiRuntime.cs`：`UiRuntimeServices.HistoryCapacity`/`EditPlatform`（内部注入）、`UiRuntime.EditPlatform`、窗口失活打断合并（不清历史）。
- 测试：`tests/CSharp.UI.Tests/TextFieldUndoRedoTests.cs`（新增 62 项）；`TestApp` 增加可选容量/平台注入。

### 复审 R1–R3 返修（R1 两轮）

- **R1 跨事务字素（两轮）**：`BuildHistoryEntry` 不再只扩到编辑后边界再映射，也不再回退保存已知非法范围。它在**前后两份文档**中寻找共同合法边界：前缀偏移直接对应、后缀偏移按文档长度差对应；起点向左、终点向右取最近的共同安全边界（文档两端永远合法，保证终止）。每份文档只枚举一次边界后用二分/集合查询，避免循环内反复整段扫描。撤销/重做片段取同一局部区间的对应文本并计入预算；扩展项不参与合并。覆盖组合附加符 U+0301（超时与 100ms 合并窗口）、分段 ZWJ 序列，以及区域指示符在**前缀插入、中间插入、删除导致重新配对**三类双文档边界场景的逐步撤销/重做、选区与版本断言。
- **R2 Undo/Redo setter 拒绝**：按 `LastTransaction=SetterRefused` 在 `!result.Changed` 分支清空两栈；撤销与重做双向拒绝后再次命令不写绑定、不移动栈、不恢复错误选区。
- **R3 事务外读取失败**：`SyncEditor`、组合客户端 `Document/GetState`（`CurrentDocument`）、`ApplyValidated`、`SyncCompositionDocument`、`SyncCompositionContext` 以及键盘命令路径的读取异常都实际清空历史；普通 UI 入口保持既有上抛契约，原生组合回调仍不外抛。历史清理由 `History.Clear()` 完成，不消费/不清除组合失效位。
- 历史失效接缝结论（按复审意见）：结构化逐事务结论 + `Sync` 返回的外部变化标志 + 各真实读取入口的显式失败清理已由独立检查覆盖，是**可接受**的实现；不为了“消费”粘性 `HistoryInvalidationRequested` 而改动 TI-03/TI-04 语义。`ConsumeHistoryInvalidation()` 继续与组合消费相互独立，未清除组合消费者标志，也未破坏 TI-04 基线。

### 构建、命令与测试计数

| 命令 | 退出码 | 结果 |
|---|---|---|
| `dotnet build CSharp.UI.slnx --no-restore -m:1 -nodeReuse:false -v:minimal` | 0 | 0 错误；仅既有警告（CompositionContracts.cs 的 CS8601 与既有 xUnit 分析器警告），新增代码无警告 |
| `dotnet run --project tests/CSharp.UI.Tests -c Debug --no-build -- -class CSharp.UI.Tests.TextFieldUndoRedoTests` | 0 | 62 通过，0 失败/跳过 |
| `dotnet run --project tests/CSharp.UI.Tests -c Debug --no-build` | 0 | 529 通过，0 失败/跳过（467 基线 + 62 TI-05） |
| `dotnet run --project tests/CSharp.UI.Text.Tests -c Debug --no-build` | — | 本轮未复跑（改动仅核心编辑/字素边界；上轮 67 通过，Codex 最终四套） |
| `dotnet run --project tests/CSharp.UI.Integration.Tests -c Debug --no-build` | — | 本轮未复跑（同上；上轮 30 通过） |

TI-05 前基线 806 项（核心467/Text67/Integration30/OpenTK242）全部保留；含 OpenTK 的预计总数为 868（本轮未重复运行原生 OpenTK/Text/Integration 套件，由 Codex 最终执行）。证据：本轮 `artifacts/ime-work/ti05/dsh-r3r2-build.log`、`dsh-r3r2-ti05.log`、`dsh-r3r2-core.log`；上一轮 `dsh-review-r1r3-*.log`；首轮 `dsh-*`。持久验收证据按任务要求由 Codex 维护到 `docs/return` 同名 `-evidence` 目录。

### 边界与未验证项

- macOS/Linux 快捷键仅通过注入平台的核心测试验证，**不是**真机验收；原生宿主最终键盘路由属 TI-07，本轮未改 OpenTK 输入适配。
- 未安装、启用、测试微软拼音或其他真实 IME，未修改系统输入法配置，未做 GUI/截图改动；模拟组合服务不代替真机 IME 验收。
- 未提交、未推送；未创建独立审计克隆或审计工程；等待 Codex 独立复测与批准后按工作流提交。
- 历史内存/记账有界（项数 + UTF-16 片段预算，`long` 计数）；差量构造每份文档只做一次核心 `StringInfo` 边界枚举，再以二分/集合查找前后共同安全边界，未引入整份文档历史快照；真正大跨度受影响时按既有超大项策略清栈。
- 复审失败用例已用**新增真实 TextField 回归**复现并修复：`🇦🇧` + Home + 输入 `🇽` → `🇽🇦🇧`，Undo 恢复 `🇦🇧`、Redo 恢复 `🇽🇦🇧`；另有中间插入与删除重配对用例（`dsh-r3r2-ti05.log`）。不以旧测试通过代替该用例。

## Codex最终验收（2026-10-19）

独立复审通过，[审核与持久证据](../return/2026-10-19-ti05-undo-redo-review.md)记录全部问题和返修。缓存构建0警告/0错误；核心529、Text67、Integration30、OpenTK242，共868项工程测试全部通过；另有TI-05独立运行时49项、TI-04兼容性41项全部通过。OpenTK实际运行Windows窗口、OpenGL及剪贴板，历史DSH记录中的“未运行”仅描述其自测阶段，不能代替本最终结果。

有界历史、时间/命令分组、方向选区恢复、Unicode跨事务字素、分段IME、Binding失败/失效、响应式节点生命周期、平台快捷键及IME token优先级均按本任务范围验收。历史标志的结构化失效取舍见审核结论。微软拼音继续免测；TI-06～TI-10按依赖继续，不宣称整个输入法计划交付。

实现提交：`e4a766bed253f30e2eeaa0c850e0aa704102ecc7`，已推送至远程main；实际提交与宿主推送过程见审核记录。
