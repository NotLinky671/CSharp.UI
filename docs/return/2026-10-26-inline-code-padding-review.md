# Markdown 行内代码：真实排版内边距 —— 评审记录

日期：2026-10-26
计划：[docs/plan/2026-10-26-inline-code-padding-plan.md](../plan/2026-10-26-inline-code-padding-plan.md)
基线：d1f105d（上轮圆角/accent 已推送；本轮在其上增量）

## 用户反馈（ground truth）

行内 `code`「字和边贴在一起」：背景矩形紧贴字形，无左右/上下留白。参考图为
灰色圆角 pill 内文字左右 ~5px、上下 ~2-3px 留白。本轮实现**真实排版内边距**，
不是把 `RectangleCommand` 外扩——padding 进入 line advance、wrap、lineheight、
baseline、caret、link hit、raster bounds。

## 最终实现

### Core（`TextStyle`/`StyledText`/`RichTextLayoutBuilder`/`RichTextContracts`）

- `TextStyle` + `InlineCodePaddingHorizontal`/`InlineCodePaddingVertical`
  （`double?`，null=0）——**任意行内盒**语义，非仅 code。两字段进
  `StyledTextFontSignature`（同 topology 仅 padding 值不同 → 重排版）。
- `RichTextLayoutBuilder.Build`：`insidePad`/`cumPadAt` 盒宽表（无 padding → null，
  零 alloc）。**左/右 pad 都是真实 advance**——`padShift[boundary]` 按行累计并入
  `xAt` 与 glyph points；canonical segment `X` = 盒原点起算的字形 X（左 pad 在其
  左侧），`Width` 已含右 pad，`InsetLeft/Right` 只记盒宽分解——pill = `[X − IL, X + W)`。
- 跨行：每个 line fragment 各自一对 pad；行共享 boundary 上的片段 pad 逐行拆开
  （行首只计本行片段左 pad、行尾只计上行片段右 pad）——相邻行的 pad 不计入本行盒宽。
- `ComputeLineMetrics`：行高 `+= 2×vPad`，`baseline += vPad` —— 字形在行盒内
  居中（上/下留白相等），普通字共享新 baseline。
- `RichTextLayoutResult.GetRangeRects`：按段 `InsetLeft`/`InsetRight` 扩段缘 —
  链接/选择命中与 Emit pill 同几何。
- `RichTextSegment` + `InsetLeft`/`InsetRight`（默认 0；旧构造器调用不变）。

### Emit（`Views/RichText.cs`）

`RectangleCommand` = `[seg.X − InsetLeft, seg.X + Width)`（`Width` 已含右 pad，
`line.Bounds` 已含 vPad）。合并键 + inset 等值——同 line、同 color、同 radius、
同 inset 才合并；两相邻不同 inset code 各独立 pill。

### Stub（`Testing/TestDoubles.cs`）

`StubTextService.BuildRich` 同几何契约（盒宽表、片段级 advance、段 inset、行高 vPad、
wrap 候选行宽含 pad）——test double 与 Skia builder 共享语义，测试不区分实现。

### Markdown

`MarkdownStyleOptions` + `InlineCodePaddingHorizontal`（默认 **5**）/
`InlineCodePaddingVertical`（默认 **2**）；`InlineAdapter.ResolvedStyle` +
`Overlay`/`ToTextStyle` 透传。`InlineCodeCornerRadius = 4` 不变（已适配）。

## 评审往返（诚实记录）

- **首轮方案被拦**：最初设计左 pad 只画不进 advance（`InsetLeft` 纯视觉、
  `xAt` 不动）、wrap 沿用无 pad 宽度——主审以「邻居空隙不是真实盒模型」
  拦下；改为左右 pad 皆真实 advance 的统一 line box 后重验。
- **Stub 绿不作真实证明**：中途几轮失败均由 StubTextService 与 Skia builder
  各自修正到同盒语义后消除——最终结论以**真实 Skia**（`RichTextServiceTests`
  新增 3 条回归）为准，Stub 只作行为对照。
