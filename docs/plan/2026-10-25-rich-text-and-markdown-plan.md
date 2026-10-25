# 只读富文本基础与 Markdown 接入实施计划

计划日期：2026-10-25

状态：**实施中**（RT-01 已完成；其余任务未开始，待授权）。

关联：前置任务组 FS-01~FS-07（Floating 滚动条）已完成并归档至 [docs/archive/2026-10-25-floating-scrollbar-tasks/](../archive/2026-10-25-floating-scrollbar-tasks/README.md)。

## 目标与范围

最终用户可见目标：框架获得一个**通用只读富文本基础**（同一段落内多样式、跨样式 shaping/断行、装饰、可交互链接），并在其上接入 **Markdig**，把 Markdown 文档渲染为框架原生视图树，作为独立可选模块交付。

### 范围三档

| 档位 | 内容 | 本轮承诺 |
|---|---|---|
| **A. 富文本基础（主线）** | `StyledText` 模型、`IRichTextService` 契约、`RichText` 视图、装饰与链接 hit | 完整实现与验收 |
| **B. Markdown 展示层（主线）** | `CSharp.UI.Markdown` 模块、Markdig AST → 视图树适配、块布局（段落/标题/列表/引用/分隔线/围栏代码/本地图片）、**表格** | 完整实现与验收；A 全部 gate 通过后才开工 |
| **C. 明确延后（非本轮）** | 代码高亮引擎、远程图片/网络加载、LaTeX/Mermaid、HTML 解释渲染、行内嵌入图片、富文本编辑/WYSIWYG、文本选择/复制、通用公开 Grid 控件、滚动 API、链接键盘/focus 激活 | 只在"不支持策略"写明行为；不实现、不 stub、不假渲染 |

**不实现（补充）**：`Flow`/`HStack` 拼 Text 的假段落方案；把 Markdig 当富文本编辑/增量模型；`IMarkdownRenderer`→HTML 中转；在 `Measure`/`Emit` 中创建 View children（文档→视图转换只发生在 `Component.Build` 或 `Responsive` builder 回调，布局期只测量/排列已声明的 children）。

## 已核事实（源码位点索引）

| 事实 | 位置 |
|---|---|
| `TextLayoutRequest` 是单 string + 单 `FontDescription` + `Editing` bool；record 值相等作排版缓存键 | `src/CSharp.UI/Text/TextContracts.cs:4-39` |
| `TextLayoutResult` 存 text/font/size/lines/caretStops/caretXs；`HitTestIndex`/`GetRangeBounds`/`GetCaretX` 已有 | `TextContracts.cs:75-675` |
| `TextLayoutBuilder` 用全局 ascent/descent/lineHeight 与单 `xAt[]`；字体回退经 `FontRunSplitter` 按字素切 run 立即 shape | `src/CSharp.UI.Text/TextLayoutBuilder.cs:26-176`、`TextShaping.cs:173-228` |
| **`SkiaFontResolver` 的 `FontInstance` 由 resolver 有界缓存（cap 64）持有，淘汰即 dispose SKFont/SKShaper** —— layout 裸持 FontInstance 引用，栅格化前 evict 即 UAF | `SkiaFontResolver.cs:7-9,68` |
| `SkiaTextService` 是 `ITextService` 唯一产品实现；`StubTextService`（`Testing/TestDoubles.cs:95`）是测试替身 | `SkiaTextService.cs:25-358` |
| `PixelBuffer` 已支持 `Alpha8`/`Rgba8888Premultiplied`/`Bgra8888Premultiplied` | `DrawCommands.cs:4-10,73` |
| `UiRuntimeServices`（**不是** `UiServices`）`required ITextService TextService`；`UiApplicationOptions.TextService` null → 宿主自建 `SkiaTextService` 并随宿主释放 | `UiRuntime.cs:6-24`、`UiApplication.cs:113-116` |
| `ITextureService.GetOrCreate(object key, Func<PixelBuffer>)` 帧保护 LRU；`RectangleCommand`/`TextureCommand`/`PushClipCommand` 齐备 | `DrawCommands.cs:130-233` |
| `IImageService.TryLoad(source, out resource)`；`ImageResource.NaturalSize` | `PlatformServices.cs:63-79` |
| `Responsive`：`Func<ResponsiveInfo, View>` builder，`ResponsiveInfo { AvailableWidth, IsWidthBounded }`；`Measure` 在 `context.IsEffectiveProposal`（`CurrentProposal==Effective`）时 `RequestResponsiveContent(node, info)` —— **已存在**"父最终有效宽→重建 child 结构"的公开机制，无需扩展框架 | `Views/Responsive.cs:83-144`、`Contexts.cs:84,286`、`UiRuntime.cs:335` |
| `ScrollView` Horizontal：`Measure` 给 child `MaxWidth=∞`，`Arrange` 用 bounded `contentRect` | `ScrollView.cs:69-89,96-151` |
| `FontDescription`/`FontFamily` 含 weight/italic/fallbacks/size，record 值相等 | `FontDescription.cs:40-160` |
| `EnvironmentValues` 提供 `Font`/`ResolvedForeground` 继承 | `Primitives.cs:101,148` |

