# R14：滚轮步数被直接当作滚动像素，导致滚动很慢

记录日期：2026-10-15  
审核人：Codex  
审核版本：`715d6217c63ae57b4a6fd8b778e8d4ec34e2d3d5`  
优先级：P2  
状态：已复现，已制定返修要求，准备委派 DeepSeek Harness 实现并独立复审。

## 已确认的直接原因

[OpenTkInputAdapter.cs](../../src/CSharp.UI.OpenTK/Hosting/OpenTkInputAdapter.cs) 的 `HandleWheel()` 将 GLFW 滚轮偏移原样放进 `WheelEvent`。在 Windows 上，GLFW 会将原生 `WM_MOUSEWHEEL` 增量除以 `WHEEL_DELTA`，普通鼠标每格通常得到 `+1` 或 `-1`；它不再是原生的 120，也不是滚动像素距离。

[ScrollView.cs](../../src/CSharp.UI/Views/ScrollView.cs) 第 127—128 行却直接用 `offset - axis` 改变以逻辑像素表示的滚动偏移，没有把滚轮步数换算为滚动距离。因此每格仅移动 1 个逻辑像素，150% 下也只有约 1.5 个物理像素，滚动长页面需要大量滚轮输入。

这与 R13 的 DPI 修复是不同的单位问题：不应靠在滚轮增量上重复乘除 DPI 来提高速度。界面缩放和滚轮步长应分别处理。

