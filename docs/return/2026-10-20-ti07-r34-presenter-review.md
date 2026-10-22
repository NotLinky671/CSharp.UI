# TI-07 R34 自绘候选隔离原型审核

审核日期：2026-10-20

状态：实现中，以下为初步源码发现；须对最终交付再次核对，不作为当前版本最终结论。不放行真实输入测试。

## 本轮终态

R34 runner8371达到20分钟上限，RPC prompt超时，会话已关闭、进程正常退出，无真实输入测试。Codex随后构建失败：TsfHostWindow.cs:2169，CS1503，GetWindowRect需要NativeMethods.RECT而传入TsfRect。日志artifacts/ime-work/r34-codex-build.log。仅有候选核心及部分显示接线，没有测试交付，不能启用实验。

R34A 后续仅修复该编译错误，正常结束。Codex独立执行正式测试504/504通过，日志artifacts/ime-work/r34a-codex-tests.log；没有新增候选呈现用例，这不证明新功能通过。

## 后续显示接线缺口

当前 overlay 对全部 child-input-surface 无条件创建，必须只在 HostDrawnCandidates 显式启用时创建；默认路径不应新增窗口。当前 ScenarioRunner 尚未实例化并连接 presenter/overlay，参数存在不代表模式已生效。overlay 创建失败应明确使实验不可运行，不能日志后继续把场景当正常成功。需在每阶段、每张图前后读取真实候选状态及原生可见性，检查 Begin/End/组合不变量；裁出后更新不得重新显示旧候选，结束后恢复不得复活缓存。保存的 _savedClipSnapshot 不能绕过活动元素代数检查。

## R34B 后续独立复核

runner64681达到10分钟上限后已终止，会话关闭、进程退出。逐调用失效检查、实际页边界和防御性复制已出现；Codex独立构建与现有504测试通过。尚无新增候选测试文件，不能关闭问题。

新增确定缺陷：CandidatePageReader.ReadPageRange在AllocHGlobal之后，第一个try里的GetPageIndex返回失败或抛异常时，流不会到第二个try/finally，分配泄漏。第一个finally是空块。应由一个覆盖查询、检查、复制的finally释放buffer，任何出口都释放且只释放一次。真实生产读取适配器测试必须覆盖这些失败出口。

ApplyPublish的overlayFactory在try外调用，且null overlay仍返回有效快照成功；真实显示层缺失/创建失败不能作为成功发布。新增测试应覆盖工厂抛异常和null结果，清楚区分读取成功与显示成功。

## 候选来源读取需要核对

当前 `ComCandidateSnapshotSource.ReadPage` 连续调用 GetCount/GetSelection/GetCurrentPage/GetPageIndex/GetString，未在每次 COM 返回后检查同步 End 或同 ID 元素替换。外层查询结束后再检查 sourceEnded，只能阻止发布，不能阻止对已结束元素继续查询；必须用真实共享来源适配器的逐调用检查及借用生命周期保护。

当前页读取以总候选数和32条为界，没有以下一页起点为终点，会把后续页面条目混入当前页；GetCurrentPage失败会回退第一页并仍报告成功，不能把猜测当真实页。页元数据读取失败应明确失败；空候选及 GetSelection 的 S_FALSE 无选择语义应按接口契约处理，不能错误制造选中项。

`CandidateSnapshot` 声称不可变但直接保存传入的 IReadOnlyList；需复制并只读封装，避免外部 List 后续修改已发布内容。

以上初步发现应在交付后检查是否仍存在，再形成返修任务。真实候选隐藏、恢复与唯一中文提交均尚未验证。

## R34C / R34D 独立结果与剩余阻断

R34C正常结束，缓冲区改为一个覆盖查询和复制的finally释放；新增11个Fact（报告误写10），Codex独立515/515通过。R34D正常结束，接线已出现，独立534/534通过。但这些测试没有实际驱动窗口裁剪恢复：

1. UpdateCandidateOverlayGeometry在 `_overlayHiddenByClip` 为true且文字已恢复可见时直接return，恢复方法只定义没有调用。候选隐藏后会永久隐藏；不能通过。
2. ApplyCandidateOverlayOnUiThread在hideByClip分支只写_savedClipSnapshot，裁剪中的新Update没更新_overlaySnapshot；恢复可能显示裁剪前旧内容。必须只保存一份当前活动快照，End清空；恢复读取presenter的当前同代数快照，不能fallback陈旧缓存。
3. InspectCandidateOverlay把IsWindowVisible=true但GetWindowRect失败当成Ok+空矩形。应明确失败，不能把读几何失败当已隐藏。捕获判据必须分别比较窗口真实可见性和几何，读root/row失败也应失败而非跳过锚定检查。

上述是源码确定路径，当前不运行真实输入。新增测试应驱动实际共享的窗口显示状态控制流，不能仅测试fake overlay自己实现的clip/restore。
