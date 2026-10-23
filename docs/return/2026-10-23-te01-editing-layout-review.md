# TE-01 编辑排版契约返修记录

日期：2026-10-23。审核：texteditor-doc-review（独立）。状态：**返修中**（good 已认领全部条目并正在修，freeze 后复审）。

依据：`docs/task/01-editing-layout-and-sanitize.md` + `docs/plan/2026-10-23-text-editor-plan.md` §3.1/§3.2。审阅快照：`src/CSharp.UI/Text/TextContracts.cs`（含 `TextLayoutResult` 新 API）、`src/CSharp.UI.Text/TextLayoutBuilder.cs`、`src/CSharp.UI/Input/InputEvents.cs`、`src/CSharp.UI/Editing/TextInputContracts.cs`、`src/CSharp.UI/Testing/TestDoubles.cs`。

## R1：非编辑路径也建编辑 caret 表（热路径分配）

- 位置：`TextLayoutResult` 构造/`CaretsByLine` getter（TextContracts.cs:177-178）。
- 问题：`CaretsByLine` getter 对**所有** layout（含 `Text`/`TextField` 显示路径）首次访问即 `BuildDefaultCaretsByLine()`：O(stops×lines) 分配；且 `GetCaretX`/`LineIndexForIndex`/`GetCaretBounds(affinity)` 等查询路径触发全表构建——显示路径只为取一个 X 付出整表成本。
- 期望：非 Editing 请求的编辑表完全惰性且查询 API 不依赖整表；热路径（绘制/命中/caret）零额外分配。
- 复审方式：读修复后代码；Editing=false 构造后调用 `GetCaretX(i)`/`HitTestIndex` 不分配（可用 GC.GetAllocatedBytesForCurrentThread 度量）。

## R2：`CaretPositionsFor` 每次调用 new List + 全表 O(lines×stops) 扫描

- 位置：TextContracts.cs:415-437（`GetCaretBounds(index, affinity)` :349-361 调用点）。
- 问题：每次 caret 命中都 `new List<CaretPosition>` + 遍历全部行子表，且循环内反复 `LineIndexForStop`（线性）；高频路径（caret 绘制、IME 几何发布）不可接受。
- 期望：按 `Lines` 边界二分定位目标行 + 行内子表二分查 stop，无分配。
- 复审方式：读修复后代码 + 精确几何断言（共享 offset 两 affinity 矩形分别落在上下行真实位置）。

## R3：`GetRangeRects` 行尾 X 取错行

- 位置：TextContracts.cs:402-403 `GetCaretX(lineStart, LineStart)`/`GetCaretX(lineEnd, LineEnd)`。
- 问题：当 `lineEnd == line.End` 且该 offset 同时是**下一行 Start**（硬换行 terminator 后 offset，如 `"ab\ncd"` 的 3）时，`LineEnd` 语义应停在本行 terminator 前；现有实现对共享/terminator 边界的 X 归属可能读到下一行 OffsetX 或旧单 X fallback，行尾选区矩形跨错行。
- 期望：行尾右缘 = 该行**行内**最后一个 caret 的真实 X（terminator 前 offset，即 `LineEndIndex(line)` 的 X）；选区含换行符时追加该行零宽 marker，不把下一行内容纳入。
- 复审方式：真实 Skia 几何——`"abcd\nx"` select [0,5)：第一行完整宽 rect + 行 0 零宽 newline marker，行 1 零 rect；`"a\n"` select [0,2)：不画尾随空行。

## R4：空选区/空行 marker 语义错误

- 位置：TextContracts.cs:391-399。
- 问题：`lineStart >= lineEnd || snappedStart == snappedEnd` 分支给 `line.Start == line.End` 的行无条件加 marker——空选区 `[i,i)` 也会给所有空行产 rect；真正的需求是"选区真实覆盖到空行"才给该行零宽全高 rect。
- 期望：空 range（`start==end`）返回空列表，无任何 marker；空行仅当选区确实覆盖其 offset 时产 rect（含 newline 的 selection [2,3) 覆盖 `\n` 后空行的真实用例）。
- 复审方式：`GetRangeRects(1,1)` 返回空；`"a\n\nb"` 中 select 覆盖第二个 `\n` 时中间空行产 rect。

