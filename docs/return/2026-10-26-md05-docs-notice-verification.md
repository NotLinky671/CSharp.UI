# MD-05 文档/许可记录（验证端）

日期：2026-10-26 · 范围：仅 docs/NOTICE/csproj 元数据与 README；不动产品代码、不动 Gallery。

## 我侧文件

| 文件 | 变更 |
|---|---|
| `NOTICE`（新，repo 根） | Markdig 1.5.0 BSD-2-Clause 全文：Copyright (c) 2016-2026 Alexandre Mutel,All rights reserved + 两条再分发条件 + 免责段；附 source URL `https://github.com/lunet-io/markdig` |
| `src/CSharp.UI.Markdown/CSharp.UI.Markdown.csproj` | `None Include="..\..\NOTICE" Pack="true" PackagePath="\"` — 已 `dotnet pack -c Release` 验证 `NOTICE` 出现在 nupkg 根（不嵌套子目录）;nuspec 依赖仅 `CSharp.UI`+`Markdig 1.5.0`,Core 未吸 Markdig |
| `README.md` / `README.zh-CN.md` | 文档列表加 Markdown 指南链接；首版范围加 RichText+Markdown 一段；计划状态行更新（RT-01~04 + MD-01~04 主审通过，MD-05 实施中，MD-06 待验） |
| `docs/guide/markdown.md`（新） | 用户指南：依赖链/`MarkdownBlockView` 公共 API/服务注入/`MarkdownPipelineOptions.Html`/`MarkdownStyleOptions`/`MarkdownImagePolicy`/`MarkdownLinkPolicy`/表格三档/不支持清单/平台边界 |
| `docs/guide/api-reference.md` | 视图表新增 `RichText`；服务契约新增 `StyledText`/`TextStyleRange`/`TextInteractionRange`/`IRichTextService`/`RichTextLayoutRequest`/`RichTextLayoutResult`/`RichTextPaint`/`TextPaintStyle`;Markdown 模块新增独立小节 |
| `docs/guide/platform-notes.md` | 原生依赖表后追加 Markdig 行（含 BSD-2-Clause/NOTICE 引用）;"默认发布不包含"改"富文本**编辑**";已知限制加 Markdown 平台边界（Windows 实机验收，macOS/Linux 源码/编译层） |
| `docs/guide/testing.md` | 测试分层表新增 `tests/CSharp.UI.Markdown.Tests`;`dotnet run` 命令新增 Markdown 测试入口 |

## 验收自查

- `NOTICE` 全文为 `https://raw.githubusercontent.com/lunet-io/markdig/1.5.0/license.txt` 原文（`Copyright (c) 2016-2026, Alexandre Mutel` / `All rights reserved.` / BSD 2 条 / 免责段）,owner 标注与 source URL 完整。
- `dotnet pack src/CSharp.UI.Markdown -c Release` 实际验证 `NOTICE` 位于 nupkg 根；`CSharp.UI` Core nupkg/csproj 无 Markdig 传递。
- `markdown.md` 中所有公共符号按源码逐一校对：`Markdown.Parse`/`MarkdownDocument`/`MarkdownBlockView`/`MarkdownPipelineOptions`(5字段含 `PipeTables=true`/`EmphasisExtras=true`/`TaskLists=false`/`AutoLinks=true`/`Html=Literal`)/`MarkdownStyleOptions`/`MarkdownImagePolicy`/`MarkdownLinkPolicy`/`HtmlPolicy`/`MarkdownLinkPayload(Url,Title)`。`LinkForeground` null → 继承段落 `Foreground`;`CodeFontFamily` null → `Cascadia Mono`/`Consolas`/`Menlo`/`monospace` 链；`QuoteBarColor`/`ThematicBreakColor` null → 主题 `Border`;`LinkHoverStyle` 独立生效不要求 `OnLinkHover`;`AllowActivation` null 走 `IsSafeUrl`（相对路径与 http/https 放行，其他 scheme 拒绝），自定义返回 false 才不产 interaction。
- 范围外清单与计划 §决策十二一致：`Html.Literal`/`Strip` 是支持策略（不放"不支持");`TaskLists` 可由 options 启用——`[ ]`/`[x]` literal marker,**非交互** checkbox，不产生 toggle；未识别节点 literal+`UnsupportedNodeStyle`、行内图 `[alt]`、代码高亮/LaTeX/Mermaid/富文本编辑/选择/复制/键盘激活/通用 Grid/滚动 API 不实现。

## 边界

- 真机验证仅 Windows x64(`docs/return/2026-10-26-md03-*`/`md04-*` 记录）;macOS/Linux 本轮**未在对应 OS 编译/运行**，只承诺源码层兼容设计 + 统一 GL 3.3/Skia 路径，不宣称跨平台实测。
- `MarkdownDemo` 新 surface(good 提供，`samples/CSharp.UI.Samples.ControlGallery/MarkdownDemo.cs`,`Program` "Markdown" 节挂入）**尚未由独立 harness 实际 exercise**——MD-06 需外 runner 引用真实 sample 程序集挂 `MarkdownDemo`，模拟 pointer click + 表格 wheel + 截图，45 帧全 Gallery 首屏不证明新 surface。
- 表格三档：行间 1px `Color.LightGray` 分隔条；cell 无独立 border，列宽含 `2×CellPadding` 水平；`Σmin > availableWidth` 时横滚（W==min 不溢出不滚）。

## 文件链接核验

实际运行相对链接 checker(Python `re`+`os.path.exists`,http/https/mailto 跳过）:

```
RESULT: 0 broken of 84 links across 7 files
```

覆盖：`README.md`、`README.zh-CN.md`、`docs/guide/markdown.md`、`api-reference.md`、`platform-notes.md`、`testing.md`、本记录。仅文件路径核验，fragment anchor 未逐条验证。
