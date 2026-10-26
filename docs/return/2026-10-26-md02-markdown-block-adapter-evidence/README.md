# MD-02 独立验证：旧快照 07df28bd（run1~5）→ 中间 1cd040f2（run6/6b）→ 最终 7a6fa83c（run7）41/0 PASS

日期：2026-10-26 · 验证代理：rich-markdown-verification（独立，非实现方）
快照：`lib-sha-md02.txt` — CSharp.UI.Markdown `07df28bd` / Core `00ad025c` /
Text `ac83efba` / OpenTK `7c7418b6` / Markdig `4dab6e49` (1.5.0.0)。
命令：`dotnet rt03verify.dll md02`（`-p:Md02Enabled=true`，%TEMP% 外置 harness）。
raw：`md02-runN-stdout/stderr/exit`（run1~run5 全保留，run5 为诊断隔离版）。
GL 证据：`md02-render.png`（quote bar + hr + list 真实渲染）。

## 确认的真实产品 bug（consumer 级）

1. **空围栏代码块崩溃**：`"```\n```"` → `AdaptCodeBlock` 产出 Range(0,0)，
   `StyledText` 构造抛 `ArgumentException: 样式区间为空或逆序：[0,0)`。
   run2 stderr 无（同步异常被 harness 捕获 NOTE）——stdout 原文：
   `NOTE MD02-empty-fence threw ArgumentException: 样式区间为空或逆序：[0,0)。`

2. **Task list 无 checkbox 标记**：`TaskLists=true` + `- [x] done\n- [ ] todo` →
   渲染文本为 `[•‖ done‖•‖ todo]`——**`[x]`/`[ ]` 未合成进 item marker**
   （InlineAdapter 对 TaskList inline `break` 跳过，BlockAdapter marker 也未补）。

3. **Inline code 非等宽字体**：`x `ii` y `WW` z` → 两字素 run 宽
   `ii=6.78px vs WW=26.14px`，说明 `code` 段用默认 UI 字体而非 mono
   （`MarkdownStyleOptions.CodeFontFamily` null 时应回退 mono，或缺默认）。

4. **MarkdownBlockView 内链接不可激活**：`[ok](https://e/x) [rel](../r/p?q=1)` →
   StyledText 含正确 Interactions `[0,2)id=1 https://e/x`、`[3,6)id=2 ../r/p?q=1`，
   但 `Down/Up` 在链接 rect 中心**不激活**（activations=0）、hover 回调也空。
   **对照隔离**：同一份 `StyledText` 放进裸 `RichText`（同 runtime 参数）
   `acts=1 payload=https://e/x` —— hit 路径本身正常，缺陷在
   `MarkdownBlockView`/`BlockAdapter` 的视图包装层（RichText 实例的
   `OnLinkActivate` 属 init 快照 vs 实际绑定？或交互 State 被外层接管）。
   待实现方排查 `OnLinkActivate = view.OnLinkActivate is { } a ? o => a(o!)` 的
   接线是否在 Component Build 中真实到达 RichText 节点。

5-6 相关失败 `MD02-link-https-activates`/`relative-activates`/`payload-verbatim`
同源（链接根本不激活，payload 无从观测）。

## Fixture/无可测 gap（不归因产品）

- **MD02-hanging-indent**：`frameW=882`——段落 `RichText` 节点未受 160px viewport
  约束（`Width` 修饰符 vs 自适应），item 单排成一行 → 无法验证挂缩进。
  run4 同现；`MD02-320-*` 同理（lines=1）。**fixture 缺陷**：需显式 `.Width(160)`
  或在 320 视口内自然 wrap——等 refreeze 重测。
- `MD02-same-url-diff-id-no-activate` PASS 但只证明"未激活"——因所有链接都未激活，
  该断言暂为弱证；待链接激活修复后此断言才真正可判。
