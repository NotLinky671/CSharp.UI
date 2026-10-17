# RL-02：Flex 与 FillWidth

创建日期：2026-10-16

状态：已完成（Codex 复审通过；实现提交 609c856）

阶段：A

设计依据：[响应式布局计划](../../plan/2026-10-16-responsive-layout-plan.md)

[返回任务索引](README.md)

## 目标与范围

让 Stack 中显式声明的弹性控件按剩余空间增长或压缩，并在最终宽度下重新测量文本。

## 前置依赖

[RL-01](01-layout-proposals-and-sizing.md) 完成并通过复审；使用其已经固定的契约。

## 实施清单与接口交接

- [x] 实现 FlexAxis(Main/Horizontal/Vertical)、不可变 Flex 修饰及扩展 .Flex(grow=1, shrink=1, basis=null, min=0, max=PositiveInfinity, axis=Main)；参数校验遵循计划。
- [x] 实现基于约束后 basis 的增长/压缩、min/max 冻结与剩余空间再分配；零 basis 的零压缩量、Spacer 共存和无界主轴行为明确。
- [x] 按最终分配长度再次测量子项，重新计算多行文本高度和 Stack 交叉轴尺寸；Arrange 使用分配结果，不只压缩旧 Frame。
- [x] 实现 .FillWidth()：有界宽度占满、横向 Stack 中参与伸缩、无界回退自然尺寸。它不取消内部硬 Frame。同轴 Flex/FillWidth 外层生效，不叠加权重。
- [x] 显式 Horizontal/Vertical 在 AdaptiveStack 切轴时不得把宽度 basis 当高度；主轴参数只由对应的最近布局容器消费。
- [x] 固定项与 min 总和无法容纳时报告可定位溢出，不生成负尺寸，不悄悄改变旧固定布局；同步补公开 API 文档。

## 测试与验收

- [x] 不同 grow/shrink、冻结、多次再分配、零 basis、min/max 边界以及小数宽度的真实子项 Frame 正确。
- [x] 真实 Skia Text 在压缩后换行并增加高度；TextField/Slider 伸缩后点击、光标与拖动坐标正确。
- [x] 填充/固定尺寸/嵌套修饰符与轴向都有回归；旧 Stack 行为与全部既有测试保留。

## 实施记录

### 公开 API

- `.Flex(grow = 1, shrink = 1, basis = null, min = 0, max = 正无穷, axis = FlexAxis.Main)`（`Views/Flex.cs`、`Views/Modifiers.cs`）：不可变 `FlexView`，参数在声明时校验（有限非负、`min ≤ max`、未知轴抛异常）。
- `.FillWidth()`（`FillWidthView`）：横向有界时占满/参与伸缩，宽度无界时回退自然尺寸，不取消内部硬 `Frame`。
- `FlexAxis`：`Main` 由最近布局容器主轴解析，`Horizontal`/`Vertical` 固定轴向。

### 内部契约

- `StackLayout.Measure` 分三步：自然尺寸（intrinsic）测量 → 弹性分配（`Allocate`/`Grow`/`Shrink`，min/max 冻结后再分配）→ 按最终主轴长度紧约束复测全部子项；`node.ChildMainAllocation`/`AllocatedMainExtent` 冻结分配结果，`ArrangeFlex` 在主轴长度一致时直接复用。
- 自然尺寸测量登记 `MeasureProposalKind.Intrinsic`（`LayoutContext.CurrentProposal`/`IsEffectiveProposal` 在测量期间也返回 Intrinsic）：不是最终提案，不能被同约束的有效测量复用，也不会被当作有效布局触发后续动态内容。缓存复用同时要求“约束 + 算法模式（自然尺寸/有效分配）”一致：探测可复用同模式同约束的任意已有结果，实际测量只复用同类结果；`ViewNode.LastMeasureIsIntrinsic` 记录算法模式（与 `LastProposalKind` 的临时/最终性质正交），探测作用域内的自然测量因此不会复用到有效分配缓存。
- 普通容器（无直接 Flex 的 Stack）在自然测量后，对“子树含 Flex 声明”的子项执行有效复测，并用复测后的最终尺寸重新汇总自身 `DesiredSize`，使嵌套 Flex 真正分配、换行高度向上传递；不含 Flex 的子树保持原有测量工作量。
- 同轴嵌套：外层 `Flex`/`FillWidth` 完整覆盖内层弹性声明（grow/shrink、basis、min/max、Fill 标记），只保留硬性 `Frame` 边界（`AxisLayoutFeatures.HardMinSize`/`HardMaxSize`）；`FillWidth` 覆盖内层 basis 后基准回到自然尺寸。
- 压缩权重使用“初始经 min/max 约束后的 basis”（`Allocate` 中冻结 `shrinkBasis`），冻结再分配期间不随尺寸变化。
- `ArrangeFlex` 的交叉轴偏移叠加父容器 `bounds.X`/`bounds.Y`（绘制与命中共用节点 `Frame`）。
- `MeasureProbeScope.MeasureSnapshot` 恢复 `ChildMainAllocation`/`AllocatedMainExtent`，与 `DesiredSize`、`ViewCache`、`Environment`、`MeasureValid`、`LastConstraints`、`LastProposalKind`、`LastMeasureIsIntrinsic` 一起覆盖嵌套探测与异常退出。

### 验证命令与退出码（顺序执行，`--no-restore -m:1 -nodeReuse:false`）

