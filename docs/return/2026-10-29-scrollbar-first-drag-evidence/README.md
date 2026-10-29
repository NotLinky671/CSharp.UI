# 2026-10-29 Scrollbar 首次拖拽修复 — 独立 Release 验收证据

本目录由验收 agent 生成，只含本次实测 raw 文本与截图，不覆盖既有 `2026-10-29-scrollbar-first-drag-review.md`。
未修改产品代码、未新增/修改永久测试、未提交。

## 1. 被验收内容

- 产品改动（由实现 agent 提供，验收时只读）：`src/CSharp.UI/Views/Scrollbar.cs` `SetBaseline` 末尾
  `LastGeometry = (axis, trackRect, contentExtent, viewportExtent);`；`SyncGeometry` 取消逻辑未放宽。
- 永久回归（实现 agent 提供，验收只运行）：`tests/CSharp.UI.Tests/ScrollbarMetricsTests.cs`
  `FirstThumbDrag_AcrossLayoutPasses_ContinuesThroughUp`、
  `FirstThumbDrag_RealResizeDuringDrag_StillCancels`、
  `SecondThumbDrag_AfterFirstCompleted_StillWorks`。

## 2. Release 构建（验收 agent 唯一执行）

```
dotnet build samples/CSharp.UI.Samples.ControlGallery/CSharp.UI.Samples.ControlGallery.csproj -c Release
```

- **exit=0，0 警告，0 错误**
- 构建产物 `src/CSharp.UI/bin/Release/net10.0/CSharp.UI.dll` mtime 2026-10-29 09:59:02（含修复）。
- raw：`raw/build-release.stdout.txt`、`raw/build-release.stderr.txt`（空）。
- 注意：Gallery build **不会编译 tests**；test 程序集另于第 5 节单独 Release 重建（否则旧 DLL 不能作为修复后证明）。

## 3. 真实新进程窗口首拖（Win32 SendInput，默认 Standard bar）

驱动 `harness/driver`：启动自己的 `ControlGallery.exe`（pid=90860，Release），等待首帧后
**不发送任何预热输入**，把光标放到冷启动 thumb 中心，首个鼠标输入即 `LEFTDOWN`；每次 Move 之间留 350 ms
让真实帧循环 pump 并截图。仅关闭自己启动的 pid（WM_CLOSE，exit=0）。

坐标：client 900x700，origin=(210,281)，DPI=144；该 app 的指针/布局坐标 = client 像素（实测反推：
内容位移 339~344 px = 55 逻辑 offset 227.9 × 渲染 1.5），故 cold Down = (894, 86.464) 逻辑/client。
源码默认 `ScrollbarStyle.Standard`，几何 thumbLen=168.927、travel=527.073、maxOffset=2184.086。

结果（`raw/run-cold2.stdout.txt`、`raw/driver.log.txt`）：

| 步骤 | 内容纵向位移（bitmap px） | 推导 offset Δ（逻辑） | Down/Up 立即位移 |
|---|---|---|---|
| cold Down | — | 0 | 0（未翻页 → 命中 thumb，非 track） |
| Move1 (+55) | 339 | 226.0 | — |
| Move2 (+55) | 344 | 229.3 | — |
| Move3 (+55) | 340 | 226.7 | — |
| Up | 0 | 0 | 0（冻结） |
| 2nd Down（当前 thumb 中心 251.464） | — | 0 | 0 |
| 2nd Move1 (+40) | 249 | 166.0 | — |
| 2nd Move2 (+40) | 248 | 165.3 | — |
| 2nd Move3 (+40) | 249 | 166.0 | — |
| 2nd Up | 0 | 0 | 0（冻结） |

首次 gesture 的**三个 Move 每一步都继续滚动**（不是仅首 Move 生效）；第二次 gesture 先按当前 offset
重算 thumb 中心再按相同动作重复，同样每步滚动。期望值 55/527.073×2184.086=227.909（×1.5≈341.9 px）、
40/527.073×2184.086=165.752（×1.5≈248.6 px），实测与期望一致（见 `raw/measurements.txt`）。

截图（新窗口 before/down/mid/end + 第二 gesture）：`screenshots/01-before-cold.png` … `screenshots/11-second-up.png`
（`-small.png` 为 450×350 缩略，原图 900×700）。

### 截图说明（为何 PNG 中看不到滚动条本体）

本机 DPI=144，app 以 1.5× 渲染（framebuffer 1350×1050），`PrintWindow` 返回的是渲染面左上 900×700
裁剪；右侧 12 逻辑 px（18 px 物理）的 Standard gutter 落在这块裁剪之外，因此截图里看不到 track/thumb。
这是 capture 通道限制，不是未绘制/不是换成 Floating fixture；滚动证据采用逐步内容位移 + Down/Up 即时位移为 0
（thumb 按住而非 track 翻页）+ 下节独立内部 trace。

## 4. 独立 runtime trace（真实 GalleryApp + UiRuntime + Skia，reflection 只读）

