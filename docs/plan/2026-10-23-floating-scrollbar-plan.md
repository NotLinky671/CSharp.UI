# Floating 滚动条实施计划

计划日期：2026-10-23

状态：**FS-01~FS-07 全部完成并主审验收通过**（最终验收 2026-10-24）。交付记录 [docs/delivery/2026-10-23-floating-scrollbar-delivery.md](../delivery/2026-10-23-floating-scrollbar-delivery.md)；审查记录 [2026-10-23-floating-scrollbar-review.md](../return/2026-10-23-floating-scrollbar-review.md)。

关联：[共享滚动条计划](2026-10-23-scrollbar-plan.md)（SB-01~SB-07 已完成，任务归档 [docs/archive/2026-10-23-scrollbar-tasks/](../archive/2026-10-23-scrollbar-tasks/README.md)）、[滚动回跳修复计划](2026-10-23-text-editor-scroll-bounce-fix.md)。

## 目标与范围

`ScrollView`（纵/横）与 `TextEditor`（纵）新增可公开 init 的 `ScrollbarStyle`，默认保持现有 persistent 12 逻辑像素 gutter 样式；`Floating` 时滚动条覆盖在内容边缘上、不占布局槽、显隐不改变内容宽。内容溢出且发生**实际滚动**时显示滚动条，停止约 1 秒后隐藏；hover 可见 bar 或拖拽期间保持可见，结束后重新计时。Floating 下不画 track，thumb 为更细的中性灰；可见时支持 thumb 拖拽与 track 按视口翻页一次（复用既有 ±V）；无 overflow 永不显示；hidden 时完全不拦底层内容。

**不实现**：淡入淡出/透明度动画、惯性、触摸、新动画系统、公开滚动命令/滚动 API、横向 TextEditor 滚动条、`globalAccent`/Slider 变更、第二公开可见性枚举（如 `ScrollbarVisibility`）。

## 冻结决策

### 决策一：公开 API 面（唯一新增）

```csharp
public enum ScrollbarStyle
{
    Standard,   // 现行为：persistent 12px gutter，overflow 恒显，track+thumb
    Floating,   // overlay：不占槽、实际滚动触发显示、约 1s 后隐藏
}
```

- `ScrollView.ScrollbarStyle { get; init; }`（默认 `Standard`）；`TextEditor.ScrollbarStyle { get; init; }`（默认 `Standard`）。
- `TextEditor.With(Binding<string>)` 复制 `ScrollbarStyle`（与既有 init 属性一致）。
- 不引入第三值/可见性枚举；`Standard` 即“现项目命名更合适”的槽位样式名（与计划/文档中 persistent 语义对应，避免 `Persistent` 与 gutter 实现细节耦合）。
- 隐藏延时常量：内部 `ScrollbarMetrics.FloatingHideDelay = TimeSpan.FromMilliseconds(1000)`，不公开、不可配置。

### 决策二：Floating 几何与布局

- `Standard`：现有 `gutter = min(12, crossExtent)` 公式不变（ScrollView `ContentRect`/`TrackRect`/Measure，TextEditor `InnerRect`/`TrackRect`/Measure 宽度项）。
- `Floating`：gutter = 0。
  - ScrollView `Measure`/`ContentRect`/`TrackRect` 按 style 取 gutter：内容充满整个 frame cross 向；track 矩形 = frame 边沿内缩 `Thickness` 的窄条（纵滚右侧 `Rect(frame.Right - g, frame.Y, g, frame.Height)`，横滚底部同构，`g=min(Thickness, crossExtent)`）。
  - TextEditor `InnerRect`/`TrackRect`：inner = `frame − padding`（不再扣 gutter）；track = `padded` 右缘内 `g` 宽条。Measure 的 `+ min(Thickness, …)` 宽度项按 style 为 0。
  - `GetChildClip` 不区分 style：仍返回内容矩形（Floating 下即全 frame/padded）。**不改** clip 扣 bar 窄条——显隐不得改变内容 clip 与 IME 可见几何（主审已否决该方向）。
- `ShowScrollbar`/thumb/track 命中/拖拽/翻页公式全部复用 `ScrollbarMetrics`，不改公式本身；Floating 的“可见”在 `ShowScrollbar` 之上再叠加 `ScrollbarState.IsFloatingVisible`（见决策五）。

