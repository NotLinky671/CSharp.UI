# TI-02：冻结编辑与文本输入契约

创建日期：2026-10-17

状态：已完成（Codex独立复审通过，2026-10-17；DSH提交6d77e9b，已推送main）

阶段：B

设计依据：[文本输入与编辑基础计划](../plan/2026-10-17-text-input-and-editing-plan.md)（TI-02 已将草案细化为下述实际签名）。

返修依据：[TI-02 编辑与输入契约首轮审核](../archive/2026-10-23-completed-reviews/2026-10-17-ti02-contract-review.md)（R1–R6 与“第二轮独立复核”）。

[返回任务索引](README.md)

## 目标与范围

冻结平台无关数据、焦点会话和宿主处理结果，保留现有应用API。

边界：契约和值类型、服务替身；不提前实现平台消息桥，也不在本任务实现状态机。编辑事务执行、组合状态机、历史淘汰、预编辑绘制与候选几何仍分别属于 TI-03～TI-06。

### 本轮返修范围决定（R1/R2 复核）

Codex 决定：TI-02 移除过早公开的 `CompositionCursor` / `CompositionBatchValidator` / `CompositionBatchReport`，把实际状态判定归 TI-04。原因：

- 版本推进依赖真实事务结果——TI-03 的绑定可能拒绝写入、净化后无变化或被 MaxLength 全部拒绝，这些情况不推进版本；契约层如果预判“每个 Result 版本 +1”，会制造错误锚点。
- 用有界“最近已结束组合 ID”列表做重复抑制，会在淘汰后放过旧 ID 重放，不能作为冻结的正确性。

因此本任务只保留：**组合事件值 + 不可变载荷校验 + 简单会话身份 + 已文档化的顺序/`Handled` 契约**。`src/CSharp.UI/Editing/` 中**不存在**任何跨消息状态机、游标、判定报告或 ended-ID 集合。

## 前置依赖

[TI-01：平台可行性与 Windows 原型](01-platform-feasibility.md)接入路线已通过Codex审核。按2026-10-17用户修订，微软拼音不测试；原生候选与取消验收移交TI-07/TI-10，不阻塞平台无关契约实施。本轮未安装、启用、探测或测试微软拼音，未运行GUI，未修改系统输入法设置。

## 实施清单与接口接入

- [x] 定义选区方向、UTF-16范围与字素处理、版本锚点、EditOperation/Result、组合事件与明确事件顺序契约；与计划第5至7节一致。
- [x] 设计ITextInputClient/Service、分项能力查询、默认已提交文本回退及依赖注入；原生类型不进入核心。
- [x] 给文本会话建立焦点epoch/组合ID/版本与同步Handled结果契约，文档化取消与过时数据处理责任（实际状态机属TI-04）。
- [x] 冻结Binding来源身份、setter回读、MaxLength与历史配置契约；仅冻结，未提前实现TI-03/TI-05，也不写“参考模型公式”冒充实现验收。

## 测试与验收

- [x] 核心程序集无OpenTK/Skia/平台依赖（`CSharp.UI.deps.json` 无外部/原生条目）；旧TextInputEvent/TextField构造用法编译不变。
- [x] 范围越界、负值、未知枚举、版本锚点不匹配、不可拆代理项、空预编辑、无能力服务、无归属批次的回退行为有明确可测行为。
- [x] 记录实际公开/内部签名与变更原因，未为测试公开节点内部编辑状态。

## 实施记录与审核门槛

DSH自测完成后状态标“待Codex复审”；Codex审核问题写docs/return带日期文件，DSH返修至通过后才英文提交并推送。状态改“已完成”须有Codex复审结论，不在Build/Measure中执行外部副作用。

本轮不修改既有archive文件；关键证据不只放可清理artifacts。后续交付记录由Codex创建，当前不预先标记验收通过。

## 实际签名与变更（新增，未改动既有公开API）

新增 `src/CSharp.UI/Editing/`，命名空间沿用 `CSharp.UI`；核心程序集仍无平台依赖。

### 1. `Editing/TextInputContracts.cs` — 编辑值与事务契约

