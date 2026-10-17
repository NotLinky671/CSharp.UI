# RL-01：测量提案与尺寸特征基础

创建日期：2026-10-16

状态：已完成（Codex 复审通过）

阶段：A

设计依据：[响应式布局计划](../../plan/2026-10-16-responsive-layout-plan.md)

[返回任务索引](README.md)

## 目标与范围

建立探测/有效测量和固定尺寸传播的内部契约，供伸缩与 Responsive 使用；保持既有布局默认行为。

## 前置依赖

无新增前置任务；基于当前已交付框架与441项测试基线。

## 实施清单与接口交接

- [x] 在 LayoutContext/ViewNode 测量路径区分 Probe 与有效提案，嵌套作用域以 finally 恢复；不向公开核心 API 暴露后端类型。
- [x] 测量缓存必须区分提案性质：Probe 后以相同约束进行有效测量仍能登记有效布局提案，不能被缓存跳过；探测不得挂载、卸载或选择 Responsive 内容。
- [x] 定义内部按轴的布局特征：自然尺寸、明确固定/min/max、Flex 配额、FillWidth。沿 Padding/Frame/普通修饰符传播，明确外层/内层与主轴/交叉轴的归属，不能从被截短的 DesiredSize 推断可压缩性。
- [x] 提供有节点路径的布局溢出/无界诊断基础；同类稳定问题不逐帧重复刷日志，诊断记录与缓存须有界。
- [x] 保留未启用新能力的 HStack/VStack/Spacer/Frame 和修饰符顺序；在本任务文末记录供后续任务使用的内部契约。

## 测试与验收

- [x] 相同约束 Probe→有效测量、嵌套探测与异常退出均正确；有效提案不会被 Probe 覆盖。
- [x] 固定宽度包在 Padding、Frame、普通修饰符内仍能正确识别；小数、零和无界约束无 NaN/无穷几何泄漏。
- [x] 明确区分外框分配与内部固定内容：外层 FrameView(maxWidth:100)/FrameView(minWidth:300) 包裹固定240内容时，自然分配基准分别为100/300；min300允许居中放置240内容，不应伪造矛盾范围。内容的240固定要求仍须可诊断，不能把内层ExplicitSize直接当作外框BaseSize。具体独立复查见审核记录。
- [x] 现有布局/状态测试不退化；新增行为测试而非仅检查 helper 字段值。

## 实施记录

状态：**待 Codex 复审**（2026-10-16）。本任务只交付内部契约与特征，未实现 Flex/FillWidth/Flow/AdaptiveStack/Responsive 公开 API。

### 交付文件

- 新增 `src/CSharp.UI/Layout/LayoutFeatures.cs`：`MeasureProposalKind`、`LayoutAxis`、`AxisLayoutFeatures`、`LayoutFeatures`、按轴小工具。
- 新增 `src/CSharp.UI/Layout/LayoutDiagnostics.cs`：有界、按节点去重的诊断收集器。
- 修改 `src/CSharp.UI/Runtime/Contexts.cs`：`LayoutContext` 探测作用域与恢复、缓存隔离、溢出/无界诊断入口；`MeasureProbeScope`。
- 修改 `Runtime/ViewNode.cs`、`Runtime/UiRuntime.cs`、`Views/View.cs`、`Views/Modifiers.cs`、`Views/Primitives.cs`、`Views/Component.cs`、`src/CSharp.UI/CSharp.UI.csproj`（为集成测试开放 internals）。
- 新增测试 `tests/CSharp.UI.Tests/LayoutProbeTests.cs`（19 例）与 `tests/CSharp.UI.Integration.Tests/LayoutProbeIntegrationTests.cs`（1 例，真实 Skia 排版 + 绘制命令）。

### 供 RL-02 及后续任务使用的内部契约

