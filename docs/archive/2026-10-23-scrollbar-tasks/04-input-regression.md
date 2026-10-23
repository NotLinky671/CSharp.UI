# SB-04：图形/输入回归（hit 优先/嵌套冒泡/边界清理/IME）

创建日期：2026-10-23

状态：已完成（消费方迁移全绿：GalleryBreakpointInput/DpiLayout/GpuIntegration/Responsive/IdleAndBounds/HostResize/TextSampling 全部经 `GalleryScrollLookup` 按 `Key` 定位；真实 GL 覆盖绘制/真实窗口 IME/极端 DPI 验收由 fast 独立通过，GPU 78/78 + Gallery 32/32 + IMM exact 24/24）。证据见 [交付记录](../../delivery/2026-10-23-scrollbar-delivery.md) 与 [审查记录](../../return/2026-10-23-scrollbar-review.md)。

阶段：C

设计依据：[Scrollbar 计划](../../plan/2026-10-23-scrollbar-plan.md) §决策四~六。

[返回任务索引](README.md)

前置依赖：SB-02、SB-03。

## 目标

对 SB-02/03 已完成的接线做**独立**图形/输入回归——不补半成品边界，验证 hit 优先级、嵌套 wheel 冒泡、outside-drag 持续、禁用/卸载安全、IME anchor 移动等消费者可见行为。

## 文件范围

- `tests/CSharp.UI.Tests/`（`TestApp`/`StubTextService`）与 `tests/CSharp.UI.OpenTK.Tests/`（真实 GL）：针对 ScrollView/TextEditor scrollbar 的集成/图形测试。
- 不改产品代码（发现问题走 return 返修）。

## 验收场景（冻结，均为消费者可见）

- **Hit 优先**：pointer 落 gutter → 落 owner（bar），不进内容 caret/drag-select；persistent gutter 区在无 overflow 时命中 owner（空白槽不透到内容）。
- **嵌套 wheel 冒泡**：scrollbar 区 wheel 仍走 owner wheel 路径（可滚时 owner 消费；`maxOffset<=0` 或到边界才冒泡外层，同既有 ScrollView/TextEditor 冒泡）。
- **Hover 过渡**：pointer 在 bar→hover 高亮（`Emit` 按 `LastPointerPoint`+`HoveredNode` 重算）；移入 child（child 消费 move）/出 window→`HoveredNode!=owner`→`None`——不滞留在最后一次 move 的命中。
- **Outside-drag**：thumb drag 在窗口内移出 owner 边界后继续（`_captured` 保证）→ release/cancel 才停；cancel 不滚回。**窗口失焦/MouseLeave→`PointerEventKind.Cancel`→捕获释放**：不承诺跨 OS 窗口拖出后继续（超出既有 host 能力）。
- **Disable**：`IsEffectivelyDisabled` 下 `OnPointerDown` 短路；disabled 中 `ValidateInteractionState` 释放捕获；恢复后可用。
- **Unmount**：node 卸载 → `ResetInteractionState` 清 bar 状态；无悬挂捕获（`ValidateInteractionState` 兜底）。
- **IME**：`ScrollY` 变→`PublishTextInputGeometry` 重发布→anchor 几何跟随；bar 交互不打断 `IsComposing`。
- **Scroll-bounce 回归**：`OuterScroll_BubbledWheel_DoesNotBounceScrollYBackToCaret` 仍绿（persistent gutter 槽恒占，`EnsureGeometryKey` 不变——bar 出现/消失不触发 ensure，非新回跳路径）。

## 边界

- 不新增测试 wiring/mockecho/源码字符串断言；不测试内部转发。
- 真实 GL 覆盖绘制（thumb 位置/颜色/最小尺寸），CPU 覆盖 hit/offset/状态机。

## 验收

- 上述场景逐项断言通过；594+85+53+678 基线绿；无新增 flaky（outside-drag/cancel 路径确定性）。
