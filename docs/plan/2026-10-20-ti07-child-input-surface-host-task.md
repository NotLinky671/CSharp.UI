# R29A：只实现真实输入child宿主，不做场景执行器

日期2026-10-20；DSH ACP devin/swe-2。完整设计在docs/plan/2026-10-20-ti07-child-input-surface-experiment.md。本任务只做下面这一小步，不做完整R29。Codex审核，DSH实现和托管测试。沿用当前dirty，不提交推送、不运行native/hidden/visible/input，不读改保护文件/production/audit。

上轮请求只反复读取并触发compaction，没有交付实现。避免再读完整ScenarioRunner/TsfSessionHost/全部tests；本轮不修改它们。按下面已确定接口直接实现：

1. TsfHostWindow.Create增加尾部可选bool childInputSurface=false；默认off旧行为完全保持。InputHwnd默认返回root，opt-in返回真实单行输入child；ViewportHwnd返回固定真实viewport（默认0）。root.Hwnd用于调度/前台/生命周期不变。
2. opt-in在root下建viewport，再建使用现有WndProc的输入child；窗口/字体与单行row+padding的真实尺寸，全部本进程同UI线程。input承担绘制/键盘/CHAR/焦点消息；根窗口不冒充实际输入。AttachWindow(input)使store.GetWnd(1)=input；提供DisplaySurfaceProvider从真实input与viewport/root客户区屏幕交集查询，查询无锁/独立文本generation，hidden/minimized或无交集为零RECT。
3. ScrollBy/ScrollByLogical/ScrollXBy/SetScroll在opt-in移动input真实位置，NOACTIVATE|NOZORDER；本地文字padding不再减scroll，精确移动一次。BuildAndPublishLayout使用input origin和真实祖先交集做clip，不能用交集origin作glyph origin。Paint/selection/underline/caret坐标以实际input origin映射，不以可见交集origin映射。所有WM_SIZE/MOVE避免递归/重复发布，默认off旧逻辑保持。
4. 仅提供InitializeInputFocus()方法：UI线程、root已经前台、input同进程同UI线程时初始SetFocus(input)一次，否则拒绝；不在scroll/layout/paint/窗口几何回调调用它，本任务还不从ScenarioRunner接入。不调用EnsureForeground/TOPMOST/AttachThreadInput，不操作输入法或系统候选HWND。不要在root WM_SETFOCUS中无条件重设child焦点。
5. 正确销毁child/viewport和窗口类，hidden创建不得ShowWindow或请求焦点/前台。已有WM_SETFOCUS/KILLFOCUS只对真正input触发session.OnWindowFocus，root仍处理dispatcher/close。

允许修改范围：tools/CSharp.UI.TsfHostCheck/TsfHostWindow.cs、DisplaySurface.cs，必要的新单一child几何辅助文件（同目录），tests/CSharp.UI.TsfHostCheck.Tests内新增目标测试。不要修改Program/ScenarioRunner/TsfSessionHost/InputScript，它们留下一小步接线。不要为全矩阵改旧测试预期。

测试默认off接口兼容、祖先交集、单行高度、scroll本地坐标不重复减、无锁surface不依赖文本版本、焦点前置predicate的实际共用逻辑；托管测试不调用Win32。运行Debug/Release build正式tests与--selftest，--no-restore -m:1 -nr:false，报告真实结果到artifacts/ime-work/ti07-dsh-r29a-host-only-result.md。Codex随后直接调用新Create(...,show:false,childInputSurface:true)跑真实独立geometry audit；你不自行跑窗口或输入。

先实现这一步，不阅读或实现完整场景/输入守卫/真实候选验收；这些在下一小任务单独接入。没有真实视觉结果就不能宣称TI07或R29通过。
