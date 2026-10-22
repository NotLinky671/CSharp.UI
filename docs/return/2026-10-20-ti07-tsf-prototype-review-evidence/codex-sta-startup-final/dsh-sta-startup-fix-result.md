# TI-07 隔离 TSF 工具：STA 入口启动修复结果（R17 / R19）

日期：2026-10-20。执行：DeepSeek Harness，精确模型 `commandcode-plan/deepseek/deepseek-v4-flash`。规划/独立审核/真实验收：Codex。

状态：**R17 宿主套间启动修复完成；R19 平台守卫顺序已修；隐藏宿主入口回归 MTA/STA 实测通过。** 未运行可见 GUI、未注入任何按键、未提交推送、未开 TI-08。
**不宣称候选跟随/裁出问题已解决**——本轮只修启动路径与补回归能力，候选可见行为仍由 Codex 真实运行判定。

审核依据：[TSF 原型审核](../../docs/return/2026-10-20-ti07-tsf-prototype-review.md) 的 **R17**、**R18**、**R19** 与“路线诊断独立复测与报告审核”。

---

## 1. R19：平台守卫必须在 ole32 调用之前（已修）

R19 指出：`Program.Main` 为记录入口套间，把 `NativeMethods.CoGetApartmentType`（ole32）放在了 `OperatingSystem.IsWindows()` 判断之前，非 Windows 上会先抛加载异常，破坏原有“打印 Windows-only 说明并返回 2”的行为。

修复后的顺序（`tools/CSharp.UI.TsfHostCheck/Program.cs`）：

| 行 | 内容 |
|---|---|
| 21–25 | `if (!OperatingSystem.IsWindows()) { Console.Error.WriteLine("CSharp.UI.TsfHostCheck only runs on Windows (TSF/GDI)."); return 2; }` |
| 29 | 平台确认**之后**、任何日志/窗口/COM 对象创建**之前**：`NativeMethods.CoGetApartmentType(...)` 记录 `ApartmentAtMainEntry` |

- 诊断位置未变（仍在任何 HWND/COM 对象与 `RunHost` 之前），宿主线程方案未改。
- 调用顺序已按源码行号确认（守卫 21 行 < ole32 调用 29 行）；**不需要真实非 Windows 机器**即可从顺序确认，符合 R19 要求。
- 该文件同批编译通过（见 §6）。

## 2. R17：宿主套间在任何 HWND/COM 之前确定（已修）

### 2.1 实测事实（决定了修法）

`--hidden-host` 入口诊断实测：**进程入口线程在 `Main` 之前就已被运行时固定为 MTA**。

```
env/host-apartment: requested=MTA hresult=S_FALSE apartment_at_main_entry="type=1 qualifier=0"
                    apartment_before="type=1 qualifier=0" apartment="type=1 qualifier=0"
```

因此把 `CoInitializeEx(APARTMENTTHREADED)` 仅仅提到窗口创建之前**不足以**修复 STA：入口线程已处于 MTA，请求 STA 只会得到 `RPC_E_CHANGED_MODE`（0x80010106）。此前 Codex 的中途复现与此一致：`artifacts/ime-work/ti07-tsf-codex-sta-startup-interim/sta/tsf-host-check.jsonl`（`apartment_before=type=1`、`RPC_E_CHANGED_MODE`、exit 2）。

### 2.2 修法

- 新增 `HostApartment`（`tools/CSharp.UI.TsfHostCheck/HostApartment.cs`）：宿主线程的 COM 套间**租约**。
  - `Prepare(sta, trace, label, entryApartment)` 在任何 HWND/COM 之前调用一次，记录 `CoGetApartmentType` 的**初始化前 / 入口 / 初始化后**三次真实值、请求模型、`CoInitializeEx` 标志与 HRESULT、线程身份。
  - **MTA 保持默认**：`HostThreadSelection.ProcessEntry`，沿用进程入口线程（既有已验证路径不变）。
  - **STA 真正使用 STA**：`HostThreadSelection.DedicatedSta`——宿主在启动**前**对自选线程设置 STA，窗口创建、TSF 接入、消息循环与清理**全部留在该线程**。
  - 请求与实际不符（如请求 STA 却落在 MTA）时如实失败（`E_FAIL`），不用错误套间产出“对照”证据。
