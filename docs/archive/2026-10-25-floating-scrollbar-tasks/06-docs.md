> **历史归档**（2026-10-25 移自 `docs/task/`）：本任务已完成并验收通过，仅供历史追溯；当前任务索引见 [docs/task/README.md](../../task/README.md)。

# FS-06：文档更新

创建日期：2026-10-23

状态：**已实施并主审验收通过**（最终验收 2026-10-24）——交付记录 [docs/delivery/2026-10-23-floating-scrollbar-delivery.md](../../delivery/2026-10-23-floating-scrollbar-delivery.md)；审查记录 [docs/return/2026-10-23-floating-scrollbar-review.md](../../return/2026-10-23-floating-scrollbar-review.md)。

阶段：C

设计依据：[Floating 滚动条计划](../../plan/2026-10-23-floating-scrollbar-plan.md)。

[返回任务索引](README.md)

前置依赖：FS-02~FS-05。

## 文件范围

- `docs/guide/api-reference.md`：`ScrollbarStyle` 枚举与两 owner 的 init 属性（默认 `Standard`；Floating 语义：overlay、实际滚动显示、~1s 隐藏、hover/drag 续命）。
- `README.md`（及 `README.zh-CN.md` 若有对应段）：`ScrollView`/`TextEditor` 滚动条描述更新（persistent 为默认、Floating 可选）。
- `docs/guide/` 其它命中滚动条描述的指南（如 testing/platform 提及处）最小同步。
- `docs/plan/2026-10-23-floating-scrollbar-plan.md` 状态段按实施结果更新。

## 实现契约

- 文档只写消费者可见契约：两枚举值语义、默认值、显示/隐藏规则（实际滚动触发、约 1 秒、hover/drag 保持）、不占布局槽、hidden 不拦命中；不承诺毫秒级精确与性能数字。
- 不写内部机制（TimedWakes/HitTestOverlay）为公开 API；可在 plan 内已有。
- 中文/英文文档同步原则与仓库现状一致（README 双语、guide 现行语言）。

## 验收

- 文档与实现行为一致；链接（plan↔task↔archive）有效；无残留"待授权"字样。