### 决策三：owner-overlay 命中钩子（internal virtual，进 `View`）

新增 internal virtual（命名按 `HitTestSelf`/`GetChildClip` 同族）：

```csharp
/// <summary>
/// 覆盖在子内容之上绘制的 owner 命中区（Floating scrollbar 等）：
/// 返回非空矩形表示该绝对区域在子节点之前优先归属本节点；
/// 仅在 truly 可交互（可见）时返回，hidden/无 overflow 返回 null 完全透传。
/// </summary>
internal virtual Rect? HitTestOverlay(ViewNode node) => null;
```

`InputRouter.HitTest` 递归体内、children 逆序遍历**之前**插入（guard 显式完整，不依赖 owner 自觉隐藏）：

```csharp
if (insideAncestorClip                        // 祖先 clip 已含 viewport∩各级 childClip
    && !node.IsEffectivelyDisabled            // 通用 hook 语义：禁用节点 overlay 永不命中
    && node.Frame.Contains(position)          // overlay 是 owner 自绘区，不越出 frame
    && node.View.HitTestOverlay(node) is { } overlay
    && overlay.Contains(position))
{
    return node;                              // owner 命中 → 走既有 OnPointer* bar 分支
}
```

- 位置在 `insideEffectiveClip` 的 children 循环之前；`IsHitTestVisible` 已在方法头部过滤。三个 guard（祖先 clip / 非 disabled / frame 内）是 router 侧契约，任何实现 `HitTestOverlay` 的视图都受同样约束。
- owner 返回 node 后走现有 `OnPointerDown`→`ScrollbarMetrics.HitTest` 分派（thumb drag / track page），**不新增**输入通道、不硬编码 ScrollView/TextEditor 类型。
- 子级嵌套：外层 floating bar 与内层 bar 不交叠时各归各；交叠区按绘制顺序外 bar 后画、命中也归外层（与 paint 一致，不要求 child bar 穿透被父覆盖的区域）。
- hidden（`IsFloatingVisible=false`、无 overflow、disabled、Standard）→ `HitTestOverlay` 返回 null：children 与底层控件命中完全不变，无死区。
- ScrollView 实现：`ScrollbarStyle == Floating && state.IsFloatingVisible && ShowScrollbar(V,C,T)` → 返回 `TrackRect(node.Frame)`。
- TextEditor 实现：同条件返回 `TrackRect(node.Frame)`（padded 右缘条）。
- **静止指针续命**（决策七补充）：滚动使 bar 出现时 pointer 可能静止悬在条区且 deepest hover 是子节点——`UpdateHover` 不会因同帧 offset 变化自动重算。owner 在 `ShowFloating` 置可见后调用 `runtime.RefreshHover()`（`InputRouter` 内部入口：用 router 持有的 `_lastPointerEvent` 重跑 `UpdateHover`——router 在 `DispatchPointer`/`DispatchWheel` 已缓存最近 e.Position；无观测则 no-op），让 owner 成为 deepest hover → `OnPointerEnter` 到 owner → suppress 正常进入。对称出口：pointer 仍在 owner hover chain 但 deepest 变子节点（bar 隐藏/overlay 消失重命中）时 owner 收不到 Leave——`UpdateHover` 在 chain 重算后对**曾 suppress 的 hover-chain 成员**经新 internal hook `View.OnHoverSuppressionLost(node, now)`（owner 检查 `FloatingSuppressed && HoveredNode!=node` → `RearmFloatingTimer`）收敛；调用点仅限 `UpdateHover` chain 变更边界与 `RefreshHover`，不全树扫描、不在 Emit 写状态。

### 决策四：overlay 唤醒调度——`TimedWakes` 集中到期所有权（无新 timer/全树扫描/公开 API）

现状：`UiRuntime.GetNextWakeup` 只对 `_focus.Focused` 取 `View.GetNextWakeup`；`OnClockTick`（:607-620）先 tick focused、再 `GetNextWakeup() is not null → InvalidateRender()`——该"有 wakeup 就重绘"判断专为 focused blink/drag-scroll 服务，**不能套用到 overlay deadline**（否则任何已 armed 的 future deadline 都让每次事件循环无端重绘、形成 needsDraw 反馈链）。扩展分两条语义：

