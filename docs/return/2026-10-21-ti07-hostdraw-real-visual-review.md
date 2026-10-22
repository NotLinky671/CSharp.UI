# TI-07 自绘候选隔离原型真实视觉审核

审核日期：2026-10-21

## 结论与范围

用户同意的应用绘制候选路线中，隔离原型的滚动跟随、完全裁出隐藏及滚回恢复，均已通过 Codex 真实微软拼音视觉审核。每项运行的三张窗口图和三张整屏图均已逐张查看，共12张。脚本退出3表示待视觉审核；本记录补充实际视觉结论，没有修改脚本退出码或门禁。

这是隔离 TSF/GDI 工具的路线验证，不代表原生候选窗口隐藏成功，不代表 OpenTK 生产迁移或 TI-07 整体验收完成。生产及保护文件尚无改动。恢复后唯一中文提交另行补验，不以本次视觉通过替代提交能力。

## 实际环境与不变量

Release 可执行文件直接运行，专用 STA UI 线程，PMv2，DPI144（150%）。实际 Microsoft Pinyin：lang0804、CLSID81d4e9c9-1d3b-41bc-9e6c-4b40bf79e35e、profile fa550b04-5ad7-411f-a5ac-ca038ec515d7。输入完整 ni'hao、span0..6；整个三阶段测量均保持 begin1/end0/session1/input_focus_events1、绑定写入0。当前活动候选 id0/generation2、当前页9条、首项你好。未通过取消重输、焦点挂起或重新创建组合来恢复候选。

## 滚动跟随：通过

原始目录：`artifacts/ime-work/r36-hostdraw-scroll-real/`。窗口及整屏 baseline/scrolled/scroll-restored 六图全部查看。

真实行矩形由 (123,139)-(211,178) 上移11物理像素到 (123,128)-(211,167)，再恢复原位。候选列表同步上移11物理像素并恢复，列表内容、首选高亮及页脚均可见，没有标题栏覆盖、页脚裁断或额外原生候选遗留。日志seq240/282/315记录完整不变量；seq351为checks_pass=true、pending-visual。

## 完全裁出隐藏及恢复：通过

原始目录：`artifacts/ime-work/r36-hostdraw-clip-real/`。窗口及整屏 baseline/clipped/clip-restored 六图全部查看。

输入可见交集从 (107,125)-(813,192) 变成空矩形，文字行移到 (123,32)-(211,71)，明显位于viewport top125之外。裁出阶段窗口和整屏中均无文字或候选列表。实际 overlay IsWindowVisible=false，hidden_by_clip=true；活动候选 id0/gen2/9条仍保持。滚回后原文字及候选全部恢复，IsWindowVisible=true，身份和组合计数不变。seq238/284/317为三段诊断，seq271/274/278/281为隐藏期逐图检查，seq353脚本检查通过。

## 持久证据与限制

脱离可清理目录的窗口图及完整日志保存在 [滚动证据](2026-10-21-ti07-candidate-presenter-review-evidence/r36-real-scroll/tsf-host-check.jsonl) 和 [裁剪证据](2026-10-21-ti07-candidate-presenter-review-evidence/r36-real-clip/tsf-host-check.jsonl)。整屏图包含桌面其他内容，仅保留本地私有 artifacts，不纳入提交材料。

本轮独立正式测试570/570通过。候选总数256的原型上限会暂时拒绝字母n对应531项列表，完整nihao对应94项时正常；该限制仍需后续改为按页限制分配，不能视为完整候选产品能力。鼠标选词、任意小窗口、完整输入法矩阵及生产 OpenTK 接入均不在本轮通过范围。

## 恢复后提交补验：通过

2026-10-21 R37在同一ReleaseSTA/DPI144环境重复完成裁剪三阶段，六张窗口/整屏图再次核对通过；另查提交后窗口图，文字精确为你好、候选消失。seq318/319记录提交前零写入、同组合及选中你好；seq320/354为真实Space down/up；seq337唯一binding记录你好/source=tsf-composition；seq362图前后状态一致；seq363提交后begin1/end1/session1/focus1、writes1、presenter清空及overlay隐藏。不存在取消后重新输入。最终独立581/581测试及Release构建通过。

完整交付范围、复现命令、证据与已知限制见[隔离原型验收记录](../delivery/2026-10-21-ti07-candidate-prototype-delivery.md)。测量中的两项视觉门槛及恢复后唯一中文提交均已通过；TI-07生产任务保持未完成。

R37日志的native_focus字段在runner线程调用GetFocus，显示0不能表示UI线程失焦；真实输入门禁及post safe point在UI线程采样，focus_events始终1。本结论以真实UI线程守卫及连续性计数为准，不使用该runner线程诊断字段推导焦点。
