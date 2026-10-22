# TI-07 正式接入审核要点

记录日期：2026-10-21

当前依据：[生产接入计划](../plan/2026-10-21-ti07-production-tsf-integration-plan.md)。这是待核对事项，不是已证实的生产缺陷或已批准的新架构。

- STA 可由应用主入口 `[STAThread]` 提供；不能从“套间需在线程启动前选择”推导为“必须创建专用 UI 线程”。P1 只验证租约，未批准关闭 GLFW 主线程检查。
- TSF `GetScreenExt` 不要求文档读锁且显示表面与文字布局失效分开；`GetTextExt` 仍要求有效读锁与当前字符几何。不可把两者都概括为无锁可查。
- 新桥的几何失效接口不能强迫 TSF 重发 IMM 的旧缓存矩形；先撤销过时几何，安全布局后发布同源数据。
- 原型 `TsfSessionHost.RouteKeyDown/HandleChar` 使用 `_lastKeyEaten` 单标志；生产不能未经审核复制该策略。需验证 GLFW 消息泵调用 TranslateMessage 与窗口过程的顺序、一次按键多字符/代理对、队列已有后续按键时的回声归属。只测单次 Space 不覆盖缓冲输入。若需要改变消息泵或增加线程局部消息拦截，先明确所有权、范围、重入与释放并通过最小原生测试，不静默替换宿主事件循环。
- 原型 EndComposition 按最后范围提取非空文本并记 BindingRecorder；生产必须区分确认、取消、部分确认/剩余组合，不把所有非空结束都当作提交。必须以真实 TextField 事务与回读结果为权威。
- 核心内部选区适配校验 epoch/version/字素边界；TSF display ACP 的预编辑偏移不能直接当作已提交文档偏移。
- 候选总数很大可合法，限制应施加在本页分配/读取上；UIElement id0 合法，End 回调可同步重入，旧 RCW 失效后不再调用。
- OpenGL 候选绘制必须保留帧纹理保护；候选更新应唤醒按需渲染。输入区域完全裁出时隐藏呈现，不调用 SetFocus(null) 导致提交。

## 已核对的原生时序依据

