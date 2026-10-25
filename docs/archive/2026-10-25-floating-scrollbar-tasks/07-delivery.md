> **历史归档**（2026-10-25 移自 `docs/task/`）：本任务已完成并验收通过，仅供历史追溯；当前任务索引见 [docs/task/README.md](../../task/README.md)。

# FS-07：交付

创建日期：2026-10-23

状态：**已实施并主审验收通过**（最终验收 2026-10-24）——交付记录 [docs/delivery/2026-10-23-floating-scrollbar-delivery.md](../../delivery/2026-10-23-floating-scrollbar-delivery.md)；审查记录 [docs/return/2026-10-23-floating-scrollbar-review.md](../../return/2026-10-23-floating-scrollbar-review.md)。

阶段：D

设计依据：[Floating 滚动条计划](../../plan/2026-10-23-floating-scrollbar-plan.md) §验收要点。

[返回任务索引](README.md)

前置依赖：FS-06。

## 文件范围

- `docs/delivery/2026-10-23-floating-scrollbar-delivery.md` + `2026-10-23-floating-scrollbar-delivery-evidence/`（SHA256SUMS、构建/测试/smoke 输出、截图）。
- `docs/plan/2026-10-23-floating-scrollbar-plan.md` 状态段更新为已交付口径。

## 实现契约

- `dotnet build CSharp.UI.slnx` Debug + Release 各 fresh 构建，0 警 0 错。
- 四套件 `CSharp.UI.Tests`/`CSharp.UI.Text.Tests`/`CSharp.UI.Integration.Tests`/`CSharp.UI.OpenTK.Tests` Debug + Release 全绿。
- 三 sample（Counter/TodoList/ControlGallery）Debug + Release fresh 构建 + 真实窗口 smoke exit 0（用户可能开 Gallery：不 kill 既有进程，用新实例/端口策略）。
- 真实 API smoke：Floating ScrollView 纵/横 + TextEditor 实际滚轮滚动显示、~1s 隐藏、hidden 命中透传、Standard 默认不变——非只测试断言（宿主真实窗口或 offscreen GL 按既有证据惯例）。
- 交付记录写范围/边界（macOS/Linux 仅源码+CPU 编译）、证据锚点、SHA256SUMS。
- 英文 commit + push，提交需主审最终批准。

## 验收

- 上述全部证据落盘且与记录一致；无半成品 scaffold、无 TODO 残留；git 状态只含本任务变更。
