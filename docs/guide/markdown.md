# Markdown 模块（可选）

`CSharp.UI.Markdown` 是独立可选程序集：基于 **Markdig 1.5.0** 解析 Markdown 为 AST，再经
`BlockAdapter`/`InlineAdapter` 转为框架原生视图树。Core `CSharp.UI` **不依赖** Markdig——
不引用 Markdown 模块时零开销。

| 关注点 | 行为 |
|---|---|
| 解析 | `Markdown.Parse(source, MarkdownPipelineOptions?)` → `MarkdownDocument`（含 `Source`/`Options`;AST internal) |
| 块渲染 | `MarkdownBlockView { Document, Style, ImagePolicy, LinkPolicy, OnLinkActivate, OnLinkHover }` |
| Inline 渲染 | 标题/段落/嵌套强调经 `InlineAdapter` 转 `RichText`(多样式、链接、行内码背景） |
| 图片 | `MarkdownImagePolicy`(`Resolve`/`AllowRemote`)→ 本地源经 `IImageService` 解 |
| 链接 | `MarkdownLinkPolicy`(`AllowActivation`)+ `OnLinkActivate`/`OnLinkHover` 回调；payload 类型为 `MarkdownLinkPayload(Url, Title)` |
| 表格 | internal `MarkdownTable` 由外层 `Responsive` 包 `ScrollView` 承载；按 `availableWidth` 在 `min`/`pref`/`W` 三档间切列宽，Σmin > W 时外层 `ScrollView` 横滚 |

依赖链：Markdown 解析（`Markdig`）→ `MarkdownDocument` → `MarkdownBlockView` → `RichText`/`Image`/internal `MarkdownTable` 等视图。视图装配与下层 `RichText` 相同——需要 `IRichTextService`（见下节"运行时服务注入")。

## 快速示例

```csharp
using CSharp.UI;
using CSharp.UI.Markdown;
using UiMarkdown = CSharp.UI.Markdown.Markdown;

var doc = UiMarkdown.Parse("""
    # 标题
    普通段落 **加粗** _斜体_ `code`,[链接](https://example.com)。
    | 列 A | 列 B |
    |---|---|
    | 1 | 2 |
    """);

var view = new ScrollView(
    new MarkdownBlockView
    {
        Document = doc,
        OnLinkActivate = payload =>
        {
            if (payload is MarkdownLinkPayload link)
                Console.WriteLine($"link: {link.Url}");
        },
    },
    ScrollAxis.Vertical);
```

`OnLinkActivate`/`OnLinkHover` 参数类型 `object`/`object?`，实参为 `MarkdownLinkPayload`。

完整可运行示例见 `samples/CSharp.UI.Samples.ControlGallery/MarkdownDemo.cs`（公开 `Component`，签名 `MarkdownDemo(string logoPath)`,Gallery "Markdown" 节）——标题/嵌套强调/列表/引用/hr/围栏代码/链接回调/本地图/表格横滚，链接点击页内回显 URL、不打开浏览器。

## 运行时服务注入

`RichText` 视图要求 `UiRuntimeServices.RichTextService` 非 null。

- `UiApplication.Run` 默认装配：`TextService` null → 创建 `CSharp.UI.Skia.SkiaTextService`（同时实现
  `ITextService` + `IRichTextService`),`RichTextService` 自动复用同一实例。
- 显式注入单接口 `ITextService` 而不实现 `IRichTextService` → `RichTextService` 为 null，渲染到含
  `StyledText` 样式的 `RichText` 时抛 `InvalidOperationException`(plain `StyledText.Create("x")`
  走 `ITextService` fast path，不需要 `IRichTextService`)。
- 无窗口测试可用 `CSharp.UI.Testing.StubTextService`——它同时实现 `ITextService` 与 `IRichTextService`，同一实例可同时注入两个接口槽。

## 解析选项（`MarkdownPipelineOptions`)

| 字段 | 默认 | 说明 |
|---|---|---|
| `PipeTables` | `true` | `\|` 表格（Markdig `UsePipeTables`) |
| `EmphasisExtras` | `true` | 仅启用 Markdig `UseEmphasisExtras` 的删除线子集 `~~strike~~`；`^sup^`/`++ins++`/`==mark==` 等其他符号**保留为字面量**，不启用无渲染器的扩展 |
| `TaskLists` | `false` | `- [ ]`/`- [x]` 前缀 literal 化（Markdig `UseTaskLists`；启用后 marker 为文本字素，**非交互 checkbox**，不产 toggle) |
| `AutoLinks` | `true` | 裸 URL 自动转链接（Markdig `UseAutoLinks`) |
| `Html` | `HtmlPolicy.Literal` | HTML inline/block 处理：`Literal` 原文 literal 段落、`Strip` 丢弃 |

