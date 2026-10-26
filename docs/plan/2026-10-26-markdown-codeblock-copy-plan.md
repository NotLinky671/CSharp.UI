# Markdown 代码块：圆角背景 + 可选复制按钮

日期：2026-10-26
基线：bbd6c53（默认排版间距已推送）。本轮只做代码块（fenced + indented 共用 `AdaptCodeBlock`），
不改行内 code、不加语法高亮/语言标签/复制反馈动画。

## 源码依据

- `BlockAdapter.AdaptCodeBlock`（fenced/indented 共用）：`RichText(等宽).Padding(CodePadding)`
  + 可选 `.Background(CodeBackground)`，外包 `ScrollView(Horizontal)`。背景矩形**直角**、
  内容在代码横向滚动时随内容一起滚走（背景是内容属性不是容器）。
- 剪贴板：`IClipboardService`（`src/CSharp.UI/Services/PlatformServices.cs:96`，
  `GetText`/`SetText`）；注入 `UiRuntimeServices.Clipboard`；OpenTK 宿主默认
  `GlfwClipboardService`（真系统剪贴板）；控件内取法：`node.Runtime.Services.Clipboard`
  （TextField/TextEditor 既有 pattern）。测试桩 `StubClipboardService`（SetCount）。
- `Button`：public `OnClick: Action?` 拿不到 runtime；本轮加 internal
  `OnClickContext: Action<InputContext>?`（与 OnClick 同一激活点派发）——
  Core 唯一增量槽位，复制在事件回调内即取 `context.Runtime.Services.Clipboard`。

## 方案

### `MarkdownStyleOptions` 新增字段（沿用既有命名）

| 字段 | 默认 | 语义 |
|---|---|---|
| `CodeCornerRadius` | `8` | 代码块背景圆角（逻辑像素） |
| `ShowCodeCopyButton` | `true` | 代码块右上工具行"Copy"按钮；`false` 完全不含按钮行 |
| `CodeCopyLabel` | `"Copy"` | 按钮标签文本（沿用 `UnorderedMarker` 式可配置文本槽） |

### `MarkdownCodeBlockView`（internal View，新文件）

```
VStack [ header(HStack: Spacer + Button(copy))?, ScrollView(H)(code RichText) ]
```

- **header 行**（`ShowCodeCopyButton` 时）：`HStack(Spacer(), Button)` —— `Spacer()` 弹性吸收
  左侧空间，按钮被推到最右；header 是文档坐标系内节点，**不随代码横滚**，宽=代码块宽，
  320 窄宽与长代码横滚下按钮保持可见、可点。
- **圆角背景**：`Emit` 先 `RectangleCommand(node.Frame, CodeBackground, CodeCornerRadius)`
  再 `EmitChild` 两个子项——背景包整个块（含 header 行），代码文字横滚时圆角底不动。
  `CodeBackground=null` 遵现有语义无底色矩形（按钮独立照常显示）。
- **复制**：普通 `Button` + internal `Button.OnClickContext`（`Action<InputContext>`，与
  `OnClick` 同一激活点派发，Core 只加这一个 internal 槽位）——激活时
  `context.Runtime.Services.Clipboard?.SetText(text)`；事件回调内即取 runtime，
  无跨节点/跨 runtime 绑定问题。复制文本 = Build 期捕获的
  `code.Lines.ToString()` 原始串（含行尾换行；不含 fence info string）；
  `Clipboard=null` 按框架既有契约 no-op。

### 明确不做

- 行内 code 不动；不复制语言标识；无成功 toast/动画/定时器；不建通用"带按钮容器"抽象。
- 不改 Core clipboard/scroll；不引 Windows API——复制走既有 `IClipboardService`。

## 验证

- 永久测试（消费者行为，非结构 pin）：
  - 真 `UiRuntime` + `StubClipboardService`：`DispatchPointer` 点"Copy" →
    `GetText()` 精确等于手写期望串（fence/语言标识不进复制，缩进与空行保留，
    Markdig `Lines.ToString()` 末行无尾部换行）、`SetCount` 精确；
  - `ShowCodeCopyButton=false` → 同位置点击不写剪贴板；
  - 组件换新 `MarkdownDocument` 重建 → 同位置点击复制**新**文本（描述快照随更新）。
- `MarkdownDemo` sample 不加新配置（默认即生效），真实窗口 smoke 由独立 fast 验收。
- 指南 `markdown.md` 样式表 + 代码块一节补按钮说明。
