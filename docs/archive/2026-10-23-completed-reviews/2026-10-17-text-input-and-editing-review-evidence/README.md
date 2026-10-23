# Codex 独立复核证据

日期：2026-10-17（Asia/Hong_Kong）。仅包含自有探针固定测试输入的日志，不包含桌面截图。

- `codex-native-entry-wetype.log`：正式 STA 入口、实际 WeType 身份、pumped 输入；真实组合及一次“你好”结果。默认字符链仍产生后续字符回调，证明需明确去重所有权。不能替代微软拼音验收。
- `codex-microsoft-pinyin-controlled.log`：受控临时激活微软拼音，实际身份确认后仍无组合；独立恢复实现 finally 恢复并读回 `RESTORED=True`。来自独立辅助工具，不是此前有缺陷的 TemporaryProfileScope。

两份日志保存的是相应时刻观察，不表示当前工具版本已通过全部场景。当前审核结论见上级同日期 review 文档。

本轮新增：

- `codex-probe-tests-35.log`：最终探针35项独立回归通过。
- `codex-mspinyin-scancode-focus-loss.log`、`codex-mspinyin-scancode-focus-loss-retry.log`：两次扫描码对照均因前台丢失而SKIP；激活身份真实，恢复回读通过。第一轮原活动为英文键盘布局，第二轮为WeType，分别按本轮快照恢复。SKIP不能作为提交验收通过。

接续对照：`codex-ms-commit-current.log`（scancode、无warmup）和`codex-ms-commit-shift.log`（scancode、Shift预热）均记录目标微软拼音激活身份，但未产生组合，只有英文字流；两轮恢复均true。没有据此判定IMM不支持。

注意：先前 `codex-mspinyin-scancode-focus-loss-retry.log` 虽然最终SKIP，原始事件包含n→ni→ni'h→ni'ha预编辑及失焦返回niha。此前按SKIP汇总遗漏了这部分观察，现已更正；组合时实际profile尚待现场核对，所以仍不能作为微软拼音正常选词提交的验收通过。

最终接续回归：`codex-probe-tests-final.log` 为86项独立工具测试通过；`codex-final-profile-readback.log` 为收尾只读状态（WeType活动flags=3，微软拼音flags=0）。早期35项日志保留作历史，最终计数以86为准。工具单测不替代真实IME验收。

2026-10-17用户范围修订：微软拼音已移除且无需测试。上述微软拼音记录仅作历史保留，不再构成TI-02的前置门槛。WeType证据支持路线审核；真实候选定位、取消及生产宿主验收仍待TI-07/TI-10完成。
