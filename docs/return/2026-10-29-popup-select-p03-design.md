# P-03 设计准备：公开 Popup 与 IsOpen 双向纪律（待 P-02 通过后实施）

依据：[plan v3 §4](../plan/2026-10-29-popup-and-select-plan.md) + [P-03 task](../archive/2026-10-31-popup-select-tasks/03-popup-view.md)。
前置：P-02（fast 实施中，本文件在 P-02 主审通过、主工作流 read 接口后才变成实施依据）。
状态：**已实施（2026-10-29），实现记录见 [2026-10-29-popup-select-p03.md](2026-10-29-popup-select-p03.md)；
本文件保留设计依据，实施中的偏差（surface 用 `UpdateNode` 而非 `ReconcileDescriptors`）已在 §5 更正。**

## 1. 所有权与观测方式（核心决定）

**manager 拉取（poll）owner 的开合意图，而不是让 owner 主动 `Show`。**
每个 owner 节点在 `OnMount` 时向 manager 注册，`OnUnmount` 注销；manager 在
`ConsumePending` 开头、按注册顺序（= 主树声明序）逐个拉取一次：

```csharp
internal interface IOverlayOwner
{
    /// <summary>owner 当前是否有打开意图；true 时给出本次打开所需的完整请求。</summary>
    bool TryGetOpenRequest(ViewNode node, out OverlayOpenRequest request);

    /// <summary>manager 侧内部关闭（非 owner 主动）：owner 同步自己的状态。</summary>
    void OnOverlayClosed(ViewNode node, OverlayCloseReason reason);
}

internal readonly record struct OverlayOpenRequest(
    ViewNode Anchor, View Content, double Margin, double MaxHeight, PopupPlacementSide Preference);

internal enum OverlayCloseReason
{
    OwnerRequest, OutsidePress, Escape, AnchorIneligible, WindowDeactivated, Displaced, Unmounted,
}
```

- `Popup.TryGetOpenRequest` = `IsOpen.Read(node)`（登记来源依赖；顺带给 manager 锚/content/
  Margin/MaxHeight/Preference 现值——每次拉取都是最新描述，实例换不重置状态）。
- `Select.TryGetOpenRequest`（P-05）= `InteractionState` 里的 open flag；flag 只在
  “items 非空 + 未全 disabled + 未 disabled”判断通过后置位。
- **两者共用一条打开/关闭路径**（Select 不再自己调 `Show`/`Close`），满足计划"同一 overlay 生命周期"。
- `Show(...)` 保留为 internal 直接入口（P-01 测试、特殊内部场景）；owner 驱动的请求走 manager 内部
  `RequestShow`/`RequestClose`（同样只登记 pending）。

理由：不在 Measure/Emit 里写状态机、不需要事件总线/订阅、没有回调重入；`ConsumePending`
已是安全阶段，拉取 + 状态迁移 + 关闭通知全部发生在同一处。

### 1.1 拉取的代价边界（主审约束）

- `TryGetOpenRequest` 只做**只读**：读当前 `node.View` 描述字段 + `IsOpen.Read(node)`
  （`Binding.Read` 负责把来源 `State` 登记为该 owner 祖先组件的依赖）；**不 new View、不建
  descriptor、不 reconcile、不写状态、不分配集合**。
- 只有 owner 描述/参数/content **实际变化**时才各做一次动作：
  - content/锚引用不同 → 对 **surface 节点**（不是 host）reconcile 一次；
  - Margin/MaxHeight/Preference 不同 → `_active with {...}` 一次；
  - 相同 → 不触碰任何节点、不 `InvalidateLayout`/`InvalidateRender`。
- 于是“无请求 + 环境/几何未变”的一轮 `ProcessPendingWork` 不产生新的 work，也不会把
  `_layoutDirty` 置回 true（空闲帧不丢失、不空转；P-07 验证）。
- owner 读取不额外订阅、不注册每帧 UI 回调：`ProcessPendingWork` 本身每轮已调用安全阶段；
  `Binding.Read` 的依赖登记保证状态变化会通过既有组件重建/重排路径唤醒。

### 1.2 注册表与旧回调安全（主审约束）

- 注册表 = `List<ViewNode>`（注册顺序即主树声明序）+ `Dictionary<ViewNode, OverlayOwnerState>`
  按引用相等；**只在 mount/unmount 时增删**，拉取循环内不增删。