`harness/probe` 引用同一 Release DLL，创建真实 `GalleryApp`（900×700，light），使用 `SkiaTextService`、
HeadlessDispatcher/TestUiClock；仅通过反射读取 `ScrollbarState` 诊断（非永久测试）。raw：`raw/probe.stdout.txt`。

- cold `Down` 后：`PressKind=Thumb`，**`LastGeom=(Vertical,(888,0,12×700),2884.0859375,700)`（Down 时已填基线）**
- Move1/2/3 均 `ProcessPendingWork()+BuildFrame()` 后：`PressKind=Thumb` 保持，offset
  **227.909 → 455.818 → 683.727**
- `Up` 后 `PressKind=None`，后续 Move offset 冻结于 683.727
- 第二 gesture（重算中心 251.464）：offset **683.727 → 849.479 → 1015.231 → 1180.983**
- 真实 resize（`SetViewport(760,640)`）后：`PressKind=None`、`LastGeom` 更新；后续 Move 冻结于 227.909；
  resize 后的新 gesture 正常滚动（434.607）
- 结果：`RESULT=PASS`（0 失败）

## 5. 定向测试（in-process runner，因环境 MSBuild MTP 命名管道不可用）

`dotnet test` 在本环境因 `NamedPipeClient.ConnectAsync` `UnauthorizedAccessException`（基建错误）无法启动，
改用测试程序集自带 xUnit v3 in-process runner（`dotnet <test>.dll -class ...`）。

**测试程序集必须是修复后现编的**：验收开始时 `bin/Release` 里的测试 DLL 是 2026-10-26 17:48 的旧产物
（不含 3 个新 regression，Discover 也查不到），因此**先重建**再跑：

- `dotnet build tests/CSharp.UI.Tests/CSharp.UI.Tests.csproj -c Release`：exit=0，0 警告 0 错误，
  产物 mtime 2026-10-29 10:19:09；raw `raw/fresh-core-tests-build.*.txt`
- `dotnet build tests/CSharp.UI.OpenTK.Tests/CSharp.UI.OpenTK.Tests.csproj -c Release`：exit=0，0 警告 0 错误，
  产物 mtime 2026-10-29 10:19:14；raw `raw/fresh-opentk-tests-build.*.txt`
- 两个测试输出目录的 `CSharp.UI.dll` 与产品 Release DLL 哈希一致：
  `0f14b931c0ab20f5b5caed6cf2acc33a`（含 `SetBaseline` 修复）。
- `-list methods` 实际 Discover 到 3 个新 regression（raw `raw/fresh-core-tests-list.txt`）：
  `FirstThumbDrag_AcrossLayoutPasses_ContinuesThroughUp`、
  `FirstThumbDrag_RealResizeDuringDrag_StillCancels`、
  `SecondThumbDrag_AfterFirstCompleted_StillWorks`。

重建后定向运行（raw 均为 `fresh-*`）：

- Core（Release，`CSharp.UI.Tests`）：
  `ScrollbarMetricsTests`、`ScrollbarInteractionTests`、`FloatingScrollbarTests`、`WheelScrollTests`、
  `TextEditorScrollbarTests`、`ScrollImageTests` → **Total 85, Errors 0, Failed 0**
  raw：`raw/fresh-core-tests-inproc.stdout.txt`
- 上述 3 个新 regression 单独运行 → **Total 3, Errors 0, Failed 0**
  raw：`raw/fresh-3-regressions.stdout.txt`
- OpenTK（Release，`CSharp.UI.OpenTK.Tests`，已针对新 Core 重建）：
  `GalleryScrollbarDemoLayoutTests`、`GalleryNestedScrollTests`、`GalleryHostResizeTests`、
  `WheelScrollIntegrationTests`、`GalleryBreakpointInputTests`、`GalleryDpiLayoutTests`
  → **Total 47, Errors 0, Failed 0**
  raw：`raw/fresh-opentk-tests-inproc.stdout.txt`

更正：本目录 `raw/*PREBUILD-STALE*` 为最初误用旧测试 DLL 得到的 82/47 结果，仅留痕，**不作为修复后测试
证明**；`raw/dotnet-test-infra-failure.txt` 记录 `dotnet test` 的基建命名管道失败（非断言失败）。

## 6. 已知不在本修复范围

- `OpenTkInputAdapter` 首个 Down 使用缓存 `_pointerWindowPosition`（OpenTK MouseButtonEventArgs 无坐标）
  的实机坐标风险：本次 headless trace 的 cold `Down` 不经过 host 缓存，修复本身不依赖它；实机驱动的
  Down 前有一次 `SetCursorPos`，故实机坐标可能来自缓存，两路证据互补而非互相替代。
- track 点击翻页、Floating bar 显隐规则未改动、未在本轮重测（Core 既有 Floating/Wheel 套件全绿）。

## 7. 清理

- 所有 mouse 输入仅作用于自己启动的 pid；Gallery 均以 exit code=0 退出，无残留进程。
- 临时 probe/driver 的 bin/obj 与 27 MB BMP 已在 repo 外删除；保留源码与 raw/截图于本目录，不含 native bin。
- 本目录 `.gitattributes` 为 `* -text`，避免 PNG/raw 文本被行尾转换破坏。
