# Markdown 默认排版间距增量计划

日期：2026-10-26
基线：RT-01~04、MD-01~06 全部完成（docs/task/README.md 状态不变，不改历史已完成任务）。

## 背景

用户反馈 Markdown 渲染"太紧凑"。源码定位（本轮回读确认）：

- `BlockAdapter.BuildDocument` 根 `VStack` 无 `Spacing`——块与块零间距。
- `AdaptList` items 容器与 item 内 content `VStack` 均无 `Spacing`——列表项之间、item 内多块零间距。
- `AdaptQuote` inner `VStack` 无 `Spacing`。
- 标题无上下 margin——节与节无层次分隔，首块也无需顶部空白。
- `MarkdownTable` `CellPadding` 仅水平参与列宽，行高无垂直 padding，行间仅 1px 分隔条。

## 方案（最小变更，保留全部 public API 形状）

### `MarkdownStyleOptions` 新增字段（遵既有 `*Padding`/`*Gap`/`Spacing` 命名）

| 字段 | 默认 | 语义 |
|---|---|---|
| `BlockSpacing` | 12 | 非标题块的"after"间距（块间基线） |
| `HeadingSpacingBefore` | 20 | 标题"before"间距（节间分隔） |
| `HeadingSpacingAfter` | 8 | 标题"after"间距（标题贴近其正文） |
| `ListItemSpacing` | 4 | 列表项之间间距（items VStack.Spacing） |
| `CellVerticalPadding` | 6 | 表格 cell 单侧垂直 padding |

### 间距模型：margin-collapse（`gap = max(prev.after, next.before)`）

- 每块贡献 `before`/`after`：`before` 仅标题非零（`HeadingSpacingBefore`）；`after` = 标题 `HeadingSpacingAfter` / 其他 `BlockSpacing`。
- 容器（文档根 / 引用 inner / 列表项内容 / 表格 cell）在相邻子块间插 `Spacer(gap)`；首块前不插 Spacer（首块标题无顶部空白），gap ≤ 0 不产节点。
- 效果：段落↔段落 12；段落→标题 max(12,20)=20；标题→段落 max(8,0)=8 < 12 —— 层次成立。
- 嵌套列表 item 内多块沿用同一模型（不双重累积段落 margin）；item 之间用 `ListItemSpacing`（VStack.Spacing）。

### `MarkdownTable` 垂直 padding

- 构造新增 `cellVerticalPadding`（可选参数，默认 6，由 adapter 传入 `style.CellVerticalPadding`）。
- `Measure`：`rowHeights[r] = maxCellH + 2×vp`；`Arrange`：cell 排到 `y+vp`、高 `rowH−2×vp`；分隔条仍占整宽、位于行间。
- 列 min/pref 与 `AllocateColumns` 完全不受影响（垂直 padding 不进宽度）。
- `LayoutPropertiesEquivalent` 增加 `_cellVerticalPadding` 比较——同 doc 换 `Style` 立即触重 measure。

### 明确不做

- 不改 `RichText.LineHeight`：`RichTextLayoutRequest.LineHeight` 是绝对逻辑像素、有字高钳制；Build 期拿不到 resolved 字体度量，不能把倍率当像素填——本轮不动行高。
- 不改 Core/ScrollView/Responsive/Flow；不建通用 spacing 引擎；不给 Gallery 外层加 Padding 掩盖模块默认。
- 不新增 Markdown 语法/字体编辑/动画。

## 验证

- 永久测试只测消费者可观察几何/配置驱动行为（不 pin 内部视图树结构、Spacer 节点、
  `VStack.Spacing` 字段或默认字面值）：
  - 表格 `CellVerticalPadding` 经真实 `UiRuntime` measure：`vp 0→10` →
    `DesiredSize.Height` +40 精确、`Width` 不变（`TableTests.Table_垂直Padding_入行高不入列宽`）。
  - 块距/层次/间距关闭等行为**不留** pin `Spacer`/`ChildrenCount` 的永久 wiring 断言——
    由实际布局与 GL smoke 承担验证。
- 真实 smoke：`MarkdownDemo` sample 保持现状（模块默认即生效），由独立验证跑真实
  OpenGL 窗口 + 320/1024 宽度 + 亮/暗主题 + DPI 矩阵目测。
- 指南 `docs/guide/markdown.md` 样式选项表更新。
