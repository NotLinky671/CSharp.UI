# TI-07 R31 真实TSF文档焦点挂起验收

审核日期：2026-10-20。DSH ACP模型`devin/swe-2`；Codex独立审核。

结论：假设失败，不迁移生产，不提交或推送。用户允许的是隔离实验，不允许将组合结束后重输当作恢复。

最终独立回归通过：Debug/Release各504正式测试、105自检；生命周期38托管+9隐藏原生接线；默认隐藏4次及child隐藏4次；原执行器37/37；新增共享焦点审核7/7；实际DPI144几何34/34。生产和保护文件SHA与基线相同。证据目录`2026-10-20-ti07-tsf-prototype-review-evidence/codex-r31-final/`及本地独立审核日志。测试不能替代真实TIP行为。

真实Release/STA、DPI144、微软拼音0804及确切CLSID/profile。基线是完整`ni'hao`、span0..6、begin1/end0/session1/input_focus_events1、writes0。几何完全裁剪后，SetFocus(null)过程中微软拼音同步改变文本并结束组合：

- seq273：composition end，commit=true，文本nihao。
- seq274：binding write，sequence1。
- seq277：挂起后GetFocus仍返回文档管理器，操作判失败。
- seq305：checks_pass=false，writes_delta=1；最终exit1、cleanup_failures0。

没有进入有效的clipped/restored抓图阶段，不能声称候选隐藏通过。原始日志`artifacts/ime-work/r31-document-focus-real/tsf-host-check.jsonl`及同名前缀runner.log。测量期间未发送补输入，也未取消重打组合。基线整屏图保持私有。

TI-07状态仍为：R29B内部滚动跟随真实通过；完全裁剪候选隐藏失败。R30自有HWND隐藏无效，R31文档焦点挂起会提交组合，均排除。
