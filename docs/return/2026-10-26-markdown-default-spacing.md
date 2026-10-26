# Markdown 默认排版间距修复记录

日期：2026-10-26
计划：[docs/plan/2026-10-26-markdown-default-spacing-plan.md](../plan/2026-10-26-markdown-default-spacing-plan.md)

## 源码依据（修复前）

- `BlockAdapter.BuildDocument` 根 `VStack` 无 `Spacing`——块间零间距。
- `AdaptList` items `VStack`、item 内 content `VStack` 均无 `Spacing`。
- `AdaptQuote` inner `VStack` 无 `Spacing`。
- 标题无 before/after 边距——节与节无层次。
- `MarkdownTable._cellPadding` 仅水平：进列宽 min/pref 与 x 偏移；行高 = cell 内容高，行间仅 1px 分隔条。

## 采取方案与默认值

**margin-collapse 块距模型**：每块 `before`/`after`，相邻块 `gap = max(prev.after, next.before)`，
容器内插 `Spacer(gap)`；首块前不插（首块标题无顶部空白）。同一 `AdaptSequence` 复用于
文档根、引用 inner、列表项内容、表格 cell——嵌套列表沿用同模型，不双重累积段距。

| 字段 | 默认 | 取舍 |
|---|---|---|
| `BlockSpacing` | 12 | 段落/列表/引用/代码/表格等块间基线 |
| `HeadingSpacingBefore` | 20 | 节间分隔 > 块间 12（层次） |
| `HeadingSpacingAfter` | 8 | 标题贴正文 < 块间 12（层次） |
| `ListItemSpacing` | 4 | 列表项紧凑成组，小于块间 |
| `CellVerticalPadding` | 6 | cell 单侧垂直 pad，入行高不进列宽 |

**不改 `RichText.LineHeight`**：其语义为绝对逻辑像素（0=行内最大字高，显式值按字高钳制），
Build 期无 resolved 字体度量，倍率无法正确换算；块距已解决"太紧凑"主诉，行高不动。

## 修改路径

- `src/CSharp.UI.Markdown/MarkdownStyleOptions.cs`：+5 字段（上表）。
- `src/CSharp.UI.Markdown/BlockAdapter.cs`：`BuildDocument` → `AdaptSequence`（margin-collapse
  + 可选 `postProcess` 钩子）；`AdaptList`/`AdaptQuote`/表格 cell 全部走 `AdaptSequence`；
  cell 对齐/header-bold 提取为 `PostProcessCellBlock`；列表 items `VStack.Spacing = ListItemSpacing`。
- `src/CSharp.UI.Markdown/MarkdownTable.cs`：构造 +`cellVerticalPadding`（clamp ≥0）；
  `Measure` 行高 `+ 2×vp`；`Arrange` cell 排 `y+vp`、高 `rowH−2×vp`；分隔条仍在行间整宽；
  `LayoutPropertiesEquivalent` +`_cellVerticalPadding`（同 doc 换 Style 触重 measure）。
  列 min/pref/`AllocateColumns` 未动——垂直 padding 不进宽度。
- `tests/CSharp.UI.Markdown.Tests/AdapterTests.cs`：首轮曾加 5 个块距测试（Spacer 存在/
  层次序/`BlockSpacing=0` 无 Spacer/`ListItemSpacing` 生效/引用内 Spacer +
  `FindInnerVStack` 树遍历 helper）——经审查全部断言内部视图树结构/字段（wiring），
  不符合永久测试约定，**已全部删除**；默认排版舒适度由实际布局与 GL smoke 承担。
- `tests/CSharp.UI.Markdown.Tests/TableTests.cs`：cell 单块不再套 VStack 壳，3 处断言
  （header bold ×2、列对齐）改为直接断言 cell 视图本身的 `RichText` 内容/属性
  （消费者可观察：bold range 覆盖、文本对齐），不再 pin 容器类型；
  +`Table_垂直Padding_入行高不入列宽`（vp 0→10：真实 UiRuntime measure，
  DesiredSize.Height +40 精确、Width 不变——消费者几何断言）。
- `docs/guide/markdown.md`：样式选项表 +4 行；表格布局一节行高含垂直 padding。

## 验证结果（最终实测）

- `dotnet build CSharp.UI.slnx` Debug + Release 新鲜构建：0 警告 0 错误。
- `CSharp.UI.Markdown.Tests`：**49 总 / 0 失败，Debug+Release 两 cfg 均绿**
  （删 5 个 wiring 断言后由独立验证实跑确认）。
- `CSharp.UI.Tests` 698 / 0、`CSharp.UI.Integration.Tests` 53 / 0（Debug）。
- 独立真实 OpenGL smoke：**76 项 fixture PASS / 0 FAIL**（真实 GL + 真实 sample），
  证据 `docs/return/2026-10-26-markdown-spacing-evidence/`。
  - 12 组合截图矩阵（light/dark × 320/1024 × PixelScale 1/1.5/2）已生成；
    主审抽查亮/暗 1024 顶部、320 表格及 400 默认完整块 fixture 视觉接受。
  - 实测几何：标题→正文 gap 8，正文→标题 gap 20，段落↔段落 12；
    自定义 Style 后 gap 8→16；表格行带 49.24→61.24（+12=2×vp）且列宽不变；
    真实 UseState Style swap 中 gap 8→16；320→1024→320 布局正确；链接点击 echo 成立。
- `dotnet test`（MSBuild 节点 IPC）本机命名管道 Access denied——环境限制，
  测试通过项目 runner（`dotnet run --no-build`）或生成的测试 exe 运行。
- 历史 harness 外跑（run1–4）失败属 harness/采集问题非产品缺陷；stdout collector
  有已知 caveat，详见独立验证证据目录（direct1 返回码未独立记录；76/0 有 raw
  输出，run5 实际 exit 0）。

## 剩余风险

- 行高：本轮保留字体自然行高（`RichText.LineHeight`=0 语义不动）——修复点是
  块间距与表格留白，未引入行高配置。
- macOS/Linux 未实际运行（沿用项目既有平台边界声明）。
- 无未完项。
