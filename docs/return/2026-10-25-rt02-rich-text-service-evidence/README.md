# RT-02 交付证据：IRichTextService、Skia 富文本排版与 lease 所有权

日期：2026-10-25
提交范围：`src/CSharp.UI/Text/RichTextContracts.cs`（新）、`src/CSharp.UI.Text/RichTextLayoutBuilder.cs`（新）、`src/CSharp.UI.Text/SkiaFontResolver.cs`（lease）、`src/CSharp.UI.Text/SkiaTextService.cs` + `SkiaTextService.Rich.cs`（双接口）、`src/CSharp.UI/Testing/TestDoubles.cs`（Stub 富文本）、`src/CSharp.UI/Runtime/UiRuntime.cs`、`src/CSharp.UI.OpenTK/UiApplication.cs`、`src/CSharp.UI.OpenTK/Hosting/OpenTkHost.cs`（接线）、测试 `tests/CSharp.UI.Text.Tests/RichTextServiceTests.cs`、`tests/CSharp.UI.Tests/RichTextServiceStubTests.cs`。

## 公开 API（冻结）

- `IRichTextService`：`Layout` / `Measure` / `Rasterize(layout, paint)` / `RichLayoutMisses` / `RichRasterMisses` / `RichLayoutCacheCapacity` / `ClearRichCaches`（实现于 `SkiaTextService`，同实例双接口；`StubTextService` 也实现）。
- `RichTextLayoutRequest`：`Text`（StyledText，含预计算拓扑）、`ParagraphFont`、`MaxWidth`、`Wrapping`、`Alignment`、`LineHeight`、`PixelScale`、`LetterSpacing`。GeometryKey 不含颜色/payload；payload 值变化不重排，interaction 端点变化重排。
- `RichTextLayoutResult : IDisposable`：`Text`、`Size`、`PixelScale`、`LetterSpacing`、`Lines`、`Segments`、`ResolvedFonts`、`Clusters`、`CaretXs`、`CaretsByLine`、`LineIndex`、`HitTestIndex`、`GetCaretBounds`、`RangeBounds`、`MeasureWidth`、`RasterBounds`、`RasterOrigin`、`CoreBackend`、`LayoutIdentity`；**公开构造** + `RetainHandle()`（外部 `IRichTextService` 可无 Skia/IVT 构建，`IDisposable owner` 末句柄归还一次）。handle 持 core 一 ref；Dispose 后所有 API 抛 `ObjectDisposedException`；`Measure` 内部借用 core 后 finally 释放。
- `RichTextSegment`：`LineIndex`、`Start`、`End`、`FontId`、`X`、`Width`、`Baseline`。
- `ResolvedFontIdentity`：`Requested`（请求 FontDescription）、`ResolvedFamilyName`（实际命中族名）、`FaceId`（进程内稳定、按 face 实例引用唯一）、`Size`/`Weight`/`Italic`。每 face run 分配；同请求字体命中不同回退 face 时 `FontId` 不同——公开身份反映真实 resolved face。
- `RichTextPaint` / `RichTextRunPaint`：只读结构相等快照；`Runs` 数与 `Segments` 数一一对应，raster 时验证。
- `UiRuntimeServices.RichTextService`（`IRichTextService?`）；`UiRuntime.SetViewport` DPI 变化时 `ClearRichCaches` 级联。
- `UiApplicationOptions.RichTextService`；null 时 `textService as IRichTextService` 复用同一实例（不强制 cast 第三方 plain 实现）。

## 内部（保持私有）

- `RichLayoutCore`：ref-counted；cache owner 一 ref，每 handle 一 ref；`AddRef`/`Release`；`Release` 归零时 `Backend.Dispose`。
- `IRichTextLayoutBackend` / `RichTextBackend`：opaque；`GlyphRun`（id + 段落绝对偏移 + cluster 表 + `FontInstanceLease`）、`FontRun`（fontId → `ResolvedFonts`）、`XAt`。
- `FontInstanceLease`：`SkiaFontResolver.AcquireLease(instance)`；LRU 淘汰只 Dispose 无 lease 实例（`AcquireLease` miss 时先 `instance.AcquireRef()`，缓存 eviction 不杀活 face）。所有 `GlyphRun` 在 `RichTextBackend.Dispose` 里归还。
- `LruCache<TKey,TValue>`：泛型化，plain 与 rich 两套缓存共用。
- `SkiaTextService` plain 部分：`Layout`/`Measure`/`Rasterize` 接口、`TextLayoutBuilder`、`Shaping`、`FontRunSplitter` 签名与行为不变（无 run 分配）。

## 真实行为验证（CPU Skia，无 GPU/GL）

`tests/CSharp.UI.Text.Tests.RichTextServiceTests`（20 用例，Debug）：

