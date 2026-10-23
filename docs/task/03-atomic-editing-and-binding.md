# TI-03：统一编辑事务与 Binding 同步

创建日期：2026-10-17

状态：已完成（2026-10-17 Codex独立复审通过；DSH提交bc38eb8，已推送main）

阶段：B

设计依据：[文本输入与编辑基础计划](../plan/2026-10-17-text-input-and-editing-plan.md)（接口冻结前均为设计草案）。

[返回任务索引](README.md)

## 目标与范围

所有已提交文本修改走同一个原子替换通道，为组合结果与历史提供可靠基础。

边界：插入/删除/剪切粘贴/选区与绑定，继续单行。

## 前置依赖

[TI-02：冻结编辑与文本输入契约](02-editing-and-input-contracts.md)均须Codex复审通过后再实施；使用前置任务已冻结契约。

## 实施清单与接口接入

- [x] 提取文档、选区、版本与原子替换模型，TextField普通键入/删除/剪贴板全部接入；选择移动不制造文本事务。
- [x] 保持UTF-16 MaxLength、选区释放容量与完整字素截断；保留既有单行净化规则。
- [x] 捕获快照、写Binding一次并回读确认，处理拒绝/规范化/抛错；外部写入、来源替换与自身确认可靠区分。
- [x] 同节点同绑定重建/Flow/Adaptive布局保持编辑状态；外部文本与配置变更按计划清理并限制选区。

## 测试与验收

- [x] 表驱动或确定性随机序列覆盖真实编辑状态，不仅测试helper；代理对、组合附加符、ZWJ与选区方向。
- [x] 粘贴替换、空串/全拒绝/边界删除、MaxLength、setter拒绝/转换/异常不污染文档版本。
- [x] 运行旧TextField/Binding/响应式输入回归；文字caret与横向滚动保持。

## 实施记录与审核门槛

实施时记录实际签名、涉及模块、命令/退出码/测试计数、证据路径及平台边界。DSH自测通过后标“待Codex复审”；Codex审核问题写docs/return带日期文件，DSH返修至通过后才英文提交并推送。状态改“已完成”须有Codex复审结论，不在Build/Measure中执行外部副作用。

本轮不修改既有archive文件；关键证据不只放可清理artifacts。后续交付记录由Codex创建，当前不预先标记验收通过。

## 实际签名与变更（TI-03，新增均为内部实现，未改动既有公开 API）

### 1. 新增 `src/CSharp.UI/Editing/TextEditSession.cs`

- `internal readonly record struct TextEditSyncResult(bool TextChanged, bool SourceChanged, bool MaxLengthChanged)`：`HasExternalChange`。
- `internal sealed class TextEditSession`：
  - 状态：`string Text` / `int Version` / `bool RequiresResync` / `bool CompositionInvalidationRequested` / `bool HistoryInvalidationRequested`（`PendingWrite` 为私有实现细节）。
  - `TextEditSyncResult Sync(object identity, string? text, int maxLength)`：首次建立基线；之后来源 / 文本 / MaxLength 任一变化推进版本并标记失效接缝；同值回读不推进、不清历史。
  - `bool IsOwnPendingWrite(string? text)` 与 `bool MatchesContext(object identity, int maxLength)`：识别“自身事务触发的重建”，仅在文本匹配且来源/MaxLength 未变时才算自身确认。
  - `EditResult Apply(object identity, int maxLength, EditOperation operation, TextSelection selection, Func<string?> read, Action<string> write)`：单次原子替换事务（见下）。
  - `void MarkRequiresResync()`：标记“无法可靠回读”，同时保守标记组合/历史失效。
  - `(bool Composition, bool History) ConsumeInvalidation()`：供 TI-04/TI-05 消费接缝（本阶段仅测试读取）。
  - `internal static string FitWholeGraphemes(string inserted, int capacity)`：完整字素截断（自 TextField 迁移）。
