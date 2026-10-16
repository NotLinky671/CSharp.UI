# RL-05：Responsive 安全内容构建事务

创建日期：2026-10-16

状态：待开始

阶段：D

设计依据：[响应式布局计划](../plan/2026-10-16-responsive-layout-plan.md)

[返回任务索引](README.md)

## 目标与范围

在测量和构建之间引入安全的响应式更新阶段，不在 Measure/Arrange 中运行任意用户 builder 或修改节点树。

## 前置依赖

[RL-02](02-flex-and-fill-width.md) 完成并通过复审；使用其已经固定的契约。

## 实施清单与接口交接

- [ ] 实现 ResponsiveInfo(AvailableWidth, IsWidthBounded) 与 Responsive(Func<ResponsiveInfo, View>)；有界宽度占满分配空间，高度自然；无界以 PositiveInfinity/false 表达。
- [ ] Responsive 节点先稳定挂载，最终有效 Measure 提案登记到去重队列；Probe 不激活内容，同轮多提案最后有效者覆盖，回到已稳定提案时撤销过时请求。
- [ ] UiRuntime.ProcessPendingWork 在安全阶段执行 builder、协调内容并再次布局，稳定后才允许 Emit。首次挂载、父宽度变化和描述更新共用机制，不递归泵事件。
- [ ] Reconciler 更新 Responsive 描述时保留原有子树直到事务消费，不能先按空 Children 清空子项；同父级 Key/类型复用规则保持。
- [ ] 输入签名包含有效宽度与描述版本；同宽重复 Measure 不重建，而同宽的新 builder 描述仍更新一次，不能误忽略捕获值变化，也不能每次创建闭包都触发布局循环。
- [ ] 先用嵌套最小原型证明提案稳定、Probe/有效复测和描述更新，再冻结内部契约；每节点重复构建预算与整体工作上限按计划验证，不把正常深层/批量节点当全局8轮错误。

## 测试与验收

- [ ] builder 只在安全阶段执行；观察到的布局/子树完整，Measure 与 Emit 不执行 builder。
- [ ] 嵌套超过8层以及批量独立 Responsive 能正常完成；同一节点真正振荡时受控诊断，不忙等或提交半成品。
- [ ] 宽度变更、同宽描述替换、首帧、无界探测转有限最终提案、卸载后的过时请求、builder 异常均覆盖。
- [ ] R18 回调即时绘制与该稳定阶段共存，不在组件重建期间重入 runtime；本任务尚不标记状态依赖能力已完成，交 RL-06。

## 实施记录

实现时填写：公开/内部接口、验证命令和退出码、测试数量、适用平台与未验证边界、提交SHA。实现完成标为“待Codex复审”，复审通过后再更新为“已完成”。
