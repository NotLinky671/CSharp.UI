# R18：交互调整窗口大小期间未及时绘制新区域

记录日期：2026-10-16

审核人：Codex

审核版本：`b3fc734`

优先级：P2

状态：DeepSeek Harness 已修复（回调内即时绘制 + 重入/生命周期保护 + 全 framebuffer 清屏），新增 9 例宿主回归并通过正反向验证；待 Codex 最终复审。

## 用户现象

用户用鼠标调整 ControlGallery 窗口大小时，旧内容保留在左上角，新扩出的右侧和底部为黑色。截图的标题栏已经覆盖新窗口宽度，UI 背景却只覆盖较小的旧矩形，符合窗口已变大但没有及时提交新帧的表现。

## 代码原因与证据

- [OpenTkHost.HookWindowEvents](../../src/CSharp.UI.OpenTK/Hosting/OpenTkHost.cs) 的 Resize/FramebufferResize 只调用 UpdateViewport，Refresh 只调用 InvalidateRender；两者均不在回调中执行绘制和 SwapBuffers。
- Run 只在 PumpEvents 返回、处理排队动作和布局后调用 DrawFrame。Windows 默认窗口过程在鼠标拖拽边框期间进入移动/调整大小的模态循环，不能假设外层 PollEvents/WaitEvents 会及时返回并继续普通绘制迭代。
- 因此尺寸可以已更新、NeedsDraw 也为 true，但新帧仍未提交。系统扩出的缓冲区没有被应用背景色覆盖，可以呈现用户截图中的黑带。

