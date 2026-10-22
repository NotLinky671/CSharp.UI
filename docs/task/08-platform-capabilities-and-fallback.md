# TI-08：跨平台能力查询与兼容回退

创建日期：2026-10-17

状态：已完成（2026-10-22 主代理审核通过：Debug/Release 各 1339 项测试全通过；macOS/Linux 仅源码级与 CPU 模拟验证，无真机 IME 证据）

阶段：D

设计依据：[文本输入与编辑基础计划](../plan/2026-10-17-text-input-and-editing-plan.md)（接口冻结前均为设计草案）。

[返回任务索引](README.md)

## 目标与范围

Windows完整路径和其他平台的已提交文本回退均可用，能力与证据一致。

边界：本轮macOS/Linux只强制能力声明和安全回退，不未经审核扩大原生IME实现。

## 前置依赖

[TI-07：Windows IME 桥与宿主输入联调](07-windows-ime-and-host-routing.md)均须Codex复审通过后再实施；使用前置任务已冻结契约。

## 实施清单与接口接入

- [ ] 根据TI-01矩阵实现分项能力查询、默认服务与诊断，其他平台加载不得触发Windows原生库。
- [ ] 保留macOS/Linux原字符输入、选区、剪贴板和平台编辑快捷键；记录X11/Wayland分别支持级别。
- [ ] 将宿主不支持组合/候选锚点与暂时无焦点区别，回退不得宣称完整IME；异常可控且不反复唤醒。
- [ ] 记录需要后续单独计划的原生接入点和缺失环境，不能把只编译通过写成原生输入通过。

## 测试与验收

- [ ] 可用平台上构建/CPU测试与原生加载隔离；注入能力组合/失败服务验证回退。
- [ ] macOS Command与Windows/Linux Ctrl映射、AltGr、不支持组合时普通已提交输入不丢失。
- [ ] 每个平台分别标编译/模拟/离屏/真机状态；缺真机须记录未验证，不伪造通过。

## 实施记录与审核门槛

实施时记录实际签名、涉及模块、命令/退出码/测试计数、证据路径及平台边界。DSH自测通过后标“待Codex复审”；Codex审核问题写docs/return带日期文件，DSH返修至通过后才英文提交并推送。状态改“已完成”须有Codex复审结论，不在Build/Measure中执行外部副作用。

本轮不修改既有archive文件；关键证据不只放可清理artifacts。后续交付记录由Codex创建，当前不预先标记验收通过。

### 实施记录（2026-10-22，Devin CLI / ti08-capabilities）

设计：[`docs/plan/2026-10-22-ti08-platform-capabilities-plan.md`](../plan/2026-10-22-ti08-platform-capabilities-plan.md)（§5b 含最小运行复现证据）。

实际签名与涉及模块：

