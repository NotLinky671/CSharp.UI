# TI-06 预编辑显示与几何审核

审核日期：2026-10-19

状态：2026-10-19最终独立复审通过，R1～R5及追加场景已关闭；允许DSH提交推送。最终结论见文末，前文保留历史返修记录。

依据：[TI-06](../2026-10-23-text-input-editing-tasks/06-preedit-rendering-and-geometry.md)。本轮只检查平台无关显示与逻辑几何，不宣称原生IME桥完成，不安装或测试微软拼音。

## 首轮独立运行时检查

独立Console直接引用已编译核心与文本DLL，使用真实Skia、UiRuntime、TextField和记录型输入服务，经公开组合与指针入口检查。131项中80通过、51失败；程序退出码1。源码及输出在同名-evidence目录的codex-initial-runtime.cs.txt与codex-initial-runtime.log。该工具只编译自身，不与DSH并行构建生产工程。

## R1 [P1] 客户端查询几何仍始终返回不可用

TextFieldCompositionClient.GetState仍使用TextInputGeometry.Unavailable构造状态。运行时虽已调用服务PublishGeometry，真实客户端查询却无法取得同一几何；四种DPI、四种宽度下48项相关检查失败，长预编辑2项检查同样受影响。TI-07将依赖当前客户端状态，不能维护相互矛盾的两份输出。

应保存并返回当前有效几何，释放、epoch失效与隐藏时不可保留陈旧锚点；不能在GetState/原生回调中排版。继续独立检查实际排版原点与caret，不能只断言非空。

## R2 [P1] 撤销锚点回调发生在旧客户端失效之前，可写入失焦节点

TextFieldState.EndInputSession先调用Composing.RetractGeometry，随后才EndForOwner。服务在PublishGeometry(Unavailable)中同步调用旧客户端的无Start Result时，旧租约仍有效。实际运行：初始ABCD，焦点已离开，撤销回调提交BAD替换[0,1)，绑定被写入一次，成为BADBCD。独立检查“retraction callback cannot write revoked focus client”失败。

必须保留TI-04先切断归属、后调用平台的边界；在会话接线中安全撤销锚点并用代际保护同步重入。不能先发外部通知再撤销，也不能在回调后重新读取已被替换的Composing并结束新会话。补失焦/失活/卸载、同步回调与切换新焦点回归。

## 后续审核

等待DSH完成实现与自测后重新检查全部改动、真实Skia/GL像素测试、长预编辑、滚动裁剪、DPI变化、同值去重及TI-04/TI-05兼容性。最终通过前不得提交实现或标任务已完成。

### 隔离检查补充

后续将几何数值断言直接对照服务收到的值，将客户端查询一致性单列，并修正独立工具对根节点宽度的假设（根节点受紧约束占满视口，不是其偏好Width）。最终131项中114通过、17失败：16项均为GetState与已发布几何不一致，另1项为撤销回调写入失焦节点。服务收到的四DPI×四宽度锚点与真实排版一致，长预编辑实际原点与caret也一致。见codex-isolated-runtime.log及同名cs.txt；初轮宽度假设带来的派生失败不另列产品缺陷。

## R3 [P1] 纯重绘发现外部Binding变化后，服务仍持有已取消的预编辑几何

实际流程：ABCD上开始预编辑替换[1,3)，生成一帧；外部直接将绑定改为X，只InvalidateRender，再BuildFrame。ProcessPendingWork在发布几何之前没有同步文档；ComputeInputGeometry把新绑定值与旧组合锚点拼接并发布。随后Emit中的SyncEditor才取消组合，画出正式X，但服务最后收到的几何仍有HasComposition=true，与这一帧实际显示不一致。

追加独立检查133项中115通过、18失败，新增失败为“pure repaint never retains stale composition geometry”；见codex-runtime-r3.log及同名cs.txt。必须在发布最终几何之前完成真实文档/来源同步、取消与会话失效处理，并保证几何与实际显示同版本；不得在GetState或原生回调中排版，不得用额外一帧的陈旧几何掩盖，不破坏TI-04延迟通知/代际边界。补纯重绘外部写入、来源变化与组合取消回归。

## R4 [P1] 150%/200%下长预编辑末尾的真实caret在裁剪区外