1. **测量提案**：`LayoutContext.CurrentProposal`/`IsProbing`；`BeginProbe()` 返回 `MeasureProbeScope`（`IDisposable`，`using`/`finally` 恢复）；`Probe(node, constraints)` 返回 `Size`；`ProbeFeatures(node, constraints)` 返回 `LayoutFeatures`。进入探测前对每个将被改写的节点快照（`DesiredSize`、`ViewCache`、`Environment`、`MeasureValid`、`LastConstraints`、`LastProposalKind`），退出时完整恢复。快照覆盖全部改写入口：正常测量路径、`MeasureFresh`（先拍快照再清缓存）、以及缓存命中但仍需重新解析环境的分支（先拍快照再赋值）。探测期间不运行 builder、不挂载/卸载节点。`Probe`/`ProbeFeatures` 的返回值只在作用域内有效。
2. **缓存隔离**：探测可复用任何同约束缓存；有效测量只复用 `LastProposalKind == Effective` 的同约束缓存；“同约束 Probe→有效测量”必定重新登记有效提案，恢复快照保证探测结果与排版缓存不残留。**探测结束后消费者必须对所有分配的子树执行有效测量**（非伸缩子项也要），否则它们不会得到最终提案。
3. **按轴尺寸特征（盒子语义）**：`ViewNode.GetLayoutFeatures()` → `LayoutFeatures{ Horizontal, Vertical }`，每轴 `AxisLayoutFeatures`：
   - `NaturalSize`：最近一次测量的自然尺寸（仅基准，可能已被约束截断）；
   - `ExplicitSize`/`MinSize`/`MaxSize`：**该节点盒子自身**的固定/最小/最大声明（`Frame(width/height/minWidth/maxWidth)`、`Rectangle.FixedSize`）；
   - `ContentMinSize`：该节点**子树**的硬最小要求（外框自身硬下界与内容要求取较大者）；null 表示子树可压缩到 0；
   - `Grow`/`Shrink`/`Basis`/`Fill`：弹性配额（`Grow` 已由旧 `FlexGrow` 填充，`Shrink`/`Basis`/`Fill` 待 RL-02 写入）；
   - `BaseSize = ExplicitSize ?? Basis ?? NaturalSize`；`EffectiveMin = max(Explicit, Min)`；`EffectiveMax = min(Explicit, Max)`；`HasConflictingBounds = EffectiveMin > EffectiveMax || (ContentMinSize ?? 0) > EffectiveMax`。
   - **传播规则**：普通修饰符（Background/Border/Clip/环境类）透传；`Padding` 把水平/垂直内边距分别叠加到对应轴的全部尺寸声明（含 `ContentMinSize`）；`Frame` 无尺寸声明时透传内容并更新自然尺寸，有声明时由外框自身声明定义盒子（内层 `ExplicitSize` 不再充当外框 `BaseSize`，但进入 `ContentMinSize` 供诊断）；`Component` 透传 `Build()` 根节点。**最外层节点的特征即父容器消费的特征（外层优先）**。
   - **轴归属**：HStack 主轴为 Horizontal、VStack 主轴为 Vertical；`FlexAxis.Main` 由 RL-02 消费者按当前堆叠方向映射到具体轴，`FlexAxis.Horizontal/Vertical` 固定映射。特征不从被截短的 `DesiredSize` 反推可压缩性。
4. **盒子与内容冲突规则**：外层 `Frame(maxWidth:100)` 包裹固定 240 内容 → `BaseSize=100`、`EffectiveMax=100`、`ContentMinSize=240`、`HasConflictingBounds=true`（保留“内容仍需 240”的溢出信息）；外层 `Frame(minWidth:300)` 包裹固定 240 内容 → `BaseSize=300`、`EffectiveMin=300`、`EffectiveMax=+∞`、`HasConflictingBounds=false`（内容可居中放置，不伪造矛盾）。后续 Flex/Flow 遇到 `HasConflictingBounds` 必须按溢出处理、不静默压缩固定项，并输出带节点路径的诊断。
5. **诊断**：`LayoutContext.ReportOverflow(node, axis, required, available)`、`ReportUnbounded(node, axis, operation)`；`UiRuntime.LayoutDiagnostics`（内部）按 `(节点, 类别, 轴)` 去重、容量 128、每轮最多输出 16 条新诊断；`UiRuntime.Diagnostics` 保留最近 512 条。当前触发点为“盒子显式固定/最小声明被父级可用空间截断”；无界诊断入口留给后续消费者接入。

### RL-02 期间的契约修订（2026-10-16）

RL-02 复审要求“自然测量不得被当作有效提案缓存”，内部契约在 RL-01 基础上扩展（RL-01 的探测/恢复/环境/排版行为不变）：

