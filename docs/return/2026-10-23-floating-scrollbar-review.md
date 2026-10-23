# Floating 滚动条 — 实施与审查记录

日期：2026-10-23
关联：[实施计划](../plan/2026-10-23-floating-scrollbar-plan.md)、[任务索引](../task/README.md)（FS-01~FS-07）、前置共享滚动条交付 [2026-10-23-scrollbar-delivery.md](../delivery/2026-10-23-scrollbar-delivery.md)。

本文只记录本轮事实；不复述 SB 组旧结论。状态：**FS-01~FS-07 全部完成并主审验收通过**——FS-01~FS-04 核心经五轮主审返修、source-freeze 后仅删 2 个确认死入口并重建验证；FS-05/06 实现已验收；FS-07 最终 runs 与 PNG/Gallery 视觉证据全过。未声称全平台真机/真实 IME 本轮已通过——本轮未跑真实 IME；fast 之后若需 IMM 验收另行计划。

## 实现概览

- 公开 API：`ScrollbarStyle` 枚举（`Standard` 默认 persistent 12px 槽 / `Floating` overlay 不占槽）；`ScrollView.ScrollbarStyle`、`TextEditor.ScrollbarStyle`（init 属性）。无公开滚动命令、无动画/惯性/触摸系统。
- 时钟：`UiRuntime` 节点级 TimedWakes（值类型单槽位），复用 `GetNextWakeup`/`OnClockTick` + `IUiClock`，无 `System.Threading.Timer`、无每帧轮询、无 `DateTime`。
- 命中：`View.HitTestOverlay(node)`（internal virtual，默认 null）；`InputRouter.HitTest` 在 children 之前、祖先 clip/非禁用/frame 内 guard 下让可见 overlay strip 优先——除此之外维持原 child-first 次序，不硬编码 ScrollView 类型。
- 共享：`ScrollbarMetrics`/`ScrollbarState`/`ScrollbarPainter` 同一套；两 owner 不复制状态机。

## 主审发现与修复（按轮）

### R1 — 时钟与死分支
- `ScrollbarState.OnTimedWakeDue` 外/内两层 guard 使内层永 false（死分支）：改为单 guard——`deadline is not { } || now < deadline || suppressed || press != none` 即 return，真正到期才清 deadline/置隐藏/invalidate。无 stale-due 救回分支。
- `DispatchDueTimedWakes` 补 `try/finally`（回调抛异常时 `_timedWakeDispatching` 与压缩仍恢复）；`NextTimedWakeDelay` 在 due 批次回调期对失效槽只 mark 不 `RemoveAt`（防逆序遍历跳项）。
- focused blink 与 future deadline 拆分：future TimedWake 不产生每 tick 重绘；到期由 `OnTimedWake` 自行 `InvalidateRender`。

### R2 — 布局等价与 hover 时序
- `ScrollView.LayoutPropertiesEquivalent` 只比 `Axis` 漏 `ScrollbarStyle` → 同节点 immutable 换 style 不重测 gutter。修：SV 增 `ScrollbarStyle`；`TextEditor` 新增 override 覆盖 `ScrollbarStyle`/`HorizontalPadding`/`VerticalPadding`/`Width`/`Height`/`MinLines`。
- `OnPointerEnter` 在 `_hovered`/`_hoverChain` commit 之前运行 → `PointerOverFloatingBar` 的 `HoveredNode` 检查是旧的。修：router 在 commit 后通知 deepest。
- `OnHoverSuppressionLost` 无 `PressKind` guard → drag captured 离 track 也注册 deadline（due 被 PressKind 挡→消费空转）。修：加 `PressKind == None` guard。

### R3 — 权威指针位置
- `OnHoverSuppressionGained(node)` 无 position 参数，owner 查自己缓存的 `LastPointerPoint`：child 消费 Move（返回 true 不冒泡）后 wheel@barPoint，owner 缓存停在旧内容点 → gained 判 `track.Contains(stale)` false → 不抑制。修：合并两 hook 为 `OnHoverDeepestChanged(node, Point position, bool isDeepest)`，position 为 router 权威 `e.Position`（私有 `_lastPointerEvent`）。
- 几何边界重判改用 router 位置（不再读 owner 缓存）。

### R4 — 几何签名 + 延迟 flush
- `SyncGeometry` 在 `PressKind == None` 时提前返回 → 未按下只是 hover 的 track/range 移位永不重判（永久 stuck suppression）。修：`ScrollbarState.LastGeometry` 持久签名每次 Arrange 比对；返回 `ScrollbarSyncResult`（Unchanged / GeometryChanged / GestureCancelled）。
- 旧代码每次无 overflow 的 Arrange 都 `ResetFloating`（非翻转）。修：仅 `visibleBefore && !stillShows` 真翻转时 Reset。
- `ReevaluateFloatingHoverAfterGeometry` 在 Arrange 内直接 `Runtime.RefreshHover()` → HitTest 读半成品子 frame。修：删除该 helper（两 owner 各 ~30 行重复）；改 `Runtime.QueueHoverRefresh()` 仅标记，`ProcessPendingWork` 在全部 `RunLayout` + `ValidateInteractionState` 后统一 flush 一次。
- onBar 判定不再 `track.Contains` 矩形——抑制走 `OnHoverDeepestChanged` 的 `isDeepest`（router clip-aware HitTest 已含祖先 clip）。