- `readonly record struct TextRange(int start, int end)`：UTF-16 半开范围。`FromLength` / `IsEmpty` / `Length` / `IsWithin` / `Clamp` / `Intersect` / `Contains` / `Offset`。
- `enum TextSelectionDirection { None, Backward, Forward }`。
- `readonly record struct TextSelection(int anchor, int active)`：`Caret` / `IsCollapsed` / `Length` / `Direction` / `Range` / `SelectionStart` / `SelectionEnd` / `Clamp` / `SnapToGraphemeBoundaries(TextLayoutResult)` / `WithActive`。
- `readonly record struct TextSnapshot(string? text, TextSelection selection, int version = 0, bool isComposing = false)`：`Empty` / `Text`（default 安全）/ `Selection` / `Version` / `IsComposing` / `IsConsistent` / `Clamp` / `Next` / `WithComposing`。
- `enum TextEditKind`（Typing/Paste/Cut/Backspace/Delete/CompositionResult/Undo/Redo/Programmatic）、`enum TextEditReason`（UserInput/Clipboard/Programmatic/History/BindingSync）。
- `readonly record struct EditOperation`：公开构造函数 `(TextRange range, string? replacement, TextEditKind kind = Typing, TextEditReason reason = UserInput, bool scrubSingleLine = true)`；`TryCreate(...)` 不抛版本；`Delete(TextRange, ...)` 与构造函数/TryCreate 使用同一套种类/原因校验；`TryValidate(out string? error)` 供 `init`/`with` 改写后的消费方（TI-03）使用；属性 `Range/Replacement`（default 安全）`/Kind/Reason/Scrubbed/IsDeletion/IsNoOp/LengthDelta`。
- `enum TextEditStatus { None, Accepted, Rejected, Unchanged }`。
- `readonly record struct EditResult(TextEditStatus status, TextSnapshot before, TextSnapshot after, string? acceptedText, int documentVersion, string? reason = null)`：`Status/Before/After/AcceptedText`（default 安全）`/DocumentVersion/Reason/Changed`；`EditResult.NotHandled`；`default(EditResult)` 的 `Status` 为 `None`，明确表示无效/未执行。

### 2. `Editing/CompositionContracts.cs` — 组合事件值（无状态机）

- `enum CompositionEventKind { Start, Update, Result, Cancel, End }`（与 `TextInputEvent` 分离，旧事件不换义）。
- `readonly record struct CompositionEvent`：工厂 `Start/Update/Result/Cancel/End`；属性 `CompositionSessionId/CompositionId/Sequence/DocumentVersion/Kind`（`init`，未定义值会被载荷校验拒绝）`/Replacement/Preedit`（default 安全）`/SelectionBeforeComposition/CompositionCaret/Segments`（只读、冻结）`/NextPreedit/CommitsResult/EndsComposition`。
- 校验入口：`TryValidatePayload(TextSnapshot document, out string? error)`（**完整**重载：版本锚点 + 长度/形状 + 用核心 `StringInfo` 校验替换区与组合前选区端点落在已提交文档的**字素边界**上）与 `TryValidatePayload(int documentLength, out string? error)`（**较弱**重载：只按长度/形状检查，不能保证字素安全）；`IsAnchoredTo(TextSnapshot)`。调用者参数错误（负 `documentLength`）抛 `ArgumentOutOfRangeException`，只有平台数据形状问题返回 false。

### 3. `Editing/TextInputServiceContracts.cs` — 服务/客户端/能力契约

- `readonly record struct TextInputGeometry(Rect caretBounds, Rect compositionBounds = default, Rect exclusionBounds = default)`：`HasCaret/HasComposition/Unavailable`（逻辑像素，换算由宿主负责）。
- `readonly record struct TextInputState(TextSnapshot document, string? preedit = null, TextInputGeometry geometry = default, bool composing = false)`：`Document/Preedit`（default 安全）`/Geometry/IsComposing`。**不提供** `DisplayText` 这类“文档 + 预编辑”拼接：显示串必须按组合身份的替换区构造，归 TI-04/TI-06。
- `readonly record struct TextInputSession(int sessionId, int compositionId = 0)`：`NextSession/NextComposition/WithComposition`（会话/组合 ID 身份与推进）。
- `[Flags] enum TextInputOutcome`（None/Started/Updated/ResultCommitted/Cancelled/Ended/Rejected）：只表示**实际完成**的动作。
- `readonly record struct TextInputApplyResult(TextInputOutcome Outcome, TextSnapshot Document, bool Handled, string? RejectionReason = null)`：`NotHandled` / `Rejected` / `IsNoOp`（不含 `RetryAsCommittedText`，见 R3）。
- `readonly record struct TextInputCapabilities(bool committedText, bool composition = false, bool candidateAnchor = false, string? reason = null)`：`None/CommittedTextOnly/Full`。
- `interface ITextInputClient`：`Document` / `Session` / `IsComposing` / `Preedit` / `GetState()` / `ApplyComposition(IReadOnlyList<CompositionEvent>)` / `CancelComposition()`。
- `interface ITextInputService`：`Capabilities` / `ActiveClient` / `StartSession(ITextInputClient)` / `EndSession()` / `ApplyComposition(IReadOnlyList<CompositionEvent>)` / `RequestCancel()` / `PublishGeometry(TextInputGeometry)`。