- 拉取循环用索引遍历且每步复核 `node.IsMounted` + `_ownerStates` 命中；即便回调**间接**导致
  注册表变化，也不依赖迭代器（不会 `InvalidOperationException`/跳过项）。
- 关闭通知**绑定到被关闭的 entry（含其 owner 节点引用）**，不按“当前活动 owner”查表：
  旧 entry 的延迟关闭不可能通知/关闭后来挂载的新 owner。配合 P-01 的 `_mountEpoch`（请求在
  新挂载后即过时丢弃），旧回调不会作用于新 entry。
- owner 状态表用节点代际：`OnNodeUnmounting` 移除该节点状态；新节点（即便同一路径/同一视图类型）
  是独立状态与新代际，不复用 `Requested/AwaitingOpenEdge`。

## 2. owner open-request 状态机（per owner node）

```text
RequireOpen(rising, awaitingEdge):
    rising = requested && !state.Requested;   state.Requested = requested
    if (requested):
        if (awaitingEdge):
            if (!rising) return;              // binding 恒 true：保持关、不写、不打开
            awaitingEdge = false;             // 真 false→true 新边沿
        if (entry.Owner == this) return;      // 已打开
        RequestShow(...)                      // 资格门在消费时判；不通过则不产 entry、不写
    else:
        if (entry.Owner == this) RequestClose(OwnerRequest)
```

- 内部关闭（`CloseEntry`）统一 `awaitingEdge = true`；`OwnerRequest` 关闭不写 binding（owner 已写）。
- **constant-true binding**：内部关闭写 false（对 getter 无效）→ `Requested` 仍为 true → 无 rising
  → 不再 Show、不再写 → 无自激（C3）。
- **资格门长期不满足**：不产 entry、不写、不设 awaiting → 每轮评估一次，资格恢复后首次即开（只开一次）。
- **挤占**：同一轮内后注册的 owner 覆盖 `_pendingShow` 时，对前一个 owner 通知
  `OnOverlayClosed(Displaced)`（写 false 一次）+ `awaitingEdge=true`（C9：不再扫描重开）。
- 未实现 `IOverlayOwner` 的 owner（P-01 A8 的 `Text` owner）只保留 `AwaitingOpenEdge` 语义，
  不受拉取影响。

## 3. `ConsumePending` 顺序调整（P-03 改 `OverlayManager`）

```text
1. PollOwners()                    // 只登记 pending / 通知被挤占者；不改变树
2. budget 检查（沿用 P-01）
3. pendingClose 消费（原因随请求携带；epoch 失配丢弃）
4. 活动 entry 资格校验（AnchorIneligible）
5. pendingShow 消费（nested/资格复验后 Mount；挤占旧 entry → Displaced）
```

把“挂起关闭”放到资格校验之前：同一轮 binding 已 false 且锚已不可见时按 `OwnerRequest` 关闭，
不再额外写一次 false（C2 的 setter 计数保持“内部关闭一次”）。

## 4. 每个 owner 关闭路径的同步动作

| reason | 谁写 `IsOpen=false` | 焦点（P-04 用） |
|---|---|---|
| `OwnerRequest` | owner 自己（manager **不通知、不回写**） | 不主动改焦 |
| `OutsidePress` / `Escape` | manager → `OnOverlayClosed` 写**恰好一次** | Esc/选后：按**当时活动 entry** 的 `SavedFocus` 恢复 |
| `AnchorIneligible` / `WindowDeactivated` / `Displaced` / `Unmounted` | manager → `OnOverlayClosed` 写**恰好一次** | 不主动改焦 |

**焦点恢复不依赖“上一次关闭”的缓存**：P-04 在**发起关闭之前**从当时活动 entry 取
`SavedFocus`/`AnchorNode`（`Overlay.ActiveEntry`），关闭被安全阶段消费后按该快照
`SetFocus(SavedFocus → 锚子树首 focusable → null)`。不引入 `LastClosedEntry` 之类会被后来
挂载/关闭污染的外部状态，也不让过期关闭影响外界焦点。

## 5. 公开 `Popup`（`src/CSharp.UI/Views/Popup.cs`）

```csharp
public enum PopupPlacement { Auto, Above, Below }   // P-03 由 fast 的 internal PopupPlacementSide 干净改名
public sealed class Popup : View, IOverlayOwner
{
    public Popup(View anchor, View content, Binding<bool> isOpen,
                 PopupPlacement preference = PopupPlacement.Auto);
    public View Anchor { get; }   public View Content { get; }   public Binding<bool> IsOpen { get; }
    public PopupPlacement Preference { get; init; } = PopupPlacement.Auto;
    public double Margin { get; init; } = 4;                        // 有限非负（构造/init 校验）
    public double MaxHeight { get; init; } = double.PositiveInfinity; // 正或 +∞
    public bool CloseOnOutsidePress { get; init; } = true;
    public bool CloseOnEscape { get; init; } = true;
}
```