- `src/CSharp.UI/Editing/TextInputFallback.cs`：`NoTextInputService` 新增 `internal NoTextInputService(string reason)`（永远 committed=true/composition=false/anchor=false，构造期 `new TextInputCapabilities(committedText: true, reason: reason)`；`Instance` 保持 `CommittedTextOnly` 单例；StartSession→false/ActiveClient→null/ApplyComposition→Rejected 语义不变）；`TextInputForwarder.ApplyComposition` 的 `service.Capabilities` 读点（原 137 行）加 `catch (Exception ex) when (ex is not OutOfMemoryException)` → `Rejected`（不穿透、不重投）。
- `src/CSharp.UI/Editing/TextInputServiceSession.cs`：`Start` 的 `service.Capabilities.Composition` 读点（原 124 行）加同样 catch → `ReportServiceFailure("Capabilities", ex)` + `return false`（不造 Full、不重试、不影响 committed 通道）。
- `src/CSharp.UI.OpenTK/WindowsTsf/WindowsTextInputRouter.cs`：`Capabilities` getter 内 `tsf.Capabilities`（308）与 `imm?.Capabilities`（311）各自包局部 catch → 诚实保守能力快照（该子桥档位视为缺失）+ 脱敏 Reason；不切换/不释放/不重试真实后端，遵守既有 doc 禁止读能力建/切后端。
- `src/CSharp.UI.OpenTK/Hosting/OpenTkHost.cs`：非 Windows else 分支 `_platformTextInput = new NoTextInputService(DescribeCurrentPlatformReason())`；`UiRuntimeServices.TextInputService = _inputBridge ?? _platformTextInput`；`internal static DescribeCommittedOnlyReason(Platform)` 纯映射（Cocoa/X11/Wayland/unknown 四档，throwaway 可经 InternalsVisibleTo 驱动真实分支）+ `DescribeCurrentPlatformReason()`（macOS→Cocoa、Linux→真实 `GLFW.GetPlatform()`、其余/异常→unknown，不读 env）；`_textInputBackendReason` 死字段已删，`TextInputBackendReason` → `_router?.BackendReason ?? _platformTextInput?.Capabilities.Reason ?? "默认 IMM 输入桥。"`。
- `src/CSharp.UI.OpenTK/UiApplication.cs`：`UseTsfInputBridge` 注释中不存在的 `UiHostContext.TextInputBackendReason` 引用改为 `OpenTkHost.TextInputBackendReason`/`router.BackendReason`。
- `tests/CSharp.UI.Tests/TextInputServiceContractTests.cs`：+2 强行为回归（`ThrowingCapabilitiesService` 替身：`FailingCapabilities_FocusAndCommittedTextStillWork_NoRetryOrSelfWake`——焦点建立、精确 setter 一次/值 Zab、恰好 1 条诊断、无输入帧推进后诊断/文本/待办稳定不重试；`Forwarder_FailingCapabilities_RejectWithoutThrowingOrWriting`——显式 Rejected、不穿透、不写目标、不重投、不伪报已执行）。
- `tests/CSharp.UI.OpenTK.Tests/RouterFallbackTests.cs`：+1 合并强回归（`ThrowingCapabilitiesBridge` 替身：子桥 getter 抛错 → 诚实保守快照不造 Full/不建不切换后端/不写归属、真实 TextField 恰一次 committed 写入）；并按主审删除既有两处 `BackendReason` 文案 `Contains` 与新增 Reason `Contains`，改行为断言。
- `tests/CSharp.UI.OpenTK.Tests/HostingTests.cs`：+1 AltGr 回归（`AltGr加Q不退出且字符落地_CtrlQ仍退出`——真实 adapter+runtime：`using var runtime` 保证失败也释放；Control|Alt+Q→0 退出/'@' 恰一次落地、Ctrl+Q→1 退出、不 pin 文案）。
- `src/CSharp.UI.OpenTK/Hosting/OpenTkInputAdapter.cs`：`DeliverCapturedCore` Q 退出分支加 `!keyEvent.Modifiers.HasFlag(KeyModifiers.Alt)`——AltGr（常见布局 Ctrl+Alt）触发 key-down 时 `IsCommandPressed`（Control 位）不再误匹配 Ctrl+Q；Ctrl/Meta 退出策略与 Esc 路径不变（真实生产 adapter + UiRuntime 复现：修复前 AltGr+Q exits=1/text='@' → 修复后 exits=0/text='@'、Ctrl+Q 仍 exits=1，见 `docs/agents-notebook/ti08-capabilities-repro/altgr-q-exit.txt`）。

命令与结果（同源双配置，2026-10-22 终验重跑，八套 exit=0）：

```
Debug  build slnx: 0 errors;  CSharp.UI.Tests 551 + OpenTK.Tests 668 + Integration 53 + Text 67 = 1339
Release build slnx: 0 errors;  CSharp.UI.Tests 551 + OpenTK.Tests 668 + Integration 53 + Text 67 = 1339
```

明细（Debug 分套）：