### 4. `Editing/TextInputFallback.cs` — 已提交文本回退与注入接缝

- `sealed class NoTextInputService : ITextInputService`：`Instance`；能力 `CommittedTextOnly`；会话/组合/锚点全部显式拒绝且不报告已执行动作。
- `static class TextInputForwarder`：`EffectiveService(ITextInputService?)` / `DispatchCommittedText(UiRuntime, string?)` / `ApplyComposition(ITextInputService?, IReadOnlyList<CompositionEvent>)`。

### 5. 运行时注入点（最小改动，向后兼容）

- `UiRuntimeServices.TextInputService { get; init; }`（`ITextInputService?`，默认 null）。
  - 变更原因：为 TI-07 原生桥与测试替身提供注入接缝；可选属性，既有构造调用不变，null 归一化为 `NoTextInputService`。
  - `UiRuntime.DispatchTextInput(TextInputEvent)` 与 `TextInputEvent`、`TextField(Binding<string>, string?)`、`TextField(State<string>, string?)` 均未改动。
- `Binding<T>.Identity`（`internal`，`Source ?? this`）：TI-03 的来源身份判定入口，见下节。

## 冻结行为与校验规则（显式、可测）

### 构造期显式拒绝（编程错误）

| 输入 | 行为 |
|---|---|
| `TextRange` 负偏移 / `end < start` | 构造抛 `ArgumentOutOfRangeException`；不静默交换端点（方向由 `TextSelection` 表达） |
| `TextSelection` 负偏移 | 构造抛 `ArgumentOutOfRangeException`；越界只在 `Clamp`/快照中显式归一化 |
| `TextSnapshot` 负版本 | 构造抛 `ArgumentOutOfRangeException` |
| `CompositionEvent` 负会话/组合 ID、序号、版本或负组合内 caret | 工厂抛 `ArgumentOutOfRangeException` |
| `EditOperation` `kind=None`、未定义的 `kind`/`reason` | `TryCreate` 返回 false + 诊断；构造函数与 `Delete` 工厂抛 `ArgumentException`（工厂不得绕过校验） |
| `EditOperation` 非空插入串净化后为空 | `TryCreate` 返回 false（不当作删除；删除请用 `Delete`） |
| `EditResult` 状态未定义或为 `None` | 构造抛 `ArgumentException`（`default` 的 `None` 表示无效/未执行） |
| `EditResult` 读回文本与 `After` 不一致 / 版本不等于 `After.Version` | 构造抛 `ArgumentException`（回读值是唯一事实来源） |
| `EditResult.Accepted` 文本未变或 `After.Version <= Before.Version` | 构造抛 `ArgumentException`（成功事务必须推进版本，通常 +1） |
| `EditResult.Unchanged` 文本或版本不一致 | 构造抛 `ArgumentException` |
| `EditResult.Rejected` 文本或版本不一致 | 构造抛 `ArgumentException`（被拒绝的事务不改变文档，`before`/`after` 文本与版本都必须一致） |
| `TextInputCapabilities` 声明组合/锚点但无已提交文本 | 构造抛 `ArgumentException` |

### 运行期返回判定（平台数据，不抛异常）

| 输入 | 行为 |
|---|---|
| 组合事件种类为未定义值（`with` 构造也包含） | `TryValidatePayload` 返回 false + “未定义”诊断 |
| 替换区或组合前选区超出文档 | `TryValidatePayload` 返回 false，不写入 |
| 替换区或组合前选区端点落在文档字素内部（拆代理对/组合字符/ZWJ） | **完整快照重载**返回 false + “字素边界”诊断；**长度重载不检查**（明确较弱） |
| 预编辑/下一段预编辑含未配对代理项（会拆代理对） | `TryValidatePayload` 返回 false，不写入 |
| 组合内 caret 或分段超出预编辑串 / 分段重叠或乱序 | `TryValidatePayload` 返回 false，不写入 |
| 事件版本锚点不等于传入文档版本（过时或未来版本） | `TryValidatePayload(document)` 返回 false，不写入 |
| 负 `documentLength` | 调用者参数错误，抛 `ArgumentOutOfRangeException`（不是“平台数据”问题） |
| 空预编辑串、`default(CompositionEvent)`（空 `Start(0)` 形状） | 形状合法；是否可应用由 TI-04 依会话/版本/顺序判定 |
| 无服务 / 无组合能力 / 无活动客户端的组合批次 | `TextInputForwarder` 返回 `Rejected`、`Handled=false`、不报告任何已执行动作、**不重投** |

