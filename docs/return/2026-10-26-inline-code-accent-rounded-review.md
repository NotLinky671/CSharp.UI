# Markdown 行内代码：圆角背景 + 主题强调色 —— 评审记录

日期：2026-10-26
计划：[docs/plan/2026-10-26-inline-code-accent-rounded-plan.md](../plan/2026-10-26-inline-code-accent-rounded-plan.md)
基线：8e0b002（blank-press clear focus 已推送）

## 用户报告（ground truth）

行内 `code` 浅底 + 直角矩形，希望圆角 + 字体用强调色。

## 源码依据

- `InlineAdapter.CodeInline`（:175-186）：overlay `Family+Background`，无圆角/前景字段。
- `TextStyle`（StyledText.cs:33-67）：`record` 无"语义色"或圆角概念；`Foreground` 为具体
  `Color?`，parse 时无法写"主题 accent"。
- `RichText.ResolvePaint`（RichText.cs:420-450）：**Emit 时**按 `node.Environment` 解前景——
  主题感知点。
- `RichText.Emit` 背景合并（:298-344）：同色同行段合并为 `RectangleCommand`，
  `CornerRadius` 未填（=0 直角）。
- `EnvironmentValues`：无 `Accent`/`ResolvedAccent` 槽——只有 `ResolvedTheme.Accent`
  （DD56 read :107-138 确认），解析点沿用既有 `ResolvedForeground`/`ResolvedTheme` 用法，
  **不新增 environment accent API**。

## 最终实现

### Core（RichText 模型，向后兼容）

- `TextStyle` + `double? CornerRadius`（背景矩形圆角；`null`/0 = 直角）+
  `bool? ForegroundIsAccent`（`true` → Emit 时解 `ResolvedTheme.Accent`；
  `style.Foreground` 显式值优先——parse 不绑主题）。
- `RichTextRunPaint` **不含** `CornerRadius`——圆角不进 glyph raster 键
  （radius-only 变化不重栅格化）；`RichTextViewCache` + `AccentForeground`。
- `RichText.ResolvePaint`：第三分支 `ForegroundIsAccent ? accentForeground : paragraphForeground`。
- `RichText.Measure` 缓存复用：比 `cache.ParagraphForeground` 或 `cache.AccentForeground`
  → 不同则重解 paint（accent-only theme 换不重排版但重 paint——**这是被 fast 抓出的
  既有 stale-accent bug 修复**）。
- `RichText.Emit` 背景合并：半径从 `Content.Ranges` 按段取（视图层元数据），
  同色+同半径才合并，`RectangleCommand(rect, color, radius)`。

### Markdown

- `MarkdownStyleOptions` + `InlineCodeCornerRadius`（默认 `4`，与块级独立）+
  `InlineCodeForeground`（`Color?` 默认 `null` → Emit 时 accent；显式色覆盖）。
- `InlineAdapter.CodeInline` + `ResolvedStyle.CornerRadius`/`ForegroundIsAccent` 补位。

## 实测（Debug）

- `dotnet build CSharp.UI.slnx`：0 警告 0 错误。
- `CSharp.UI.Tests`：**707 / 0**（+3 新增：
  `行内代码_Accent主题切换_同geometry重解paint不重排版`——accent-only theme 换
  `SetEnvironment` 后 `Assert.Same(layout)` + `Assert.Equal(accent2, paint)`；
  `行内代码_显式前景_覆盖Accent`；
  `行内代码_跨行_每行独立圆角片段`——窄宽折行后按行排序、行间不重叠、
  X/Right 在 frame 有效宽内、code 片段不覆盖前导"aaaa "，真实几何非常量转发）。
- `Markdown.Tests` 50/0、`Integration.Tests` 53/0（回归不破）。

## 受审修正

- fast 提 `Measure` 复用仅 `ParagraphForeground` 变才 `ResolvePaint` → accent-only
  theme 换 stale：补 `AccentForeground` 比对，实测同 geometry 复用路径即时重解。
- fast 提 `RichTextRunPaint.CornerRadius` 进 paint hash → radius-only 变化无谓
  重栅格化：移出 run paint，`Emit` 从 `Content.Ranges` 取半径。
- `TextStyle.XML` `cref=EnvironmentValues.Accent` 不存在：改 `ResolvedTheme` 引用避免
  文档 warning。

## 实测（Release + 独立 GL smoke，fast 会话验收）

- `dotnet build CSharp.UI.slnx`（Release）：0 警告 0 错误。
- `Core.Tests` 707/0、`Text.Tests` 109/0、`Markdown.Tests` 50/0、
  `Integration.Tests` 53/0（Release，真实 exe/`dotnet run --no-build`）。
- 独立 GL run5：**87 PASS / 0 FAIL**，exit 0；5 代表组合已生成并像素验收，
  主审抽查亮色窄宽/暗色宽屏截图接受。
  run4 曾发现 plain-bg 控制项采 page 假绿（单独修复 probe-origin 采样后
  run5 复测通过，如实保留受审历史）；行内代码主功能 checks 全过。
- accent-only theme swap（Light→Light{Accent=Orange}）实测新 ink 旧 ink=0
  （重解 paint 不重排版，`Assert.Same` handle 复用）；
  `RichTextRunPaint` 无 `CornerRadius` 字段——radius-only 变化 `layout=0`/`raster=0`，
  只重发 `RectangleCommand`（无无谓 glyph 重栅格化）。
- 跨行 48 字符 code：每行独立片段不重叠、尾文字无底；`CodeBackground=null`
  底消失 accent 字仍显示；外层 `.Foreground` rust 色普通字 rust 而 code 用 Accent；
  bold+linked code 点击正确；块级复制按钮回归不破。

## 边界与限制

- GL 截图为离屏 render-to-texture 管线，**非物理窗口 + 非 OS 鼠标注入**；
  结论覆盖框架层输入路由与绘制结果。
- OS 剪贴板未实测；验证平台 Windows x64；macOS/Linux 未实跑（沿用既有边界）。
- `InlineCodeForeground` 显式色在 `StyledText` 里仍存具体 `Color`（显式请求即绑定）；
  `ForegroundIsAccent` 只用于默认 null 场景。
- 无 inline copy/语法高亮/动画——行内代码只改 paint，不改交互契约。
