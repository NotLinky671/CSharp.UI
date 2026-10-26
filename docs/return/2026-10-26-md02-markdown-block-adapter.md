# MD-02 交付记录：AST→View 适配——inline 样式栈与块映射

日期：2026-10-26
状态：**实施完成，待主审验收**

## 变更文件

### 新建

- **`src/CSharp.UI.Markdown/MarkdownBlockView.cs`** — `public sealed class : Component`：`Document`（required）/`ImagePolicy`/`LinkPolicy`/`Style`/`OnLinkActivate`/`OnLinkHover`；`Build()` 调 `BlockAdapter.BuildDocument`；internal `BuildForTest()` 桥接供测试直接执行 `Build`（不入运行时管线）。
- **`src/CSharp.UI.Markdown/MarkdownImagePolicy.cs`** — `public sealed record`：`Resolve`（url→IImageService source）/`AllowRemote`（默认 false）。
- **`src/CSharp.UI.Markdown/MarkdownLinkPolicy.cs`** — `public sealed record`：`AllowActivation` 回调 + `internal IsSafeUrl` 默认实现（http/https + 无 scheme 相对路径放行，`javascript:`/`data:` 等危险 scheme 拒绝）；`Default` 静态实例。
- **`src/CSharp.UI.Markdown/MarkdownLinkPayload.cs`** — `public sealed record(string Url, string? Title)`。
- **`src/CSharp.UI.Markdown/MarkdownStyleOptions.cs`** — `public sealed record`：HeadingSizes(6 档)/CodeFontFamily/CodeBackground/CodePadding/UnorderedMarker/ListMarkerGap/QuoteBarWidth/QuoteBarColor/QuotePadding/ThematicBreakColor+Height/LinkForeground/LinkHoverStyle/UnsupportedNodeStyle；颜色槽位 null → 主题继承。
- **`src/CSharp.UI.Markdown/InlineAdapter.cs`** — internal：AST inline 子树 → `StyledText`。
- **`src/CSharp.UI.Markdown/BlockAdapter.cs`** — internal：AST block 子树 → `View` 树。
- **`tests/CSharp.UI.Markdown.Tests/AdapterTests.cs`** — 29 个测试（含 MD-01 原有 8 个 parse 测试，共 29）。

### 修改

- 无（不改 Core/RichText/ScrollView/Responsive/任何框架文件）。

## 设计要点（决策八/九落地）

### InlineAdapter——栈语义显式，两遍单遍归一

**第一遍（递归 walk）**：携带 `ResolvedStyle`（record，字段 null = 继承）向下递归，内层 `Overlay` 覆盖外层同字段——**深度语义在 walk 时即时 resolve**，不靠 mark 排序猜栈深。每个产文本 leaf 发一条 `StyleMark(Start,End,Style,Depth)`：严格升序、互不重叠（leaf 间天然连续）。`linkId`/`linkPayload` 随行传递，链接内 leaf 自动产 `InteractionMark`。

**第二遍（逐字素单遍归一）**：`StringInfo.ParseCombiningCharacters` 产字素边界表；游标随字素推进扫描 mark 列表（O(字素+mark)，非 O(g*m)）。每字素取覆盖它的 mark 的 style 输出 `TextStyleRange`；相邻同 style 自动合并。**不做**双侧外扩——`StyledText` 构造时已验证所有端点在字素边界。

**病态路径**：单字素被切到两个不同样式 mark（AST 把 `e`+`́` 分属两 inline）→ 取 Depth 更大者作该字素唯一样式 + `SplitGraphemeDiagnosticCount++`（internal 计数，不吞不抛）。

### BlockAdapter——块映射全项

| AST | 落地 |
|---|---|
| HeadingBlock | `RichText(Wrap)` + `FontDescription(FontFamily.Default, HeadingSizes[level-1], Bold)` |
| ParagraphBlock | `RichText(Wrap)` |
| ListBlock | `VStack(HStack(marker RichText + VStack(item children)))` — hanging indent（次行不回 marker 列） |
| QuoteBlock | `HStack(Rectangle bar + VStack(children).Padding)` |
| ThematicBreak | `Rectangle(height=1)`（被 `.Height()` 包成 `FrameView`） |
| FencedCode/CodeBlock | `ScrollView(Horizontal)` + `RichText(NoWrap)` + code font/background/padding |
| HtmlBlock | Literal→`RichText` 原文；Strip→不产出 |
| Table | **阶段降级** `[Table]` literal + `UnsupportedNodeStyle`（MD-03 落地后此分支移除，非永久 stub） |
| 未知块 | literal 子树解码（不可得→`[NodeTypeName]`）+ `UnsupportedNodeStyle` |

