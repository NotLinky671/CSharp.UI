# P-02 PopupPlacementCalculator 纯算法与有效可视区契约

依据：[plan v3](../plan/2026-10-29-popup-and-select-plan.md) §3。
前置：P-01（已主审通过）。状态：**已实现并经主审独立复核通过**（记录见 [P-02 开发记录](../return/2026-10-29-popup-select-p02-dev.md)，原始输出在 `2026-10-29-popup-select-p02-evidence/`）。

## 目标

internal 纯计算 `PopupPlacementCalculator`：输入锚矩形/期望尺寸/已内缩 margin 的可用边界/方向偏好/最大高度，
输出 popup 绝对 Frame 或"关闭"信号——独立单测，同时供 `OverlayManager.ArrangeEntry` 与
`Select` 限高契约复用。

## 契约

```csharp
internal static class PopupPlacementCalculator
{
    // popupBounds = viewport.Inset(margin)（caller 已内缩；本函数不再二次扣 margin）；
    //   锚祖先 clip 不参与，否则 popup 逃不出 ScrollView clip。
    // contentDesired = popup 节点在 popupBounds 宽约束下 Measure 得到的尺寸。
    // maxHeight: Select 传 MaxPopupHeight；通用 Popup 默认 PositiveInfinity。
    // 返回 null = 无有效空间可定位 → caller 关闭 popup（绝不产 NaN/负/部分越界 Frame）。
    internal static Rect? Resolve(
        Rect anchorRect, Size contentDesired, Rect popupBounds,
        PopupPlacementSide preference, double maxHeight);
}
internal enum PopupPlacementSide { Auto, Above, Below }
```

实现位置：`src/CSharp.UI/Runtime/PopupPlacementCalculator.cs`；`OverlayManager.ArrangeEntry`
只做两阶段 measure + Arrange，不再内联定位公式。

算法（plan v3 §3 冻结，两阶段有界 measure）：

- **阶段 1（Measure）**：`OverlayHost` 给 `PopupSurface` 约束 =
  `(0..popupBounds.W, 0..popupBounds.H)` → 拿 `DesiredSize`（宽约束下真实高）；
- **阶段 2（纯计算 Resolve，不调 Measure）**：
  - `popupBounds`（已含 margin 内缩）宽或高 ≤ 0 → `null`；
  - `w = clamp(desiredW, 0, popupBounds.W)`；
  - `spaceBelow = max(0, popupBounds.Bottom - anchor.Bottom)`、
    `spaceAbove = max(0, anchor.Y - popupBounds.Top)`；
  - `side = Auto ? (desiredH ≤ spaceBelow || spaceBelow ≥ spaceAbove ? Below : Above)
    : preference`——`Above`/`Below` **强制**方向（不足按该侧限高、不翻转；
    `Auto` 才两向 flip）；
  - `sideAvail = side==Below ? spaceBelow : spaceAbove`；
  - `h = min(desiredH, maxHeight, sideAvail, popupBounds.Height)`（side 可用高度
    优先于通用 maxHeight/边界，B5）；
  - `h ≤ 0 || w ≤ 0` → `null`（关闭）；
  - 非有限输入（anchor/desired/bounds 含 NaN/Inf）→ `null`，保证返回值恒有限；
  - `top = side==Below ? anchor.Bottom : anchor.Y - h`，clamp `[popupBounds.Top, popupBounds.Bottom-h]`；
  - `left = anchor.X`，clamp `[popupBounds.Left, popupBounds.Right-w]`；
- **阶段 3（有界二次 measure，至多一次）**：最终 `(w,h)` ≠ 阶段 1
  `DesiredSize` → 用约束上限 `(w,h)` 再 Measure 一次（仍超限 → Arrange 按
  `(w,h)`、内容经 `PopupSurface` clip——Select 内套 `ScrollView` 拿到最终
  有限高后可视区/thumb 正确）；
- **零空间关闭纪律**：`Resolve` 返回 `null` 时 `ArrangeEntry` 只把 surface
  Arrange 成 `Rect.Empty` 并置 geometry-invalid 意图；不在 Arrange 内
  unmount。安全阶段 `ConsumePending` 看到该意图后关闭并卸载 entry
  （`ProcessPendingWork` 返回后 `HasActiveEntry=false`）。
- 返回有限 `Rect(left, top, w, h)`，`w/h ≥ 0`；Anchor 滚动/Frame 变化 →
  下轮重算（popup 跟随）。

## 不做

锚可见区收集（caller：`OverlayHost` 只算 `anchorVisible` = `anchor.Frame ∩
viewport ∩ 各祖先 GetChildClip`——**只用于锚可见资格判定**，placement 的
宽/高上限用 `popupBounds`=`viewport.Inset(Margin)` 独立矩形，不与锚祖先
clip 交；P-01 实现或本任务内补 helper，写注释）；popup 内容超限的滚动
（Select 内套 ScrollView）。

## 验收（纯函数测试，不经 runtime）

- B1：充足空间 → 锚下方左对齐、Frame 全部有限。
- B2：下方不足上方充足（Auto）→ 上翻、底边贴锚顶。
- B3：两侧都不足（Auto）→ 取空间大的一侧 + h 限该侧可用 → 结果仍在 popupBounds 内
  （可截内容高度）。
- B4：锚贴 popupBounds 左/右/上/下边缘 → clamp margin 内。
- B5：`desiredW > popupBounds.W` → w=popupBounds 宽；`h = min(desiredH, maxHeight,
  sideAvail, popupBounds.H)`——side 可用高度优先（该侧 60 时即使 maxHeight 400 也取 60）；
  popup 不穿 anchor（side 正确）。
- B6：`popupBounds` 零宽/零高/空 → `null`（关闭信号，非异常）。
- B7：`preference=Above` 而上方空间不足但下方充足 → **仍 Above**、按上方可用
  限高（强制、不翻转）；`Below` 对称——与 `Auto` 的 flip 区分开。
- B8：`h ≤ 0 || w ≤ 0` → `null`（不挂零高 popup）。
- B9：阶段 3——最终 `(w,h)` 与阶段 1 DesiredSize 不同 → 经 `UiRuntime`
  Arrange 后 popup `Frame == Resolve` 返回且内部子树按最终尺寸 Arrange
  （用含 ScrollView 的 popup content 断言可视区正确——非纯函数条，走
  P-01 host 间接断言）。
- 断言只输出 `Rect?`/经 runtime `Frame`；internal 可见经 `InternalsVisibleTo`
  或经 P-01 Arrange 层间接断言。