```
dotnet build src/CSharp.UI/CSharp.UI.csproj          exit=0, 5 warnings(既有), 0 errors
dotnet build src/CSharp.UI.OpenTK/CSharp.UI.OpenTK.csproj  exit=0, 10 warnings(既有), 0 errors
dotnet build tests/CSharp.UI.Tests/CSharp.UI.Tests.csproj  exit=0, 0 errors
dotnet build tests/CSharp.UI.OpenTK.Tests/CSharp.UI.OpenTK.Tests.csproj  exit=0, 0 errors
dotnet exec tests/CSharp.UI.Tests/bin/Debug/net10.0/CSharp.UI.Tests.dll
    Total: 553→551 after 收敛, Errors: 0, Failed: 0   (新净 +2)
dotnet exec tests/CSharp.UI.Tests/bin/Debug/net10.0/CSharp.UI.Tests.dll -class CSharp.UI.Tests.TextInputServiceContractTests
    Total: 18, Errors: 0, Failed: 0
dotnet exec tests/CSharp.UI.OpenTK.Tests/bin/Debug/net10.0/CSharp.UI.OpenTK.Tests.dll -class CSharp.UI.OpenTK.Tests.RouterFallbackTests
    Total: 8, Errors: 0, Failed: 0
dotnet exec tests/CSharp.UI.OpenTK.Tests/bin/Debug/net10.0/CSharp.UI.OpenTK.Tests.dll
    Total: 668, Errors: 0, Failed: 0   (新净 +2：合并+AltGr)
```

平台边界：macOS/Linux **源码级 + CPU 模拟**验证（注入/替身断言 committed-only 语义；平台 Reason 走 `internal static DescribeCommittedOnlyReason(Platform)` 纯映射，throwaway 可驱动真实 Cocoa/X11/Wayland/unknown 分支——WSL2 真实判定覆盖由 ti08-validation 独立记录），无真机 IME 证据；X11/Wayland 判定走 `GLFW.GetPlatform()` 真实后端 API（Linux 上需 `OPENTK_4_USE_WAYLAND=1` 才选 Wayland 后端）；Windows 默认 IMM/TSF opt-in 语义未改；`IsCommandPressed` 语义（Control∥Meta）未改，仅 Q 退出消费点加 Alt 排除（AltGr+Q 不误退，Ctrl+Q 仍退——见 §5.1b 与 altgr-q-exit.txt）。WSLg 真实 `UiApplication.Run` 建窗抛 `UiHostException(inner=NullReferenceException)`——已观察初始化失败，**根因待查**（原生探针 `wsl-glfw-probe.txt` 记录 `GLFW.Init()=False`/`PlatformUnavailable` 首个 callback 错误与 lib/socket 状态，但未排除脚本 init 生命周期误用；不预定"无显示服务器"或"产品 bug"任一结论）。

证据：`docs/agents-notebook/ti08-capabilities-repro/`（`before-capabilities-throw.txt`/`after-capabilities-throw.txt` 能力复现对照、`altgr-q-exit.txt` AltGr+Q 退出修复前后对照，各含命令/stdout/exit）。

复审返修反例自查（本轮）：
- (1) Linux `GLFW.GetPlatform()` 未知/异常 → `_`/`Null` 档返回 committed-only Reason，绝不声明 Composition；非 Windows else 分支不实例化 `WindowsImeBridge`/`WindowsTextInputRouter`/任何 Windows 原生类型。
- (2) 能力=服务属性，与临时无焦点正交：router 对 Full-capable 后端在无 `ActiveClient` 时仍按真实后端能力报告；`NoTextInputService` 始终 committed-only，区分成立。
- (3) 新增/改动 catch 全部 `when (ex is not OutOfMemoryException)`（TextInputFallback:143、Session:129/171/276/294/310/397/494、Router:312/327、OpenTkHost:148/323；其余既有 `catch(Exception)` 为 TI-07 原状未动）。
- (4) `TextEditHistory`/`TextEditCommandResolver`/剪贴板/`OpenTkInputMapping`/`IsCommandPressed` 语义本轮零改动；**`OpenTkInputAdapter.DeliverCapturedCore` Q 退出分支 1 行新增 `!Alt` 排除**（AltGr+Q 误退修复，见 §5.1b），Ctrl/Meta 退出策略与 Esc 路径不变，编辑命令/Undo/Redo/Cut/Copy/Paste/SelectAll 路径未被截断。

状态：自测通过，主代理已于 2026-10-22 复核通过。