### R5 — 同 target 强制通知 + 权威 bool
- `UpdateHover` 早退（`target == _hovered`）：TextEditor content 与 bar 同属一个 owner，track 移离 pointer 后 target 不变 → deepest 通知永不发 → suppressed 永久 stuck。修：`RefreshHover` 用 `forceDeepestNotify=true`，同 target 也按真实位置通知；普通 Move 不付此成本。
- owner hook 统一 `over ? suppress : (suppressed && PressKind==None → rearm)`；`over = isDeepest && IsFloatingVisible && track.Contains(position)`。
- `ClearHover` 在 `_hovered` 清空前通知 `isDeepest=false`：旧 `HoveredNode != node` 重查会看到仍 deepest → 跳过 rearm（窗口失焦 stuck）。修：`isDeepest` 为权威身份，删除重查。

## 测试（消费者可观察，非源码/flag 断言）

新增 22 个 `FloatingScrollbarTests`（`tests/CSharp.UI.Tests/`）：显隐转换、~1s 隐藏、hover/drag 续命+重挂、hidden 穿透+visible owner-hit+track 翻页、无 overflow 不显、disabled/unmount/style 切换清理、`GetNextWakeup` deadline 反映+隐后清除、future deadline 无 busy-loop、focused blink 合并、同节点 style 重测、child 消费 Move 后 wheel@bar 静止悬停、hover-only resize 移离/移入 track、祖先 clip 裁出 bar、TextEditor 同 owner target 边缘。

## 验证（本轮实际执行，原始输出见 [evidence](2026-10-23-floating-scrollbar-review-evidence/)；最终 suite/sample/GL/Gallery 证据见 [delivery](../delivery/2026-10-23-floating-scrollbar-delivery.md)）

- `dotnet build CSharp.UI.slnx`：Debug / Release 各 exit 0（0 警告 0 错误）；死入口删除后 postdelete 重建同结果（`postdelete-build-*.txt`）。
- 四套件：**post-cleanup 最终源每配置 1456 全绿**（640/85/53/678，Debug+Release 均 exit 0）——最终数字以 [delivery manifest](../delivery/2026-10-23-floating-scrollbar-delivery.md)（20 步）为准。
  - 中间态曾记录 632/635/636/639 等计数——为不同 source 中间版本，不是最终一次统一全通过；本 evidence 目录内旧 log 属 mixed cycle 历史（如 `CSharp.UI.Tests-*.txt` 为某轮 640、`postdelete-*` 为死代码删除后 640/85/53），勿按统一最终批次解读。
- 三 sample 真实窗口 smoke：Counter / TodoList / ControlGallery 各 exit 0；post-cleanup 最终 6 smoke 全 exit 0（delivery manifest）。
- raw logs：`~/.omp/tmp/floating-impl-logs/` → 复制至 [evidence](2026-10-23-floating-scrollbar-review-evidence/)（stdout tail 截取非全程；`.gitattributes` `-text`）；`postdelete-*` 8 个为清理后本轮重建原始输出。说明：早前轮同名日志已被覆写，仅存最新一轮实际输出，不代表各中间 cycle 独立版本。[SHA256SUMS.txt](2026-10-23-floating-scrollbar-review-evidence/SHA256SUMS.txt) 覆盖全部现有文件（exclude 自身）。

## fast 独立验收反馈与处理

- fast snapshot 期报告 C2 / S8 真实失败——已在本轮修复链内（hover/deadline/几何边界）；最终 DLL 由 fast 复测。
- fast S4 报告为 fixture 误判：`release 后 pointer 仍 hover bar` 时正确保持可见是设计语义，非 product bug（release→悬停抑制）。
- fast 报的 clipped-point nested-wheel 用例为 fixture 裁剪点构造问题，不按 product bug 处理。
- `RequiresCaretReveal` 核心逻辑本轮未改；Floating 不占槽带来的新宽度下 IME 组合几何无 flap 由 fast 以 fake composition + 窗口运行验证——本轮未跑真实 IME。
- **dead weight 已清理（已批准）**：`UiRuntime.TryGetPointerPosition` 与 `InputRouter.LastPointerEvent` accessor 在 R5 后无任何调用方（原消费者 `ReevaluateFloatingHoverAfterGeometry` 已删）。仅删这两个死入口——私有 `_lastPointerEvent` 字段保留（`RefreshHover`/`ClearHover` 在用）。删除后 slnx Debug/Release 各 0 警告 0 错误，CSharp.UI.Tests 640 / Text.Tests 85 / Integration 53 双配置 exit 0（raw log 见 evidence `postdelete-*`）；OpenTK 套件由 fast 统一复验。

## 已明确不做

无 fade/惯性/触摸/新动画系统；无公开滚动命令；globalAccent/Slider/TextField 不改；未在 macOS/Linux 构建或真机运行（保留既有平台回退边界）；真实 IME 验收不在本轮。