## 其他已发 good 的非阻塞项

- `HitTestLine` affinity 判定中 `Text[stop-1] is not ('\n' or '\r')` 对 `\r\n`/孤 `\r` 分支归类正确性待 smoke 实证（`"a\r\nb"` offset 3、孤 `\r` 场景）。
- `StubTextService` editing 分支按 `\n` 切段（注释称 CR 已归一化）——与真实 builder 对 `\r\n`/孤 `\r` 的 SplitParagraphs 行为一致性待核（stub 是测试替身，不能掩盖真实差异）。
- 永久测试须用真实 `SkiaTextService` 后端（tests/CSharp.UI.Text.Tests），core 侧只留值模型/selection 查询/EditOperation 边界；删除 stub-echo/wording/cache-copy/NotNullCount 类断言（主审已发 good）。

## 复审流程

good freeze 通知后：独立 `git diff` 读全部 touched code → 跑新增真实 Skia 测试 + 既有 Text/TextField 回归（串行 build，与 good 协调）→ throwaway 真实字体 smoke（local 临时工程，不入 repo）按 §R1-R4 精确几何断言复核 → 结果更新本文件（复审结论以本文件为准）→ 主审 read 确认后才标完成。

## 复审 1（freeze 后真实 Skia smoke，`~/.omp/tmp/te01-smoke` throwaway 工程，FontDescription.Default）

33 断言：30 PASS。R1-R4 修复经代码复审+运行证据确认有效（ResolveLineIndex 零分配、CBL lazy、GetRangeRects 行内 clip+marker、空 range 无 rect、中间空行覆盖产 marker、`LayoutMisses`+2 缓存隔离）。新增：

## R5：硬换行 terminator 后 offset 在非 LineStart 下归错行（真实几何缺陷）

- 位置：`TextContracts.cs` `ResolveLineIndex`（:203-235）+ `GetCaretBounds(affinity)`/`GetCaretX(affinity)`/`LineIndexForIndex`。
- 复现：`"ab\ncd"` Editing=true（真实 Skia）。offset 3 是 `\n` 后位置，只存在于下行 CBL：
  - `GetCaretBounds(3, Unspecified)` → Y=0（行 0）；`GetCaretX(3, Unspecified)=0`；`LineIndexForIndex(3, Unspecified)=0`。
  - `GetCaretBounds(3, LineEnd)` → Y=0（行 0）；仅 `LineStart` 正确归行 1。
- 根因：`Unspecified` 早退走 `LineIndexForStop`（`stop<=End` 归上行）；`LineEnd` 分支 `snapped==line.End` 命中但 `FindCaretX(0,3)=null`（builder 已不把硬边界 offset 放上行 CBL）→ 仍返回 primary。
- 期望：硬换行 terminator 后 offset 唯一下行 `LineStart`——任何 affinity（含 Unspecified/LineEnd）都解析到唯一真实位置（行 1）；`FindCaretX` 未命中时查相邻行或按 `Text[stop-1]` 换行符判定。
- 消费场景：caret 落 `\n` 后（点击下行行首、Enter 插入后 caret=新行首）→ caret 画在上一行末尾。
- 状态：已发 good 修复，待复审。

## 澄清项（非缺陷，待 good/主审确认语义）

- `"hello\n"` 行 0 最右点击 → `HitTestLine` 返回 idx=5, aff=`Unspecified`（offset 5 非共享、行内容末尾唯一位置）。TE-03 行尾点击若期望 `LineEnd` 需确认契约意图；当前 Unspecified 自洽。

