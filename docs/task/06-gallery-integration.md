# P-06 Gallery 集成与窄宽/DPI/主题核对

依据：[plan v3](../plan/2026-10-29-popup-and-select-plan.md) §8。
前置：P-05。状态：待授权实施。

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

## 验收

- E1：Debug build 0w0e；Gallery 真实窗口打开 Select：下方弹出、底部翻转、
  外层滚动时跟随、外点关、Esc 关、选后回显。
- E2：320 宽 viewport 弹层不出屏；1024 正常；DPI 144 命中/显示一致。
- E3：Light/Dark 主题切换 popup 颜色可读（popup 继承锚环境——含 ThemeView 场景）。
- E4：内层 ScrollView 内 Select：打开后滚外层/内层——锚出视口即关；锚部分可见跟随。
- E5：fast 独立真实 OS 验收（SendInput 物理输入，参照 scrollbar 轮证据模式：
  打开→Move 到 item→Up 提交→画面断言；外点吞并截图）。
- 验收证据入 `docs/delivery/2026-10-29-popup-select-delivery-evidence/`（命名按交付时
  实际日期目录）。