`Markdown.Parse` 结果按 `(Source, Options)` 值缓存；同输入重 Parse 返回等值 `MarkdownDocument`,AST 不复制。

## 样式选项（`MarkdownStyleOptions`)

| 字段 | 默认 | 说明 |
|---|---|---|
| `HeadingSizes` | `[28,24,20,18,16,14]` | H1–H6 字号（逻辑像素） |
| `CodeFontFamily` | `DefaultCodeFontFamily` = `FontFamily.FromName("Cascadia Mono").WithFallbacks("Consolas","Menlo","monospace")` | 行内/围栏代码字体；null → 默认链 |
| `CodeBackground` | `Color(128,128,128,40)` | 行内/围栏代码底色；`null` → 无底色矩形（**盒几何仍保留**——padding/圆角/advance/行高不变，只不画背景；CSS 语义：background 与 padding 正交） |
| `CodePadding` / `CodeCornerRadius` | `8` / `8` | 代码块内边距 / 背景圆角（逻辑像素） |
| `InlineCodePaddingHorizontal` / `InlineCodePaddingVertical` | `5` / `2` | 行内代码盒内边距（**单侧**，逻辑像素）；进入真实排版——advance/wrap 预算/caret/行高，不是把背景矩形外扩；`0` → 紧凑（无内边距，退回纯字形宽） |
| `InlineCodeCornerRadius` | `4` | **行内**代码背景圆角（逻辑像素），与块级独立 |
| `InlineCodeForeground` | `null` | 行内代码前景色；`null` → Emit 时解 `theme.Accent`（light/dark 感知），显式色覆盖 |
| `ShowCodeCopyButton` / `CodeCopyLabel` | `true` / `"Copy"` | 右上工具行复制按钮（写系统剪贴板）/ 标签文本 |
| `UnorderedMarker` / `ListMarkerGap` | `"•"` / `8` | 无序列表 marker + 与文本间距 |
| `QuoteBarWidth` / `QuoteBarColor` / `QuotePadding` | `3` / `null` / `12` | `QuoteBarColor` null → 主题 `Border` 色 |
| `CellPadding` | `8` | 表格单元**单侧**水平 padding（列宽含 `2×CellPadding`) |
| `CellVerticalPadding` | `6` | 表格单元**单侧**垂直 padding（计入行高，不影响列宽/横滚判定） |
| `BlockSpacing` / `HeadingSpacingBefore` / `HeadingSpacingAfter` | `12` / `20` / `8` | 块级间距（margin-collapse：相邻块 gap = max(前块 after, 后块 before)；文档/引用/列表项/单元格内首块不产生前空白） |
| `ListItemSpacing` | `4` | 列表项之间的间距 |
| `ThematicBreakColor` / `ThematicBreakHeight` | `null` / `1` | `ThematicBreakColor` null → 主题 `Border` 色 |
| `LinkForeground` | `null` | null → 继承段落 `Foreground`（不默认 Accent) |
| `LinkHoverStyle` | `null` | 链接悬停 paint 覆盖；独立生效，**不要求** `OnLinkHover` 非 null |
| `UnsupportedNodeStyle` | 橙色 | 未识别节点 literal 文本的强调色 |

## 代码块（fenced/indented 共用）

围栏与缩进代码块渲染为圆角底块：`CodeCornerRadius`（默认 8）圆角背景包整个块
（含工具行），代码文本超宽时内部横向滚动，背景与按钮行不随内容横滚。
`ShowCodeCopyButton`（默认 `true`）在右上工具行产"Copy"按钮——点击把
`CodeBlock.Lines` 生成的代码文本（保留缩进与行间空行；Markdig 结构上去掉末行
尾部换行；不含 fence 语言标识）写入系统剪贴板（`UiRuntimeServices.Clipboard`，
OpenTK 宿主默认 GLFW 实现）；剪贴板服务为 null 时按钮点击 no-op（框架既有契约）。

```csharp
// 关闭复制按钮，只保留圆角底。
Style = new MarkdownStyleOptions { ShowCodeCopyButton = false, CodeCornerRadius = 8 }

// 换标签文案（本地化）。
Style = new MarkdownStyleOptions { CodeCopyLabel = "复制" }
```

## 行内代码（`` `code` ``)

行内代码渲染为带内边距的盒：背景矩形左右/上下各扩
`InlineCodePaddingHorizontal`（默认 5)/`InlineCodePaddingVertical`（默认 2)——
padding 进入真实排版盒几何（advance、wrap 预算、caret、行高），不是把
`RectangleCommand` 外扩。折行时每个行内片段（line fragment）独立成盒，各自带
完整左右 pad，盒宽不越 `MaxWidth`（单字形盒装不下时硬断，属边界情形）。

