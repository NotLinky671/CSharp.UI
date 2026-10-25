# MD-01：CSharp.UI.Markdown 模块、管线与解析缓存

创建日期：2026-10-25

状态：**待实施**（规划完成，待用户授权）

阶段：C（G1 之后）

设计依据：[计划](../plan/2026-10-25-rich-text-and-markdown-plan.md) §决策七/十二。

[返回任务索引](README.md)

前置依赖：RT-04（G1）。

## 文件范围

- `src/CSharp.UI.Markdown/CSharp.UI.Markdown.csproj`（新）：`PackageId CSharp.UI.Markdown`、`ProjectReference → src/CSharp.UI`、Markdig 1.5.0（`Directory.Packages.props` 中央管理）。
- `src/CSharp.UI.Markdown/MarkdownDocument.cs`：`MarkdownDocument`、`Markdown.Parse`、`MarkdownPipelineOptions`、`HtmlPolicy`。
- `src/CSharp.UI/CSharp.UI.csproj`：`InternalsVisibleTo` 增加 `CSharp.UI.Markdown`、`CSharp.UI.Markdown.Tests`。
- `CSharp.UI.slnx`：注册新项目 + `tests/CSharp.UI.Markdown.Tests/`。
- `tests/CSharp.UI.Markdown.Tests/`（新）：Parse/缓存/options 单测。

## 实现契约

### 公开面

```csharp
namespace CSharp.UI.Markdown;

public sealed class MarkdownDocument
{
    public string Source { get; }
    // 内部持 Markdig AST；结构相等 = Source + Options 值相等
}

public static class Markdown
{
    public static MarkdownDocument Parse(string source, MarkdownPipelineOptions? options = null);
}

public sealed record MarkdownPipelineOptions
{
    public bool PipeTables { get; init; } = true;        // UsePipeTables
    public bool EmphasisExtras { get; init; } = true;    // UseEmphasisExtras（~strike~ 等）
    public bool TaskLists { get; init; }                 // UseTaskLists（checkbox → literal 前缀）
    public bool AutoLinks { get; init; } = true;         // UseAutoLinks
    // 每个开关对应 adapter 已实现的节点映射；不设"启用但无 renderer"的开关。
    // 禁用 UseAdvancedExtensions()/UsePreciseSourceLocation()。
    public HtmlPolicy Html { get; init; } = HtmlPolicy.Literal;
}

public enum HtmlPolicy { Literal, Strip }
```

- pipeline 构建：`MarkdownPipelineBuilder` 逐项按 bool `Use…()`；**不** `UseAdvancedExtensions()`、**不** `DisableHtml`（保留 HTML 节点结构供 adapter 按 policy 处理）。
- 解析缓存：`LruCache<(string, MarkdownPipelineOptions), MarkdownDocument>`（options 值相等进键——pipeline 变化必重 Parse）；resolver/link/image policy **不进** parse 键（adapter 期输入）。
- `MarkdownDocument` 不可变、持 AST 只读遍历；不跨实例共享、不跨线程 mutate。
- Core 零 Markdig 引用；`CSharp.UI.Markdown` 不引用 `CSharp.UI.Text`/`CSharp.UI.OpenTK`。
- 本任务**只**交付 Parse+options+缓存+项目接线；视图类型在 MD-02 新建，不留半成品公开 API。BSD notice 全文归 MD-05（csproj 正常包元数据）。

## 边界

- 不实现视图/渲染/adapter 行为。
- 不改 Core 文本管线/服务接口。

## 验收

- `Markdown.Parse` 中文文档（标题/粗体/链接/图片/列表/围栏代码/表格/strike）产出可遍历 AST（internal/diagnostic 断言块类型计数）。
- 同 source+同 options → 缓存同实例；options 任一字段变 → 重 Parse。
- `HtmlPolicy` 两值进 options 相等键。
- slnx 构建绿；Core csproj 无 Markdig 依赖（静态断言）；Markdown 测试套纳入矩阵。
