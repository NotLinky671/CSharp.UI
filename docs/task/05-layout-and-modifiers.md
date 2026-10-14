# 任务 05：布局、修饰符与绘制命令生成

- 任务 ID：05
- 创建日期：2026-10-14
- 状态：已完成
- 原计划：[CSharp.UI 框架设计计划](../2026-10-14-declarative-ui-framework-plan.md)
- 任务索引：[返回任务索引](README.md)

## 目标与范围

将声明式描述转换为正确的逻辑像素布局和有序绘制命令，支持组合式布局与链式修饰。

实现布局与命令生成；具体 GPU 绘制、真实文字测量、滚动交互和控件行为由后续任务接入。

## 前置依赖

- 前置任务：[任务 03：视图描述、组件与节点协调更新](03-view-reconciliation.md)、[任务 04：状态、双向绑定与更新调度](04-state-and-binding.md)。
- 接入依据：使用任务 03 的运行时节点和任务 04 的失效更新；文本测量先通过任务 02 的测试替身验证。

## 实施清单

- [x] 实现 Measure / Arrange 两阶段布局，使用父级约束得到期望尺寸，再分配实际位置与尺寸。
- [x] 实现 VStack、HStack、ZStack、Spacer 的间距、对齐与剩余空间分配；支持嵌套布局和有限/无限约束。
- [x] 实现 Padding、Frame、Background、Foreground、Border、CornerRadius、Opacity 和矩形 Clip 包装描述；Key 沿用任务 03。
- [x] 实现 Rectangle；按视图层级输出平台无关绘制命令，处理背景、内容、边框、透明度和裁剪顺序。
- [x] 明确修饰符顺序：先 Padding 再 Background 时背景覆盖内边距；修改包装树不得改变原视图描述。
- [x] 区分需要重新布局和仅需重绘的失效，保留文本测量、图片自然尺寸、Disabled 和环境继承的接入位置。

## 公开 API 与交接

交付 VStack、HStack、ZStack、Spacer、Rectangle 和上述链式修饰符；具体控件可复用布局及命令生成流程。

交付实现、对应测试及必要使用说明；所有测试与实现同步完成。后续任务消费本任务契约时保持原计划的模块边界。

## 测试与验收

- [x] 嵌套 Stack、间距、对齐、Spacer 和 Frame 的布局结果符合约束，零尺寸视口不会产生无效坐标。
- [x] Padding/Background 调用顺序产生相应不同的背景范围，Border、Opacity 和 Clip 的命令顺序正确。
- [x] ZStack 按描述顺序层叠；嵌套 Clip 取有效相交范围，空裁剪区不输出可见内容。
- [x] 在多个 DPI 下逻辑布局一致；文本替身宽度变化会重新测量，纯颜色变化不必重新布局。

完成前记录实际执行的测试与结果；未运行的检查写明原因，不将任务标为已完成。完成后更新本页状态及索引中的复选框。

## 实施与验收记录

- 状态：**已完成**（2026-10-14）。
- 交付：`Views/Stacks.cs`、`Views/Modifiers.cs`、`Views/Primitives.cs`、`Runtime/Contexts.cs`（LayoutContext/DrawContext）。
- 实际执行：`dotnet run --project tests/CSharp.UI.Tests` → `LayoutTests` 11 项全部通过：嵌套 Stack/间距/对齐、Spacer 分配、Frame 约束与居中、修饰符顺序、边框与透明度、ZStack 层叠与顶层命中、嵌套裁剪与空裁剪、跨 DPI 布局一致、纯颜色变化不重新布局、文本度量变化重新测量、零尺寸视口无非法坐标。
- 实现约定：测量只按上限收缩，最小尺寸由各视图自身声明（`Frame` 的 MinWidth/MaxWidth 等）；`ScrollView` 在滚动轴上声明弹性（fill 语义）。
