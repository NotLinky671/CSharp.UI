# TI-07 R31 文档焦点实验首审

审核日期：2026-10-20。暂不允许运行真实输入；仅返修隔离原型。

## 必须修复

1. `ScenarioRunner.ChildSurfaceScenarioHost.RestoreTsfFocus`仅检查窗口PID/TID。finally失败清理绕过Probe后，前台已切走但TSF GetFocus暂时为null时，仍会调用SetFocus(saved)。`TsfDocumentFocus.Restore`中的GetFocus又可能COM重入；必须在真实SetFocus委托的最后一刻复核自有root前台/input Win32焦点/窗口归属/无锁无重入，而非仅在外层检查。实际GetFocus/GetTop/身份查询之后、写入之前和写入之后均要有共享可注入的检查。失去前台时清理应明确跳过，不抢回焦点、不覆盖外部状态。

2. 当前只在Suspend/Restore瞬间读回TSF文档；`ProbeLifecycleSafePoint`、等待前后及每张root/monitor证据均没有实际TSF焦点检查。600ms内自动回到OWN，现有截图仍可被标有效。新路径需要明确的阶段状态：基线OWN同doc/context、挂起NULL、恢复原OWN同doc/context；每个真实安全点必须读实际焦点并在COM查询后复核原生归属和组合连续性。原非实验路径判据不放宽。记录状态和失败原因，不能仅凭动作HRESULT或假宿主状态断言。

3. `Acquire.Fail`连续release doc/context时第一个抛异常会跳过第二个；`ReleaseHandle`忽略错误列表时吞异常，并且ReleaseTsfFocus没有向执行器反映失败。逐个尽力释放取得的引用，错误必须进入最终诊断，不能成功返回。保留首败，避免清理错误覆盖原错误；避免重复释放。

托管测试需要覆盖真实共享操作/门禁接线：GetFocus期间变为外部前台且返回null时，绝不发生SetFocus；GetTop重入后不写；挂起后等待期间/两张截图之间TSF自动回OWN时拒绝证据；第一引用释放异常仍释放第二引用且结果明确失败。已有498项/99项报告不能替代这些具体路径。

## 第二轮独立复核

runner34743已正常结束。新增原生前台门禁在独立用例中通过，第一次释放失败也能继续释放第二引用。但实际共享`ReadPhase`在取得doc+top且身份匹配后直接return，未释放这两个引用；OWN成功和NULL期望下读到OWN的路径均泄漏。独立审核5/7，失败2项，见`artifacts/ime-work/r31-codex-audit-before.log`，源码`ti07-r31-codex-audit/Program.cs`。需要确保所有出口逐一释放取得引用，异常如实判失败，释放各一次。

每图接线仍未完成：`CaptureOrStop`仅在`host.Capture`整个root+monitor bundle后检查阶段；`ScenarioRunner.CaptureChildSurface.ProbeUi`仍仅检查组合/原生焦点。必须在这个真实每图前后ProbeUi中加入当前host的ReadTsfFocusPhase，并在读COM后重验组合/原生安全点。stage对象必须是当前执行器的同一个实例，避免创建另一个默认OWN。NULL阶段瞬时回OWN时，root后检查失败应禁止继续monitor，不以bundle后检查替代。

## 最终复审与真实结果
上述托管缺陷已在后续返修中修复。Codex最终独立验证：Debug/Release各504正式测试与105自检通过；共享焦点审核7/7、执行器审核37/37、实际DPI144几何34/34、生命周期38+9、默认/child隐藏各4次通过。默认关闭路径不查询实验焦点阶段，真实每图前后接入同一阶段检查，捕获失败直接保留首败而不追加查询。证据见同名前缀证据目录中的codex-r31-final。
真实微软拼音实验仍失败：SetFocus(null)同步结束并提交组合，产生一次binding写入。该失败属于实验假设不成立，不能以托管回归通过判定路线通过。详见[真实焦点审核](2026-10-20-ti07-r31-real-focus-review.md)。TI-07仍未完成。
