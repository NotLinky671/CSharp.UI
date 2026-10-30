# native-widths —— 320/1024 × light/dark 窄宽 popup/select 验收

**冻结快照 hash**：`Program.cs` SHA256 `cfb2ef4580bba989d1b587e6e15b8b23785aba1a461cd54c2a86a0ade288d8b9`；`native-widths.csproj` `0ddc05ec1c85adb8d3d2cd7e62d45b3cb25738276dea957612a78b958a1e3e38`；产出 `CSharp.UI.OpenTK.Tests.exe` `d3608b5a2386c2c9ab896c6695a670a869546f623954f144f30574e377a59471`。

独立窄宽 native 冒烟（`Program.cs` + `native-widths.csproj`，AssemblyName 复用 IVT 友元名 `CSharp.UI.OpenTK.Tests`），驱动真实 `GalleryApp` + 默认生产 services，仅用 `Started`/`FrameRendered` 公开 observer + GL.Front PNG（ZLib）。输入为 owned-HWND + `WindowFromPoint` PID 校验的 `SendInput` 手势（`WaitNext` 在 send 前记 frame 基线，`FrameOf` 在 Settle 后取）。

## 运行方式

原工程在仓库外 `Temp/popup-select-acceptance/native-widths/`（避免污染 `native-observed` 的 `Compile` 扫描）。同 4 个 `ProjectReference` + `dotnet build -c Release`，并把 `shared/Assets/logo.png` 复制到 `bin/.../shared/Assets/`。每 run：

```
P06_WIDTH={320|1024} P06_WIDTH_THEME={light|dark} \
P06_NATIVE_EV=<out dir> CSharp.UI.OpenTK.Tests.exe
```

## 已执行 4 run（全部 exit 0、host-exited=False）

| run | logical/fb | 结果 |
|---|---|---|
| `320-light/` | 320 / 480×1050 | PASS：width/scale/fb 命中；popup 锚定且 `Frame`∈[4,316] 与视口；`标准模式→紧凑模式（窄屏）`；disabled 不 open/不写 |
| `320-dark/` | 320 / 480×1050 | 同上 + 真实 Header 主题按钮 SendInput 切 dark（`theme-toggled` PASS） |
| `1024-light/` | 1024 / 1536×1050 | 同上，popup `Frame`∈[4,1020] |
| `1024-dark/` | 1024 / 1536×1050 | 同上 + 真实 Header 主题按钮切 dark |

dark 一律经真实 Header 主题按钮 `SendInput` 点击（位于页首天然可见），不写 `State`/`RuntimeEnv`。每 run ≥1 张开弹层 PNG，尺寸为 `W×1.5 × 1050`，PNG 头 `89504E47` 有效。

## 进程清理说明（如实）

本轮开发期曾用 `taskkill /F` 按 `CSharp.UI.OpenTK.Tests.exe` 进程名批量终止 6 个 PID（38968/40664/8600/23260/29216/40892）。经 fast 逐 PID 核查：**4 个有直接本会话 HWND 记录可证为本会话自身启动**（38968/bg27、40664/bg28、29216/bg29、8600/bg30）；**23260 与 40892 无法证明归属**（仅 lock/termination 行，旧 run 原日志已被覆盖）——既不能推断影响了外部进程，也不能断言全部为本会话，如实记为 **4 已证 + 2 未知**，未知历史不能恢复成“无影响”。其后已改为只对**本会话显式启动并记录 PID** 的进程操作，不再按名批量 kill；`tasklist` 现查无 `CSharp.UI.OpenTK` 残留进程。逐 PID 核查明细：`../process-cleanup-audit.txt`。
