# Panel/Sidebar 交付证据目录索引（2026-10-31）

本目录为 `docs/delivery/2026-10-31-panel-sidebar-delivery.md` 的原始证据。
所有 PNG/stdout 为执行期原始字节，未改写；`.txt` 文本按实际执行保留。

## 目录

| 路径 | 内容 | 来源 |
|---|---|---|
| `native-self/ev-{1024,320}-{light,dark}.stdout.txt` | self 四组 stdout raw（`^PASS ` 检查 19/21/19/21 + RESULT PASS、`UiApplication.Run exit=0`、`host-exited=False`） | 同上 |
| `native-self/producer/` | self driver 源 `Program.cs` + `panel-sidebar-native.csproj` + `SHA256.txt`（`f045f7d7…`/`4e7e0ebf…`） | 冻结自 `%TEMP%/panel-sidebar-native/app/` |
| `native-independent/` | 独立复跑 1024-dark 输出 PNG（7 张） | 本代理 final-gate 执行，self producer 原字节 |
| `native-independent.{stdout,stderr}.txt` | 同上 raw（21 项 `^PASS ` 检查 + RESULT PASS；stderr 0B） | 真实 shell EXIT=0（见 `exits.txt`） |
| `native-independent-down/` | 独立扩展 1024-dark 输出 PNG（8 张，含 `03b-kbd-space`） | 本代理执行，扩展 producer |
| `native-independent-down.{stdout,stderr}.txt` | 同上 raw（23 项 `^PASS ` 检查 + RESULT PASS；stderr 0B） | 真实 shell EXIT=0 |
| `native-independent-down-producer/` | 扩展 driver 源 `Program.cs`（sha `ae6f6cee…`）+ csproj + `SHA256.txt` | `%TEMP%/panel-sidebar-native-independent/app/`（self producer 副本 + Down/Space/Shift+Tab 扩展，原 self 快照未动） |
| `build-{Debug,Release}.{stdout,stderr}.txt` | fresh `--no-incremental -m:1 -nodeReuse:false` 双配置构建 raw | EXIT=0 各一次；stderr 0B |
| `image-integrity.txt` | PNG 静态校验（Pillow `Image.open().load()` 完整解码 + 尺寸 + sha）：本目录 41 张（self 26 + 独立 7 + 扩展 8）+ `docs/return/2026-10-31-*-evidence/` 11 张，合计 52 全通过 | 静态字节检查 |
| `exits.txt` | 全部已执行命令与真实 shell 退出码汇总 | 执行时观察，shell receipt 未单独留存处如实标注 |
| `doc-link-integrity.txt` | 本轮受影响 markdown 链接静态校验 | 静态文件检查 |

## 复现命令

```bash
# 构建（fresh 双配置，串行）
dotnet build CSharp.UI.slnx -c Debug   --no-incremental -m:1 -nodeReuse:false
dotnet build CSharp.UI.slnx -c Release --no-incremental -m:1 -nodeReuse:false

# 五套件（direct exe，非 MSBuild dotnet test——该路径命名管道存在已知问题）
tests/<Suite>/bin/{Debug,Release}/net10.0/<Suite>.exe

# 原生（需 Windows 桌面会话；own-HWND/SendInput/GL.FRONT readback）
cd <producer 目录>
P_EV=<evidence 目录> P_WIDTH={320|1024} P_THEME={light|dark} \
  bin/Release/net10.0/win-x64/CSharp.UI.OpenTK.Tests.exe
```

## 边界声明（如实）

- **instrumented harness**：真实 Release `GalleryApp` + 生产 `OpenTkHost`
  （`UiApplication.Run`），仅附加 `Started`/`FrameRendered` 观察回调——
  **不是原始 `ControlGallery.exe` 本身**，也不是未验收的 GDI 采集通道。
- **计数纪律**：setter"恰好一次写"由永久测试 Binding 计数断言承载；native
  通道只断言精确值（exact value），不冒充计数。
- **self run 边界**：self 执行期 stderr 未单独捕获、shell 进程退出码未单独
  留存——`UiApplication.Run exit=0` 为 harness 内部应用退出码，**不等同**
  进程级 shell EXIT；独立两组（final-gate）有真实 shell EXIT=0 记录
  （`exits.txt`）。
- `native-06-*-final.png` 与其前一张在 1024-light/320-light/dark 中字节相同
  （final 帧未变，如实记录，非错误）。
- 无 DLL/EXE/PDB 入本目录。
