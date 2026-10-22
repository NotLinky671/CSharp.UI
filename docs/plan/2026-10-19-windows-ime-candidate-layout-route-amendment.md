# TI-07 Windows 候选窗动态布局路线修订

计划日期：2026-10-19

状态：Codex 已批准隔离可行性原型；未批准生产迁移，TI-07 未完成。

设计依据：[文本输入与编辑计划](2026-10-17-text-input-and-editing-plan.md)、[TI-07](../task/07-windows-ime-and-host-routing.md)、[审核与真实证据](../return/2026-10-19-ti07-windows-ime-review.md)。本文件为现有 TI-07 的路线修订，不启动 TI-08 或更后任务，不替换当前任务集合。

## 已证实的问题

当前微软拼音、生产 MTA 线程、150% DPI 下，真实预编辑和唯一结果提交可以工作，正式测试 Debug/Release 各 965 项及独立审核 42 项通过。然而：

- 组合内容不变、输入框滚动上移约 10 物理像素时，系统候选仍在原位。
- 完全裁出时，本侧撤销几何，但系统候选仍可见。
- `IMR_QUERYCHARPOSITION` 已真实读取更新后的逐字符屏幕坐标；继续真实输入一个字母后，候选会跳到新位置。
- IMM 位置/排除区/组合窗更新、隐藏原生 caret 与位置通知、默认 IME 窗口的 `IMC_SETCANDIDATEPOS` 对照均未证明可通过该行为。专用控制消息返回成功且读回新位置，也未移动候选。

这些证据不能证明所有 IMM 实现或参数组合都无解，也不能证明 TSF 原型必然解决问题。禁止从 API 成功推断候选已移动，禁止通过取消、提交、重输组合或直接移动系统候选窗口来通过验收。

## 第一步：隔离 TSF 可行性原型

实现与测试继续由 Devin CLI SWE-2 承担，Codex 独立审核与真实验收。先读取 SDK 与官方接口文档，提出并实现范围有界的原型，放在 `tools/` 与配套 `tests/`；生产桥、公开核心接口、生产线程模型保持当前状态，原型不自动加入生产运行。

1. 定义真实 TSF 文本存储，使用 UTF-16 ACP、文档版本与实际选区；支持真实预编辑、结果和取消。不能只实现矩形查询而伪造文本、选区或锁协议成功。
2. 核对 `ITfThreadMgr`、document/context、文本存储、组合及 sink 接口的实际 IID、vtable、HRESULT、字符串/内存所有权。必需操作实现真实语义，不支持的扩展明确拒绝。
3. 显式实现同步/异步读写锁和重入边界。回调不递归布局、不在原生边界抛出托管异常，不在无授权写锁时改文档，不在只读查询中隐式更新绑定。
4. 使用稳定布局后的真实文本范围几何应答 `GetTextExt`、`GetScreenExt` 等查询；布局改变后按 sink mask 与协议发出 `OnLayoutChange`。部分/完全裁剪使用文档规定的裁剪和不可用语义，不用旧矩形或窗口原点假充。
5. 记录真实套间与 UI 线程身份；先验证当前 MTA 路线。若无法工作，允许仅在独立原型进程对照 STA，并明确与生产宿主的差异。未经 Codex 审查不更改生产线程或另设隐藏输入线程接管生产焦点。
6. TSF 与 IMM 文本写入来源互斥，普通英文/Unicode字符与组合结果分别归因。原型也要证明同一次结果只写一次。
7. 仅使用已有微软拼音配置与自有窗口；不改全局默认输入法、profile 启用状态或其他应用的窗口。普通请求焦点后等待人工点击，所有真实键盘注入前验证自有前台。
8. 激活、sink advice、document/context push/pop、焦点、COM 与窗口资源成对清理；失败初始化、失焦和关闭同样验证，无活动组合或钩子遗留。

## 原型验收门槛

- 单元测试验证 ACP/代理对边界、锁状态与重入、选区/文档事务、通知 mask、过期几何拒绝和资源失败路径；不得仅用模拟测试代替真实 IME。
- 当前微软拼音真实输入 `nihao`，见证组合和候选、选词唯一提交、Esc 取消且不退出、Enter 不泄漏；英文和补充平面输入无重复。
- 组合保持且绑定零写入时，依次小滚动、窗口移动/调整尺寸、横向文本滚动；截图必须证明候选位置实际更新。
- 完全裁出时系统候选窗实际隐藏，同时保留未提交组合与绑定语义；恢复可见后验证状态。若 TSF 服务实际不满足，明确记录失败，不修改验收口径。
- 记录调用线程/套间、请求锁序列、布局通知、实际范围查询、原生组合与结果来源、资源清理和截图。关键证据放审核目录，工具自测退出码不等于候选视觉通过。

