# TI-07 P3b 交付记录：正式 OpenTK 宿主 TSF 接线与 UI-less 候选呈现

日期：2026-10-22
状态：**P3b 范围内验收完成**（Debug/Release 指定回归、真实微软拼音基线、真实滚动/裁出/恢复均通过）。
**TI-07 整体与 P4 未完成**：本记录不声称整体 TI-07 交付；未覆盖矩阵见 §5。未提交、未推送。

依据：[P3b 设计](../plan/2026-10-21-ti07-p3b-host-integration-design.md)、[正式接入计划](../plan/2026-10-21-ti07-production-tsf-integration-plan.md)、[宿主接线执行记录](../return/2026-10-21-ti07-p3b-host-integration.md)、[独立审核](../return/2026-10-22-ti07-p3b-independent-review.md)。

## 1. 交付范围

- 单次 `ITfThreadMgrEx::ActivateEx(TF_TMAE_UIELEMENTENABLEDONLY)` + 真实 `GetActiveFlags` 校验；失败逆序释放并回退默认 IMM，不双激活、不并行写入来源。
- `ITfUIElementSink` 生命周期：Begin 只识别候选类型、id 0 合法；Update 才按页读取；End/替换/失焦/epoch 立即失效；RCW 恰好释放一次；Clear 防嵌套、Dispose 先置失效。
- 生产 `CandidateSnapshot`/`CandidatePageReader`（按页有界、严格页表/错误 HRESULT、buffer 配平、文本截断不拆代理对）。
- `CandidateOverlay`：快照与几何独立；测量缓存（快照/字体/DPI/可用宽度）；客户区/上下放置/可见交集/有限值；内容之后、同一 `BeginFrame` 内、`ToArray` 之前绘制；`try/finally` 裁剪恢复。
- `HostAcpGeometryProvider`：文本几何与显示表面独立缓存；`GetTextExt` 在 store 同一读锁内构造 native 代次/store 版本/完整文本身份；不匹配/越界/完全裁出如实失败；不伪造原点。
- 布局通知：安全阶段发送、mask 门控；撤销/释放清空延迟队列，旧连接排队的项不会送给新连接；不可用几何仅“可见→不可用”排队一次（真实宿主验证无自激）。
- 正式宿主接线：`UiRuntime.FrameOverlay`、按需唤醒、最小化/移动/resize/DPI 失效、失败回退 IMM；只读组合身份接缝（`CompositionIdentity`）用于真实滚动验收，不扩张公开 API。

## 2. 验证结果

### 2.1 Release（原始日志已落盘，均 exit 0）

同一最终 Release 构建后逐类运行（`release-final-*`；两个平台守卫测试另在 `release-TsfHostIntegrationTests-final/`、`release-TsfLayoutNotificationRegressionTests-final/` 以同一构建复跑），原始 `stdout.txt`/`stderr.txt`/`exit.txt` 见 §4：

| 类 | 计数 |
|---|---|
| `AcpTextStoreProtocolTests` | 75/75 |
| `CandidateSnapshotContractTests` | 38/38 |
| `TsfUiElementSinkTests` | 18/18 |
| `HostAcpGeometryProviderTests` | 5/5 |
| `CandidateOverlayRenderingTests`（真实 GPU） | 4/4 |
| `TsfCancellationTests` | 4/4 |
| `TsfSessionLifetimeTests` | 1/1 |
| `TsfLayoutNotificationRegressionTests` | 1/1 |
| `TsfHostIntegrationTests` | 1/1 |
| `RouterFallbackTests` | 7/7 |
| `WindowsInputBridgeContractTests` | 12/12 |
| **合计** | **166/166，Errors 0，Failed 0** |

`release-build-final/exit.txt` = 0；所有最终 `release-*` 的 `stderr.txt` 为空、`exit.txt` 为 0；工具 `selftest` 全部通过（`tool-selftest-final/`，exit 0）。首次构建失败日志 `build-scroll-helper/`、`build-enter-end-wait/`（exit 1）与修复后 `build-scroll-helper-retry/`、`build-enter-end-wait-retry/`（exit 0）保留为轨迹。

Debug 轮 147 项同类（当时未含 RouterFallback/WindowsInputBridgeContract）亦 Failed 0 / exit 0；该轮只有 real-ime 会话执行转录，原始 stdout 未持久，故以独立审核 §5 记录，不伪造原文。最终 Release 为原始日志。

跨平台测试约定：`TsfHostIntegrationTests` 与 `TsfLayoutNotificationRegressionTests` 在非 Windows 直接 `return`（无 TSF/msctf，避免 `Thread.SetApartmentState` 在 Linux/macOS 抛 `PlatformNotSupportedException`），沿用 `TsfSessionLifetimeTests` 的既有模式。加守卫后已在 Windows 以最终构建复跑通过（1/1、1/1）；Linux/macOS **未执行**，不声称通过。

### 2.2 真实微软拼音（正式 OpenTK 宿主，Debug 工具 `--tsf`）

