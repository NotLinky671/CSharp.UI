# Scrollbar（滚动条）任务索引

创建日期：2026-10-23

设计依据：[Scrollbar 实施计划](../plan/2026-10-23-scrollbar-plan.md)。本文档与任务文件均为设计实施规格；文档建立不表示功能已实现。

状态：SB-01~SB-07 待实施（planning-only，未改 src；待用户授权开始）。TE-01~TE-07 多行文本编辑器任务组已完成并归档至 [docs/archive/2026-10-23-text-editor-tasks/](../archive/2026-10-23-text-editor-tasks/README.md)。

## 核心决策（已定，实施见各任务）
- **persistent gutter**：轨道槽恒占 `ScrollbarThickness` 宽；`overflow` 只决定画不画 track/thumb——槽位固定免宽度回流环；无 overflow 空槽仍属 owner（不透内容，wheel 仍 owner/ancestor）。
- **owner 自绘**：共享内部 helper 算几何/判定，ScrollView 与 TextEditor 各自在 `Emit`（content clip pop 后）画轨道、`OnPointer*` 处理拖拽/翻页；不新建 child View 节点（`GetChildClip` 对所有 child 生效，child scrollbar 会被裁出）。
- **组合期持久浏览意图**：`RequiresCaretReveal` 由显式操作/组合内容推进 set、用户 scroll clear——caret 裁出后多帧不弹回，`Unavailable` 期间组合保活（`WindowsImeBridge` exclusion，不 native cancel）。
- **公开 API 零新增**：无 `ScrollbarVisibility` 枚举、无公开滚动命令；行为变化 = 溢出时显示 scrollbar + persistent gutter（文档写明）。
- 详细公式（`s`=inset 后起点）、收敛规则、拖拽手势取消语义见 [计划](../plan/2026-10-23-scrollbar-plan.md)。

## 任务与依赖

| 完成 | ID | 任务 | 前置依赖 | 阶段 |
|---|---|---|---|---|
| ☐ | SB-01 | [共享滚动条几何与交互 helper](01-scrollbar-shared-metrics.md) | 无 | A |
| ☐ | SB-02 | [ScrollView 接线（persistent gutter + owner 绘/拖）](02-scrollview-wiring.md) | SB-01 | B |
| ☐ | SB-03 | [TextEditor 接线（inner 恒扣 + bar 优先 + 状态边界）](03-texteditor-wiring.md) | SB-01 | B |
| ☐ | SB-04 | [图形/输入回归（hit 优先/嵌套冒泡/边界清理/IME）](04-input-regression.md) | SB-02、SB-03 | C |
| ☐ | SB-05 | [示例（ControlGallery 演示）](05-samples.md) | SB-02、SB-03、SB-04 | C |
| ☐ | SB-06 | [文档更新](06-docs.md) | SB-02~SB-05 | C |
| ☐ | SB-07 | [交付（构建+真实 smoke+记录）](07-delivery.md) | SB-06 | D |

## 阶段门槛

- A：共享 helper 公式/夹取/最小 thumb/track 方向冻结并测试。
- B：ScrollView/TextEditor 各自接线完成（含 cancel/disable/unmount 状态清理），拖拽/翻页/边界冒泡可用。
- C：hit 优先/嵌套/IME anchor 回归 + Gallery 演示 + 文档一致。
- D：Windows 真实窗口 smoke 证据与交付记录；macOS/Linux 仅源码/CPU 编译。

## 执行约定

- 每个任务经主审独立复审通过才开依赖任务；返修写 `docs/return/` 带日期文件。
- SB-02 与 SB-03 文件不同可并行；SB-05 仅碰 samples 可与 SB-04 并行。
- 所有任务只承诺消费者可观察行为与既有计数器，不承诺伪精确性能数字；不引入 wiring/mockecho/源码字符串测试。