## 复审 1 其余通过项（真实 Skia 证据）

尾随空段（2 行/零宽/第二行 Y）；Editing=false `"a\n"` 单行回归；软 wrap 双真实 X（up=21.85/dn=0.00）与 `GetCaretX` 双 affinity；`LineEndIndex(0)=2`；CBL[0] 不含硬边界 offset 3；行尾空白推进 vs display TrimEnd 夹紧；CRLF 单字素+offset 3 唯一下行；select[0,5) 行 0 内容+newline marker 不含行 1；`"a\n"` select[0,2) 不画尾空行；空 range 0 rect；`"a\n\nb"` 覆盖中空行产 marker；`LayoutMisses`+2；`SanitizeMultiLine("a\r\nb\rc\td")`→`"a\nb\ncd"`；`TryCreate` Range 不回裁+全滤空拒绝；Editing=false `trail  ` 尾 caretX≤内容宽。

## 复审 2（同一 throwaway smoke 追加 R6 用例，pre-fix 基线证实 4/4 缺陷真实存在）

## R6：`GetRangeRects` marker/terminator 语义三缺陷（主审定位 + smoke 证实）

- R6a（newline-only selection 无 marker）：`"ab\ncd"` select [2,3) → `rects=0`。`lineStart==lineEndBound` 触发 continue，terminator 范围∩selection 未独立产 marker。期望：行 0 一个零宽 marker。
- R6b（软 wrap 误产 marker）：`"abcdef"` wrap 断两行，select [1,5) → `rects=3` 含 `(21.8,0,0x18.6)` 零宽"marker"（`snappedEnd > lineEndBound` 误触发）。软换行**从无** newline marker。期望：仅 2 个非零宽内容 rect。
- R6c（全空白行误判为空行）：`"   \nx"` select [0,3) → 仅 `(0,0,0x…)` 一个 marker。空行判据 `Bounds.Width<=0` 按 trimwidth 错；三空格行应画内容 rect（行内 caret 有真实 X 推进）。期望：按内容 offset 判空行（无 caret stop/Start==End），不按 trim width。
- R6d（CRLF-only selection 无 marker）：`"a\r\nb"` select [1,3) → `rects=0`。`"a"`内容 [1,1) 空 + `\r\n` terminator 独立相交未产 marker。期望：行 0 内容 rect（'a'）+零宽 `\r\n` marker（或按半开区间实际覆盖产 marker）。
- 整体算法要求（主审）：每行=内容 caret 区∩selection 独立 rect + 真实 terminator 范围∩selection 独立 marker；marker 不依赖 contentrect 存在；软 wrap 从无 marker；空行按内容 offset 非 trimwidth。
- 状态：已发 good，待修复复审。

另：`LineIndexForStop` 注释称二分实为 for-scan——主审要求改真二分（display 语义不变）；`"hello\n"` 行末 idx=5+Unspecified 确认允许，不补 label 断言。

## 复审 3（修复后重跑，39 断言全 PASS）

- R5 关闭：`"ab\ncd"` offset 3 在 Unspecified/LineEnd/LineStart 下 `GetCaretBounds`/`GetCaretX`/`LineIndexForIndex` 全部归行 1（Y=18.6）；`ResolveLineIndex` 加 `isPostBreak`+`downIndex` 前置分支，`LineIndexForStop` 改真二分（display 语义不变）。
- R6 四项关闭：`ab\ncd`[2,3)→1 个行 0 marker；软 wrap[1,5)→2 内容 rect 无 marker；`"   \nx"`[0,3)→三空格真实内容 rect（11.5 宽）；`a\r\nb`[1,3)→1 marker（半开 [1,3) 不含 'a'，单 marker 为正确语义，前述"内容+marker"预期已修正）；`a\n\nb` 中间空行覆盖产 marker、空 range 0 rect、`end==下行Start` 不误选——全部实证。
- T8a 澄清关闭：offset 5 唯一位置 `Unspecified` 接受，不强求 LineEnd 标签、不加断言（主审确认）。
- R1-R4 此前已证有效；Editing=false 回归项全绿。
- good 自报 core 561/561 + Text 79/79 绿、0 警告；本记录的运行证据以独立 throwaway 真实 Skia smoke 为准。

