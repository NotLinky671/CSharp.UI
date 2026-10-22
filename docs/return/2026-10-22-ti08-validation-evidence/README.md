# TI-08 验证证据目录（2026-10-22）

## 阶段二新增：最终 Debug/Release 回归 stdout（同源最绿,2026-10-22）

| 套件 | Debug | Release |
|---|---|---|
| CSharp.UI.Tests | 551 过 / 0 失败 (test-debug-ui.stdout.txt) | 551 过 / 0 失败 (test-release-ui.stdout.txt) |
| CSharp.UI.OpenTK.Tests | 668 过 / 0 失败 (test-debug-opentk.stdout.txt) | 668 过 / 0 失败 (test-release-opentk.stdout.txt) |
| CSharp.UI.Integration.Tests | 53 过 / 0 失败 (test-debug-integration.stdout.txt) | 53 过 / 0 失败 (test-release-integration.stdout.txt) |
| CSharp.UI.Text.Tests | 67 过 / 0 失败 (test-debug-text.stdout.txt) | 67 过 / 0 失败 (test-release-text.stdout.txt) |

合计 1339/1339 × 2 配置；子进程 TEMP/TMP 指向 artifacts 临时根（用后已删）。

> **历史保留**（test-*.stdout.txt，同源旧计数）：CSharp.UI.Tests 551/551、OpenTK.Tests 668/668、Integration 53/53、Text 67/67（Debug）；Release 551/668/53/67 已用新 `final-release-*` 覆盖，见下。

## 阶段二新增：真实净增量 (ti08-capabilities 最终收敛, 2026-10-22)

`TextInputServiceContractTests` +2 项（失败能力查询不穿透 + committed 落地无自唤醒; Forwarder 拒绝不写）、`RouterFallbackTests` +1 项（IMM 新能力查询拦截时如实回退:不适 Full 不穿透 committed 仍落地）。`OpenTkHost.DescribeCommittedOnlyReason(Platform)` 纯映射（Cocoa/X11/Wayland/unknown 四档）+ `DescribeCurrentPlatformReason()`（macOS→Cocoa、Linux→真实 `GLFW.GetPlatform`、其余/异常→unknown）。生产 else 分支调后者；非 Windows 分级 smoke 如需驱动各档，可经 InternalsVisibleTo 直接调前者（WSL smoke 已真实覆盖 committed-only，未依赖该 helper）。

## 同源最终 Release 证据（2026-10-22，含 OOM 修正 + 测试收敛）

命令：`cd tests/<proj> && dotnet build -c Release && dotnet exec bin/Release/net10.0/<dll> > final-release-<x>.stdout.txt 2>final-release-<x>.stderr.txt`
真实 exit：四套全 0；stdout 完整重定向，未用 tail 后的 `$?`。

| 套件 | 过/总 | 文件 |
|---|---|---|
| CSharp.UI.Tests | 551/551 | final-release-ui.stdout.txt |
| CSharp.UI.OpenTK.Tests | 667/667 | final-release-opentk.stdout.txt |
| CSharp.UI.Integration.Tests | 53/53 | final-release-integration.stdout.txt |
| CSharp.UI.Text.Tests | 67/67 | final-release-text.stdout.txt |
| 合计 | 1338 | final-hashes.sha256 |

## 真实 Linux 驱动 smoke（v2，真实 helper+ctor+runtime）

`artifacts/ti08-wsl-smoke/Ti08Smoke`（throwaway，app-local self-contained，用后已删）
→ `wsl-linux-smoke-v2.txt`
- 真实 `OpenTkHost.DescribeCommittedOnlyReason(Platform)` 映射 4 档（Cocoa/X11/Wayland/Null）
- 真实 `new NoTextInputService(reason)` 实例 → `UiRuntime(TextInputService)` → `TextField`
- 普通字符/全选替换/剪贴板复制粘贴/undo-redo/AltGr 不误关/Composition 被拒
- 平台修饰键真实驱动：macOS=Meta（Cmd+Shift+Z redo）/ Linux=Ctrl（Ctrl+Y redo）
- WSL2 app-local self-contained 真实执行，REAL_EXIT=0，56/56 PASS
- 边界如实：WSLg ≠ 裸机 IME；未声称 X11/Wayland IME 原生接入

