# PS-04 Gallery 真实集成与原生实证

依据：[plan](../plan/2026-10-31-panel-sidebar-plan.md) §6/§8。
前置：PS-02、PS-03（主审通过）。状态：**主审通过**——self 四组原生矩阵全 PASS
（1024-light 19 / 1024-dark 21 / 320-light 19 / 320-dark 21 项 `^PASS ` 检查）、
独立复跑 1024-dark 21 项检查 + Down/Space/Shift+Tab 扩展 23 项检查、真实
EXIT=0、`host-exited=False`；证据归档
[delivery-evidence/native-self 与 native-independent](../delivery/2026-10-31-panel-sidebar-delivery-evidence/)，
复核记录见 [2026-10-31-gallery-review](../return/2026-10-31-gallery-review.md)。

## 目标

`CSharp.UI.Samples.ControlGallery` 真实接入 `Panel` 与 `Sidebar<T>`，覆盖
窄宽/DPI/主题/滚动/真实输入场景，作为消费者级功能演示与原生验收面。

## 改动

- `samples/CSharp.UI.Samples.ControlGallery/` 新 Demo 文件（沿用
  `PopupSelectDemo.cs`/`NestedResponsiveExample.cs` 独立文件模式）+
  `Program.cs` 新 `Section("面板与导航")` 接入。
- 页面结构：`HStack`/`Flex`/`AdaptiveStack` 组合 sidebar + 内容区；
  `Panel` 作内容区装饰容器；持久 `State` 外持于 Demo 组件（外层状态模式），
  不新建 Router/Shell/Drawer。
- 窄宽分支用既有 `Responsive`/`AdaptiveStack` 表达（如窄宽时 sidebar 置顶或隐藏），
  不做控件内置折叠。
- `Sidebar` 超高内容外裹调用方 `ScrollView`；面板色/边框按传入 `Theme`。
- 静态演示数据 `private static readonly` 不可变（沿用 Popup demo 纪律）。

## 原生实证（own PID/own window，禁止按名称 kill）

- 真实 Release `GalleryApp` + 生产 `OpenTkHost`（`UiApplication.Run`，仅附加
  `Started`/`FrameRendered` 观察回调）；owned-HWND SendInput 真实
  MouseUp/键盘/滚轮驱动；记录自有进程 PID 与窗口句柄，清理只用自有句柄。
- 截图：既有 ZLib/`GL.ReadPixels(FRONT)` 通道；**不用 GDI 采集通道**。
- 覆盖矩阵：逻辑宽 320 / 1024 × light / dark × DPI 1.5：
  - sidebar + panel 布局渲染正确、宽度偏好/收缩表现；
  - 真实 pointer Up 提交选中（回显变化）；
  - 键盘 `Up`/`Down`/`Enter`/`Space`/`Tab`/`Esc`（若涉及）真实按键路径；
  - 调用方 `ScrollView` 内 active 移动 ensure-visible；
  - light/dark 经真实主题按钮切换。
- raw（PNG + driver stdout + 冻结 harness 源）存
  `docs/delivery/` 下按交付实际日期命名的 evidence 目录（PS-05 统一收口）。
- macOS/Linux 只承诺源码级一致路径，不虚报运行结果。

## 验收

- D1：Gallery `Section("面板与导航")` 可见可用，demo 源冻结存证。
- D2：原生矩阵 raw 齐全且结论与断言一致（PASS/FAIL 如实记录；失败 raw 保留不删）。
- D3：`host-alive`/`host-exited=False` 式存活断言（沿用 popup 实证模式）。
- D4：无范围外产物（drawer/modal/路由/折叠/图标/动画）。
- 主审独立复审通过后开 PS-05。
