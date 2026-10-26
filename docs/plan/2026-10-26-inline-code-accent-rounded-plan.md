# Markdown 行内代码：圆角背景 + 主题强调色

日期：2026-10-26
基线：8e0b002（blank-press clear focus 已推送）。本轮只改 RichText paint 模型 +
Markdown 样式选项 + InlineAdapter，不改块级 code/复制按钮/语法高亮/inline copy。

## 用户报告（ground truth）

行内 `code` 背景太浅且为直角矩形，希望圆角 + 字体用强调色。

## 源码依据

- `InlineAdapter` `CodeInline`（:175-183）：overlay `Family + Background`，无 corner radius/
  foreground 字段。
- `TextStyle`（StyledText.cs:33-58）：`record` 字段 `Foreground/Background/Underline/...`，
  无"语义色"或圆角概念；`Foreground` 是具体 `Color?`，parse 时无法写"主题 accent"。
- `RichText.ResolvePaint`（RichText.cs:417-441）：**Emit 时**按 `node.Environment` 解
  `foreground = hover?.Foreground ?? style?.Foreground ?? paragraphForeground`——
  这是主题感知点。
- `RichText.Emit` 背景循环（:297-330）：同一行内相邻同色 segment 合并为一次
  `RectangleCommand`，`CornerRadius` 未填（=0 直角）。
- `RichTextRunPaint`（RichTextContracts.cs:159）：`readonly record struct(Foreground,
  Background, Underline, Strikethrough)`——加字段自动进值相等/hash/paint key。
- `RectangleCommand`（DrawCommands.cs:218-222）：`CornerRadius` 参数已存在，背景
  合并处只需带上半径。

## 方案（最小扩展，无新协议）

### Core（RichText 模型）
1. `TextStyle` + `double? CornerRadius`（背景矩形圆角，逻辑像素；`null`=0 直角，
   向后兼容）+ `bool? ForegroundIsAccent`（`true`→Emit 时解 `ResolvedTheme.Accent`，
   `style.Foreground` 显式值优先——parse 不绑主题）。
2. `RichTextRunPaint` **不加** `CornerRadius`——圆角是视图层元数据，不进 glyph
   raster 键（避免 radius-only 变化无谓重栅格化）；`RichTextViewCache` 加
   `AccentForeground`（同 `ParagraphForeground` 一起记"解 paint 时的环境值"）。
3. `RichText.ResolvePaint`：`foreground` 第三分支
   `ForegroundIsAccent ? accentForeground : paragraphForeground`（
   `accentForeground = node.Environment.ResolvedTheme.Accent`）。
4. `RichText.Measure` 缓存复用：比 `cache.ParagraphForeground` **或** `cache.AccentForeground`
   → 不同则重解 paint（`Theme.Light`→`Light{Accent=X}` foreground 不变但 accent 变，
   无此字段会 stale）。
5. `RichText.Emit` 背景合并：`CornerRadius` 从 `Content.Ranges` 按段取
   （与 `ResolvePaint` 同一段→range 走法；不在 paint key），同色+同半径才合并，
   `RectangleCommand(rect, color, radius)`——多行每行片段独立圆角。

### Markdown

6. `MarkdownStyleOptions` + `InlineCodeCornerRadius`（默认 `4`）+
   `InlineCodeForeground`（`Color?` 默认 `null` → Emit 时 `ResolvedTheme.Accent`；显式色覆盖）。
7. `InlineAdapter` `CodeInline`：`CornerRadius = style.InlineCodeCornerRadius`，
   `ForegroundIsAccent = style.InlineCodeForeground is null`（无显式色时让 Core 解
   accent），否则 `Foreground = style.InlineCodeForeground`。

## 不做

- 不动块级 code 样式/复制按钮/`CodeCornerRadius`；无 inline copy/语法高亮/动画。
- 不改 `TextPaintStyle`（hover 覆盖无圆角——hover 背景仍按 run 半径画，可接受）。
- 不建语义色枚举/theme token 系统；`ForegroundIsAccent` 是唯一新语义槽。
- 不改 `StyledText`/分段拓扑/字体签名（paint-only，不重 shape/texture）。

## 验证（消费者可观察，非结构 pin）

`tests/CSharp.UI.Markdown.Tests/AdapterTests.cs` + `tests/CSharp.UI.Tests/RichTextViewTests.cs`：

1. `InlineCode_DefaultAccent`：行内 code 段 `RichTextRunPaint.Foreground` ==
   `theme.Accent`（light/dark 切 theme 断言）。
2. `InlineCode_RoundedRectsPerLine`：跨行 code 每行片段一个 `RectangleCommand`、
   `CornerRadius` 非零（不断言默认数值）。
3. `InlineCode_ExplicitForeground_OverridesAccent`：`InlineCodeForeground` 显式色时
   覆盖 accent。
4. `NonCode_BackgroundStaysSquare`：普通 `TextStyle.Background` 区间矩形
   `CornerRadius` 仍 0（不回归现有背景）。
5. `InlineCode_CustomRadius`：`InlineCodeCornerRadius` 显式值进 `RectangleCommand`。

## 状态

已交付并受审（GL run5 87/0 exit 0、Release 0w0e、四套件全绿、fast 独立验收）。
run4 曾发现 plain-bg 控制项采 page 假绿，单独修复后 run5 复测通过。
