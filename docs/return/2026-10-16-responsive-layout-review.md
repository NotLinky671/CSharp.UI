# 通用响应式布局实施审核记录

记录日期：2026-10-16

审核人：Codex

设计依据：[响应式布局计划](../plan/2026-10-16-responsive-layout-plan.md)、[当前任务索引](../task/README.md)。

状态：按任务持续复审；文档建立不表示实现已通过。

## RL-01 首版代码复查

### P1：Probe 恢复测量有效标记，却没有恢复文本排版缓存

首版 `MeasureProbeScope.MeasureSnapshot` 只保存 DesiredSize、MeasureValid、LastConstraints、LastProposalKind；Text.Measure 会同时改写 ViewCache。如下序列会导致有效缓存与实际排版对象不一致：

1. Text.Wrap 在宽100下完成有效测量，ViewCache为100宽排版，MeasureValid=true。
2. Probe以宽300测量，ViewCache被改为300宽排版。
3. 退出Probe恢复原100宽的LastConstraints和有效标记，但ViewCache仍为300宽。
4. 再次有效Measure(100)命中缓存，不执行Text.Measure，最后按100宽排列却使用300宽的排版/纹理。

需要恢复所有测量拥有的缓存状态（至少ViewCache；Environment也不能先修改后才拍快照），或令受Probe影响的缓存失效并保证下一次有效测量真正重算。用真实Text.Wrap/Skia排版与绘制命令验证，不只检查DesiredSize和LastProposalKind；覆盖异常退出和嵌套Probe。若选择“恢复之前有效缓存”，恢复内容必须完整，不能只恢复有效标记。

### P2：把内部固定内容的尺寸当作外层 Frame 自身尺寸，导致基准与边界错误

首版Frame.ApplyFrame在内层固定240、外层max100时保留ExplicitSize=240与MaxSize=100，但EffectiveMax及BaseSize均优先返回240。实际外层Frame的自然尺寸已是100；同理外层min300的自然尺寸是300，当前BaseSize/EffectiveMin/Max仍返回240，后续Flex会按错误的基准分配。

注意：初版审核把所有外层范围都视为“直接取交”的描述过于简单，现按Frame实际盒子语义修正。外层min300允许自身变宽并居中放置240的内容，这是合法布局，不应伪造300..240的矛盾区间。外层max100则需要保留“自身上限100、内部仍需240”的溢出信息，不能隐藏上限或谎称内容已压缩。应区分外框分配特征与内容固定要求，具体内部表示由本阶段冻结，不要求生硬合成同一范围。

添加外层FrameView(maxWidth:100)/FrameView(minWidth:300)包裹固定240内容以及Padding的真实节点回归。没有Flex basis时，基准须反映外框自然尺寸100/300，最大/最小声明不能被内层属性覆盖；内部240的固定内容仍保留，默认Frame的Measure/Arrange行为不变。现有Frame扩展的范围重载需要完整参数，测试可直接使用FrameView构造器，不应把概念性短写误当成既有可编译API。

### 独立验证进展

`artifacts/responsive-work/invariants/Verify.csproj` 使用真实Skia Text.Wrap复测。当前新增ViewCache快照后，Probe300再恢复有效100，最终仍为6行、纹理宽100，此项已通过。外层固定/max/min与Padding的特征仍需按上面的盒子语义修正；首版基准记录见`rl01-first.txt`，其中早期min/max期望区间已被本节纠正，以本节与后续验证为准。

### RL-01 快照入口完整性补查

新增完整快照后仍需核对两条捷径：`Measure`命中已有缓存时可能改写Environment，但当前在该分支没有CaptureForProbes；`MeasureFresh`在调用Measure之前就把MeasureValid改为false，快照会记录改写后的false而非进入探测前的true。请在改写任何探测拥有的节点状态前拍快照，并覆盖“同约束缓存命中但环境需解析”和“BeginProbe内MeasureFresh”的恢复。正常有效测量仍保持原行为。

本阶段尚未通过的主要问题仍是P2外框盒子语义：不能只把EffectiveMin/Max改成数学交集。最新任务验收补充和上文已明确，外层min300包240内容是合法扩张，外层max100的自然基准是100；内部固定要求与外框自身配额应区分。当前`BaseSize=ExplicitSize??...`及透传内层ExplicitSize仍会给出错误基准，不能将此版本交给RL-02。

### RL-01 当前最后一项独立失败（待修正）

盒子语义已按修订正确，7个Sizing案例与真实文本缓存都通过。扩展探针`artifacts/responsive-work/invariants/rl01-refined.txt`还实测：`Cached Probe environment restore: PASS=False`，而`MeasureFresh Probe restore: PASS=True`。复现仅保持测量约束/字体不变，将节点缓存的Foreground设为Red以模拟旧视觉环境；BeginProbe内Measure命中缓存，ResolveEnvironment将其改为父级继承值，退出后Red未恢复。该路径正是Measure中显式处理的缓存命中环境更新分支，应在赋值前CaptureForProbes。普通有效测量的环境更新不受影响。

请补这个入口与回归后再提交。当前基础尺寸/文本8项通过不等于扩展10项全部通过；不需要再重做已证明的负对照或拓展范围。