- `TextEditSession.Apply` 的执行顺序（实际同步语义）：
  1. 读取真实文本；getter 抛错时直接上抛，不改版本、不移动光标/选区。
  2. 用真实回读对齐会话；外部文本/来源/MaxLength 变化在此推进版本并标记失效接缝。
  3. 用 TI-02 `TextGraphemeBoundaries.AllAreBoundaries` 拒绝越界或落在代理对/组合附加符/ZWJ 内部的范围。
  4. 容量按“替换后释放的空间”计算（`maxLength - (text.Length - range.Length)`），插入内容只在完整字素边界截断；放不下任何完整字素时整笔 `Rejected`，不写绑定。
  5. 候选与当前文档一致时返回 `Unchanged`，不调用 setter。
  6. 只写一次绑定；setter 抛错时尝试回读，确实变化则重同步并标记失效，随后原样重抛 setter 异常（外部副作用不回滚）。
  7. 回读：getter 抛错时标记 `RequiresResync` + 失效接缝并上抛，绝不把候选当事实。
  8. 回读值等于旧文本 → `Rejected`（文档未变、选区不动）；回读值不同 → 一律 `Accepted` 并推进版本；回读值不等于候选（绑定转换/规范化）时按**实际回读文本**吸附确认选区并标记组合/历史失效，不记录请求替换的逆操作。

### 2. 修改 `src/CSharp.UI/Views/TextField.cs`

- 普通键入、Backspace/Delete、剪切、粘贴、选区替换全部构造 `EditOperation` 后经 `ApplyEdit` 走 `TextEditSession.Apply`；删除空范围等空操作不调用 setter。选择/光标移动不经过会话。
- `TextFieldState` 新增 `internal TextEditSession Editor { get; }`；编辑会话与节点同寿命。
- `ApplyEdit` 在事务结束后重新核对挂载视图：节点已卸载、描述已换成非输入框、或来源/MaxLength 已变时，旧事务结果与选区不写入新客户端，改为按当前挂载绑定 `SyncEditor`。异常路径调用 `RecoverAfterTransactionFailure`，按真实回读文本同步会话、限制选区并使布局失效，次生读取失败只打标记、不覆盖原始异常。
- `SyncEditor(node, state, invalidate, knownLayout?)` 是统一安全入口：挂载、描述更新、排列、绘制、焦点变化、指针、键盘与文本输入都会观测真实绑定值；自身确认与外部变化按 `IsOwnPendingWrite + MatchesContext` 区分。排列/绘制路径复用已有排版结果，避免递归 `GetLayout`，并且不在绘制路径失效布局。
- `EnsureCaretVisible(node, state, knownLayout?, knownInner?)` 是可复用版本：`Arrange` 与 `Emit` 传入同一排版结果与内容区，只调整 `ScrollX`，保留原有的物理像素吸附（`TextPainter.ResolveOrigin` 在夹回后使用新的 `ScrollX`），不写绑定、不改光标/选区、不请求失效。

### 3. 与计划第 6/7/9 节的边界

- 组合状态机（TI-04）与撤销/重做（TI-05）未实现；只记录 `CompositionInvalidationRequested` / `HistoryInvalidationRequested` 接缝与文档版本。
- 未接入任何 IME/预编辑/候选窗路径；微软拼音未安装、未启用、未测试。

## 首轮审核 R1/R2/R3 返修