## R7：软 wrap 共享 offset 三 affinity 归属不一致（good 已修，待复审）

- 位置：`TextContracts.cs` `ResolveLineIndex`（原 :203-263）。
- 复现（fast 独立实测，新构）：`"abcdef"` Editing=true MaxWidth=30 软 wrap，shared offset=3（line0 End=3=Y0，line1 Start=3=Y18.6）；`GetCaretBounds(3,LineEnd)`→(21.85,**18.6**) line=**1**（X 取自上行 CBL=21.85 但 Y/lineIndex 归下行）；`Unspecified` 同混行；仅 `LineStart` 正确。
- 根因：`LineIndexForStop` 二分取 latest `Start<=stop` → shared offset 归下行 primary=1；`LineEnd` 分支只判 `snapped==Lines[primary].End`（1.End=6≠3）不命中 → 落回 primary=1 且 x=null → `GetCaretX` 旧单 X fallback（上行值）→ X/Y/line 三量不一致。
- 修复（good）：`ResolveLineIndex` 重写为三段判定——`isPostBreak`（`Text[stop-1]` 是 `\n`/`\r`）任何 affinity 唯一下行（`primary` 已是下行）；`isShared`（软 wrap 共享 `Lines[primary-1].End==stop==Lines[primary].Start`）`LineStart`→下行/`Unspecified`与`LineEnd`→上行；非共享忽略 affinity 归 `LineIndexForStop` 行。`x` 经 `FindCaretX(target)` 取目标行 CBL 真实坐标。
- 运行证据（throwaway 真实 Skia，C:/tmp/te01repro + Text.Tests bin deps）：shared offset `Unspecified`/`LineEnd`→`(21.85, 0.0)` line=0，`LineStart`→`(0.0, 18.6)` line=1；`"ab\ncd"` offset 3 三 affinity 均 `(0.0,18.6)` line=1。`docs/agents-notebook/te01-affinity-repro/softwrap-affinity.txt`。
- 新增真实 Skia 回归：`SoftWrapSharedOffset_AffinityResolvesConsistentLineXAndY`（X/Y/lineIndex 三元一致）+ `HardBreakPostOffset_ResolvesToLowerLineForAllAffinities`（硬换行后 offset 全 affinity 唯一下行），`TextEditorEditingLayoutTests`。自测 Text.Tests 81/81、core 561/561。
- 状态：已修，待独立复审（本项要求覆盖全部 affinity 三元 X+Y+row，已实测）。

## 剩余待办（不阻塞 TE-01 完成判定，交主审决定）

- TE-01 任务文件 `docs/task/01-editing-layout-and-sanitize.md` 契约节可补"硬换行后 offset 唯一下行 + GetRangeRects 三区段"措辞（good 询问中）。
- TE-01 完成状态勾选与提交由主审 read 本记录后决定；我未 commit/push。

## 复审 4（next-step-validation 独立接手，2026-10-23 晚；throwaway `~/.omp/tmp/te01b-smoke`，真实 Skia，程序集 `CSharp.UI.Text.dll` 写时间 2026-10-23T14:26:09 —— 当前源码新构）

## R7：软换行共享 offset 经 `GetCaretBounds`/`GetCaretX`/`LineIndexForIndex` 在 LineEnd 与 Unspecified 下产出混行矩形（真实几何缺陷，主审源码推断已运行证实）

