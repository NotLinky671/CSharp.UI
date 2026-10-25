# RT-02：IRichTextService 契约、Skia 排版实现与 lease 所有权

创建日期：2026-10-25

状态：**待实施**（规划完成，待用户授权）

阶段：A

设计依据：[计划](../plan/2026-10-25-rich-text-and-markdown-plan.md) §决策二/三/四。

[返回任务索引](README.md)

前置依赖：RT-01。

## 文件范围

- `src/CSharp.UI/Text/RichTextContracts.cs`（新）：`IRichTextService`、`RichTextLayoutRequest`、`RichTextLayoutResult`（公开几何面+IDisposable handle）、`RichTextPaint`/`RichTextRunPaint`、`ResolvedFontIdentity`；internal `RichLayoutCore`/`IRichTextLayoutBackend`。
- `src/CSharp.UI.Text/RichTextLayoutBuilder.cs`（新）：多样式排版核心。
- `src/CSharp.UI.Text/SkiaFontResolver.cs`：`FontInstanceLease`（refcount，internal）——缓存淘汰只 dispose 无 lease 实例。
- `src/CSharp.UI.Text/SkiaTextService.cs`：追加 `IRichTextService` 实现（同实例双接口）；缓存持 `RichLayoutCore`。
- `src/CSharp.UI/Testing/TestDoubles.cs`：`StubTextService` 追加 `IRichTextService`（core 无原生资源，AddRef/Release 计数 no-op）。
- `src/CSharp.UI/Runtime/UiRuntime.cs`：`UiRuntimeServices.RichTextService`（`IRichTextService?`）+ `ClearCaches` 级联。
- `src/CSharp.UI.OpenTK/UiApplication.cs`：`UiApplicationOptions.RichTextService`；null 时 `TextService is IRichTextService` 复用同实例，否则 null（不 cast 强转外部单接口实现）。
- `tests/CSharp.UI.Text.Tests/`、`tests/CSharp.UI.Tests/` 对应单测。

## 实现契约

### 接口与四元键

```csharp
public interface IRichTextService
{
    RichTextLayoutResult Layout(RichTextLayoutRequest request);              // 每次返回新 handle
    Size Measure(RichTextLayoutRequest request);
    PixelBuffer Rasterize(RichTextLayoutResult layout, RichTextPaint paint);  // RGBA premultiplied
    int CachedLayoutCount { get; }
    int CachedRasterCount { get; }
    int LayoutCacheCapacity { get; set; }
    void ClearCaches();
}
```

- `RichTextLayoutRequest`（GeometryKey）：`Text` + **分段端点拓扑** + 每 segment resolved font 度量字段（Family/Size/Weight/Italic——由 `StyledText` 预算签名提供）+ `ParagraphFont` + `MaxWidth`/`Wrapping`/`Alignment`/`LineHeight`/`PixelScale`/`LetterSpacing`。**颜色/payload 值不进键**。
- `RichTextPaint`：`IReadOnlyList<RichTextRunPaint>`（与 canonical segment 一一对应：resolved fg/bg/underline/strike）+ `Color ParagraphForeground`。栅格缓存键=`(core 引用, paint 值相等)`。
- `RichTextLayoutResult`（公开，`IDisposable` handle）：`Size`/`Lines`/`CaretStops`/`CaretXs`/`HitTestIndex`/`GetRangeBounds`/canonical `Segments`（每行 `{Start,End,ResolvedFontId,X,Width,Baseline}`）。**不持** `StyledText`/`StyleIndex`/payload 引用——paint 解算在视图侧按当前 `Ranges` 与 segment 文本区间归并（决策二）。测试替身可无字体构造。

### 所有权（唯一方案：共享 core + 每 caller handle）