```csharp
// UiRuntime —— 内部调度小框架（TimedWakes），不扩公开 scheduling API
private readonly List<TimedWake> _timedWakes = [];   // 有界（典型 0-2 项），节点级单槽位

private struct TimedWake              // 值类型槽位：重挂/重注册零分配（改字段不 new）
{
    internal ViewNode Node = null!;
    internal TimeSpan Deadline;       // Clock.Now 域绝对时刻
    internal bool Armed;
}

internal void RequestTimedWake(ViewNode node, TimeSpan deadline)  // 查 node 槽位→改字段；无→Add（每节点至多一次 Add）
internal void CancelTimedWake(ViewNode node)
private TimeSpan? NextTimedWakeDelay(TimeSpan now)                // min(deadline-now)，遍历时惰性剔除失效槽
```

- **槽位语义**：节点级单槽位——同 node 重挂即覆盖 `Deadline`（struct 原地改，reschedule 零分配）；`CancelTimedWake` 分两种模式：非遍历期直接 `RemoveAt`；`_timedWakeDispatching`（due 批次中）只置 `Armed=false` 标记，批次结束后统一压缩——**禁止在回调期 `RemoveAt`**（owner 回调取消其它节点的槽若删低 index 会让逆序遍历跳项/越界）。
- **`OnClockTick` due 批次（有界、回调安全）**：(1) `now = Clock.Now`；(2) `_timedWakeDispatching=true`，逆序 `for (i=_timedWakes.Count-1; i>=0; i--)` 且**上界取进入批次时的 Count**（新注册落尾部不回看）：失效槽（`!IsMounted || IsEffectivelyDisabled`）→ `Armed=false` 标记（不移除，统一压缩）；`Armed && now>=Deadline` → `Armed=false` **后** `Node.View.OnTimedWake(node, now)`。(3) `dispatching=false` 后**单次压缩遍历**移除全部 `!Armed` 槽。由此：回调内 `RequestTimedWake`（自重挂→本槽已标记待清，新槽落尾部不回看，同 tick 不二次触发）、`CancelTimedWake`（标 false）、取消**其它**节点槽（也只标记）都安全；批次上限=进入时 armed 到期数，天然有界不 infinite loop。`NextTimedWakeDelay` 只做惰性剔除：正向遍历命中失效槽 `RemoveAt(i)` 后 `i--` 修正；它从不触发回调，无重入。
- **`GetNextWakeup` 拆分**：
  - 公开 `GetNextWakeup()`（宿主 `WaitEventsTimeout` 喂值，OpenTkHost:1069-1101）：`min( focused?.View.GetNextWakeup(focused, now), NextTimedWakeDelay(now) )`。
  - `OnClockTick` 尾部重绘判据**只看 focused 原语义**：`focused.View.GetNextWakeup(focused, now) is not null → InvalidateRender()`。timed-wake 到期隐藏由 due 回调自己 `InvalidateRender`，future deadline 不产生任何每 tick 重绘。hidden idle：集合空 → 零额外唤醒、零额外重绘。
- **清理矩阵**：`UiRuntime.OnNodeUnmounting`（Reconciler :397 → :806 实现）内 `CancelTimedWake(node)` 兜底摘除（不依赖 owner OnUnmount 记得调）；disabled 由 owner `ResetFloating`→`CancelTimedWake`（ValidateInteractionState/Arrange 路径）+ due 批次惰性剔除双保险；`ReleaseGesture`/`ResetFloating`/style 切走统一走 `CancelTimedWake`。
- **hot path 零分配**：struct 槽位 + 逆序索引 + 批次后单次压缩；无 foreach/无 `ToArray`/无枚举器/无每注册 `new`。

### 决策五：Floating 可见性状态机（存 `ScrollbarState`，无独立第二套）

`ScrollbarState` 新增（全部 internal，随 `InteractionState`/`ResetInteractionState` 卸载清理）——最少字段，无冗余镜像：