- 位置：`TextContracts.cs` `LineIndexForStop`（:469-489）+ `ResolveLineIndex`（:203-263）。
- 复现：`"abcdef"`、`MaxWidth=30`、`Wrap`、`Editing=true`（真实 Skia）：Lines=`[0,3)`Y=0 W=21.8、`[3,6)`Y=18.6 W=20.0；CBL 正确（L0 `(3@21.85)`、L1 `(3@0.00)`）；共享 offset `s=3`：
  - `GetCaretBounds(3, LineEnd)` → **(X=21.85, Y=18.6)**，`LineIndexForIndex=1`：Y 是下行但 X 是上行末 X——混行矩形，caret 画在下行 21.85（超过该行内容宽 20.0 之外）。期望 (21.85, Y=0.0) 行 0。
  - `GetCaretBounds(3, Unspecified)` / `GetCaretX` / `LineIndexForIndex` → 同样 (21.85, 18.6) 行 1：混行，不是任何一个真实位置。
  - 仅 `LineStart` 正确：(0.00, 18.6) 行 1。
- 根因（两处叠加，与主审推断一致）：
  1. `LineIndexForStop`（:469-489）按 `Lines[mid].Start <= stop` 取**最大 Start**，共享 offset `s==Lines[1].Start` 归**下行**——与自身注释"边界归上行"及 display 语义相反。
  2. `ResolveLineIndex` 行 231 `LineEnd` 分支只查 `snapped==line.End`；primary=下行时 `snapped==line.Start`（即 `prev.End`）的情形完全没有分支（`LineEnd` 需要查**上一行** CBL）；行 241 的 `LineStart` 分支条件相反同样帮不到 `LineEnd`/`Unspecified`。
  3. `x` 保持 null → `GetCaretX`/`GetCaretBounds`（:188,:381）回落 `GetCaretX(snapped)` 全局 `caretXs`——builder 主循环（`TextLayoutBuilder.cs:146-151`）用自身线性 `LineIndexForStop`（**首个**含 stop 的行）给共享 offset 产上行 X=21.85，与返回的下行 lineIndex 拼出混行矩形。这就是主审所说"GetCaretX fallback 旧 globalX 恰好 upperrowX 可能掩盖"——X 数值看似正确但 Y/LineIndex 错行，只有 X+Y+LineIndex 三项一起断言才暴露。
- 期望：共享 offset 的 `LineEnd`→上行（Y0，X=上行内容右缘）、`LineStart`→下行（Y1，X=OffsetX）；`Unspecified` 落到契约默认一侧的**同一行真实 (X,Y)**——建议与 display 语义统一归上行（`LineIndexForStop` 边界归上行即满足，同时硬换行 post-terminator 由既有 `isPostBreak`+`downIndex` 分支继续正确归下行，probe 各 offset 遍历显示其余位置行为不变）。
- 修法提示（供 good）：最小修是把 `LineIndexForStop` 二分改为边界归上行（candidate 取首个 `stop<=End` 的行，或候选后若 `candidate>0 && stop==Lines[candidate].Start && stop==Lines[candidate-1].End` 回退一行）；`ResolveLineIndex` 其余分支经 probe 全 offset 遍历验证不受影响。若保留下行 primary，则需为 `LineEnd`/`Unspecified` 补"prev 行 CBL 含 stop"分支。
- 消费场景：TE-03 caret 落在折行点按 `LineEnd` 绘制（从左侧移入/点击行尾）→ caret 画到下一行、超出该行内容宽；IME 几何发布同一坐标会跟着错。
- 状态：已报 good（本记录即复审输出），待修复后复审——复审需断言 X+Y+LineIndex 三项，不只 X。

## 复审 5（next-step-validation，R7 修复后独立复审，2026-10-23 深夜；程序集 `CSharp.UI.Text.dll` 写时间 2026-10-23T14:26:09 新构）

