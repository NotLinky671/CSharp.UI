# RT-03：RichText 视图——paint 解算缓存、装饰、纹理键

创建日期：2026-10-25

状态：**已完成并验收**（2026-10-26 主审：核心套件 684 全绿含 14 新测试；47 项真实 GL 检查无失败；fast 独立 harness 证据见 `docs/return/2026-10-26-rt03-rich-text-view-evidence/`）

阶段：B

设计依据：[计划](../../plan/2026-10-25-rich-text-and-markdown-plan.md) §决策二/五/十三。

[返回任务索引](README.md)

前置依赖：RT-02。

## 文件范围

- `src/CSharp.UI/Views/RichText.cs`（新）：`RichText` 视图。
- `src/CSharp.UI/Rendering/DrawCommands.cs`：`RichTextTextureKey`。
- `src/CSharp.UI.Text/SkiaTextService.cs`：`Rasterize(layout, paint)` RGBA 实现（payload glyph + leased face 直接绘制，不重 shape）。
- `tests/CSharp.UI.Tests/`、`tests/CSharp.UI.OpenTK.Tests/` 对应测试。

## 实现契约

### 视图

```csharp
public sealed class RichText : View
{
    public RichText(StyledText content);
    public StyledText Content { get; init; }
    public FontDescription? Font { get; init; }
    public TextAlignment Alignment { get; init; }
    public TextWrapping Wrapping { get; init; }            // 默认 Wrap（富文本多行展示为主）
    public double LineHeight { get; init; }
    public double LetterSpacing { get; init; }
    public TextPaintStyle? LinkHoverStyle { get; init; }   // paint-only hover 覆盖（无字体字段）
    public Action<object?>? OnLinkHover { get; init; }
    // With* 复制方法同 Text 惯例
}
```

- `Measure`：`HasRichContent` → 构造 `RichTextLayoutRequest`（`StyledText` 预算签名——Measure 不做全量样式 hash）：**`ViewCache` 已持 handle 且新请求 GeometryKey 相同 → 复用 handle（不重复 `Layout`/AddRef）**；几何键不同 → `Layout` 取新 handle 并在覆盖 `ViewCache` 前 `Dispose` 旧 handle（每次 Measure 先做这步——resize/DPI 触发 Measure 不走 `OnUpdate`）。`ViewCache` 存 `(handle, resolvedPaint)`。否则 `TextService.Layout` → `TextLayoutResult`（union，零 rich 分配）。
- `RichTextService` null 且 `HasRichContent` → `InvalidOperationException`（指明 `UiRuntimeServices.RichTextService`）。
- **Emit 无额外富文本分配**：paint **不在**每帧重建——`ViewCache` 持已解算 `RichTextPaint`；解算只发生在：Measure（新排版）、`OnUpdate`（新 Content/环境语义变）、RT-04 `PaintDirty`（hover 边）。Emit 用现成 paint 取纹理+加命令，**不额外构建/复制 runs、paint、glyph 集合**（绘制命令按既有 `DrawCommand` 路径产生，不为零分配私改渲染管线）。
- **paint 解算**（脏时）：按**当前** `StyledText.Ranges` 与 layout canonical segment **文本区间交集**归并——segment 是"模型端点起始、再按换行/回退 face/script 细分"的实际几何片段（不跨任何模型端点但可在其内细分），映射取交集后按 segment 覆盖的唯一 style range / interaction range 解算 → resolved fg/bg/underline/strike + `ParagraphForeground`（环境）；hover Id 命中时该 Id 覆盖 segment 用 `LinkHoverStyle`（`TextPaintStyle`）覆盖字段。**不经 layout 回查旧 `StyledText`/`StyleIndex`**。装饰矩形合并（相邻 segment 同背景色）只影响绘制矩形数量，不改 glyph 几何。
- Emit 命令顺序：背景矩形（相邻 segment 同背景合并、TrimEnd 内）→ 文本纹理（`RichTextTextureKey(LayoutIdentity, paint, PixelScale)`——core 稳定身份非 caller handle 引用，Tint=White，opacity=环境）→ underline → strikethrough。
- `RichTextTextureKey = (LayoutIdentity, RichTextPaint 值, double PixelScale)`——`LayoutIdentity` 为 `RichLayoutCore` 的后端无关稳定身份（同 core 全 handle 共享；caller handle 只是所有权 wrapper 不入键，多 view 同 core 共享 raster/texture 缓存）。
- `Rasterize`（Skia）：RGBA premultiplied `PixelBuffer`（`RasterBounds` × PixelScale 向外取整：左/上 floor、右/下 ceil——墨迹可越 advance 盒，**不裁字**）；run 色按 resolved `Foreground` 原值直绘（含真实 alpha，预乘合成由位图承担，不钳 255）。
- 纹理原点：纹理放在 **排版原点 + `layout.RasterOrigin`**（=`floor(RasterBounds·scale)/scale`——位图像素 (0,0) 的逻辑坐标，可为负）；纹理尺寸对应 `RasterBounds`（不是 `layout.Size`）——视图按此把位图对齐回 caret/segment 逻辑坐标。负 bearing/斜体尾画的越界墨迹随位图落在 `layout.Size` 盒外属合法（`layout.Size` 仍只表示 advance 度量）。
- handle 生命周期：`ViewCache` 持有；`OnUpdate`/`OnUnmount`/plain↔rich 切换 → `Dispose` 旧 handle（幂等）。
- plain fast path `Emit` 与 `Text.Emit` 等价（Alpha8+tint）。

## 边界

- 链接 hit 状态机在 RT-04；本任务预留 `PaintDirty` 位与 hover Id 查询点（RT-04 联调；本任务 hover 恒 null 时 paint 不变的断言）。
- 不做 selection/编辑/嵌入对象。
- `Text`/`TextField`/`TextEditor`/`ITextService` 零改动。

## 验收

- 多样式段落：粗/斜/变色/背景/underline/strike 逐 segment 正确（命令序列+像素断言）。
- 顺序：背景→纹理→underline→strike；`TrimEnd` 外无矩形。
- **三键**：换前景（样式或环境）→ `Layout` 计数不变、`Rasterize` +1；改 payload（端点不变）→ 两计数不变；改端点（样式范围/interaction 范围）→ 重排版；字体/宽变 → 重排版。
- **Emit 无富文本重建**：连续 N 帧 Emit 后 `RichTextPaint` 对象引用不变（无每帧重建，替身计数/引用断言）；绘制命令按既有 `DrawCommand` 路径产生。
- hover `LinkHoverStyle`：RT-04 联调——hover 中 paint 变（新纹理键）、离开还原。
- 像素吸附与 plain 同规则；祖先 clip/opacity/disabled 一致。
- plain fast path：`StyledText.Create("x")` 经 `ITextService`（替身断言 `IRichTextService` 零调用）；`RichTextService` null 无样式正常、有样式明确抛。
- handle Dispose：换内容/unmount 后旧 handle 释放（服务侧 core refcount 断言）；Dispose 后 raster `ObjectDisposedException`。