- **R1（生命周期与异常同步）**：新增 `SyncEditor` 于挂载/`OnUpdate`/`Arrange`/`Emit`/焦点/指针/键盘/文本输入入口；`Emit` 复用 `GetLayout` 结果，不递归排版、不持续失效布局。`Emit` 与 `Arrange` 共用 `EnsureCaretVisible(node, state, knownLayout, knownInner)`：纯重绘观测同步外部缩短文本后，立即用同一排版与内容区把横向滚动夹回，避免旧 `ScrollX` 把新文字整个移出可见区（不靠绘制阶段夹取光标掩盖）。setter 写后抛错、回读失败后，按真实回读恢复选区并失效布局；getter 恢复后仅靠布局或键盘入口即可清除 `RequiresResync` 并重同步，不要求另一次成功编辑。未知/失败回读立即标记组合/历史失效接缝；恢复路径的次生读取异常被吞掉，原始异常原样上抛。稳定重复帧不推进版本、不反复改动滚动。
- **R2（来源/配置归属）**：`OnUpdate` 的自身确认判定加入 `Binding.Identity` 与 `MaxLength`；`ApplyEdit` 结束时核对 `node.IsMounted` 与当前挂载 `TextField` 的来源/配置，节点不可用或来源/配置已换时不用旧快照串写新会话。已覆盖同值来源替换、异值来源替换与 setter 期间卸载。
- **R3（字素不变量）**：事务范围用 TI-02 `TextGraphemeBoundaries` 校验；事务前选区与确认后选区都吸附到字素边界，`EditResult` 快照不再含字素内部位置。

## 命令、退出码与测试计数（cwd = 仓库根）

| 命令 | 退出码 | 结果 |
|---|---|---|
| `dotnet build CSharp.UI.slnx --no-restore -m:1 -nodeReuse:false` | 0 | 全解决方案通过；缓存构建 0 警告 0 错误 |
| `dotnet build src/CSharp.UI/CSharp.UI.csproj --no-restore -m:1 -nodeReuse:false -t:Rebuild` | 0 | 核心强制重编译：仅 TI-02 既有 `CompositionContracts.cs` 5 条 CS8601，TI-03 文件 0 警告 |
| `dotnet exec tests/CSharp.UI.Tests/bin/Debug/net10.0/CSharp.UI.Tests.dll` | 0 | 405 通过 / 0 失败 / 0 跳过（TI-02 基线 369，本轮新增 36） |
| `dotnet exec tests/CSharp.UI.Text.Tests/bin/Debug/net10.0/CSharp.UI.Text.Tests.dll` | 0 | 67 通过 / 0 失败 |
| `dotnet exec tests/CSharp.UI.Integration.Tests/bin/Debug/net10.0/CSharp.UI.Integration.Tests.dll` | 0 | 30 通过 / 0 失败（真实 SkiaSharp/HarfBuzz 排版） |
| `dotnet exec tests/CSharp.UI.OpenTK.Tests/bin/Debug/net10.0/CSharp.UI.OpenTK.Tests.dll` | 0 | 242 通过 / 0 失败（含原生隐藏窗口/OpenGL 与真实剪贴板往返） |

证据（可清理 artifacts）：`artifacts/ime-work/ti03-solution-build-debug.log`、`ti03-core-rebuild-warnings.log`、`ti03-test-rebuild-warnings.log`、`ti03-tests-CSharp.UI.Tests.log`、`ti03-tests-CSharp.UI.Text.Tests.log`、`ti03-tests-CSharp.UI.Integration.Tests.log`、`ti03-tests-CSharp.UI.OpenTK.Tests.log`。

Codex 独立复现与复测的持久证据（只读引用，未改动）：`docs/archive/2026-10-23-completed-reviews/2026-10-17-ti03-atomic-editing-review-evidence/`，含原失败场景日志 `codex-sync-audit-initial.log`、`codex-input-sync-audit-initial.log`、`codex-source-audit-initial.log`、`codex-render-scroll-audit-initial.log` 与第二轮通过日志 `codex-sync-audit-round2.log`、`codex-final-*.log`、`codex-core-round2.log`、`codex-text.log`、`codex-integration.log`、`codex-opentk.log`。

## 新增/变更文件清单

新增：

- `src/CSharp.UI/Editing/TextEditSession.cs`
- `tests/CSharp.UI.Tests/TextFieldAtomicEditTests.cs`

修改：

- `src/CSharp.UI/Views/TextField.cs`
- `docs/task/03-atomic-editing-and-binding.md`（本文件，仅 TI-03 实施记录）

