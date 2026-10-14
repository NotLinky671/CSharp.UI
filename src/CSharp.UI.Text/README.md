# CSharp.UI.Text

基于 **SkiaSharp 3.119.4 + SkiaSharp.HarfBuzz（HarfBuzzSharp 8.3.1.5）** 的 CPU 文本与图片服务实现：

- `SkiaTextService`：字体解析与缺字回退、shaping 排版、测量、字素级光标/命中映射、CPU 栅格化（`Alpha8` 覆盖率遮罩）。
- `SkiaImageService`：本地图片文件解码为预乘 8888 位图并按来源缓存。

所有公开 API 只使用 `CSharp.UI` 的框架类型（`TextLayoutRequest`、`TextLayoutResult`、`PixelBuffer`、`FontDescription` …），
不向调用方暴露 `SKTypeface`、`SKShaper`、`SKBitmap` 等后端对象。

装配示例（宿主组装层）：

```csharp
var runtime = new UiRuntime
{
    TextService = new SkiaTextService(),
    ImageService = new SkiaImageService(),
    // …
};
```

约定：与核心一致，服务只在 UI 线程使用，实例不是线程安全的；`Dispose`（或 `ClearCaches`）会释放原生字体、位图与 shaper。

## 一、字体来源与回退策略

`FontFamily` 有三种来源，解析顺序如下（`SkiaFontResolver`）：

1. `FontFamily.FromFile(path)` → `SKTypeface.FromFile(path, 0)`。
2. `FontFamily.FromName(name)` → `SKFontManager.Default.MatchFamily(name, style)`。
3. `FontFamily.Default` → `SKTypeface.Default`（平台默认字体：Windows 为 Segoe UI 系列、macOS 为 Helvetica/PingFang、Linux 为系统默认）。

`FontWeight` 与斜体被翻译为 `SKFontStyle(weight, Normal width, slant)`，因此粗体/斜体请求会尽量匹配同族的对应字重。

### 家族级回退

首选来源失败时，按 `FontFamily.Fallbacks` 的顺序依次 `MatchFamily`，最后回退到 `SKTypeface.Default`：

```csharp
var font = FontFamily.FromName("Microsoft YaHei")
    .WithFallbacks("Segoe UI", "Arial");
```

### 缺字回退（逐字素）

首选字体不含某个码点时，按字素簇（不是码元）做缺字回退：

1. `FontFamily.Fallbacks` 中第一个覆盖该码点的家族；
2. `SKFontManager.Default.MatchCharacter(<首选家族名>, style, bcp47: null, codepoint)`；
3. `SKFontManager.Default.MatchCharacter(null, style, bcp47: null, codepoint)`（全局回退）。

在 Windows 上中文会因此回退到 **Microsoft YaHei UI**，Emoji 回退到 **Segoe UI Emoji**。
每次排版按“字体族”切分成多个运行段（run），逐段 shaping，因此中英文混排、跨字体的字符都能正确显示；
同一对（首选字体, 回退字体）只在 `Diagnostics` 中记录一次，避免刷屏。

所有字体都无法覆盖的码点会记录 `TextDiagnosticKind.MissingGlyph`，并按缺字方框绘制（可解释行为，不抛异常）。

### 诊断

```csharp
var service = new SkiaTextService();
service.Layout(request);
foreach (var diagnostic in service.Diagnostics) { /* 写入日志 */ }
service.DiagnosticReported += (_, d) => log(d.ToString());
```

| 类别 | 触发条件 | 行为 |
|---|---|---|
| `MissingFontFile` | `FromFile` 路径不存在或不是有效字体 | 默认记录诊断并回退；`ThrowOnMissingFontFile = true` 时抛 `InvalidOperationException`（消息含路径） |
| `MissingFontFamily` | `FromName` 家族不存在 | 记录诊断，按回退链继续 |
| `CharacterFallback` | 字符不在首选字体中 | 记录诊断（按字体族去重），使用回退字体 |
| `MissingGlyph` | 无字体覆盖该字符 | 记录诊断，绘制缺字方框 |

`Diagnostics` 有界（最多 64 条、按内容去重），字体解析失败不会让排版失败。

