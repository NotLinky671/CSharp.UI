# SB-06：文档更新

创建日期：2026-10-23

状态：已完成（source 与相对链接一致性审核 104 相对链接、0 broken；fragment/锚点未逐条核）。scrollbar 用户文档与实现同步；与本任务组同步的 `docs/return/2026-10-23-scrollbar-review.md` 持续记录验收证据；SB-07 交付已生成最终记录。

阶段：C

设计依据：[Scrollbar 计划](../plan/2026-10-23-scrollbar-plan.md)；前置依赖见 [任务索引](README.md)（SB-02~SB-05）。

[返回任务索引](README.md)

前置依赖：SB-02~SB-05。

## 目标

文档与已实现行为一致：`ScrollView`/`TextEditor` 注释、platform-notes 限制条目、api-reference 行为说明、本计划状态。

## 文件范围

- `src/CSharp.UI/Views/ScrollView.cs`：`remarks` "首版不显示滚动条" → 改为"内容溢出显示滚动条（persistent gutter，无溢出留槽）"。
- `src/CSharp.UI/Views/TextEditor.cs`：`remarks`/相关注释补 scrollbar 行为。
- `docs/guide/platform-notes.md`：已知限制条目更新（"ScrollView 首版不绘制滚动条" → 新行为说明；persistent gutter 空白槽代价写明）。
- `docs/guide/api-reference.md`：`ScrollView`/`TextEditor` 条目补 scrollbar 行为（自动、track 翻页、drag、caret/选区/undo 不受影响）。
- `docs/guide/testing.md`：scrollbar 验收要点（hit 优先/嵌套冒泡/IME anchor）。
- `docs/plan/2026-10-23-scrollbar-plan.md`：状态"规划定稿（待用户授权实施）"→ SB-02~SB-06 完成后"已实现待最终验收"（由 SB-07 记录），SB-07 交付后"已完成"。
- `README.md`/`README.zh-CN.md`：若范围说明需补 scrollbar。

## 边界

- 不宣称非 Windows 真机结论；不写伪精确性能/内存数字；persistent gutter 空白槽如实写为取舍。
- 不改历史 archive/delivery/return 结论；公开 API 零新增（无 `ScrollbarVisibility` 等描述）。

## 验收

- 文档描述与已实现代码一致；无残留"不显示滚动条"；链接有效；persistent gutter 取舍/无 overflow 留槽/不命中内容等默认行为变化明确记录。
