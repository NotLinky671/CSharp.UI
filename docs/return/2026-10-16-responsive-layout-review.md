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