- `bool IsFloatingVisible`：当前 floating bar 是否可绘/可命中。
- `TimeSpan? FloatingHideDeadline`：armed 时的到期时刻（`IUiClock.Now` 域）；**null = 未挂 deadline**（抑制中/隐藏/idle 三态合一，不再单设 `FloatingTimerArmed` bool）。
- `bool FloatingSuppressed`：hover/drag 抑制中（抑制时 `FloatingHideDeadline==null`，两个字段描述正交事实不互镜像）。

状态迁移（`ScrollbarState` 方法，owner 调；node/now 显式传入——state 不持 node 引用；runtime 注册统一经决策四 `RequestTimedWake`/`CancelTimedWake`）：

- `ShowFloating(node, now)`：`IsFloatingVisible=true`；若非 suppress → `FloatingHideDeadline = now + FloatingHideDelay`、`RequestTimedWake(node, deadline)`；suppress 中 → 只置可见、不动计时。
- `OnTimedWakeDue(node, now)`（owner `OnTimedWake` 唯一入口；runtime 已把槽标记剔除）：**复核** `now >= FloatingHideDeadline`（陈旧 due 不 hide 已重挂的新 deadline）且 `!FloatingSuppressed && PressKind==None`（hover/drag 抑制下不误 hide）→ `FloatingHideDeadline=null`、`IsFloatingVisible=false`、`InvalidateRender`；复核未过 → 幂等收敛（armed 槽已被 runtime 摘除，本字段同步清 null）。
- `SuppressFloatingTimer(node)`：hover 进 bar/drag 开始 → `FloatingSuppressed=true`；`FloatingHideDeadline!=null` → `CancelTimedWake(node)`+deadline=null。可见性不动（hover 在可见条上隐含可见；drag 期间必然可见）。
- `RearmFloatingTimer(node, now)`：hover 离开/drag 结束/`OnHoverSuppressionLost` → `FloatingSuppressed=false`；`IsFloatingVisible` → deadline=now+`FloatingHideDelay`、`RequestTimedWake`；不可见 → idle。
- `ResetFloating(node)`：visible=false、deadline=null、suppress=false、`CancelTimedWake`（style 切走/disabled/无 overflow 翻转时调）。

可见性判定（绘制与命中共用）：`Visible = style==Floating && IsFloatingVisible && ShowScrollbar(V,C,T)`。
overflow 消失（range 收敛到 M≤0）时 `ShowScrollbar=false` → 不画不命中；Arrange 检测到其翻 false → `ResetFloating`（隐藏计时状态自然 cleanup）。

### 决策六：显示触发——仅“实际滚动”

**显示源（set/restart）**：owner 自身滚动偏移发生真实变化的路径：

- ScrollView：`OnWheel` 消费且 `next != offset`（:191-206）；`OnPointerMove` thumb-drag 写 offset（:303-317，仅 `next` 变化时）；`OnPointerDown` track 翻页（:371-380）。Arrange 的 offset clamp（:106-107）不触发。
- TextEditor：`OnWheel`（:1649）；bar drag/track（:510/:560）；`EnsureCaretVisible` 的**主动路径**（键盘导航/编辑/caret 推进/指针/组合 publish ensure；`OnClockTick` 拖选自动滚动 :1739 同列）。

**不触发**（计划明确）：

- 被动 range clamp：Arrange clamp（SV:106-107）、`PublishTextInputGeometry` clamp（TE:1936）、`SyncGeometry` gesture 取消、`SetViewport`/resize 自身、parent translate、DPI。
- **`EnsureCaretVisible` 的被动路径**：`Arrange` 内 `ensureKey` 变化驱动的 ensure（:241-244，resize/reflow/字体/DPI 键变）——与"resize 不显示"一致，不是用户滚动语义。
- 无 offset 变化的 wheel（边界 wheel `next==offset` return false，:193-197）不显示、不延期——事件没产生滚动。
- 同值写（`Math.Abs(next-offset)<0.0001` 早退路径）不触发。

实现（不集中改 `ScrollOffsetY` setter——那是 node 公共属性；TextEditor `ScrollY` 是 state 公共 set）：

