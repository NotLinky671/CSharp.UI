# TI-07 R29：真正的子窗口输入表面（隔离原型）

日期2026-10-20。DSH ACP精确模型devin/swe-2。你实现与测试；Codex独立审核/隐藏原生验证/真实输入截图。沿用当前dirty，不重置，不提交/推送。只读本任务和相关源码，不重复研究已停止的R24/R26/R27/R28刷新假设。

## 已知结果和本轮假设

R25原生EDIT自有child移动11px会带候选移动11px；同EDIT内部scroll或被父viewport裁剪都没有解决候选行为。R28实际高度0仍候选可见，restore正常：该假设失败。R27真实Show S_OK后同步End，不存在可持续控制原生UIElement的对象，已停止。不再重复这些变体。

现TsfHostWindow的整个top-level同时是输入/绘制/TSF GetWnd表面，应用内部scroll只改glyph位置。增加显式opt-in的真正单行输入child：其本地绘制、键盘焦点、TSF关联和GetWnd必须是同一个真实child。应用容器滚动移动该child，父viewport保持不动。完全裁出时整行输入表面都在真实viewport之外，而不是大窗口内仅glyph隐藏。此为待验证宿主差异，不承诺系统隐藏行为；不能将内部横向glyph滚动等同于容器移动，也不能宣称TI07已经通过。

## 范围

- tools/CSharp.UI.TsfHostCheck/**、tests/CSharp.UI.TsfHostCheck.Tests/**。
- 仅允许 tools/CSharp.UI.ImeProbe/InputScript.cs 增加默认null、兼容旧调用的每键额外guard，用于本轮输入child焦点/归属检查；不可修改EnsureForeground或调用其中TOPMOST/AttachThreadInput路径。相关测试可放TsfHostCheck.Tests。
- 不改src、NativeEditReference、AGENTS.md、ACP记录/脚本、Codex audit或其他dirty。保护文件artifacts/ime-work/ti07-tsf-protected-hashes.json；生产baseline ti07-r27-production-before.json。
- 不自行运行visible/hidden/native GUI/SendInput，由Codex复核后执行。仅托管测试、自检、构建可执行。

## 实现

1. 参数 `--child-input-surface` 默认关闭，ScenarioOptions和help/parse/RunHostCore接线；旧默认窗口/场景/激活行为不变。可明确仅支持scroll-follow、clip-out、move-window及hidden-host的opt-in，其他组合参数早期拒绝（尤其native-ui-lifecycle/blur-close等不应悄悄运行不兼容假设）。不需要重构全部工具。
2. 根窗口Hwnd继续用于前台/消息调度/截图/生命周期；新固定viewport真实child和单行InputHwnd真实child。InputHwnd才处理绘制、GDI字体/光标、WM_KEY/CHAR/SETFOCUS/KILLFOCUS及TSF文档关联/GetWnd/AttachWindow。TSF Engage与AssociateFocus使用实际input句柄；root/viewport不可冒充输入surface。所有类/child句柄正确销毁，hidden-host不得显示/激活/设焦点/发送输入。
3. 正常初始聚焦仅在root已前台且root/input归属为本进程/同UI线程时通过UI Invoke SetFocus(InputHwnd)，记录仅一次初始化；布局/scroll/clip/move/几何回调绝不SetFocus或重新AssociateFocus。root被用户点击时可在初始准备阶段设自己child；不能循环抢焦点。失焦时终止测试。
4. child高度是实际单行字体行高+真实padding，viewport固定；root/viewport坐标、input本地padding与屏幕坐标分别处理。容器scroll只移动input child，不再本地glyph减scroll造成双重偏移；默认off仍沿用原逻辑。DPI从真实窗口取得，尺寸支持144dpi；窗口尺寸变化仅更新实际布局，不能硬编码screen坐标。
5. AcpLayoutSnapshot/GetTextExt的clip必须来自真实input/viewport/root客户区交集；整个input裁出时返回真实不可见zeroRECT/clipped。GetScreenExt仍无锁、独立于文本generation，opt-in读取真实input显示表面与祖先viewport/root交集，完全裁出零RECT；没有窗口/查询失败如实返回，不用合成fake坐标。不修改默认FromWindow行为。GetTextExt文档读锁不变。
6. ScrollBy/ScrollByLogical/SetScroll等只在opt-in分支移动自有child，NOACTIVATE|NOZORDER，不隐藏窗口、不改style、不操作系统候选窗口。WM_MOVE/WM_SIZE发布一次实际布局，避免递归重复/通知风暴。不要在sink/锁内移动窗口或探测profile。
7. 每次SendInput down/up（包括cleanup）都要检查root前台+input焦点+process/thread归属。InputScript可增加Func<bool>? BeforeSendGuard，默认null旧行为；false或throw在SendInput前立即中止，先运行guard以便无原生纯托管negative test。guard通过UI dispatcher读取GetFocus，不能worker GetFocus。不得绕过现root前台guard，不用EnsureForeground。场景级状态检查也必须是UI线程快照。
8. 新模式scroll-follow和clip-out：保存baseline完整实时text/composition span/IsComposing/binding writes/current实际MSPinyin完整identity/ownfocus；动作前后和pump后截图前后全部核对，初始writes必须0；任何变化拒绝后续动作/截图。新clip-out完成后恢复同child几何并补restore截图（same composition/no end/writes0/focus保留），用finally覆盖异常/早退；失焦不发Esc，几何恢复不恢复焦点。不得取消/提交/重打/更改selection伪造通过。截图覆盖原候选区域，不因input完全裁出缩小截图。
9. 记录root/viewport/input window+client屏幕矩形、实际交集、row、scroll offset、GetWnd/TSF关联句柄、ownerPID/TID/focus、实时profile/text/span/writes、动作与查询配对。候选移动/隐藏仍由Codex亲看，执行成功exit3 pending-visual，不能0冒充通过。采集额外nativecaret时必须ClientToScreen，不沿用NativeEditReference错标的rcCaret_screen。

## 测试与停止条件

- Debug/Release build正式测试+selftest，--no-restore -m:1 -nr:false，报告真实数量。不跑GUI或输入。
- 默认off与参数组合、真正输入句柄接线、无双重scroll、实际clip交集含全裁出/边缘相交、无锁surface与有锁GetTextExt分离。
- 注入实际共用执行器测试失焦/组合变化/span变化/text变化/profile变化/writes增加/早退/异常finally，不能另写假的算法而真实路径不使用。
- 每键guard false/throw不执行原生调用；关闭/销毁及默认路径回归。Codex后续独立真实hidden子窗口几何/归属/关联/零交集/恢复检查，再真实中文scroll、clip、restore三图验收。
- 若真实scroll候选不跟随，或完全裁出仍候选可见，或组合/焦点改变，停止本轮假设，报告FAIL；不能继续随机刷新/改验收。任何路线通过也仍需TI07全矩阵，不直接迁移生产。

完成写 artifacts/ime-work/ti07-dsh-r29-child-input-surface-result.md，说明修改、测试及未验证项；不要修改Codex规划/审核结论。