- 引用所有权（不强制反初始化别人的初始化）：
  - `S_OK` / `S_FALSE` → 本租约持有一次引用，退出时成对 `CoUninitialize`；
  - 其它负值（`RPC_E_CHANGED_MODE` 等）→ `owns_reference=false`，记 `couninitialize-skipped`，**绝不**调用 `CoUninitialize`。
  - 未改任何全局设置（不改系统 DPI/输入法/profile/COM 全局），只操作自有线程。
- `Program.RunHost` → `RunHostCore`：`RunHost` 只负责线程选择；`RunHostCore` 是可见运行与隐藏回归**共用**的一条真实路径（套间 → 窗口 → TSF → 消息泵 → 清理）。
- `TsfSessionHost` 接受宿主租约：`Engage` 在有租约时**不再二次 `CoInitializeEx`**；`Cleanup` 记 `couninitialize-delegated` 表明引用由宿主租约成对释放。

## 3. 新增 `--hidden-host`：实际宿主入口回归

- 复用可见运行同一条 `RunHost`/`RunHostCore`，**不是**另写一条 NativeSmoke 路线。
- 隐藏模式**绝不** `ShowWindow`、绝不请求前台、不注入任何按键、不构造 `ScenarioRunner`、不跑任何场景；每次进程只跑一个套间（默认 MTA，`--apartment sta` 才用 STA）。
- 与 `--scenario`/`--selftest`/`--native-selftest` **互斥**，冲突时解析失败（exit 1），不静默忽略其中一项。
- 输出结构化记录 `host/hidden-host-result`；退出码 `0` 通过 / `1` 隐藏检查失败 / `2` 前置失败（套间、窗口或 TSF 接入）。
- 检查项：套间命中请求、会话上报宿主租约、单宿主线程（Win32/托管/窗口三者一致）、窗口隐藏、`show_window_calls==0`、未请求前台、TSF engaged、store sink mask `0x1F`、会话资源全部建立、COM 引用归宿主、布局已发布、无组合、清理 0 失败、资源全释放、sink 移除、窗口已销毁、类已注销、零绑定写入。

## 4. 实测证据（隐藏宿主入口，最终代码）

命令（无 GUI、无按键）：

```powershell
dotnet run --project tools/CSharp.UI.TsfHostCheck -c Debug --no-build -- --hidden-host --out artifacts/ime-work/ti07-dsh-sta-startup-fix/hidden-mta
dotnet run --project tools/CSharp.UI.TsfHostCheck -c Debug --no-build -- --hidden-host --apartment sta --out artifacts/ime-work/ti07-dsh-sta-startup-fix/hidden-sta
```

| 项 | MTA（默认） | STA（显式） |
|---|---|---|
| 退出码 | **0** | **0** |
| `thread_selection` | `ProcessEntry` | `DedicatedSta` |
| `runs_on_entry_thread` | `true`（entry=2, host=2） | **`false`**（entry=2, host=4） |
| `apartment_at_main_entry` | `type=1 qualifier=0` | `type=1 qualifier=0` |
| `apartment_before` | `type=1 qualifier=0` | `type=3 qualifier=0` |
| `apartment`（最终） | `type=1 qualifier=0`（MTA） | **`type=3 qualifier=0`（MAINSTA）** |
| `matches_requested` | `true` | `true` |
| `show_window_calls` | 0 | 0 |
| `scenario_ran` / `key_input_sent` / `foreground_requested` | false / false / false | false / false / false |
| `cleanup_failures` | 0 | 0 |
| 其它检查 | `window-created-and-destroyed`、`class-unregistered`、`store-sink-removed`、`resources-released` 全部 PASS | 同左 |

证据文件：`artifacts/ime-work/ti07-dsh-sta-startup-fix/hidden-mta/tsf-host-check.jsonl`、`hidden-mta-console.txt`、`hidden-sta/tsf-host-check.jsonl`、`hidden-sta-console.txt`。

说明：STA 线程的套间由运行时在 `Thread.Start` 前按 `SetApartmentState(STA)` 建立（因此 `apartment_before=type=3`），`HostApartment` 负责**核验**（`CoGetApartmentType`）并成对释放自己那次 `CoInitializeEx`（`S_FALSE`）。

