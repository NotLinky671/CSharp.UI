# Floating 滚动条交付记录

日期：2026-10-23（计划日；最终验收 2026-10-24，证据见 [2026-10-23-floating-scrollbar-delivery-evidence](2026-10-23-floating-scrollbar-delivery-evidence/)）
状态：**FS-01~FS-07 全部完成并主审验收通过。**

设计与任务：[实施计划](../plan/2026-10-23-floating-scrollbar-plan.md)、[任务索引](../archive/2026-10-25-floating-scrollbar-tasks/README.md)（FS-01~FS-07，已归档）、[审查记录](../return/2026-10-23-floating-scrollbar-review.md)。前置共享滚动条交付 [2026-10-23-scrollbar-delivery.md](2026-10-23-scrollbar-delivery.md)（SB-01~SB-07 已归档）。

## 范围与公开 API

为单轴 `ScrollView`（纵/横）与 wrap-only `TextEditor`（纵）增加第二种滚动条样式：

- **`ScrollbarStyle` 枚举**（`src/CSharp.UI/Views/View.cs`）：`Standard`（默认，保持既有 persistent 12 逻辑像素 gutter 槽，无溢出留空槽）与 `Floating`。
- **`ScrollView.ScrollbarStyle` / `TextEditor.ScrollbarStyle`**（init 属性，默认 `Standard`）。
- **Floating 行为**：覆盖内容边缘不占布局槽、不因显隐改内容宽；内容溢出且发生**实际滚动**时显示，停止约 **1000ms**（固定内部值）后隐藏；hover 可见 bar 或拖拽期间保持可见，结束后合理重新计时；更细中性灰 thumb（`ControlBackground`→`Foreground` 混色，不继承 Accent），平时不画 track；thumb 可拖、track 点击按视口翻页一次（无自动重复）；无 overflow 不显示；hidden 完全不拦底层命中；visible bar 优先于子内容命中且尊重祖先 clip；切 style/axis/resize/content-range/disable/unmount 取消 gesture 并 clamp offset、隐藏计时状态自然 cleanup。
- **不实现**：淡入淡出、惯性、触摸、新动画系统、横向 TextEditor 滚动条、双轴 ScrollView、公开滚动命令、minimap。`TextField`、`Slider`、global Accent、`RequiresCaretReveal` 核心逻辑不改。

## 架构要点（主审冻结）

- **时钟**：`UiRuntime` 节点级 `TimedWakes`（值类型单槽位），复用 `GetNextWakeup`/`OnClockTick` + `IUiClock`；hidden/idle 无多余唤醒、无 busy-loop、无 `System.Threading.Timer`/`DateTime`/每帧轮询。到期经 `View.OnTimedWake` 回调一次，future deadline 不产生每 tick 重绘。
- **命中**：`View.HitTestOverlay(node)`（internal virtual，默认 null）+ `InputRouter.HitTest` 在 children 之前、祖先 clip/非禁用/frame 内 guard 下让**可见 overlay strip** 优先；除此之外维持原 child-first 次序，不硬编码 ScrollView 类型。
- **deepest 通知**：`View.OnHoverDeepestChanged(node, Point position, bool isDeepest)`——`position` 为 router 权威 `e.Position`（私有 `_lastPointerEvent` 字段）；`isDeepest` 为权威身份。`RefreshHover` 用 `forceDeepestNotify` 让"content 与 bar 同属一个 owner deepest"（TextEditor）在同 target 下也按真实位置重估。
- **几何边界**：`ScrollbarState.LastGeometry` 持久签名每次 Arrange 比对（axis/track/content/viewport，offset 不入）；`SyncGeometry`→`ScrollbarSyncResult`（Unchanged/GeometryChanged/GestureCancelled）。`QueueHoverRefresh` 仅标记，`ProcessPendingWork` 在全部布局+交互校验后的安全阶段统一 flush——不在 Arrange 里对半成品 frame 做 HitTest。

## 验证（证据原始输出 + sha256 见 manifest.json 与 SHA256SUMS.txt）

### 构建（fresh，`--no-incremental -m:1 -nodeReuse:false`）
| 配置 | exit | 警告 | 错误 |
|---|---|---|---|
| Debug | 0 | 0 | 0 |
| Release | 0 | 0 | 0 |

### 四套件（`dotnet run --no-build`，xUnit 进程内）——每配置 **1456** 全绿
| 套件 | Debug | Release |
|---|---|---|
| CSharp.UI.Tests | 640/640 | 640/640 |
| CSharp.UI.Text.Tests | 85/85 | 85/85 |
| CSharp.UI.Integration.Tests | 53/53 | 53/53 |
| CSharp.UI.OpenTK.Tests | 678/678 | 678/678 |

