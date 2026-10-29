# MD-02：AST→视图适配——inline 样式栈与块映射

创建日期：2026-10-25

状态:**已完成并验收**(2026-10-26 G1 放行实施;主审验收 48 checks 0 fail,unicode grapheme 归一/quote/hr 两 theme 命令+pixel/list hanging/mono/link callback 均实测)

阶段：C

设计依据：[计划](../../plan/2026-10-25-rich-text-and-markdown-plan.md) §决策八/九/十二。

[返回任务索引](README.md)

前置依赖：MD-01。

## 文件范围

- `src/CSharp.UI.Markdown/MarkdownBlockView.cs`（新）：`MarkdownBlockView : Component`、`MarkdownStyleOptions`。
- `src/CSharp.UI.Markdown/InlineAdapter.cs`（新，internal）：inline 树 → `StyledText`（样式栈压平 + interaction 区间 + 字素边界归一）。
- `src/CSharp.UI.Markdown/BlockAdapter.cs`（新，internal）：block 树 → `View` 树。
- `tests/CSharp.UI.Markdown.Tests/`：adapter 单测（断言 View 树结构与 `StyledText` 区间）。

## 实现契约

### 组件

```csharp
public sealed class MarkdownBlockView : Component
{
    public MarkdownDocument Document { get; init; }
    public MarkdownImagePolicy ImagePolicy { get; init; }
    public MarkdownLinkPolicy LinkPolicy { get; init; }
    public Action<object>? OnLinkActivate { get; init; }
    public Action<object?>? OnLinkHover { get; init; }
    protected override View Build() => BlockAdapter.BuildDocument(Document, this);
}
```

- `Build()` 内完成全部 AST→View 转换；`Measure`/`Emit` 零 AST 接触。文档值变化 → reconciler 重建 → `Build` 重跑（不做 AST diff）。

### inline 适配（样式栈压平，**禁止**手工索引拼接）

- 第一遍：产出展示文本（literal 拼接、实体已解码、soft break→空格、hard break→`\n`），同步记录"样式栈快照@展示 offset"边界事件。
- 第二遍（**逐字素单遍归一**）：对每个完整字素簇取其起始展示 offset 处的样式栈（每字素唯一栈，天然不重叠）；栈内字段解析=内层覆盖外层同字段、不同字段叠加；同 resolved style 相邻段合并。产出 `TextStyleRange` 端点全在字素边界且互不重叠——**不做**双侧外扩（外扩可造成重叠）。
- 病态输入：AST inline 边界把单一字素切到两个不同 inline（如 `e`+`́` 分属两节点且样式不同）→ 该字素取**内层（更深栈）样式**作唯一样式 + adapter diagnostic（internal 计数）；不产出重叠、不抛到 UI、不静默丢内容（正常 emoji/组合输入不走此 fallback）。
- `LinkInline`（`IsImage=false`）：`TextInteractionRange`，同链接全片段同 `Id`（adapter 递增 id 分配器；同 URL 两处不同 Id），`Payload=MarkdownLinkPayload(url,title)`；链接内 emphasis 样式照常叠加（两表正交重叠）。interaction 区间端点同样经逐字素归一。
- `AutolinkInline` → 同链接区间（url=literal 文本）。
- `CodeInline` → code `FontFamily`+`Background`；`EmphasisInline` → Italic/Bold 叠加。
- `LinkInline(IsImage=true)` 行内 → `[alt]` literal 区间（普通样式，行内嵌图范围 C）；块级唯一图片 → MD-04。
- `HtmlInline` → `HtmlPolicy`（Literal→原文文本；Strip→跳过）。

### 块映射

| AST | 输出 |
|---|---|
| HeadingBlock | `RichText(Wrap)`，`Style.HeadingSizes[level]`+Bold |
| ParagraphBlock | `RichText(Wrap)` |
| ListBlock | item = `HStack(marker:RichText("•"/"1.")+间距, VStack(item 块))`——**hanging indent**（次行不回 marker 列下）；嵌套 list 作 content 子块 |
| QuoteBlock | 左 `Rectangle` 边条 + `Padding` 内 `VStack` 递归 |
| ThematicBreak | `Rectangle` 高 1 |
| FencedCode/CodeBlock | `ScrollView(Horizontal)` + `RichText`（code font+background+padding、NoWrap）；语言标记不渲染 |
| HtmlBlock | Literal→`RichText` 原文；Strip→不产出 |
| TableBlock | `Responsive`+`ScrollView(H)`+`MarkdownTable`（MD-03 真实实现；本任务期间该节点按未知块降级路径——MD-03 落地后移除，非永久 stub） |
| TaskList 项 | `[ ]`/`[x]` literal 前缀进 marker（`TaskLists` 启用时；禁用则源文本原样） |
| 未知块 | 降级：`RichText`（literal 子树解码文本，不可得时 `[NodeTypeName]` 标记 + `Style.UnsupportedNodeStyle`）；**不** `node.ToString()` 当内容、不依赖 source Span slice |

## 边界

- 任务边界：表格真实实现 MD-03；图片 resolver/remote 判定 MD-04。本任务已接入 `LinkPolicy.AllowActivation`（只决定 interaction 区间产不产，属 inline 产出逻辑）；行内图片按 `[alt]` literal 落地（最终语义，非占位）。
- 不改 Core/RichText；组件只消费公开+internal API。

## 验收

- 中文 Markdown → View 树：标题档位、bold/italic/code 区间、嵌套 `**bo*ld***` 叠加、嵌套列表 hanging indent、引用条、hr、围栏代码等宽横滚。
- `StyledText` 区间合法；同 style 相邻合并；组合字符跨 inline 归一正确（诊断路径断言）。
- 链接区间同 Id 跨样式、不同链接不同 Id；`AllowActivation` false → 无 interaction 区间。
- 未知节点降级含 literal 内容可见不吞；HtmlBlock 两策略正确。
- `Build` 外（Measure/Emit）不触碰 AST（review+结构性断言）。