组合内 `CompositionCaret` 与 `Segments` 是相对**预编辑串**的 UTF-16 内部位置，可能与文档字素边界不同；到排版 caret 的映射由 TI-06 负责，两种坐标不混用。

### 顺序契约（TI-04 实际执行，本任务只文档化）

一段组合的顺序是 `Start → Update* → (Result | Cancel) → End`，可跨多条原生消息，因此后续消息的批次不要求带 Start；`Sequence` 是**同一组合跨消息**递增的序号（不随消息重置）。平台也存在“没有 Start 的 Result”序列。同一批次内若同时携带结果与下一段预编辑，必须按“先结果、后预编辑”处理，并以结果确认后的**真实**文档版本重新锚定。会话 epoch、组合 ID、序号的隔离与重复/过时抑制由 TI-04 的组合状态机执行；`CompositionEvent` 只提供数据与载荷校验。

**TI-04 尚未实现**：当前没有任何组件消费这些事件，也没有状态机；`ITextInputClient.ApplyComposition` 的首个实现与顺序/过时判定在 TI-04 完成。

## Binding/State 来源身份契约（R6 冻结；TI-03 依据）

身份定义（内部）：`Binding<T>.Identity = Source ?? this`。

- **`State<T>.ToBinding()`**：每次调用返回**新的** `Binding<T>` 实例，但 `Source` 指向组件节点持有的同一 `State<T>`，因此 `Identity` 在重建之间稳定。**不能**按绑定实例引用或字符串相等判断来源是否变化。
- **复用同一个自定义绑定实例**：身份稳定（`Identity` 即该实例）；跨重建把同一实例交给控件即视为同来源。
- **每次重建新建的委托绑定 / `Select` 映射绑定**：没有可证明的 `Source`，`Identity` 是各自的新实例，因此按“来源已更换”**保守处理**（取消未提交组合、按新文本重同步并清空历史）。要跨重建保留编辑状态，必须缓存该绑定实例（组件字段）或直接使用 `State<T>`。
- **同值写入**：`State<T>.Value` 对相等值短路、不通知依赖；这只说明“没有变化”，**不代表真实编辑不会重建**——把旧文本写成新文本会 `NotifyDependents` 并重建组件。保留历史的依据是“稳定来源 + 事务确认（写一次绑定并回读）”，不是“不会重建”。
- **`Select` 的策略已在 `Binding<T>.Select` 文档中明确**：不保留来源，需缓存或改用 State；未新增庞大公开 API（仅新增 `internal Identity`）。

## 后续任务责任划分（本任务不实现、不写参考模型冒充验收）

- **TI-03（统一编辑事务）**：真实 setter 回读确认（`Accepted`/`Rejected`/`Unchanged`）、来源更换判定与清理、MaxLength 容量与完整字素截断的真实行为测试、外部写入与自身确认的区分。
  - **绑定转换/规范化的重同步语义由 TI-03 明确**：回读值与旧文本不同就必须报告 `Accepted`（并推进版本），不得构造“同版本却换了文档”的 `Rejected`；`EditResult` 已强制 `Rejected` 的 `before`/`after` 文本与版本一致。
  - `with` 改写后的 `EditOperation` 必须经 `TryValidate` 再执行。
- **TI-04（组合状态机）**：事件顺序、跨消息继续、重复/过时/乱序抑制、“结果 + 下一段预编辑”的真实重基、取消幂等、焦点/禁用/卸载规则；与真实事务结果共同决定版本推进；预编辑显示串按替换区构造。
- **TI-05（历史）**：100 条 / 1,048,576 UTF-16 双上限、500ms 合并、淘汰与超大单项策略、redo 分叉。
- 本轮**不新增**任何“MaxLength/历史参考模型”测试：那只是重写一份公式，不能作为真实编辑实现的验收。

## 计划歧义与解决方式

