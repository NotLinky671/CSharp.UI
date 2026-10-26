# MD-03 复审返修记录

返修日期：2026-10-26
对应任务：[07-markdown-table.md](../task/07-markdown-table.md)
主审轮次：第二轮（fast 完成 MD-04 image 后）

## 主审命中并修复的缺陷

| # | 缺陷 | 修法 | 文件位置 |
|---|------|------|---------|
| 1 | `CollectMinContent` 每 token `new Text` + `new ViewNode` + `context.Measure`——违"Measure 零创建 View"契约，丢 environment/font/theme，忽略 `rt.Content` 内 bold/size/code 字体 | 改为：pref measure 先行让 `RichText`/`Text` 把 layout commit 到 `node.ViewCache`；min-content 从已提交 `RichTextLayoutResult`/`TextLayoutResult` 用 `GetCaretX(end)−GetCaretX(start)` 取不可断 token 宽——保留原 `StyledText` 全部字体/样式真实宽。零 `new View`/`new ViewNode`/`new Text`/`substring`。 | `MarkdownTable.cs` `MeasureCell` + `TextMinContent` + `MaxTokenWidth` |
| 2 | `BeginProbe(double.Epsilon)` 返回的是 context clamp 后宽（≈0），丢了容器固有 chrome（列表 marker/引用条/代码 padding） | 不再探测 min；改为**递归真实 node 树**按 pref 后几何组合：`VStack`/`ScrollView`/`Responsive` → max child min；`HStack` → Σ child min + (pref−Σ child pref) chrome；单 child wrapper → child min + (parent pref−child pref) padding；`MarkdownTable` 嵌套按列 min 求和；其它默认 → pref（本 adapter 实际不产）。 | `MarkdownTable.cs` `MinContent` + `MinWidthOf` |
| 3 | plain `RichText`（`HasRichContent=false`）的 min 退到 padding——`ViewCache` 是裸 `TextLayoutResult` 而非 `RichTextViewCache` | `TextMinContent` 现在 `node.ViewCache switch` 同时吃 `RichTextViewCache{Layout}` 和 `TextLayoutResult` 两 union；`MaxTokenWidth` 对两 layout 各一 overload。 | `MarkdownTable.cs` `MinContentOfRichText` |
| 4 | header bold 只把**已有 range**加 bold——`plain *italic* tail` 的 plain/tail 洞未覆盖 | 按原 range 端点切片 `[0, len)` 全集：原 range 切片保留原 style 字段 + `Weight=Bold`；洞补 `new TextStyle{Weight=Bold}`；空 text 直接跳过（不产 `(0,0)` 空 range）。 | `BlockAdapter.cs` header cell overlay |
| 5 | `MarkdownStyleOptions.CellPadding` 缺失——`MarkdownTable` 硬编码 8 | `MarkdownStyleOptions.CellPadding { get; init; } = 8` 已加；`AdaptTable` 显式 `style.CellPadding` 传入 `MarkdownTable` ctor。 | `MarkdownStyleOptions.cs`、`BlockAdapter.cs`、`MarkdownTable.cs` ctor |
| 6 | `UnknownLiteral("")` 产 `TextStyleRange(0,0,…)` 空 range | 空 text 早退返回 `new RichText(StyledText.Create(""))`，不进 range 构造路径。 | `BlockAdapter.cs` `UnknownLiteral` |
| 7 | `MarkdownTable._aligns` 字段存而不用（alignment 已由 adapter 在 cell `RichText.Alignment` 消费） | 删字段 + ctor 参数 + callsite。 | `MarkdownTable.cs`、`BlockAdapter.cs` |
| 8 | token 跨行时 `GetCaretX(end)−GetCaretX(start)` 会把上行尾 token 宽并进 | `TokenWidth` 分两种 layout 都按行切片：`startLine==endLine` 直接差；跨行按 `Lines[li].Start`/`Lines[li+1].Start` 分段取 max 行内宽。 | `MarkdownTable.cs` `TokenWidth` ×2 |

## 现存风险（供 fast 独立验证）