- ScrollView：三处真实写点（wheel/drag/track）在 `node.ScrollOffset* = next` 成功后调 `state.NotifyScrolled(node)`（`state` 即 `ScrollbarStateOrCreate`；Floating 才 set 可见+计时，Standard 下 NotifyScrolled 为 no-op——style 判定放 owner，state 只存可见性）。
- TextEditor：`EnsureCaretVisible(node, state, bool showFloating)` 加显式因果参数——主动入口（键盘/编辑/指针/publish ensure/焦点）传 `true`，Arrange ensure-key 路径传 `false`；内部比较旧值，`showFloating && ScrollY 变化` 才 `NotifyScrolled`。wheel/drag/track/OnClockTick 四处显式写点同 SV 处理（写后比较成功才通知）。共 5 个写点，逐个明确，不引入“每帧 diff”。
- `NotifyScrolled(node)` 语义：`IsFloatingVisible || !HoverSuppressed` 时 `ShowFloating`；已在 hover/drag 抑制中则只保证 `IsFloatingVisible=true`（不 reset suppress）。

### 决策七：hover 抑制与计时重启

- 悬停 bar 判定（`Emit`/输入路径共用）：`ReferenceEquals(context.Runtime.HoveredNode, node) && state.LastPointerPoint is { } p && HitTestOverlay(node) 含 p`。Floating 下 `HitTestOverlay` 非空即 bar 可命中区（track 条）。
- **进入抑制**：`OnPointerEnter`/`OnPointerMove` 中判定从“不在 bar 上”→“在 bar 上”（按 `LastPointerPoint`+overlay 现算，不新增持久 hover-kind）→ `SuppressFloatingTimer`：deadline 不挂、不唤醒；bar 保持 `IsFloatingVisible`（hover 在可见条上本身就隐含可见）。
- **退出抑制**：pointer 移出条（仍在 owner 上）或 `OnPointerLeave`/`HoveredNode!=owner` → `RearmFloatingTimer(now+1000ms)`；bar 已不可见则不 rear（无事件泄漏）。
- hover 进入**隐藏**的条位置不显示（不可见即不可命中，pointer 必落在内容/子上）；hover 对可见性的作用仅是“续命”，不是“唤起”。
- **drag 抑制**：`BeginThumbDrag`/`BeginTrackPress` → suppress；`ReleaseGesture`（Up/Cancel/geometry-cancel/SyncGeometry 取消）→ rear（`IsFloatingVisible` 仍 true——drag 期间必然可见）。
- 移入条上时 `InvalidateRender` 已由 DispatchPointer 尾部统一触发（router:295），hover 边无需额外 invalidate；suppress/rearm 只动 deadline 注册。

### 决策八：cleanup 矩阵（全部经 `ResetFloating`/`SyncGeometry`/既有 gesture cancel）

| 触发 | 动作 |
|---|---|
| `ScrollbarStyle` 属性变化（immutable 重建换值，`OnUpdate`） | `ResetFloating`（Standard 下状态字段即死值，不注册）；axis 变化沿用既有 gesture cancel |
| Axis 变化（ScrollView `OnUpdate`） | 既有 `CancelBarGesture` + `ResetFloating` |
| resize/track/content/viewport 变化 | `SyncGeometry` 已取消 gesture；`ResetFloating` 只在 `ShowScrollbar` 翻 false 时调用（几何动但还溢出→计时继续） |
| disable | `ValidateInteractionState`→`OnPointerCancel`（既有）+ Arrange/安全路径 `ResetFloating`；runtime 遍历跳过 disabled 兜底 |
| unmount | `OnNodeUnmounting`（runtime 集合移除）+ `ResetInteractionState`（state 销毁） |
| own offset 写回（drag/wheel/ensure） | **不** cancel gesture、不 reset——现有 `SyncGeometry` 签名不含 offset，保持 |
| hidden 期 pointer 事件 | `HitTestOverlay=null` → 正常落子内容；owner `OnPointer*` bar 分支因 `!Visible` 不进（ScrollView :344 的 `ShowScrollbar` 检查前加 `Visible` 判定；TextEditor :490 同） |

### 决策九：绘制（复用 `ScrollbarPainter` 单入口）