追加真实Skia场景：空文档输入80个W作为预编辑、caret在末尾，200逻辑宽视口；在DPI1.5和2下，纹理实际原点+排版GetCaretX落在内容区右边界之外。绘制分支把矢量caret夹回框内，ComputeInputGeometry却按未夹取矩形裁剪而返回不可用，候选锚点消失。DPI1和1.25本例通过，不能据此排除150%问题。

必须让真实排版原点+caret确实可见：横向滚动范围应考虑caret宽度和物理像素吸附余量，并统一绘制与发布的几何。不能仅复用矢量夹取结果掩盖原点错位，也不能取消像素吸附、缩放文字纹理或破坏R16/R17。补四DPI下空文档长预编辑末尾、无后缀的非空文档末尾与窄框回归。

本轮累计141项中119通过、22失败，见codex-runtime-r4.log及同名cs.txt；新增4项失败均为DPI1.5/2下真实caret不可见和锚点失效。

## R5 [P1] 取消长预编辑后，PointerDown仍使用旧ScrollX命中

长预编辑使ScrollX很大；在左侧内容区按下鼠标，OnPointerDown先取消组合，但IndexFromPosition仍使用长预编辑留下的ScrollX，落到正式文档末尾，而不是点击位置。之后EnsureCaretVisible才夹回正式文档滚动；PointerUp又重新命中，会掩盖这个错误，但按下瞬间的光标和拖选锚点已错。

追加公开入口检查：ABCD，替换[1,3)为80个W的预编辑，caret在预编辑末尾，BuildFrame后在内容区左侧PointerDown，不先发PointerUp；Document.Selection.Active实际非0，检查失败。修复应先取消组合，并基于正式文档和恢复选区收敛ScrollX，再计算命中。补按下即时、后续拖动与松开测试；不要仅断言Up后的最终位置。

本轮对更新的Debug DLL共142项检查，120通过、22失败，见codex-runtime-r5.log及同名cs.txt、codex-r5-binaries.txt。此时R4的锚点被ClampCaretIntoClip夹回而不再消失，但真实排版caret仍在裁剪区外；另一个长预编辑场景的锚点X也已被夹取而与实际原点不符。仍须修正滚动余量，不能以“误差小于一个物理像素”替代任务要求。

### R1～R5返修复测：142项通过，R3追加长滚动场景仍有失败

返修后原142项全部通过（codex-revision1-runtime.log），TI-04/TI-05独立兼容检查41/49也通过。进一步把R3外部变更场景与长预编辑滚动组合：ABCD替换[1,3)为80个W且caret在末尾；BuildFrame后外部绑定变成X，只InvalidateRender，再BuildFrame。PrepareTextInputGeometry虽已同步文档/换epoch，但未收敛ScrollX；ComputeInputGeometry用旧的长预编辑滚动计算短文档caret，发布不可用。随后Emit才EnsureCaretVisible并正确画出X，服务锚点仍与这一帧不符。

追加后144项中142通过、2失败，见codex-revision1-long-external.log及同名cs.txt。R3仍需在发布之前完成正式显示串排版与ScrollX收敛，再生成锚点；确保纯重绘在同一帧有正确的有效caret，不依赖Emit后来修正或下一帧恢复。补长预编辑→外部短文档和普通长文档→外部短文档回归。原生回调/GetState不得排版，安全阶段可以准备真实文本几何；对应Prepare入口现有“不排版”注释与调用SyncEditor可能排版的事实也需统一。

### R4追加复测：已提交文本与组合结果切换仍有不一致，暂不通过

146项独立检查通过后，复查发现EnsureCaretVisible仅对Composing保留caret宽度与像素余量；普通已提交文本退回旧的committedMaxScroll，而ComputeInputGeometry仍使用真实未夹取caret。补普通长文档按End、长预编辑Result提交后的四DPI检查：DPI1/1.25下几何与实际绘制caret不同，DPI1.5/2下锚点直接不可用。现162项中150通过、12失败，见codex-revision2-committed.log及同名cs.txt。

R4须涵盖普通caret及Composing→Result→Idle切换：统一滚动可见性预算，使真实排版原点+caret保持可见、绘制与发布一致。不能只给组合分支修正，再称已提交分支保留旧语义。原有R12测试要求1:1纹理、纵向居中和caret可见，并不要求文本末端必须贴死内容区右边界；保留已有断言即可。禁止重新引入ClampCaretIntoClip冒充正确，或修改独立工具降低标准。补普通长文本End和长组合确认后的四DPI回归；完成后继续待Codex复审，不提交。

