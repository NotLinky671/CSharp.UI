# Markdown 代码块复制按钮 + 圆角 —— 评审记录

日期：2026-10-26
计划：[docs/plan/2026-10-26-markdown-codeblock-copy-plan.md](../plan/2026-10-26-markdown-codeblock-copy-plan.md)
基线：bbd6c53（默认排版间距已推送）

## 需求与方案演进

用户要求：块级代码（fenced/indented 共用 `AdaptCodeBlock`）默认圆角背景 +
可选复制按钮（可选择加或不加）。

**初版被主审拒绝**：`MarkdownCopyButton : Button`（Button 为 sealed）→ 改写成
140 行自绘/自实现交互的独立 View，镜像了 Button 的 focus/hover/pressed/键盘/
disabled 协议——建第二按钮实现、且有 KeyUp 无匹配 KeyDown 仍 Copy 的错误面，
违反复用既有 pattern 与六个月维护约束。

**最终方案（接受）**：复制走**现有 `Button`**。Core 唯一增量：internal
`Button.OnClickContext : Action<InputContext>?`——与 `OnClick` 同一激活点派发
（先 context 版后普通版），`With`/`Primary` 副本携带该字段。Markdown 侧
`new Button(label){ OnClickContext = ctx => ctx.Runtime.Services.Clipboard?.SetText(text) }`
——事件回调内即取 runtime，无跨节点/跨 runtime 绑定；Clipboard=null 按框架
既有契约 no-op。无 public API 增面、无第二交互实现。

## 结构（`MarkdownCodeBlockView`，internal）

- 单子节点 `VStack(header?, scroll)`：header = `HStack(Spacer, Button)`（文档坐标系，
  不随代码横滚，320 窄宽下按钮可见可点）；scroll 为代码横滚 `ScrollView(H)`。
- `Emit` 先画 `RectangleCommand(frame, CodeBackground, CodeCornerRadius)` 再发子项——
  圆角底包整个块；`CodeBackground=null` 遵既有无底色语义。
- `Children` 返回预存 readonly 数组（不每次 alloc）。
- `LayoutPropertiesEquivalent` 比 text/背景/圆角/开关/标签。

## `MarkdownStyleOptions` 新增

| 字段 | 默认 |
|---|---|
| `CodeCornerRadius` | `8` |
| `ShowCodeCopyButton` | `true` |
| `CodeCopyLabel` | `"Copy"` |

## 测试演进（违约项已清）

- AdapterTests：`CodeBlock_产ScrollView` / `EmptyCodeBlock`——旧 pin 容器类型、
  新改"渲染非空/不抛"仍违约定，**两个测试整体删除**。
- CodeBlockCopyTests：删"无 Clipboard 不抛"（bare not-throw）与"圆角命令存在"
  （转发参数断言，任何圆角按钮都能绿）、删自定义 label 重复路径（label 视觉由
  fast smoke 覆盖）。保留 3 个消费者测试：
  1. `复制按钮_点击_剪贴板收到精确代码原文`——fixture 为 fenced `csharp` +
     前导 2 空格 + 空行 + Unicode；期望串手写固定 `Assert.Equal`（不调用同一实现产
     期望）；实测 `Markdig Lines.ToString()` 末行无尾部换行（行间/空行换行保留）。
  2. `ShowCodeCopyButton_关闭_同位置点击不写剪贴板`——`SetCount == 0`。
  3. `文档更新_同位置复制_收到新文本`——SwapDoc 组件换 `MarkdownDocument`，
     AAA→BBB，验证捕获文本随描述更新。

## 实测

- `dotnet build CSharp.UI.slnx`：Debug + Release 均 0 警告 0 错误。
- `Markdown.Tests`：**50 / 0**（Debug + Release）；`Core.Tests` 698/0；
  `Integration.Tests` 53/0（测试 exe / `dotnet run --no-build` 直跑）。
- 独立真实 OpenGL smoke（fast，`cleanup-stray-scrollbar-archive`）：**40 项 fixture
  PASS / 0 FAIL**，原始证据 `docs/return/2026-10-26-markdown-codeblock-evidence/`
  （raw 日志 + 截图）。运行 exit 0。
  - 4 个代表组合截图已接受：light 320 ps1、dark 1024 ps1、dark 320 ps1.5、
    light 1024 ps2（非 12 全矩阵）。
  - 真实 GL 像素：圆角 0 vs 8 vs 20 差异可见。
  - 220 字符长代码不 wrap、横滚 scrollX 8→−352 时 header 按钮仍在视口内可点击。
  - `UseState` 同 doc `ShowCodeCopyButton` true→false→true：关闭后旧位置点击
    不写，开启写精确 payload（含缩进/空行/Unicode、无 fence）。
  - 空代码块复制 `''`；inline code 无按钮；`CodeBackground=null` 仍可复制。

## 边界与限制

- **OS 剪贴板**：验收环境剪贴板含 CF_BITMAP/自定义格式，枚举被安全跳过——
  未读取/破坏用户已有 payload。因此本轮验证的是"真实指针事件 →
  `IClipboardService`（Stub）收到精确文本"；生产走既有 `GlfwClipboardService`
  （源码层真实系统剪贴板），**本轮未做系统剪贴板端到端实测**——产品 backend
  为既有真实实现，不算功能 stub。
- macOS/Linux 未实跑（沿用既有平台边界）；验证平台 Windows x64。
- 行内 code 不动；无复制成功反馈/动画（明确不做）；语言标识不复制。
