# SB-05：示例（ControlGallery 演示）

创建日期：2026-10-23

状态：已完成（2026-10-23；`.Key` 已注入 `gallery-root-scroll`/`gallery-nested-scroll`/`scrollbar-vertical-demo` 供测试定位，演示用例代码与文档已落；SB-07 双 config fresh 构建/真机 smoke 通过；fast 独立 Gallery **真实示例 offscreen 渲染 + UiRuntime 指针/滚轮切换** 32/32——长短切换消隐、height toggle 120→180、内/外层 wheel 分流；IME 组合锚点跟随为单独 OS 真实窗口验证）。证据见 [交付记录](../delivery/2026-10-23-scrollbar-delivery.md) 与 [审查记录](../return/2026-10-23-scrollbar-review.md)。

阶段：C

设计依据：[Scrollbar 计划](../plan/2026-10-23-scrollbar-plan.md)；前置依赖见 [任务索引](README.md)（SB-02、SB-03、SB-04）。

[返回任务索引](README.md)

前置依赖：SB-02、SB-03、SB-04。

## 目标

`ControlGallery` 新增 scrollbar 演示区：vertical/horizontal ScrollView + 多行 TextEditor，覆盖 overflow show/no-overflow hide、drag/track、size change、scroll-bounce 回归、IME anchor 跟随。

## 文件范围

- `samples/CSharp.UI.Samples.ControlGallery/Program.cs`：新增 `Section("滚动条", …)`（遵循既有 `Section(title, …)` 模式）。

## 演示内容（冻结）

- `ScrollView` 纵轴：长列表溢出 → scrollbar 显示；缩短内容 → 隐藏（槽仍占）。
- `ScrollView` 横轴：宽内容溢出 → 横向 scrollbar。
- `TextEditor` 多行：行数超视口 → scrollbar；`Ctrl+End` 到底 → 滚轮上滚到顶 → 继续滚外层冒泡（scroll-bounce 回归场景）。
- IME：中文组合时 scrollbar 拖拽 → 候选窗锚点跟随。

## 边界

- 不改 `TodoList` 业务模型；不改既有 Section；persistent gutter 空白槽为预期视觉（文档写明）。
- 示例代码遵循 Gallery 风格，不新增业务。

## 验收

- `CSHARPUI_SMOKE_FRAMES=5` smoke 通过；真实窗口肉眼验证：overflow show/hide、thumb 拖拽、track 翻页、boundary 冒泡、IME anchor 移动。
- 既有 smoke 路径不破坏。
