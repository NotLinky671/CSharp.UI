# native-observed-producer —— 900×700 全量 popup/select native 驱动（生产者快照）

`Program.cs` + `native-observed.csproj` 是跑出本交付**独立 native 900** 证据（`../native-independent/`）的生产者源码快照，按真实文件名/结构保存以便复现。AssemblyName `CSharp.UI.OpenTK.Tests`（IVT 友元名）；`UiApplicationOptions.Width=900 / Height=700`，`GalleryApp`，默认生产 services，owned-HWND `SendInput` 驱动 + `Started`/`FrameRendered` 公开 observer + GL.Front PNG。

原工程在仓库外 `Temp/popup-select-acceptance/native-observed/`。可复现：同 4 个 `ProjectReference`（`CSharp.UI` / `CSharp.UI.Text` / `CSharp.UI.OpenTK` / `CSharp.UI.Samples.ControlGallery`）+ `dotnet build -c Release`，并把 `shared/Assets/logo.png` 复制到 `bin/.../shared/Assets/`；`P06_NATIVE_EV=<out>` 指定证据目录。

## 关键契约（本快照内含）

- `WaitNext` 在 `Mark()`（send 前取 `RenderedFrameCount` 基线）后只等 `baseline+1`——空闲 UI 不重绘，`+N` 会 timeout 造成假“未滚动/未提交”。
- `Click`/`Key`/`Wheel` 前置 `Settle`，`FrameOf` 在 settle 后取；click target 经 `ToScreen`（logical→client→screen）+ `WindowFromPoint` PID 校验，拒绝投给非本进程窗口。
- Esc 关闭证明：popup 打开时一次真实 Esc Down+Up 必须**只**关 popup、`IsOpen=false`、**宿主仍活**（`app-alive-after-esc`），其后才由 harness 自身 `RequestExit(0)`。这是验证宿主 Esc-Down/Up 修复的专段。