## 二、排版与坐标约定

- shaping 由 `SKShaper`（HarfBuzz）完成，逐运行段（run）进行，包含 kerning 与连字。
  HarfBuzz 返回的位置是字体内部单位，这里按 shaper 报告的总宽度归一化为**逻辑像素**，
  因此“推进量之和 == 排版宽度 == 栅格化时的字形位置”，光标与绘制严格同源。
- 字素边界使用 `System.Globalization.StringInfo`（Unicode 字素簇算法）：
  代理对（`😀`）、组合字符（`e` + `U+0301`）、零宽连接序列（`👨‍👩‍👧`）都不会被拆开。
  `CaretStops` 只包含字素边界上的 UTF-16 索引，严格递增，首项 0、末项 `Text.Length`。
- 换行：`TextWrapping.Wrap` 且 `MaxWidth` 有限时按
  “CJK/假名/谚文逐字素断行、拉丁文本在空白或连字符后断行”贪心排版；
  单词过长且无断行机会时硬断，保证每行至少前进一个字素。行尾空白不计入行宽。
- `MaxLines`：超出部分不可见；`TextTrimming.Ellipsis` 时末行以 `…` 截断（`Text` 为截断后的可见文本，`IsTrimmed = true`）。
- 对齐按**最宽行的内容宽度**计算偏移（与核心 `Text.Emit` 用 `layout.Size.Width` 定位文本块的约定一致），
  行内光标 X 已包含该偏移。
- `Size` = (最宽行内容宽度, 行数 × 行高)；行高 = `LineHeight`（0 表示字体默认 `ascent + descent + lineGap`）。
- `LetterSpacing`（字距）按字素逐个附加在推进量之后。

### 行与光标边界

- `Lines[i]` 的 `[Start, End)` 连续覆盖可见文本；段落末尾的换行符算在**上一行**末尾（被省略时不含）。
- 某个索引同时属于两行边界时（软换行/硬换行处），它归属**前一行**——
  这与 `TextLayoutResult.GetLineForIndex` 的规则一致，因此 `GetCaretBounds`/`GetRangeBounds` 自洽：
  该处光标画在上一行末尾，而下一行内的后续索引使用下一行的坐标。
- 因此 `CaretXs` 在**同一行内**单调不减，跨行会回到下一行起点（单行文本全局单调）。

## 三、缓存键与容量

| 缓存 | 键 | 默认容量 | 释放 |
|---|---|---|---|
| 排版缓存 | `TextLayoutRequest`（记录值相等：文本、字体、`MaxWidth`、`MaxLines`、换行、省略、对齐、`LineHeight`、`PixelScale`、`LetterSpacing`） | `LayoutCacheCapacity = 128` | 无需释放（纯托管结果） |
| 栅格缓存 | `TextLayoutResult` **实例引用** | `RasterCacheCapacity = 64` | 无需释放（`PixelBuffer` 为托管数组） |
| 首选字体缓存 | 文件路径/家族名 + 粗细 + 斜体 + 回退名列表 | 32 | 淘汰/清空时释放 `SKTypeface` |
| 缺字回退缓存 | 码点 + 粗细 + 斜体（失败也缓存 null） | 256 | 淘汰/清空时释放 `SKTypeface` |
| 字体实例缓存 | `SKTypeface` 引用 + 字号 | 64 | 淘汰/清空时释放 `SKFont` 与 `SKShaper`，并释放字体族引用 |

- 所有缓存都是**有界 LRU**（`CSharp.UI.Collections.LruCache`，通过 `InternalsVisibleTo` 使用），
  高频内容变化下 `CachedLayoutCount`/`CachedRasterCount` 不会超过容量。
- `SKTypeface` 在解析器内部按引用计数持有，只有最后一个引用消失时才 `Dispose`（避免共享字体被提前释放）。
- DPI 或字体变更时调用 `ClearCaches()`（同时释放字体资源）；`Dispose()` = `ClearCaches()` + 解析器释放。
- 尺寸/DPI 变更天然通过键失效：`PixelScale` 在排版键中，栅格尺寸按 `ceil(逻辑尺寸 × PixelScale)` 计算。
- 注意：`FontFamily` 的 `Fallbacks` 是 `IReadOnlyList<string>`，record 相等性对集合使用引用相等，
  因此两次分别构造、内容相同的 `FontFamily`（含非空回退列表）在 `TextLayoutRequest` 中**不相等**。
  字体解析内部使用自己的结构化键，不受影响；如需最大化排版缓存命中率，请复用同一个 `FontFamily` 实例。

