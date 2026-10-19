# 文本输入与编辑基础任务索引

创建日期：2026-10-17

设计依据：[IME、编辑事务与撤销重做计划](../plan/2026-10-17-text-input-and-editing-plan.md)。

状态：TI-01 接入路线经 Codex 审核通过（探针86项测试通过；用户免除微软拼音测试）；TI-02～TI-05均经Codex独立复审通过。TI-04验收记录见[组合状态机审核](../return/2026-10-18-ti04-composition-review.md)；TI-05于2026-10-19完成：868项工程测试、49项独立检查及41项TI-04兼容性检查通过，见[撤销重做审核](../return/2026-10-19-ti05-undo-redo-review.md)。TI-06～TI-10 待开始。Windows候选实际定位、取消与生产宿主验收保留至TI-07/TI-10。

## 任务与依赖

| 完成 | ID | 任务 | 前置依赖 | 阶段 |
|---|---|---|---|---|
| ☑ | TI-01 | [平台可行性与 Windows 原型](01-platform-feasibility.md) | 无 | A |
| ☑ | TI-02 | [冻结编辑与文本输入契约](02-editing-and-input-contracts.md) | [TI-01：平台可行性与 Windows 原型](01-platform-feasibility.md) | B |
| ☑ | TI-03 | [统一编辑事务与 Binding 同步](03-atomic-editing-and-binding.md) | [TI-02：冻结编辑与文本输入契约](02-editing-and-input-contracts.md) | B |
| ☑ | TI-04 | [组合会话与预编辑状态机](04-composition-state-machine.md) | [TI-03：统一编辑事务与 Binding 同步](03-atomic-editing-and-binding.md) | B |
| ☑ | TI-05 | [有界撤销重做与编辑命令](05-undo-redo-and-edit-commands.md) | [TI-04：组合会话与预编辑状态机](04-composition-state-machine.md) | C |
| ☐ | TI-06 | [预编辑绘制、命中与候选几何](06-preedit-rendering-and-geometry.md) | [TI-04：组合会话与预编辑状态机](04-composition-state-machine.md) | C |
| ☐ | TI-07 | [Windows IME 桥与宿主输入联调](07-windows-ime-and-host-routing.md) | [TI-01：平台可行性与 Windows 原型](01-platform-feasibility.md)、[TI-05：有界撤销重做与编辑命令](05-undo-redo-and-edit-commands.md)、[TI-06：预编辑绘制、命中与候选几何](06-preedit-rendering-and-geometry.md) | C |
| ☐ | TI-08 | [跨平台能力查询与兼容回退](08-platform-capabilities-and-fallback.md) | [TI-07：Windows IME 桥与宿主输入联调](07-windows-ime-and-host-routing.md) | D |
| ☐ | TI-09 | [输入示例、使用指南与验收工具](09-samples-guides-and-ime-tests.md) | [TI-08：跨平台能力查询与兼容回退](08-platform-capabilities-and-fallback.md) | D |
| ☐ | TI-10 | [完整回归、真实 IME 验收与交付](10-final-validation-and-delivery.md) | [TI-09：输入示例、使用指南与验收工具](09-samples-guides-and-ime-tests.md) | D |

## 完成清单

- [x] [TI-01：平台可行性与 Windows 原型](01-platform-feasibility.md)
- [x] [TI-02：冻结编辑与文本输入契约](02-editing-and-input-contracts.md)
- [x] [TI-03：统一编辑事务与 Binding 同步](03-atomic-editing-and-binding.md)
- [x] [TI-04：组合会话与预编辑状态机](04-composition-state-machine.md)
- [x] [TI-05：有界撤销重做与编辑命令](05-undo-redo-and-edit-commands.md)
- [ ] [TI-06：预编辑绘制、命中与候选几何](06-preedit-rendering-and-geometry.md)
- [ ] [TI-07：Windows IME 桥与宿主输入联调](07-windows-ime-and-host-routing.md)
- [ ] [TI-08：跨平台能力查询与兼容回退](08-platform-capabilities-and-fallback.md)
- [ ] [TI-09：输入示例、使用指南与验收工具](09-samples-guides-and-ime-tests.md)
- [ ] [TI-10：完整回归、真实 IME 验收与交付](10-final-validation-and-delivery.md)

## 阶段门槛

- A：TI-01路线审核已通过，依据现有WeType真实预编辑/结果、消息来源与三平台矩阵；候选实际定位与取消验收移交TI-07/TI-10，微软拼音不再是门槛。
- B：TI-02～TI-04交付核心契约、原子编辑与组合状态。
- C：TI-05～TI-07交付历史、预编辑几何与Windows原生桥；可独立工作以依赖表为准。
- D：TI-08～TI-10交付平台回退、示例文档、真实IME和最终构建证据。

## 执行与审核

Codex规划并提交推送文档；DSH按任务实现与单元测试，自测后等待Codex审核。问题写docs/return带日期文件并循环返修，只有审核通过才由DSH英文提交并推送实现。前置任务审核通过才能开始依赖任务。用户已授权继续实施；每项仍须自测及Codex复审，不能预先宣称完成。

基线以TI-01启动时构建测试为准，历史参考639项。Windows使用当前可用输入法进行完整IME验收，不安装、启用或测试用户已移除的微软拼音；macOS/Linux本轮交付能力声明与已提交文本回退，不宣称三平台完整组合输入。多行TextEditor不在范围。

最终交付由Codex写docs/delivery/2026-10-17-text-input-and-editing-delivery.md及同名前缀-evidence目录；完成前不创建假交付结论。

## 历史任务

上一轮9份响应式任务及索引已从本目录移出并归档到[响应式任务历史](../archive/2026-10-16-responsive-layout-tasks/README.md)，原有archive未改动。旧计划和交付链接已修正；仅作为历史记录，不作为本轮实施依据。