- **token tokenizer 与 line-breaker 的边界一致性**：`UnbreakableTokens` 用 `StringInfo.ParseCombiningCharacters` + 自定义宽字符表断词，而 `SkiaTextService` 的断行用 HarfBuzz/Unicode break——两算法在边界 case（如泰文合成、emoji ZWJ 序列、复合 mark 接空格）可能切出不同 token 边界。当前验收只用 ASCII/CJK/`plain *italic* tail` 覆盖，**fast 独立跑 table 三档宽测试时应含 `你好 world 混合**bold**tail` 这类边界**。
- **`MinWidthOf` 嵌套表路径**：cell 内嵌 `MarkdownTable`（NestedTableBlock）时若外层 `Measure` 已先跑过其 `Measure`，`ViewCache` 已是 `TableLayout`——直接 `ColumnWidths.Sum()`；否则递归取 cell min。**嵌套表的真实 pref/min 传播未被测试覆盖**（Markdig 很少产出嵌套 pipe table）。
- **`MinContent` default 分支退到 pref**：目前 adapter 产出的 cell 视图集合是已枚举闭集（`VStack`/`RichText`/`Text`/`Image`/`Responsive`/`ScrollView`/`HStack`/`ThemedBar`/`Rectangle`/嵌套 `MarkdownTable`），新加 cell 视图类型需显式入此 switch 否则退 pref。
- **性能**：`UnbreakableTokens` 用 `yield` + `StringInfo.ParseCombiningCharacters`（每 cell 一次 O(len) 分配），`TextMinContent` 递归 `node.Children`；cell 规模小（表列×cell 内 block）可接受，无 per-frame 分配路径。

## 验收

- `Markdown.Tests` 46/46、Core `CSharp.UI.Tests` 698/698、全 slnx build 绿。
- `TableTests` 新增：`Table_Header_mixedStyle_全段Bold` 验 `| plain *italic* tail |` 的 `[0,len)` 全覆盖 + italic 保留。
- 320 窄宽中文表真 runtime（`UiRuntime` + `StubTextService`）产非空 `frame.Commands`（cell `VStack` + 行分隔 `Rectangle` 均渲染）。

## 不涉及

- fast 的 `MarkdownImageView.cs` 未改（按分工独立验证）。
- `CSharp.UI` 通用 `LruCache` 未改——假定 UI 线程的语义保持；并发入口在 `Markdown.Parse` 侧加锁承担。

## 第二轮：公开静态缓存并发

主审指出：`Markdown.Parse` 的模块级 `LruCache` 假定 UI 线程，静态公开入口跨线程可达——
并发 `Parse` 触发 `_order.Last`/`_map.Remove` 竞态（fast 的 image 测试 1/6 次复现）。

修法（不改 `CSharp.UI` 通用 `LruCache`，最小化）：`CacheLock` 私有静态 `object`，
`Parse`/`ClearParseCache`/`ParseCacheCount` 全部 `lock(CacheLock)` 包住 `LruCache` 访问。
`DefaultOptions` 已 immutable record 语义，共享安全。

新增 `Parse_并发同键异键_值正确`：16 线程 × 32 次，同 source（共享 key）+ 独立 source
（每线程异 key）并发解析——断言无异常、`Source`/`Options` 值正确、共享 source 命中同实例。

**fast image 测试在锁修复后 5 连跑全绿**（原 1/6 flake 消除）。

## 第三轮：TokenWidth 行定位

主审命中：`TokenWidth` 用 `LineIndexForPosition(new Point(startX, 0))`——caret X 当 Y 用，
跨行分支永死。

修法：`LineIndexOfStop(lines, stop)` 按 `Lines[i].End` 半开区间定位 UTF-16 stop 所属行
（stop==End 归下一行；行尾 caret 归上行——soft-break 共享 offset 由排版器记录在 `LineCarets`）。
token 自身不跨行（空白/换行即断点），若排版器在 token 内断行则按行分段取行内 caret 差 max。

`RichTextLayoutResult.GetCaretX(stop)` / `TextLayoutResult.GetCaretX(stop)` 已吸附 caret stop；
行内片段宽 = `|GetCaretX(segEnd) − GetCaretX(segStart)|`。无 `LineIndexForPosition` 误用，
无新 public API。

**Markdown.Tests 47/47 绿**（fast image 测试在锁修复后稳定）。

## 第四轮：Responsive 换档不重 measure + cell 内嵌块真排版 smoke

主审命中：`MarkdownTable` 未 override `LayoutPropertiesEquivalent`——`Responsive` builder
产新 table（captured `_availableWidth` 变化）时 reconciler 默认 children 等价仅重 render，
不重 measure，新 table 沿旧 `TableLayout`，320→1024 仍 320、回 320 反而 434。

修法：`LayoutPropertiesEquivalent` 显式比较 `_availableWidth`/`_cellPadding`/`_columnCount`/`_rows.Count`。