- **可见基线**：640 逻辑高窗口（480×640 @1.5），9 项真实候选、蓝底选中“你好”、页脚第 1 页；预编辑 `ni'hao` 写入增量 0；Space 精确提交“你好”、写入增量 1；exit 0。主代理已亲读截图与 JSONL。
- **滚动/裁出/恢复**：offset 0→7→324→0；小滚候选跟随；完全裁出时几何 `unavailable`、候选隐藏但组合保留；恢复后几何重发。`initial/small/hidden/restored` 的 `composition_session_id=1`、`composition_id=0`、UIElement id 0、generation 3、preedit `ni'hao` 全部一致；setter 全程 0；Space 精确“你好”、+1。`layoutNotifications 6→7→8→9`，hidden 期间反复 clear 不增长（自激守卫有效）。
- **Escape/数字/分段**：Escape 取消零写、文本不变、host 存活；数字选词精确“你好”一次、数字未写入；连续分段精确“你好世界”、两次写入，exit 均 0。
- **默认 IMM Unicode 直输（合成字符通道）**：`default-imm-unicode-direct/` exit 0，提交 `中😀`、2 次标量写入；工具明确标注这是合成字符通道见证，**不作为 IME 证据**。
- **Enter（候选 ready 前置）**：`enter-select-candidate-ready-final/` exit 0；`candidate_ready_before_enter=true`、committed `nihao`、delta 1、无换行/submit 泄漏、组合结束 true、host 存活、`safety_result=pass`。本机微软拼音在有候选时仍提交原始拼音，故 `enter_selected_candidate=false`、`enter_outcome=Enter确认原始拼音（本机微软拼音行为）`；按 README 判据不要求 CJK。此前 10s 调度超时（WaitFor/Query 预算）与绑定写入后立即读 `IsComposing` 的时序问题均已修复；`build-enter-end-wait` exit1/`retry` exit0 保留为修复轨迹。

## 3. 失败与修复轨迹

- 布局通知旧表述声称 sink generation，实际由 `UnadviseSink` 清空延迟队列 + 禁止并发替换保证；已统一文档并新增 3 项协议边界测试（ACP 72→75）。
- 真实宿主发现持续 Unavailable 时每安全阶段重复排 pending 的自激风险，已按“仅可见→不可用排一次”修正；Release 通知回归 1/1 与真实 scroll 计数验证。
- 真实滚动缺少可读核心组合身份，新增 internal 只读接缝 `ICompositionIdentitySource` + `TsfInputBridge.CompositionIdentity`（不扩张公开契约），使裁出时也能证明同一组合。

## 4. 证据索引（不复制大证据集）

小型索引/manifest：[`2026-10-22-ti07-p3b-delivery-evidence/README.md`](2026-10-22-ti07-p3b-delivery-evidence/README.md)（只指向 `docs/return/` 原始证据，不复制截图与大日志）。

根目录：`docs/return/2026-10-22-ti07-p3b-real-ime-evidence/`

| 目录 | 内容 | 原始性 |
|---|---|---|
| `nihao-visible-baseline/` | 可见候选 JSONL + `s1-preedit.png` | 原始 |
| `container-scroll/` | JSONL、stdout/stderr/exit、s7a–s7d PNG | 原始 |
| `escape-cancel/`、`digit-select/`、`segmented/`、`enter-select/`、`enter-select-candidate-ready/`、`enter-select-candidate-ready-final/` | JSONL、stdout/stderr/exit | 原始 |
| `build-enter-ready-bounded/`、`build-enter-end-wait/`、`build-enter-end-wait-retry/` | bounded Query / 组合结束等待的构建（失败与修复） | 原始 |
| `default-imm-unicode-direct/` | 默认 IMM Unicode 直输 JSONL/stdout/stderr/exit | 原始 |
| `release-build-final/` | 最终 Release 构建 stdout/stderr/exit | 原始 |
| `release-final-AcpTextStoreProtocolTests/`、`release-final-CandidateSnapshotContractTests/`、`release-final-TsfUiElementSinkTests/`、`release-final-HostAcpGeometryProviderTests/`、`release-final-CandidateOverlayRenderingTests/`、`release-final-TsfCancellationTests/`、`release-final-TsfSessionLifetimeTests/`、`release-final-TsfLayoutNotificationRegressionTests/`、`release-final-TsfHostIntegrationTests/`、`release-final-RouterFallbackTests/`、`release-final-WindowsInputBridgeContractTests/`；以及两个平台守卫测试的新目录 `release-TsfHostIntegrationTests-final/`、`release-TsfLayoutNotificationRegressionTests-final/` | 最终逐类 Release 计数与 exit（166/166） | 原始 |
| `tool-selftest-final/` | 最终 selftest JSONL、stdout/stderr/exit | 原始 |
| `enter-select-ready-first-failure/` | Enter 候选 ready 首失败（调度超时）原始证据 | 原始 |
| 独立审核（real-ime） | [2026-10-22-ti07-p3b-real-ime-review.md](../return/2026-10-22-ti07-p3b-real-ime-review.md) | 执行记录 |
| 独立审核（review） | [2026-10-22-ti07-p3b-independent-review.md](../return/2026-10-22-ti07-p3b-independent-review.md) | 记录（Debug 为会话转录） |

宿主接线早期 summary 转录仍见 [`2026-10-21-ti07-p3b-host-integration-evidence/`](../return/2026-10-21-ti07-p3b-host-integration-evidence/README.md)，已标注非原始 stdout。

## 5. 未覆盖 / P4 矩阵（本记录不声称通过）

- 窗口移动/resize、可用显示器 DPI 变化、横向文本滚动、普通英文、IME Unicode 路径（默认 IMM 合成字符通道已通过，不作为 IME 证据）。
- 失焦/失活（含窗口级 alt-tab 时候选收敛）、外部绑定变更、撤销/重做。
- 断点响应式重排、锚点跟随、最小化恢复、隐藏/恢复、外部更新取消、关闭窗口。
- 候选鼠标选择未实现；不把 fake/合成候选当作真实输入证据。
- 以上通过前，TI-07 整体不勾选、不交付、不提交推送。
