# 2026-10-29 滚动条首次拖拽失效修复（增量计划）

## 背景

用户反馈 Gallery 首次打开直接按住滑块拖动无效（约 1 个 thumb 行程残余后冻结），第二次拖拽正常。
上一轮（仅调查，未改码）已实锤根因，见 `local://gallery-first-scroll-investigation.md`：

- `ScrollbarState` 惰性创建于首个指针事件（`OnPointerEnter`/`OnPointerDown`/`OnWheel` 经
  `ScrollbarStateOrCreate`），晚于节点首次 `Arrange`——`LastGeometry` 冷启动为 `null`。
- 首次 thumb 拖拽：Down → `BeginThumbDrag`（`PressKind=Thumb`）→ 第一个 Move →
  `InvalidateLayout` → pump `Arrange` → `SyncGeometry` 把 `LastGeometry==null` 当几何变化 →
  `ReleaseGesture()`（`GestureCancelled`）→ 后续 Move 不写入 `ScrollOffset`。
- 第二次拖拽：`LastGeometry` 已被上一次 pass 填充 → 签名匹配 → 正常。
- fast 独立实机复核（真实 GalleryApp + Skia + SendInput 物理事件）轨迹与 headless 完全同构。

触发条件：state 创建后未经历一次 `Arrange` 的首次拖拽都触发，与用户先晃鼠标/先点击无关。

## 方案（本轮已按上轮评审意见确定）

**在 `SetBaseline`（`BeginThumbDrag`/`BeginTrackPress` 共用）内顺手把
`LastGeometry` 写为 gesture 起始签名** `(axis, trackRect, contentExtent, viewportExtent)`：

- gesture 基线 = 漂移检测基线，语义一致：保护"gesture 期间几何漂移"，而非"state 创建后第一次见签名"。
- 不采用 `LastGeometry==null => Unchanged` 一行放宽——会在 Begin 与 baseline 补写之间把真实
  resize/内容漂移误当首次基线，保留过期 `DragTrackRect`/`DragContentExtent`/`DragViewportExtent`。
- 覆盖重挂载场景（state 销毁重建后同样冷启动）。
- 不改 `SyncGeometry` 逻辑、不改 host 缓存指针坐标（次要风险另案，fast 实机采证中）。

## 改动

- `src/CSharp.UI/Views/Scrollbar.cs`：`SetBaseline` 末尾 `LastGeometry = (axis, trackRect, contentExtent, viewportExtent);` + 注释。
- `tests/CSharp.UI.Tests/ScrollbarMetricsTests.cs`（`ScrollbarInteractionTests`）新增 3 个回归：
  - `FirstThumbDrag_AcrossLayoutPasses_ContinuesThroughUp`：冷启动首拖，每个 Move 间真实
    `ProcessPendingWork`+`BuildFrame`，offset 单调推进、Up 后冻结。
  - `FirstThumbDrag_RealResizeDuringDrag_StillCancels`：首拖已推进后真实
    `SetViewport` resize → gesture 仍被取消（防过度放宽）。
  - `SecondThumbDrag_AfterFirstCompleted_StillWorks`：完整链路首尾两次拖拽均推进。

永久测试只断言消费者可观察行为（offset 推进/冻结/取消），不断言私有字段。

## 验证（本 agent 负责；fast 独立真实窗口 Release 验收另线并行）

- `dotnet build -c Debug`（src + tests）
- `dotnet test tests/CSharp.UI.Tests --filter "FullyQualifiedName~ScrollbarInteractionTests"`
- `dotnet test tests/CSharp.UI.Tests`（全量回归，确认无连带）
- `dotnet test tests/CSharp.UI.OpenTK.Tests`（host 侧定向，若有 scrollbar 相关）

完成后 freeze，不再 build，等 fast 真实窗口验收 + 主审 commit 门。

## 边界

- 不预称修复覆盖一切首次交互问题；`_pointerWindowPosition` 缓存坐标陈旧是独立源码级风险，
  若真实窗口仍失败按新证据另行定位。
- 不做 docs/guide 改写（internal 行为修复，无公开 API/契约变化）；交付记录等 fast 验收后补。
