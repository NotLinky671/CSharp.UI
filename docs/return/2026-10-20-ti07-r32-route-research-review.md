# TI-07 R32 UIElement路线研究复核

审核日期：2026-10-20。DSH ACP `devin/swe-2`研究任务4122正常结束，无可见窗口运行。

现有R26证据表明：pbShow=TRUE时，当前微软拼音Begin之后立即End；R27初始pbShow=FALSE保留live元素，Show(TRUE)内同步End。因此“稍后对已经系统显示且仍live的元素调用Show(FALSE)”目前没有有效对象，不能对已End对象调用，也不重复此前失败实验。

Codex另查[官方UILess Mode Overview](https://learn.microsoft.com/en-us/windows/win32/tsf/uiless-mode-overview)：文档明确允许TIP在Show(FALSE)时结束不支持隐藏状态的UIElement；pbShow=TRUE后不要求继续Update。文档描述的是接口契约，不能证明当前TIP一定支持保留组合的隐藏/恢复。

DSH研究报告的“没有任何其他文档路线”是研究判断，不是已证明的平台不可能性结论。其声称扫描458份日志的统计没有独立重做，不作为验收依据。当前不得宣称TI-07通过或Windows绝对不可能实现。

下一步核对成熟宿主对完全不可见组合的实际策略，区分几何通知、TIP行为和宿主主动结束组合。Chromium当前TSFTextStore::GetTextExt源码主要输出caret/composition bounds并设置clipped=FALSE，不能拿它当“裁剪后候选隐藏且保留组合”已经实现的证据：[源码](https://chromium.googlesource.com/chromium/src/+/HEAD/ui/base/ime/win/tsf_text_store.cc)。Mozilla相关零矩形候选位置缺陷亦只能作为线索：[Bug1121408](https://bugzilla.mozilla.org/show_bug.cgi?id=1121408)。不放宽原视觉验收，不迁移生产。
