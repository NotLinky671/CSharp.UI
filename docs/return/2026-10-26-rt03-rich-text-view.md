# RT-03 交付记录：RichText 视图——paint 解算缓存、装饰、纹理键

日期：2026-10-26
状态：**实施完成，待主审验收**（GL 真实像素证据由 fast 外部 harness 接续，本记录不预声明通过）

## 变更文件

### 产品代码

- **`src/CSharp.UI/Views/RichText.cs`（新，554 行）** — `RichText` 视图。
  - `Measure`：`HasRichContent` 分流。
    - plain 路径（无样式/交互区间）：经 `ITextService.Layout` 得 `TextLayoutResult` 直接入 `node.ViewCache`（union），**无富文本分配**（无 segment/paint/interaction 对象），与 `Text` 等价。
    - rich 路径：`IRichTextService.Layout(request)` 返回独立 `IDisposable` handle；`ViewCache` 持 `RichTextViewCache { Layout, Request, Paint, ParagraphForeground }`。
    - **GeometryKey 等价复用**：`Request.Equals(cachedRequest)` 命中时不重复 `Layout`/AddRef，直接复用 handle + 现 paint；`ParagraphForeground` 漂移时即时重算 paint（不重排版）。
    - **探测隔离**：仅 `context.IsProbing`（`BeginProbe` 作用域）用临时 `Layout`+`Dispose`，不污染提交 cache——探测快照恢复原 `ViewCache` 引用保持有效句柄；自然尺寸测量（`Intrinsic`，Stacks 基准遍）**照常提交** cache，与 plain `Text` 同语义。
  - `Emit`：`IsVisible` 早退 → `ViewCache` union 分发。plain → `EmitPlain`（Alpha8 遮罩 + tint，与 `Text.Emit` 等价）；rich → `EmitRich`。
  - `EmitRich`（无逐帧重建）：命令序 = 背景矩形（相邻 segment 同背景色行内合并、行间不合并，仅 `TrimEnd` 内区间）→ `TextureCommand(RichTextTextureKey(LayoutIdentity, Paint, PixelScale), RasterBounds/RasterOrigin 对齐, Tint=White, opacity=环境)` → underline → strikethrough。**Emit 不新建 paint/runs/glyph 集合**，所有 paint 在 ViewCache 已解算。
  - `ResolvePaint`：segment 与 `StyledText.Ranges`/`Interactions` 按文本区间交集解算 `RichTextRunPaint`（fg/bg/underline/strike），hover Id 命中时按 `LinkHoverStyle` 覆盖（RT-04 联调用）。
  - `OnUpdate`/`OnUnmount`/plain↔rich 切换 → `Dispose` 旧 handle（幂等，`Set` 内部先 Dispose 再覆盖）。

- **`src/CSharp.UI/Rendering/DrawCommands.cs`** — 新增 `internal readonly record struct RichTextTextureKey(RichLayoutIdentity Identity, RichTextPaint Paint, double PixelScale)`；`LayoutIdentity` 为 `RichLayoutCore` 的后端无关稳定身份（同 core 多 handle/多 view 共享纹理缓存）。

### 测试宿主修复（非产品行为）

- **`tests/CSharp.UI.Tests/TestApp.cs`** — `UiRuntimeServices` 补 `TextureService = Textures`（此前漏注入：`RecordingTextureService` 实例已建但未接线，`context.TextureService` 为 null，一切 texture 命令路径被 Emit 内 `textureService is null` 早退短路；RT-03 命令断言依赖此路径。现有 684 测试无回归——无纹理断言的测试不受影响，有纹理断言的测试此前因 null 早退而实际未覆盖该路径）。

### 测试代码

- **`tests/CSharp.UI.Tests/RichTextViewTests.cs`（新）** — 14 个消费者行为测试（`[Fact]`），覆盖：plain fast path 服务零调用 / 无区间缺服务不抛 / 有样式缺服务明确抛 / 多样式命令序列（纹理 + 装饰矩形按序）/ 相邻同色背景行内合并且行间不合并 / 换前景只重栅格化不重排版（`Rasterize+1`、`Layout+0`）/ 改 payload 端点不变两计数不变 / 改端点重排版 / 环境前景变化只重栅格化 / Emit 连续帧不重建 paint 或纹理（行为断言：`FactoryInvocations` 不增；**不 pin 内部 `cache.Paint` 对象引用**——内部 paint identity 是实现细节）/ 换内容与卸载释放 handle + Dispose 后 rasterize 抛 `ObjectDisposedException` / 祖先 Opacity 只乘命令不进 paint / 透明样式前景真实预乘合成。

## 实施期发现并修复

- **`IsProbing` vs `Intrinsic` 误判**：首版把 intrinsic 测量当探测丢弃 handle，导致 Stacks 基准遍后提交 cache 为空、`Emit` 无输出。改为仅 `context.IsProbing` 走临时 handle，`Intrinsic` 正常提交 cache。经主审定位（`Contexts.cs` `SubtreeNeedsEffectiveProposal`/`LastMeasureIsIntrinsic` 语义）。
- **`TestApp.TextureService` 漏注入**：见上；非产品缺陷，纯测试宿主接线。

## 验证状态

- `dotnet run`（`tests/CSharp.UI.Tests`，Debug）：**684/684 绿**（含 `RichTextViewTests` 14/14）。
- 真实 GL 像素证据：**主审已验收**——fast 独立 harness **r2（037E）51 项检查 0 失败**（URU/BRSB/trim/alpha/clip + null service/shared texture 补验；dark theme × pixelScale=1.5 快照与 semi-foreground PNG 确认预乘合成）。证据目录 `docs/return/2026-10-26-rt03-rich-text-view-evidence/`（raw `rt03-r2-stdout.txt` RESULT 51 PASS / 0 FAIL）。
- 调试插桩（`Console.Error` stderr）、`DbgTest.cs`、`Dbg2.cs`、`tmp/rt03dbg` 已全部清除；源码无 `TODO`/`NotImplemented`/stub。
- `RichTextViewCache` remarks 措辞已修正（探测/自然测量区分）；`docs/task/03-rich-text-view.md` "run 色 alpha=255" 措辞已更正为按 resolved `Foreground` 原值直绘（不钳 255，真实 alpha 参与预乘）。
- 测试宿主 `TestApp.UiRuntimeServices` 补 `TextureService = Textures`（漏注入修复，非产品缺陷）。
- 平台边界：主审验收证据来自 Windows GL 后端；`RasterBounds` 越 advance 盒的墨迹保留行为由后端无关 `PixelBuffer`/`RichTextTextureKey` 契约承担。

## 风险与边界

- `RichTextTextureKey` 用 `RichLayoutIdentity`（core 稳定身份）而非 caller handle —— 同 core 多 handle 共享纹理缓存是预期语义；若服务侧 evict 后重建 core，`LayoutIdentity` 变化自然失效旧纹理键。
- `ParagraphForeground` 在 `cache.Request` 命中分支即时比较并重算 paint —— `Measure` 命中分支不重跑排版，仅 paint 字段更新。
- RT-04（链接 hit 状态机）在 RT-03 验收通过后开工；本记录不宣称其完成。
