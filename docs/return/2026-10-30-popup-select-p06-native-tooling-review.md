# P-06 原生验收工具审查记录（通过——保留历史返修轮次）

日期：2026-10-30。任务：[P-06 Gallery 集成](../archive/2026-10-31-popup-select-tasks/06-gallery-integration.md)；依据：[计划](../plan/2026-10-29-popup-and-select-plan.md) §8。

本轮 main 对实机验收 harness 的审查记录。**以下问题均属于验收工具（harness）层面，未据此改动产品源码**；`PopupSelectDemo`（samples/CSharp.UI.Samples.ControlGallery/PopupSelectDemo.cs）与产品实现保持冻结。

## 1. 旧 GDI OS 驱动（已归档，未通过）

- `os-driver`（GDI `PrintWindow`/屏幕 `BitBlt` 捕获 + SendInput）一轮实际执行：进程 exit 1、16 步手势期间应用未崩溃、但 `FAIL theme-darkened` / `RESULT FAIL (1)`。
- 全部 16 张 BMP 的 SHA256 相同；这一事实尚不能区分采集未刷新与输入未命中，不能据此判定 GDI 通道失效；该轮不算实机验收通过，FAIL 原样保留。
- 16 张 BMP 原字节已无损打包 `legacy-os-gdi-bmp.zip`（逐成员解压 sha256 校验一致，清单 `legacy-os-gdi-bmp.sha256.csv`），5 张关键步骤转 PNG 可读。见 [交付证据 README](../delivery/2026-10-30-popup-select-delivery-evidence/README.md)。

## 2. 键盘与断言审查发现（harness 层面）

- **弱断言**：Select 键盘提交后预期值原写为"标准模式"——恰是 demo 初始值（`popupSelectMode` 初始 `"标准模式"`），不改值也通过。必须断言**变化后的精确值** `"紧凑模式（窄屏）"`。
- **End/方向键**属扩展键，`SendInput` 需 `KEYEVENTF_EXTENDEDKEY`（0x0001）否则真实键盘事件与预期不符。
- 输入只允许发往**自己启动的 HWND**（own PID 前台窗口）；`SendInput` 返回计数要逐次核对，不算交互证据但可用于定位事件是否进入队列。
- 长列表提交：部分可见的行应按 **row ∩ clip（可视裁剪）中心**取点；末项必须断言精确值 `选项 30 · 长列表项标签`（含"30"）且选后 popup 关闭。

## 3. 原生 PNG 解码与已确认正向事实

- main 初次实际读取 native PNG 失败：harness 自写 PNG 用裸 `DeflateStream`，未带 zlib 封装头；改 `ZLibStream` 后解码通过。
- 修复后 main 实际读取 `native-00`、`native-06`、`native-07`、`native-09` 成功，均为真实 1350×1050 帧（DPI 1.5）：
  - `native-00`：初始浅色主题页面；
  - `native-06`：30 项长列表 End 后**末项高亮可见**；
  - `native-07`：鼠标点击末项后 trigger 回显 30；
  - `native-09`：普通 Select 已改"紧凑模式"回显、通用 Popup TextField 文本 `portal-ui` 与外部绑定回显一致。
- **来源边界**：这些正向事实来自 good 的 instrumented 宿主——真实 `GalleryApp` + 生产 `OpenTkHost`（`UiApplication.Run`，仅附加 `Started`/`FrameRendered` 观察回调，不加 test-only hook）+ `GL.ReadPixels(FRONT)` 读回，**不是原始 ControlGallery.exe 本身的捕获，也不是 legacy GDI 通道被修复**。两类证据不得混称。
- 截图文件当前在外 Temp `native-observed/out/native-*.png`，本记录只记 basename；待冻结进 `docs/delivery/2026-10-30-popup-select-delivery-evidence/` 后由交付收口补正式相对链接。

## 4. 当时待完成项（历史——现已全部通过）

> 以下 §4 为**当时的 pending 清单**，现已逐项闭合（见下方“最终通过”）：