### Markdig 调研事实（已核）

- Markdig **1.5.0**：net10 可引用，无传递依赖，BSD-2-Clause；源码 `https://github.com/xoofx/markdig/tree/1.5.0`。
- `Markdown.Parse` → AST 可手工遍历生成原生 UI；不需要 HTML 中转或 `IMarkdownRenderer`。
- 外部 smoke 已验证中文标题/正文/粗体/链接/图片/列表/围栏代码/表格 AST 解析；`UsePreciseSourceLocation` 未实测——不声称精确 source span 可用；AST `Span` 是 UTF-16 源 offset，**不是**展示文本坐标。
- `DisableHtml` 是把 HTML 标记当 literal（不是 sanitizer）；`NormalizeRenderer` 不作 round-trip 证明。

## 冻结决策

### 决策一：展示文本模型——`StyledText`（单根展示字符串 + 样式区间 + 交互区间）

**展示文本** = 解码后的 UTF-16 字符串（Markdown 实体/转义已由 adapter 解码，soft break 已转为空格或 `\n`）；`StyledText` 不持 Markdown 源 offset。

```csharp
public sealed class StyledText
{
    public string Text { get; }                              // 展示文本
    public IReadOnlyList<TextStyleRange> Ranges { get; }     // 样式区间快照
    public IReadOnlyList<TextInteractionRange> Interactions { get; }  // 交互区间快照
    public bool HasRichContent { get; }                      // 任一区间非空
    public static StyledText Create(string text);            // plain fast path
}

public readonly record struct TextStyleRange(int Start, int End, TextStyle Style);

public readonly record struct TextInteractionRange(int Start, int End, int Id, object Payload);
//   Id：同一逻辑链接的稳定身份（跨样式片段同 Id；同 URL 两个不同链接不同 Id）。

public sealed record TextStyle
{
    public FontFamily? Family { get; init; }
    public double? Size { get; init; }          // 绝对逻辑像素
    public FontWeight? Weight { get; init; }
    public bool? Italic { get; init; }
    public Color? Foreground { get; init; }
    public Color? Background { get; init; }
    public bool? Underline { get; init; }
    public bool? Strikethrough { get; init; }
    // 全值相等；null = 继承段落默认。不含 Payload。

public sealed record TextPaintStyle   // paint-only（LinkHoverStyle 等）：无字体字段
{
    public Color? Foreground { get; init; }
    public Color? Background { get; init; }
    public bool? Underline { get; init; }
    public bool? Strikethrough { get; init; }
}
```
```

规则（构造时验证，违反抛 `ArgumentException`，**不静默 snap/截断**）：

- 两表各自：`Start` 升序、半开 `[Start,End)`、内部两两不重叠、非空、`0 ≤ Start`、`End ≤ Text.Length`。
- `Ranges` 与 `Interactions` **之间允许重叠**（样式与交互正交）。
- 所有端点必须是 `StringInfo` 字素簇边界（切代理对/组合序列/ZWJ → 抛）；adapter 负责在产出区间前归一化（决策八的逐字素规则）。
- 输入集合复制为内部快照并排序验证；不持调用方集合引用。
- 结构相等（逐字符+逐区间）+ **构造时预计算**：缓存全量哈希 + **分段拓扑**（见决策二）；实例不可变，可作缓存键。
- `TextInteractionRange` 的 `Id`/`Payload` 参与值相等；`Payload` 应为值相等 record（Markdown 层职责）。
- **不存在**第二套公开 run 列表模型。

### 决策二：分段拓扑 / 几何 / 绘制 / 交互四元分离（最终一致路径）

**分段端点集合（segment boundary topology）**：构造 `StyledText` 时预计算 = `{0, Text.Length}` ∪ 全部 `Ranges` 端点 ∪ 全部 `Interactions` 端点（去重排序，全部已是字素边界）。这是排版分段的**唯一模型来源**。

- **shape/几何分段只由 ①分段端点 ②resolved font identity 变化（family/weight/italic/size）③字体回退 face 变化 ④script/direction 属性 决定**——不依赖颜色/payload 的**值**。
- 颜色/payload **值**变化 → 端点集合不变 → geometry 不变 → layout 复用（缓存命中）。
- 区间**边界**变化（新增/删除/移动端点，含改可点击范围）→ 端点集合变化 → 合法地重排版。
- paint 合并规则固定：**相邻 segment 若 resolved paint 字段（fg/bg/underline/strike）完全相同，在 paint 阶段合并**；这只影响矩形合并数量，不影响 shape 分段与字宽。同一边界集合下不同颜色 → 同 geometry、不同 paint。
- 不允许"按颜色相等动态决定 shape 是否分段"——分段先于颜色解析。

**四元键**：

| 键 | 内容 | 变化后果 |
|---|---|---|
| **GeometryKey** | 展示 `Text` + 分段端点拓扑 + 每 segment resolved font 度量字段（Family/Size/Weight/Italic）+ `ParagraphFont` + MaxWidth/Wrapping/Alignment/LineHeight/PixelScale/LetterSpacing | 变 → 重排版 |
| **PaintKey** | 每 segment resolved paint（fg/bg/underline/strike 标志）+ `ParagraphForeground` + PixelScale | 变 → 只重栅格化，layout 复用 |
| **InteractionKey** | `Interactions`（Start/End/Id/Payload 值） | 变 → 只换 hit 表（若端点变，GeometryKey 也必然变，重排版是合法后果） |

- `RichTextLayoutRequest` = GeometryKey 字段集——`StyledText` 贡献 `(Text, 分段拓扑, 每段几何样式)`；颜色/payload 对请求不可见。
- `IRichTextService.Rasterize(RichTextLayoutResult layout, RichTextPaint paint)`：`RichTextPaint` = `IReadOnlyList<RichTextRunPaint>`（与 layout 的 canonical geometry segment **一一对应**：resolved fg/bg/underline/strike）+ `Color ParagraphForeground`。
- **paint 解算路径（防 StyleIndex 错位）**：layout 只存 **canonical geometry segment 的展示区间**（`[Start,End)`+ResolvedFont+几何），**不存** `StyledText`/`StyleIndex`/payload 引用。视图在状态变更时（`OnUpdate`/环境/interaction/hover）按**当前** `StyledText.Ranges` 与 layout segment 的**文本区间交集**做归并解算——**segment 不跨任何模型端点**（端点集合 ⊇ 分段必需边界），但 segment 还会因换行/回退 face/script 在模型端点内**再细分**；映射取区间交集后按 segment 覆盖的唯一 style range / interaction range 解算，天然唯一。缓存的旧 layout 与新 `StyledText` 的 segment 区间一致才复用（同 GeometryKey 前提下区间必然一致）；**颜色/payload 永不通过 layout 上的旧引用回查**。
- 缓存键身份用 **core 稳定身份**，不用 caller handle 引用：`RichLayoutCore` 提供后端无关的 `LayoutIdentity`（Core 可比较的 opaque 身份值，同 core 全 handle 共享）；栅格缓存键 = `(LayoutIdentity, paint)`；GPU 纹理键 `RichTextTextureKey = (LayoutIdentity, paint, PixelScale)`——同 core 不同 handle 共享 raster/texture 缓存，caller handle 只是所有权 wrapper 不入键。run 色与 paragraph 色都在 paint 键内。
- hover/disabled：hover 命中 interaction Id → 该 Id 覆盖的 segment 用 `LinkHoverStyle` 覆盖解算 paint → 新 PaintKey；opacity/disabled 在命令侧乘一次。`LinkHoverStyle` 类型为 `TextPaintStyle`（新小 record，只含 `Foreground?`/`Background?`/`Underline?`/`Strikethrough?`——**无字体字段**，hover 不得改 font 不重排版；不复用 `TextStyle` 防止字体字段进入 paint-only 路径）。
- `IRichTextService.Measure(request)` 语义：返回 `Layout(request).Size` 的等价实现——内部若 acquire handle 必须 finally dispose caller 份（缓存 owner 份保留），不泄漏 handle 给调用方。
- **验收必须断言**：显式样式或环境前景换色后 `Layout` 计数不变、`Rasterize` +1；改 URL payload（端点不变）→ 两者均不变；改可点击范围（端点变）→ 重排版合法。

### 决策三：`IRichTextService`、backend payload 与 lease 所有权（单一路径，无粘并）

```csharp
public interface IRichTextService
{
    RichTextLayoutResult Layout(RichTextLayoutRequest request);            // 返回调用方持有的 handle
    Size Measure(RichTextLayoutRequest request);
    PixelBuffer Rasterize(RichTextLayoutResult layout, RichTextPaint paint); // RGBA premultiplied
    int CachedLayoutCount { get; }
    int CachedRasterCount { get; }
    int LayoutCacheCapacity { get; set; }
    void ClearCaches();
}
```

- **禁止**：默认抛 `NotSupportedException`、纯文本降级假实现、Core 强转 `SkiaTextService`。
- **所有权方案（唯一，无"服务自动知道无人持有"魔法）**：
  - 服务内部持有共享的 `RichLayoutCore`（geometry+backend payload：glyphID/positions/advances 按 shape 段 + 每 resolved face 一个 `FontInstanceLease`）；`RichLayoutCore` 内部引用计数。
  - `Layout(request)` 命中缓存 → `core.AddRef()`、返回新的 `RichTextLayoutResult`（**每 caller 一个独立 handle**，薄包装持 core 引用 + 公开几何 API）；未命中 → 排版、缓存持 1 份、caller handle 1 份。每次 `Layout` 返回一个新 handle 对象（**每次 layout 获取一次 handle 分配是可接受的所有权成本；Emit 不额外分配富文本集合**，绘制命令按既有框架路径产生）。
  - `RichTextLayoutResult` 实现 `IDisposable`（幂等）：`Dispose` → `core.Release()`；计数归零 → core 释放 face leases。缓存淘汰/`ClearCaches` → 缓存那份 Release——**调用方仍持有的 handle 完全合法可用**（lease 未归零，face 不死）。已 Dispose 的 handle 再调用任何 API → `ObjectDisposedException`。
  - `ViewNode.ViewCache` 持有 handle：`RichText.Measure` 放入时即获所有权；`OnUpdate`（换 layout）/`OnUnmount`/`OnUpdate` plain↔rich 切换 → `Dispose` 旧 handle。多 View 各持各 handle，互不干扰。
  - `StubTextService` core 无原生资源，AddRef/Release 是计数 no-op（语义一致便于测试）。
  - 可见性：`RichTextLayoutResult` 公开（API/替身构造器需要），`IRichTextLayoutBackend`/`FontInstanceLease`/`RichLayoutCore` 全 internal——`CSharp.UI` ↔ `CSharp.UI.Text` 双向 `InternalsVisibleTo` 已有（`CSharp.UI` 的 IVT 已含 `CSharp.UI.Text`；`CSharp.UI.Text` 内类型访问 Core internal 无需额外 IVT，反向若需要按实现加）。
- `RichTextLayoutResult` 公开几何面：`Size`/`Lines`/`CaretStops`/`CaretXs`/`HitTestIndex`/`GetRangeBounds(start,end)`/canonical `Segments`（`{Start,End,ResolvedFontId,X,Width,Baseline}` 每行）；测试替身可无字体构造（backend 为 no-op）。
- `ITextService` 原样保留；`RichText` 无区间时走 plain 路径，`ViewCache` 持 `TextLayoutResult`——不产生 rich handle。
- `UiRuntimeServices` 新增 `public IRichTextService? RichTextService { get; init; }`；`ClearCaches` 级联。
- `UiApplicationOptions.RichTextService`；null 且 `TextService is IRichTextService` → 复用同实例；外部单接口 `ITextService` → null，`RichText` 富文本路径 `InvalidOperationException`（指明注入点）。
- **stress 验收**：>64 face/样式压力下 raster 无 UAF；`ClearCaches` 后 ViewCache 中 handle 仍合法；同 core 多 view 各自 Dispose 不误释放；Dispose 后 raster 明确 `ObjectDisposedException`；同 layout 换 paint 重 raster。

### 决策四：排版规则（断行/行高/baseline）

- 段落切分/断行机会/`TrimEnd` 语义与 `TextLayoutBuilder` 同域（同组换行码点、拉丁空白/连字符、CJK 逐字素、超词硬断、行尾空白不推进）。
- 断行机会只看字符属性不看区间边界（英文单词跨 bold 不拆行）；advance 按 shape 段累加。
- 行几何：行高 = `max(LineHeight>0 ? LineHeight : 0, max(ascent)+max(descent))`——**`LineHeight` 下限即行内 max(ascent)+max(descent)**：显式值小于字高时按字高取（钳制），glyph 不越行盒、RGBA 纹理按 `layout.Size` 即完整覆盖，无需额外 ink bounds 契约；baseline = leading/2 + max(ascent)；run 按 baseline 对齐。此钳制语义已冻结（glyph 不被纹理截断，行距过小的请求被合法收敛）。
- canonical segment = 不跨任何模型端点的实际几何片段（以模型端点起始，可在其内因换行/回退 face/script 再细分）；paint 同色合并只影响装饰矩形批次，不改变几何。

### 决策五：`RichText` 视图与 Emit 无额外富文本分配

```csharp
public sealed class RichText : View
{
    public RichText(StyledText content);
    public StyledText Content { get; init; }
    public FontDescription? Font { get; init; }
    public TextAlignment Alignment { get; init; }
    public TextWrapping Wrapping { get; init; }
    public double LineHeight { get; init; }
    public double LetterSpacing { get; init; }
    public Action<object>? OnLinkActivate { get; init; }
    public Action<object?>? OnLinkHover { get; init; }
    public TextPaintStyle? LinkHoverStyle { get; init; }
}
```

- `Measure`：`HasRichContent` → 构造 `RichTextLayoutRequest`（`StyledText` 预算签名，Measure 不做全量样式 hash）：**当前 `ViewCache` 已持 handle 且新请求 GeometryKey 与缓存 handle 的相同 → 复用 handle（不重复 `Layout`/AddRef）**；几何键不同 → `RichTextService.Layout` 取新 handle 并在覆盖 `ViewCache` 前 `Dispose` 旧 handle（每次 Measure 都先做这一步——resize/DPI 触发 Measure 不走 `OnUpdate`）。`ViewCache` 存 `(handle, resolvedPaint)`。否则 `TextService.Layout`（plain，`TextLayoutResult`，零 rich 分配）。
- **Emit 无额外富文本分配**：`ViewCache` 持 `(handle, resolvedPaint)`——paint 只在 Measure（新排版）、`OnUpdate`（新 Content/环境语义变）、RT-04 `PaintDirty`（hover 边）重解算一次；Emit 用现成 paint 取纹理+按预计算 segment→命令映射加命令，**不额外构建/复制 runs、paint、glyph 集合**（绘制命令按既有框架 `DrawCommand` 路径产生，不为零分配私改渲染管线）。
- **paint 解算**（脏时）：按当前 `Ranges` 与 layout canonical segment 文本区间交集归并（决策二映射规则）→ resolved fg/bg/underline/strike + `ParagraphForeground`（环境）；hover Id 命中时覆盖 `LinkHoverStyle`（`TextPaintStyle`，paint-only）字段。
- Emit 命令顺序：背景矩形（相邻 segment 同背景合并、TrimEnd 内）→ 文本纹理（`RichTextTextureKey(LayoutIdentity,paint,PixelScale)`，Tint=White，opacity=环境）→ underline → strikethrough。
- `LayoutPropertiesEquivalent`：比较 `Content` 的 **GeometryKey**（分段拓扑+每段几何样式）+ Font/Alignment/Wrapping/LineHeight/LetterSpacing——**不**比颜色/payload（paint/interaction 变化走 `OnUpdate`+`PaintDirty`/hit 刷新，不触发 Measure）。`Wrapping` 默认 `TextWrapping.Wrap`（富文本以多行展示为主场景；单行场景 caller 显式 init `NoWrap`；Markdown 全部 Wrap。冻结默认 = `Wrap`）。
- 纹理原点吸附复用 `TextPainter.ResolveOrigin` 同规则。
- plain fast path `Emit` 与 `Text.Emit` 等价（Alpha8+tint）。

### 决策六：链接 hit——行内矩形命中 + Id 身份

- 命中几何 = `TextInteractionRange` 的**行内 segment 矩形并集**（`GetRangeRects(start,end)` 逐行矩形）；pointer local 落在任一矩形内 → 命中该 `Id`。
- **不命中**：`TrimEnd` 空白、行间空隙、跨行区间的行内非覆盖区；**不用** `HitTestIndex` caret 吸附做链接判定。
- 状态机（node 级）：
  - `OnPointerDown`：命中 Id → `pressedId=Id`、`cancelled=false`、pointer capture、return true；未命中 → return false。
  - `OnPointerMove`（capture 中）：`命中Id != pressedId` → `cancelled=true`（**离区即取消，移回不恢复**）。
  - `OnPointerUp`：`!cancelled && 命中Id==pressedId` → `OnLinkActivate?.Invoke(payload)`；清态。
  - `OnPointerCancel`/`OnUpdate`/`OnUnmount`/disabled → 清 pressed+hover（hover 发 null 收敛）。
  - `OnPointerEnter`/`Move`（非 pressed）：hover Id 变化 → `OnLinkHover?.Invoke(payload ?? null)`；`LinkHoverStyle != null` 时置 `PaintDirty`+`InvalidateRender`。
- `Id` 是链接身份：同 URL 两处不同 Id（Down A/Up B 不激活）；同链接跨样式同 Id。
- 无 focus/键盘/IME；`OnLinkActivate` null 时按下不 capture。

### 决策七：Markdown 模块与管线

- `src/CSharp.UI.Markdown/`：`PackageId CSharp.UI.Markdown`，依赖 `CSharp.UI` + `Markdig 1.5.0`（`Directory.Packages.props` 中央管理）；**不**依赖 `CSharp.UI.Text`/`CSharp.UI.OpenTK`；Core 零 Markdig 引用，`InternalsVisibleTo` 增加 `CSharp.UI.Markdown`+`CSharp.UI.Markdown.Tests`。
- **管线选项（具体枚举，无"启用但无 renderer"项）**：

```csharp
public sealed record MarkdownPipelineOptions
{
    public bool PipeTables { get; init; } = true;
    public bool EmphasisExtras { get; init; } = true;    // ~strike~ 等
    public bool TaskLists { get; init; }                 // → [ ]/[x] literal 前缀
    public bool AutoLinks { get; init; } = true;
    // 不启用 UseAdvancedExtensions()/UsePreciseSourceLocation()/DisableHtml()。
    public HtmlPolicy Html { get; init; } = HtmlPolicy.Literal;
}
public enum HtmlPolicy { Literal, Strip }
```

- HTML：pipeline 默认解析 HTML 节点（不 `DisableHtml`）；adapter 收 `HtmlBlock`/`HtmlInline` 按 policy：`Literal`→展示标记原文；`Strip`→不产出。
- `Markdown.Parse(source, options)` → 不可变 `MarkdownDocument`（持 AST）；缓存 `LruCache<(string, MarkdownPipelineOptions), MarkdownDocument>`（options 值相等进键，pipeline 变必重 Parse；resolver/policy 不进键）。
- `MarkdownBlockView : Component`：`Build()` 内 AST→View 树；`Measure`/`Emit` 零 AST 接触。
- AST 不跨线程 mutate、不跨实例共享；BSD-2-Clause notice 归 MD-05。

### 决策八：AST→StyledText 归一化（逐字素单遍，不双边外扩）

- inline 遍历两遍：第一遍产展示文本（literal 拼接、实体已解码、soft break→空格、hard break→`\n`）+ 每展示字素位置的**样式栈快照**；第二遍对每个**完整字素簇**取其起始展示 offset 处的样式栈（**逐字素单遍归一**——每字素唯一栈，天然不重叠；栈内字段解析规则：内层覆盖外层同字段，同字段取最深设置；不同字段叠加）。产出的 `TextStyleRange` 端点全部在字素边界且互不重叠。
- 病态输入（栈在某字素内部不一致理论上不可能——快照按 offset 记录，字素即最小单位）：若 Markdig AST 边界把单一字素切到两个不同 inline（如 `e`+`́` 分属两节点且样式不同），按"**字素取内层（更深栈）样式**"确定唯一样式 + adapter diagnostic（internal 计数）。不产出重叠、不抛到 UI。
- `LinkInline`（`IsImage=false`）：interaction 区间 = 该链接覆盖的展示字素区间，同链接同 `Id`（adapter 递增 id 分配器），payload=`MarkdownLinkPayload(url,title)`；链接内 emphasis 样式正交叠加。
- `AutolinkInline` 同链接区间；`CodeInline` → code font+background；`EmphasisInline` → Italic/Bold 叠加。
- `LinkInline(IsImage=true)` 行内 → `[alt]` literal 区间（行内嵌图范围 C）；块级唯一图片 → MD-04。
- `HtmlInline` → `HtmlPolicy`。

### 决策九：块级布局映射

| AST 块 | 映射 |
|---|---|
| Paragraph | `RichText(Wrap)` |
| Heading 1-6 | `RichText`，`MarkdownStyleOptions` 字号档位 + Bold |
| List（有序/无序/嵌套） | item = `HStack(marker:RichText("•"/"1.")+间距, VStack(item 块))`——hanging indent（次行不回 marker 列下）；嵌套 list 作 content 子块 |
| Quote | 左 `Rectangle` 边条 + `Padding` 内 `VStack` 递归 |
| ThematicBreak | `Rectangle` 高 1 |
| FencedCode/CodeBlock | `ScrollView(Horizontal)` + `RichText`（code font+background+padding、NoWrap）；语言标记不渲染 |
| 块级图片 | `Image`（决策十一） |
| HtmlBlock | Literal→`RichText` 原文；Strip→不产出 |
| Table | `Responsive`+`MarkdownTable`（决策十） |
| TaskList 项 | `[ ]`/`[x]` literal 进 marker |
| 未知块 | literal 子树解码文本（不可得→`[NodeTypeName]` 标记）+ `Style.UnsupportedNodeStyle`；不 `node.ToString()`、不依赖 source Span |

- `MarkdownStyleOptions`：标题字号档位、code FontFamily、列表缩进/marker 间距、引用条色/宽、cell padding、链接色/hover 样式、unsupported 样式；颜色槽位 null→主题环境。

### 决策十：表格——`Responsive` + internal `MarkdownTable`（复用现有机制，零框架扩展）

- 结构（**唯一方案**，不扩 `RequiresEffectiveProposal`/ScrollView）：

```csharp
// MarkdownBlockView 对 TableBlock 恒产出（不暴露 WrapTables 开关）：
new Responsive(info => new ScrollView(
    new MarkdownTable(rows, availableWidth: info.IsWidthBounded ? info.AvailableWidth : double.PositiveInfinity),
    ScrollAxis.Horizontal))