### 链接/图片/交互

- `LinkInline(!IsImage)`：url 经 `LinkPolicy.IsActivationAllowed`；放行 → `++linkIdCounter` 分配同 Id（同 URL 两处不同 Id），`Payload=MarkdownLinkPayload(url,title)`；子树所有 leaf 继承该 Id → interaction 区间跨样式同 Id。
- `AutolinkInline`：同 link 样式（foreground + underline）+ interaction。
- 链接内 emphasis：样式照常叠加（两表正交）。
- `LinkInline(IsImage=true)` 行内：`[alt]` literal（alt 来自子树 literal 收集）。
- `HtmlInline`：policy 由 `Adapt` 参数传入（不是先拼接再 replace）；Literal→tag 原文；Strip→跳过 tag 但保留中间 literal。

## 测试

**29/29 绿**（`cd tests/CSharp.UI.Markdown.Tests && dotnet run`）：

- `Paragraph_产出RichTextWrap`、`Heading_产字号Bold`、`BoldItalic_样式区间存在`
- `Link_产Interaction区间_同Id`、`UnsafeUrl_不产Interaction`、`UnsafeUrl_CustomPolicy_放行则产Interaction`
- `List_产HStack含marker`、`CodeBlock_产ScrollView`、`Quote_产HStack含Rectangle`、`ThematicBreak_产Rectangle`
- `InlineImage_降级为方括号alt`、`TableBlock_未知块降级_不吞`（阶段占位）
- `NestedEmphasis_叠加样式`（`**bo*ld***` → Bold+Italic 区间存在）
- `LinkNestedEmphasis_同Id跨样式`（链接内 bold 与链接同 Id）
- `HtmlBlock_Literal展示`/`HtmlBlock_Strip丢弃`、`HtmlInline_Literal与Strip`
- `ChineseMixed_样式区间合法`（中文 + bold 区间正确切片）
- `StyledText区间_非空不重叠在字素边界`、`CodeInline_产code字体背景区间`、`Autolink_产interaction_同link样式`

## 边界与已知事项

- `Measure`/`Emit` 零 AST 接触：`MarkdownBlockView.Build()` → `BlockAdapter.BuildDocument` → 纯 `View` 树；RichText 下游不引用 Markdig。
- TableBlock 阶段降级非永久 stub：MD-03 完成真实 `MarkdownTable`+`Responsive`+`ScrollView` 后 `AdaptTableFallback` 分支移除（task06 文件明确"MD-03 落地后移除"）。
- `MarkdownBlockView.BuildForTest()` 是 internal 桥接（IVT 到 Tests），不占公开 API 面。
- `UnsupportedNodeStyle` 默认提供橙红前景（`new Color(255,128,0)`）使降级块可见；调用方可覆盖。
- 链接 `LinkForeground` 默认 null → 主题 `Accent`（在 `RichText` 渲染层经 environment 解析，不在 adapter 硬编码色值）；`Underline=true` 恒叠加（已固化的链接视觉语义）。
- 跨字素病态分裂诊断计数仅 internal（`InlineAdapter.SplitGraphemeDiagnosticCount`）；测试通过 `ResetDiagnostics` 不显式重置——新 `Adapt` 调用自动归零起算。
- 剩余 MD-03（真实 MarkdownTable+列宽两段分配）、MD-04（块级唯一图 `Image`+resolver/remote 判定/alt fallback）、MD-05（sample+NOTICE）、MD-06（交付 smoke）待放行。

## 验证

- `dotnet build src/CSharp.UI.Markdown` 0w0e。
- `dotnet build CSharp.UI.slnx` 0w0e。
- `tests/CSharp.UI.Markdown.Tests` 29/29 绿（含 MD-01 的 8 个 parse 测试）。
- `tests/CSharp.UI.Tests` 698/698 绿（核心套件回归）。
- Core 源/csproj 零 Markdig 引用（grep 验证）。

---