## 第二步：生产接入决策门槛

仅当隔离原型经过 Codex 独立复审和真实行为验证后，另写实际生产接入范围与任务内清单，明确：是否更改宿主套间、如何互斥 IMM/TSF、如何复用当前组合状态/历史/Binding、如何保持公开兼容与非 Windows 回退、如何关闭与降级。生产迁移须再经 Codex 明确批准。

原型失败则保留证据，回到路线设计；不把失败包装为系统限制后直接交付。TI-07 仍以真实候选跟随及裁出行为通过为完成条件。只有整体审核通过后，才由 Devin 按严格文件清单英文提交与推送，排除用户自行修改的文件。

## 官方参考

- [TSF 应用集成](https://learn.microsoft.com/en-us/windows/win32/tsf/text-services-framework)
- [ITextStoreACP](https://learn.microsoft.com/en-us/windows/win32/api/textstor/nn-textstor-itextstoreacp)
- [ITextStoreACPSink](https://learn.microsoft.com/en-us/windows/win32/api/textstor/nn-textstor-itextstoreacpsink)
- [IMR_QUERYCHARPOSITION](https://learn.microsoft.com/en-us/windows/win32/intl/imr-querycharposition)
- [IMC_SETCANDIDATEPOS](https://learn.microsoft.com/en-us/windows/win32/intl/imc-setcandidatepos)

接口参考链接用于实施时核对；此计划没有据尚未实测的 TSF 接口作可行性承诺。

## 2026-10-20 通知调用链有界验证

用户授权继续通知、锁和焦点协议审核。隔离context/view诊断已证实有效中文组合范围0..6在内部滚动后可查询正确新坐标，候选仍不跟随；裁出返回零矩形但候选仍显示，整窗移动通过。当前证据不能把TSF管理器的store回调直接归因于TIP消费新位置。

本轮假设：区分应用探针与TSF通知内/外回调，并检查通知期间的实际焦点/上下文关联，可以确定后续研究应定位通知送达、查询拒绝还是查询后的候选行为。继续由DSH ACP、精确模型`commandcode-plan/deepseek/deepseek-v4-flash`实施，Codex审核与实测。

只增加默认关闭的`--trace-layout-chain`诊断，不修改生产src、文本/组合/选区/Binding、锁与通知实际时序，不额外发送刷新/焦点设置/输入，不迁移生产TSF、不实现自绘候选。追踪内容为通知enter/exit的配对ID、布局代数/版本/view、锁请求/授予/退出、几何成功与拒绝、探针调用阶段及同UI线程只读焦点快照。只有确实位于已知应用调用范围内才能标记为probe；其余称TSF callback或unknown，不能声称识别了具体TIP调用者。异步查询仅靠时间相关不能断言因果。

验证默认关闭不增加原生焦点查询；开关不改变原有结果。先目标测试与隐藏宿主，再做一次不启用context/view探针的真实中文滚动，必要时仅补裁出。验收日志应能复原发布布局→通知→回调范围/几何→返回，并识别拒绝。若同一通知内已有成功的新坐标查询且关联正确，停止添加刷新变体，保留失败截图，另写下一路线依据；不得把诊断通过记作TI-07通过。

### 2026-10-20 调用链阶段结论及下一协议假设

trace-only真实中文小滚动与裁出均未通过，已满足本轮停止条件：通知内有成功READ锁查询的新RECT/不可见零RECT，滚动采集点的自有焦点/document/top context身份正确，不再添加刷新变体。诊断代码独立审核通过，候选行为未通过。证据及序号详见[审核记录R23/R24](../return/2026-10-20-ti07-tsf-prototype-review.md)。

下一有界调查为GetScreenExt：对照官方文档/样例及独立测试核对无锁读取显示表面、非法view错误码与文本布局生命周期，确认契约偏差后只修改隔离工具并做一次单变量视觉对照。该假设不能直接解释已成功查询的新坐标为何未驱动候选，若修正后仍失败应如实记录，不承诺此项能修复候选。不扩大到生产迁移或自绘候选。

### 2026-10-20 R24 官方依据确认与实施边界

Codex读取微软Windows-classic-samples的[tsfapp/textstor.cpp](https://github.com/microsoft/Windows-classic-samples/blob/main/Samples/Win7Samples/winui/tsf/tsfapp/textstor.cpp)，CTSFEditWnd::GetScreenExt明确写有`//no lock is necessary for this method.`，直接GetClientRect/MapWindowPoints；非法view为E_INVALIDARG。这与[接口文档](https://learn.microsoft.com/en-us/windows/win32/api/textstor/nf-textstor-itextstoreacp-getscreenext)一致。源码副本保存在artifacts/ime-work/ti07-official-textstor.cpp。

Codex独立screenext audit复现3失败：无锁非法view未得E_INVALIDARG、无锁无法读有效显示区域、仅文本布局失效后仍无法读未变化显示区域；GetTextExt仍需读锁的对照通过。允许DSH仅修隔离工具的GetScreenExt及显示表面接线、相应测试/说明；表面查询独立于文档读锁及文本布局版本，真实HWND从当前客户区屏幕坐标得出，最小化/未显示时零RECT，不能在窗口移动后沿用过期缓存。无真实HWND的单元测试可注入显示表面或保留独立表面快照，不能以文本失效清空未变化表面。GetTextExt/GetACPFromPoint/文本编辑的锁语义不改，不加刷新/焦点/写入，不修改生产src。

先Debug/Release目标测试、自检、Codex独立audit与隐藏原生检查，再默认关闭主动context/view探针的真实中文滚动/裁出单变量对照。若视觉仍失败，R24协议修订可单独通过，R15/TI07仍失败并停止该假设，不堆叠其他行为改动。实现DSH ACP精确模型保持不变。

### 2026-10-20 R24 单变量结果

官方契约确认的GetScreenExt错误已修正并独立通过真实显示区域状态测试；中文滚动与完全裁出仍视觉失败。该假设按停止条件结束，不再叠加表面查询/刷新变体。R24合规通过与R15候选行为失败分开记录，详见[审核记录末尾](../return/2026-10-20-ti07-tsf-prototype-review.md)。下一项需有正面对照的宿主差异依据和独立的范围/停止条件；本次不批准生产迁移或自绘候选。
# 2026-10-20 补充：R25 原生编辑控件对照（进行中）

用户要求持续修复至真实验收通过。R24 的 GetScreenExt 协议修复已通过审核，但实际候选跟随与裁出隐藏仍失败；不能作为 TI-07 完成依据。

下一轮在独立 NativeEditReference 工具中使用 Windows 原生 EDIT 默认输入处理，分别隔离内部行滚动、自有子窗口移动、自有子窗口被父 viewport 裁剪。比较真实微软拼音组合前后的候选位置、文字坐标、组合与提交事件。子窗口移动只作为宿主行为对照，不等同于内部滚动修复。每次按键必须验证自有窗口前台与编辑控件焦点，保持系统输入法配置不变。

若原生控件出现可重复的成功路径，再据此制定最小隔离原型修改；若同样失败，则如实记录系统行为，不能降低验收条件。研究不授权生产 TSF 迁移、自绘候选、操作系统候选窗口，亦不允许提交/取消/重打输入来冒充几何跟随。实现仍由 DSH ACP 指定模型完成，Codex 独立审核并亲看真实截图。


## R26（2026-10-20）：ActivateEx 对照通过协议审核，真实跟随仍失败

首版 ITfThreadMgrEx ComImport 继承声明错误，实际 ActivateEx/GetActiveFlags 落在槽3/4。Codex静态审核复现4/9，已要求DSH显式重声明11个基方法；旧版所谓系统GetActiveFlags时序失败结论作废。修正后独立ABI及激活失败/清理audit21/21，Debug/Release各234正式测试+80自检，8隐藏宿主矩阵、4原生自检命令全部通过。默认路径不查询扩展接口；ActivateEx模式初始化及engaged两处真实flags含0x4，唯一Deactivate，三保护文件hash不变。证据：`2026-10-20-ti07-tsf-prototype-review-evidence/codex-activation-final/`。

真实scroll使用已审核二进制、UIElement-only实际flags0x8000000D、当前微软拼音profile前后均匹配、DPI144。Codex亲看两张PNG：文字屏幕Y139→128，候选框保持原位，视觉FAIL；组合期间绑定零写入、清理零失败。证据：`2026-10-20-ti07-tsf-prototype-review-evidence/activation-scroll-real/`。pbShow TRUE之后Begin立即End，新激活方式未改变此行为，停止激活参数假设。

后续hide-probe第一次发生前台丢失并导致组合提交，第二次等待前台超时，均为无效验收，不能用于隐藏行为结论。未强夺焦点、未改系统配置、未提交推送。R15/TI07保持未通过。

下一项R27仅隔离原生UIElement生命周期：按微软UILess协议先Begin返回FALSE保留候选元素，再用ITfUIElement.Show(TRUE/FALSE/TRUE)请求其原生UI并检查组合不变、零写入。无自绘候选、无系统候选HWND操作，无生产迁移。若元素不存活、Show不支持或视觉失败，则结束该有界假设；初始化即抑制不能当作裁出隐藏验收通过。DSH指定模型实现，Codex独立审核后真实截图验收。

## 2026-10-20 R27 结论与 R28 对照

R27工具生命周期观测缺陷修正后，Codex独立Debug/Release293正式+82自检、38项纯托管/重入audit、9项真实隐藏接线和4隐藏宿主均通过，生产及保护文件hash不变，证据codex-r27-reentrant-final。

真实微软拼音原生Show(TRUE)实际返回S_OK，同步EndUIElement；400ms后同一ni'hao/0..6组合、零写入、自有focus/foreground、完整profile继续有效，但原生候选未在预期窗口截图显示，且元素已结束无法再合法执行hide/restore。Codex亲看初始抑制与native-shown-ended两图，原始证据已保存r27-native-lifecycle-reentrant-final。此前E_FAIL是工具观测污染，已纠正；本结论不是“系统返回E_FAIL”，也不是初始化抑制已经通过动态裁出隐藏。R27假设停止，TI07仍开放。

R28下一有界对照在NativeEditReference里增加仅显式启用的child-collapse：只把自有EDIT高度变0并恢复原尺寸，NOACTIVATE|NOMOVE|NOZORDER，不隐藏窗口style、不SetFocus、不改输入法、不操作系统候选HWND。依据R25自有child移动已能驱动候选的正面对照，探究输入表面完全收缩是否可隐藏候选且保留实时组合和焦点。微软SetWindowPos文档仅说明几何API，不保证本假设的IME行为：https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowpos 。

先DSH devin/swe-2实现与托管测试，Codex审核/隐藏几何接线后真实单变量collapse/restore，核对live GCS_COMPSTR、end/result/change及focus事件、前后profile和截图。旧默认三场景不变。若组合提交/结束、焦点丢失、候选不隐藏或不能恢复，则立即停止此假设；不以重新输入/取消补救或降低验收口径。若通过也只是宿主对照，后续仍须隔离TSF自有child输入表面和内部scroll实测，不能直接宣称框架修复。无生产迁移或自绘候选授权，无提交推送。

## R28 最终结论与 R29（2026-10-20）
Codex独立Debug/Release各41正式测试+27自检、2隐藏smoke、27/27真实隐藏几何audit通过；合法零高度恢复和守卫修复已复核，生产/TSF/三保护文件hash未变。证据codex-r28-collapse-final。

真实child-collapse运行exit3仅表示待视觉审核。Codex亲看3PNG：EDIT高度420→0→420，候选始终可见，collapse时移到父窗口上方区域，restore后回原位。实时ni'hao组合不变，测量期end/result/change/focuschange均0，当前微软拼音0804有效。隐藏FAIL，R28假设停止。原始图/日志/visual verdict已存r28-collapse-real。native_caret_screen字段实为未转换的client rcCaret，不能用于屏幕几何推理；本结论用真实PNG与已映射的EM/组合坐标，不受此错误影响。

R29拟在隔离TsfHostCheck显式--child-input-surface下，让真实单行输入child承担TSF关联/GetWnd、焦点、键盘和绘制，固定viewport内容器scroll移动该child；真实祖先交集用于GetScreenExt/GetTextExt。验证宿主表面差异，不再重复height0或UIElement Show刷新。范围与不变量详见artifacts/ime-work/ti07-dsh-r29-child-input-surface.md。DSH ACP devin/swe-2实现，Codex独立审核后真实验证；默认路径不变，不迁移生产或自绘候选。TI07未通过，无提交推送。