```

- `Responsive` 语义已核（`Responsive.cs:113-143`）：`Measure` 在 effective proposal 阶段把父最终有效宽（bounded 时 `constraints.MaxWidth`）经 `RequestResponsiveContent` 交给 builder 重建 child——child 结构在安全阶段一次声明齐全，非 `Measure`/`Emit` 期创建。父宽变化 → `info` 不同 → `InvalidateResponsiveContent` 重建表（宽度是 builder 输入的 key 语义，现有事务机制）。
- `MarkdownTable`（internal，`CSharp.UI.Markdown`）：构造参数 = 已建好的行/cell 视图（Build 期一次建全）+ `availableWidth`（immutable 输入）；自身 `Measure` 在 HScroll 的 `MaxWidth=∞` 约束下**仍用构造时的 `availableWidth`** 做列分配（不是约束值）；`Arrange` 收到超宽 bounds 不覆盖 captured available。
- **两段列宽**（`Measure`，`context` 服务测 cell）：
  - **min-content**：每列 = max over cells（cell 最长**不可断单元**宽：拉丁最长词、CJK/emoji 单字素簇、code 最长 token）+ padding；
  - **pref-content**：每列 = max over cells（NoWrap 自然宽）+ padding；
  - **分配**（`W = availableWidth`，`∞` → 列宽=pref）：`Σpref ≤ W` → pref；`Σmin ≤ W < Σpref` → 按 `(pref−min)` 权重压缩至 `Σ=W`（clamp `[min,pref]`）；`Σmin > W` → 列宽=min、`DesiredWidth=Σmin`（自然超宽由外层 HScroll 承载）。
- 列宽定后每 cell 按固定列宽 `context.Measure`（正常两遍布局）；行高=max(cell desired)；`Arrange` 按列宽向量分 bounds、行 stretch。
- header 行 `Weight=Bold`；`ColumnDefinition.Alignment`→cell `RichText.Alignment`；行分隔 `Rectangle` 1px；缺 cell 按最大列补齐。
- wheel：纵轮在横向表格可滚时横滚属现有 runtime wheel fallback 语义（guide 已载），不新增全局行为；HScroll 无 overflow 时现有冒泡。
- `MarkdownTable` 是 internal 专用布局 View，不演进为公开 Grid；**无 `WrapTablesInHorizontalScroll` 公开开关**——`MarkdownBlockView` 恒按上述 `Responsive`+`ScrollView` 结构产出；表格自然超宽=scroll，无 overflow 时 ScrollView 等价普通容器。

### 决策十一：图片与链接策略

```csharp
public sealed record MarkdownImagePolicy
{
    public Func<string, string?>? Resolve { get; init; }   // url → IImageService source；null/返回null → alt fallback
    public bool AllowRemote { get; init; }                 // 默认 false：http(s) → alt fallback，不调 Resolve
}
public sealed record MarkdownLinkPolicy
{
    public Func<string, bool>? AllowActivation { get; init; }  // 默认：http/https + 无 scheme 相对路径可激活
}
public sealed record MarkdownLinkPayload(string Url, string? Title);
```

- 块级图片（段落唯一 inline image）→ `Image`：remote 判定 → resolver → `TryLoad` false→alt `RichText`；宽>available 等比缩；`AllowRemote=true` 仍只把 url 交 `Resolve`（框架不加载 URL）。
- 行内图片 → `[alt]` literal 区间。
- `AllowActivation` false → 不产 `TextInteractionRange`（样式照常，点击不激活）；相对 URL 原样进 payload。
- resolver 输出 source 即 `IImageService` 缓存键，policy 变化自然失效。

### 决策十二：不支持内容策略（显式不吞）

| 内容 | 行为 |
|---|---|
| HtmlBlock/HtmlInline | `Literal`→原文 literal 段落；`Strip`→丢弃 |
| 未识别 block/inline | literal 子树解码文本（不可得→`[NodeTypeName]` 标记段）+ `UnsupportedNodeStyle`；不 `node.ToString()`、不依赖 source Span slice |
| 高亮/LaTeX/Mermaid/脚注等 | 扩展不启用 → CommonMark 普通段落，不伪渲染 |
| 行内图片 | `[alt]` literal 区间 |
| 远程图片 | 默认 alt fallback |
| TaskList checkbox | `[ ]`/`[x]` literal |
| 文本选择/复制/编辑/键盘激活 | 范围 C，无 UI |

### 决策十三：plain 隔离与回归义务

- `ITextService`/`Text`/`TextField`/`TextEditor`/`TextLayoutBuilder` 零改动；plain `RichText` 走 `ITextService`，`ViewCache` 持 `TextLayoutResult`，无 segment/paint/interaction 分配。
- `TextEditor` IME/undo/`Editing` 排版不复用富文本管线。
- 新公开 API：决策一/二/三/五/六/七/十一 列出项；`MarkdownTable`/adapter/lease/backend 全 internal。

## 阶段门槛

| Gate | 条件 | 解锁 |
|---|---|---|
| G1 | RT-01~RT-04 完成并主审验收 | MD-01~MD-02 |
| G2 | MD-01~MD-04 完成并验收 | MD-05 |
| G3 | MD-05 完成 | MD-06（交付） |

## 验收要点（任务拆分细化）

1. `StyledText`：重叠/越界/非字素边界/空区间全拒；值相等+预算哈希+分段拓扑可作键；两表正交重叠合法。
2. 排版：CJK 逐字断行、英文跨 bold 词不拆行、surrogate/组合/ZWJ 不切、跨 face 回退正确、行内异字号 baseline/行高/下划线度量正确、`LineHeight` 钳制语义（显式值<行内字高→按 max(ascent)+max(descent)，glyph 不被纹理截断）一致；paint 边界禁跨段连字（不同色不成 ligature、kerning 边界截断，无单 glyph 吞两样式）。
3. 三键：换色（样式或环境）→ `Layout` 计数不变、`Rasterize` +1；改 payload URL（端点不变）→ 两计数不变；改可点击范围（端点变）→ 重排版；layout 上无 StyledText/payload 引用泄漏。
4. lease：>64 face 压力下 raster 无 UAF；`ClearCaches` 后 ViewCache handle 合法；多 view 同 core 各自 Dispose 不误释放；Dispose 后 raster `ObjectDisposedException`；同 layout 换 paint 重 raster。
5. hit：行内矩形命中（空隙/行尾/跨行空处不中）、Down A/Up B 不激活、离区取消不移回恢复、capture/cancel/unmount/disabled/换内容清态、祖先 clip、disabled。
6. Markdown：中文全要素（heading/嵌套 emphasis/hanging list/quote/hr/code/本地图/链接回调/危险 scheme 不激活/表格三段分配+横滚）；不支持内容 literal 降级可见不吞；Core 无 Markdig。
7. plain 回归全绿，无 rich 分配；Emit 零 per-frame paint 重建（计数断言）。
8. 交付（MD-06）：双 config 四套件+Markdown 套全绿、3 sample×2 config fresh build+真窗 smoke、真实 GL 两 theme×多 scale 像素证据（含链接 hit 与窄宽表格横滚）、BSD NOTICE 随包、raw manifest+source hash、零 stub。

## 已知边界与风险

- paint 边界禁跨段 ligature/kerning：相邻异色字符无连字、kerning 截断微差——已冻结接受（正确性优先）。
- 每次 `Layout` 调用一次 handle 分配（所有权成本）；Emit 不额外构建/复制富文本集合。
- 表格 `Responsive` builder 在父宽变化时整表重建（cell 视图重声明；既有事务机制，长表格只承诺正确性）。
- `UsePreciseSourceLocation` 未启用：adapter 不依赖 source span。
- 行内图片 alt 降级、hover 重 paint 一份像素已冻结。