### RL-01 修订版复审通过

Codex 已复核缓存命中路径在改写 Environment 前调用 CaptureForProbes，MeasureFresh 也在清除有效标记前拍快照。独立执行 `dotnet run --project artifacts/responsive-work/invariants/Verify.csproj --no-build`，退出码 0，10/10 通过；包含真实 Skia 文本缓存、环境恢复、MeasureFresh 恢复及七组外框尺寸案例。

已核对本轮完整测试日志：核心 191、文本 67、集成 15、OpenTK 188，合计 461，失败/错误/跳过均为 0。`git diff --check` 无格式错误。此前 P1、P2 与快照入口问题均已关闭，RL-01 可作为 RL-02 的基础；本结论不代表后续响应式 API 或三平台人工验收已完成。

## RL-02 实施期间预审（尚未整体验收）

### 待核对：新增分配缓存的 Probe 恢复

本阶段新增 `ViewNode.ChildMainAllocation`、`AllocatedMainExtent`。若测量会改写这些值，应与已有 DesiredSize、ViewCache 一起纳入探测作用域快照；不能退出探测后只恢复尺寸有效标记，却留下另一提案的分配结果。请覆盖“有效分配后以不同宽度 Probe，再按原约束命中测量缓存并 Arrange”，包含嵌套探测和异常退出。此项为实施中的完整性检查，最终以成品代码与实测为准。

### 待核对：同轴外层 Flex 是否真正覆盖内部配额

当前 `FlexView.GetLayoutFeatures` 把内层 `MinSize`/`MaxSize` 与外层 min/max 合并。内层可能来自另一层 Flex，也可能来自硬 Frame，两者需要区别：硬 Frame 应保留，而已被外层同轴 Flex 覆盖的内部弹性配额不应继续限制分配。例如 `new Text("a").Flex(basis:100,min:90).Flex(grow:0,basis:10,max:20)`，外层声明应生效；不能残留内部 min90，产生 90..20 的冲突或最终宽90。请补实际 Frame 的回归，并同时验证硬 Frame 不会被覆盖。FillWidth 外层与 Flex 内层的默认 basis/上下限也需给出一致规则。

### 待核对：普通外层容器不能让内部 Flex 永远停留在 intrinsic 结果

当前 Stack.Measure 总是 PushIntrinsicMeasure 测量所有子项；若外层没有直接显式 Flex 则直接返回，不做最终复测。普通 `new VStack(new HStack(new Text("nested").Flex(basis:80)))` 在 400 宽视口中，内层 HStack 因 alreadyIntrinsic 提前返回，将永远没有机会分配其内部 Flex。需要让最终布局提案到达嵌套容器，并区分 intrinsic 与有效测量的缓存；同约束的 intrinsic 缓存不能掩盖有效分配。自然测量也应遵循 Probe 契约，避免未来 Responsive 把自然测量误认为有效提案。请以普通 VStack/ScrollView 包含 Flex HStack 的真实节点尺寸验证，而不是只有根容器直接带 Flex 的用例。

### 待核对：ArrangeFlex 缺少父容器交叉轴原点

当前 ArrangeFlex 将 ResolveCross 返回的相对 Offset 直接当作绝对坐标。横向容器需要加 bounds.Y，纵向容器需要加 bounds.X。例如 `new VStack(new Text("offset").Flex(basis:40)).Padding(20)`，文字 X 应为 20，不能回到窗口 X=0。请覆盖两个轴、非零父原点和 Center/End 对齐，验证绘制与点击采用同一实际位置。

### 待核对：压缩权重必须使用初始约束后的 basis

当前 StartSize 对 basis 执行 min/max 夹取，但 Shrink 仍使用原始 item.Base 算权重。计划要求使用“shrink × 初始约束后 basis”。两个子项分别为 `.Flex(basis:1000,max:100)` 和 `.Flex(basis:100)`，父宽100、默认 shrink 均为1时，初始尺寸均100，应压缩为50/50；不能按1000:100的原始权重得到约9.09/90.91。请保存并使用分配前夹取后的基准，冻结再分配过程中保持该基准不变。

### RL-02 独立探针：首轮实测 5 项失败，暂不放行

已使用 Harness 本轮生成的核心/文本程序集构建独立验证程序，未重建其共享工程：`dotnet build artifacts/responsive-work/flex-invariants/Verify.csproj --no-restore -p:BuildProjectReferences=false -p:NuGetAudit=false -m:1 -nodeReuse:false` 退出0（有一条既有 NU1900 漏洞服务网络警告）；随后 `dotnet run --project artifacts/responsive-work/flex-invariants/Verify.csproj --no-build` 退出1。14项断言中9项通过，以下5项失败，确认上述问题具有实际行为影响：

| 用例 | 预期 | 实测 |
|---|---|---|
| 普通 VStack 内的 Flex HStack | 文字分配宽400 | 宽50.4，仍是自然尺寸 |
| Padding20 中的纵向 Flex | 文字 X=20 | X=0 |
| 有效布局→不同宽度 Probe→恢复并 Arrange | 保持120/280 | 变为8.4/8.4 |
| basis1000/max100 与 basis100 的压缩 | 50/50 | 9.0909/90.9091 |
| 外层 Flex 覆盖内层弹性上下限 | 宽10 | 宽90 |