- 换行：纯 ASCII 长行 wrap；CJK `世界世界世界世界` 逐字断行，行数 >1。
- paint 分色：`fi`（红 f / 蓝 i）、`红f蓝i` 三段（不同字号+颜色）→ 各 segment 行矩形内 dominant 像素命中各自颜色（`FindDominantNonTransparent`），跨样式英文词不断行。
- surrogate/组合：`e\u0301` 组合、`👨\u200d👩\u200d👧` ZWJ 均在 `HitTestIndex` 返回同一 grapheme 起点。
- 行内异字号：`小BIG小` 三段 baseline/行高正确（大行段 baseline 更深、行高 ≥ 最大字高），`LineHeight` < 字高时按字高钳制。
- 几何键：同 `StyledText` 仅 `TextStyle.Foreground` 不同 → `LayoutMisses` 不变（core 命中，新 handle）；改 `TextInteractionRange` 端点 → miss +1；改 payload 值（端点不变）→ miss 不变。
- lease：`for size 1..80 GetFontMetrics` 逐出 >64 face 后，已持 handle 的 `Rasterize` 像素与之前逐字节一致（`expected.Pixels` 等于 `again.Pixels`）。
- 多 handle 同 core：分别 Dispose 互不误释放；全部 Dispose 后 `Rasterize` 抛 `ObjectDisposedException`。
- `ClearRichCaches` 后已持 handle 仍可 raster 与查询；`Measure` 返回与 `Layout.Size` 一致（内部借用不泄漏 handle）。
- 服务 `Dispose` 后 handle 几何仍可读、`Rasterize` 抛 `ObjectDisposedException`。
- `LetterSpacing` 加宽测量宽度。
- `paint.Runs` 数与 `Segments` 不符 → `ArgumentException`。

`tests/CSharp.UI.Tests.RichTextServiceStubTests`（Stub，`CharWidthScale` 等宽估算，无原生资源）：layout + hit + `Segments`/`ResolvedFonts` 可用，`RichTextPaint` 校验一致；`RichLayoutMisses`/`ClearRichCaches` 语义与 Skia 一致。

plain 回归：现有 `SkiaTextServiceTests`、`TextLayoutBuilderTests` 等不动；`CSharp.UI.Text` Debug 构建 `0 警告 0 错误`，solution 构建 `0 警告 0 错误`。

## 已知边界（本轮不实现）

- RT-03 视图 Emit/链接 hover、`TextField`/`TextEditor` 富文本化、Markdown。
- `ResolvedFontIdentity.FaceId` 仅进程内稳定，不跨进程持久。
- `ResolvedFonts` 表只含本次排版实际命中的 face；同一 `FontDescription` 在不同排版/不同 face 实例上可能产生不同 `FaceId`。
- `SkiaTextService.ClearCaches` 同时清 plain 与 rich 缓存；`ClearRichCaches` 只清 rich。

## 验证产物

- `/tmp/rt02-logs/rich-tests.txt`：`RichTextServiceTests` 全 20 用例通过（`Total: 20, Failed: 0`，0.741s）。
- Debug solution build：`0 警告 0 错误`。

## 冻结前 reviewer 修正（已合入）

- `GlyphRun.Points` 改为**段落绝对** X（相对段落内容起点），`BuildRichRaster` 按行用 `paragraphOriginX = line.Bounds.X - xAt[line.Start]` 作 blob origin；消除此前 run-local 二次偏移导致的跨 run 重定位 bug。
- `BuildRichRaster` 绘制前 `canvas.Clear(SKColors.Transparent)`，不依赖 bitmap 默认初始化。
- `DrawGlyphRun` 不再逐 segment 克隆 `subIds`/`subPoints`：用 `GlyphIds.AsSpan(first,count)` / `Points.AsSpan(first,count)` + `SKTextBlobBuilder.AddPositionedRun(ReadOnlySpan<ushort>, SKFont, ReadOnlySpan<SKPoint>)`。
- `ResolvedFontIdentity` 补齐：按（请求字体 + face 实例引用）每 face run 分配，公开 `ResolvedFamilyName`/`FaceId`/`Size`/`Weight`/`Italic`；`RichTextSegment.FontId` 索引此表，回退 face 身份真实而非请求 family 冒充。


## 冻结后补充修正（第二轮合入）

- **script/direction 切分**：`ShapeSegment` 内 face run 再按 dominant Unicode script 切分
  （`UnicodeFunctions.Default.GetScript`，HarfBuzz 内置数据库非手写码表）；Common/Inherited/
  Unknown 继承当前 run script。每个 shape 子段 script 一致，`GuessSegmentProperties` 推断与
  真实 script 相同——Arabic/CJK 等异 script 混排不跨段 shaping（完整双向算法不在本轮范围）。
- **`ParagraphFont` 冻结**：init setter 在 `Family.Fallbacks` 非空时 `FrozenSnapshot()`
  （零 fallback 不 clone，getter 返回原 `FontFamily` 引用）；调用方事后修改底层可变列表
  不改变缓存键。
