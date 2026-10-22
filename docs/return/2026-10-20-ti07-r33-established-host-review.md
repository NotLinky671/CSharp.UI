# TI-07 R33 成熟宿主源码独立审核

审核日期：2026-10-20

TI-07 未通过；本次源码研究不产生真实候选行为通过结论。

## Codex 独立取得与核对的源码

普通 HTTPS 访问失败，提升权限后的直接下载仍有 TLS 解密错误。随后通过 `gh api` 成功取得两个项目的公开完整源码，未关闭证书检查。

- [Firefox TSFTextStore.cpp](https://github.com/mozilla-firefox/firefox/blob/main/widget/windows/TSFTextStore.cpp)：`GetTextExt` 当前源码第2110行附近先把原始文字矩形宽高补足至少1像素，再转换到屏幕坐标、与显示表面求交。第2150行附近交集为空时 `SetRectEmpty`，随后根据交集与原矩形是否相同设置 `pfClipped`，最后返回 `S_OK`。**1×1修正发生在裁剪前，不能据此提出裁剪后返回1×1的新实验。** 这段代码只证明几何应答策略，不能证明微软拼音隐藏并恢复原生候选。
- [Chromium tsf_text_store.cc](https://github.com/chromium/chromium/blob/main/ui/base/ime/win/tsf_text_store.cc)：`GetTextExt` 第459～464行附近把结果转换到屏幕坐标，直接设置 `*clipped = FALSE` 并返回 `S_OK`。这段实现没有提供“完全裁出即隐藏候选”的额外操作。

取得时点的完整源码保留于本地 `artifacts/ime-work/r33-*`，上述行号仅用于定位本次快照；远程 main 会变化。关键摘录和完整文件 SHA256 已保存至 [R33证据目录](2026-10-20-ti07-tsf-prototype-review-evidence/r33-source-review/source-hashes.json)。

## 对下一步的影响

目前独立核对的源码没有提供不同于已测路线的新原生候选隐藏方法。不重复零矩形、裁剪标志或焦点挂起实验，不将有限研究说成平台不可能性证明。

自绘候选没有获得授权；[候选呈现待决方案](../plan/2026-10-20-ti07-candidate-presentation-decision.md)仅供范围决策。原要求保留，生产迁移、提交推送以及TI-08均不放行。

## DSH 请求最终状态与输出纠错

同一个 ACP runner66939 最终返回 `RPC session/prompt timed out`；`ownedSessionClosed=true`、`cleanup=process exited`、`serverExitCode=0`，runner退出码1。该请求已终止，不能继续标记为运行，也不能称其已正常完成研究交付。现存 result 文件是会话逐段输出，正式整理稿未完成；不据此重开同样的研究请求。

输出中的以下推断不作为验收依据：

1. R27 的同步 End 发生于 `Show(TRUE)`，不是已验证的 `Show(FALSE)`。原生已显示且仍活动的候选元素目前没有取得，不能调用已 End 对象继续试验。
2. 原型并非在完全裁出时返回 `clipped=FALSE`。Codex复核 `AcpTextStore.GetTextExt`：已布局但无可见交集时明确设置零RECT、`clippedResult=1`、返回 `S_OK`。真实R29B日志seq240和255均命中 `text-ext-fully-invisible-zero-rect`，范围0..6。候选仍可见不能归因于漏设裁剪标志。
3. “458份日志”与其他宿主全树均未使用某接口的笼统结论没有被Codex完整复现；不以此证明所有原生路线不可能。

当前没有可支撑下一次不同原生实验的新证据。保留已通过的滚动跟随和未通过的隐藏门槛，等待用户范围决策或新的具体原生协议/实现依据。不能通过改变组合语义、自绘候选或降低验收自行解除阻塞。
