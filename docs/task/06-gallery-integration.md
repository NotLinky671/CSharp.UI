# P-06 Gallery 集成与窄宽/DPI/主题核对

依据：[plan v3](../plan/2026-10-29-popup-and-select-plan.md) §8。
前置：P-05。状态：**主审验收通过**（详见 [P-06 原生验收工具审查记录](../return/2026-10-30-popup-select-p06-native-tooling-review.md) 与[交付记录](../delivery/2026-10-30-popup-select-delivery.md)）。

## 目标

`CSharp.UI.Samples.ControlGallery` 真实接入 Select，覆盖窄宽/DPI/主题/滚动内锚定场景，
作为消费者级功能演示与人工验收面。

## 改动

- `samples/CSharp.UI.Samples.ControlGallery/Program.cs` 基础控件 Section（约 :84-107）：
  - 一个 `Select<string>`（如字号/主题选项，绑定现有或新增 `State`）；
  - 一个 disabled `Select` 示例；
  - 复用 `AdaptiveStack(FormBreakpoint, label, control)` 表单行模式；
  - 一个处于内层 `ScrollView` 内的 Select（验证锚滚动跟随/出视口关闭，
    可放"滚动容器"Section 内）。
- 不加新 csproj 依赖；不改样例资产。

## 验收（已执行，原始证据如下）

- **E1（下方弹出/底部翻转/外层滚动跟随/外点关/Esc 关/选后回显）**：
  真实 Release `GalleryApp` + 生产 `OpenTkHost`（仅 `Started`/`FrameRendered`
  观察回调包装，**非原始 ControlGallery.exe**）owned-HWND `SendInput` 驱动。
  翻转由 `native-independent/native-05..07`（长列表弹层位于 trigger 上方）
  证明；**外层滚动跟随**由 `native-outer/`（小 delta 使
  `outer off 2112→2124`、`anchorΔY==popupΔY=-12` 非零、popup 仍开、binding
  不变）证明；外点/Esc 关与选后回显见 `native-independent.stdout.txt`（47 PASS）。
  原生 900 47-case raw：`native-independent.stdout.txt`。
- **E2（320/1024 不出屏 + DPI 命中一致）**：`native-widths/{320-light,
  320-dark,1024-light,1024-dark}/driver.stdout.txt` + 各 PNG——requested-width
  命中、PixelScale 1.5、framebuffer `W×1.5`（480/1536×1050）、popup `Frame` 在
  `[4,W-4]` 与视口内、命中/显示一致。
- **E3（Light/Dark 主题可读）**：`native-widths/*-dark` run 经**真实 Header
  主题按钮 `SendInput` 点击**（非 State 写）切 dark，`theme-toggled` PASS；
  `native-widths/{320-dark,1024-dark}/native-02-select-open.png` 深色弹层
  文字/高亮可读（弹层打开态真实帧，非关闭后截图）。
- **E4（打开后滚外层/内层）**：**外层** `native-outer/`（滚出根视口
  `anchor top=-45` 不相交 → `overlay-closed-on-clipout`、binding 不变、
  host-alive）；**内层** `native-independent.stdout.txt` inner wheel
  `0→24 随动、24→216 裁空关`。
- **E5（真实 fast 独立复跑）**：fast 独立 native900 47 PASS 0 FAIL（
  `native-independent.stdout.txt`），debug/release 各 1689 项 0w0e。
- 证据目录（按交付实际日期）：[docs/delivery/2026-10-30-popup-select-delivery-evidence/](../delivery/2026-10-30-popup-select-delivery-evidence/README.md)。