- 主题测试在旧快照双主题均可见 ink（PASS）；hard-coded black HR 在 light 下差异
  待 refreeze 后按"深色下 HR 是否仍可见"的真实像素判。

## PASS 摘要（24 项）

全块映射（h1/para/quote/hr/list×2/fenced code）、中文全块渲染、heading 字号 28>Bold
与正文 17.78 行高差真实测量、嵌套 bold>italic>code 三样式叠加于同 grapheme、
Html Literal/Strip 双向文本校验、危险 scheme（javascript:/data:）无 interaction
不激活、relative URL verbatim、ZWJ/组合字素跨 inline 文本完整+style 边界对齐字素、
双主题 quote+hr ink 像素、GL 真实渲染 PNG。

## run 轨迹

run1 fixture 错误（heading 用错字体、html-strip 过度断言、链接段查找 `Contains
("javascript")` 误命中显示文本而非 URL）→ run2 修正仍 crash → run3 加
interactions 诊断 → run4 全断言跑通 24/6 → run5 加裸 RichText 对照（确认
链接缺陷在 MarkdownBlockView 层非 Runtime）。

等待实现方修复 → refreeze 后全套重跑（unique runN 保留）。

## run6（refreeze 快照 `1cd040f2`）：33 PASS / 4 FAIL, exit 1

raw：`md02-run6-stdout.txt`(命中坐标修正前,28/6) / `md02-run6b-stdout.txt`(
origin+frame-space 修正后,33/4) / 对应 stderr 全 0B / exit 文件。lib-sha-md02-r2.txt。

### 修复确认（旧快照的真实 bug 全部消失）

