# TI-01 IME 平台决策证据目录

配套决策记录：[../2026-10-17-ime-platform-decision.md](../2026-10-17-ime-platform-decision.md)。

## 当前（2026-10-17 本轮，事件字段为 `managed_tid`/`win32_tid`）

| 文件 | 说明 |
|---|---|
| `ti01-2026-10-17-final-batch-summary.txt` / `.json` | 本轮最终 `--scenario all --input-mode batch` 结果，逐项 `UNVERIFIED`/`OBSERVED`/`PASS` |
| `ti01-2026-10-17-active-profile.jsonl` | STA/apartment、焦点后**实际**活动 profile、输入语言、临时 profile 事件提取 |
| `ti01-2026-10-17-mspinyin-temporary-enable-fail.jsonl` | 微软拼音快照 + `ActivateProfile(FORPROCESS|ENABLEPROFILE)` E_FAIL + 恢复校验 |
| `ti01-2026-10-17-screenshot-scope.txt` | 截图范围核对：922×576 自有窗口 vs 2560×1600 桌面 |
| `ti01-2026-10-17-own-window-screenshot.png` | 自有窗口截图示例（无候选、无整屏） |

## 本轮返修（2026-10-17 15:13–15:25，DSH；事件字段同 `managed_tid`/`win32_tid`）

| 文件 | 说明 |
|---|---|
| `ti01-2026-10-17-r2-run-summaries.txt` | 4 次真实运行（`info` + 3 次 `ime-commit`）的场景结论摘录 |
| `ti01-2026-10-17-r2-identity-and-native-mode.jsonl` | 初始化关键时刻只读身份快照 + 同一时刻 IMM open/conversion + native 设置回读 |
| `ti01-2026-10-17-r2-sta-review-identity-flags.txt` | 独立只读入口（`ImeStaReview --identity-only`）：活动 WeType `flags=3`、微软拼音 `flags=0` |
| `ti01-2026-10-17-r2-sta-review-after-runs-1.txt` / `-2.txt` | 工具运行后再跑两次同一命令，结论稳定一致 |

本轮结论要点（详见决策记录 §4.1）：`native-mode-readback` 4/4 PASS（只认回读值）；
`profile-temporary-enable` FAIL（微软拼音未启用，`ActivateProfile` 返回 `0x80004005`），恢复回读
`RESTORED=true`；真实按键场景均 SKIP（探针窗口非前台）。**未取得任何真实微软拼音组合/提交/取消/候选证据。**

## 历史（早期观察，保留作对照，不代表当前状态）

这些文件来自更早的原型轮次，事件字段为旧的 `tid`/`stage` 形态，或结论已被后续证据修正；
仅作历史对照，**不得**作为当前通过依据。

| 文件 | 说明 |
|---|---|
| `mspinyin-activation-extract.txt`、`tsf-engagement-extract.txt` | 早期 `ActivateProfile` 失败（旧字段）观察 |
| `probe-all-2-summary.*`、`probe-all-2-events.jsonl`、`probe-all-2-analysis.txt` | 早期 `all` 轮次（含“未组合时 PASS”的旧判据，已修正） |
| `probe-repeat-*`、`probe-all-final-foreground-lock-console.log` | 早期重复/前台锁观察 |
| `settings-snapshot-before.txt` / `settings-snapshot-after.txt` | 早期输入法设置快照 |
| `binary-evidence.json` / `binary-evidence.txt` | 依赖二进制身份（仍然有效） |
| `glfw-3.4-source-scan.txt` | GLFW 3.4 平台后端接入点扫描（仍然有效） |
