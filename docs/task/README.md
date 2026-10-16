# 响应式布局任务索引

创建日期：2026-10-16

设计依据：[通用响应式布局计划](../plan/2026-10-16-responsive-layout-plan.md)。用户已授权继续拆分与实施。

本轮9个本地任务按依赖顺序执行；文档建立不表示功能已完成。首版14个任务及索引/汇总共16个文件已按AGENTS要求从本目录移出，保存在[首版历史任务索引](../archive/2026-10-14-framework-tasks/README.md)。

## 任务与依赖

| 完成 | ID | 任务 | 依赖 | 阶段 |
|---|---|---|---|---|
| ☑ | RL-01 | [测量提案与尺寸特征基础](01-layout-proposals-and-sizing.md) | 无 | A |
| ☑ | RL-02 | [Flex 与 FillWidth](02-flex-and-fill-width.md) | [RL-01](01-layout-proposals-and-sizing.md) | A |
| ☐ | RL-03 | [Flow 自动换行容器](03-flow-layout.md) | [RL-02](02-flex-and-fill-width.md) | B |
| ☐ | RL-04 | [AdaptiveStack 稳定布局切换](04-adaptive-stack.md) | [RL-02](02-flex-and-fill-width.md) | C |
| ☐ | RL-05 | [Responsive 安全内容构建事务](05-responsive-transactions.md) | [RL-02](02-flex-and-fill-width.md) | D |
| ☐ | RL-06 | [Responsive 状态依赖与生命周期](06-responsive-state-and-lifetime.md) | [RL-05](05-responsive-transactions.md) | D |
| ☐ | RL-07 | [示例改造与使用指南](07-responsive-gallery-and-guides.md) | [RL-03](03-flow-layout.md)、[RL-04](04-adaptive-stack.md)、[RL-06](06-responsive-state-and-lifetime.md) | E |
| ☐ | RL-08 | [输入、DPI 与图形集成回归](08-responsive-integration.md) | [RL-07](07-responsive-gallery-and-guides.md) | F |
| ☐ | RL-09 | [完整验收与交付记录](09-responsive-delivery.md) | [RL-08](08-responsive-integration.md) | F |

## 阶段分组

- A：RL-01、RL-02，测量契约与弹性尺寸。
- B：RL-03，自动换行。
- C：RL-04，稳定身份布局切换。
- D：RL-05、RL-06，安全响应式内容与状态依赖。
- E：RL-07，示例与指南。
- F：RL-08、RL-09，输入/图形集成与交付。

## 完成清单

- [x] [RL-01](01-layout-proposals-and-sizing.md) 测量提案与尺寸特征基础
- [x] [RL-02](02-flex-and-fill-width.md) Flex 与 FillWidth
- [ ] [RL-03](03-flow-layout.md) Flow 自动换行容器
- [ ] [RL-04](04-adaptive-stack.md) AdaptiveStack 稳定布局切换
- [ ] [RL-05](05-responsive-transactions.md) Responsive 安全内容构建事务
- [ ] [RL-06](06-responsive-state-and-lifetime.md) Responsive 状态依赖与生命周期
- [ ] [RL-07](07-responsive-gallery-and-guides.md) 示例改造与使用指南
- [ ] [RL-08](08-responsive-integration.md) 输入、DPI 与图形集成回归
- [ ] [RL-09](09-responsive-delivery.md) 完整验收与交付记录

## 执行与审核

Codex规划与独立审核；DeepSeek Harness实现和测试，通过后英文提交。前置任务复审通过后才能执行依赖任务。各任务同步补回归，RL-09负责整体集成验收。

当前基线441项测试；已有HStack/VStack/Frame默认语义、R16/R17文字和R18回调绘制保持。所有新能力显式启用。

首次API冻结还包括：Flex默认grow/shrink为1；显式硬Frame不被Flex覆盖；Flow的FillWidth占整行，不参与逐行权重分配；Responsive同宽描述更新需重建一次，而普通重复测量不得因闭包引用重新构建。

每阶段记录实际命令/数量/退出码，区分程序化、原生循环与人工真机验证；保留用户原有AGENTS.md修改，不推送。