`ScrollbarPainter.Draw` 增加 `floatingVisible` 参与决策——签名加 `ScrollbarStyle style`（或 `bool floating`）参数，内部：

- `!ShowScrollbar` → return（两式共用）。
- Floating && `!state.IsFloatingVisible` → return。
- Floating 绘制：不画 track 矩形；thumb 沿用中性灰三态混色（0.40/0.48/0.56）不变色值；thumb cross 向宽度 = `track.Cross − 2×Inset` 同公式，但 Floating 的 thumb 视觉上更细：cross 尺寸改用 `FloatingThumbThickness = 4`（新内部常量，thumb 矩形 cross 宽 4、沿 track cross 居中），drag/hover 命中区仍是整条 `track`（`HitTestOverlay` 矩形）而非 thumb 细条——命中宽度不变，只有绘制宽度变细（“更细中性灰 thumb”需求）。thumb 圆角 = `min(thumb.W, thumb.H)/2`。
- Standard 绘制：现状不变（track 矩形 + thumb 全宽）。
- disabled：Floating 下 `Visible=false`（决策八）不画；Standard 沿用 Disabled* 色。

### 决策十：due 回调与 `GetNextWakeup` 合并语义

- **due 回调入口是新的 `internal virtual void OnTimedWake(ViewNode node, TimeSpan now)`**（`View` 基类默认 no-op，`OnClockTick` 同族但语义独立）：runtime 只在 `Armed && now>=Deadline` 时调用一次。**必须独立于 `OnClockTick`**——TextEditor 的 `OnClockTick`（:1693 拖选自动滚动）同时挂在 focused 路径上、每 host cycle 都会调，若 due 复用同方法，owner 无法区分"focused 周期 tick"与"deadline 到期"；两个 virtual 分开后 TextEditor 两条路径并存互不替代（ScrollView 只需 override `OnTimedWake`）。
- **deadline 所有权在 runtime**：owner 不往 `View.GetNextWakeup` 里叠 floating deadline——`GetNextWakeup(node, now)` 仍只表达 focused 视图原语义（TextEditor blink/drag-scroll；ScrollView 恒 null）。非 focused owner 的 deadline 经 `_timedWakes` 到达 `WaitEventsTimeout`，不依赖 focus。
- `OnTimedWake` 只在 runtime due 时被调（armed 到期才触发，非每 tick 轮询 owner）——无"早醒 busy loop"问题。
- hover/drag 抑制期：`CancelTimedWake` 后集合无槽 → `GetNextWakeup` 不为 floating 贡献值、到期回调不会到达（无 1ms 级轮询）。

### 决策十一：无动画/无每帧轮询

- 显隐是离散状态翻转 + `InvalidateRender`，无 opacity/渐变/过渡帧。
- 不新增 `IUiDispatcher.RequestWakeup` 调用、不 touch `OpenTkHost`：宿主 `WaitEventsTimeout` 已消费 `UiRuntime.GetNextWakeup`（OpenTkHost:1069-1101），集合扩展后 floating deadline 自动进入同一 timeout 计算。
- hidden idle：注册集空 → `GetNextWakeup` 退回 focused blink/null 原状，零额外唤醒。

### 决策十二：TextEditor IME/编辑语义不变量（复用，不新增）

- bar 命中仍最先分支、`return true` 短路：不 `SetFocus`、不 `CancelCompositionForHostAction`、不改 caret/selection/history/Binding（:482-519 现有结构不变，只把 `ShowScrollbar` 门换成 `Visible` 门）。
- `RequiresCaretReveal` 协议不变：用户 wheel/bar/track 清（:511/:561/:1650），组合推进 set（:1952），publish 消费（:1955-1958）；Floating 的 `NotifyScrolled` 不碰它。
- `PublishTextInputGeometry` 的 clamp（:1936）不属显示源（决策六）；`EnsureCaretVisible`（publish 内 :1957）属显示源——组合 caret 推进产生的真实滚动显示 floating bar，与 wheel 同级。
- `Unavailable` 几何/组合保活/`_anchorRevoked` 链路不动。

### 决策十三：回归与删除义务

