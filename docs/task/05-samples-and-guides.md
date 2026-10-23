# TE-05：示例与文档

创建日期：2026-10-23

状态：未开始。设计依据 [TextEditor 计划](../plan/2026-10-23-text-editor-plan.md) §1、§3、§5。

[返回任务索引](README.md)

## 目标

`ControlGallery` 新增多行 `TextEditor` 编辑区；四份指南与根 README 更新。`TodoList` 仅作说明，不改模型（多行描述为后续示例扩展，非本任务）。

## 文件范围

- `samples/CSharp.UI.Samples.ControlGallery/Program.cs`：新增多行编辑演示区（占位符 + 绑定 + `.FillWidth().Frame(minHeight:…, maxHeight:…)`）。
- `docs/guide/api-reference.md`：`TextEditor` 控件条目、多行快捷键表、Editing/affinity 公开类型（`CaretPosition`/`TextAffinity`/新查询/`GetRangeRects`/`SanitizeMultiLine`）、`ITextInputClient` 面向多行的说明。
- `docs/guide/platform-notes.md`：多行 IME 边界（IMM Full 已证实 API 存在但多行真机验收在 TE-06；非 Windows committed-only；TSF 内部路径保持现状回归，不公开化）。
- `docs/guide/testing.md`：多行编辑验收要点（尾随空行/affinity/逐行矩形/嵌套滚动/真机 IME）。
- `README.md` + `README.zh-CN.md`：范围说明加 `TextEditor`；链接到新任务索引。

## 边界

- 不改 `TodoList` 业务模型；不改既有控件 API 描述（只新增）。
- 不宣称非 Windows 多行 IME；不把 TSF 内部候选写为公开选项；不写伪精确性能/内存数字。
- 示例代码遵循现有 Gallery 区块风格（`Section(title, …)` 模式），不重做业务。

## 验收

- Gallery 多行编辑区可见可用：输入多行、换行、选区、滚动；`CSHARPUI_SMOKE_FRAMES=5` smoke 通过。
- 指南描述与已实现代码一致（不提前宣称 TE-06 真机结论）；README 链接指向 `docs/task/README.md` 与新计划。
- 既有文档断言（TextField/IME/快捷键/平台能力表）不被破坏。