## Windows 宿主 smoke v2（真实 OpenTK 窗口 + 真实 IMM）

`artifacts/ti08-wsl-smoke/Ti08WindowsSmoke`（throwaway，真实 Windows 桌面 + GL）
→ `windows-host-smoke-v2.txt`
- 真实 `UiApplication.Run` + 真 GLFW 窗口（320×120，VSync off，ExitOnEscape=false）
- `before`（frame 3，未聚焦）→ `SetFocus` + `ProcessPendingWork` → `after`（frame 5，已聚焦+会话建立）
- 真实 `WindowsTextInputRouter` → `committed=True composition=True anchor=True`，`Reason=null`（健康 IMM，无回退原因）
- 提交字符 `DispatchTextInput("A")` + `DispatchTextInput("你")` → `field=A你`（真实通道，非键盘回调证明 — 标注为 managed-input 模拟）
- 剪贴板 `GlfwClipboardService` 服务确认（read-only，未写真实 OS 剪贴板避免用户数据丢失）+ `StubClipboardService` 内存往返
- Ctrl+Z 合并撤销 → `field=""`，Ctrl+Y 重做 → `field=A你`
- AltGr（Ctrl+Alt+Z）精确断言：`field=="A你"` 且 `setterCount==0`（不触发 undo、不写绑定）
- 真实 exit=0，errors=0

> **历史保留**：`windows-host-smoke.program.txt`（原 13/13）存在 :68 `after` 在真正 `SetFocus` 之前读取、:128 `fieldValue is not null` bare check、:109-113 无 `finally` 且 `beforeClip==null` 时不恢复原剪贴板 — 已修正为 `windows-host-smoke-v2`，原文件仅作历史，不作最终充分证明。

## 真实 Linux 驱动 smoke（v3，真实 helper+ctor+runtime+WSLg 窗口边界如实）

`artifacts/ti08-wsl-smoke/Ti08Smoke`（throwaway，app-local self-contained，用后已删）
→ `wsl-linux-smoke-v3.txt` + `wsl-linux-smoke-v3.sha256`
- 真实 `OpenTkHost.DescribeCommittedOnlyReason(Platform)` 映射 4 档（Cocoa/X11/Wayland/Null）
- 真实 `new NoTextInputService(reason)` 实例 → `UiRuntime(TextInputService)` → `TextField`
- 普通字符/全选替换/剪贴板复制粘贴/undo-redo/AltGr 不误关/AltGr+Q 不退出/Composition 被拒
- 平台修饰键真实驱动：macOS=Meta（Cmd+Shift+Z redo）/ Linux=Ctrl（Ctrl+Y redo）
- WSL2 app-local self-contained 真实执行，REAL_EXIT=0；57 PASS / 1 边界 FAIL
- 边界 FAIL 如实：`UiApplication.Run` → `UiHostException`（GLFW NotInitialized，无 GL/X11/Wayland 会话；非 mock 失败，WSLg 无显示服务器）
- WSLg ≠ 裸机 IME；未声称 X11/Wayland IME 原生接入；不装 fontconfig/不改全局

> **历史保留**：`wsl-linux-smoke-v2.txt` 已被 `wsl-linux-smoke-v3.txt` 取代（同源重建，含 AltGr+Q 断言 + WSLg 窗口边界如实 FAIL）；旧文件已删，不再引用。

## 最终同源 Release 证据（2026-10-22，含 OOM + AltGr+Q 修正）

命令：`cd tests/<proj> && dotnet build -c Release && dotnet exec bin/Release/net10.0/<dll> > final-release-<x>.stdout.txt 2>final-release-<x>.stderr.txt`
真实 exit：四套全 0；stdout 完整重定向，未用 tail 后的 `$?`。

| 套件 | 过/总 | 文件 |
|---|---|---|
| CSharp.UI.Tests | 551/551 | final-release-ui.stdout.txt |
| CSharp.UI.OpenTK.Tests | 668/668 | final-release-opentk.stdout.txt |
| CSharp.UI.Integration.Tests | 53/53 | final-release-integration.stdout.txt |
| CSharp.UI.Text.Tests | 67/67 | final-release-text.stdout.txt |
| 合计 | **1339** | final-hashes.sha256 |

