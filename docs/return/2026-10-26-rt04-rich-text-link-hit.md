# RT-04 交付记录：链接 hit——行内矩形命中与 Id 身份状态机

日期：2026-10-26
状态：**实施完成，待主审验收**（真实 GL/pointer 证据由 fast 接续，本记录不预声明通过）

## 变更文件

### 产品代码

- **`src/CSharp.UI/Views/RichText.cs`** — RT-04 链接交互状态机与命中判定。
  - `OnLinkActivate`（`Action<object?>`）：按下并释放于同一 interaction Id 时触发，payload 取 `TextInteractionRange.Payload`；null 时按下不消费不 capture。
  - `RichTextInteractionState`（node 级，`node.InteractionState`）：`HoveredId`、`PressedId`、`Cancelled`。
  - `HitLink(layout, content, local)`：遍历 `Content.Interactions`，用 RT-02 已实现的 `layout.GetRangeRects(start,end)`（逐行行内矩形：真实 X/Width、行盒高、`TrimEnd` 外无矩形、行间空隙不在并集）判定包含——**不用 `HitTestIndex` caret 吸附**。
  - `HitLinkAt(node, position)`：frame 绝对坐标 → layout 局部（`position − TextPainter.ResolveOrigin(frame, layout.Size, Alignment, layout.PixelScale)`——与 Emit 共用同一吸附后原点）。
  - `OnPointerDown`：disabled → false；`OnLinkActivate` null → false；未命中 → false。命中 → `PressedId=id, Cancelled=false`，返回 true（router 自动 `IsPointerCaptured`，view 不直接操作 capture）。
  - `OnPointerMove`：`PressedId` 非 null 时 `hit != PressedId` → `Cancelled=true`（**离区永久取消，移回不恢复**）；否则更新 hover（`UpdateHoverState`）。
  - `OnPointerUp`：`PressedId` 非 null → `!Cancelled && hit == PressedId` → `OnLinkActivate(payload)`；清 pressed/cancelled。
  - `OnPointerEnter`/`OnPointerMove`（非 pressed）：`UpdateHoverState`——hover Id 变化 → `OnLinkHover?.Invoke(payload)`（payload 经 `PayloadForId(id)` 查询）；`LinkHoverStyle != null` → `InvalidateRichPaint` 重算 paint 并 `InvalidateRender`。同 Id 不重发。
  - `OnPointerLeave`/`OnPointerCancel`/`OnUpdate`/`OnUnmount`：`ClearHoverState`——hover 非 null 时收敛发一次 `null` 并按需 `InvalidateRichPaint`；`PressedId`/`Cancelled` 清。
  - `UpdateHoverState`/`ClearHoverState` 只读 `Content.Interactions` 表，不查 `StyledText` 旧描述。

### 测试代码

- **`tests/CSharp.UI.Tests/RichTextViewTests.cs`** — 12 个新消费者行为测试（总 26）。现有 `StyledTextHost` 扩展 `OnLinkActivate` 字段透传到 `RichText`。测试经 `TestApp.Runtime.DispatchPointer` 走真实 `InputRouter` 分发（含 hover/capture/disabled/unmount 边界）：
  - down+up 同 Id → `OnLinkActivate(payload)` 一次；
  - 拖离后松手 / 拖离再移回 / `PointerCancel` → 不激活；
  - 同 URL 不同 Id（down A / up B）→ 不激活；
  - 非链接区按下 → `PressedId` 仍为 null（未消费未捕获）；
  - `OnLinkActivate` 为 null → 按下不捕获；
  - `OnLinkHover`：进入收 payload、离开收 `null`、同点不重复；
  - `LinkHoverStyle`：hover 重 paint（`Paint` 值变、`Layout` handle 不变——不重排版）、悬停 segment 前景覆盖为 Red、离开 paint 值还原；
  - 换 `StyledText`（Interactions 变）→ `PressedId` 清、不激活；
  - `Disabled`（祖先 `view.Disabled()`）→ `app.Click` 不激活、`PressedId` 清；
  - 按下后 `host.Visible=false` 卸载 → `ValidateInteractionState`→`OnPointerCancel`→`PressedId` 清、up 不激活。

## 实现细节

- **不吞未命中**：`OnPointerDown` 未命中/`OnLinkActivate` null 时返回 false——router 的 `_pressed=_captured=_hovered` 仍已赋值，但 view 返回 false 不进 `MarkHandled`；`OnPointerUp` 只在 `PressedId` 非 null 时消费。view 不直接操作 `IsPointerCaptured`（router 拥有）。
- **disabled**：`Route` 跳过 `IsEffectivelyDisabled` 节点的 handler；`ValidateInteractionState` 在 disabled 后调 `OnPointerCancel`——view 侧只需 `IsEffectivelyDisabled` 早退 + `OnPointerCancel` 清态。
- **回调重入**：`OnLinkHover`/`OnLinkActivate` 调用在状态清写之后（hover/pressed 先清再 invoke）——回调内触发 `ProcessPendingWork`/unmount 不会读到半更新状态。
- **`GetRangeRects` 行内矩形**：硬换行 terminator 覆盖时产零宽 marker（宽度 0 不命中 pointer）；行尾 trim/行间空隙不在并集 → 自然不命中。跨行 interaction 只命中行内覆盖区。
- **无障碍/focus/键盘**：未接入（契约范围外）；`OnLinkActivate` 是唯一激活路径。
## 验证状态