- `MeasureProposalKind` 新增 `Intrinsic`：Stack 的自然尺寸测量登记 Intrinsic 提案，不是最终布局提案；`LayoutContext.CurrentProposal` 在自然测量期间返回 `Intrinsic`（视图在 `Measure` 内读取到的也是它），并新增 `IsEffectiveProposal` 作为唯一的“有效提案”判定供后续动态内容使用。
- 缓存复用同时要求“同约束 + 同算法模式（自然尺寸/有效分配）”：`ViewNode.LastMeasureIsIntrinsic` 与 `LastProposalKind`（最终/临时性质）分开保存；探测可复用同模式同约束的任意已有结果，实际测量只复用同类结果。同约束的自然测量不再掩盖后续必须执行弹性分配的有效测量，探测（含 `PushIntrinsicMeasure` 嵌套）也不会复用到有效分配算法产生的缓存。
- `MeasureProbeScope.MeasureSnapshot` 补齐 RL-02 新增的 `ViewNode.ChildMainAllocation`/`AllocatedMainExtent` 与 `LastMeasureIsIntrinsic`，恢复覆盖嵌套探测与异常退出。
- `LayoutProbeTests`/`LayoutProbeIntegrationTests` 中“普通 Stack 子项只经过自然测量”的提案断言由 `Effective` 更新为 `Intrinsic`（其余断言不变）。

### 验证命令与退出码（顺序执行，`--no-restore -m:1 -nodeReuse:false`）

| 命令 | 结果 |
|---|---|
| `dotnet build CSharp.UI.slnx --no-restore -m:1 -nodeReuse:false` | exit 0，0 错误；Counter/TodoList/ControlGallery 三个 Debug 示例均重建 |
| `dotnet run --project tests/CSharp.UI.Tests --no-build` | exit 0 |
| `dotnet run --project tests/CSharp.UI.Text.Tests --no-build` | exit 0 |
| `dotnet run --project tests/CSharp.UI.Integration.Tests --no-build` | exit 0 |
| `dotnet run --project tests/CSharp.UI.OpenTK.Tests --no-build` | exit 0 |
| `dotnet run --project artifacts/responsive-work/invariants/Verify.csproj --no-build`（Codex 独立探针，10 项） | exit 0，10/10 PASS（含 `Cached Probe environment restore`、`MeasureFresh Probe restore`） |

### 测试数量（真实 runner 输出）

| 工程 | Total | Failed | Skipped |
|---|---|---|---|
| CSharp.UI.Tests | 191 | 0 | 0 |
| CSharp.UI.Text.Tests | 67 | 0 | 0 |
| CSharp.UI.Integration.Tests | 15 | 0 | 0 |
| CSharp.UI.OpenTK.Tests | 188 | 0 | 0 |
| 合计 | 461 | 0 | 0 |

基线 441，新增 20（核心 19 + 集成 1）。负向对照：关闭探测恢复→核心 8 例 FAIL；不恢复 `ViewCache`→核心 2 例 + 集成真实绘制几何 1 例 FAIL；退回旧 `EffectiveMin/Max` 优先级→冲突边界回归 1 例 FAIL（均随后恢复）。运行日志见 `artifacts/responsive-work/rl01-progress.txt`。

### Codex 复查处理

- **P1**：快照补齐 `ViewCache` 与 `Environment`，并把快照移到改写环境之前（缓存命中路径仍更新环境）；集成测试用真实 Skia 排版与 `TextureCommand` 几何验证探测不泄漏（嵌套探测 + 异常退出）。
- **快照入口完整性补查**：`MeasureFresh` 先拍快照再清缓存；缓存命中分支在重新解析环境前先拍快照。回归覆盖“同约束缓存命中但环境需解析”与“BeginProbe 内 MeasureFresh”；正常有效测量的环境更新行为不变。
- **P2**：按 Frame 盒子语义修正。外层框的 `BaseSize`/`EffectiveMin`/`EffectiveMax` 只来自外框自身声明与自然尺寸；内层固定尺寸进入独立的 `ContentMinSize`，不再覆盖外框边界。补 `FrameView(maxWidth:100)`/`FrameView(minWidth:300)`（是否带 Padding）、同 Frame 双声明、Padding 外层嵌套与单内容 Component 透传回归；默认 `Measure`/`Arrange` 行为未改变。Codex 独立探针 10/10 PASS。

### 适用平台与未验证边界

- Windows 本机 .NET 10 全部通过；核心/文本/集成测试不需要窗口或 GL，OpenTK 套件使用本机 OpenGL 3.3。
- 本阶段不重复 Release 打包与示例冒烟，按任务说明由 RL-09 统一执行。
- 未验证：macOS/Linux 原生、真实窗口下的响应式行为；未实现弹性/换行/响应式公开 API。
- 提交：本次 RL-01 单一提交，SHA 见 `git log -1`。
