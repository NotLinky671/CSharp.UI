> **历史归档**（2026-10-25 移自 `docs/task/`）：本任务已完成并验收通过，仅供历史追溯；当前任务索引见 [docs/task/README.md](../../task/README.md)。

# FS-05：示例（ControlGallery Floating 演示）

创建日期：2026-10-23

状态：**已实施并主审验收通过**（最终验收 2026-10-24）——交付记录 [docs/delivery/2026-10-23-floating-scrollbar-delivery.md](../../delivery/2026-10-23-floating-scrollbar-delivery.md)；审查记录 [docs/return/2026-10-23-floating-scrollbar-review.md](../../return/2026-10-23-floating-scrollbar-review.md)。

阶段：C

设计依据：[Floating 滚动条计划](../../plan/2026-10-23-floating-scrollbar-plan.md) §决策一/十三。

[返回任务索引](README.md)

前置依赖：FS-02、FS-03、FS-04。

## 文件范围

- `samples/CSharp.UI.Samples.ControlGallery/`（演示用例；遵循既有 Key 注入惯例便于测试定位）。

## 实现契约

- ControlGallery 增加一个 Floating 演示区（ScrollView + TextEditor 各一，`ScrollbarStyle = ScrollbarStyle.Floating`），可滚动内容溢出；保留既有 persistent 演示不动（双式并存对照）。
- 演示不引入新控件/新公开 API 用法之外的东西；不改其它 sample（Counter/TodoList 不动）。
- 注入定位 `Key`（如 `floating-scrollbar-demo`），与既有 gallery key 命名一致。

## 验收

- Gallery fresh 构建 Debug/Release 0 警 0 错；真实窗口 smoke exit 0。
- 演示区实际滚轮滚动可见 floating bar、停止隐藏、hover 续命、无 layout 槽（内容贴边）。