- **stub `SegmentAt` 捕获游标 bug**：原实现捕获 `segIndex` 跨行 metrics 与 run 聚合两轮复用，
  第二轮起始终越界——改为纯查找，修复 stub 多段/多行场景段切分。
- **回归**：`RichTextServiceTests` 23/23（含混合 script + RasterBounds 负 bearing/斜体
  墨迹无裁切）、`RichTextServiceStubTests`+`ExternalRichTextServiceTests` 9/9（第三方
  公开构造 layout/owner 归还）、`CSharp.UI.Text.Tests` 全套 108/108、
  `CSharp.UI.Tests` 全套 670/670 全绿（Debug）；solution 构建 0 警告 0 错误。
  原始日志：`/tmp/rt02-logs/rich-tests.txt`、`/tmp/rt02-logs/stub-tests.txt`、
  `/tmp/rt02-logs/text-suite.txt`、`/tmp/rt02-logs/core-suite.txt`。

### 第三轮修订记录

- stub 测试中非法的 mutation fixture（`IList` cast 抛 `NotSupportedException` / 反射改私有
  backing field）已删除——`FontFamily.Fallbacks` 公开为 init-only，外部本就无法注入可变底层；
  替换为真实消费者语义：两独立同值 family 的请求命中同 core、fallback 不同则 miss、
  getter 暴露只读视图。不 pin `ReadOnlyCollection` 具体类型或 `Assert.Same` 实现细节。
- `UiRuntime.SetViewport` 缓存清理去重：`RichTextService` 与 `TextService` 同实例时不再
  二次 Clear（不同实例各自清；plain 路径仅一次引用比较）。

### 第四轮修订记录（RasterBounds 修复 + 公开构造面）

- **墨迹裁切修复**：`RichLayoutCore`/`RichTextLayoutResult` 新增 `RasterBounds`（逻辑
  坐标，glyph 真实 ink bounds ∪ 布局盒，可负 bearing）与 `RasterOrigin`；排版一次算定
  （`SKFont.GetGlyphWidths` per-glyph bounds + 段落绝对 glyph 位置 + 行 origin/baseline），
  栅格 buffer 按物理网 floor/ceil 覆盖并 Translate(-RasterOrigin)——italic `fj`/`jf` 的
  右尾画/j 负 bearing 不再按 advance 盒裁切（参照实测右裁 1px、jf 左 6px 右 4px → 0）。
  布局 Size/Lines/caret/segment 语义不变。
- **公开构造面**：`RichTextLayoutResult` 公开 ctor（text/fonts/lines/segments/
  resolvedFonts/逐行 LineCarets/RasterBounds/可选 owner），`RetainHandle()`，
  `LineCarets` 公开，`ResolvedFontIdentity.Create`/`NextFaceId` 公开——第三方
  `IRichTextService` 实现无需 Skia/IVT；`owner` 在末句柄 Dispose 归还一次
  （`NoopRichBackend` 默认无资源）。

### 双配置最终回归

- Debug：`CSharp.UI.Text.Tests` 109/109、`CSharp.UI.Tests` 670/670。
- Release：`CSharp.UI.Text.Tests` 109/109、`CSharp.UI.Tests` 670/670
  （`implementation-*-release.txt` 新日志，不覆盖旧文件）。
- `RasterBounds` 实测：`jf` italic 20px → `(-2, 0, 13.84, 26.6)` vs advance 宽 11.09
  （左负 bearing −2px、f 尾画右 +2.75px 全保留；`RasterOrigin=(-2,0)`）。
  软换行 `"xxx jf"` wrap（width 40px → 2 行）→ 下行行首 j 的 −2px 左 bearing 计入
  `RasterBounds.Left`；glyph 行归属按 `[line.Start, line.End)` 半开区间（与绘制
  `segment.Start<=cluster<segment.End` 同源；caret 上行 affinity 不参与墨迹定位）。

### 独立验证快照说明（rt02-verification）

- `final-source-sha.txt`：按文件系统遍历 `src/`/`tests/`/`samples/` 的源码与构建输入（排除 bin/obj/.git/TestResults），共 237 项 = 232 已跟踪 + 5 本轮新增未跟踪文件（`RichTextContracts.cs`、`RichTextLayoutBuilder.cs`、`SkiaTextService.Rich.cs`、`RichTextServiceTests.cs`、`RichTextServiceStubTests.cs`），重算与磁盘 0 mismatch。早前 232 项版本基于 `git ls-files`，漏掉该 5 个新文件，已更正并记录在 `independent-notes.txt`。
- 独立 82-check smoke 原始记录见 `final-frozen-independent-stdout.txt`（二进制 SHA 262169c1…/01c64555…）；更早 `final-independent-stdout.txt`（48/1 clip FAIL）与 `t5-independent-stdout.txt`（interim）按原样保留作历史。