### 独立消费者验收（fast，非测试源码断言）
- **CPU 14/14**（[logs/fs-smoke-cpu-14of14.txt](2026-10-23-floating-scrollbar-delivery-evidence/logs/fs-smoke-cpu-14of14.txt)、[cpu-final.txt](2026-10-23-floating-scrollbar-delivery-evidence/logs/cpu-final.txt)）：hidden Button 透传 / visible owner-hit、hover suppress + leave rearm、drag keep-alive + release-on-bar suppress + off-track release hides、boundary no-show + track-page、disabled 无 thumb 无 wakeup、unmount 清 deadline、**同节点 style 切换 188→200**（Standard gutter→Floating 无槽重测）、嵌套 overlay 按绘制序命中、S10 IME fake geometry 无 republish、keyboard ensure 显示 + passive clamp 不显示、横向 H、C2 静止 pointer 抑制、C3 bar→消费子重挂。
- **真实 UiApplication host**（[logs/fs-smoke-host-idle.txt](2026-10-23-floating-scrollbar-delivery-evidence/logs/fs-smoke-host-idle.txt)）：`frames=3`，bar 在 **647ms** 显示、**1652ms** 自动隐藏（真实窗口时钟 ~1s 语义）。
- **GL 离屏像素 6/6**（[logs/fs-smoke-gl-final-6of6.txt](2026-10-23-floating-scrollbar-delivery-evidence/logs/fs-smoke-gl-final-6of6.txt)，PNG 见 [visual/final-gl-*](2026-10-23-floating-scrollbar-delivery-evidence/visual/)）：light/dark × pixelScale 1.0/1.5/2.0，正确主题进入实际渲染后——light thumb `#ffa4a5a6`/bg `#fff5f6f8`、dark thumb `#ff72757b`/bg `#ff15171c`，各 scale 精确 ±10；show→hide PNG 12 张。注：多 scale 为离屏渲染，非实机 DPI；早前 `fs-smoke-gl-6of6-theme-notapplied.log` 为 fast harness 未 `SetEnvironment` 的 driver 问题（非产品），已被本 final 替代。

### Gallery 模式切换（离屏真实示例渲染 + `UiRuntime.Dispatch*` 事件注入，非 OS 真窗）
[logs/fs-smoke-gallery-mode.txt](2026-10-23-floating-scrollbar-delivery-evidence/logs/fs-smoke-gallery-mode.txt)：w320/w1024：Standard→Floating 同节点 frame 118.8→106.8、gap 12→0、child 宽 106.75 不变（overlay 不占槽）、wheel 4px 真滚动 → bar 显示 → 1 tick 隐藏；切回 Standard gap 恢复 12。PNG 6 张（[visual/gallery-*](2026-10-23-floating-scrollbar-delivery-evidence/visual/)），w320 画面经主审认可。

### 示例（3 sample × 2 配置 fresh build + `CSHARPUI_SMOKE_FRAMES=5` 真实窗口）
全部 exit 0：Counter（480×320, fb 720×480, DPI 1.5）、TodoList（760×480, fb 1140×720）、ControlGallery（900×700, fb 1350×1050）；GL 3.3.0 Intel。另 `gallery-smoke-30f.txt` 30 帧 exit 0。

### manifest
[manifest.json](2026-10-23-floating-scrollbar-delivery-evidence/manifest.json)：**最终 20 步全部 exit 0**（8 测试 run + 6 sample build + 6 sample smoke，均 post-cleanup 最终源上重跑），每步 stdout/stderr log + sha256。说明：清理前原 22 步 manifest 被覆盖；最终 build 以 postdelete 对应 raw 为准（存于 [review-evidence](../return/2026-10-23-floating-scrollbar-review-evidence/) `postdelete-build-*.txt`、`postdelete-*-{Debug,Release}.txt`），不依赖原 run 还原。

### 源快照
[SOURCE-SHA256.json](2026-10-23-floating-scrollbar-delivery-evidence/SOURCE-SHA256.json)：post-cleanup 最终源 229 文件与磁盘一致（0 mismatches / 0 drift），对应本 manifest + final-gl visuals + 全部最终执行套件。[SOURCE-SHA256-pre-cleanup.json](2026-10-23-floating-scrollbar-delivery-evidence/SOURCE-SHA256-pre-cleanup.json)：清理前历史快照保留。注：`visual/gallery-*` PNG 截于清理前同功能版——删除仅两个 unused 方法、无二进制行为差异；最终二进制上 Gallery PASS 已由 fast 在新 source 上复跑确认（`logs/fs-smoke-gallery-mode.txt`）。

## 边界与声明

- **输入注入级别**：验证经 `Runtime.DispatchWheel`/`DispatchPointer` 事件注入（runtime 层），非 OS SendInput/HID 级输入。
- **真实 IME**：本轮未实机复验——S10 为 `FakeTextInputService` CPU 几何稳定验证（hide 不触发 republish、content clip 稳定）；旧 SB 组 IMM 24/24 仅为前置历史，不宣称 Floating 原生 IME 通过。
- **GL**：多 scale 为离屏渲染，非实机 DPI 变化。
- **平台**：Windows 实机/CPU + 离屏 GL；未在 macOS/Linux 构建或真机运行，保留既有平台回退边界。
- **dead code 清理**：`UiRuntime.TryGetPointerPosition`/`InputRouter.LastPointerEvent`（R5 后无调用方）已删——清理后 slnx 双配置 0 警告 0 错误、headless 三套件双配置 exit 0（log 见 [review-evidence](../return/2026-10-23-floating-scrollbar-review-evidence/) `postdelete-*`）。