## 四、像素格式约定

- **文本栅格化**：`Rasterize(TextLayoutResult)` 返回 `PixelFormat.Alpha8` 的灰度覆盖率遮罩，
  尺寸为 `max(1, ceil(逻辑尺寸 × PixelScale))`；绘制时由渲染器通过 `TextureCommand.Tint` 着色。
  栅格化使用 `SKColorType.Alpha8` + `SKAlphaType.Premul` + 字形灰度抗锯齿（`SKFontEdging.Antialias`，关闭子像素渲染以避免彩边），
  字形位置与光标位置同源（同一份 shaping 结果）。
- **图片解码**：`SkiaImageService` 统一输出 `PixelFormat.Rgba8888Premultiplied`
  （核心文档中“纹理与图片统一使用”的格式）。不同平台的 Skia 原生顺序不同（Windows 为 Bgra8888），
  这里统一转换，并且**始终为预乘 alpha**，避免透明边缘在合成时出现色晕。
  `ImageResource.Scale` 为 1，`NaturalSize == PixelSize`。
- 解码失败（文件不存在、格式无效、内容损坏）抛 `ImageLoadException`；`TryLoad` 返回 false 而不抛。
- 图片缓存键为来源字符串，`Capacity` 上限（默认 64），淘汰与 `Clear()` 会 `Dispose` 资源并触发
  `ImageResource.PixelsDisposed`，让纹理服务失效对应纹理。

## 五、第三方字体与可复现截图

**本仓库不重新分发任何第三方字体二进制文件。** 默认（`FontFamily.Default`）使用系统字体，
并按缺字回退覆盖中英文，因此无需随包分发字体即可显示中文：

- Windows：Segoe UI（拉丁）+ **Microsoft YaHei UI**（中文）+ Segoe UI Emoji（Emoji）
- macOS：Helvetica/SF（拉丁）+ **PingFang SC**（中文）
- Linux：DejaVu/Noto（拉丁）+ **Noto Sans CJK**（中文，需系统安装）

如需**可复现的截图或固定排版结果**（不随系统字体变化），请自行把许可允许分发的字体（例如 SIL OFL 的
Noto Sans SC / Source Han Sans）放到应用字体目录，并用文件路径指定：

```csharp
var font = new FontDescription(FontFamily.FromFile(@"assets/fonts/NotoSansSC-Regular.otf"), 14);
var layout = textService.Layout(new TextLayoutRequest("计数：12 次") { Font = font });
```

测试同样不依赖固定字体文件（`tests/CSharp.UI.Skia.Tests` 在任何装有系统字体的机器上运行），
只在“有效字体文件”用例中把系统默认字体的数据导出为临时文件再解析。

## 六、已知限制（首版范围）

- 不实现完整双向文本（RTL）：shaping 按左到右处理，阿拉伯文/希伯来文等从右到左文本不保证正确顺序；
  `CaretStops`/`CaretXs` 的单调性也按 LTR 假设给出。
- 不实现彩色 Emoji 渲染：Emoji 以单色字形（覆盖率遮罩）绘制，`COLR`/`CBDT` 彩色图层不参与栅格化。
- 不实现 IME 组合文本与候选窗定位；不实现字形图集缓存。
- 未做换行压缩（两端对齐）、避头尾标点规则、以及基于 UAX #14 的完整断行规则：
  仅按空白/连字符断行与 CJK 逐字素断行。
- 行高与纵向度量取自**首选字体**；同一行内混用回退字体时不会按最高字体调整行高。
- 段落末尾恰好一个换行符时不会额外产生一个空行（`"a\n"` 为 1 行）。
- 全空白文本的宽度为 0（行尾空白的裁剪规则所致）。
