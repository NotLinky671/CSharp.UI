# Markdown 代码块复制按钮 + 圆角 —— 交付

日期：2026-10-26
计划：[docs/plan/2026-10-26-markdown-codeblock-copy-plan.md](../plan/2026-10-26-markdown-codeblock-copy-plan.md)
评审记录：[docs/return/2026-10-26-markdown-codeblock-copy-review.md](../return/2026-10-26-markdown-codeblock-copy-review.md)
证据：[docs/return/2026-10-26-markdown-codeblock-evidence/](../return/2026-10-26-markdown-codeblock-evidence/)

## 交付内容

块级代码（fenced/indented）默认渲染为**圆角底块**（`CodeCornerRadius` 默认 8，
背景包整个块含工具行；代码横滚时底与按钮不动）。右上工具行默认带 **Copy** 按钮，
点击把代码文本写入剪贴板。

```csharp
var view = new MarkdownBlockView
{
    Document = doc,
    // 关闭复制按钮、保留默认圆角：
    Style = new MarkdownStyleOptions { ShowCodeCopyButton = false, CodeCornerRadius = 8 },
    // 换按钮文案：CodeCopyLabel = "复制"
};
```

### `MarkdownStyleOptions` 新增

| 字段 | 默认 | 语义 |
|---|---|---|
| `CodeCornerRadius` | `8` | 代码块背景圆角（逻辑像素） |
| `ShowCodeCopyButton` | `true` | 右上工具行复制按钮；`false` 完全不含按钮行 |
| `CodeCopyLabel` | `"Copy"` | 按钮标签文本（可本地化） |

### 实现

- `MarkdownCodeBlockView`（internal）：`VStack(header? + ScrollView(H))`；
  header `HStack(Spacer, Button)` 在文档坐标系、不随代码横滚——320 窄宽/长代码
  下按钮保持可见可点。圆角底由 `Emit` 的 `RectangleCommand` 画，不随内容滚。
- 复制复用**现有 `Button`**：Core 加 internal `OnClickContext: Action<InputContext>?`
  （与 `OnClick` 同一激活点派发；`With`/`Primary` 副本携带）。回调内
  `ctx.Runtime.Services.Clipboard?.SetText(text)`——事件取 runtime，无跨节点绑定。
- 复制文本 = `CodeBlock.Lines.ToString()` 快照：保留缩进与行间空行、含 Unicode，
  不含 fence 语言标识；Markdig 语义下末行无尾部换行。`Clipboard=null` 时 no-op。

## 验证范围（实测）

- 构建：Debug + Release solution，0 警告 0 错误。
- 测试：`Markdown.Tests` **50/0**（两 cfg）；`Core.Tests` 698/0；
  `Integration.Tests` 53/0。
- 独立真实 OpenGL smoke：**40 项 fixture PASS / 0 FAIL**（exit 0）；4 个代表组合
  截图接受（light 320 ps1 / dark 1024 ps1 / dark 320 ps1.5 / light 1024 ps2）；
  圆角 0/8/20 像素差异可见；220 字符横滚时按钮仍视口内；`UseState` 开关切换
  行为正确（关→不写，开→精确 payload）；空代码块复制 `''`；inline code 无按钮；
  `CodeBackground=null` 仍可复制。

## 边界与限制

- **OS 剪贴板**：验收环境剪贴板含 CF_BITMAP/自定义格式被安全跳过——验证到
  `IClipboardService`（Stub）收到精确文本；生产走既有 `GlfwClipboardService`
  真实系统剪贴板（源码层），本轮未做系统剪贴板端到端实测。
- macOS/Linux 未实跑；验证平台 Windows x64。
- 行内代码保持原有样式，不增加块级背景或复制按钮；未新增复制成功提示、动画或语法高亮。