## 5. 修改文件清单（仅隔离工具与目标测试）

| 文件 | 改动 |
|---|---|
| `tools/CSharp.UI.TsfHostCheck/HostApartment.cs` | **新增** 套间租约：入口/前/后真实套间记录、`CoInitFlag`、引用所有权、套间匹配判定、宿主线程选择枚举 |
| `tools/CSharp.UI.TsfHostCheck/Program.cs` | R19 平台守卫提前；入口套间诊断；`RunHost`/`RunHostCore` 拆分与 STA 宿主线程选择；`--hidden-host` 与互斥校验；`RunHiddenHost`/`PumpHostMessages`；`TryParse`/`ParsedArguments` 改为 internal 以便测试；usage 更新 |
| `tools/CSharp.UI.TsfHostCheck/TsfSessionHost.cs` | 可选宿主租约参数；`Engage` 有租约时不二次初始化、套间不匹配即失败；`Cleanup` 记录 `couninitialize-delegated` |
| `tools/CSharp.UI.TsfHostCheck/TsfInterop.cs` | 新增 `CO_E_NOTINITIALIZED`/`RPC_E_CHANGED_MODE` 常量与可读名称 |
| `tools/CSharp.UI.TsfHostCheck/TsfHostWindow.cs` | 只读 `IsAlive`/`IsClassRegistered`/`ShowWindowCalls` 供隐藏检查取证 |
| `tools/CSharp.UI.TsfHostCheck/ScenarioRunner.cs` | `ScenarioOptions.HiddenHost`（隐藏模式不构造 runner） |
| `tools/CSharp.UI.TsfHostCheck/SelfTest.cs` | 自检新增宿主启动策略 7 项（默认 MTA、STA 自选线程、引用所有权、套间匹配、`--hidden-host` 默认/STA、与 `--scenario` 冲突拒绝） |
| `tools/CSharp.UI.TsfHostCheck/README.md` | `--hidden-host`、套间规则、平台守卫顺序、参数表与自检说明 |
| `tests/CSharp.UI.TsfHostCheck.Tests/HostApartmentTests.cs` | **新增** 纯策略测试 17 例（含 Theory） |
| `tests/CSharp.UI.TsfHostCheck.Tests/ProgramArgumentsTests.cs` | **新增** CLI 解析/互斥 7 例 |
| `artifacts/ime-work/ti07-dsh-route-reassessment-result.md` | 勘误：撤回穷尽性/无手段/唯一路线的表述；修正 STA 能力依据 |

未改：生产 `src/`、公开 API、全局输入法配置、外部候选 HWND；`AGENTS.md`、两份 ACP 保护文件、`artifacts/ime-work/ti07-tsf-codex-audit/` 均未改动（§9）。

## 6. 测试命令与统计（如实；含未完成项）

目标工程构建与测试使用 `-m:1 -nodeReuse:false`（原因见 §7）：

```powershell
dotnet build tests/CSharp.UI.TsfHostCheck.Tests/CSharp.UI.TsfHostCheck.Tests.csproj -c Debug --no-restore -v quiet -m:1 -nodeReuse:false
tests/CSharp.UI.TsfHostCheck.Tests/bin/Debug/net10.0/CSharp.UI.TsfHostCheck.Tests.exe
dotnet run --project tools/CSharp.UI.TsfHostCheck -c Debug --no-build -- --selftest
```

| 项目 | 状态 | 结果 |
|---|---|---|
| Debug 目标工程构建 | **完成** | 0 错误（2 条既有警告：NU1900、`UiDispatcherTests` xUnit1051） |
| Debug 目标正式测试 | **完成** | **Total 139 / Errors 0 / Failed 0 / Skipped 0**（`artifacts/ime-work/ti07-dsh-sta-startup-fix/Debug-tests.txt`） |
| Debug `--selftest` | **完成** | **59 passed / 0 failed**（`.../Debug-selftest.txt`） |
| Debug `--hidden-host`（MTA / STA） | **完成** | 各 **exit 0**、全部检查 PASS（§4） |
| **Release 目标构建 + 正式测试 + `--selftest`** | **未完成 → 交 Codex** | 本轮未运行；不得用旧 Release（R17 前 115/52）或旧二进制代替 |
| **Release `--hidden-host` MTA/STA** | **未完成 → 交 Codex** | 同上 |
| 可见 STA 滚动 `--scenario scroll-follow --apartment sta` | **未完成 → 交 Codex** | 本轮不运行可见 GUI/按键 |
| Codex 独立 audit（28 项） | **未运行（不归 DSH）** | audit 源码未改 |