- `dotnet run`（`tests/CSharp.UI.Tests`，Debug）：**698/698 绿**（`RichTextViewTests` 28/28，RT-04 新增 14 含 clip-out 与 hover-rebuild 回归用例）。
- 无调试插桩、无 `Dbg*.cs` 残留、无 `TODO`/`NotImplemented`。
- 真实 GL/pointer 像素证据：待主审（fast harness 接续）。

## 边界

- `InteractionState` 是 `object` 槽位（框架 `ViewNode.InteractionState`）——`RichText` 独占使用该槽，不与他类冲突。
- `InvalidateRichPaint` 在 hover 边调用：读 `node.ViewCache.Layout` 现 handle + 当前 `Content` + `HoveredId` 重解算 `Paint`——不重排版、`Layout` handle 引用不变。
- `OnLinkActivate` payload 查询按 Id 线性扫 `Interactions`（区间数小，一次 per activate）。

## 主审边界修复（2026-10-26）

- **`OnUpdate`/`OnUnmount` 清 hover 不重算 paint**：`OnUpdate` 时新 `Content` 与旧 `cache.Layout` 端点可能错位——`ClearHoverState(state)` 拆为只收敛 hover 回调 `null`（不重算 paint）；`InvalidateRichPaint` 仅在 `layout`/`Content` 匹配的输入路径（`OnPointerLeave`/`OnPointerCancel`/hover 边 `UpdateHoverState`）调用。`OnUnmount` 同理——handle 将 Dispose，重算 paint 无消费者。
- **捕获期间 clip 外不命中**：`HitLinkAt` 在 `layout`/`size` 检查后追加 `node.Runtime.HitTest(position)`——走 router 完整命中（祖先 clip 累积 + `IsHitTestVisible` + disabled）；返回的 deepest 不是本节点 → 按 clip 外 null——捕获期 pointer 穿过祖先 clip 时 Move/Up 不再误判"仍在 link 矩形上"。
- **捕获外 clip 测试**：`捕获期间指针移到祖先clip外_命中被取消`——`ScrollView`（viewport 200×8，`ScrollbarStyle.Floating`）内 RichText link：按下后 pointer 移到 scroll frame 下缘外，`HitLinkAt` 经 `Runtime.HitTest` 判出 clip → `Cancelled=true` → up 不激活。
- **OnUpdate hover 清后 paint 还原**：`OnUpdate` 先记录 `hadHover`（旧 `HoveredId` 非 null）再 `ClearHoverState`——同值 `Content`/`LinkHoverStyle` 重建时 `paintSemanticsChanged` 纳入 `hadHover`，几何等价即时重算 `cache.Paint`（hover 覆盖撤销），几何不等交 `Measure`。回归：`悬停后父组件重挂同值描述_paint恢复原始`——hover→无关 state 重建→`OnLinkHover(null)` 且 `cache.Paint` 值还原。
- **命中无分配**：`HitLink` 不建 `GetRangeRects` 的 `List<Rect>`——canonical `Segments` 不跨任何 interaction 端点（端点已并入分段边界），逐 `(interaction, segment)` 用 `seg.Start >= range.Start && seg.Start < range.End` 判属并即时构造 `Rect(seg.X, line.Bounds.Y, seg.Width, line.Bounds.Height)` 含点判定；`HitLinkAt` 的 `Runtime.HitTest` 门先判 clip 外早退（不建 rect）。无新增公开 API/资源框架。

## 捕获策略接缝（2026-10-26，主审指定）

- 契约"OnLinkActivate=null 按下不 capture"原本无法在 view 侧实现（router 无条件 `_captured=_hovered` 且 `IsPointerCaptured=true`）。引入 `View.WantsPointerCapture(node, e)` 内部虚拟钩子（默认 true，保持既有控件行为不变）；`RichText` 仅"disabled 外、`OnLinkActivate` 非 null、`HitLinkAt` 命中"时 true。router 在 `_captured` 赋值处走该策略——拒捕时 `_captured=null`，Move/Up 按 `_pressed/_hovered` 既有冒泡路径路由，父 Button 仍正常激活；非 link 区按下不吞事件。
- 同步覆盖：`TextField`/`TextEditor`/`ScrollView`/`Button` 等所有现有 view 走默认 true，行为不变；路由全套件回归 698/698。