| 命令 | 结果 |
|---|---|
| `dotnet build CSharp.UI.slnx --no-restore -m:1 -nodeReuse:false` | exit 0，0 错误（仅既有 NU1900 漏洞服务网络警告与 xUnit 分析器警告） |
| `dotnet exec tests/CSharp.UI.Tests/bin/Debug/net10.0/CSharp.UI.Tests.dll` | exit 0，227 Total / 0 Failed |
| `dotnet exec tests/CSharp.UI.Text.Tests/bin/Debug/net10.0/CSharp.UI.Text.Tests.dll` | exit 0，67 Total / 0 Failed |
| `dotnet exec tests/CSharp.UI.Integration.Tests/bin/Debug/net10.0/CSharp.UI.Integration.Tests.dll` | exit 0，18 Total / 0 Failed |
| `dotnet exec tests/CSharp.UI.OpenTK.Tests/bin/Debug/net10.0/CSharp.UI.OpenTK.Tests.dll` | exit 0，188 Total / 0 Failed |
| `dotnet build artifacts/responsive-work/flex-invariants/Verify.csproj --no-restore -p:BuildProjectReferences=false -p:NuGetAudit=false -m:1 -nodeReuse:false`（Codex 独立探针构建） | exit 0（1 条既有 NU1900 警告） |
| `dotnet run --project artifacts/responsive-work/flex-invariants/Verify.csproj --no-build`（Codex 独立探针，18 项） | exit 0，18/18 PASS |

> 说明：`dotnet test` 在 Windows 沙箱内因 Microsoft.Testing.Platform 的命名管道被拒而无法运行（`UnauthorizedAccessException`），因此按工程直接执行已构建的测试程序集，数量与失败计数取自 runner 输出。

### 测试数量（真实 runner 输出）

| 工程 | Total | Failed | Skipped |
|---|---|---|---|
| CSharp.UI.Tests | 227 | 0 | 0 |
| CSharp.UI.Text.Tests | 67 | 0 | 0 |
| CSharp.UI.Integration.Tests | 18 | 0 | 0 |
| CSharp.UI.OpenTK.Tests | 188 | 0 | 0 |
| 合计 | 500 | 0 | 0 |

基线 461（RL-01 复审结论）；RL-02 新增 39：核心 36（`tests/CSharp.UI.Tests/FlexLayoutTests.cs` 33 例 + `LayoutProbeTests.cs` 3 例）、集成 3（`tests/CSharp.UI.Integration.Tests/FlexIntegrationTests.cs`）。其中 10 例核心与 1 例集成是本次复审修复新增，另外更新 RL-01 既有提案断言 7 处（6 处核心 + 1 处集成）。

### 复审修复对照

| 复审项 | 修复 | 回归 |
|---|---|---|
| 普通 VStack/ScrollView 内 Flex HStack 停在 intrinsic | 自然测量登记 Intrinsic 提案 + 普通容器向含 Flex 子树传递有效复测 | `NestedFlex_InsidePlainContainer_ReceivesEffectiveAllocation`、探针第 1 项 |
| ArrangeFlex 缺父容器交叉轴原点 | 交叉轴偏移叠加 `bounds.X`/`bounds.Y` | `ArrangeFlex_AddsParentCrossAxisOrigin_AcrossAxesAndAlignments`、`ArrangeFlex_DrawAndHitTestUseSameActualPosition`、探针第 2 项 |
| Probe 复用未区分“算法模式”与“临时结果” | `LastMeasureIsIntrinsic` 与 `LastProposalKind` 分开保存；复用要求约束 + 算法模式一致 | `ProbeInsideIntrinsicScope_DoesNotReuseEffectiveAllocationCache`、探针第 4 项 |
| Probe 未恢复分配缓存 | 快照补齐 `ChildMainAllocation`/`AllocatedMainExtent` | `Probe_RestoresFlexibleAllocationCacheIncludingNestedAndExceptionPaths`、探针第 5 项 |
| shrink 权重用原始 basis | 冻结“初始约束后 basis”作为压缩权重 | `Shrink_WeightsUseInitiallyConstrainedBasis`、探针第 7 项 |
| 外层同轴 Flex 未覆盖内层弹性上下限 | 硬/弹性边界分离（`HardMinSize`/`HardMaxSize`），外层完整覆盖弹性配额 | `OuterSameAxisFlex_ReplacesInnerElasticQuotaButKeepsHardFrame`、探针第 9 项 |
| FillWidth 仍保留内层 basis、Flex 残留内层 Fill | FillWidth 重置 basis 到自然尺寸；Flex 清除同轴 Fill 标记 | `FillWidth_ResetsInnerElasticBasisAndClearsInnerFill`、探针第 10 项 |
| 有效复测后未重新汇总最终 DesiredSize | 普通容器在复测后用最终尺寸重新汇总 | `NestedFlex_PropagatesFinalWrappedHeightToPlainAncestors`、探针第 14 项 |
| 视图可见的提案入口未排除 Intrinsic | `CurrentProposal` 增加 `Intrinsic`，新增 `IsEffectiveProposal` | `NaturalMeasurement_IsVisibleToViewsAsIntrinsicProposalOnly`、探针第 15 项 |

RL-01 断言按新契约更新：普通 Stack 子项的自然测量现在是 `Intrinsic` 提案（不再是 `Effective`），涉及 `LayoutProbeTests` 6 处与 `LayoutProbeIntegrationTests` 1 处；缓存/恢复/环境/排版行为不变，`BeginProbe+MeasureFresh`、缓存命中环境恢复等断言保持通过。

### 适用平台与未验证边界

- Windows 本机 .NET 10；核心/文本/集成测试不需要窗口或 GL，OpenTK 套件使用本机 OpenGL 3.3。
- 未实现（后续任务负责）：Flow、AdaptiveStack、Responsive 与 `ResponsiveInfo`；本任务只交付 Flex/FillWidth 与所需内部契约。
- 未验证：macOS/Linux 原生、三平台人工视觉验收、真实窗口 DPI 组合。
- 提交：RL-02 单一提交（含复审修复），SHA 见 `git log -1`。