**fast fixture 观察到的真实跨档失败**（不经新建 runtime）：同 `MarkdownDocument` 320→1024→320
永久回归 `Table_同文档_跨档Resize_重排版`——断言 `DesiredWidth` 依次 320 / >320（Σpref）/ 320。

cell 内嵌块真排版（`Table_Cell内嵌块_真实排版_min含chrome`）：`SkiaTextService` +
手工构造 `cell VStack{para, list(HStack{marker,content}), quote(HStack{bar,text}), code RichText}`，
断言 `DesiredSize` 非零（真实 min 含 list marker/quote bar/code padding）。Markdig pipe table
只产 inline cell——本测试验**内部布局契约**（adapter 按真实 AST 结构产任意 cell 块序列），
非公开语法扩展。

## MD-05 交付：Gallery MarkdownDemo + 任务状态

**实现**：`samples/CSharp.UI.Samples.ControlGallery/MarkdownDemo.cs`（新，公共 `Component`）——
构造 `MarkdownDocument`（标题/嵌套强调/列表/引用/分隔线/围栏代码/链接/本地图/远端图 alt 降级/三列表格），
`MarkdownBlockView` + `ScrollView` 360px 高度；链接回调页内 `State` 回显 URL（不打开浏览器）；
`MarkdownImagePolicy.Resolve` 仅放行本地已存在文件（`File.Exists(src) ? src : null`），`AllowRemote=false`。

`Program.cs` `Section("Markdown", new MarkdownDemo(logoPath))` 插入文档尾——`logoPath` 复用
`shared/Assets/logo.png`（既有图片示例同路径）。

**Gallery smoke**：`CSHARPUI_SMOKE_FRAMES=45` 真实窗口 45 帧渲染正常退出（900×700 逻辑，
1350×1050 framebuffer，GL 3.3.0 / Intel RaptorLake-S）——Markdown section 在文档底随外层
`ScrollView` 展示。

**任务状态**：docs/task/README.md MD-03/MD-04/MD-05 全部 ☑；plan 状态更新为
`G1/G2/G3 达标，MD-06 交付验收待最终跑批`；task 07/08/09 文件头同步"已完成并验收"。

**清理**：`InlineAdapter` 已无 `ResolvedTextStyle` 包装/`prevResolved` 变量/`InteractionRanges.Sort`；
`ThemedBar` 在用（hr/quote bar 主题感知矩形）；probe.cs/fsx 已删；`Markdown.cs` `CacheLock` 在 MD-03 二轮落地。

## MD-05 复审：示例三处修正 + 真实跨档重排版证据

主审命中：
1. URL 虚构仓库 → `https://github.com/NotLinky671/CSharp.UI`。
2. "很长中文单元格" CJK 逐字可断不撑 min → 改不可断拉丁长标识符
   `MarkdownTableUnbreakableIdentifierForHorizontalScrollingAtNarrowWidths`。
3. `Build()` 每次 hover/click state 重建 StringBuilder+Parse → ctor 缓存 `MarkdownDocument`；
   删 `OnLinkHover`/`hoveredUrl` state（hover 触发的 state 回写重建会自抹显示，不在 RT-04 契约内）。

**真实 harness**（`UiRuntime` + `SkiaTextService`，非全 Gallery 窗口）：
`MarkdownDemo` 独立 component 挂 root，viewport 320/1024/320 三档切换：

| 视口 | `MarkdownTable` DesiredWidth | `ScrollView(Horizontal)` | 行高 |
|------|------------------------------|--------------------------|------|
| 320 | **631.215**（Σmin 超宽横滚） | 308 视口 | 169.749（token 折行） |
| 1024 | **792.871**（Σpref 自然展开） | 1012 视口 | 76.644（单行） |
| 320（回） | **631.215**（重 measure 恢复横滚） | 308 | 169.749 |

Latin 长标识符列首段 471.652 宽（行内 token 真实 advance），行内 `RichText("…")` 在窄列内自动换行——`LayoutPropertiesEquivalent` 修复生效，Responsive 换档后 captured `availableWidth` 驱动重 measure。

表格行高随列宽窄变化（56.86→38.24→55.86 等差异——列宽窄时 cell 多行折行增高），`LayoutPropertiesEquivalent` 的 `_rows.Count`/`_columnCount` 比较保留对行结构变化的防护。

## MD-05 样例复审修正（主审三条）

实施端文件 `samples/CSharp.UI.Samples.ControlGallery/MarkdownDemo.cs` 三处修正——

