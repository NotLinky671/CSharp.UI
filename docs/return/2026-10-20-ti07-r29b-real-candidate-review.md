# TI-07 R29B 真实候选框审核

审核日期：2026-10-20。实现模型：DSH ACP `devin/swe-2`；审核：Codex。

## 结论

真实 input child 路线的**内部滚动候选跟随通过，完全裁出候选隐藏失败**。TI-07 未完成，不提交、推送或迁移生产。

独立回归：Debug/Release 各448项正式测试、82项自检；生命周期38项托管及9项隐藏原生接线；默认4次隐藏启动、child Debug/Release × STA/MTA 4次隐藏启动；PMv2 实际DPI144几何34/34；执行器故障注入34/34。生产及保护文件SHA256与既有基线一致。证据在 `2026-10-20-ti07-tsf-prototype-review-evidence/codex-r29b-visible-final/`。

## 真实微软拼音

Release/STA，DPI144，微软拼音0804/inputprocessor，CLSID `81d4e9c9-1d3b-41bc-9e6c-4b40bf79e35e`、profile `fa550b04-5ad7-411f-a5ac-ca038ec515d7`。两次运行均有完整 `ni'hao`、span0..6、begin1/end0/session1/focus_events1、绑定写入0贯穿测量。进程退出3只表示脚本等待视觉评定。

- scroll-follow：Codex亲看前/后/恢复三张窗口截图及三张整屏截图。真实文字和input上移11物理像素，候选同步上移，恢复回到原位。视觉通过。
- clip-out：input从 `(107,125)-(813,192)` 移到 `(107,18)-(813,85)`；固定viewport顶部125，input底部85，已完全移出且有40px余量。真实交集严格全零，文字不可见。Codex亲看三张窗口截图及三张整屏截图，候选仍显示在原位置，隐藏失败。恢复文字与实时组合均正常。

日志与窗口裁图：`2026-10-20-ti07-tsf-prototype-review-evidence/r29b-child-real/{scroll,clip}/`。整屏原件含其他桌面内容，仅留在私有 `artifacts/ime-work/r29b-child-{scroll,clip}-real/`，不复制到交付证据。

## 下一项有界实验

R30只在完全裁出后，显式隐藏**自有input子窗口**并在恢复时显示，不操作候选窗口。使用NOACTIVATE窗口可见性变更，真实焦点/组合计数若变化即失败，不SetFocus修复、不取消重打组合。这是尚未验证的假设，不能将Win32操作成功视为候选隐藏成功。默认关闭，仅隔离原型显式测试，不修改生产。

不重复此前已经失败的IMM候选关闭、UIElement结束后Show、零高度窗口或激活flag路线；不把几何裁剪伪称为文本视图销毁通知。