增长/压缩冻结再分配、零basis、两种 FillWidth/maxFrame 顺序、真实 Skia 最终宽度换行、嵌套测量量以及输入框缩窄后的真实光标/焦点/选区/Binding 均在本轮独立验证中通过。RL-02 仍处于实施中，此处不是最终成品验收；请修复5个失败项并补正式回归，再重新构建和验证。

### 修订中的同轴覆盖补查

新 HardMinSize/HardMaxSize 已将硬 Frame 与弹性边界分开；但当前 FillWidthView 覆盖 min/max 时还保留内层 Basis。若约定外层同轴声明完整生效，外层 FillWidth 的默认基准应恢复自然尺寸，不能继续使用内层 Flex 的显式 basis。请覆盖水平无界 ScrollView 中 `new HStack(new Text("abcd").Flex(basis:300).FillWidth())`：外层 FillWidth 按契约回退自然文字宽度，而非300；有限宽度下与兄弟弹性项的空间比例也应一致。外层 Flex 同样需要核对是否残留内层 Fill 标记，避免外层普通弹性声明仍被当作 FillWidth 的交叉轴请求。

新增 Intrinsic 缓存种类后还需统一视图可见的提案入口：当前 LayoutContext.CurrentProposal 在 IsIntrinsicMeasure=true、无显式 Probe scope 时仍返回 Effective，而私有 Measure 再把它改为 Intrinsic 写入 LastProposalKind。后续动态视图在 Measure 内读取 CurrentProposal 会误认其为有效布局；请使该属性或唯一的“有效提案”判定同时排除 Intrinsic，并用实际 Measure 观察上下文的回归固定契约。只修 LastProposalKind 缓存不能解决视图执行期间的提案分类。

嵌套 Flex 的有效复测修订还需把最终自然高度向上传递：当前无直接 Flex 的 Stack 分支在复测子项之前调用 SumNatural，复测后仍用旧 naturalMain/naturalCross 计算自己的 DesiredSize。`ScrollView(VStack(HStack(Text.Wrap().Flex(...), 固定标签), 尾部内容))` 中，内层换行高度虽然变大，外层 VStack 仍可能报告单行旧高度，导致祖先排列/滚动范围错误。请在有效复测后重新汇总最终 DesiredSize，并以真实 Skia 换行高度、尾部位置和 ScrollView 范围验证，不仅检查内部 Text 的宽度。

引入 Intrinsic 算法模式后，Probe 缓存复用也不能再无条件复用所有种类：当前 CanReuseMeasurement 在 kind==Probe 时直接返回true，即使当前 IsIntrinsicMeasure=true、已有缓存来自有效分配算法。有效 HStack 测量完成后，以原约束在 BeginProbe+PushIntrinsicMeasure 中测量，仍可能复用有效 ChildMainAllocation，而没有执行自然尺寸算法。请把“算法是否 intrinsic”与“结果是否临时 probe”分开识别/保存，或采取等效的安全复用规则，并恢复相应快照。独立探针已覆盖同约束下的模式切换，正常 Probe 读取兼容缓存仍可保留。

### RL-02 修订版复审通过

Codex 已复核同约束缓存同时区分 LastMeasureIsIntrinsic 和临时提案性质；Probe 快照恢复算法模式与分配缓存；Frame 硬边界与弹性配额独立，外层 Flex/FillWidth 覆盖旧弹性声明；最终子项高度向上传递，排列包含父级交叉轴原点，压缩使用夹取后的初始 basis。

独立执行 `dotnet run --project artifacts/responsive-work/flex-invariants/Verify.csproj --no-build` 退出0，18/18通过。首轮5项失败及后续补查全部关闭：嵌套宽400、Padding X=20、Probe后120/280、压缩50/50、外层Flex宽10；无界FillWidth回到自然33.6；真实Skia最终换行及祖先高度111.7265625正确；自然提案分类、模式切换与长文本缩窄后的真实光标/选区/焦点均通过。

已核对本轮实际 runner 结果：核心227、文本67、集成18、OpenTK188，合计500，失败/错误/跳过均为0。`git diff --check` 无格式错误。RL-01部分“初始子项提案为Effective”的断言随新增Intrinsic契约更新，探测/环境/文本缓存/异常恢复的原有行为断言保留并扩展。RL-02可作为后续任务基础；此处不表示尚未实现的Flow/AdaptiveStack/Responsive已完成，也不代替最终平台验收。

## RL-03 / RL-04 独立预验收

Flow 实现提交为 `f76177d`；AdaptiveStack 尚在实施，本节不代替最终四套回归核对。

Codex 独立验证程序位于 `artifacts/responsive-work/flow-adaptive-invariants`，使用已有框架程序集，以 `BuildProjectReferences=false` 构建，未与 Harness 并行重建共享工程。构建退出0、0警告/错误；随后运行退出0，23项断言全部通过：