1. **URL 虚构** `github.com/codeium/CSharp.UI` → `https://github.com/NotLinky671/CSharp.UI`（真实远端，已
   证）。
2. **CJK 长单元格** `"很长很长…"` 逐字可断不撑 min-content → 改不可断拉丁标识符
   `MarkdownTableUnbreakableIdentifierForHorizontalScrollingAtNarrowWidths`——真实 `RichText` 测宽 471.652
   > viewport 320，`Σmin` 必然溢出触发 `ScrollView(Horizontal)` 横滚。
3. **`Build()` 重建文档** —— 每次 `OnLinkActivate` state 写 → 重建 `StringBuilder`+`Markdown.Parse`
   （LRU 命中仍经过 source/option lookup)→ 改为 ctor 一次 `Markdown.Parse`，字段 `_document` 只读。
   同频删除 `OnLinkHover`/`hoveredUrl` state——RT-04 契约未要求 hover 回显，hover 写 state 触发的
   `Component` 重建会把 `RichText` 的 hover paint 视觉"自抹";`LinkHoverStyle` paint 覆盖本身仍有效。

**真实 harness**(temp dir,已删）:`UiRuntime` + `SkiaTextService`,`MarkdownDemo` 作 root
component,viewport 320→1024→320:

| 视口 | `MarkdownTable` DesiredWidth | `ScrollView(Horizontal)` frame | 行高 | 证据 |
|------|------------------------------|--------------------------------|------|------|
| 320  | **631.215** | 308 视口 | 169.749 | Σmin > 320 → 横滚；窄列多行折行 |
| 1024 | **792.871** | 1012 视口 | 76.644 | Σpref ≤ 1012 → 自然展开，单行 |
| 320b | **631.215** | 308 视口 | 169.749 | `LayoutPropertiesEquivalent` 比较 captured width 触发重 measure |

**冻结声明**：产品代码/tests/samples/csproj 已冻结，不再改；审计范围 src/CSharp.UI.Markdown、
src/CSharp.UI/Text、samples/CSharp.UI.Samples.ControlGallery、tests/CSharp.UI.Markdown.Tests 无
TODO/NotImplementedException/AdaptTableFallback/phantomStub 残留。`MarkdownParse` 缓存测试用
`Assert.Same` 检查同 source+options——`CacheLock` 内同步读，xUnit 默认 sequential collection 无并发；
不 pin LRU 容量/淘汰实现。测试收敛、无 mock。

## MD-05 终审:tests-only 修正(冻结期内允许)

**tests/CSharp.UI.Markdown.Tests/MarkdownParseTests.cs**:

- 删 `Parse_缓存有界`——pin 私有 capacity 64 + `>0`,incidental implementation 断言,主审禁止。
- 删 `Parse_缓存evict后旧实例仍可用`——只读 `Source` 不消费 AST,不构成行为验证;留作 throwaway 脚本范围,
  永久测试不保留此形。
- 重写 `Parse_并发同键异键_值正确`:并发 N 线程 Parse 同 source+每线程独立 source,逐条捕获
  `(expected, actual)` 真比 `Assert.Equal(expected, d.Source)/Assert.Equal(expectedOpts, d.Options)`;
  `ConcurrentQueue<Exception>` 收集 thread 内异常,`await Task.WhenAll` 后 `Assert.Empty(errors)` 精确抓错;
  不把 `Assert.Equal(d.Source, d.Source)` 同语反复当正确性证明。

跑批:`CSharp.UI.Markdown.Tests` **47/47** 绿(49-2);`docs/return/README.md` 索引同步。

**给 fast**——MD-06 全矩阵跑批必须以本次测试文件为基准;不要重跑历史 49-count 快照求绿。

## MD-05 主审边界声明(验收生效后补记)

- **runtime input ≠ physical OS mouse**:harness 通过 `UiRuntime`/`ProcessPendingWork`+`DispatchPointer` 注入
  指针路径,真实 `RichText` interaction hit-testing 正确;不是物理鼠标驱动,不虚构 DPI 覆盖范围。
- **真实 window 仅 DPI 1.5**(framebuffer 1350x1050 = 900x700 logical);light 380 link shot 旧画面主审注明
  **不能当 echo 视觉 proof**——fast MD-06 必须用 offscreen `Render` 序列补正确证据。
- `Guide/API/NOTICE/pack` 主审已读接受;link checker 84 links/7 files 0 broken(fast 输出)。
- 未实测平台:macOS/Linux 本轮**未在对应 OS 编译/运行**,仅源码层兼容设计,不宣称跨平台实测。
