# MD-04：图片/链接策略落地与不支持内容降级核对

创建日期：2026-10-25

状态:**已完成并验收**（2026-10-26；image/link policy+降级路径真实落地）

阶段：C

设计依据：[计划](../plan/2026-10-25-rich-text-and-markdown-plan.md) §决策十一/十二。

[返回任务索引](README.md)

前置依赖：MD-02（与 MD-03 可并行，文件不重叠）。

## 文件范围

- `src/CSharp.UI.Markdown/MarkdownPolicies.cs`（新）：`MarkdownImagePolicy`、`MarkdownLinkPolicy`、`MarkdownLinkPayload`。
- `src/CSharp.UI.Markdown/BlockAdapter.cs`/`InlineAdapter.cs`：块级图片真实映射（替换 alt 占位）、`AllowActivation` 过滤复核。
- `tests/CSharp.UI.Markdown.Tests/`：策略单测。

## 实现契约

### 图片

```csharp
public sealed record MarkdownImagePolicy
{
    public Func<string, string?>? Resolve { get; init; }   // url → IImageService source；null/返回null → alt fallback
    public bool AllowRemote { get; init; }                 // 默认 false：http(s) src → alt fallback，不调 Resolve
}
```

- **块级图片**（段落内唯一 inline image）→ `Image` 视图：
  - `AllowRemote=false` 且 url 为 http/https absolute → alt fallback（`Resolve` 不被调用，计数断言）；
  - `Resolve(url)` → source → `IImageService.TryLoad` false → alt `RichText`（段落默认样式）；
  - `AllowRemote=true` 时也只把 url 交给 `Resolve`（caller 映射本地 source）；框架不加载 URL、不内置网络；
  - 尺寸：`NaturalSize` ≤ available → 自然尺寸；否则按宽等比缩放（available∞→自然）；
- **行内图片**（段落混排）→ `[alt]` literal 区间（普通样式）——行内嵌图属范围 C。
- resolver 输出 source 即 `IImageService` 缓存键；policy 变化自然失效；不进 parse 键。

### 链接

```csharp
public sealed record MarkdownLinkPolicy
{
    public Func<string, bool>? AllowActivation { get; init; }
    // 默认：仅 http/https absolute + 无 scheme 相对路径可激活；
    // javascript:/data:/vbscript:/file: 等 → false
}
public sealed record MarkdownLinkPayload(string Url, string? Title);
```

- `AllowActivation(url)` false → 不产 `TextInteractionRange`（样式照常：underline+链接色，点击不激活）；true → 产区间（`Id`/`Payload` 见 MD-02）。
- 相对 URL 原样进 payload 不解析；`MarkdownBlockView.OnLinkActivate`/`OnLinkHover` 透传每个 `RichText`。
- 链接样式槽位（underline、链接色、hover `TextStyle`）在 `MarkdownStyleOptions`，MD-02 定义、本任务对齐验收。

### 不支持内容收口（决策十二逐项核对）

- HtmlBlock/HtmlInline 两策略验收（MD-02 实现，本任务补全测试）。
- `MarkdownPipelineOptions` 每个启用项造源验证：无节点被静默丢弃（pipe table→MD-03、strike→区间、tasklist→`[x]` literal、autolink→interaction）。
- 未启用扩展的源文本（math/footnote/mermaid 等）→ CommonMark 普通段落，不伪渲染。
- 未知节点降级：literal 子树解码文本优先，不可得 → `[NodeTypeName]` 标记段 + `UnsupportedNodeStyle`；不 `node.ToString()`。

## 边界

- 不改 `IImageService`/`Image`/`RichText`。
- alt fallback 用段落默认样式，不做 alt 样式化。
- 不承诺远程图能力。

## 验收

- 本地图：resolve→Image；宽图等比缩；`TryLoad` false→alt。远程默认→alt 且 resolver 零调用；`AllowRemote`+resolver→按 source 加载。
- `javascript:alert(1)`→不激活（callback 零调用、无 interaction 区间）；`https://`、相对 `docs/x.md`→激活 payload 原样。
- 行内图片→`[alt]` literal 区间；块级段落混排图片（同段有文字）→ 按行内处理。
- policy record 值相等；组件参数透传正确；全量降级路径测试无静默吞节点。
