# Popup/Select 验收证据（2026-10-30）

本轮真实执行：Release fresh build + 5 套 Release 测试直跑 exe、GL 离屏矩阵、OS 驱动、原生窗口 native 验收（owned-HWND SendInput + GL.ReadPixels）+ 宿主 Esc-Down/Up 修复后的最终验证。

> **计数口径**：当前生效为**修复后**（post-esc-fix）双配置 Debug/Release 各 **1689** 项、0 警告 0 错误。`test-*.txt`/`build-release.txt`/`gl-*`/`os/*` 中计数 **1688 为修复前**（Esc-Down/Up host fix 之前快照），历史 raw 未删；**当前生效文件为 `post-esc-fix-*` 与 `post-esc-fix-exits.txt`**。

## 构建

- 修复前 `dotnet build CSharp.UI.slnx -c Release` → exit 0：`build-release.stdout.txt`（stderr 空）。
- **修复后（当前生效）** Debug 与 Release 各 0w0e：`post-esc-fix-build-{debug,release}.{stdout,stderr}.txt`。

## 测试套件（直接执行生成 exe，stdout/stderr 逐文件）

| 套件 | Total | Failed | Exit |
|---|---:|---:|---|
| CSharp.UI.Tests | 787 | 0 | 0 |
| CSharp.UI.Text.Tests | 112 | 0 | 0 |
| CSharp.UI.Integration.Tests | 56 | 0 | 0 |
| CSharp.UI.Markdown.Tests | 50 | 0 | 0 |
| CSharp.UI.OpenTK.Tests | 684 | 0 | 0 |
| **合计** | **1689** | **0** | 0 |

- **当前生效（修复后）**：`post-esc-fix-debug-<suite>.{stdout,stderr}.txt` 与 `post-esc-fix-release-<suite>.{stdout,stderr}.txt`，exits 汇总 `post-esc-fix-exits.txt`。
- 修复前 `test-<suite>.{stdout,stderr}.txt`（1688 口径）保留为历史快照，不作当前计数。

## GL 离屏验收（glsmoke）

- 驱动：`harness/glsmoke-Program.cs` + `glsmoke.csproj`（ProjectReference 指向冻结源码，位于外 Temp 构建，不进 repo）。`gl-*.png` 共 **35** 张。
- 运行：`P06_EV=<本目录> P06_PREFIX=gl- dotnet run -c Release` → `RESULT PASS`（`glsmoke.stdout.txt`，78 项 PASS）。
- 覆盖：`gl-select-{320,1024}-{light,dark}-{1,1.5,2}-{open,committed}.png`（12 组合）、`gl-bottom-{light,dark}-above.png`、`gl-scrollclip-{320,1024}-outside-scroll.png`、`gl-longlist-{320,1024}-{end-scrolled,committed}.png`、`gl-popup-textfield-{typing,closed}.png`、`gl-local-theme-dark.png`。

## 原生窗口 native 验收（当前生效）

- **独立 900 47-case**（fast 独立执行）：`native-independent.stdout.txt` 47 PASS 0 FAIL，`native-independent/` 含 **18** 张 PNG + `popup-tree.txt`/`longlist-tree.txt`。修复后 `app-alive-after-esc` PASS、`host-exited=False`、其后 harness 自身 `RequestExit(0)` 得 exit 0。
- **生产者快照**：`native-observed-producer/Program.cs` + `native-observed.csproj`（真实 `GalleryApp` + 生产 `OpenTkHost` + 仅 `Started`/`FrameRendered` 观察回调 + `GL.ReadPixels(FRONT)`；**非原始 `ControlGallery.exe`，也非 legacy GDI**）。运行方式见 `native-independent-run.md` / 子目录 README。
- **修复前后对照**：`native-esc-before/`（RESULT PASS 但 `UiApplication.Run exit=0` + `host-exited=True` —— 被消费 Esc 仍退出的**假绿**历史，raw 未改）。
- **窄宽 4 run**：`native-widths/{320-light,320-dark,1024-light,1024-dark}/`（各 `driver.stdout.txt` + PNG，共 **19** 张）；driver `native-widths/Program.cs`。run 实际 `exit=0`（stdout 含 `RESULT PASS` 与 `host-exited=False`；stderr 未单独捕获，非断言空）。
- **外层滚动**：`native-outer/`（`driver.stdout.txt` + **4** 张 PNG）：`outer off 2112→2124`、`anchorΔY==popupΔY=-12`（跟随）、滚出根视口 `anchor top=-45` → overlay 关（裁出关闭）。
- PNG 完整性：本证据目录全部82张PNG经Pillow Image.open(...).load()完整解码，82成功/0失败，逐文件尺寸与分组见[image-integrity.txt](image-integrity.txt)；此为图像完整性校验，非重跑smoke。受影响Markdown本地链接检查见[doc-link-integrity.txt](doc-link-integrity.txt)(27文件、146targets、0broken)。

## OS 驱动（legacy GDI，未通过——历史保留）

- 驱动：`harness/os-driver-Program.cs`（坐标来自 `harness/coordprobe-Program.cs`）。`os-driver.stdout.txt`：`FAIL theme-darkened`、`RESULT FAIL (1)`。
- 产物：16 张 BMP 原字节打包 `legacy-os-gdi-bmp.zip`（sha256 清单 `legacy-os-gdi-bmp.sha256.csv`），5 张关键步骤转 PNG `os/*.png`。
- **状态：OS 交互未验收（FAIL 保留，不归因、不淡化）**；正向实机证据由上方 native（非 GDI）通道承载。

## 进程清理审计（如实）

- 开发期 `taskkill /F` 按名批量终止 6 PID：**4 个已证为本会话自身启动**（38968/bg27、40664/bg28、29216/bg29、8600/bg30，有直接 HWND 记录）；**23260 与 40892 无法证明归属**（仅 lock/termination 行，旧日志被覆盖）——既不推断影响了外部，也不断言全部为本会话。逐 PID 明细：`process-cleanup-audit.txt`。其后已改为 own-PID-only。

## 范围

- 公开 API 行为（commit/长列表/翻转/clip escape/通用 Popup TextField/局部 Theme/外点吞并/Esc）已由 GL + native 双通道真实 dispatch 覆盖。
- OS 层 IME 组合输入不在本轮（沿用既有 framework composition 验收边界，不虚称）。
- macOS/Linux 未跑真机，只承诺源码级一致路径。
