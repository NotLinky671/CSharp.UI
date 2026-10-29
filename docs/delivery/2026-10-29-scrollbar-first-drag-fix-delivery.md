# 2026-10-29 滚动条首次拖拽失效修复 — 交付记录

## 范围

用户报告：Gallery 首次打开直接按住滑块无法拖动，第二次正常。
本交付为单点修复 + 消费者回归测试 + 文档；无公开 API/契约变化。

## 根因

`ScrollbarState` 惰性创建于首个指针事件（`OnPointerEnter`/`OnPointerDown`/`OnWheel`），
**晚于节点首次 `Arrange`**——`LastGeometry` 冷启动为 `null`。首次 thumb 拖拽的第一个
Move 触发 `InvalidateLayout` → 下一 `Arrange` → `SyncGeometry` 把 `null` 基线当几何变化 →
`ReleaseGesture()`（`GestureCancelled`）→ 后续 Move 冻结。第二次拖拽签名已填故正常。
凡 state 建立后未经历一次 `Arrange` 的首次拖拽都触发，与先晃鼠标/先点击无关。

## 修复（一处赋值）

`src/CSharp.UI/Views/Scrollbar.cs` `SetBaseline`（`BeginThumbDrag`/`BeginTrackPress` 共用）
末尾写 gesture 起始签名：

```csharp
LastGeometry = (axis, trackRect, contentExtent, viewportExtent);
```

gesture 基线 = 漂移检测基线：冷启动不再误判，gesture 期间真实视口/轨道/内容漂移仍检出并取消。
未采用 `null => Unchanged` 放宽（会把真实 resize 误当首次采样）。未动 host 缓存指针坐标
（独立源码级风险，不在本范围）。

## 测试

`tests/CSharp.UI.Tests/ScrollbarMetricsTests.cs`（`ScrollbarInteractionTests`）+3 消费者回归：

- `FirstThumbDrag_AcrossLayoutPasses_ContinuesThroughUp`：冷启动首拖多 Move 间真实 pump，offset 单调推进，Up 后冻结。
- `FirstThumbDrag_RealResizeDuringDrag_StillCancels`：已推进后真实 `SetViewport` resize → gesture 仍取消。
- `SecondThumbDrag_AfterFirstCompleted_StillWorks`：首尾两次完整拖拽均推进。

只断言消费者可观察 offset/冻结/取消，不 pin 私有字段。

## 验证结果

### Debug（本 agent）

- `dotnet build src/CSharp.UI -c Debug`：0 警告 0 错误。
- `dotnet test --filter` 经 MSBuild test-host named-pipe IPC 失败（基建侧 `NamedPipeClient.ConnectAsync`，非断言失败）；改直接运行 `tests/CSharp.UI.Tests/bin/Debug/net10.0/CSharp.UI.Tests.exe`（xUnit v3，`-class` 过滤 ScrollbarInteraction/FloatingScrollbar/WheelScroll/TextEditorScrollbar/ScrollbarMetrics）：**75 tests，0 failed**。3 新回归均通过；`GeometryChangeDuringDrag_CancelsGesture` 仍绿。

### 独立验收（fast，真实 OS）

- headless 真实 `UiRuntime`：cold Down `LastGeometry` 已填、`Press=Thumb`；Move+pump offset
  `227.909→455.818→683.727` 每次 Thumb 保持；Up 冻结；第二次 `849→1015→1180` 正常；
  真实 `SetViewport(760,640)` → `Press=None` 后续 Move 冻结、新 gesture 正常——
  真实几何变化取消语义未退化。
- **全新启动 Release 真实窗口**（Windows，pid 90860，DPI 144，SendInput 物理输入）：
  首拖三次 Move 画面位移 `339/344/340` bitmap px（每次预期 ~342），Up 0；第二次按当前
  thumb 重算 `249/248/249`，Up 0。证据图为 PrintWindow 裁剪的窗口位图——按内容位移
  证明拖拽推进，不声称可见 thumb 形状。
- `samples/CSharp.UI.Samples.ControlGallery/bin/Release/net10.0/ControlGallery.exe`
  已重建（0 警告 0 错误）。**此前已运行的旧实例需关闭后重新打开此新 exe 才带修复。**
- Release 测试（fresh rebuild 后）：**Core 相关 85/0（含 3 个新回归）、OpenTK 相关 47/0，
  3 个新回归单跑 3/0**；Core 测试程序集输出与当前 src DLL hash 一致、
  list 确认 3 新 methods 在列。初次 82/47 数字来自 10-26 旧 dll，不作 post-fix 依据。
- 证据目录：本轮 `first-drag-evidence`（README + PNG + raw 捕获 + harness 文本，
  无原生二进制）。

## 文档

- `docs/guide/api-reference.md`：`ScrollView` 行与 `TextEditor` 滚动条目补
  "滑块首次按下即可拖拽（无需先滚动/悬停预热）"与"拖拽期间真实几何变化取消该次手势"。
- `docs/plan/2026-10-29-scrollbar-first-drag-fix-plan.md`：本轮增量计划。
- `docs/return/2026-10-29-scrollbar-first-drag-review.md`：审核记录。

## 边界与已知余项

- 修复覆盖 ScrollView/TextEditor 共用滚动条路径（Standard 与 Floating——Floating 因滚轮
  先行建 state+layout，实践中不易冷启动，但同一缺陷路径同源修复）。
- 次要观察 `OpenTkInputAdapter` 首 Down 用缓存 `_pointerWindowPosition`（`MouseButtonEventArgs`
  无坐标字段）为独立源码级风险；实机验证中未观察到与本症状叠加，不在本交付范围、不声称覆盖。
- 本轮修复经主审通过后提交主线（commit 详情见 git 历史）。