- Flow 的 Padding20 非零绝对原点、精确放满、超宽硬尺寸保留及溢出诊断；未创建每行运行时父节点。
- AdaptiveStack 在 Padding 扣除后的精确断点及断点下方，DPI1/1.25/1.5/2 结果一致；水平无界使用横向自然布局，显式 Horizontal basis 不变成高度。两种模式分别显式设置间距，未假定宽模式 Spacing 自动覆盖 VerticalSpacing。
- Flow/AdaptiveStack 中 TextField 换行/切换后保留节点、焦点、光标及选区，旧坐标不命中移动后的输入框，Tab 仍按声明顺序遍历。
- 真实 Skia 多行文字换行后，尾项位置与 Flow/ScrollView 自然高度相符。
- AdaptiveStack 中内层 ScrollView 先滚动50逻辑像素，跨断点后同一节点及偏移50均保持。

当前未发现阻塞性行为问题，仍需等待 RL-04 正式测试、最终完整回归及文档提交后放行。

### 四套测试独立重跑

Codex 随后直接执行当前 Debug 测试程序集，核心259、文本67、集成27全部退出0。OpenTK首次在沙箱内执行为188项中1失败（系统剪贴板写入读回为空）；按环境限制使用正常桌面权限重跑相同程序集后188项全部通过，退出0。正常权限完整结果合计541，失败/错误/跳过均为0，R17文字颜色遮罩与既有窗口缩放回归仍通过。该结果不包含尚未实施的 Responsive 和最终示例改造。

### RL-03 / RL-04 最终复审通过

已核对实现 `f76177d`（Flow）及 `b3fc0ff`（AdaptiveStack）的变更、正式测试及实施记录。Flow 行缓存与 Adaptive 选定模式均被 Probe 快照恢复；同一父节点中的排列改变不创建新运行时父节点；Adaptive 的横/纵间距、对齐及显式轴向规则独立。三个现有示例 Debug 构建退出0，`git diff --check` 无格式错误。独立23项与四套541项结果如上，无未关闭阻塞问题，两项可作为后续阶段基础。Release和最终Gallery改造仍由后续任务验收。

## RL-05 实施期间预审

### 待核对：ResponsiveInfo 的公开 init 绕过构造校验

当前 `ResponsiveInfo` 构造器校验宽度与有界标记，但 `AvailableWidth` / `IsWidthBounded` 仍公开 `init`，可用对象初始化或 `with` 构造 NaN、负宽度及有限性不一致的值。请在接口冻结前关闭这种绕过（例如只读属性、统一校验入口），或提供等效的完整校验；测试不能只有构造器路径。结构体的不可避免 default 值应与“主动公开可修改但不校验”的路径区别说明。此项为实施中检查，最终以成品及实测为准。

### 首轮独立探针：正常场景14/14通过

`artifacts/responsive-work/responsive-invariants/Verify.csproj` 使用已有框架程序集（`BuildProjectReferences=false`）构建退出0、0警告/错误；运行退出0。普通 VStack/ScrollView 中裸 Responsive 首帧、Padding 后最终260/140宽度、尾项及内容高度、同宽重复测量、Probe 不构建、不替换稳定内容、无界宽度、16层嵌套、40并列节点、同宽新描述更新一次及同引用描述不重复构建均通过。本阶段尚未完成，不据此放行。

### [P1] 整体预算未能阻止无限扩张树先发生 StackOverflow

Codex 在独立子进程运行 `dotnet run --project artifacts/responsive-work/responsive-invariants/Verify.csproj --no-build -- --growing`，退出1，实际输出 `Stack overflow.`。复现描述仅为 `View Next() => new Responsive(_ => Next());`，随后正常启动 runtime 并调用 ProcessPendingWork；这会每轮新增下一层节点。栈反复经过 Responsive.Measure / LayoutContext.Measure，未到达预期的受控整体预算异常。当前4096次构建的预算与递归布局栈深度不匹配，不能保证在耗尽栈前终止。

请加入适合递归布局的深度/工作安全边界或等效机制，既允许16层和批量节点，又在无限扩张时抛出带路径/输入的普通可捕获异常，清空队列并允许后续合法更新恢复。仅断言预算常量或只验证反复替换浅层身份不够；应对真实逐层增长描述运行独立进程回归，避免把StackOverflow带入主测试进程。修正后同一 `--growing` 探针必须受控退出0，不能依赖进程崩溃/超时/StackOverflow。RL-05 暂不放行。

边界还需覆盖失败后再次处理同一错误 builder：仅把每轮总构建预算降低但保留已增长链，下一轮预算重置后仍可能逐次加深并最终溢出。需要实际树深度边界/回滚或等效机制，保证连续失败也保持安全，而合法切回有限内容能够恢复。正常/异常独立探针已扩为18项全部通过（额外含主动异常与BuildFrame重入后的请求清理、合法更新恢复），不关闭此项增长链阻塞问题。

### 512深度修订实测：首次受控，但再次处理仍栈溢出

已用18:30构建的修订程序集重跑扩展 `--growing`：首次得到普通异常（测量深度512、构建512、pending0），随后再次调用 ProcessPendingWork，进程仍以 StackOverflow 退出（Windows退出码-1073741571）。新栈落在 UiRuntime.InvalidateSubtree → InvalidateLayout → ProcessPendingWork，说明测量入口限制不足以保障异常清理及重复处理路径。完整输出保存在 `artifacts/responsive-work/rl05-growing-independent.txt`。

