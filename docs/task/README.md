# 多行文本编辑器 TextEditor 任务索引

创建日期：2026-10-23

设计依据：[多行文本编辑器计划](../plan/2026-10-23-text-editor-plan.md)。本文档与任务文件均为设计实施规格；文档建立不表示功能已实现。

状态：TE-01~TE-07 全部完成。真实 Windows IMM + 微软拼音验收通过（28/28 check,outcome=PASS;证据 [2026-10-23-te06-real-ime-evidence](../return/2026-10-23-te06-real-ime-evidence/)）;最终构建 0 警 0 错、四套测试 1409×2 全绿、6 次示例 smoke exit 0;交付记录见 [2026-10-23-text-editor-delivery](../delivery/2026-10-23-text-editor-delivery.md),最终提交待主审 gate。旧 TI-01～TI-10 文本输入任务组已归档至 [docs/archive/2026-10-23-text-input-editing-tasks/](../archive/2026-10-23-text-input-editing-tasks/README.md),仅作历史追溯。

## 任务与依赖

| 完成 | ID | 任务 | 前置依赖 | 阶段 |
|---|---|---|---|---|
| ☑ | TE-01 | [编辑排版模式与多行净化契约](01-editing-layout-and-sanitize.md) | 无 | A |
| ☑ | TE-02 | [IME 客户端泛化与桥单行假设审计](02-ime-client-cutover.md) | TE-01 | A |
| ☑ | TE-03 | [TextEditor 控件核心](03-text-editor-core.md) | TE-02 | B |
| ☑ | TE-04 | [TextEditor IME 接入](04-text-editor-ime.md) | TE-03 | B |
| ☑ | TE-05 | [示例与文档](05-samples-and-guides.md) | TE-03 | C |
| ☑ | TE-06 | [图形集成与 Windows 真实验收](06-graphics-and-real-ime.md) | TE-04、TE-05 | D |
| ☑ | TE-07 | [交付](07-delivery.md) | TE-06 | D |

## 阶段门槛

- A：编辑排版契约（尾随空段、行内 caret 表、affinity、逐行选区矩形、归一化）与 IME 客户端泛化落地；TextField 全部既有测试与 IME 回归绿。
- B：TextEditor 控件可用（键盘/指针/滚动/历史/接缝），IME 协议与 TextField 等价。
- C：ControlGallery 多行编辑区与四份指南更新齐。
- D：Windows 真机证据（IMM mspinyin 多行组合/候选锚点/取消/唯一提交/嵌套滚动）与交付记录齐。

## 执行约定

- 每个任务经主审独立复审通过才开依赖任务；返修写 `docs/return/` 带日期文件循环。
- TE-03/TE-04 串行（同 `TextEditor.cs`）；TE-05 仅碰 samples/guide 可与 TE-04 并行。
- TE-06 真实 IME 为硬门槛：不可以用测试绿、合成消息或截图普通文本代替真实输入法验收；非 Windows 如实记 committed-only/待真机。
- 所有任务只承诺消费者可观察行为与既有计数器（`LayoutPassCount`/纹理/排版缓存/资源计数），不承诺伪精确性能数字。
