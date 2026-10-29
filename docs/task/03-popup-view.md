# P-03 Popup 公开 view 与 IsOpen 双向纪律

依据：[plan v3](../plan/2026-10-29-popup-and-select-plan.md) §2。
前置：P-02。状态：待授权实施。

## 目标

公开 `Popup`（core `CSharp.UI`，新文件 `Views/Popup.cs`）+ `PopupPlacement` 枚举：
声明式锚定弹层，content 为普通 `View`，`Binding<bool>` 双向开合。

## API（plan 冻结）

```csharp
public sealed class Popup : View
{
    public Popup(View anchor, View content, Binding<bool> isOpen,
                 PopupPlacement preference = PopupPlacement.Auto);
    public View Anchor { get; }
    public View Content { get; }
    public Binding<bool> IsOpen { get; }
    public PopupPlacement Preference { get; init; } = PopupPlacement.Auto; // Auto|Above|Below
    public double Margin { get; init; } = 4;                        // 有限、非负
    public double MaxHeight { get; init; } = double.PositiveInfinity; // 正或+∞
    public bool CloseOnOutsidePress { get; init; } = true;
    public bool CloseOnEscape { get; init; } = true;
}
public enum PopupPlacement { Auto, Above, Below }
```

## 行为契约

- `Popup` 是主树正常节点：**布局只透传 Anchor**——`Children = [Anchor]`
  （`Content` 不经 `Children`）；Measure 取 anchor Desired、Arrange 给 anchor
  同 Frame、`GetLayoutFeatures` 透传 anchor features；**`Emit` 只 emit
  Anchor**（popup content 由 overlay 层自绘）；popup content 不影响主布局。
- **开合（owner open-request 状态机，plan v3 §4 冻结）**：
  - manager 对本 `Popup` owner 记录"已观察 open-request"——三种：
    `从未开` / `已打开` / `已内部关闭(待 false→true 边沿)`；
  - `IsOpen` 变 true + owner `从未开` → **资格门**（锚 `IsMounted` + 非
    `IsEffectivelyDisabled` + `anchorVisible` 非空 + `popupBounds` 非空 +
    窗口激活）通过才 `Show`（记 `已打开`）；不通过 → 不开**且不回写**
    （binding 不擅改；资格恢复后**首次**评估即开一次）；
  - `IsOpen` 变 false（owner 主动）→ 关（若开着）→ 记 `待边沿`；
  - 开着时任何**内部关闭**（外点/Esc/`anchorVisible` 空/锚卸载/disabled/
    失活/另一 `Show` 挤占）→ 写 `IsOpen=false` **恰好一次** + 记 `待边沿`——
    之后每轮即使 binding 仍 true **不再 `Show`**；getter 恒 true →
    保持关、不反复写 false、**无自激**；外部 State 真变 `false→true`
    （controlled setter 实际收到 false 再 true）→ 新边沿 → 重开；
  - owner `OnUnmount`/节点替换 → owner 状态清（新 owner 独立 generation）；
  - `IsOpen` binding **实例**换（`State.ToBinding()` 每 Build 新实例）→
    按新 binding 现值收敛，**不重置 owner 已观察 open-request 状态**
    （换实例不算新边沿）；
  - `Show`/`Close` 不写除 `IsOpen` 外任何绑定；
  - **嵌套 `Show` 请求**（popup content 内再 `Popup`/`Select` 请求开）→
    **拒绝**（诊断——外层保持开、内层不开；非本轮目标）。
- **binding 追踪**：`Popup` 对 `IsOpen` 的读取走 `Binding.Read(node)`
  （登记 SourceState 依赖——`.Value` getter 直接读不登记、外部 `State` 变化
  不会驱动重建；binding 实例换按现值收敛不重置 owner 状态）。
- **描述更新**：`Popup` 视图重建（同 key/类型）→ entry 保持（`Anchor`/`Content`/
  `IsOpen` 各属性经 reconcile 更新；`Content` 描述变化且开着 → popup 子树
  reconcile，不 unmount/remount 丢 content 内 `UseState`/InteractionState）；
  `Popup` 节点 `OnUnmount` 且开着 → 走内部关闭路径写 `IsOpen=false`
  （owner 状态清，setter 重入由 manager request-generation + 既有 guard
  覆盖——复用纪律不直接引私有 focus 字段）。
- **content 环境**：popup 子树按锚当前 Environment（P-01）——`Popup` 若在
  `ThemeView`/`DisabledView`/`FontView` 内，content 继承锚局部环境。
- **`CloseOnOutsidePress`/`CloseOnEscape` false** → 对应路径跳过（外点照常分发
  不吞并——无 dismiss-target 介入；Esc 透传下层）。

## 不做

外点吞并/Esc/失活/capture 实现（P-04，但 `Popup` view 暴露的 flag 已在 API 面）；
modal、多层、动画。

## 验收（headless runtime）

- C1：`Popup(anchor=Button, content=VStack(Text), isOpen=state)`：
  `state=true` → popup 节点挂载、HitTest 命中在 anchor 之上（popup content
  可点——Host `HitTestSelf=false` 但 entry 期间 `IsHitTestVisible=true`）；
  `state=false` → 卸载；anchor 正常命中/布局不受影响。
- C2：binding `false→true→false` 序列 → mount/unmount 各一次；
  内部关闭路径写回 false **恰好一次**（Binding setter 计数）。
- C3：getter 恒 true + 外点 → 写 false 一次 + owner 记 `待边沿`——
  **之后不重开**（`ProcessPendingWork` 返回后仍关、无每帧竞争；
  外部 State 真变 `false→true` 新边沿 → 重开）。断言写序列=false 一次、
  关后稳定、显式再 true 才 mount。
- C4：`CloseOnOutsidePress=false` + 外点 → 弹层不关、下方正常接收 Down。
- C5：`CloseOnEscape=false` + Esc → 弹层不关、Esc 透传（下方可观察 handled=false）。
- C6：锚在 ThemeView/DisabledView 内 → popup content 环境=锚环境；
  DisabledView 下 `Show` 拒绝（popup 不开、binding 不被写）。
- C7：`Popup` 描述更新（新 Content）→ popup 子树 reconcile 不闪断；
  anchor `Frame` 变化 → popup 跟随。
- C8：`preference=Above/Below` 生效（Frame 断言经 Arrange 后 popup 位置）。
- C9：两 `Popup` 初始 binding 同 true → 按主树声明序**后者** `Show` 胜、
  前者被挤占记 `待边沿`不再重开（`ProcessPendingWork` 返回后稳定单 entry、
  无每帧 Show 竞争断言——Binding setter 每 owner 至多 false 一次）。
- C10：嵌套 `Show`（popup content 内 `Popup`/`Select` 请求开）→ **拒绝**
  （诊断断言）、外层保持开、无第二 entry。
- C11：popup 打开 gesture 中途外部 binding 变 true → 本轮收敛开；
  **当前 gesture 后续 Up/Move 仍按 captured/pressed 原目标路由**
  （不误选新 popup 内容——原目标收 gesture；下一 Down 才按新 hit 判）。
- 断言经 `HitTest`/`IsMounted`/`Binding` 探测/`FocusedNode`/DrawCommand 序，
  不 pin 内部字段名。
