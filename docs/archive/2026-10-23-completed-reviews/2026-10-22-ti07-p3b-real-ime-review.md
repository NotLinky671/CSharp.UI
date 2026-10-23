# TI-07 P3b 真实微软拼音与验证记录（2026-10-22）

本记录只覆盖正式 TSF 候选接入与本轮工具/回归验证，不代表 TI-07/P4 整体通过。系统输入法未安装/启用或改全局默认；微软拼音选择仅以 `TF_IPPMF_FORPROCESS` 为进程范围，运行后恢复原活动 profile。输入只由 `SendInput` 发往确认前台的本工具窗口；截图仅取自本窗口。

## 工具入口

`tools/CSharp.UI.ImeHostCheck/Program.cs` 增加显式 `--tsf` opt-in，主入口以 `[STAThread]` 在现有主线程启动；不传 `--tsf` 仍是原 IMM 默认。TSF 窗口逻辑高度 640，测试内容有足够滚动长度。工具读取已有正式 `TsfBridge.Session`、只读 UIElement 快照、`CompositionIdentity`、几何缓存和 setter 见证。`--selftest` 保持无 GUI、不发送输入。

## 真实 Windows 场景

所有命令均通过正式 `UiApplication` 宿主运行，`--profile mspinyin` 只做进程级 profile 选择并恢复。每个目录的 `stdout.txt`、`stderr.txt`、`exit.txt` 是该命令真实捕获输出及进程码；`ime-host-check.jsonl` 是工具原始事件日志。

| 场景 | 结果 | 证据 |
|---|---|---|
| `nihao-commit`，TSF | 通过，exit 0。真实活动 profile 为微软拼音；预编辑 `ni'hao` 时 setter 0；UIElement 候选 9 项并含“你好”，ActivateEx flags `0x80000004`；Space 精确追加 `你好`，setter +1。 | `nihao-visible-baseline/ime-host-check.jsonl`, `nihao-visible-baseline/s1-preedit.png` |
| `container-scroll`，TSF | 通过，exit 0。offset 0→7→324→恢复 0；小滚、完全裁出、恢复均观察到真实帧；完全裁出时几何为空而组合仍在；各状态 Composition SessionId/CompositionId、UIElement id/generation、预编辑相同，恢复前 setter 0；Space 后 `你好` / +1。四张 PNG 人工核对：小滚跟随、裁出隐藏、恢复再现。 | `container-scroll/ime-host-check.jsonl`, `container-scroll/s7a-composing.png`, `s7b-small-scroll.png`, `s7c-scrolled-out.png`, `s7d-restored.png` |
| `escape-cancel`，TSF | 通过，exit 0；Esc 后宿主存活、setter 0。 | `escape-cancel/ime-host-check.jsonl` |
| `enter-select`，TSF | 候选 ready 已确认（有效候选快照含“你好”）后发 Enter；通过安全判据，exit 0。微软拼音提交原始 `nihao`，setter +1、无换行、组合结束且宿主存活。日志明确 `enter_commits_raw_with_candidates=true` / “Enter确认原始拼音（本机微软拼音行为）”；**不声称 Enter 选中了汉字候选**。 | `enter-select-candidate-ready-final/ime-host-check.jsonl` |
| `digit-select`，TSF | 通过，exit 0；数字未泄漏，精确提交“你好”，setter +1。 | `digit-select/ime-host-check.jsonl` |
| `segmented`，TSF | 通过，exit 0；精确提交“你好世界”，setter +2。 | `segmented/ime-host-check.jsonl` |
| `unicode-direct`，默认 IMM 后端 | 通过，exit 0；精确写入 `中😀`，setter +2。此项是默认后端兼容 smoke，不是 IME 候选证据。 | `default-imm-unicode-direct/ime-host-check.jsonl` |