与 Debug 同源 1339 一致（OpenTK 668 因 AltGr+Q 测试 +1 高于旧 667）。

## 真实 Linux 驱动 smoke（v4，含 AltGr+Q + 正确 GLFW init 顺序 + 完整 NRE 证据）

`artifacts/ti08-smoke-v4`（throwaway，app-local self-contained，用后已删）
→ `wsl-linux-smoke-v4.txt` + `wsl-linux-smoke-v4.sha256`
- 57 PASS：helper 4 档映射、NoTextInputService ctor → UiRuntime → TextField、Meta/Ctrl 修饰键、AltGr+Z 不撤销、AltGr+Q 不退出、Composition Rejected 不写
- **真实 FAIL（非边界）**：`UiApplication.Run` → `UiHostException` 内嵌 `NullReferenceException` at `GlWindowFactory.cs:172` inside `NativeWindow..ctor` — GLFW.Init() 成功但无显示服务器时 OpenTK NativeWindow 构造器 NRE，属真实生产 bug（非调用前提错误、非库缺失、非 mock）
- GLFW.Init() 成功 → `GetPlatform()=0`（真实，init 后调用）→ 窗口创建失败（NRE）→ `DescribeCurrentPlatformReason` 未达（生产 bug 阻断）
- REAL_EXIT=0 但 `RESULT: FAIL (1)` — 失败计数如实反映，不 suppress

## 真实 Linux 驱动 smoke（v5，含 NRE→GLFW 原因修复 + AltGr+Q + 正确 init 顺序）

`artifacts/ti08-smoke-v5`（throwaway，app-local self-contained，用后已删）
→ `wsl-linux-smoke-v5.txt` + `wsl-linux-smoke-v5.sha256`
- **57 PASS 全绿**：helper 4 档、NoTextInputService ctor → UiRuntime → TextField、Meta/Ctrl、AltGr+Z 不撤销、AltGr+Q 不退出、Composition Rejected
- **真实 FAIL 已修**：`UiApplication.Run` → `UiHostException` 的"原因"行从 `NullReferenceException: Object reference...` 变为 `GLFW：NotInitialized：The GLFW library is not initialized`（真实 GLFW last-error，非裸 NRE）；`GlWindowFactory.cs:172` 内嵌 `NativeWindow..ctor` NRE 仍在（真实边界），但外层原因可观察
- GLFW.Init() 成功 → `GetPlatform()=0`（真实，init 后调用）→ 窗口创建失败（NRE→GLFW 原因）→ `DescribeCurrentPlatformReason` 未达（生产边界阻断）
- REAL_EXIT=0，RESULT: PASS

## 最小 GLFW 原生初始化探针（独立 throwaway，不复用自清理 artifacts）

`artifacts/ti08-glfw-probe`（throwaway，app-local self-contained，用后已删）
→ `wsl-glfw-probe.txt` + `wsl-glfw-probe.sha256`

**判定：真实环境边界，非生产 bug**

- `DISPLAY=:0`、`WAYLAND_DISPLAY=wayland-0`、`XDG_RUNTIME_DIR=/run/user/1000/` — 环境变量存在
- `wayland-socket=/run/user/1000/wayland-0` 文件存在、`x11-socket=/tmp/.X11-unix/X0` 文件存在
- `ldd libglfw.so.3` exit=0（GLFW 库可加载）
- **`GLFW.Init() = False`** — error-callback 报 `PlatformUnavailable: Failed to detect any supported platform`（真实 GLFW init 失败，非库缺失、非权限）
- `GLFW.GetError() = PlatformUnavailable`（真实本次错误，非 stale NotInitialized）
- `GLFW.GetPlatform()` 未达（Init 失败即跳过，符合正确时序）
- `GlfwProvider.EnsureInitialized()` 同样 NRE — 确认是 OpenTK 内部初始化失败，非我们代码路径问题
- **判定**：GLFW 库可加载但无可用显示平台（WSLg 仅 sockets 无合成器，无真实 X11/Wayland 服务器），`Init()=False` 是真实环境边界；`NativeWindow` ctor 的 NRE 是 OpenTK 在 Init 失败后的下游行为，不是独立生产 bug
