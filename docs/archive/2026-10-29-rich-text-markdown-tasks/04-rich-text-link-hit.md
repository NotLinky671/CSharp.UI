# RT-04：链接 hit——行内矩形命中与 Id 身份状态机

创建日期：2026-10-25

状态：**已完成并验收**（2026-10-26 主审：34 项真实 GL/pointer 检查 0 失败，含 Q clip-out 不激活、O/F capture 策略、hover GL 原色恢复与 cache 稳定；fast r3+ 证据）

阶段：B（G1 出口）

设计依据：[计划](../../plan/2026-10-25-rich-text-and-markdown-plan.md) §决策六。

[返回任务索引](README.md)

前置依赖：RT-03。

## 文件范围

- `src/CSharp.UI/Views/RichText.cs`：`OnPointerDown/Move/Up/Enter/Leave/Cancel`、pressed/hover 状态、`PaintDirty` 联动（RT-03 预留点）。
- `src/CSharp.UI/Text/RichTextContracts.cs`（如需要）：`GetRangeRects(int start, int end)` 逐行矩形 API。
- `tests/CSharp.UI.Tests/`：runtime+stub 服务驱动输入事件。

## 实现契约

### 命中判定（不用 caret 吸附）

- 命中几何 = `TextInteractionRange` 的**行内 segment 矩形并集**（`GetRangeRects` 逐行矩形：行内 X/Width、行盒高）。pointer local 落在任一矩形内 → 命中该 `Id`。
- **不命中**：`TrimEnd` 空白、行间空隙、跨行区间的行内非覆盖区、矩形并集外 padding；`HitTestIndex` 吸附**不用**于链接判定。

### 状态机（node 级 interaction state）

- `OnPointerDown`（`IsHitTestVisible`、非 disabled、frame 内）：命中 Id → `pressedId=Id`、`cancelled=false`、pointer capture、return true；未命中 → return false **不吞事件**。**捕获经最小内部接缝**：`View.WantsPointerCapture(node,e)` 默认 true（既有语义）；`RichText` 仅"有 `OnLinkActivate` 回调且命中 link"时返回 true——非 link 区/空回调按下不 capture、不吞事件、祖先 Button 照常激活。
- `OnPointerMove`（capture 中）：`命中Id != pressedId` → `cancelled=true`（**离区即取消，移回不恢复**）。
- `OnPointerUp`：`!cancelled && 命中Id == pressedId` → `OnLinkActivate?.Invoke(payload)`；清态释放 capture、return true。Down A/Up B（同 URL 不同 Id）→ 不激活。
- `OnPointerCancel`/`OnUpdate`（新 `StyledText`）/`OnUnmount`/disabled → 清 pressed+hover；hover 收敛发 `OnLinkHover(null)`（若曾 hover）。
- `OnPointerEnter`/`Move`（非 pressed）：hover Id 变化 → `OnLinkHover?.Invoke(payload ?? null)`；`LinkHoverStyle`（`TextPaintStyle`，paint-only 无字体字段）非 null → 置 `PaintDirty` + `InvalidateRender`（RT-03 解算在下帧重 paint）；无 hover 样式不 invalidate。
- `OnLinkActivate` null：按下不 capture（区间照常渲染）。
- 无 focus/键盘/IME；不抢 `TextEditor` 输入。回调异常沿用框架输入事件策略。

## 边界

- 无 OS OpenUrl/`Process.Start`；payload 语义 caller 定。
- 无 selection/复制；`HitTestIndex` 仅内部。
- `TextField`/`TextEditor`/IME 路径零改动。

## 验收

- down/up 同 Id → callback 收 payload 一次；同 URL 不同 Id Down A/Up B 不激活。
- drag-out → up 不激活；移回不恢复；`PointerCancel` → 不激活。
- 行内空隙/行尾/跨行空处/padding：down return false 不 capture。
- `OnLinkHover`：进入收 payload、离开收 null、同区间不重复；`LinkHoverStyle` 联调：hover segment 换色（新 paint 键）、离开还原。
- 换 `StyledText`（Interactions 变）/unmount/disabled → 态清理无 stale 激活。
- 祖先 clip 外不命中；既有 pointer 路由测试全绿。
