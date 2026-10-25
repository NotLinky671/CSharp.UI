# Floating 演示视口宽度修复记录

日期：2026-10-25
关联：[Floating 交付记录](../delivery/2026-10-23-floating-scrollbar-delivery.md)、[任务索引](../archive/2026-10-25-floating-scrollbar-tasks/README.md)（FS-01~FS-07 已验收，已归档）。本记录是交付后用户反馈的最小样例修复；证据目录 [2026-10-25-floating-demo-width-evidence/](2026-10-25-floating-demo-width-evidence/)。

## 用户反馈（ground truth）

用户截图（287×200，画廊 w320 附近）：Floating 模式下“滚动条”演示区第 21~25 行，灰色 thumb 直接压在第 23 行末尾文字上（约 x200）。用户判定“Floating 有问题”。先前 2026-10-23 交付的 `gallery-w320/w1024-floating-shown.png` 已可见同一缺陷（thumb 落在“纵向滚动第 X 行”行尾字形上），上轮审查漏判。

## 根因（源码 + 实测帧，非推测）

框架 Floating 契约本身正确：`ScrollbarStyle.Floating` → `GutterExtent()=0`，轨道为 `frame.Right−12` 的 overlay 条、4px thumb 居中画在 `frame.Right−8..−4`，按设计覆盖内容右缘。

实际缺陷在样例布局：`scrollbar-vertical-demo` 的 ScrollView 只声明 `.Frame(height:)`，没有宽度声明。本场景的实际测量链：

1. `.FillWidth()` 向父级声明占满交叉轴可用宽（`Flex.cs` `FillWidthView` 既有契约），外框获得区块可用宽——实测 FillWidthView 外层已拿到 948；但 FrameView 默认 `alignment: Center` 仍按子 `DesiredSize` 收缩内部排布。
2. 真正的收缩点在 `FrameView`：其默认 `alignment: Center`，Arrange 时用 `bounds.AlignInside(child.DesiredSize, Center)` 把自然宽 118.75 的 ScrollView **居中**放进已分配的宽度里。修复前实测 `ScrollView frame=(446.625, …, 118.75×120)`（≈32+(948−118.75)/2 居中），`frame.Right−8` 的 thumb 正好落在每行文字末尾。
3. Standard 模式没暴露同一窄视口，是因为 persistent 12px gutter 是空白轨道槽，把 thumb 推离字形；Floating 无槽直接贴内容右缘，窄视口缺陷立刻可见。

横向演示 `.Frame(height: 56)` 有完全相同的 Center 居中问题。

## 修复（最小 scope，4 行）

`samples/CSharp.UI.Samples.ControlGallery/Program.cs` 滚动条演示区内两个 ScrollView：

- `.Frame(height: …, alignment: Alignment.Stretch)` —— 让视口填充分配宽度而不是居中收缩；
- `.FillWidth()` —— 向父级声明占满交叉轴可用宽（`Flex.cs:111-145` 的既有契约），保证 FrameView 拿到的是区块可用宽而不是自然宽。

没有：Flow 包装（中间试验，证实是重复 wrapper，已回退）、Section helper 全局改动、根/嵌套 ScrollView 改动、框架默认/几何/overlay 合约改动、内容 padding 症状修补、新公开 API。TextEditor 演示既有 `.FillWidth()` 未动。

## 证据（本轮，旧交付 PNG/hash 未改）

- **真实 GL 离屏四场景**（fast 独立验证，final：[2026-10-25-floating-demo-width-evidence/](2026-10-25-floating-demo-width-evidence/) 的 `final-fsdemo-run.txt` 90 PASS / 0 failures，`final-gallery-{w320,w1024}-{light,dark}-{standard,floating-shown,floating-hidden}.png` 与 `final-crop-*-shown.png` 全部留存）：w320/w1024 × light/dark 真实 `SetEnvironment` + 节点 resolved-theme 断言——视口占满区块可用宽（w320 244 / w1024 948）、Standard gutter 12 保持、Floating 无槽不回流、滚轮后 bar 可交互、thumb 贴视口右缘（268/972）、可见文本墨迹右缘（138/139）与 thumb 左缘有真实间隙、中性灰、隐藏无回流、横向演示占满、切回 Standard 恢复槽。主审已 read `final-crop-w320-light-shown.png` 与 w1024-dark 全图确认行尾遮挡修复；中期一版含 Flow 包装的中间二进制结果存 `interim-flow/`（非 final，不作为结论证据）。
- **几何探针**（修复前后对照）：修复前 `ScrollView frame=(446.625,1267.664,118.75×120)`（Center 居中于 948 分配宽内）；修复后 `frame=(32,1267.664,948×120)` 与 FillWidthView/FrameView 同宽。
- **永久消费者回归** `tests/CSharp.UI.OpenTK.Tests/GalleryScrollbarDemoLayoutTests.cs`（真实 `GalleryApp` + `GalleryIntegrationHost` + 真实 `SkiaTextService`/`OpenTkInputAdapter` 路径，320/1024）：演示视口在 Standard 与 Floating 下 `frame.Right` 等于区块内 Wrap 说明文本右缘（可用宽真实代理，不 pin 窗口字面值）；Floating 实际滚动后 `HitTestOverlay` 返回非空 overlay、其中心 `HitTest` 命中演示 ScrollView、可见短行字形右缘严格在 overlay 左缘之左（真实空隙，不 pin 4px 常量）；横向演示同样占满。断言面向 frame/glyph/真实交互合约，不断言 `.FillWidth` 属性、wrapper 结构或内部状态位。

## 边界声明

- Floating 是 overlay：不保证任意长满宽内容永远不与滑块叠盖；本轮修复的是示例视口收缩到内容自然宽导致短行字尾被挡的场景。
- 修复依赖 `FrameView alignment: Stretch` + `.FillWidth()` 组合在本场景 Arrange 期生效（实测帧证实）；未改框架 intrinsic 测量/弹性语义。
- 注入级别：测试经 `Runtime.DispatchWheel`/`DispatchPointer` 事件注入；fast 截图为真实 OpenGL 离屏渲染（viewport 320/1024 逻辑像素、pixelScale 1），非实机多 DPI。未在 macOS/Linux 构建；本轮未做原生 IME 复验。

## 检查（本轮执行过）

- 测试工程 fresh build：Debug/Release 0 警告 0 错误（`final-build-opentk-tests-{Debug,Release}.*.txt`）。
- `CSharp.UI.OpenTK.Tests` 全套：Debug 684/684、Release 684/684，0 失败 exit 0（`final-suite-opentk-{Debug,Release}.*.txt`，含本类新 6 例；收口断言——HitTestOverlay 真实合约 + root gutter 滚轮——在同一 frozen 二进制上通过；本类单独复跑见 `testfix-newclass-debug.txt`，配套构建日志 `testfix-build-debug.txt`）。
- fast GL 四场景独立视觉验证（final，`final-fsdemo-run.txt` 90 PASS / 0 failures，`final-gallery-*`/`final-crop-*` PNG 全存）。
- ControlGallery 样例 fresh build + 真实窗口 smoke：Debug/Release 0 警告 0 错误 + 5 帧窗口 smoke exit 0（`final-build-gallery-*`、`final-smoke-*` 日志）。
- DLL sha256：ControlGallery.dll `0d1775a7…`（修复后，fast 核证与源 `6eb4627e…` 一致）；框架 CSharp.UI.dll `9f5b59fe…`（未改）。