- ~~**E1 底部翻转**~~：`native-05..07` 长列表弹层位于 trigger 上方——已由真实帧证明。**（历史记录，原样保留）**
- ~~**E4 内层滚动锚**~~：已由 `native-independent.stdout.txt` inner wheel `0→24 随动、24→216 裁空关` 证明；**外层**进一步由 `native-outer/`（`outer off 2112→2124`、`anchorΔY==popupΔY=-12`、滚出 `anchor top=-45` 裁出关闭）证明。
- ~~**E2 宽度**~~：`native-widths/{320,1024}-{light,dark}` 4 run 已实证 requested-width / fb `W×1.5`。
- ~~**E5 fast 独立复跑**~~：fast 独立 native900 47 PASS 0 FAIL。

**最终通过事实**（2026-10-30 主审 raw 验收）：native900 47 PASS 0 FAIL、width 4 run 全 PASS、outer scroll 跟随/裁出关闭 PASS、双配置 Debug/Release 各 1689 项 0w0e。交付记录：[2026-10-30-popup-select-delivery](../delivery/2026-10-30-popup-select-delivery.md)；证据：[delivery-evidence](../delivery/2026-10-30-popup-select-delivery-evidence/README.md)。

## 关联

- 任务与计划：[P-06](../archive/2026-10-31-popup-select-tasks/06-gallery-integration.md)、[计划](../plan/2026-10-29-popup-and-select-plan.md)、demo 源 `samples/CSharp.UI.Samples.ControlGallery/PopupSelectDemo.cs`。
- 证据目录（旧 GDI 轮 + GL 轮）：[2026-10-30-popup-select-delivery-evidence](../delivery/2026-10-30-popup-select-delivery-evidence/README.md)。


## 5. 真实宿主 Esc 穿透（已修复并验收——历史 bug 轮次保留）

- good 原生实机 observed 轮：`popup-esc-closed` 与 `popup-isopen-false` 均 PASS，但**同一次 Esc 同时导致 `UiApplication.Run` 返回 exit 0**——Esc 在关闭弹层后继续穿透到宿主 `ExitOnEscape`，postEsc `app-alive` 失败。原 raw：`../delivery/2026-10-30-popup-select-delivery-evidence/native-esc-before/`（**RESULT PASS 但 `host-exited=True`——假绿历史，字节保留，未改成 rawFAIL/exit1**）。
- 主审拒绝把 app-alive 移到 Esc 之前迁就结果——保持 postEsc 应用仍活的验收顺序。
- 期望语义（已落地）：只有无 popup 且 Esc 未被任何节点消费的 `KeyEventKind.Down` 才执行 `ExitOnEscape`；popup 打开时 Esc 归 popup 关闭、不再退出；第二次无 popup 的 Esc 退出仍独立验证。
- 已实施并验收（见 §下根因）：`OpenTkInputAdapter.DeliveredCapturedCore` 退出条件加 `key.Kind == KeyEventKind.Down`；永久回归 `OpenTkInputAdapterTests.Esc完整DownUp关闭popup不退出_第二次无popupEsc才退出`（真实 UiRuntime+Popup+adapter 完整 Down/Up）；native 复验 `app-alive-after-esc` PASS、`host-exited=False`。
- 边界：修复前 Debug/Release 1688 与 GL 为旧源码快照（raw 未删）；当前生效为修复后 **1689**。

### 已定位源码根因（main 已 read）

- `src/CSharp.UI.OpenTK/Hosting/OpenTkInputAdapter.cs:559-579`：`DeliverCapturedCore` 的默认 Esc 退出条件**未限定 `KeyEventKind.Down`**——第一次 Esc Down 由 popup 消费关闭弹层（handled），随后同一次 **KeyUp** 在无 popup、未消费时 `handled=false`，误执行 `Exit`。
- 修复（已实施并验收）：只在未消费的 **`KeyEventKind.Down`** 上执行既有 Esc / Ctrl(Cmd)+Q 退出路径；保留 FIFO 顺序与 IME 标志语义，不新增抑制状态/时间窗。
- 永久回归（已落地）：`OpenTkInputAdapterTests.Esc完整DownUp关闭popup不退出_第二次无popupEsc才退出`（真实 UiRuntime+Popup+adapter 完整 Down/Up）；native `app-alive-after-esc` PASS、`host-exited=False`。
- **最终状态：通过**（历史返修轮次原样保留）。交付：[2026-10-30-popup-select-delivery](../delivery/2026-10-30-popup-select-delivery.md)；提交信息见 git 历史。