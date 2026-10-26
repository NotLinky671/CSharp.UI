# MD-06 返修:Gallery 测试宿主缺 `RichTextService` 注入

日期:2026-10-26

## 问题

`UiApplication` 默认装配逻辑(已 frozen):`RichTextService` null → 若 `TextService` 实现 `IRichTextService` 则复用同实例。**手工构造 `UiRuntimeServices` 的测试宿主**沿用旧契约只传 `TextService`——Gallery `MarkdownDemo` 段(2026-10-26 加入)把 `MarkdownBlockView`+`RichText` 挂进示例 surface,**66 个 OpenTK 测试**在含 `MarkdownDemo` 的视图树下抛:

```
System.InvalidOperationException : RichText 含有样式或交互区间,需要富文本排版服务;
请通过 UiRuntimeServices.RichTextService 注入(默认装配复用同实例的 SkiaTextService)。
```

fast 真实 OpenTK Debug 跑批:66/684 fail,全部同因。`Gallery` 本身行为正确——测试宿主装配缺字段是 feature cutover 必需修复。

## 文件

| 文件 | 变更 |
|------|------|
| `tests/CSharp.UI.OpenTK.Tests/GalleryIntegrationHost.cs` | `UiRuntimeServices` init 加 `RichTextService = TextService as IRichTextService`(同实例 Skia 双接口) |
| `tests/CSharp.UI.OpenTK.Tests/*.cs` 29 个 | 所有 `new UiRuntimeServices { TextService = X, ... }` init 补 `RichTextService = X as IRichTextService`(`StubTextService`/`SkiaTextService` 双接口) |
| `tests/CSharp.UI.OpenTK.Tests/UiApplicationTests.cs` | `闪烁测试` 显式分两个 `StubTextService` 实例→同实例注入(契约建议);`TrackingTextService` 仅 `ITextService` 单接口,`as` cast 得 null 保契约原状 |
| `tests/CSharp.UI.OpenTK.Tests/GalleryScrollbarDemoLayoutTests.cs` | `FindHorizontalDemo`:MarkdownDemo 表格/围栏代码新增横向 ScrollView,不能再断言"整页唯一";按 `Text="滚动条"` section 定位后筛 `Axis=Horizontal` |
| `tests/CSharp.UI.OpenTK.Tests/GalleryIdleAndBoundsTests.cs` | `连续宽度变化布局测量`:累计 `LayoutPassCount ≤ widths` 断言**删除**(单行级 `Assert.InRange(passes,0,2)` 保留——`Responsive` 换档 560/840 断点时 1 步 2 pass 合法,累计数字属 incidental implementation 重 pin) |

## 不变契约保留

- `UiRuntime.RichTextService` 显式 init;`null` 时不 fallback,`RichText` 有样式→`InvalidOperationException`(`RichTextViewTests.缺失时_有样式抛明确异常_无样式正常` 保留)。
- `TrackingTextService` 单接口测试(`UiApplicationTests.应用注入的服务不被宿主释放`)未改断言——同实例断言用 `ITextService` 路径。
- 没改 `UiApplication`/`UiRuntime`/`RichText` 任一产品代码;`MarkdownDemo`/csproj 未动。
- 没新增 wiring/mockecho test;`TextService=X` literal 沿用,cast 不伪造 backend capability(`as IRichTextService` 返回 null for 单接口 mock)。

## 验证

- 修复后:`CSharp.UI.OpenTK.Tests` **684/684** Debug 绿(0 fail/error),原 66 fail 全消除。
- `CSharp.UI.Tests` 698/698、`CSharp.UI.Markdown.Tests` 47/47 同步绿。
- 修复前 raw 保留:`docs/delivery/2026-10-26-rich-text-markdown-delivery-evidence/suite-opentk-tests-Debug.stdout.txt`(fast 跑批原文,不覆盖)。
- 修复后 raw:`suite-opentk-debug-fixed.stdout.txt`、`suite-markdown-debug.stdout.txt`、`suite-core-debug.stdout.txt`(同目录)。

## 边界