扩展探针包含首次失败、两次重复处理、随后 `stop=true` + `InvalidateResponsiveContent(root)` 切回有限 Text 的恢复断言。请修正后完整运行该探针；不能以“首次抛普通异常”关闭本问题，测量、失效清理、后续收敛/卸载都需留足栈深度余量。此为实施期修订实测，仍未放行。

### 增长链及公开init问题已验证关闭

18:37修订程序集下，`--growing` 首次受控失败（512构建、pending0）、连续两次重试均保持512构建，并在根节点显式内容失效、切回有限Text后恢复，整体退出0。InvalidateSubtree已改显式栈。ResponsiveInfo两个属性已改只读，新增反射检查也通过。以上关闭各自问题，但下述新增最终提案回归仍阻塞放行。

### [P1] 布局前按旧宽度消费新描述，瞬时错误分支卸载了应复用的输入框

为恢复过深旧内容，当前 ProcessPendingWorkCore 在 RunLayout 前消费请求，MarkDescriptionChanged/InvalidateContent 用 LastInfo 直接排队。若同一批更新既改变描述又改变父级宽度，就会先按上一轮宽度构建错误分支，再按本轮实际宽度构建最终分支，违背同轮只激活最终有效提案的契约。

Codex独立探针当前22项，20通过、2失败，运行退出1：

1. 旧宽300，新描述与SetViewport180一起更新：预期builder只执行1次，实际2次（最终宽180）。
2. 初始TextField获得焦点与选区；builder仅在“新描述版本1且宽>200”时返回Rectangle，否则TextField。版本1与宽180同批更新时，最终仍应是同父级同类型TextField，保留原节点/焦点/选区。实际先按旧300构建Rectangle，把原输入框卸载，再按180创建新输入框；实测oldMounted=false、FocusedNode=null。

复现已放入 `artifacts/responsive-work/responsive-invariants/Program.cs`，普通模式直接覆盖。请让深树恢复与最终提案合并同时成立，不能用无条件消费上轮LastInfo来修复恢复问题；同步状态/描述/父约束变化时不得挂载或卸载过时分支。修复后普通22项及增长链模式都须通过，且保留已通过的事务/Probe/异常及既有回归。RL-05仍不放行。

### 描述路径修正通过，显式失效路径仍复现同一P1

18:46修订后 MarkDescriptionChanged 不直接排队，以上两个描述更新断言通过。但 InvalidateContent 仍 QueueIfMeasured，按LastInfo提前消费；此入口正是RL-06状态失效接入点，不能保留同类过时分支问题。

独立普通探针新增第23项：根Responsive初始宽300、内容TextField并获得焦点；修改builder捕获条件，同时调用 `InvalidateResponsiveContent(root)` 与 `SetViewport(180)`，随后 ProcessPendingWork。builder在“新条件且宽>200”时返回Rectangle，否则TextField。预期只构建一次、保留最终兼容输入框；实测调用2次、oldMounted=false，运行退出1（其余22项通过）。因此同一P1仍未关闭；最终提案规则须覆盖描述与显式/状态内容失效两条路径，同时保留深树合法更新恢复。

### 最终提案修订：独立24项及增长链验证通过

18:59构建的修订程序集下，Codex重新以BuildProjectReferences=false构建独立探针，退出0、0警告/错误；普通模式24/24通过，退出0。新增第24项同时改变嵌套父Frame宽度并显式内容失效（视口不变），也只执行一次builder，原TextField仍挂载且焦点保持；因此不是只修SetViewport的特例。此前描述+宽度、显式失效+缩放的失败均已关闭。

增长链模式随后独立重跑退出0：达到测量深度256时受控失败（builds256/pending0），两次重试仍builds256，显式根内容更新切回有限Text后成功恢复。当前安全循环先尝试本轮布局、按最终有效提案消费；只有明确LayoutDepthLimitException且有请求时尝试恢复，正常布局不提前消费旧LastInfo。公开Info只读属性、异常清理、Probe和同宽描述规则均通过。

本节关闭上述实施期问题，尚需完整回归、最终提交与变更核对后才标记RL-05复审完成；不提前变更任务状态。

### RL-05 最终复审通过

实现 `465d3e0`，注释整理 `18a2d3b`，父约束变更与显式失效的正式回归 `4723e65`。Codex已核对最终变更与记录，并亲自运行四套当前Debug程序集：核心285、文本67、集成29、OpenTK189，合计570，失败/错误/跳过均为0，全部退出0；OpenTK按正常桌面权限执行。三个现有示例Debug构建记录均退出0，`git diff --check` 无格式错误。

独立普通24项及增长链模式结果如上；最终提案与内容签名、异常及深树清理、节点复用和R18即时回调绘制均通过，本阶段问题全部关闭。可以开始RL-06状态依赖工作。此处不表示尚未实施的状态订阅、Gallery改造或最终Release/跨平台交付完成。

## RL-06 实施期间预审

### 待核对：控件卸载后清理布局/绘制时补登记的绑定依赖