微软给出的典型顺序是先 InsertTextAtSelection 插入初始文本，再用该范围 StartComposition。因此 SetText/InsertTextAtSelection 到来时尚无活动组合，不能据此立即提交 Binding；锁内临时编辑缓冲与随后组合观察必须配合。这是本计划先实现协议缓冲、后实现组合分类的依据。[Compositions](https://learn.microsoft.com/en-us/windows/win32/tsf/compositions)

OnUpdateComposition 的 pRangeNew 是更新后的范围；回调返回之前，pComposition.GetRange 仍是旧范围。后续实现必须分别捕获这两个范围，不能把回调内 GetRange 当作已更新几何；也不能在回调结束后继续使用未经持有/复制的借用参数。[OnUpdateComposition](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfcontextownercompositionsink-onupdatecomposition)

OnEndComposition 文档只承诺组合终止，其签名没有取消/确认标志。由此推断：单凭此回调或非空文本无法满足框架的取消/唯一提交语义，仍须对接原生编辑结果、活动范围、受控取消请求及真实输入证据；这不是微软文档提供了一个隐藏的确认 API。[OnEndComposition](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfcontextownercompositionsink-onendcomposition)

GetScreenExt 返回显示表面的屏幕包围矩形；非法 view 返回 E_INVALIDARG，文档窗口最小化等不显示状态返回零矩形。它不等同于可见文字范围矩形。[GetScreenExt](https://learn.microsoft.com/en-us/windows/win32/api/textstor/nf-textstor-itextstoreacp-getscreenext)

下一批需要明确原生写入批次的完成边界，不能仅以“当前没有组合”推断 InsertText 是结果。可核对 `ITfTextEditSink.OnEndEdit`：官方说明它在一次读写 `ITfEditSession.DoEditSession` 完成后通知，并提供只读 cookie；这与 `ITextStoreACPSink.OnEndEditTransaction` 是不同方向/用途的接口，不得把后者当作 TSF 向应用报告编辑完成的回调。若采用前者，需核实连接、ABI、生命周期和是否覆盖目标微软拼音路径，回调内不写文档，应用纠正仍在安全阶段执行。[OnEndEdit](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itftexteditsink-onendedit)

P2b 审核已确认“锁已释放”不足以代表 `RequestLock` 已返回。内联执行的 dispatcher 可能在该方法仍位于调用栈时刷新通知；需显式请求栈/通知重入保护，拒绝不安全应用更新并保留后续所属线程安全阶段处理机会。不能仅靠 dispatcher 的名字保证异步行为。

原生 owner 的连接方式也有明确官方依据：`ITfContextOwnerCompositionSink` 文档说明，TSF 在 `ITfDocumentMgr.CreateContext` 时查询传入对象，若支持该接口则安装通知 sink。因此仅在测试中创建一个独立 owner 并调用 Capture，或仅注册 TextEditSink，不能证明产品的组合 owner 已连接；产品 CreateContext 对象必须支持该 QI（或使用经验证的等价聚合设计）。[接口文档](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nn-msctf-itfcontextownercompositionsink)

P3 输入顺序还须验证跨来源交错：一次窗口消息泵可能连续处理“TSF 确认 → 普通 GLFW 字符 → 点击另一 TextField”。若 TSF 事实统一等整个 pump 返回后才应用，而 GLFW 字符/焦点立即改核心，会倒置原生顺序，导致旧版本拒绝或文字进入错误输入框。方案须明确 COM 回调只捕获与后续 UI 安全阶段的边界，在下一来源事件前按原生顺序应用已完成事实，且不在 COM/读锁/通知内写入、不排版重建。不要只分别测试单个 TSF 按键和单个普通字符。若引入消息 hook/更改 pump，先做最小原生时序验证并审核，不能据此静默替换默认事件循环或关闭 GLFW 主线程检查。

P2b R8 已证明：不能只保留“上一完成事务的原生文字”。`ABCD` caret2 → `ABniCD`（Start/OnEndEdit，零写入）→ 原生文字不变但 End/OnEndEdit，合法确认必须成为 `ABniCD`；最后事务 delta 为空不能推断取消。需要把核心权威已提交文档、组合原替换区与不可变原生完成事实分别保留。部分确认后，剩余预编辑的确认同样需以真实回读后的已提交基线为依据，而非将上一份含预编辑的原生文字当作提交基线。另一方面，完成事务时恢复原已提交文字的取消仍须零写入且恢复原选区；End 回调本身仍不能直接提交。

P2b 操作身份的契约需可实现：有界精确内存无法永远记住任意无限 opaque string key。内部来源应在第一次捕获时分配带 epoch 的单调 ticket，重放沿用该 ticket；消费时用单调退休水位拒绝旧 ticket。只在 Drain 检查水位，但在 Resolve 给已遗忘旧字符串重新分配新序号，仍会把旧操作当新输入。若迁移内部 capture API，产品调用方和测试使用真实 ticket 契约；Codex 自行调整独立审计调用，保留场景断言，不为了固定测试字符串添加解析特例。完成、忽略或拒绝的事实均需明确退休策略，不能让正常 OnEndEdit 编号形成永久洞、进而触发假溢出。

构建应沿用已验证命令参数：`dotnet build ... --no-restore -m:1 -nr:false -p:NuGetAudit=false`。此环境并行 MSBuild 子节点可能崩溃；自动 restore 可能尝试写入沙箱外 NuGet 缓存。先查看实际退出码和完整失败输出，不将尾部“0 errors”当成功。每次产品构建后重建审计以复制新 DLL，再执行对应输出路径。旧版本因构建失败仍运行旧副本，导致诊断插桩从未出现、反复推测错误；后续执行必须 fail-fast，不允许这种证据进入验收。

P2b owner 已于2026-10-21独立验收通过。最后发现并修复：同一完成事实的部分结果＋选区子动作在Drain中新取高来源ticket，令后续已排队最终结果被P2a拒绝。现在来源ticket只管重放/退休，core action序号按实际安全应用顺序单独分配。P3不得把这两个域再次混用，也不得绕过owner给session发送不相关序号。七组审计去重后共39场景、Debug/Release各545核心+504宿主全部通过，隐藏原生Start1/Update2/End1/OnEndEdit2、真实contextA/B连接通过。默认IMM保留，正式P3/P4尚未通过。详见owner审核文末与配套证据。

P3a 独立审核待查：工具创建真实OpenTK窗口本身不足以证明GLFW实际取消息时序。应至少一条实际生产宿主GLFW.PollEvents/WaitEvents路径驱动的消息证据；工具自己PeekMessage/TranslateMessage/DispatchMessage循环只能证明自定义pump。PM_NOREMOVE也须实际调用并断言同消息不被处理两次，不能只命名场景。SetWindowsHookEx线程参数须为kernel32.GetCurrentThreadId（OS id），日志同时明确托管id与OS id。隐式deps/HintPath临时变通不能成为不可构建的新工具：最终应ProjectReference并由Codex在DSH停止修改后必要时提升restore，正常拷贝传递与原生依赖。不应复制旧产品DLL覆盖新AssemblyInfo后再拿过期输出验收。