R18（`GeometryTraceTests` 的 `_` 参数遮蔽 `out _`）已修复并复核：本轮重新扫描工具与测试全部 `.cs`（含 `out _` 的块 lambda）**无同类残留**；未通过放宽断言绕过。

## 7. 构建环境失败：现象如实记录，根因未确定（交 Codex）

- 默认（多节点）构建 `dotnet build tests/CSharp.UI.TsfHostCheck.Tests/... -c Debug --no-restore` 会失败，但摘要为“**0 个错误**”。
- 已观察到的控制项（原始 diag 保留在 `artifacts/ime-work/ti07-dsh-sta-startup-fix/build-*.txt`）：
  - `-m:1`（单节点）→ **exit 0**；`-m:2` 或默认 → exit 1；`-nodeReuse` 无影响。
  - 成功的单节点构建里**同样出现 MSB4276**；把 workload resolver 关掉后多节点**仍失败且不含 MSB4276**。
  - `NU1900` 只是“无法访问 api.nuget.org 漏洞数据源”的警告。
- 结论：**MSB4276 与 NU1900 都不能当作已确定的根因**；失败点在多节点 MSBuild 的 `_GetProjectReferenceTargetFrameworkProperties` → `MSBuild` 任务 → 引用工程的 `GetTargetFrameworks`。根因未确定，**本轮不再继续诊断**，交由 Codex 核验。
- 本轮使用的绕过参数仅为构建参数：`-m:1 -nodeReuse:false`（不改源码）。

## 8. 明确不声称 / 未验证行为

- **不声称候选移动/裁出问题已解决**；本轮无任何候选视觉结论，未运行可见 GUI、未注入按键、未做真实拼音输入。
- STA 只验证到**隐藏宿主入口**（套间、线程、TSF 接入、消息泵、清理）；**可见 STA 滚动尚未由 Codex 运行**，不能据此宣称 STA 对照已通过。
- 非 Windows 行为未在真实非 Windows 机器运行；R19 只按源码调用顺序确认。
- `--hidden-host` 只覆盖启动/清理路径，不评估候选视觉，也不替代 `--native-selftest` 的底层接口 smoke。
- Release 配置、Codex 独立 audit 与完整独立复测均未由本轮完成（§6）。

## 9. 保护文件与工作树

未改动（SHA256 与本轮开始前一致）：

```
4605FBE73D44FADE23E6A6BAC8EFC1D54AA87226455A8C06F8A81365FC630B40  AGENTS.md
81A85B5A8B6681EB8A2E80AEB42C81621245EF982EDCC0B44585F1E6DB3350D8  docs/agents-notebook/acp/2026-10-19-devin-acp-check.md
2DCC755EEDF47674E4B16EB84FEA19588924A88555A41572E2BB2933892EBC7C  docs/agents-notebook/acp/check-devin.mjs
BB9140FEA161004F8F6CC5F6B0B03F20D84A0408037D190C00BFAB33904C6FE5  artifacts/ime-work/ti07-tsf-codex-audit/Program.cs
```

工作树保持 dirty（含用户/他人既有变更），未回滚、未提交、未推送、未开 TI-08。

## 10. 待 Codex 审核

1. R19 顺序与 R17 宿主线程选择是否接受；`-m:1` 仅为构建绕过参数。
2. 请独立复测：Debug/Release 目标构建与正式测试、`--selftest`、`--hidden-host`（MTA/STA）、audit。
3. 请运行可见 `--scenario scroll-follow --apartment sta`，核对真实 STA 对照与截图。
4. 候选跟随/裁出（R15）仍开放；本轮不批准 TI-07 完成、生产 TSF 迁移、自绘候选实现或提交推送。