## 主审返修（2026-10-26 第二轮）

### 静态确定项

1. **空围栏代码 `TextStyleRange(0,0)` 崩溃** — `AdaptCodeBlock` 在 `text.Length==0` 时产 `ranges=[]`（不构造零长 range），`ScrollView`+`Padding`+`Background` 盒仍渲染。`UnknownLiteral` 同理（`literal ?? $"[{block.GetType().Name}]"` 保证 text 非空，零长仅在所有子树都无 literal 的极端场景——`TryGetBlockLiteral` 已兜底 `[NodeTypeName]`）。
2. **TaskLists checkbox 状态被吞** — `ExtractTaskListPrefix` 从 `ListItemBlock` 首个 paragraph 的首个 inline 取 `Markdig.Extensions.TaskLists.TaskList.Checked`，合成 `"[ ]"/"[x]"` 前缀进 marker；`InlineAdapter` 对 `TaskList` 节点 `break`（不产文本，由 BlockAdapter 在 marker 层合成）。测试 `TaskList_合成marker含复选状态` 断言 `marker0` 含 `[ ]`、`marker1` 含 `[x]`。
3. **code 字体非等宽** — `MarkdownStyleOptions.DefaultCodeFontFamily = FontFamily.FromName("Cascadia Mono").WithFallbacks("Consolas","Menlo","monospace")`（跨平台回退链）；`CodeInline`/`AdaptCodeBlock` 的 `Family = style.CodeFontFamily ?? DefaultCodeFontFamily`，不再落 `FontFamily.Default`（UI 字体非等宽）。
4. **颜色槽位 null 时硬编码黑底不可读** — `ThematicBreakColor`/`QuoteBarColor`/`CodeBackground` 的"null→主题"承诺修正为"null→中性灰"：`ThematicBreakColor` `new Color(128,128,128,90)`、`QuoteBarColor` `new Color(128,128,128,110)`、`CodeBackground` `new Color(128,128,128,40)`（半透明白底浅灰、黑底深灰——两 theme 都可读）。这是 Build-期常量解算（非 Emit 读 theme），颜色槽位设为具体值仍可用主题色。
5. **`NormalizeInteractionRanges` marks 非升序** — 移除了 `LinkInline` 容器级兜底 `InteractionMark`（原设计会append 在 leaf marks 之后破坏 Start 升序，且与 leaf mark 同 Id 重复）；现仅 leaf `EmitText` 产 mark（正常路径每 leaf 一次），升序天然成立。`NormalizeStyleRanges`/`NormalizeInteractionRanges` 共用一份 `GraphemeBoundaries` 表（避免重算）。
6. **逐字素归一 marks 扫描** — 游标推进 markIndex，同 offset 多 mark 按 `Depth` 显式取最深（修复"先子后父按 mark 顺序覆盖会外层盖内层"）；病态 mark（不覆盖整字素）触发 `SplitGraphemeDiagnosticCount++` + 取最深栈。`ToTextStyle` 仅在 style 变化时创建新 record 进 result（相邻同 resolved style 合并逻辑保持，但 transition 边界处仍分配——`TextStyle` 是 record struct 语义等价判断依赖值相等，复用需缓存上一 style 的 `TextStyle`——已按字素合并语义保留，因为分配只在 range 输出时）。

### 需实测布局风险（不在本轮回合复现，由 fast 独立 GL smoke 验证）

- `HStack(marker RichText + content VStack)` 未给 content `FillWidth` → 长多行列表项 content 是否被压到 marker 列宽之下待 runtime 验证（已补 `.FillWidth()`，但真实断行行为由 smoke 截图断言）。
- `QuoteBarWidth` 已 `Width()` 但 bar 高度是否 stretch 满 quote 块高度（`HStack` 默认 align 决定）待 smoke 验证。
- `AdaptTableFallback` 阶段降级将在 MD-03 移除。

### 测试增量

- `TaskList_合成marker含复选状态` — `[ ]`/`[x]` literal 进 marker。
- `EmptyCodeBlock_不抛且有可见盒` — ` ``` ``` ` 产 `ScrollView` 不抛、内部 `StyledText.Ranges` 空。
- `Autolink_产interaction_同link样式` — autolink 有 interaction + Underline。


---

## 第三轮：主题语义真实化（不再硬编码中性灰）