1. **畸形数据的处理方式**：按契约种类拆分——构造期编程错误抛异常；来自平台的会话/组合数据返回 `TryValidatePayload` 的 false + 诊断，保护原生回调路径。
2. **无 Start 的 Result/End 序列**（计划§7 要求覆盖）：文档化为合法平台序列，必须允许；具体接受/拒绝由 TI-04 状态机执行。
3. **“结果 + 下一段预编辑”同一原生消息**：`CompositionEvent.Result(...nextPreedit)` 承载；重基规则文档化为“按真实事务结果后的版本重新锚定”，由 TI-04 实现。
4. **契约与状态机的边界**（R1/R2 复核后）：TI-02 只冻结事件值与载荷校验，不提供游标/判定报告，避免把仍依赖真实事务结果的判定提前冻结。
5. **版本类型**：公开契约统一用 `int`（文档版本/组合序号/会话 epoch），与既有 `TextSnapshot.Version` 一致。
6. **历史容量是否公开**：本任务不新增公开历史选项；容量与合并属 TI-05。
7. **几何/单位归属**：核心矩形为逻辑像素，平台单位换算与锚点发布由宿主（TI-07/TI-08）负责；核心不出现 HWND/NSView/OpenTK/Skia 类型。
8. **注入方式**：`UiRuntimeServices.TextInputService`（可选，默认 null）+ `TextInputForwarder` 归一化，不把可空服务散落到每个调用点，也不改动既有输入路径。
9. **回退语义**（R3）：已提交文本回退始终是既有字符通道 `DispatchCommittedText`；组合批次在无服务/无能力/无归属时被拒绝且不重投，“批次含 Result”本身不构成可安全重投的理由。
10. **default 安全**：`readonly record struct` 无法禁止 `default`，因此所有含 `string` 的公开属性（`TextSnapshot.Text`、`EditOperation.Replacement`、`CompositionEvent.Preedit`、`TextInputState.Preedit`、`EditResult.AcceptedText`）改为 default 安全的只读 getter；`CompositionEvent.Segments` 归一化为空只读集合；`default(EditResult).Status` 为 `None`，明确表示无效/未执行。
11. **诊断脱敏**：所有 `ToString()` 只输出长度/版本/种类等元数据，不输出用户原文。
12. **`TextSelection.SnapToGraphemeBoundaries` 依赖排版结果**：字素吸附复用现有 `TextLayoutResult.CaretStops`，不新建字素映射层；契约校验路径改用核心 `StringInfo`，不要求渲染器。
13. **`with` 改写的校验入口**：`readonly record struct` 的公开 `init` 允许改写枚举值，因此提供最小的 `EditOperation.TryValidate(out string? error)` 供 TI-03 在执行前复核，而不是新增拦截层或只依赖工厂。
14. **`TextInputState` 不提供显示串快捷属性**：`DisplayText`（文档 + 预编辑）会误导替换语义，已移除；显示构造归 TI-04/TI-06。

## 测试与证据

新增/更新测试文件（全部无窗口 CPU 测试，不运行 GUI/原生输入）：

- `tests/CSharp.UI.Tests/TextEditingContractTests.cs`（22 项：范围/选区/快照/操作/结果构造规则的失败与边界）
- `tests/CSharp.UI.Tests/CompositionSessionContractTests.cs`（23 项：会话身份、载荷校验边界、字素边界（代理对/组合字符/ZWJ）、未知枚举、版本锚点、不可变载荷、default 安全、脱敏）
- `tests/CSharp.UI.Tests/TextInputServiceContractTests.cs`（16 项：能力一致性、服务替身、回退不重放、注入、已提交文本路径不变、Build/Measure 不改文档）
- `tests/CSharp.UI.Tests/BindingSourceIdentityContractTests.cs`（8 项：`Identity = Source ?? this`、重建稳定、复用自定义绑定稳定、新建/映射绑定保守、真实编辑会重建）

命令与结果（cwd = 仓库根，本轮 DSH 自测；按 Codex 第二轮要求只重跑解决方案构建与核心套件，OpenTK 沙箱剪贴板用例由 Codex 在非受限环境执行）：

| 命令 | 退出码 | 结果 |
|---|---|---|
| `dotnet build CSharp.UI.slnx --no-restore -m:1 -nodeReuse:false` | 0 | 0 错误（全解决方案编译通过） |
| `dotnet exec tests/CSharp.UI.Tests/bin/Debug/net10.0/CSharp.UI.Tests.dll` | 0 | 369 通过 / 0 失败 / 0 跳过（基线 300，新增 69） |