- 仅修测试宿主装配;无 src/samples/csproj/pack 输入变化。
- 释放 hook(`Assert.Throws<ObjectDisposedException>` on Layout post-Dispose)保留在 `UiApplicationTests` 原测试,未移位。
- 横向 ScrollView 筛选改 "section 限定" 而非 frame 高度/坐标 pin——对 markdown 新增内容不脆弱。
- `LayoutPassCount` 累计 `≤widths` 断言**删除**(单行级 `InRange(0,2)` 已足;widths*2 是 incidental
  implementation 重 pin)。

## 附加审计:Markdig `UseEmphasisExtras` 默认开关

**发现**:`Markdown.cs` 用 `builder.UseEmphasisExtras()` 无参 → `EmphasisExtraOptions.Default` = "allows all
extra emphasis"。Markdig 1.5.0 XML:`Default` 启用 `~`(subscript,单字符)+`~~`(strike,双字符)+`^`
(superscript)+`++`(inserted)+`==`(marked)。

`InlineAdapter.EmphasisOverlay` 只对 `~` 分支(`count>=2 → Strikethrough,否则 Empty`)与 `*`/`_` 分支
(奇数 italic、≥2 bold)。其它 delimiterChar 走 `*`/`_` fallthrough:

| 输入 | AST | 当前 renderer | 实际效果 |
|------|-----|---------------|----------|
| `~~strike~~` | `~` ×2 | strike | ✓ 正确 |
| `^sup^` | `^` ×1 | fallthrough→`count%2==1 → Italic` | ✗ superscript 错渲染 italic |
| `~sub~` | `~` ×1 | `~` 分支 `count<2 → Empty` | subscript 渲染普通(语义错) |
| `++ins++` | `+` ×2 | fallthrough→`count>=2 → Bold` | ✗ inserted 错渲染 bold |
| `==mark==` | `=` ×2 | 同上 | ✗ marked 错渲染 bold |

违反 `MarkdownPipelineOptions` 自述"每个开关都对应 adapter 已实现的节点映射"。

**修复**:`builder.UseEmphasisExtras(EmphasisExtraOptions.Strikethrough)`——仅启用 `~~`,其它 delimiter
不注册 parser(`^`/`++`/`==`/单 `~` 保持 literal 文本可见)。`MarkdownPipelineOptions.EmphasisExtras` doc
更新声明"仅 strikethrough,不启用未实现 renderer 的额外 delimiter"。

**证据**:Markdig XML `F:EmphasisExtraOptions.Default`="Allows all extra emphasis";真实 probe(临时
`DumpEmphasisDelimiters` [Fact] 跑后删)`^sup^`→`delim='^' count=1`、`++ins++`→`delim='+' count=2`、
`==mark==`→`delim='=' count=2`、`~sub~`→`delim='~' count=1`——全落 `*`/`_` fallthrough。

**回归**:`Markdown.Tests` 47/47 仍绿(`Parse_Options各字段影响AST` 中 `~~x~~`→`EmphasisInline` 不变);
**MD-02 strike regression 需 fast fresh matrix 时复核**。

## 测试边界声明

- `UiApplicationTests.应用注入的服务不被宿主释放`:`TrackingTextService` 仅 `ITextService` 未强加
  `IRichTextService`——`as` 返回 null 保契约原状,不伪造 backend capability。
- `FindHorizontalDemo` 用 section-title fixture 定位(非 wiring test);`scrollbar-vertical-demo` 仍用
  `Key`——横向 demo 无 Key 不改 sample。

## 回归补:EmphasisExtras flag 收紧的 consumer regression

新增 `AdapterTests.EmphasisExtras_未支持符号保留Literal_删除线仍生效`:

- 真实 `Markdown.Parse("^sup^ ++ins++ ==mark== ~~strike~~")` + `BuildForTest` → `RichText`。
- 断言 `styled.Text` 含 literal markers `"^sup^"/"++ins++"/"==mark=="`(未支持符号不吞,不伪渲染)。
- `strike` 段有 `Strikethrough=true`;`Assert.DoesNotContain` 任何 `Italic/Bold` range——
  防回归回退到 `UseEmphasisExtras()` Default 使 `^`/`++`/`==` 错渲染 italic/bold。
- 不 pin `DelimiterChar`/`count`/`enum` 常量;断 consumer `styled.Text`/`Ranges` 语义。

`Markdown.Tests` 47→**48/48** 绿(`suite-markdown-after-extras-flag.stdout.txt`,exit=0)。
