# RT-02 交付记录：富文本排版服务（IRichTextService + Skia 实现 + Stub）

日期：2026-10-25
任务：[docs/task/02-rich-text-service.md](../task/02-rich-text-service.md)
证据目录：[2026-10-25-rt02-rich-text-service-evidence/](2026-10-25-rt02-rich-text-service-evidence/)

## 本轮公开 API

`src/CSharp.UI/Text/RichTextContracts.cs`（新）：

- **`IRichTextService`**：`Layout(request)` 返回 `RichTextLayoutResult` handle（`IDisposable`）、
  `Measure`、`Rasterize(layout, paint)`、缓存计数/容量、`ClearCaches`。
  `SkiaTextService` 同实例双接口实现；`StubTextService` 以等宽估算真实实现（非 no-op）。
- **`RichTextLayoutRequest`（GeometryKey）**：`Text`（StyledText 本体 + 预计算拓扑）、
  `ParagraphFont`、`MaxWidth`、`Wrapping`、`Alignment`、`LineHeight`、`PixelScale`、
  `LetterSpacing`。相等/哈希仅含几何相关字段——**颜色值与 interaction payload 值不参与**，
  端点/字体签名变化才重排。`ParagraphFont` init 时冻结 `Family.Fallbacks` 快照
  （零 fallback 不复制），防御缓存键被外部可变底层破坏——但 payload/样式对象的可变性
  仍按 RT-01 调用方契约（模型不声称对任意可变 object 深冻结）。
- **`RichTextLayoutResult`**：公开几何面——`Text`/`ParagraphFont`/`PixelScale`/`Size`、
  `LetterSpacing`、`Lines`、`Segments`、`ResolvedFonts`、`CaretStops`、`CaretXs`、
  `RasterBounds`/`RasterOrigin`、`LayoutIdentity`；方法 `HitTestIndex`、
  `LineIndexForPosition`、`SnapToCaretStop`、`GetCaretX`、`GetRangeRects`、
  `GetRangeBounds`、`RetainHandle`/`Dispose`（`LineCarets` 仅是公开构造数据
  record，非 result 属性）。每 `Layout` 调用返回独立 handle（各持 core 一引用）；
  cache 一份；全部引用归还才释放后端 face 租约。`ClearCaches`/cache 淘汰只归还
  cache 那份——**调用方持有的 handle 仍可 Rasterize**。服务 Dispose 后 handle
  几何仍可读、Rasterize 抛 `ObjectDisposedException`；handle Dispose 后所有 API 抛。
- **`RichTextSegment`**：`LineIndex`/`Start`/`End`/`FontId`/`X`/`Width`/`Baseline`。
- **`ResolvedFontIdentity`**：`Requested`（请求字体）+ `ResolvedFamilyName`（实际命中族名）+
  `FaceId`（进程内稳定，按真实 face 实例唯一）+ `Size`/`Weight`/`Italic`。
  **每 face run 分配**——同请求字体经缺字回退命中不同 face 时 FontId 不同，
  公开身份反映真实 face，不以请求 family 冒充实际回退。
- **`RichTextPaint`/`RichTextRunPaint`**：逐 canonical segment 的已解析绘制属性
  （前/背景色、underline/strikethrough 标志），结构相等快照作 raster 缓存键
  （同 LayoutIdentity 换 paint 直接重 raster，不重排）。本轮栅格只烘前景色；
  **背景与装饰矩形由 RT-03 视图层绘制**，raster 不烘（避免双层）。
- **`RichTextLayoutResult`（公开构造）**：外部 `IRichTextService` 实现可仅凭公开 API
  构建有效结果（`text`/字体/`PixelScale`/`Lines`/`Segments`/`ResolvedFonts`/逐行
  `LineCarets`/`RasterBounds`；可选 `IDisposable owner` 在末句柄 Dispose 时归还一次）。
  `RetainHandle()` 供实现方缓存持有份额（与 `Layout` 返回句柄同 core 同权）。
  `LineCarets` 公开；`ResolvedFontIdentity.Create`/`NextFaceId` 公开——第三方不需要
  Skia 或 InternalsVisibleTo 即可自包含实现。

## 内部实现

### RasterBounds（墨迹包围盒，本轮修正）

- `RichLayoutCore.RasterBounds` / `RichTextLayoutResult.RasterBounds`/`RasterOrigin`：
  排版时由已存 glyph（id+段落绝对位置）与租用 face 的真实 `SKFont.GetGlyphWidths`
  每 glyph ink bounds ∪ 布局盒一次算定（逻辑像素，坐标可为负——斜体尾画/j 负 bearing
  合法越出 advance 盒）。**布局度量 Size/Lines/caret 不变**，无重排。
- 栅格化 buffer = RasterBounds 在 `PixelScale` 下向外取整的物理范围（左/上 floor、
  右/下 ceil），canvas 先 `Scale` 再 `Translate(-RasterOrigin)`；像素与逻辑坐标同源。
  修复了 italic `fj`/`jf` 尾画与 j 负 bearing 被按 advance 盒裁 1~6px 的真实缺陷
  （独立参照：ps1/ps1.5/ps2 右裁 1px、`jf` 左裁 6px 右裁 4px——修后 0 裁切）。
- `RasterOrigin`（= floor(bounds·scale)/scale）是视图把纹理放回排版原点的公开
  放置元数据——RT-03 在 layoutOrigin+RasterOrigin 处放纹理即像素对齐 caret/segment。