- **run3 主审实图拦下两个缺陷**：
  1. `HitLink` 命中矩形仍是 `(seg.X, seg.Width)`——左 pad 条带（盒左缘 ..
     字形起点）不响应点击；修为 Emit pill 盒几何 `[X − InsetLeft, X + Width)`
     （Width 已含右 pad，不再 +InsetRight），hover/down/up 同源。
  2. 合并键误用 inset 等值：同一逻辑 `TextStyleRange` 盒被 face/script fallback
     切成三段接缝 pill（实图 `中Subagent文` 三个圆角块）；修为**逻辑盒身份**——
     同 range 内 face 子段合并一个覆盖整盒的背景，仅取首段 `InsetLeft` 与末段
     `Width`；相邻独立 range / 跨行 / 无 padding 段保持旧合并兼容。

## 不变量（主审要求逐条验证）

- **vPad 上下对称**：`baseline = (height − fontHeight)/2 + ascent`，`height =
  fontHeight + 2vPad` → 字形在行盒内居中，非全堆 bottom。
- **hit 含 inset**：`GetRangeRects` 段起于片段边界时左缘再减 `InsetLeft`——
  命中/选择矩形与 Emit pill 同盒几何；邻居段 `InsetLeft=0` 不被 pill 覆盖命中。
- **wrap 含 pad**：`MeasuredWidth` 按盒宽预算（行首续片段左 pad + 行内
  range Start/End pad 事件 + 行尾片段右 pad）——每行 fragment 盒宽含完整
  左右 pad 且不越 MaxWidth。
- **每行一对**：片段级 pad 按行拆算；跨行 canonical segment 各自读本行
  start/end 的自身 pad——非每 glyph/face-run 重复。
- **CodeBackground=null**：padding 盒仍占 advance/行高（CSS：`background` 与
  `padding` 正交），只背景矩形 skip。

## 实测（Release 封口 + Debug）

- **Release**：`dotnet build CSharp.UI.slnx -c Release` 0 警告 0 错误（exit 0）；
  `CSharp.UI.Tests` **711/0**、`Text.Tests` **112/0**、`Markdown.Tests` **50/0**、
  `Integration.Tests` **56/0**——四套件实际运行全绿（evidence `logs/run5-test-*.txt`）。
- Debug 先行计数同为 711/112/50/56（先于 `[1,11)` fixture 修正；最终 run5 计数
  以上面 Release 为准）。
- `CSharp.UI.Tests` 含 `行内代码_几何Padding_进LineAdvance与Height`、
  `跨行每行独立Inset`、`cache按GeometryKey`、`CodeBackgroundNull_几何Padding保留`——
  跨行 pill 断言收紧为**不越 frame 右缘**，盒宽含完整左右 pad。
- `Text.Tests` +3 真实 Skia 回归：纯 `'Subagent'` pad0-vs-H5 盒宽差 +10；
  `'中Subagent文'` 混排同 +10；MaxWidth < token 且 > 单 glyph+2pad 时每行
  fragment 盒含完整 L/R 且不越 MaxWidth。
- `Integration.Tests` +3 真实 Skia 集成回归 `InlineCodePillTests`：单逻辑盒
  CJK/Latin/CJK 混排一个无缝 pill、相邻独立 code 盒不误合并、HitLink
  左/右 pad 条带激活。

## 最终验收（fast run4，真实 Skia + GL offscreen）

- **172 PASS / 0 FAIL，exit 0**：line167 左 pad 命中点 FAIL→PASS；右 pad /
  second link / 普通 gap 正确；`'中Subagent文'` 3 个 canonical segment 同
  `TextStyleRange` 背景合并为单个 Rect（page seam=0）；盒宽差 +10
  （mixed pad5）；wrap 各 fragment IL/IR=5 且不越 MaxWidth；圆角 cache
  identity 相同、raster 无重算。
- 主审实图验收：light/dark @ps1.5 默认 H5/V2（`'行内代码'`/`'Subagent'` 参考
  效果）接受；run4-E3-seam 图单完整 pill 接受。
- 平台：Windows x64 offscreen GL（非 OS 物理鼠标）；OS 剪贴板未触碰
  （copy 走 stub 边界）；macOS/Linux 未实跑。

## 边界与限制

- macOS/Linux 未实跑；验证平台 Windows x64（沿用既有边界）。
- `InlineCodePaddingHorizontal/Vertical` 为几何字段——改值必触发重排版
  （非 paint-only）。
