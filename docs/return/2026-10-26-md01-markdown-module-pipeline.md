# MD-01 交付记录：CSharp.UI.Markdown 模块、管线与解析缓存

日期：2026-10-26
状态：**实施完成，待主审验收**

## 变更文件

### 新建

- **`src/CSharp.UI.Markdown/CSharp.UI.Markdown.csproj`** — `PackageId CSharp.UI.Markdown`、`ProjectReference → ../CSharp.UI`、`PackageReference Markdig`（版本中央管理）；`InternalsVisibleTo → CSharp.UI.Markdown.Tests`。
- **`src/CSharp.UI.Markdown/HtmlPolicy.cs`** — `public enum HtmlPolicy { Literal, Strip }`。
- **`src/CSharp.UI.Markdown/MarkdownPipelineOptions.cs`** — `public sealed record`：PipeTables/EmphasisExtras/TaskLists/AutoLinks/Html 五字段，值相等自动进缓存键。
- **`src/CSharp.UI.Markdown/MarkdownDocument.cs`** — `public sealed class`：`Source`/`Options` 公开；`Ast`（`Markdig.Syntax.MarkdownDocument`）internal 持——**公开面不暴露 Markdig 可变对象**；值相等 = `Source + Options`；每实例独持一棵 AST（"同值返回同实例"指 doc 句柄共享，非 AST 遍历器跨 doc 复用）。
- **`src/CSharp.UI.Markdown/Markdown.cs`** — `public static class Markdown`：`Parse(source, options)`；`internal static LruCache<(string, MarkdownPipelineOptions), MarkdownDocument>` 容量 64；`internal ClearParseCache()`（测试隔离用，不公开宿主重置）；`internal ParseCacheCount` 诊断。pipeline 构建逐项 `Use*()`，不 `UseAdvancedExtensions()`/`DisableHtml()`/`UsePreciseSourceLocation()`；`static readonly DefaultOptions` 复用（不每 Parse 新分配默认 options）。
- **`src/CSharp.UI/CSharp.UI.csproj`** — `InternalsVisibleTo` += `CSharp.UI.Markdown`、`CSharp.UI.Markdown.Tests`。
- **`CSharp.UI.slnx`** — `src/` 加 `CSharp.UI.Markdown`、`tests/` 加 `CSharp.UI.Markdown.Tests`。
- **`Directory.Packages.props`** — 中央 pin `Markdig 1.5.0`（BSD-2-Clause；license 全文由 fast 已核 `Copyright (c) 2016-2026, Alexandre Mutel`，MD-05 NOTICE 按此分发）。

### 测试

- **`tests/CSharp.UI.Markdown.Tests/CSharp.UI.Markdown.Tests.csproj`** — xunit v3 进程内（同其他测试项目约定）。
- **`tests/CSharp.UI.Markdown.Tests/MarkdownParseTests.cs`** — 8 facts：null 抛、中文全要素 AST 遍历（标题/段落/列表/引用/围栏代码/表格/strike 块类型断言）、同 source+options 同实例、异 options 重解析、各字段影响 AST（PipeTables/EmphasisExtras/AutoLinks/TaskLists）、HtmlPolicy 两值进键、LRU 有界（≤64）、evict 后实例仍可用。

## 边界确认

- **Core 零 Markdig**：`src/CSharp.UI/**.cs`/`*.csproj` 无 `Markdig` 引用（obj 里 transitive 记录是中央 pin 的 NuGet 元数据，非源码依赖）。
- **`CSharp.UI.Markdown` 不依赖** `CSharp.UI.Text`/`CSharp.UI.OpenTK`。
- **未提前实现** `MarkdownBlockView`/`InlineAdapter`/`BlockAdapter`/图片/链接策略——按 task 边界留给 MD-02。
- AST 访问路径仅 `internal Ast` 属性；公开 API 无 `Markdig.*` 类型暴露。

## 验证状态

- `dotnet build CSharp.UI.slnx`：**绿**（0 警告 0 错误）。
- `dotnet run` `tests/CSharp.UI.Markdown.Tests`：**8/8 绿**。
- `dotnet run` `tests/CSharp.UI.Tests`：**698/698 绿**（核心回归无破坏）。
- 无调试插桩、无 `TODO`/`NotImplemented`/stub。

## 真实管线验证（Markdig 1.5.0 行为确认）

- `PipeTables=false` → `|a|b|` 解析为 `ParagraphBlock`；`true` → `Table`。
- `EmphasisExtras=false` → `~~x~~` 无 `EmphasisInline`。
- `AutoLinks=false` → 裸 `https://x.com` 无 `LinkInline`（Markdig 1.5.0 归一化为 `LinkInline` 而非 `AutolinkInline`）。
- `TaskLists=false` → `- [ ] todo` 无 `TaskList` inline 节点。
- `HtmlPolicy` Literal/Strip 进 options 键（两值产不同 doc 实例）。