不只检查builder直接读取State的依赖集合。旧RegisterBindingDependency把Binding.Read登记为最近Component的长期_extraDependencies；Responsive分支移除控件但保留外层Component时，这类订阅可能继续存在。请覆盖“在任何Build之外缓存source.ToBinding，仅由活跃TextField在布局/绘制读取；Responsive条件切换移除TextField；再改source值”：不应残留订阅或重建已经无关的Component/Responsive。独立RL-06探针 `responsive-state-invariants` 已准备该场景（尚未运行，待实施程序集），需结合本阶段附加依赖生命周期契约验证。

### 待核对：直接构建依赖与附加绑定依赖重叠时的订阅并集

当前草稿ComponentState新增RemoveExtraDependency，最后一个引用释放时无条件state.RemoveDependent(owner)，但owner可能仍在_dependencies中直接读取该状态；EndBuild移除直接读取时也需保留仍被_extraDependencies引用的订阅。最终State.Dependents应反映两类依赖的并集，不能因清理其中一类而取消另一类有效订阅。

独立探针已补“外层Component.Build直接显示Value，同时Responsive内TextField绑定同一Value；移除TextField后改Value，外层显示必须继续更新”。这是附加依赖清理的互补场景，避免只验证无关订阅被删除，却误删仍需要的订阅。当前属于实施中检查，最终以成品/实测为准。

### [P1] 独立实测：移除绑定控件误删外层仍有效的构建订阅

使用19:20构建的实施程序集，Codex独立构建 `artifacts/responsive-work/responsive-state-invariants/Verify.csproj`（BuildProjectReferences=false）退出0、0警告/错误，运行退出1：12项中11通过、1失败。

失败项为上述重叠场景：外层Component.Build一直用Text显示Value，内层Responsive在Show=true时挂载绑定同一Value的TextField；Binding在任何Build之外创建。Show=false移除TextField后，再改Value，预期外层重新Build一次并显示新值，实际ownerBuilds增量0，显示停留旧值。RemoveExtraDependency最后一个引用释放时无条件RemoveDependent(owner)会删除直接构建订阅，确认预审问题有实际行为影响。

请按直接依赖与附加引用的并集维护订阅，并补相反方向回归：停止直接读取但控件仍绑定时，附加订阅仍驱动更新。已通过的builder-only更新、无需外层重建、条件依赖清理、批量状态/宽度合并、卸载与外层数据保留、纯附加绑定移除清理须保留。RL-06暂不放行。

相反方向也已补入独立程序并实测失败：外层停止在Build中读取Value，但保留仍绑定Value的TextField；再改Value，ownerBuilds仍增量0。普通探针现13项中11通过、2失败，退出1。两项均指向订阅并集清理，不应只单边加判断而遗漏另一方向。

### [P2] 同一控件节点替换Binding来源时保留了旧来源的附加订阅

独立探针再补“相同TextField节点从缓存Binding A换为缓存Binding B”（两者都在Build之外创建）。节点应复用，旧A无其它读取时应释放订阅。当前只在节点卸载时释放BindingDependencyRegistrations；属性更新复用节点不会卸载，因此仍保留A，同时又登记B。实测A.Dependents.Count=1；修改旧A还会触发外层无关Build一次。新B仍能驱动更新，通过其互补断言。

请将附加绑定订阅与当前实际绑定来源对应，节点更新/重新测量时替换失效来源，保留多控件引用与直接构建依赖并集；不要通过禁用正常节点复用处理。普通独立探针现15项：12通过、3失败（两个并集方向及此旧来源残留），退出1。该项与“依赖数量有界、属性更新后的绑定正确”契约相关，修复后应一起通过。

### 订阅修订独立15/15通过

Codex使用19:32修订程序集重新构建状态探针（BuildProjectReferences=false），退出0、0警告/错误；运行退出0，15项全部通过。StateBase按节点维护引用计数，ComponentState对直接依赖集合只增减实际变化，使直接读取与附加绑定引用按并集生效；描述更新前释放控件的旧绑定登记，随后按新描述重新登记，同时保留原控件节点。

实测两个重叠方向ownerBuilds均正确增量1；替换绑定后旧来源订阅数0、修改旧来源ownerBuilds增量0，新来源仍可更新。其余builder-only、条件切换、批量合并、卸载及外层数据保留均通过。上述P1/P2已关闭为实施期问题；仍需正式完整回归和最终提交核对后才放行RL-06。

独立探针随后扩为21项并全部退出0：另覆盖builder写相同值、写不同值、创建UseState的三种禁止操作，以及各自失败后的合法恢复。状态均保持原值，BuildTracker.Current恢复null、请求队列清空；不以相等值短路掩盖纯度契约。该轮构建0警告/错误，未重建共享框架工程。

收尾文档核对：请同步移除Responsive公开XML注释“本阶段不提供状态依赖能力”，以及UiRuntime.InvalidateResponsiveContent仍称“后续RL-06、不会自动订阅”的过时注释，使源码提示与本轮已经实现的状态读取能力一致。

### RL-06 最终复审通过

实现 `964a1e9` 已核对，过时XML说明已更新。独立状态探针21/21通过；Codex独立复跑核心299及真实Skia集成30项全部退出0，核对正式四套完整日志299+67+30+189=585，失败/错误/跳过均为0。三个现有示例Debug构建记录全部退出0，格式检查通过。