- `TextEditorScrollbarTests`/`ScrollbarMetricsTests`/`WheelScrollTests` 等现有测试只测 Standard 默认行为：Floating 不改变 Standard 路径，全部应保持绿；如发现测试 pin 了“gutter 恒 12”的内部假设且属 incidental（非契约），按主审结论更新而非 re-pin。
- Floating 永久测试只承诺消费者可观察行为：显示/隐藏转换、hover 续命、命中优先/透明、无 overflow 不出现、disabled/unmount cleanup、Standard 默认不变、GetNextWakeup 合并最小值；不 pin 内部字段名/deadline 常量的字面（1000ms 可经 `TestUiClock` 推进验证转换，不读私有字段）。
- 旧 scrollbar 任务组归档至 `docs/archive/2026-10-23-scrollbar-tasks/`（本次实施同步完成），task 索引重写为 FS 组。

## 源码位点索引（实施映射）

| 位置 | 变更 |
|---|---|
| `src/CSharp.UI/Views/Scrollbar.cs` | `FloatingHideDelay`/`FloatingThumbThickness` 常量；`ScrollbarState` +4 成员与 `ShowFloating`/`OnTimedWakeDue`/`SuppressFloatingTimer`/`RearmFloatingTimer`/`ResetFloating`/`NotifyScrolled`；`ScrollbarPainter.Draw` 加 style 参数与 floating 分支 |
| `src/CSharp.UI/Views/View.cs` | `internal virtual Rect? HitTestOverlay(ViewNode node) => null`（`GetChildClip` 同族）；`internal virtual void OnTimedWake(ViewNode node, TimeSpan now)` 默认 no-op（`OnClockTick` 同族、独立 due 通道） |
| `src/CSharp.UI/Input/InputRouter.cs` | `HitTest`（:405-431）children 循环前 overlay 检查 |
| `src/CSharp.UI/Runtime/UiRuntime.cs` | `TimedWake` 私有类 + `_timedWakes` 列表 + `RequestTimedWake`/`CancelTimedWake`（幂等）/`NextTimedWakeDelay`；`OnNodeUnmounting`（:806）摘除；`GetNextWakeup`（:592）min 合并；`OnClockTick`（:607）逆序 due 遍历 + focused 原判据分离 |
| `src/CSharp.UI/Views/ScrollView.cs` | `ScrollbarStyle` init 属性；Measure/`ContentRect`/`TrackRect` gutter 按 style；`HitTestOverlay` override；`OnTimedWake` override（→state due 收敛）；`OnPointerEnter`/`Move`/`Leave` hover 抑制边；三处滚动写点 `NotifyScrolled`；`OnUpdate` style 变化 `ResetFloating`；`OnPointerDown` 可见门 |
| `src/CSharp.UI/Views/TextEditor.cs` | `ScrollbarStyle` init 属性 + `With` 复制；`InnerRect`/`TrackRect`/Measure gutter 按 style；`HitTestOverlay` override；`OnTimedWake` override；hover 抑制边（`OnPointerEnter`/`Move`/`Leave` :619-632）；5 处 ScrollY 写点 `NotifyScrolled`；`OnUpdate`/`Arrange` `ResetFloating` 时机；`OnPointerDown` 可见门（:490） |
| `tests/CSharp.UI.Tests/` | 新 `FloatingScrollbarTests`（consumer-observable：runtime+TestUiClock 驱动转换）；既有文件只动被 Floating 破坏的假设 |

## 验收要点（任务拆分细化）

1. `ScrollbarStyle.Standard` 为默认且行为与现状逐点等价（gutter/常显/track 绘制/hover/disabled）。
2. Floating：overflow+实际滚动 → 可见；~1000ms 无活动 → 隐藏；hover/drag 续命；hidden 完全不命中（底层 Button 可点）；可见 bar 命中优先于子内容；祖先 clip 有效。
3. wheel 边界无滚动不显示；被动 clamp/translate 不显示；own offset 不 cancel gesture。
4. 无 overflow 永不显示、不占命中；无 fade/无新 timer/空闲零唤醒。
5. TextEditor：IME 组合期浏览/锚定/Unavailable 语义全数保留；`TextField` 零改动。
6. Debug+Release 四测试套全绿 + 三 sample 真实构建/smoke + 文档（guide/README/delivery）同步。