### 正确性方案（未改 Core/RichText/Component）

- **`ThemedBar`（internal View）**：`QuoteBarColor`/`ThematicBreakColor` null → `Emit` 时 `node.Environment.ResolvedTheme.Border` 解析；非 null 用显式值。`Measure` 与 `Rectangle` 同（`FixedSize` 优先，否则按约束）。不新增公开 API——adapter 内部使用，AST 不触 Emit。
- **`CodeBackground` 显式默认 `new Color(128,128,128,40)`**（中性半透明灰）：record 默认值非 null 是合法契约；caller 显式 `CodeBackground = null` → 无背景（区分"默认有背景"与"显式禁用"）。代码块用 `.Background(style.CodeBackground.Value)`，inline code 经 `TextStyle.Background` 走 paint 层（不重 shape）。
- **`LinkForeground` 文档精确改"继承段落 ResolvedForeground"**——计划只承诺 null→主题环境，未承诺 Accent；当前默认即段落前景+underline。
- **code family 默认链保留**：`Cascadia Mono → Consolas → Menlo → monospace`。

### 实测验证（Md.Tests 内嵌真实 runtime smoke）

- `ThemedBar_主题切换时Fill与尺寸正确`：Light→`Theme.Light.Border`、Dark→`Theme.Dark.Border`（真 Emit + 主题切换）；`Bounds.Height>0`。

32/32 Markdown.Tests 绿；698/698 Core.Tests；slnx 0w0e。

---

## 第四轮：对齐/alpha 双乘修复

### 静态失配

- **Quote bar 不 stretch**：`HStack` 默认 `VerticalAlignment.Center`，`ThemedBar.FixedSize(width,0)` 会被 Arrange 成 0 高 → bar 不渲染。修：`new HStack(bar, inner) { VerticalAlignment = Alignment.Stretch }`；inner 同时 `.FillWidth()` 防长 quote 不 wrap。
- **List marker 垂直居中**：`HStack` 默认 `VerticalAlignment.Center` 会把 marker 放在多行 item 中央，不是 hanging-indent 的首行 marker。修：`itemView = new HStack(...) { VerticalAlignment = Alignment.Start }`。
- **Code block alpha 双乘**：原 `TextStyle.Background=CodeBackground` + 外层 `.Background(CodeBackground)` 两层半透明叠加（文本行区域比 padding 区深）。修：fenced block 只在 wrapper `.Background(codeBg)` 一次，`TextStyle.Background` 不再设；inline code `CodeInline` 仍走 `TextStyle.Background`（无 wrapper）。

### 测试删改

- 删 `Link_InteractionRange在RichText上可见`（与 AdapterTests 同 path 重复，非新 consumer bug）。
- 改 `ThemedBar_解析主题Border色` → `ThemedBar_主题切换时Fill与尺寸正确`：Light→`Theme.Light.Border`，Dark→`Theme.Dark.Border`（真 consumer 可见主题切换）；断言 `Bounds.Height>0`。

---

## 第五轮：interaction 字素边界归一 + ToTextStyle 分配消除

### 静态失配

- **`[e](https://x)\u0301` 展示 `é` 一字素，interaction mark [0,1) 在字素内部** → `StyledText` 构造抛 `ArgumentException`（fast `md02-run8-stdout.txt` 实测复现）。修：`NormalizeInteractionRanges` 输出端点恒 `[gStart,gEnd)`；同字素多 mark 按 `Depth` 取唯一 owner（`InteractionMark` 加 `Depth`），同 depth 取首个。
- **`NormalizeStyleRanges` 每字素 `new TextStyle`** — 可避免 record 分配。修：`lastStyle` 缓存 `ResolvedTextStyle`（`ResolvedStyle` ref + 已转换 `TextStyle`），先 `ReferenceEquals`/值相等比较 resolved，相同直接扩 End 不分配；仅 transition 时 `ToTextStyle`。

### 测试增量

- `CombiningChar_跨链接字素_不抛且样式合法`：`[e](https://x)\u0301` → 不抛、`Interactions[0]`=`[0,2)`。
- `TwoLinks_切同字素_唯一归属`：`[e](a)\u0301[x](b)` → 字素归一合法、interactions 非空。

34/34 Markdown.Tests 绿；698/698 Core.Tests；slnx 0w0e。