候选 ready 的 Enter 补验曾遇到工具调度等待问题：首个失败保留于 `enter-select-ready-first-failure/`。发现 `WaitFor` 的 UI `Query` 固定 10 秒、未尊重外层 5 秒预算；已使 `Query` 使用剩余 deadline，并保持 probe 由 Dispatcher 在 UI 线程执行。下一次候选 ready 运行正确确认 ready，但即时读取组合结束过早；改为有界等待真实结束后单次补验通过。工具构建中一次临时错误（Enter delta 局部变量缺失）亦已修正；最终工具 Debug build 成功。历史失败目录仅作诊断记录，不覆盖最终通过证据。

## 工具自检

实际命令：

```text
dotnet tools/CSharp.UI.ImeHostCheck/bin/Debug/net10.0/CSharp.UI.ImeHostCheck.dll --selftest --out docs/return/2026-10-22-ti07-p3b-real-ime-evidence/tool-selftest-final
```

结果：exit 0，`all_passed=true`。完整 JSONL/stdout/stderr/exit 在 `tool-selftest-final/`。

## Release 构建与测试

实际 Release 构建命令：

```text
dotnet build tests/CSharp.UI.OpenTK.Tests/CSharp.UI.OpenTK.Tests.csproj -c Release
```

结果：exit 0，0 errors（构建警告见原始 `release-build-final/` 输出）。当前为 Windows；Release 中运行以下现有测试类，无新增测试或测试替身：

| 测试类 | Total | Errors / Failed / Skipped | Exit |
|---|---:|---:|---:|
| `AcpTextStoreProtocolTests` | 75 | 0 / 0 / 0 | 0 |
| `TsfLayoutNotificationRegressionTests` | 1 | 0 / 0 / 0 | 0 |
| `CandidateSnapshotContractTests` | 38 | 0 / 0 / 0 | 0 |
| `TsfUiElementSinkTests` | 18 | 0 / 0 / 0 | 0 |
| `CandidateOverlayRenderingTests` | 4 | 0 / 0 / 0 | 0 |
| `HostAcpGeometryProviderTests` | 5 | 0 / 0 / 0 | 0 |
| `TsfCancellationTests` | 4 | 0 / 0 / 0 | 0 |
| `TsfSessionLifetimeTests` | 1 | 0 / 0 / 0 | 0 |
| `TsfHostIntegrationTests` | 1 | 0 / 0 / 0 | 0 |
| `RouterFallbackTests` | 7 | 0 / 0 / 0 | 0 |
| `WindowsInputBridgeContractTests` | 12 | 0 / 0 / 0 | 0 |
| **Total** | **166** | **0 / 0 / 0** | **all 0** |

各类完整 stdout/stderr/exit 文件位于 `release-final-<TestClass>/`；构建日志位于 `release-build-final/`。`TsfHostIntegrationTests` 与 `TsfLayoutNotificationRegressionTests` 的最终 Release 运行包含 Windows guard 之后的源码；新命名副本也在 `release-TsfHostIntegrationTests-final/` 和 `release-TsfLayoutNotificationRegressionTests-final/`。它们在本机 Windows 实际执行；Linux/macOS 未执行，不推断其运行结果。

Debug 的既有结果摘要是从当时工具输出转录，**不是原始 stdout**：协议 75、布局通知 1、候选快照 38、sink 18、overlay 4、geometry 5、取消 4、session lifetime 1、host integration 1 均 exit 0；再加 `RouterFallbackTests` 7 与 `WindowsInputBridgeContractTests` 12 的最终 Release 运行后，本次相关 Release 总数为 166。

## 未覆盖边界

本次不声称 P4 大矩阵完成。未在本轮真实微软拼音场景中执行：实际汉字候选的 Enter 选择（本机 Enter 提交原拼音）、普通英文输入、中途窗口移动/resize、响应式断点切换、DPI 变化、横向文本滚动、焦点/窗口失活切换、外部绑定修改、禁用/卸载、关闭时组合释放，以及其他输入法/非 Windows 平台行为。已证明的真实候选呈现及滚动行为限于上表的正式 Windows OpenTK 宿主场景。TI-07/P4 仍未整体验收。
