# Markdown 行内代码：圆角背景 + 主题强调色 —— 交付

日期：2026-10-26
计划：[docs/plan/2026-10-26-inline-code-accent-rounded-plan.md](../plan/2026-10-26-inline-code-accent-rounded-plan.md)
评审记录：[docs/return/2026-10-26-inline-code-accent-rounded-review.md](../return/2026-10-26-inline-code-accent-rounded-review.md)
证据：[docs/return/2026-10-26-inline-code-style-evidence/](../return/2026-10-26-inline-code-style-evidence/)

## 用户效果

行内 `code` 背景从浅底直角矩形改为**圆角矩形**（默认 4 逻辑像素），代码字色
从段落前景改为**主题强调色**（light/dark 与外层 theme 感知）：

```
默认（无配置）：圆角底 + Theme.Accent 字色
Style = new MarkdownStyleOptions { InlineCodeForeground = null } // 同上
Style = new MarkdownStyleOptions { InlineCodeForeground = Color.Red } // 显式色覆盖
Style = new MarkdownStyleOptions { InlineCodeCornerRadius = 8 } // 换圆角
```

## 实现

### Core（`TextStyle`/`RichText` 最小扩展，向后兼容）

- `TextStyle` + `double? CornerRadius`（背景矩形圆角；`null`/0 = 直角）+
  `bool? ForegroundIsAccent`（`true` → Emit 时解 `ResolvedTheme.Accent`；
  显式 `Foreground` 优先——parse 不绑主题）。
- `RichTextRunPaint` **不含** `CornerRadius`——圆角是视图层元数据，不进 glyph
  raster 键；radius-only 变化不重 shape/纹理。
- `RichText.ResolvePaint`：第三分支 `ForegroundIsAccent ? accent : paragraphForeground`。
- `RichText.Measure` 缓存复用：`AccentForeground` 一并比对——accent-only theme
  swap（Light→Light{Accent=X}）同 geometry 复用 handle、即时重解 paint。
- `RichText.Emit` 背景合并：`CornerRadius` 从 `Content.Ranges` 按段取，
  同色+同半径才合并，每行一个独立 `RectangleCommand`。

### Markdown（`MarkdownStyleOptions` + `InlineAdapter`）

- `InlineCodeCornerRadius` 默认 `4`（与块级 `CodeCornerRadius` 独立）；
  `InlineCodeForeground` 默认 `null` → Emit 时 `theme.Accent`，显式色覆盖。
- 行内 `code` overlay 传 `CornerRadius` + `ForegroundIsAccent`/`Foreground`；
  块级 code 样式与复制按钮不动。

## 验证（实测）

- Debug + Release solution：0 警告 0 错误。
- `Core.Tests` 707/0、`Text.Tests` 109/0、`Markdown.Tests` 50/0、
  `Integration.Tests` 53/0。
- 独立 GL run5：**87 PASS / 0 FAIL**，exit 0；5 代表组合已生成并像素验收，
  主审抽查亮色窄宽/暗色宽屏截图接受；plain-bg square 控制项已修
  probe-origin 采样假绿问题，验证通过。
  块级复制回归均实测通过。

## 边界与限制

- GL 截图为离屏 render-to-texture，**非物理窗口 + 非 OS 鼠标注入**。
- OS 剪贴板未实测；`MarkdownCodeBlockView` 复制按钮仍走既有 `IClipboardService`（
  stub 层已验，系统剪贴板端到端本轮未测）。
- macOS/Linux 未实跑；验证平台 Windows x64。
- 行内代码不改文本内容/range、不改字体族/断行/链接命中；仅 paint 语义变化。