`CodeBackground = null` 时盒几何保留（padding/advance/行高不变）只不画背景——
background 与 padding 正交，与 CSS 盒语义一致。
`InlineCodePaddingHorizontal = 0` / `InlineCodePaddingVertical = 0` → 紧凑
（无内边距，退回纯字形宽/行高）。

默认外观对齐常见 Markdown 参考效果：主题感知 accent 前景
（`InlineCodeForeground = null` → `theme.Accent`)、中性半透明灰底
（`CodeBackground`)、圆角 4（`InlineCodeCornerRadius`)——非硬编码高亮色。

## 图片策略（`MarkdownImagePolicy`)

| 字段 | 默认 | 说明 |
|---|---|---|
| `Resolve` | `null` | `url → localPath` 同步映射；返回 null → 显示 alt 文本 |
| `AllowRemote` | `false` | `false` → `http(s)://` URL 直接落 alt，不调用 `Resolve` |

**远程不加载**:`AllowRemote = true` 仅允许把 URL 透传给 `Resolve`——框架不发起网络；
`Resolve` 必须返回本地路径。图片宽超可用宽度按 `IImageService` 解码的像素比等比缩；行内
`![alt]` 一律降级为 `[alt]` literal 文本（不支持行内嵌图）。

## 链接策略（`MarkdownLinkPolicy`)

| 字段 | 默认 | 说明 |
|---|---|---|
| `AllowActivation` | `null` | `null` → 走 `IsSafeUrl` 默认规则：无 scheme 相对路径放行、`http`/`https` 放行、其他 scheme 拒绝。自定义返回 false → 不产 `TextInteractionRange`（样式照常，点击不激活） |

`IsSafeUrl` 当前规则：`url.IndexOf(':') < 0` → 放行；`http`/`https`（不分大小写）→ 放行；其他 scheme 拒绝。
相对 URL 原样进入 `MarkdownLinkPayload.Url`,caller 自解释。框架从不 `Process.Start` 浏览器。

`OnLinkActivate` 参数为 `MarkdownLinkPayload(Url, Title)`（经 `object` 形参传入；cast 后取 `Url`/`Title`)。
`OnLinkHover` 同型；进入收 `MarkdownLinkPayload`，离开收 `null`。`LinkHoverStyle` 在悬停 id 命中时覆盖区间 paint
（新纹理键、不重排版）。

## 表格布局

表格块产出 `new Responsive(info => new ScrollView(internal MarkdownTable, ...))`;`MarkdownTable` 是
internal 实现——公开 API 是 `MarkdownBlockView`，不直接构造表格组件。

| 档位 | 条件 | 行为 |
|---|---|---|
| 自然 | `pref ≤ availableWidth` | `width = pref`，列不压缩 |
| 压缩 | `min < availableWidth < pref` | 各列按 `(pref-min)` 比例收缩，长行 wrap |
| 横滚 | `Σmin > availableWidth` | 列宽 = `min`，外层 `ScrollView` 横向滚动 |

列宽含 cell **水平 padding** `2×CellPadding`；行高含 **垂直 padding** `2×CellVerticalPadding`。
行间有 1px `Color.LightGray` 分隔条；cell 无独立 border。header 行 bold。跨档 resize 行高随 wrap 自适应。

## 范围外（不实现、不 stub、不假渲染）

| 内容 | 行为 |
|---|---|
| HtmlBlock/HtmlInline | `Html` 选项控制：`Literal` 原文渲染（默认）/ `Strip` 丢弃 |
| 未识别 block/inline | literal 子树解码 + `UnsupportedNodeStyle` 强调；不可解码 → `[NodeType]` 占位段 |
| 行内嵌入图片 | `[alt]` literal |
| 代码高亮 / LaTeX / Mermaid / 脚注 | Markdig 扩展未启用——按 CommonMark 普通段落渲染，不伪渲染 |
| 富文本编辑 / 跨 run 文本选择/复制 / 链接键盘激活 / 通用公开 Grid / 滚动 API | 不实现 |

## 平台边界

- 消费者验收在 **Windows x64** 完成（`docs/return/2026-10-26-md0{1..4}-*.md`)。
- macOS/Linux 本轮未在对应 OS 实际编译/运行——只承诺源码层与统一 GL 3.3/Skia 路径设计兼容，不宣称跨平台验证已过。
- `CSharp.UI.Markdown` 纯托管，不引入新原生依赖；渲染仍走 `IRichTextService` → Skia → GL 链路。

## 已知限制

- `MarkdownBlockView` 不可变描述文档——`Document` 变化整树重建；不做增量 patch。
- `Markdown.Parse` 缓存键含 `Source` 全文；超长文档注意内存。
- `OnLinkActivate` 在 `StyledText` 替换/unmount/禁用时不残留按压/悬停状态。