- 空 fence 不再抛（`MD02-empty-fenced-code` PASS）
- TaskList marker 真实合成：`[x] •‖ done ‖ [ ] •‖ todo`
- mono：`ii`=`WW`=16.406px 等宽断言 PASS
- **链接全链路激活**：origin 修正后 `[ok](https://e/x)`/`[rel](../r/p?q=1)` 真实
  Down/Up → activations=[https://e/x, ../r/p?q=1]（payload verbatim）、
  hover=[https://e/x]、同 URL 双链接 A 激活/B 不激活（Id 身份）——
  run5 的"MarkdownBlockView 内不激活"确证为**我的 fixture 坐标错**
  （frame 全高 600×200 时 ResolveOrigin 垂直居中，探测点需 (7.6,100.3) 非 (7.6,9.3)），
  **不是产品 bug，撤回该归因**。
- hanging indent 真实成立：marker node X=0..5.69,content node X=13.69,
  续行 frame-space X=13.69 对齐内容列（断言修正为 frame-space 后 PASS）。
- fill-width：`frameW=306.31 ≤ 320` PASS（FillWidth 修复生效）。

### 残留 FAIL（真实待查现象，non-阻塞归因）

`MD02-theme-{dark,light}-bars-present/visible`：quote bar `ThemedBar` 节点存在
（`bars=2`）但其 frame=`(0,9.31,3×0)` —— **HStack 行高塌为 0**（hr 240×1 正常）。
`AdaptQuote` 的 `HStack(bar, inner VStack)` 里 bar FixedSize(3,0) + Stretch
期望拉满行高；行高为 0 说明 inner VStack desired height 0 或 HStack 行未分到高。
引用文字本身渲染在（run1 PNG 可见）——疑似 bar 的 Emit early-out `frame.IsEmpty`。
需要实现方确认 HStack/quote 行的高度来源。其余 33 项全绿。

### PNG/诊断

`md02-render.png`（run4 版，quote+hr+list 渲染）保留；run6b NOTE 行含
`ThemedBar` frames 与 rectCmds 明细供实现方定位。


## run7 最终轮（snapshot `7a6fa83c` 含 quote Stretch+FillWidth 真修）：41 PASS / 0 FAIL, exit 0

raw：`md02-run7-stdout/stderr/exit`；lib：`lib-sha-md02-final.txt`
（Markdown `7a6fa83c`、Core `00ad025c`、Text `ac83efba`、OpenTK `7c7418b6`、
Markdig `4dab6e49`）。PNG：`md02-render-final.png`。

**run6 残留 quote-bar 塌缩已修**：`ThemedBar` frames 现为
`(0,0,3×18.6)` + `(0,18.6,240×1)` —— quote bar 与 hr 各自以 RectangleCommand
出现（按 bounds 区分，非总 ink），双主题 rect 中心像素均与 bg 差异成立：

- dark `bg=#15171C`：quote bar 3×18.6、hr 240×1 命令+像素双证；
- light `bg=#F5F6F8`：同。

run7 41 项全过涵盖：全块映射/中文字素、heading 28 Bold 高度差、hanging indent
frame-space（marker 0..5.69 / content 13.69 / 续行 13.69）、嵌套
bold+italic+code 同 grapheme、Html Literal/Strip、**链接经真实 origin 点击激活**
（https + relative verbatim payload、javascript:/data: 无 interaction 不激活、
同 URL 双 Id Down-A/Up-B 不激活、hover payload 回调）、ZWJ/组合字素文本与 range
边界对齐、empty fence、tasklist `[x]`/`[ ]` marker、mono 等宽（ii=WW=16.4）、
fill-width ≤320、双主题 bar/hr 命令+像素、GL PNG。

**验收状态（run7 时点）**：既有断言面独立验证通过；run8 追加的字素-边界
验收发现 7b 缺陷已上报修复，最终验收以字素修 refreeze 后全套重跑为准。

## run8（同 snapshot `7a6fa83c`，新增 7b 跨链接字素边界探测）：41 PASS / 1 FAIL

新增断言组 7b：`[e](https://x)◌́ [f](https://y)◌́ tail`——链接文本 `e` + 后续组合符
构成**单字素**时，interaction 端点必须落在字素边界（契约：max(mark.Start,gStart)/
min(mark.End,gEnd) 归一需取该字素的"owner"归属，而非原样端点）。

**结果（raw `md02-run8-stdout.txt` L2 FAIL + exit=1）**：
`ArgumentException: 区间端点 1 不在字素边界上（会切开代理对/组合序列/ZWJ 序列）。
(Parameter 'text')` —— `Markdown.Parse`/`StyledText` 构造在 InlineAdapter 归一时
按 mark 原端点 [0,1) 切开了 `e+◌́` 单字素。**确定真实产品 bug**（非 fixture：
StyledText 字素校验本身拒绝该端点），与主审 read6417 判断一致。
已通知实现方修归一（端点按字素归属整体并入或整体排除）+ 永久回归。

其余 41 项在 run8 同快照仍全 PASS（与 run7 同断言面，新增组之外无回归）。
等字素修 refreeze 后重跑全套 + 7b。

## run9 最终轮（字素修 snapshot `13ac3c70`）：48 PASS / 0 FAIL, exit 0

raw：`md02-run9-stdout/stderr/exit`；lib：`lib-sha-md02-r3.txt`。

**7b 修复确认**：`[e](https://x)◌́ [f](https://y)◌́ tail` 不再抛——
`text=[é f́ tail]`（10 字符，组合符并入前字素），ranges=`[0,2);[3,5)`、
interactions=`[0,2)id=1;[3,5)id=2`——端点全部落在字素边界、展示保留。

**7c 链式共享字素**：`[e](https://a)[◌́](https://b) x` → `text=[é x]`，
`inters=[[0,2)id=1 p=https://a]`——`e+◌́` 单字素只有**一个 owner**
（首 link，depth 同值按起始确定），真实点击该字素 `acts=1 payload=https://a`，
不重复、不丢、不切字素。

**最终验收**：既有 41 项 + 7b/7c 新增共 48 断言全过；MD-02 独立行为验证完整通过。