- 布局：照 `ModifierView` 的透传约定（`Children = [Anchor]` 固定数组、Measure 透传约束取
  anchor Desired、Arrange 同 bounds、`GetLayoutFeatures` 透传 anchor、默认 `Emit` 只画 Children）。
- `OnMount` → `Overlay.RegisterOwner(node)`；`OnUnmount` 无需自己处理：运行时既有的
  `OnNodeUnmounting` 钩子（先于 `View.OnUnmount`）已负责移除 owner 状态并把活动 entry 记为
  `Unmounted` 关闭（→ `OnOverlayClosed` 写 false 一次）。`OnUpdate` 也不需要重注册
  （manager 按节点存状态，拉取读的是当前 `node.View`）。
- **content 描述更新（C7）**：仅在 **引用真的不同** 时（`surface.Content != request.Content`
  或锚节点变了）才更新一次：用 `Reconciler.UpdateNode(surfaceNode, new ChildDescriptor(new
  PopupSurfaceView(anchorNode, content), ...))` **替换 surface 节点自身的描述**（注意不是
  `ReconcileDescriptors(surfaceNode, ...)`——那会协调 surface 的子项、在 surface 下再建一层
  节点，P-03 实现时已实测为自激来源）。surface 节点身份保持，content 子树按 key/类型复用
  （`UseState`/InteractionState 不丢），不是 unmount/remount host。相同引用 → 该轮不做任何
  reconcile（见 §1.1，避免每 poll 新建 surface 导致 layout 永不稳定的自激）。
- `entry` 的 `AnchorNode/Margin/MaxHeight/Preference` 只在**值变化**时用 record `with` 刷新一次；
  未变化不写 entry（不产生额外的失效）。

## 6. 需要 P-02 提供的接口（read 审核清单，以**实际源码**为准）

- `PopupPlacementCalculator.Resolve(Rect anchorRect, Size contentDesired, Rect popupBounds,
  PopupPlacementSide preference, double maxSize)` 返回 `Rect?`——**主审已裁定删除多余 `margin`
  参数**：`popupBounds` 已由 caller `viewport.Inset(margin)`，不得二次内缩。旧 task 文档里的
  `margin` 形参是过时描述，不与源码冲突时不改源码。
- `ActiveOverlayEntry` 增加 `Preference`；`Show(...)` 末参 `PopupPlacementSide preference`。
- `ArrangeEntry` 用 calculator + 至多一次 `MeasureFresh`；`MeasureEntry` 语义不变。
- `PopupPlacementSide` 内部枚举名与 `Resolve` 的参数顺序/**null 语义**（h≤0||w≤0 → null）要在 P03
  改名时一并检查；无 alias、无 shim（P-03 直接 `PopupPlacementSide` → `PopupPlacement` 全量替换）。

## 7. 验收映射（P-03 新增测试文件 `tests/CSharp.UI.Tests/PopupViewTests.cs`）

C1 mount/hit/focus · C2 false→true→false 各一次 + 内部关闭写 false 一次 · C3 constant-true 不自激 +
真边沿重开 · C4/C5 flag=false 跳过（P-04 断言路径，P-03 只暴露 flag） · C6 锚环境 + disabled 拒开 ·
C7 content 更新保 state · C8 preference 生效 · C9 两个初始 true 后者胜、前者 false 一次后不再争 ·
C10 嵌套拒（P-01 已有，C10 用公开 `Popup` 复验） · C11 归 P-04。

## 8. 实施风险（P-03 需明确守住）

1. polling 不得每轮分配：`List<ViewNode>` 注册表 + 索引循环（注册/注销只发生在 mount/unmount）。
2. 状态写只能在安全阶段（`ConsumePending`），绝不在 Measure/Arrange/Emit。
3. 每次内部关闭最多写一次 false；写后靠 `awaitingEdge` 抑制，不重试。
4. `Popup.OnMount` 时节点尚未挂父、`ChildList` 为空：拉取要容忍（`IsMounted` + `ChildList.Count` 检查）。
5. 枚举改名会动 P-02 单测（B1–B8）：机械替换，保留算法断言数值不变。
