# Popup 覆盖层与 Select 控件任务索引

创建日期：2026-10-29

设计依据：[Popup 与 Select 实施计划](../plan/2026-10-29-popup-and-select-plan.md)。

状态：**待授权实施，全部未开始**。前一任务组（RT-01~04、MD-01~06 富文本与 Markdown）已完成并归档至
[docs/archive/2026-10-29-rich-text-markdown-tasks/](../archive/2026-10-29-rich-text-markdown-tasks/README.md)。

## 核心决策（计划 v3 已冻结，以下为摘要——细节以 plan 为准）

- **Root 层结构**：`UiRuntime` 内部固定 `UiRootLayerView`（[userRoot, OverlayHost]
  两子，经 `Reconciler.MountRoot` 单次挂载——`Root` public 返回其中 userNode，
  runtime traversal 起点为 `_layer`）；**`Root.Parent` 现为 `_layer`（非 null）、
  `Depth` +1——兼容风险如实记录**；overlay 子树与用户树隔离，用户 Root 可为
  `ClipView`/`ScrollView` 无影响。
  **命中纪律**：RootLayer `IsHitTestVisible=true` + 显式 `HitTestSelf=false`；
  Host `IsHitTestVisible=(entry!=null||swallow active)` + 显式 `HitTestSelf=false`——
  `IsHitTestVisible=false` 剪整棵子树（RootLayer 必须恒 true），自身无命中靠
  `HitTestSelf` 显式覆写（默认 `Frame.Contains` 会命中）。
- **公开 `Popup`**：`Popup(View anchor, View content, Binding<bool> isOpen,
  PopupPlacement preference = Auto)`——`Margin`/`MaxHeight`/
  `CloseOnOutsidePress`/`CloseOnEscape` 为 init 属性（非构造参数）；
  `IsOpen` 双向：资格门（mounted/enabled/`anchorVisible` 非空/`popupBounds`
  非空/窗口激活）先判；**owner open-request 状态机**（从未开/已打开/已内部关闭
  待 false→true 边沿）——内部关闭写 false 一次后记待边沿，binding 恒 true
  不再 `Show`、无自激；binding 读取走 `Binding.Read(node)`；
  **嵌套 `Show` 拒绝**（诊断——外层保持开）。
- **单 popup**：`OverlayManager.ActiveEntry` 单槽，`Show` 先 `CloseAll()`；
  不建 Entries 列表假多层。
- **环境**：popup content 的父为 `PopupSurface`（`ResolveEnvironment` 返回**锚当前
  `Environment`**——非快照，每轮读现值）；`GetChildClip`/`Emit` 按最终 Frame
  裁剪；锚 `IsEffectivelyDisabled` → 关闭；窗口失活 → 关。
- **placement（两阶段有界，双矩形）**：主树 measure+arrange → overlay measure
  （约束 `(0..popupBounds.W, 0..popupBounds.H)`，`popupBounds=viewport.Inset(Margin)`，
  **不与锚祖先 clip 交**——否则 popup 逃不出 ScrollView clip）→ 纯计算 Resolve
  （w clamp→`Above`/`Below` 强制/`Auto` 两向 flip→h=min(desired,maxSize,
  sideAvail,popupBounds.H)；h≤0||w≤0→null 关）→ 最终尺寸≠DesiredSize 则
  **有界二次 measure 至多一次** → arrange；锚 Frame（非可见交）为定位参考；
  **`anchorVisible`=anchor.Frame∩viewport∩各祖先 GetChildClip 只用于锚可见资格**
  （空→关、部分非空→保持），与 `popupBounds` 独立。
- **外点 = dismiss + 吞并整次 gesture**：dismiss-target 只在吞并 gesture 期间
  存在（Up/Cancel 后释放；**无 entry 且非吞并期** `OverlayHost.IsHitTestVisible=false`
  不拦 wheel/hover；entry 存在 → true 但 `HitTestSelf=false`、popup 子节点可点）；
- **Esc**：复用 `DispatchKey` 现有顺序——Route 先给 focused 节点（composition-
  cancel/自处理控件/Select trigger 先消费），未 handled + entry 存在才拦截；
  无 IME 特判。焦点恢复：Esc/选后→SavedFocus→锚子树首 focusable→null；
  外点/外写/替换/失活/挤占→不主动设焦。
- **Up-activation**：弹层在 Up(同点) 打开，opening gesture 完整落 trigger；
  binding 中途变 true → 本轮收敛开，**当前 gesture 仍按 captured/pressed
  原目标路由**，下一 Down 才按新 hit 判——不误选。
- **Select**：`Select<T>(Binding<T>, IReadOnlyList<SelectItem<T>>, placeholder)`——
  items `Value` **唯一**（duplicate→`ArgumentException`，非静默 first-wins）；
  值在但项 disabled → 仍显示该项 Label；items 空/全 disabled/Select disabled →
  不打开（pointer/键盘统一判）；同值重选仍写一次；active stable-by-value；
  **内部关闭（manager `Close`）→ Select 同步清 open flag**；
  `Value` 读取走 `Binding.Read(node)` 登记依赖；完整键盘表见 plan §6。

## 任务与依赖

| 完成 | ID | 任务 | 前置依赖 | 阶段 |
|---|---|---|---|---|
| ☐ | P-01 | [UiRootLayerView + OverlayManager：挂载/锚环境现值/关闭规则](01-overlay-manager.md) | 无 | A |
| ☐ | P-02 | [PopupPlacement 纯算法与有效可视区契约](02-popup-placement.md) | P-01 | A |
| ☐ | P-03 | [Popup 公开 view 与 IsOpen 双向纪律](03-popup-view.md) | P-02 | A |
| ☐ | P-04 | [输入语义：外点吞并+锚豁免、Esc、失活、capture/Up-activation](04-popup-input.md) | P-03 | A |
| ☐ | P-05 | [Select\<T\> 控件](05-select-control.md) | P-04 | B |
| ☐ | P-06 | [Gallery 集成与窄宽/DPI/主题核对](06-gallery-integration.md) | P-05 | B |
| ☐ | P-07 | [文档与交付验收](07-docs-delivery.md) | P-06 | C |

依赖链：P-01 → P-02 → P-03 → P-04 → P-05 → P-06 → P-07（串行）。

## 阶段门槛

- A：overlay 真实挂载/命中/绘制/焦点可验；placement 翻转/clamp/有效可视区成立；
  `Popup` API 与 IsOpen 纪律闭合；外点吞并/Esc/失活/Up-activation 成立。
- B：Select 消费者行为完整（打开/键选/提交/取消/disabled/placeholder/Tab/回调次数/
  限高滚动/ensure-visible）。
- C：Gallery 演示 + 文档；双 config 构建 + 套件 + 真实 GL/窗口 smoke + 交付记录。

## 执行约定

- 每个任务经主审独立复审通过才开依赖任务；返修写 `docs/return/` 带日期文件。
- 永久测试只承诺消费者可观察行为与既有计数器，不 pin 内部字段名/常量字面/源码字符串；
  不引入 wiring/mockecho 测试。
- 范围外：modal/focus trap/dialog/context menu/tooltip/多层 popup、可编辑 ComboBox、多选、
  远程搜索、虚拟化、动画、公共滚动 API、多窗口、**popup 内嵌 popup（嵌套 open
  拒绝 + runtime 诊断，外层保持开）**——不实现、不 stub、不假渲染。
