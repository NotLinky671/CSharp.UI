# TI-07 P4 发布集成记录（p4-release-integrator 独占）

日期：2026-10-22 · 集成者：p4-release-integrator

## 提交链（origin/main 已推送，push exit=0）

- `75f995e..b320745` `TI-07: production Windows TSF text input with host-routed dispatch and deactivation-safe cancel` — 1761 files +183380/−118，主审批准的 TI-07 P1~P4 精确范围。
- `03a78de` `Keep local agent workflow changes out of TI-07 delivery` — AGENTS.md index-only 恢复为 b320745^ blob（6362f0b）。

## 集成事故（如实记录）

- **事故**：staging 分两步执行时，第二步 re-stage 修改文件清单未重新排除 `AGENTS.md`，导致它被并入 b320745。发现后未 amend/reset/force（规则禁止），按主审方案 `git restore --source=b320745^ --staged -- AGENTS.md` + follow-up commit 纠正。
- **定点验证**：工作区 AGENTS.md sha256 前后不变 `a159a7413e7252a64ad400cccd1ca9c873a2e61e5318429d17abc766bf434ffa`；HEAD tree AGENTS blob `6362f0b2…` == b320745^ blob；用户修改仍为未提交 working-tree 状态。
- **教训**：分阶段 add 时排除规则必须在每一次 add 调用上重复应用，不能只在第一轮过滤。

## 未提交项（按主审裁定保留原位）

- `AGENTS.md` — 用户/工作流上下文，modified 未提交（有意）。
- `docs/agents-notebook/acp/check-devin.mjs` — 内部 ACP 检查脚本，untracked 未提交。
- `build-full.log,verbosity` — 仓库根 throwaway 构建日志，untracked 未提交。
- `artifacts/`、全部 `bin/`/`obj/` — gitignored，恢复 DLL/PDB/decompiled 备份留在 artifacts 不入库。

## 最终验证摘要（同源 sha256=58626b17… NativeCompositionOwner.cs）

- slnx Debug+Release 0 错；ImeHostCheck/HostRoutingSpike 双配置 0 错。
- Release 2053/0：UI 549 + OpenTK 666 + TsfHostCheck.Tests 581 + Integration 53 + NativeEditReference 41 + Text 67 + ImeProbe 96（后两项经进程级 TEMP/TMP 重定向）。
- Debug：UI 549 + OTK 666（TsfCancellation 10/10）+ TSFC 581 + TextFieldComposition 66。
- postclean2 真实三场景（minimize/hide/window-deactivate）：失活段零写、session_id 1→2、恢复唯一你好提交、候选 PNG 可读。
- 证据：`docs/delivery/2026-10-22-ti07-p4-delivery.md` + `docs/delivery/2026-10-22-ti07-p4-delivery-evidence/`（持久 raw logs + source manifest）；GUI 证据索引 `docs/archive/2026-10-23-completed-reviews/2026-10-22-ti07-p4-real-validation/`。

## 边界

TI-08~10 未开始；默认 IMM 不变；单显示器固定 DPI 144（无多 DPI 证据）；unicode-direct 为合成通道；--english-keyboard 为 en-US KEYBOARDLAYOUT profile 非拼音内部英文；NativeCompositionOwner.cs 为反编译恢复（非逐字等价，审计链见 restored-core-review）。