- **glyph 行归属**：`ComputeRasterBounds` 用 `[line.Start, line.End)` 半开区间定位
  glyph 所在行（`GlyphLineIndex`），与绘制 `segment.Start<=cluster<segment.End` 同源；
  caret 的默认上行 affinity（`LineIndexForStop`）不再错把下行行首 glyph 算进上行。

- `src/CSharp.UI.Text/RichTextLayoutBuilder.cs`（新）：模型段内按 **face + dominant script**
  切 shape 子段（script 用 HarfBuzz `UnicodeFunctions.Default.GetScript`，
  Common/Inherited 继承当前 run）。每个 shape 子段 script 一致，HarfBuzz 方向推断正确；
  **注意**：本轮只保证每子段 script/direction 属性一致，**不是完整双向布局引擎**
  （项目首版不支持 RTL 排版，与既有 plain 路径边界相同）。glyph 存 id + 段落绝对坐标 +
  cluster 表；raster 用 `SKTextBlobBuilder.AddPositionedRun` 的 span 重载直接绘制，
  **绝不重新 shaping**。
- `SkiaFontResolver`：`FontInstanceLease`（引用计数）——LRU 淘汰只 Dispose 无租约实例；
  已租 face 在 `RichTextBackend` 释放时归还。
- `RichLayoutCore`/`IRichTextLayoutBackend`：core 引用计数，cache 与 handle 各持一份；
  backend 不透明（stub 实现为无资源 no-op 计数）。
- `StubTextService`：`IRichTextService` 等宽估算（`CharWidthScale`），真实产出
  行/segment/identity/caret/hit 几何与逐 segment 前色预乘位图。
- 接线：`UiRuntimeServices.RichTextService`（可空）、`UiApplicationOptions.RichTextService`；
  host 默认 `options.RichTextService ?? (textService as IRichTextService)` 复用同实例；
  `UiRuntime.SetViewport` DPI 变化清缓存（同实例去重，仅一次引用比较）。
  host 对自建 `SkiaTextService` Dispose 一次；外部注入实例由应用自行释放。

## 不变式（范围边界）

- `ITextService`/`TextLayoutBuilder`/`TextShaping`/`SkiaFontResolver` 公开签名**零变化**；
  plain 路径无新增分配（仅 SetViewport 一处引用比较）。
- `TextField`/`TextEditor` 行为不变，仍走 `ITextService`。
- 无新视图 Emit/链接交互（RT-03/RT-04）、无 Markdown/Markdig 包、无 RTL/BiDi 引擎、
  无公开资源管理框架。
- `src/CSharp.UI/Collections/LruCache.cs` 本轮**未修改**（早前记录误列）。

| 套件 | Debug | Release | 原始日志 |
|---|---|---|---|
| `CSharp.UI.Tests` | **670/670** | **670/670** | `final-frozen-test-{cfg}-CSharp.UI.Tests.txt` |
| `CSharp.UI.Text.Tests` | **109/109** | **109/109** | `final-frozen-test-{cfg}-CSharp.UI.Text.Tests.txt` |
| `CSharp.UI.Integration.Tests` | **53/53** | **53/53** | `final-frozen-test-{cfg}-CSharp.UI.Integration.Tests.txt` |
| `CSharp.UI.OpenTK.Tests` | **684/684** | **684/684** | `final-frozen-test-{cfg}-CSharp.UI.OpenTK.Tests.txt` |
| 合计（每配置） | **1516/1516** | **1516/1516** | — |
| solution 构建 | 0 警告 0 错误 | 0 警告 0 错误 | `final-frozen-build-{debug,release}.txt` |
| 三 samples（Counter/TodoList/ControlGallery）新鲜构建+5 帧 smoke | exit 0 | exit 0 | `final-frozen-smoke-{cfg}-*.txt` |

本轮新增测试源：`RichTextServiceTests` 24 例（Skia 真实排版/像素）、
`RichTextServiceStubTests` 7 例 + `ExternalRichTextServiceTests` 2 例
（第三方公开构造/owner 归还），已含在上述套件计数内。

### 独立验证（验证 agent，快照外运行真实公共 API）

最终快照证据：`final-frozen-independent-stdout.txt`（`failures=0`，**82 项检查全过**）+
`final-frozen-independent-stderr.txt`、PNG `c2-*`/`c4f-*`/`c4g-*`、`final-source-sha.txt`、
`SHA256SUMS.txt`/`manifest.txt`。历史轮次保留：初轮 42 项（首轮 1 处测试侧期望值 bug——
caret stop 表漏末尾 `Text.Length` 断言，产品输出正确；修复后期望后 42/42）、
`t5-independent-*` 中期 81 项；`final-independent-*` 是裁切发现期（右裁 1px/jf 左 6px
已修）的中间快照——各轮结果不合并表述。

覆盖要点：CJK 逐字断行、跨样式英文词不拆、ZWJ/组合序列不切、paint 边界分次 shape
（红 f 蓝 i 各色段真实像素）、异字号 baseline/LineHeight 钳制、几何键（同文本仅换色命中
同 core / payload 变命中 / 端点变重排）、>64 face 租约压力下已持 handle raster 逐字节一致、
多 handle Dispose 隔离、服务 Dispose 后几何可读/栅格抛、Measure 不泄漏 handle、
paint 数量校验、ParagraphFont fallbacks 值相等键、负 bearing/斜体墨迹 `RasterBounds`
全保留（单/多行、PixelScale 1/1.5/2 裁切 0）、非 IVT 纯托管 provider 公开构造可消费、
owner 全部句柄归还后恰好 Dispose 一次。连字换行：当前所用字体+shaper 6-glyph 样本
未产生 ligature——**未观察到跨 ligature 换行问题**，不宣称已完整验证 ligature 边界。