未改动：既有测试、既有公开 API、`TextInputEvent`、`TextField` 构造函数、`docs/return`、`docs/plan`、任务索引 `docs/task/README.md`、agents-notebook 与其他任务状态文件。

## 平台边界与限制

- 本轮新增的 TI-03 测试（`TextFieldAtomicEditTests`）全部是无窗口 CPU/headless 测试，驱动真实 `UiRuntime` 输入分发与真实 `TextField` 节点；它们不打开窗口、不调用原生 API。既有套件并非全部 headless：OpenTK 套件包含原生隐藏窗口与 OpenGL 上下文、以及真实剪贴板往返，本次在同一沙箱重跑 242 项全通过；Text 与 Integration 套件使用真实 SkiaSharp/HarfBuzz 排版。未接入任何原生 IME 桥，未取真实组合数据。
- 未安装、未启用、未探测、未测试微软拼音；未修改系统输入法设置。TI-01 探针与 ImeProbe 套件本轮未重跑（不在 TI-03 范围）。
- 字素切分源一致：核心事务校验、`TextField` 字素截断与 `CSharp.UI.Text` 的真实 Skia 排版都使用 `System.Globalization.StringInfo`（`src/CSharp.UI.Text/TextShaping.cs` 的 `Graphemes`），因此不假设两套不同的切分边界。已实际覆盖：代理对、组合附加符、ZWJ 家庭序列的整字素删除与 MaxLength 截断（headless），以及真实 Skia 中文已提交文本的 caret/选区回归；真实渲染器下的 ZWJ 组合输入/IME 结果仍未覆盖（属 TI-04/TI-07 范围，不是本轮宣称已完成的验收）。
- 绑定转换/规范化后的选区采用“按实际回读文本向后吸附到完整字素”，这是尽力而为的同步语义；未实现历史，因此不承诺可逆。

## 待Codex复审事项

1. `TextEditSession.Apply` 的状态划分（容量/字素整体拒绝与 setter 未改文档 → `Rejected`；候选与文档一致 → `Unchanged`；回读不等于旧文本 → `Accepted` 并推进版本；规范化额外标记失效接缝）是否符合 TI-02 `EditResult` 不变量与计划第 6 节。
2. `SyncEditor` 在 `Arrange`/`Emit` 只读绑定、仅调整节点内部会话/选区，是否满足“不在 Build/Measure 写 Binding 或产生外部副作用”；`Emit` 复用排版结果且不失效布局是否符合“不递归 GetLayout、不持续失效”。
3. `OnUpdate` 的自身确认要求“文本匹配 + `Binding.Identity` + `MaxLength` 未变”，以及 `ApplyEdit` 结束时的挂载/来源/配置复核是否足以关闭 R2。
4. 事务范围改用 TI-02 `TextGraphemeBoundaries`、确认选区吸附字素边界，是否满足 R3；核心与真实 Skia 排版同样基于 `StringInfo`，剩余待补的是真实渲染器下的 ZWJ 编辑/组合覆盖，而非切分边界差异。
5. `Emit`/`Arrange` 用同一 `EnsureCaretVisible(knownLayout, knownInner)` 在纯重绘观测后夹回横向滚动，是否满足“保持横向滚动、不靠绘制夹取掩盖、不递归 GetLayout、不持续失效”。
6. 失败/未知回读与 setter 抛错一律标记组合/历史失效接缝是否符合 TI-04/TI-05 的预期（本阶段不实现消费方）。

## Codex最终复审

R1～R3及键盘、纯重绘和横向滚动补充已关闭；最终构建通过，核心405、Text67、Integration30、OpenTK242及独立14项实际输入检查通过，来自各自验证轮次。[审核及持久证据](../archive/2026-10-23-completed-reviews/2026-10-17-ti03-atomic-editing-review.md)。TI04～TI10仍待实施。
