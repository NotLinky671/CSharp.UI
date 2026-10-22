# TI-07 R34E 恢复逻辑复审

审核日期：2026-10-21

状态：实现尚在运行，以下为实际源码初步发现，最终交付后复核。

## 元素 ID 0 是合法值

新 OverlayPresentation.Restore 中的 `ElementId != 0` 会拒绝恢复有效的微软拼音候选元素。真实 R29B 日志已经记录 `uielement begin id=0` 且确认为候选列表。应以 Live 是否存在、Valid、generation及id匹配来判断活动性，不可用0作为无效id哨兵。新增覆盖真实id=0的隐藏→恢复测试。

证据：artifacts/ime-work/r29b-child-scroll-real/tsf-host-check.jsonl seq71、74，均为 id0；seq74 candidate_count531。禁止把真实候选ID0判为不可恢复。

## 最终托管复核
R34E重试59253正常结束，恢复路径和唯一快照槽接线已复核；R34F20524正常结束，已移除id0哨兵，改以Live存在、Valid和id/generation匹配判断，新增合法id0恢复及End后不可恢复2项。Codex独立543/543通过，Release构建0错误，生产/保护文件哈希无漂移。测试输出已持久保存至2026-10-21-ti07-candidate-presenter-review-evidence。真实中文视觉验收尚未通过，不迁移生产。
首轮真实scroll进程97799运行中，日志artifacts/ime-work/r34f-hostdraw-scroll-real；等待用户点击自有窗口，未发送输入，不强制抢前台。

## 首轮真实窗口结果（不构成中文验收）
2026-10-21运行Release/STA、自绘候选scroll-follow，runner97799已结束exit2。窗口等待前台180秒后正常关闭；seq41 foreground-missing，最终text空、writes0、cleanup_failures0，没有发送测试输入。未生成候选视觉证据，不得标记scroll/clip通过。日志保存于候选呈现证据目录r34f-first-scroll-foreground-timeout.jsonl。后续需用户能点击自有测试窗口并保持前台时重新运行；不修改系统输入法配置、不强制抢前台。