两类依赖并集、绑定换源及卸载清理均已有正式与独立回归，动态失效实际派发、栈式作用域恢复、纯构建约束、分支卸载与数据保留符合任务，实施期问题全部关闭。可以开始RL-07示例与指南；本结论不代替后续示例、输入/DPI/图形集成或Release最终交付验收。

## RL-07 实施期间预审

### [P1] maxWidth Frame 向子项重新扩大可用宽度，真实文本排版仍超出窄卡片

Codex独立程序 `artifacts/responsive-work/gallery-invariants` 直接实例化新版GalleryApp，使用真实Skia排版，检查320/480/768/1024×DPI1/1.25/1.5/2。所有控件Frame边界、真实内层滚动区、滚动到底部及输入框跨断点选区/焦点保持均通过；但追加“实际TextLayoutResult.Size.Width不超过Frame.Width”后，在320宽的四档DPI均失败，运行退出1。

320窗口扣除页/卡片Padding后可用宽256：Wrap示例实际Frame宽256、排版宽358.477；Ellipsis示例Frame256、排版宽318.254。FrameView.Measure的MaxWidth路径直接WithMaxWidth(360/320)，将父256重新扩大，再在排列时缩成256，文字并未以实际内容宽度重新排版。仅把示例从固定width改成maxWidth无法解决。请在通用Frame约束组合中使上限与父可用上限相交，同时保留硬固定Frame及既有Flex/修饰符顺序契约；补真实Skia窄约束下Wrap/Ellipsis的正式回归。

### [P2] 内层滚动区说明文字未换行

同一320场景，说明“多行内容用于演示单轴滚动；下面共40行”Frame宽256，但排版宽274.75。该示例说明需要按当前内容宽度Wrap，不能只依靠Frame截断或滚动裁剪。其余正文若同类问题也须逐项检查。

独立NestedResponsiveExample已验证同一1100宽视口下两个Responsive分别宽616/296并选择宽/窄分支，其内容Frame在各自容器内；此项通过不代替上述实际文字排版检查。请运行独立普通探针（当前新增真实文字断言），不能只断言Frame留在视口内就关闭裁切问题。RL-07仍在实施，暂不放行。

### RL-07 最终复审通过

实现 f8ef52c 已核对，P1/P2均关闭。Codex独立重建并运行真实Gallery探针67/67通过（四逻辑宽×四DPI、实际文本排版宽度、两层滚动、焦点/选区及独立嵌套组件），独立核心300项退出0；核对完整四套300+67+30+204=601全部通过，三示例Debug构建退出0，格式检查通过。指南与实际父级约束及状态边界一致。允许开始RL-08输入/DPI/图形集成；尚不代表Release、原生缩放或最终交付完成。

## RL-08 实施期间独立检查

Codex新增 artifacts/responsive-work/input-invariants（直接引用实际Gallery程序集，不重建共享工程）并独立运行，176项检查全部通过、退出0，日志 rl08-input-independent.txt。四档DPI分别连续1024→320→768→480→1024→320：实际新坐标按钮/Toggle点击、Slider拖动与捕获、中文输入后实际排版原点+caret（未先夹取矢量线）、输入节点/焦点/选区保持、Tab遍历、无焦点后20次空闲不构建/布局/重绘、真实双层滚轮路由及文字缓存容量均通过。此结论仅覆盖该CPU/真实Skia输入探针，不替代本任务GPU、宿主resize及原生模态循环验证。后续实现有改动需复跑。

原生循环证据注意：旧 artifacts/resize-native-review/Program.cs 的最终return未强制frontReadable/expandedPixels>0，front读取失败时会跳过像素检查仍exit0。RL-08/09若复用需报告并校验实际非零采样数量；不能将expandedPixels=0解释为无黑边。该旧脚本只运行简单Text，也不能替代真实Gallery跨断点布局的原生循环验证。

RL-08测试预审：GalleryBreakpointInputTests.DragSlider当前Down/Move/Up使用同一坐标，不足以固定拖动及指针捕获行为；请让Down与Move使用不同位置，至少覆盖从左向右拖动后实际绑定值变化（Codex独立探针已验证该实际动作通过）。请保留旧坐标不命中、实际光标原点检查，不以放宽断言来处理测试场景本身的定位错误。

独立输入探针扩展至180项并退出0：实际Gallery与Nested各240次连续变宽320..1024、DPI1.5。Gallery最大单轮Measure175/Arrange154、每次1轮布局/0动态builder；Nested最大Measure96/Arrange67、每次2轮布局/2动态builder。两者resize期间组件Build增量0、无待处理动态请求，Skia缓存128/64均在上限，纹理live256且后续两帧回到容量内。保留实际计数不作未经测量帧率承诺。

RL-08 GPU覆盖预审：当前GalleryGpuIntegrationTests仅固定900宽、新建runtime、DPI1.25/1.5/2，尚未把响应式跨断点/DPI变化接入最终RGB检查。请增加真实同一runtime从1024到320及DPI1→1.25→1.5→2的重排/文字纹理与最终RGB验证（直接参考沿用现有实现，不需新算法）。这样才能排除仅新建窗口时正确、实际缩放仍复用旧排版/纹理的缺口；保留实际墨迹及裁剪采样断言。