官方依据：[Windows WM_ENTERSIZEMOVE](https://learn.microsoft.com/en-us/windows/win32/winmsg/wm-entersizemove) 说明默认窗口过程会进入调整大小模态循环；[GLFW 窗口刷新指南](https://www.glfw.org/docs/3.4/window_guide.html#window_refresh) 的刷新回调示例在回调里绘制并交换缓冲。GLFW 3.4 的 Win32 源码把 WM_PAINT 转为窗口 damage/refresh 通知。这里确认的是宿主缺少回调期间绘制路径，未把隐藏窗口的程序化事件测试宣称为鼠标拖拽全过程截图复现。

## 独立隐藏窗口验证

使用真实 OpenTkHost、真实 GL 上下文与默认文本服务。在首帧完成后投递一个 UI 调度动作，确保不在 DrawFrame 内重入，然后扩大 NativeWindow 客户区并显式触发 NativeWindow.OnRefresh，观察回调返回后的状态：

```text
before: frames=1,windowFb=(450, 300),rendererFb=450×300,needsDraw=False
afterResizeAndRefresh: frames=1,windowFb=(690, 460),rendererFb=450×300,logical=460×306.667,needsDraw=True
```

本机 DPI 1.5。窗口 framebuffer 已变为 690×460，运行时逻辑尺寸也更新了，但渲染器仍停留在 450×300，帧数仍为 1。这个结果独立确认“收到尺寸/刷新通知不等于已绘制新区域”。测试随后请求退出，没有启动普通循环的下一次绘制。

复现位于本地忽略目录 `artifacts/resize-review/Verify.csproj`、`Program.cs`，输出为 `result.txt`。没有操作用户当前窗口或更改系统设置。

## 修复要求

1. 在窗口刷新/交互调整大小期间增加主线程即时绘制路径，刷新实际 framebuffer、逻辑视口与 DPI，处理必要布局后完整绘制并交换缓冲。保持空闲时按需重绘，不依赖持续忙循环或修改背景色来掩盖问题。
2. 该路径需防止构造/启动未就绪、最小化/零尺寸、正在绘制、嵌套尺寸事件以及退出/销毁时重入；保留后续重绘请求，不能递归绘制或从后台线程直接操作 GL。避免重入式排空整个事件循环或任意执行全部队列动作。
3. 核对 NativeWindow 缓存尺寸的回调时序，必要时查询当前 GLFW framebuffer，不能在新 framebuffer 上继续提交旧尺寸的 frame。GlRenderer 当前在清屏前启用并设置 scissor，清屏也受它限制；需确保清屏区域对应实际新 framebuffer，不能仅更新 GL.Viewport 后仍用旧 scissor 清屏。
4. 覆盖放大/缩小、两轴连续拖拽、150% DPI、恢复窗口、关闭流程以及嵌套裁剪；验证新增区域的实际背景像素与新视口布局，不只断言 NeedsDraw。
5. 增加回调返回前已提交正确尺寸帧的回归及重入/生命周期保护验证；鼠标拖拽过程须单独验证并区分程序化 resize 与真实原生模态循环。三个示例 Debug/Release 更新，四套完整测试及启动检查通过后英文提交，由 Codex 复审。

## Codex 实现期间独立原生循环基线（2026-10-16）

新增本地忽略目录 `artifacts/resize-native-review/` 独立探针：创建自动关闭的专用窗口，通过发送到本进程自有 HWND 的 SC_SIZE 命令进入 Windows 默认窗口过程的原生 sizing modal loop；用原生窗口子类回调确认收到 WM_ENTERSIZEMOVE/WM_EXITSIZEMOVE。后台仅向该测试 HWND 请求程序化尺寸变化和重绘，全部 OpenGL 操作仍在主线程 FrameRendered 回调内。不是人工鼠标拖拽，但确实在原生模态循环未退出时验证提交，不是退出循环后再改尺寸。

修复前结果（`before.txt`）：`active=1,enters=1,framesAtEnter=1,framesNow=1,modalFrames=0,client=610x420`，退出时 `enters=1,exits=1`。原始客户区 450×300、DPI1.5，确认循环期间尺寸已增大却没有新帧。默认 framebuffer 前缓冲可读，后续将检查扩出右侧/底部所有背景像素、当前实际 GLFW framebuffer 与 renderer 尺寸，以及循环内扩出尺寸帧是否真的提交。

## Codex 首版实现复查（2026-10-16）

即时回调绘制的方向正确；当前首版实现还需检查两处保护：

1. `_frameInProgress` 只包围 DrawFrame，`_immediateRedrawActive` 只包围即时路径。普通 Run 第 5 步直接调用 `_runtime.ProcessPendingWork()` 时二者均为 false；组件 Build/布局或绑定读取若间接引发窗口 Resize/Refresh，仍可能从回调再次进入同一 runtime 的 ProcessPendingWork/BuildFrame，绘制尚未协调完成的树。请保护普通重建/布局阶段，或以 runtime 本身的处理状态统一阻止重入，保留其后的重绘请求；新增一个 Build 内引发 Resize/Refresh 的回归，验证 FrameRendered 不会在 Build 尚未返回时发生。已有 FrameRendered 内的重入保护不能替代此场景。
2. Run 结尾普通语句将 `_immediateRedrawEnabled=false`，若正常路径中 Started/FrameRendered/ProcessPendingWork 抛出异常则会跳过该语句。建议以 finally 明确关闭即时路径，确保 Run 已异常退出但 host 尚未 Dispose 的间隙也不能再因窗口通知提交帧。

### 首版独立复现结果：黑边路径通过，两个保护缺口已实测

编译首版实现后，Codex 用三个独立程序检查（均位于忽略的 artifacts 目录）：

- `resize-review/initial-fix.txt`：resize+refresh 返回后帧数由 1 变 3，renderer 与实际 framebuffer 均为 690×460，NeedsDraw=False，主问题路径通过。
- `resize-native-review/initial-fix.txt`：记录到一对原生 enter/exit；循环期间提交 3 帧，其中 2 帧包含扩展区域；实际 GLFW framebuffer 与 renderer 尺寸始终一致，读取的 26100 个扩展区域背景像素无错误。该次循环在驱动后续尺寸变化前已退出，不能宣称八次尺寸变化都发生在模态循环内；已确认的两帧扩展绘制确实在循环未退出时完成。
- `resize-guard-review/initial-fix.txt`：`Rebuild resize guard: PASS=False,framesDuringBuild=2,frames=4`。首帧之后投递状态更新，在下一次 Component.Build 内更改窗口尺寸并触发 OnRefresh，FrameRendered 被调用两次，此时 Build 深度仍为 1。应先完成普通重建/布局，再处理回调重绘，不得在重建期间再次进入 runtime。
- 同一 guard 程序：`Run failure lifecycle guard: PASS=False,exceptionObserved=True,before=1,afterRefresh=2`。让调度动作抛出受控异常，Run 抛出并被测试捕获后再次触发 Refresh，仍提交了第 2 帧；需要用 finally 关闭即时重绘许可。

这些是当前首版尚未通过的真实独立检查，不是历史原实现的失败。DSH 请据此修正并添加持久回归，再执行完整验证后提交。Codex 后续会用相同 guard 程序检查。

### 新增测试的确定性与平台范围

复查 `WindowResizeRedrawTests` 首版还需修正测试环境假设：

- 清屏测试共享 Graphics fixture，但直接假定 FBO 仍为 256×256。其他图形测试可通过 Surface.Render 改变尺寸；请先 `scope.Surface.EnsureSize(256,256)` 再清底色，避免依赖用例执行顺序，确保读取 (200,200)/(255,0) 有效。
- 最小化测试在 Started 中启动驱动线程，可能在首帧之前就最小化，却最终要求 frames>=1；请从首帧 FrameRendered 后启动一次驱动，消除调度竞态。
- 项目的 Linux 图形验证说明使用裸 xvfb-run，没有承诺窗口管理器。原生最小化是 WM 功能，不能把 NativeWindow.WindowState=Minimized 当作所有平台/环境必然生效。请明确这项原生验证的平台/WM前提，其他环境保留状态守卫的可检查覆盖，不能导致未安装 WM 的 Linux 图形测试无条件失败或伪称原生最小化已验证。

Codex 另外以全新 GlOffscreenSurface/GlRenderer 独立验证清屏（`artifacts/resize-guard-review/Program.cs` 的 `clear` 模式）：先建立 640×160 表面，再 EnsureSize(256,256)、清红底、绑定 FBO 后提交 32×32 白色空帧，读取 (31,31)、(200,200)、(255,0) 均为白色，GL.NoError。确认当前生产 GlRenderer 的完整 FBO 清屏在隔离上下文中成立；若共享 fixture 用例仍受其他图形用例状态影响，可改用该用例独立拥有的表面/纹理服务/renderer，明确初始化尺寸与目标 FBO，不必继续扩大生产渲染策略来掩盖测试状态污染。首版 DLL 的 IL 也已核对确实在 GL.Clear 之前 Disable(ScissorTest)，不是旧产物。

### 两处保护缺口的独立返修验证

当前补充重建阶段守卫及 Run finally 后，Codex 重新编译同一 `resize-guard-review` 探针，`after.txt` 两项均通过：`framesDuringBuild=0,frames=2`；异常由测试正常捕获，刷新前后帧数均为 1。简单回调探针 `resize-review/after.txt` 也继续通过，刷新返回时 renderer 与真实 framebuffer 均为 690×460、NeedsDraw=False。DSH 正在做临时回退的反向验证，完成后必须恢复最终源码并重建测试和所有示例，不能让旧实现 DLL 混入最终交付；最终仍以全套测试、示例哈希及提交复核为准。

## DeepSeek Harness 修复与验证（2026-10-16）

### 生产实现

- `src/CSharp.UI.OpenTK/Hosting/OpenTkHost.cs`
  - `Resize`/`FramebufferResize`/`Refresh` 回调改为“刷新视口 → 处理必要重建/布局 → 按需绘制并交换缓冲”，不再只置 `NeedsDraw` 等待外层循环；仍然只在 UI 线程操作 GL，不排空调度队列、不递归泵事件。
  - `ReadActualWindowSizes` 直接查询 `glfwGetWindowSize`/`glfwGetFramebufferSize`，`UpdateViewport`/`ApplyLogicalClientSize` 均改用它，避免在回调内沿用 OpenTK 尚未更新的缓存尺寸（查询失败回退缓存值）。
  - 重入保护：`_immediateRedrawActive`（即时路径内只记录 `_immediateRedrawPending`，本轮补画，最多 4 遍）、`_frameInProgress`（覆盖 `FrameRendered` 用户回调）、`_processingWork`（覆盖普通循环与即时路径的 `ProcessPendingWork` 阶段）。未启动、最小化/零尺寸、退出、已销毁时只保留 `NeedsDraw`。
  - `Run` 用 `finally` 关闭即时重绘许可；窗口事件统一走 `RunWindowEventSafely`，异常记录诊断并请求退出码 1，不穿过 native 回调边界。
- `src/CSharp.UI.OpenTK/Rendering/GlRenderer.cs`
  - 提交帧时先 `Disable(ScissorTest)` 清空整个 framebuffer，再启用 scissor 按当前裁剪绘制命令；避免沿用旧 scissor 或舍入残差导致新扩出区域未清屏。

### 针对 Codex review 的返修

1. 重建阶段重入：新增 `_processingWork`，普通循环第 5 步与即时路径都经 `ProcessPendingWork()` 包装。窗口事件在 Build/布局中途只保留重绘，普通循环随后补画。
2. Run 异常生命周期：`_immediateRedrawEnabled` 改在 `finally` 关闭，Run 因 Started/FrameRendered/ProcessPendingWork 抛异常退出但宿主未 Dispose 的间隙也不能提交帧。
3. 测试确定性：清屏回归改用该用例独立创建、显式 256×256 的 `GlOffscreenSurface`+`GlTextureService`+`GlRenderer`（不再依赖共享 fixture 的尺寸/状态）；最小化回归改为首帧 `FrameRendered` 后启动一次驱动，消除“最小化早于首帧”竞态，并在 `context.IsMinimized` 未生效（无窗口管理器）时 `Assert.Skip`，不伪称原生最小化已验证。

### 新增回归（`tests/CSharp.UI.OpenTK.Tests/WindowResizeRedrawTests.cs`，9 例）

- 尺寸与刷新回调返回前按真实 framebuffer 提交新帧：放大后 resize 与 refresh 各提交一帧、renderer/`context.FramebufferSize`/GLFW 真实 framebuffer 三者一致、`NeedsDraw=False`、右下新区域为清屏色、左上仍为内容色。
- 连续放大缩小每次回调都按新尺寸绘制（含嵌套 `Clip`），逐步骤校验 renderer 尺寸、帧数与右下实际像素。
- `FrameRendered` 回调内触发尺寸事件不递归且后续重绘不丢失。
- 已请求退出后刷新回调不再提交帧。
- 启动前（构造完成、未进入 Run）窗口事件只保留重绘不提交帧。
- 最小化期间刷新回调不提交帧，恢复后重绘。
- 清屏不受旧裁剪限制覆盖整个 framebuffer。
- Build 内触发的尺寸事件不会在重建返回前绘制（`FramesRenderedDuringBuild == 0`）。
- Run 异常退出后窗口事件不再提交帧。

反向验证：临时回退上述两处保护后，仅运行最后两例，实测 2 failed；恢复修复后全部通过。

### 验证结果

- 四套测试顺序执行（`dotnet run --no-build`，各工程先 `dotnet build --no-restore -m:1 -nodeReuse:false`）：`CSharp.UI.Tests` 172、`CSharp.UI.Text.Tests` 67、`CSharp.UI.Integration.Tests` 14、`CSharp.UI.OpenTK.Tests` 188（含新增 9 例），合计 441，Failed/Skipped 均为 0，退出码 0。
- 三个示例 Debug/Release 共 6 次 `dotnet build --no-restore -m:1 -nodeReuse:false` 全部退出码 0；Release `CSHARPUI_SMOKE_FRAMES=5` 各渲染 5 帧后退出码 0（Counter 480×320 / framebuffer 720×480；TodoList 760×480 / 1140×720；ControlGallery 900×700 / 1350×1050；DPI 1.5）。
- 关键结果写入 `artifacts/resize-review/progress.txt`；Codex 原有 `Program.cs`/`result.txt` 保留未改。

### 本轮未验证的边界

- 未做人工鼠标拖拽边框验证：本机为程序化 resize + `NativeWindow.OnRefresh`，仅覆盖宿主回调路径；真实 Windows 模态循环由 Codex 的独立 SC_SIZE 探针覆盖，本轮未复跑。
- 无窗口管理器环境（例如裸 `xvfb-run`）上的原生最小化/恢复不验证，测试动态 Skip；非 Windows 平台本轮未运行。
- 未在“模态循环进行中跨 DPI 显示器移动窗口”下验证；DPI 变化路径沿用既有 `SimulateContentScaleChange` 覆盖。
- `artifacts/resize-review/opentk-tests.txt` 等临时输出保留在忽略目录，不影响交付。
