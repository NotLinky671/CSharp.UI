# TI-07 P4 交付证据归档笔记（进行中，非交付文档）

日期：2026-10-22 · 角色：交付证据归档独立工作流（不接管产品/GUI/既有 audit；仅 relay + 收集实际证据）。
来源：本笔记只记录已实际观察到的命令/exit/计数/日志；未经真实验收的项目不写结论。

## 工作流状态

- 主代理 vibe_send 对 good/real/audit 三者 `agent://` 直发 no longer resolves parent；本工作流承担 relay，保持原 good/real session 不重建。
- 2026-10-22 已转达主审消息（见下）。

## 已 relay 的主审指令（转达时点快照）

### → p4-cancel-safety（good，产品修复）

1. 最新补丁：SC_MINIMIZE / WM_SHOWWINDOW false / WA_INACTIVE / KILLFOCUS 提前 fence 登记；owner 生命周期禁止旧 Start 恢复；预期取消 9/9、routing 22/22。
2. 主审发现 `TsfHostSession.cs:893-900`：仅 SC_MINIMIZE 请求即永久 fence。若下游取消命令未真正失活，可能废弃活窗口输入——需真实结果/设计确保取消"请求"不害输入。
3. `TsfHostSession.cs:872-875` XML 注释仍声称 KILLFOCUS 先于 capture，与 minimize-trace2 实际观察矛盾，须修。
4. NativeCompositionOwner.cs 误删恢复（21:29 Release DLL 反编译）：记录恢复来源 hash/备份/丢失 XML 注释与命名差异；只宣称实际验证（Release 编译 0 错、TsfCancellationTests 8/8），不宣称源码等价。
5. 全局 Reset 租约测试隔离修复：主审已 read 证据，记为完成。

### → p4-real-validation（real，工具+真实 GUI）

1. good 补丁落地后立即**新目录**三场景复验：minimize-restore / hide-restore / window-deactivate；严格判据：失活阶段 0 旧段写 + 恢复新 epoch 唯一提交。
2. 与 good 直接协调 dotnet/GUI 独占。
3. 不得弱化未 drain 独立结果 1 写回归判据。
4. 旧工具 exit 0 但日志有 nihao 泄漏的目录不得标 PASS（见下表"不采纳"行）。
5. 稳定后按既定清理计划移除 wnd-tap/owner-diag 接线并实际 smoke；保留 binding-write/phase trace。

## 证据索引（截至 relay 时点；详见 real 的 README manifest）

- PASS（真实验收、判据严格）：focus-switch-cancel、external-update-cancel、breakpoint-reflow、anchor-follow-fixed、resize-samebranch-fixed、disabled-unmount-v1、horizontal-scroll-v2、unicode-direct（合成通道）、undo-redo、close-window、english-uithread（ascii-plain）、window-deactivate-trace3（取证 v3：cancel-intent→Terminate→drain suppressed×4→零写）。
- 工具 exit 0 但**不采纳为 PASS**（日志证 nihao 泄漏）：minimize-restore-cancel-fixed、hide-restore、hide-restore-cancel-fixed。
- 待复验：minimize-restore 待 good 归因修复后新目录复跑；deactivate/hide 已 PASS（见下行）；wnd-tap/owner-diag 清理后实际 smoke。
- **本轮新证据（good 更早窗口消息边界补丁后，real 新目录严格零写判据）**：
  - `deactivate-trace4/` window-deactivate exit=0 **PASS 零写**：WM_ACTIVATE 边界先于 TIP capture → cancel-apply terminated → drain suppressed×4 → Retire → 恢复新 epoch。
  - `hide-trace/` hide-restore exit=0 **PASS 零写**：WM_ACTIVATE@155 → cancel-apply t12 → suppressed t13-16。
  - `minimize-trace3/` minimize-restore exit=1 **FAIL**：ShowWindow(SW_MINIMIZE) 无 SC_MINIMIZE/SHOWWINDOW tap；TIP 经消息泵外 TSF focus 通道先于所有窗口消息 capture（seq164 End@t14 先于 seq165 WM_KILLFOCUS）；pendingCancel=1 登记未执行 → nihao 1 写。**更早窗口消息边界对该路径无效（仅此路径证据，不可外推）**，主审已令 good 停止该变体并先做最小取证（自有 HWND GetFocus/GetForegroundWindow/IsIconic 或 TSF focus 通知既有接缝），禁止全局状态吞旧结果/drain 顺序回归变更/文本特判；SC_MINIMIZE 永久 fence 无效方案须移除不留死重量。
- **段号 suppression 方案已被主审叫停**：主审亲读 `CancelHost.StartComposition:997-1002`——显式 `Owner.Capture(Start)+Drain`，good history:1209"独立段=0/失活批=1"为源码误读（测试B同样真实 capture Start）；B 与 minimize 均 Start→End/Edit pending→fence，Start 计段无法区分。主审未批准段号方案、未允许先实现再取证。当前仅授权：捕获瞬间自有 HWND 物理焦点状态取证（GetFocus/GetForegroundWindow/IsIconic）+ 真实独立提交对照，或 TSF focus 通知真实早边界；方案变更须给真实可达反例与非夹具归因，新回归须用同一真实捕获链（含 Start）。
- **FocusProbe 取证完成（两新目录同构建，real 执行）**：
  - `minimize-probe/` minimize-restore exit=1 FAIL（仍泄漏，取证目的）：失活段 capture t12-15 全部 `focus=0x0 focusIsHwnd=False`；fgIsHwnd=True、iconic=False。
  - `anchor-probe/` anchor-follow exit=0 PASS（对照）：正常提交 capture t12-14 全部 `focusIsHwnd=True`，WRITE 你好×1。
  - 结论：capture 时刻 `GetFocus==自有hwnd` 在该两跑严格二分真实失活 vs 正常独立提交（fg/iconic 不区分）。判据候选=focusIsHwnd，已转 good；不预设等价，实现后须实测。原始 JSONL/stdout/stderr/exit 全留存。
- **good focus-attribution 实现落地，待真实复验**：
  - 实现：`NativeCompositionFact.CapturedDeactivated`（bool? 三态，capture 时冻结）；首个 deactivated==true fact 到达时执行 `ExecuteDeactivationFences()`；deactivated==false 独立结果在 fence 前应用→恰好一次提交；suppression = armed AND (deactivated==true OR (null && opId>ticket 回退))；CancelHost 注入 SimulatedDeactivated 供协议测试。
  - good 自报本地验证：TsfCancellationTests 9/9、邻居 51/51、TextFieldCompositionTests 66/66；证据 `artifacts/ime-work/p4-cancel-safety/test-fa4*.stdout.txt`；审核记录 `docs/return/2026-10-22-ti07-p4-cancel-safety-review.md` §6。
  - **待 real 新目录真实复验**：minimize/hide/deactivate → 失活 0 旧段写 + 恢复新 epoch 唯一提交（未验证前不写交付结论）。
- 原两个验收判据同时保持：失活阶段 0 旧段写 + 恢复新 epoch 唯一提交。
- DPI 变化：单 DPI 屏（144），不改系统 DPI——环境边界。
- 微软拼音内部英文模式：已用既有 en-US KEYBOARDLAYOUT profile 覆盖普通输入；不声称拼音英文模式。

## 交付文档

最终 `docs/delivery/2026-10-22-ti07-p4-delivery.md` + 同前缀 evidence 索引：仅在全部真实验收且主审通过后创建；当前不创建。
