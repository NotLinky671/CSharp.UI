# TI-07 R29B 连续性检查审核

审核日期：2026-10-20

本轮只审核隔离原型的连续性检测及按键守卫，不代表候选框行为通过。

## 独立验证

- Debug/Release 各 329 项 TsfHostCheck 正式测试和 82 项 selftest 通过。
- Debug/Release 各 95 项 ImeProbe 正式测试通过。
- 四次默认隐藏宿主运行通过；独立生命周期审核 38 项托管、9 项隐藏原生接线检查通过。
- 生产及受保护文件 hash 校验通过。
- 证据：`2026-10-20-ti07-tsf-prototype-review-evidence/codex-r29b-continuity/`；输入脚本原始输出暂在 `artifacts/ime-work/codex-r29b-imeprobe-*-tests.log`。

源码确认：真实输入 focus 消息计数进入快照；begin/end/session 与 text/span 在同一次 store 锁内读取；真实 ScenarioLifecycleHost 转发新字段；SameInvariant 检查连续性及基线仍在组合中；真实 SendRaw 共享守卫在扫描码映射和 SendInput 前执行。

## 尚需改进

1. `ForeignForegroundStillAbortsAfterGuardPasses` 虽注入 NativeFacts，但失败日志的 DescribeWindow 仍直接调用 user32。该测试不能宣称整个控制流完全无原生调用。需为真实共享路径提供诊断替身或无原生的测试诊断路径，保留默认真实诊断。
2. `SameTextRestartIsDetectedByCounters` 的第一次快照取在 End 后，不能直接证明两个组合中的相同 text/span 被连续性规则拒绝。需在第一次组合活跃且 span 完整时取样，结束后用相同范围重新开始，比较两次真实活跃快照并验证 SameInvariant 拒绝。

这两项纳入下一轮隐藏接线任务一并修正。隐藏 child 模式需确认真实 TSF HWND 关联及零 show/focus/input；可见几何场景尚未接入。真实微软拼音跟随与隐藏均未验收，TI-07 继续未完成，不提交或推送。

## 隐藏接线返修及独立原生结果

两项测试证明力问题均已修正并复核源码：发送门的失败诊断可注入，注入后缺省诊断退化为纯句柄；相同 text/span 的重启测试使用真实 store 的两次活跃组合快照，交给实际 SameInvariant 拒绝。

Codex 独立重建和执行：Debug/Release 各 337 项原型测试、82 项自检、96 项 ImeProbe 测试通过；默认四隐藏宿主及 38 项托管、9 项原生接线审核通过；生产/保护 hash 未变化。

真实 `--hidden-host --child-input-surface` 四次检查（Debug/Release × MTA/STA）均 exit0：三窗口真实父子链及 PID/TID 正确，实际 AssociateFocus 使用输入 child，store.GetWnd 返回同一 child，show=0、input focus events=0、全部不可见，TSF 资源清理无失败。证据：`2026-10-20-ti07-tsf-prototype-review-evidence/codex-r29b-child-hidden/`；默认回归：`codex-r29b-hidden-wiring/`。

AssociatedHwnd 是成功 AssociateFocus 后的真实传入句柄诊断，结合实际 native HRESULT/日志证明关联调用参数；并未通过重新 AssociateFocus 冒充只读查询。此处没有声称 Windows 提供了额外的只读 HWND 关联查询 API。

放行可见场景执行器实现；真实候选移动及隐藏仍未验收。TI-07 未完成。
