# RL-02：Flex 与 FillWidth

创建日期：2026-10-16

状态：待开始

阶段：A

设计依据：[响应式布局计划](../plan/2026-10-16-responsive-layout-plan.md)

[返回任务索引](README.md)

## 目标与范围

让 Stack 中显式声明的弹性控件按剩余空间增长或压缩，并在最终宽度下重新测量文本。

## 前置依赖

[RL-01](01-layout-proposals-and-sizing.md) 完成并通过复审；使用其已经固定的契约。

## 实施清单与接口交接

- [ ] 实现 FlexAxis(Main/Horizontal/Vertical)、不可变 Flex 修饰及扩展 .Flex(grow=1, shrink=1, basis=null, min=0, max=PositiveInfinity, axis=Main)；参数校验遵循计划。
- [ ] 实现基于约束后 basis 的增长/压缩、min/max 冻结与剩余空间再分配；零 basis 的零压缩量、Spacer 共存和无界主轴行为明确。
- [ ] 按最终分配长度再次测量子项，重新计算多行文本高度和 Stack 交叉轴尺寸；Arrange 使用分配结果，不只压缩旧 Frame。
- [ ] 实现 .FillWidth()：有界宽度占满、横向 Stack 中参与伸缩、无界回退自然尺寸。它不取消内部硬 Frame。同轴 Flex/FillWidth 外层生效，不叠加权重。
- [ ] 显式 Horizontal/Vertical 在 AdaptiveStack 切轴时不得把宽度 basis 当高度；主轴参数只由对应的最近布局容器消费。
- [ ] 固定项与 min 总和无法容纳时报告可定位溢出，不生成负尺寸，不悄悄改变旧固定布局；同步补公开 API 文档。

## 测试与验收

- [ ] 不同 grow/shrink、冻结、多次再分配、零 basis、min/max 边界以及小数宽度的真实子项 Frame 正确。
- [ ] 真实 Skia Text 在压缩后换行并增加高度；TextField/Slider 伸缩后点击、光标与拖动坐标正确。
- [ ] 填充/固定尺寸/嵌套修饰符与轴向都有回归；旧 Stack 行为与全部既有测试保留。

## 实施记录

实现时填写：公开/内部接口、验证命令和退出码、测试数量、适用平台与未验证边界、提交SHA。实现完成标为“待Codex复审”，复审通过后再更新为“已完成”。