- `TextContracts.cs:203-244` `ResolveLineIndex` 已读：三段分派正确——`isPostBreak`（text[stop-1] 为换行）→任何 affinity 归 `primary`（下行，因 `LineIndexForStop` 取 latest Start≤stop）；`isShared`（软换行共享 offset）→`LineStart` 归 `primary`（下行）、其余（含 `Unspecified`）归 `primary-1`（上行）；非共享忽略 affinity 归 `primary`。`LineIndexForStop` 二分本身未改（边界归下行保留），语义补偿由 isShared/isPostBreak 分支承担——display 语义不变。
- good 留存证据 `docs/agents-notebook/te01-affinity-repro/softwrap-affinity.txt` 与下方独立运行一致。

### 独立运行证据（我亲跑，非转述）

| 命令 | 结果 |
|---|---|
| `dotnet run` @ `~/.omp/tmp/te01b-smoke`（R7 专用 probe） | exit 0，R7-end/R7-start/R7-uns/R7-mixed 全 PASS；共享 offset 3 三量一致：LineEnd/Unspecified→(21.85, Y=0, li=0) 上行，LineStart→(0.00, Y=18.6, li=1) 下行；全 offset 0-6 遍历无回归 |
| `dotnet run` @ `~/.omp/tmp/te01-smoke`（39 断言全套） | **38 PASS / 1 FAIL：T8a**——`HitTestIndex` 点击 `"hello\n"` 行 0 右侧空白返回 `idx=5, aff=Unspecified`。复审1 已澄清 Unspecified 为可接受自洽语义（offset 5 非共享唯一位置），主审复审3 亦确认不强求 LineEnd 标签——**此 FAIL 是旧断言滞留，非回归**；除 T8a 外全部断言通过 |
| `dotnet run --project tests/CSharp.UI.Text.Tests -c Debug` | exit 0，**81/81**（good 自报 79 增 2 新测试，独立证实） |
| `dotnet run --project tests/CSharp.UI.Tests -c Debug -- -class TextEditorEditingLayoutTests` | exit 0，10/10 |
| `dotnet run --project tests/CSharp.UI.Tests -c Debug -- -class` × 5（TextFieldTests 11 / AtomicEdit 36 / Composition 66 / UndoRedo 62 / CaretAlignmentRegression 15 / PreeditGeometry 16） | 全 exit 0，合计 206/206 |

### 新增真实 Skia 测试亲读（`tests/CSharp.UI.Text.Tests/TextEditorEditingLayoutTests.cs`）

- R7 三量断言（`LineIndexForIndex`+`GetCaretX`+`GetCaretBounds` 绑定同行）覆盖 `Unspecified`/`LineEnd`/`LineStart` 三 affinity —— 正是复审4 要求的"不只 X"。
- 软 wrap 共享 offset 双 `CaretPosition`、硬换行 terminator 后唯一下行、`HitTestLine` 上行→LineEnd/下行→LineStart、行尾空白推进、`GetRangeRects` 行内 X 不归下行 Start——consumer 边界断言，非 stub/echo/计数类。

### R1–R7 关闭建议

- **R7 关闭**（复审4 缺陷已修复并经独立 X+Y+LineIndex 三量证实）。
- R1–R6 维持复审1-3 已证通过（本轮无相关回退）。
- T8a 属历史断言与已澄清语义的差异，请主审决定：更新旧断言为 `Unspecified`（与复审1 澄清一致）或改实现。不影响 R 系列关闭判断。
- good 自报 core 561/561 + Text 81/81 绿与独立运行一致。


## 复审 6（T8a 断言修正后完整 smoke 收官，同日下午）

- throwaway `te01-smoke` 修正：删除 T8a 中滞留的 `aff==LineEnd` 标签断言（唯一位置不要求 affinity，主审确认语义）；`Environment.Exit` 改为 `failures.Count`（不再恒 0）。
- 真实进程退出码重跑：**exit 0，39 断言全 PASS**（0 failures）。R1–R7 全部关闭；无新缺陷。
- 历史各轮 fail 记录保留未动；本轮为 TE-01 最终 smoke 证据。