上一轮（返修前）记录、仅供参考：核心 363、Text 67、Integration 30、OpenTK 242（其中 1 个既有剪贴板往返在受限沙箱失败，需非受限环境复跑，不伪报通过）。历史参考基线 639 只作数量对照，未用“数量不减少”代替断言保留，旧断言未删改。

平台边界与本轮未验证项：

- 未接入任何原生消息桥，未发布真实候选锚点，未取得真实 IME 组合数据；组合状态机、预编辑绘制、候选几何与历史均为后续任务。
- 未安装、启用、探测或测试微软拼音；未运行 GUI；未修改系统输入法设置。
- 本轮证据为构建/测试命令与退出码，不包含截图或原生消息日志（TI-07/TI-10 负责真实 IME 验收）。
- `CompositionEvent` 的顺序/过时判定、`EditOperation`/`EditResult` 的真实执行与 MaxLength/历史行为在本任务**只有契约与文档**，没有可运行的实现或验收。

## 变更文件清单

新增：

- `src/CSharp.UI/Editing/TextInputContracts.cs`
- `src/CSharp.UI/Editing/CompositionContracts.cs`
- `src/CSharp.UI/Editing/TextInputServiceContracts.cs`
- `src/CSharp.UI/Editing/TextInputFallback.cs`
- `tests/CSharp.UI.Tests/TextEditingContractTests.cs`
- `tests/CSharp.UI.Tests/CompositionSessionContractTests.cs`
- `tests/CSharp.UI.Tests/TextInputServiceContractTests.cs`
- `tests/CSharp.UI.Tests/BindingSourceIdentityContractTests.cs`

修改：

- `src/CSharp.UI/Runtime/UiRuntime.cs`（仅新增可选属性 `UiRuntimeServices.TextInputService`）
- `src/CSharp.UI/State/State.cs`（仅新增 `internal Binding<T>.Identity` 与文档）
- `docs/task/02-editing-and-input-contracts.md`（本文件，仅 TI-02 实施记录）

第二轮返修中的删除（尚未提交，属本轮新增代码，不涉及既有 API）：

- 移除 `CompositionCursor`/`CompositionBatchValidator`/`CompositionBatchReport`/`CompositionEventDecision`/`CompositionEventVerdict`（改为 TI-04 责任）。
- 移除 `TextInputApplyResult.RetryAsCommittedText`、`TextCompositionPhase`（回退不重放、请求阶段由事件种类表达）。
- 移除 `TextInputState.DisplayText`（误导性拼接）与未使用的 `TextRange.Normalized()`。

未改动：既有测试、既有控件行为、`TextInputEvent`、`TextField` 构造函数、Codex 拥有的 plan/return/decision notebook/证据目录与其他任务状态文件、TI-01 探针与证据文件。

## 待Codex复审事项

1. 上述公开签名是否可冻结；是否同意“构造期抛异常 / 平台数据返回判定 / 参数错误抛异常”的划分。
2. 移除 `CompositionCursor/BatchValidator/Report` 并将顺序与过时抑制明确归 TI-04 是否满足 R1/R2 的范围决定；`ITextInputClient.ApplyComposition` 的单批次形状是否足够 TI-04/TI-07 使用。
3. `TextInputApplyResult` 去掉 `RetryAsCommittedText`、回退仅走既有字符通道且组合批次不重投，是否满足 R3。
4. `Binding<T>.Identity = Source ?? this` 与“新建/映射绑定保守处理”的表述是否满足 R6；是否需要额外公开标记“来源已更换”。
5. `EditResult` 的版本与“拒绝即文档不变”不变量（`Accepted` 必须严格推进版本、`Rejected` 文本与版本都不变）是否满足 TI-03/TI-05；绑定转换后的重同步语义留给 TI-03 是否符合预期。
6. 完整快照重载用核心 `StringInfo` 校验字素边界、长度重载明确较弱、组合内 caret/segments 保持 UTF-16 预编辑坐标，是否满足第二轮 R2。

## Codex最终复审

R1～R6及第二轮边界已关闭；独立最终构建exit0，核心369项通过，定向14项检查通过。其他三套独立回归67/30/242通过（OpenTK在可访问剪贴板环境重跑）；上述结果来自分轮验证，最终不涉及完整原生IME交付。[完整审核与证据](../archive/2026-10-23-completed-reviews/2026-10-17-ti02-contract-review.md)。