依据：[GLFW 3.4 Windows 滚轮归一化实现](https://github.com/glfw/glfw/blob/3.4/src/win32_window.c#L903)。

## 独立复现

使用核心运行时和无窗口文本替身，创建内容高度大于视口的 `ScrollView`，每次发送 `WheelEvent(..., DeltaY: -1)`，布局后读取 `ScrollOffsetY`：

```text
DPI=1: oneNotch=1 logical px; tenNotches=10 logical px; physicalPixels=10
DPI=1.5: oneNotch=1 logical px; tenNotches=10 logical px; physicalPixels=15
```

复现程序位于本地忽略目录 `artifacts/scroll-review/Probe.csproj`；本次已实际构建并运行。

## 现有测试为何没发现

[ScrollImageTests.cs](../../tests/CSharp.UI.Tests/ScrollImageTests.cs) 多数用 `-100`、`-120` 等大增量直接注入核心 `WheelEvent`，自然能产生明显移动，没有覆盖真实适配器普通一格的 `-1` 输入。

当前 DPI 输入测试正确验证了滚轮事件的位置换算、以及滚轮增量不重复乘 DPI，但没有把原生步数一直传递到 ScrollView 并断言合理的最终滚动距离。

## 后续返修要求

- 明确滚轮步数和逻辑像素距离的单位契约，增加合理的步长/行高换算，避免适配层与 ScrollView 重复换算。
- 考虑 Windows 系统滚动设置和可配置灵敏度；保留触摸板等设备的小数增量，不能简单截断成整数。
- 保留横向滚动、边界夹紧、嵌套容器事件传递及 R13 的鼠标位置换算。
- 新增从适配器 `MouseWheelEventArgs(0, -1)` 到 ScrollView 的集成回归，并覆盖小数增量、多个 DPI 和边界行为；不能只使用模拟的 100/120 大增量。

## 性能观察的边界

每次滚动还会调用 `InvalidateLayout(node)`，让滚动内容子树重新测量和排列，大量内容时有额外开销。本轮没有测量帧耗时或掉帧情况，不能把该开销直接认定为已经证实的渲染卡顿。当前已独立确认的是滚动步长过小。

## 实施与验收约定

- 默认滚轮事件表示归一化步数；如需支持逻辑像素输入，必须显式标记单位，不能猜测增量大小。适配器继续保留原始小数步数。
- 核心通过平台无关服务获取滚动设置。Windows 只读查询系统的每格行数和字符数；其他平台或查询失败时回退到 3。系统设置 0 表示禁用，`WHEEL_PAGESCROLL` 表示每格一页。
- 默认每行 16 个逻辑像素、每字符 8 个逻辑像素；提供有限、非负的灵敏度配置。每步距离不超过对应视口大小，再乘实际小数步数，最后夹紧内容边界。
- 当前系统每格 3 行时，一格向下应移动 48 个逻辑像素，四分之一格应移动 12 个；DPI 100%、150%、200% 下逻辑距离一致。
- 覆盖横向及纵向、禁用、整页、超大行数、分数增量、边界及嵌套传递，并保留既有 DPI、文本和输入测试。
- 构建 Debug 与 Release 示例；不引入惯性动画、设备识别猜测、虚拟化或无依据的布局性能改写。

## 实现中提前审核

Codex 在首次构建前发现横向查询常量误写为 `0x0069`（该值是 `SPI_SETWHEELSCROLLLINES`），已立即将其更正为官方文档的 `SPI_GETWHEELSCROLLCHARS = 0x006C`。返修验收应固定核对数值，并确认查询不改变系统设置，不能仅用同一实现的常量作为测试预期。

依据：[Microsoft SystemParametersInfoW 参数表](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-systemparametersinfow)。

---

## R14 实施与验证记录（DeepSeek Harness 追加）

执行模型：`opencode-go/deepseek-flash`。以下命令与结果均为本机实际执行；未修改任何系统设置，未实现动画或虚拟化。

### 对“实现中提前审核”的处理

横向查询常量已按官方文档固定为 `SPI_GETWHEELSCROLLCHARS = 0x006C`（`SPI_GETWHEELSCROLLLINES = 0x0068`），
相邻的 `SPI_SETWHEELSCROLLLINES = 0x0069` / `SPI_SETWHEELSCROLLCHARS = 0x006D` 绝不使用。
回归测试现在直接写死这两个十六进制数值（GET 误用 SET 会立即失败），并用文档字面量动作在服务调用前后各查询一次，
断言系统设置未被改变；不再用实现自己的常量作为预期。

### 单位契约与新增 API（核心不含任何 Windows/OpenTK 类型）

- `WheelDeltaMode`（`Steps` 默认 / `Pixels`）与 `WheelEvent.Mode`：显式声明增量单位。
  `Steps` 是归一化步数（一格为 1，触摸板可为小数），`Pixels` 表示增量已是逻辑像素、不再按行/字符换算。
  适配器继续原样保留 GLFW 的小数步数，不按 delta 大小猜测单位，也不重复乘除 DPI。
- `WheelScrollSettings`（不可变 record struct）：`LinesPerNotch` / `CharsPerNotch`，
  常量 `Disabled = 0`、`PageScroll = uint.MaxValue`、`DefaultNotches = 3`、
  `LogicalPixelsPerLine = 16`、`LogicalPixelsPerCharacter = 8`；
  `StepDistance(horizontal, viewportExtent)` 完成单个滚轮步到逻辑距离的换算并按视口封顶；
  `Default` 为每格 3 行/3 字符，`IsDisabled(horizontal)` 暴露禁用语义。
- `IWheelScrollSettingsService` + `DefaultWheelScrollSettingsService.Instance`：核心只依赖平台无关契约；
  服务挂在 `UiRuntimeServices.WheelScrollSettings`，`ScrollView` 在每次滚轮事件时读取，设置变化即时生效。
- `ScrollView.WheelSensitivity`（`init`，默认 1）：有限非负的灵敏度；`NaN`/`±∞`/负数回退为 1，不会反向滚动。
- Windows 宿主 `WindowsWheelScrollSettingsService`：只读 `SystemParametersInfoW`（0x0068 / 0x006C）；
  0 禁用、`uint.MaxValue` 每页，查询失败或非 Windows 回退 3。`UiApplicationOptions.WheelScrollSettingsService`
  可注入自定义实现（测试或在特殊设备上覆盖）。
- 指南更新：`docs/guide/api-reference.md`（`WheelEvent.Mode`、`WheelScrollSettings`、服务与测试替身）、
  `docs/guide/platform-notes.md` 新增“滚轮单位与系统滚动设置”、`docs/guide/testing.md` 说明步数/像素单位约定。

### ScrollView 行为

`OnWheel` 先按轴取增量（水平轴优先 `DeltaX`，为 0 时回退 `DeltaY`；垂直轴只用 `DeltaY`），
再按单位换算：`Steps` 的单个滚轮步距离 = 系统设置行/字符 × 16/8 并按对应视口大小封顶，然后乘实际小数步数与灵敏度；
`Pixels` 直接使用增量乘灵敏度。最后 `offset - distance` 夹紧 `[0, maxOffset]`，全程保留小数不取整。
内容不超出视口、系统禁用该方向、或已在边界时返回 `false` 不消费事件，交给外层嵌套容器；否则写入偏移、
使布局失效并标记事件已处理。换算只用逻辑像素，DPI 不参与，因此 100%/150%/200% 下逻辑距离一致。

### 本机系统设置（只读核对）

`SystemParametersInfoW(0x0068/0x006C)` 与 `HKCU\Control Panel\Desktop` 均读到 `WheelScrollLines=3`、
`WheelScrollChars=3`，与报告假设一致；因此当前系统下一格为 48 逻辑像素、四分之一格为 12。

### 测试

新增核心 `WheelScrollTests`（26 例）与宿主集成 `WheelScrollIntegrationTests`（7 例，走真实 `OpenTkInputAdapter`
的 `MouseWheelEventArgs(0, ±1)` 路径）；原有像素式滚动测试改为显式传 `WheelDeltaMode.Pixels`，断言未弱化。
覆盖：一格 48 / 0.25 格 12 / 0.1 格 4.8 与多次小数累积、DPI 100/150/200 距离一致、行数 0 禁用（并验证设置恢复即时生效）、
整页、超大行数按视口封顶、上下边界夹紧、嵌套内层到边界后外层接手、水平字符换算与 `DeltaY` 回退、
像素模式 `-1` 只移动 1 像素、灵敏度 2.5 倍/0/`NaN`/`±∞`/负数回退、换算函数的无效视口与默认单位，
以及 Windows 常量的固定数值与“查询不改变系统设置”。

四个测试工程（`dotnet run --project tests/<工程> --no-build`，Debug，串行、顺序构建）：

| 测试工程 | Total | Failed | Skipped |
|---|---|---|---|
| CSharp.UI.Tests | 164 | 0 | 0 |
| CSharp.UI.Text.Tests | 58 | 0 | 0 |
| CSharp.UI.Integration.Tests | 13 | 0 | 0 |
| CSharp.UI.OpenTK.Tests | 144 | 0 | 0 |
| 合计 | **379** | **0** | **0** |

### 构建与冒烟

- `dotnet build CSharp.UI.slnx -m:1 -nodeReuse:false --no-restore`（Debug）与
  `dotnet build CSharp.UI.slnx -c Release -m:1 -nodeReuse:false --no-restore`：均 0 错误；
  仅离线 `NU1900` 与既有 xUnit 分析器警告，无新增代码警告。
- 三个示例 Debug 与 Release 全部重新构建成功；Release 以 `CSHARPUI_SMOKE_FRAMES=5` 启动真实窗口冒烟：

| 示例 | 逻辑视口 | framebuffer | DPI | 退出码 |
|---|---|---|---|---|
| Counter | 480×320 | 720×480 | 1.5 | 0 |
| TodoList | 760×480 | 1140×720 | 1.5 | 0 |
| ControlGallery | 900×700 | 1350×1050 | 1.5 | 0 |

### 未验证项

- macOS / Linux 无真机：`Pixels` 单位与系统滚动设置的实机来源仅由纯计算与测试替身覆盖，不宣称实机通过。
- 未触发系统滚动设置的运行时变更（不修改系统设置）；`IWheelScrollSettingsService` 每次事件读取，理论上可即时反映。
- 触摸板小数增量以适配器同形的 `±0.25`/`±0.1` 事件覆盖，非人工触摸板操作。

### 提交

本次 R14 修复、测试、指南与本文合并为一次本地英文提交，未推送；未暂存 Codex 拥有的
`docs/agents-notebook/README.md`、`docs/agents-notebook/acp/`、`docs/return/2026-10-15-system-dpi-scaling.md`
与 `docs/return/2026-10-15-textfield-caret-alignment.md`。提交 SHA 见 DeepSeek Harness 的交付报告与 `git log -1 --stat`。