## Codex 最终独立复审（2026-10-19）

通过。R1～R5及R3/R4追加场景均已关闭，允许DSH按工作流英文提交并推送本项实现。前文失败为历史返修证据，不代表最终状态。

- 几何查询与发布同源；释放/epoch失效不返回旧锚点，查询不排版。
- 撤销锚点先切断旧归属，代际保护同步回调；旧结果不能写入失焦节点，也不会结束重入建立的新会话。
- 纯重绘先同步真实Binding、取消/换会话并收敛ScrollX，再发布锚点；长预编辑或普通长文档外部缩短后，同一帧定位与实际文字一致。
- 普通与组合caret统一计入宽度及像素吸附余量；四DPI长预编辑末尾、普通长文本End、组合确认后的真实排版caret可见，发布与绘制一致。未用夹取几何掩盖错位。
- PointerDown取消组合后先恢复正式文档滚动再命中，按下瞬间和后续拖选不会继承临时文字的滚动偏移。

### 最终独立验证

Codex直接运行最终已编译测试DLL，两种配置各自独立执行；OpenTK在真实Windows桌面环境运行窗口、OpenGL与剪贴板测试。命令形式为`dotnet tests/<工程>/bin/<Debug或Release>/net10.0/<工程>.dll`，均exit 0。

| 检查 | Debug | Release |
|---|---|---|
| 核心 | 545通过，0失败/跳过 | 545通过，0失败/跳过 |
| 文本 | 67通过，0失败/跳过 | 67通过，0失败/跳过 |
| 集成（真实Skia） | 53通过，0失败/跳过 | 53通过，0失败/跳过 |
| OpenTK（真实GL） | 251通过，0失败/跳过 | 251通过，0失败/跳过 |
| 单配置合计 | 916通过 | 916通过 |
| 最终缓存解决方案构建 | exit 0，0警告/错误 | exit 0，0警告/错误 |

Codex额外独立运行时检查162/162；TI-04兼容41/41、TI-05兼容49/49，均exit 0。独立工具直接引用最终核心/文本DLL，生产输出与审计副本SHA256一致，见codex-final-binaries.txt。关键源码与输出已保存：codex-final-runtime-162.cs.txt、codex-audit.csproj.txt、codex-final-runtime-162.log；最终两配置工程输出为codex-final-Debug-*.log、codex-final-Release-*.log，构建为codex-final-build-Debug.log和codex-final-build-Release.log，兼容检查为codex-final-ti04-compatibility.log和codex-final-ti05-compatibility.log。审计独立目录与DSH运行目录分开，避免并行编译干扰。

本项新增48项工程测试（核心16、集成23、GL9）。Flex/Flow两处旧“纹理末端贴死右边缘”的断言按统一caret余量修订；原选区、绑定、真实文本末端可见、真实排版caret位置、纹理/纵向原点等能力断言保留，并新增完整caret宽度可见性断言。审核接受这一行为变化，未以矢量夹取代替真实文字位置。DSH最后一次Release重编日志有8条既有nullable/xUnit警告（dsh-build-release-r4.log）；最终缓存构建0警告不表示已修复这些既有警告。

### 本项边界

通过的是平台无关预编辑显示、命中/滚动与逻辑候选几何。真实Skia/GL测试中的组合事件由测试服务产生；未接入Windows原生IME桥、未验证系统候选窗口，窗口位置/屏幕坐标换算及真实输入法联调仍属TI-07/TI-10。未安装、启用或测试微软拼音，未变更系统输入法配置；不宣称macOS/Linux完整IME。本计划尚未整体完成，不创建计划级交付记录。

## 提交与推送结果

DSH按Codex批准范围创建实现提交`ab41a14cc3f27ad727b7d771cc1a5252f61e400e`（Implement preedit rendering and caret geometry）。DSH沙箱凭据助手出现Win32 error 5，未改凭据、远程或提交历史；Codex宿主执行同一条正常`git push origin main`成功，远程main从2a5ba8d推进至ab41a14。实现提交后工作区干净。后续文档提交仅记录这一实际推送结果，不改代码或验收结论。
