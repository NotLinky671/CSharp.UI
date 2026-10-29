# 2026-10-29 滚动条首次拖拽失效修复 — 审核记录

## 问题

用户反馈：Gallery 首次打开直接按住滑块拖动无效（短促残余位移后冻结），第二次拖拽正常。
复现路径与窗口/内容无关——凡是滚动条交互状态建立后未经历一次布局的首次拖拽都触发。

## 根因（上一轮调查已实锤，本轮源码复核确认未修）

`ScrollbarState` 惰性创建于首个指针事件（`OnPointerEnter`/`OnPointerDown`/`OnWheel` 经
`ScrollbarStateOrCreate`），**晚于节点首次 `Arrange`**——`LastGeometry` 冷启动为 `null`。

首次 thumb 拖拽：

1. `OnPointerDown` → `BeginThumbDrag`（`PressKind=Thumb`，此时 `LastGeometry` 仍 `null`）；
2. 第一个 `Move` → `InvalidateLayout` → 下一 `Arrange` → `SyncGeometry` 把
   `LastGeometry==null` 当几何变化 → `ReleaseGesture()`（`GestureCancelled`）；
3. 后续 `Move` 不再写入 `ScrollOffset`，`Up` 清捕获——用户看到"拖不动"。

第二次拖拽：`LastGeometry` 已被上一次 pass 填充且签名未变 → `Unchanged` → 正常。

证据（调查阶段）：

- repo 外 headless 真实 `UiRuntime`+`StubTextService` 复现：
  `DRAG1 pressKind Thumb→None、offset 冻结；DRAG2 正常`（纯 `VStack`+`ScrollView`，无懒内容）。
- fast 独立实机复核（真实 GalleryApp + Skia + SendInput 物理事件，默认 Standard）：
  `Down(thumb mid) LastGeom=null Press=Thumb → Move+60 offset 0→248.63 →
  pump: Press=None LastGeom 填 → Move+120 offset 冻结`——与 headless 完全同构。

## 修复

`src/CSharp.UI/Views/Scrollbar.cs` `SetBaseline`（`BeginThumbDrag`/`BeginTrackPress` 共用）
末尾写入 gesture 起始签名：

```csharp
LastGeometry = (axis, trackRect, contentExtent, viewportExtent);
```

取舍：gesture 基线与漂移检测基线合一——state 冷启动不再被误判为"几何变化"，
而 gesture 期间真实视口/轨道/内容漂移仍因签名不同被检出并取消手势。
未采用 `LastGeometry==null => Unchanged` 的放宽写法：它会在 Begin 与 baseline 补写之间
把真实 resize 误当首次采样，保留过期 `DragTrackRect`/`DragContentExtent`/`DragViewportExtent`。

覆盖重挂载场景（state 随 `ResetInteractionState` 销毁重建后同样冷启动）。
未改 `SyncGeometry` 比对逻辑，未动 host 缓存指针坐标（独立源码级风险，另案）。

## 测试

`tests/CSharp.UI.Tests/ScrollbarMetricsTests.cs`（`ScrollbarInteractionTests`）+3 回归：

- `FirstThumbDrag_AcrossLayoutPasses_ContinuesThroughUp`：冷启动首拖，每个 Move 间真实
  `ProcessPendingWork`+`BuildFrame`，offset 单调推进，Up 后冻结。
- `FirstThumbDrag_RealResizeDuringDrag_StillCancels`：首拖已推进后真实 `SetViewport` resize
  → gesture 仍被取消（防过度放宽）。
- `SecondThumbDrag_AfterFirstCompleted_StillWorks`：完整链路首尾两次拖拽均推进。

## 验证（本 agent，Debug）

- `dotnet build src/CSharp.UI -c Debug`：0 警告 0 错误。
- `dotnet test ... --filter` 路径失败于 **MSBuild test-host named-pipe IPC**（基建侧
  `NamedPipeClient.ConnectAsync`，非测试断言）；改直接运行测试程序集
  `tests/CSharp.UI.Tests/bin/Debug/net10.0/CSharp.UI.Tests.exe`（xUnit v3 in-process runner，
  `-class` 过滤 ScrollbarInteraction/FloatingScrollbar/WheelScroll/TextEditorScrollbar/
  ScrollbarMetrics）：**75 tests，0 failed，0.534s**，3 个新回归均发现并通过；
  既有 `GeometryChangeDuringDrag_CancelsGesture` 仍绿（真实几何取消语义未退化）。

## 文档

- `docs/guide/api-reference.md`：`ScrollView` 行与 `TextEditor` 滚动条目补
  "滑块首次按下即可拖拽（无需先滚动/悬停预热）"+"拖拽期间真实几何变化取消该次手势"。
- `docs/plan/2026-10-29-scrollbar-first-drag-fix-plan.md`：本轮增量计划（不删旧 task）。

## 独立验收（fast，真实 OS）— PASS

- headless 真实 `UiRuntime`：cold Down `LastGeometry` 已填、`Press=Thumb`；
  Move+pump offset `227.909→455.818→683.727` 每次 Thumb 保持；Up 冻结；
  第二次 `849→1015→1180` 正常；真实 `SetViewport(760,640)` → `Press=None`
  后续 Move 冻结、新 gesture 正常（真实几何取消未退化）。
- 新启动 Release 真实窗口（pid 90860，DPI 144，SendInput）：首拖三次 Move
  位移 `339/344/340` bitmap px（预期 ~342/次），Up 0；第二次 `249/248/249`，Up 0。
- `samples/CSharp.UI.Samples.ControlGallery/bin/Release/net10.0/ControlGallery.exe`
  已重建（0w0e）；**旧实例需关闭后重开新 exe**。Release 测试（fresh rebuild）：
  Core 相关 85/0（含 3 新回归）、OpenTK 相关 47/0、3 新回归单跑 3/0；Core 测试
  输出与 src 当前 DLL hash 一致。窗口证据为 PrintWindow 裁剪位图——按内容位移
  证明拖拽推进，不声称图中可见 thumb 形状。

## 边界

- `OpenTkInputAdapter` 首 Down 缓存 `_pointerWindowPosition`（`MouseButtonEventArgs`
  无坐标字段）为独立源码级风险；实机未观察到叠加，不在本修复范围、不声称覆盖。
- 交付记录 `docs/delivery/2026-10-29-scrollbar-first-drag-fix-delivery.md` 已写；
  commit/push 已授权一次英文提交。
