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