- 服务内部缓存 `RichLayoutCore`（geometry + backend payload：glyphID/positions/advances 按 shape 段 + 每 resolved face 一个 `FontInstanceLease`）；core 内部引用计数。
- `Layout` 命中缓存 → `core.AddRef()`，返回新 handle；未命中 → 排版、缓存持 1 份、handle 1 份。**每次 `Layout` 一次 handle 分配是声明的所有权成本；Emit 不额外构建/复制富文本集合**。
- `RichTextLayoutResult.Dispose()`（幂等）→ `core.Release()`；归零 → core 释放 face leases。缓存淘汰/`ClearCaches` → 缓存那份 Release——**调用方仍持有的 handle 完全合法**。已 Dispose handle 调 API → `ObjectDisposedException`。
- **缓存键身份**：`RichLayoutCore` 提供后端无关 `LayoutIdentity`（同 core 全 handle 共享）；栅格缓存键=`(LayoutIdentity, paint)`——多 handle 同 core 共享 raster 缓存，caller handle 只是所有权 wrapper 不入键。
- `ViewNode.ViewCache` 持有 handle：`RichText.Measure` 放入即获所有权——**每次 Measure 覆盖前 Dispose 旧 handle**（同 GeometryKey 时复用现有 handle 不重复 acquire，避免可避免 wrapper 分配）；`OnUpdate`/`OnUnmount`/plain↔rich 切换同样 Dispose。多 View 各持各 handle。
- 可见性：internal 类型跨 `CSharp.UI`/`CSharp.UI.Text` 访问——`CSharp.UI` 的 IVT 已含 `CSharp.UI.Text`；反向需要处按实现补 IVT（任务内列出实际添加项）。
- `Rasterize` 用 payload glyph 数组 + leased face 直接绘制，**不重新 shape**。

### 排版与 shape 分段

- **分段边界 = 分段拓扑端点**（模型显式，含 interaction 端点位置）+ resolved font identity 变化 + 回退 face 变化 + script/direction 变化。**不按颜色/payload 值分段或合并**——同边界集合不同颜色 → 同 geometry。
- paint-only 边界（模型端点但同 font/face/script）：**分次 shape**（同 face 子段 `AddUtf16`）——禁止跨边界 ligature、kerning 边界截断；一个 glyph 绝不吞两样式。
- 断行机会只看字符属性（英文词跨 bold 不拆行）；`TrimEnd`/段落切分同 `TextLayoutBuilder` 域。
- 行几何：行高=`max(LineHeight>0 ? LineHeight : 0, max(ascent)+max(descent))`——`LineHeight` 下限即行内字高（显式值小于字高时钳制到 max(ascent)+max(descent)，glyph 不越行盒、RGBA 纹理按 layout.Size 完整覆盖）；baseline=leading/2+max(ascent)；run 按 baseline 对齐。
- `StubTextService`：等宽估算镜像契约（segment 宽=字号×CharWidthScale×字数；行高=行内最大字号×1.25；hit 映射可用）。

### 服务注册

- `UiRuntimeServices.RichTextService { get; init; }`（`IRichTextService?`）；ClearCaches 级联。
- `UiApplication` 装配：`options.RichTextService ?? (textServiceInstance as IRichTextService)`；外部单接口 `ITextService` → null，`RichText` 富文本路径明确 `InvalidOperationException`。

## 边界

- `TextLayoutBuilder`/`TextShaping`/`ITextService` 零签名变化；plain 路径零新分配。
- 不实现视图 Emit/链接（RT-03/04）。
- 不引入公开资源框架。

## 验收

- 中文 CJK 逐字断行；英文跨 `**bo**ld` 词不拆行；surrogate/组合/ZWJ 不切；多 face 回退正确。
- paint 边界分次 shape：红f蓝i 各自 glyph 各自色段，无跨边界 ligature、无吞并 glyph。
- 行内异字号 baseline/行高正确；`LineHeight` 钳制语义一致（显式值<字高→按字高，无 glyph 截断）。
- **几何键**：仅颜色不同的 `StyledText` → `Layout` 缓存命中同 core（新 handle，`LayoutMisses` 不变）；改 interaction 端点 → 重排版；改 payload URL（端点不变）→ 不重排版。
- **lease stress**：>64 face 压力下 raster 无 UAF/错 face；`ClearCaches` 后 ViewCache handle 仍合法 raster；同 core 多 handle 各自 Dispose 不误释放；Dispose 后 raster `ObjectDisposedException`；同 layout 换 paint 重 raster。
- `StubTextService` 无字体构造 layout+hit 映射；plain 回归全绿。