Codex独立GPU探针 gpu-invariants 已在Intel RaptorLake OpenGL3.3上退出0，16/16通过。直接实例化同一Gallery运行时，DPI1/1.25/1.5/2，每档1024→320→768→480；比较实际换行说明整块文字最终RGB与Skia直接颜色绘制，mean0.0585..0.0992、max2（阈值0.75/4），每场实际墨迹2905..10588像素。320时真实排版从宽358.477×37.242变为252×55.863，纹理随DPI为252×56/315×70/378×84/504×112，1纹素=1物理像素。日志 artifacts/responsive-work/rl08-gpu-independent.txt；没有使用旧纹理缩放来冒充重排。此探针不代替正式持续回归及嵌套裁剪/原生循环。

RL-08最终文档预审：responsive-layout.md新加的纹理‘上限为纹理缓存容量’需限定。ITextureService实现有本帧/上一帧引用保护，容量是长期目标，受保护条目可临时超过容量，后续帧回收；不能将本轮样例峰值256的结果写成全局硬上限。请与既有纹理缓存契约一致。
Codex已独立复跑新增242项OpenTK完整套件，全部退出0（日志rl08-opentk-independent.txt）；含7个同运行时RGB检查点、双层裁剪、7步真实宿主150%resize，R16/R17/R18全部保留通过。

### RL-08 最终复审通过

实现 fc5abeb 已核对。本阶段增加38项实际示例/真实Skia/GL集成回归；四套300+67+30+242=639全部通过。Codex独立运行242项完整OpenTK套件退出0，并以独立180项输入/连续缩放与16项同运行时GPU逐像素探针验证。实际拖动、跨DPI纹理更新和缓存长期容量的审核事项均已处理；指南已如实区分程序化resize、旧Text原生探针和人工边界。未发现需改生产代码的问题，允许执行RL-09。最终仍须完成三示例Debug/Release、18 DLL副本、5帧smoke、真实Gallery原生模态循环及截图交付，不以此阶段测试替代。

## RL-09 实施期间独立验收

最终Debug/Release解决方案构建日志均exit0、0编译错误（各保留12条既有xUnit分析器警告）。Codex独立SHA256核对三示例×两配置×三库共18份DLL，18/18匹配；记录final-dll-independent.json。核对三Release实际5帧smoke全部exit0且DPI1.5。
Codex额外独立执行四套Release程序集300+67+30+242=639项，全部失败/错误/跳过0、退出0；日志final-release-*-independent.txt。此验证针对最终优化后产物，不只是Debug测试。等待真实Gallery原生模态循环和原尺寸图像等最后交付证据。

RL-09截图探针预审：rl09-capture.FrameRendered里settle<3直接return，但宿主按需绘制，不保证还有后续帧；请像SmokeTestHook一样在需要下一帧时显式RequestRedraw，确保无人值守截图完成，而不是依赖偶发系统刷新或持续泵消息。仅作用于截图探针，不改生产宿主。原生Gallery/Nested日志已核对均RESULT PASS、非零采样、geometryErrors0。

RL-09交付文档预审：已独立查看真实宽/窄Gallery与Nested原尺寸PNG，视觉与日志一致，未见黑区。请将5张原尺寸PNG复制到docs/delivery下按日期命名的证据目录，并在交付记录中给出可点击相对链接/预览；现仅列ignored artifacts纯文件名，克隆仓库后会丢失计划要求的图像交付。复制既有输出即可，不重采样/编辑图像。另请将Codex独立四套Release639全通过与18DLL核对证据同步进交付文档（return已有真实结果），最终状态由Codex收尾。

### RL-09 与全套响应式任务最终复审通过

核对实现e66bbcd、证据faca6ba，所有实施期审核事项关闭。Debug/Release解决方案构建exit0（0错误、各12条既有分析器警告）；两配置各四套639项全部通过，Release由Codex独立执行。独立18份DLL哈希18/18一致，三Release示例5帧exit0。真实Gallery原生SC_SIZE循环7宽、Nested6观测宽均正常enter/exit，DPI1.5、geometryErrors0、实际非零背景/内容采样、右边缘未绘制像素0，且逐节点布局对照一致。5张原尺寸PNG已保存docs并与原始输出逐一SHA256匹配；已实际查看宽/窄Gallery及宽/窄Nested图像。截图显式请求后续帧缺口修正并复跑。
独立input180/gpu16通过，状态/焦点/选区、双层滚轮、实际文字原点、DPI纹理、连续240次变宽、缓存及空闲契约均通过。最终9任务ID唯一、10条依赖无环、相对链接/UTF8/代码围栏通过；索引9/9完成。macOS/Linux真机与交叉编译、物理鼠标人工拖拽、LCD/ClearType未验证，不将其报告为通过。保留用户AGENTS修改，所有提交均本地英文提交，未推送。

结论：原计划通用Flex/FillWidth、Flow、AdaptiveStack、Responsive安全构建与状态依赖、示例和集成验收全部完成并通过当前已声明的Windows自动化范围，无待修复审核问题。
