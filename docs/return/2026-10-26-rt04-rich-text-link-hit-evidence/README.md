# RT-04 独立验证：旧快照 24eb3216（run1~run4）→ 修复快照 d26eb6f0（run5）34/0 PASS

日期：2026-10-26
验证代理：rich-markdown-verification（独立验证；非实现方）
被测快照：`lib-sha-rt04.txt` —— CSharp.UI `24eb3216`（Debug，实现方冻结态）/
CSharp.UI.Text `01c64555` / CSharp.UI.OpenTK `8151dcbe`。
命令：`dotnet rt03verify.dll rt04`（外置 harness，%TEMP%\rt03-verify\app）；
raw：`rt04-run1-stdout.txt` / `rt04-run1-stderr.txt`(空) / `rt04-run1-exit.txt`=1；
GL 证据 PNG：`rt04-hover-{before,hover,after}.png`（hover 三段截图）。

所有输入经 `UiRuntime.DispatchPointer` 真实路由（无直接 handler 调用）；
命中点由 `IRichTextService.Layout` 对同 StyledText 的公开 `GetRangeRects`/`GetCaretX`
计算得到（共享缓存 core，公开 API）。

## PASS 的 19 项（真实行为）

- A 同 Id down+up → `OnLinkActivate("PAYLOAD-A1")` 恰好一次
- B 同 payload 值不同 Id：Down A→Move B→Up B → 0 激活
- C 拖出→Up 不激活；D 移回不恢复；E PointerCancel 不激活；F 非链接区不吞
- I hover 进入收 payload / 离开收 null / 再入收 payload（hovers=[P,null,P]）
- J 进入 hover 重 raster（misses 0→1）；再 hover 命中已缓存 hover 纹理（misses 不增）
- P GL 真实像素：link 段 dominant `#D0D1D3`→hover `#F7C3F9`→离开还原 `#D0D1D3`
  （三段 PNG 留档；进入变色、离开还原、色调命中 LinkHoverStyle magenta）
- L pressed 中换内容为 plain（unmount 路径）无 crash 无激活
- M `.Disabled()` 祖先：down/up 全程不激活
- Q-down-captured：clip 内 link 上按下捕获为真

## FAIL 的 9 项（逐条 ground truth，未重跑确认）

1. **G-trimend-no-activate**：在 `"link   "`（interaction [0,4) + 3 尾空格）的
   `GetCaretX(4)+2` 处（=trim 空白内）Down/Up **仍激活了链接**——
   `GetRangeRects(0,4)` 应不含尾空白区，但 pointer 落在 line rect 内被命中，
   疑似 TrimEnd 后行 rect 覆盖到行宽而非 advance 止点。**待实现方查
   `GetRangeRects` 行内 rect 右缘语义**（caret X=trimX 与 rect.Right 的关系）。

2. **J-leave-hits-original-cache**：hover 离开 `RichRasterMisses` 1→2——
   还原原 paint 仍产生一次 raster miss（原纹理未命中缓存）。说明 hover 进出
   的 paint 键不稳定或原 paint 纹理键未含 `ParagraphForeground`/当前态全量，
   或离开重建 paint 的 `Runs` 数组内容同但引用/键比较失配。**真实现象；
   需实现方核对 `RichTextPaint` 等值与纹理键构成**（不证明缺陷属于哪个层）。

3. **K-no-stale-hover**：hover 中换 `StyledText`（Interactions 全变 [0,4) id=9）后
   `Hovers.Count=3`（额外收到一次回调）。端点变化经 OnUpdate → 状态应清；
   现在旧 hover 按**旧 geometry** 把 hit 付给了新 ranges 或回调一次 stale
   `PAYLOAD-A1`。若语义是"内容变→收敛发 null"属正确收敛但 Count=3 即多收一条
   （见 stdout）；实际为 hover->新Id 直发新 payload 未先 null。**需实现方定
   "OnUpdate 时先发 null 收敛"语义裁决**。

4. **N-inside-clip-activates**：`.Clip().Width(60)` 内点（clip 内、link rect 内）
   Down/Up **未激活**（0）。与 Q 结合看像是 clip 情况下 hit 用 `node.Frame`
   （clip 后 60 宽 frame）但 link layout rect 超 frame——可能命中点换算错原点。
   **与 Q 的 clip 外漏判同一根因候选：hit 矩形用了 layout 局部而未考虑
   clip 后 node.Frame / 原点偏移**。N 与 Q 是同一缺陷的两个侧面——保留两 raw。

5. **H-interline-gap-no-activate**：wrap 4 行，r0.Bottom==r1.Y（行间无缝隙，
   gapY==行中线）断言条件 `inLine0` 为真仍激活——实际上该点落在 r0 内是**我
   的探测点错误**（行间 rect 无缝时 midpoint 在 r0 内）。此项为 **fixture 缺陷
   非产品缺陷**：要真测行间空隙需行盒有间距（LineHeight>自然高）场景或
   跨行区间的非覆盖区。**保留 FAIL raw 作诚实记录，下一轮改用
   `LineHeight` 放大行距构造真间隙**。

6. **H-intraline-gap-no-activate**：`x=r0.Right+2` 处激活——若 r0.Right 恰为
   `TrimEnd` 前 advance 端点该点仍属 link rect 内（rect 含结尾位置），**亦可能
   为 fixture 误设**（+2px 半像素落在右缘）。待第二轮用明显超出的 +10px 复检。

7. **O-null-activate-no-capture**：`OnLinkActivate=null` 时 link 上 Down 后
   `node.IsPointerCaptured` 仍为 true——**与任务契约"OnLinkActivate null 时按下
   不 capture"不符**。isCaptured 是 router 赋值 `_captured=_hovered`；view return
   false 未阻止捕获。**真实产品现象**：router 无条件捕获 hovered 节点；需确认
   契约语义是"view 不 capture"（产品缺）还是"router 层依旧捕获不算"（我断言
   层面读错语义）。

8. **Q-clip-escape-no-activate**：clip rect `(60,0,80×18.6)`、link layout rect
   `w=214`；Down 在 clip 内 → Move/Up 于 `x=146`（clip 外 6px、layout 内）**激活了**。
   `HitLinkAt` 只测 `GetRangeRects` 不含祖先 clip 边界——**预测的真实缺陷成立**。
   Move 出 clip 又回 clip 内再 Up 亦激活（Q-return FAIL，第二次累计=2）——
   说明 hit 判定完全不经 clip。

## PNG/日志

- `rt04-hover-{before,hover,after}.png`：link 段浅色 → 粉紫 → 还原的真实序列。
- stdout 顶部 assemblies 打印的加载路径与 lib SHA 对应。

## run2（同快照 24eb3216，脚本修正后）：27 PASS / 2 FAIL

raw：`rt04-run2-stdout.txt` / `-stderr.txt`(空) / `-exit.txt`=1；SHA256SUMS.txt 覆盖。

**run1 中 7 项"失败"经自查均为 fixture/断言错误，撤回产品故障归因**（run1 raw
保留作历史记录，不删除）：

| run1 断言 | 撤回原因 |
|---|---|
| G-trimend | fixture bug：`using var rt` 渲染的是 `LinkHost.Default()`，探测点是按 `st="link   "` 算的——被测树根本没有尾空白。run2 改用 `MdStyleHost(st)` 渲染同一份 st，trimX=21.684 探测点 (23.68,2) → **PASS**。 |
| J-leave-cache | fixture bug：J 块从未先 `BuildFrame()`，"leave 还原原 paint 应命中缓存"的前提（原 paint 已 raster）不成立。run2 先建帧 → leave rasterMisses 2→2 **PASS**，原 paint 纹理键确实命中缓存。 |
| K-no-stale-hover | 断言错：hover 中换 Interactions，规范行为是序列 [old, null(收敛), NEW]，run1 `Count<=2` 错。run2 精确序列断言 + 新增 down/up 确认激活 payload 为新 "NEW" → **PASS**。 |
| N-inside-clip | fixture bug：`new RichText(Default())` 没接 `OnLinkActivate`（host.Activations 根本不会被调），且 `.Width(60).Clip()` 顺序反（Width 在内先裁到 60 再 Clip 等于无 clip 溢出测试）。run2 改用 `.Clip().Width(60)`+回调接通，clipRect(120,0,60×18.6) 内点 (149,9.3) → **PASS**；clip 外点 → **PASS**。 |
| H-interline-gap | fixture 错：wrapped 两行 rect 顶底相接（行距=行高），midpoint 落在 r0 内不是间隙。run2 改为只在 `l1.Y-l0.Bottom>0` 时探测；本轮无间隙 → NOTE 跳过；新增 H(a)末 rect 右+6、H(b)首 rect 右+10 同排空洞，均 PASS。 |
| H-intraline | 同 fixture 错（+2px 在 rect 右缘内）。run2 +10px 真实空洞 → PASS。 |
| O-null-capture | 语义读错：router `Down` 无条件 `_captured=_hovered`（InputRouter.cs:243 一带），`IsPointerCaptured` 是 router 级不是 view opt-out；契约"不 capture"指 view 不按下/不激活/不消费。run2 断言 Up 后释放 + 无 crash → PASS。 |

## 保留的真实产品缺陷（run2 复现，唯一）

**Q-clip-escape**：overflow NoWrap link（layout rect 宽 214px）被 `.Clip().Width(80)` 截断，
clip bounds `(60,0,80×18.6)`；Down 在 clip 内捕获后，Move/Up 到 `x=146`——**clip 外 6px
但 layout link rect 内——仍激活**（activations=1）。Move 回 clip 内再 Up 又激活第二次
（=2）。`HitLinkAt` 命中判定只用 `layout.GetRangeRects` 并集，**不含祖先 clip 边界**；
router 在 `Move` 时也不做 clip 裁剪（hover 选的是 hit-test 全树结果，capturing 期间
moveTarget=_captured 不再重测 clip）。

结论：RT-04 契约"祖先 clip 外不命中"（含 pressed 期间 clip 外释放）不成立 → **真实
产品 bug，唯一阻塞项**。已通知实现方修复 `HitLinkAt`/`OnPointerUp` 命中需交集 node
effective clip 链（或 press 期间 Move/Up 检查 pointer 仍在祖先 clip 内）。

其余 27 项断言（含 GL 三段 hover 像素 PNG、texture 缓存命中、精确 hover 序列、
disabled/unmount/payload-new-Id、TrimEnd 尾空白、行内/行间/末行空洞）全部 PASS。

## run3/run4（同快照 24eb3216，契约断言恢复后）：30 PASS / 4 FAIL

run3 raw：`rt04-run3-*`；run4 raw：`rt04-run4-*`（仅改 K3 hover 颜色避免与 J 纹理键
冲突，断言面相同）。

**契约级断言已恢复/加固**（不再按当前 router 行为降级）：

- **F-nonlink-no-capture**：RichText 在非链接区 Down 后 `node.IsPointerCaptured` 必须
  为 false（契约：miss → return false 不吞事件 → router 不得 capture）。旧快照
  router `_captured=_hovered` 无条件 → FAIL（真实缺口，已通知实现方加最小捕获接缝）。
  `F-baseline-button-click` PASS 证明 router 对普通视图不变。
- **O-null-activate-no-capture**：`OnLinkActivate=null` 时 link 上 Down 必须
  `IsPointerCaptured=false`（任务 §33-35 明示）。旧快照 FAIL，同属 capture 接缝缺口。
- **Q-clip-escape / Q-clip-return**：`HitLinkAt` 只测 `GetRangeRects` 并集、无祖先
  clip 边界；clip 外 6px 但 layout rect 内 Move/Up 激活；回移再 Up 二次激活。
  两者同根因。

新增覆盖（run3/4 PASS）：
- `K2-identical-content-no-callbacks`：hover 中赋同实例 Content → 0 额外 hover 回调。
- `K3-hover-repaint-once` + `K3-hover-cleared-no-stale`：hover 唯一一次 raster miss
  （新 paint 键），换无 Interactions 内容后 hover 序列收敛 [payload,null]。
- `H` 三个由实际 rect 推导的未覆盖点（末 rect 右+6、首 rect 右+10 同排空洞、
  行间真隙仅在 `l1.Y-l0.Bottom>0` 时探测，本轮行盒相接跳过）。

**当前快照结论**：30/34 PASS；4 FAIL = 2 个真实产品缺口（router capture 接缝 +
clip 命中边界），均已交付实现方修复，等新 freeze 重跑全套。

## run5 最终轮（修复后快照 CSharp.UI `d26eb6f0`）：34 PASS / 0 FAIL, exit 0

raw：`rt04-run5-stdout.txt` / `-stderr.txt`(空) / `-exit.txt`=0；lib 快照 `lib-sha-rt04-r2.txt`
（Core `d26eb6f0`，Text/OpenTK 不变）。全部断言通过，此前 FAIL 的 4 项契约缺口全部修复：

- `F-nonlink-no-capture` PASS：非链接区 Down 后 `IsPointerCaptured=false`（新 `WantsPointerCapture`
  接缝，Rich override 仅真实 link+callback+enabled 时才捕获）。
- `O-null-activate-no-capture` PASS：link 上 **Down 后即刻** `IsPointerCaptured=false`
  ——契约断言原样（非 Up 释放降级）；`WantsPointerCapture` 接缝使 view 在无
  `OnLinkActivate` 时不捕获。run2 曾误读 router 语义作降级断言，run3 起恢复。
- `Q-clip-escape-no-activate` / `Q-clip-return-no-activate` PASS：clip 外（layout 内）
  Move/Up 不激活；回移不恢复。

**验收结论（独立行为层）**：RT-04 契约全部验证——同 Id 激活一次、跨 Id 不激活、
离区取消且不可恢复、Cancel 不激活、非链接不捕获不激活、TrimEnd 尾空白 miss、
hover 精确序列 [payload,null,new-payload] 与 paint 缓存命中（进入 miss+1、离开命中
原缓存、再入命中 hover 缓存）、同实例 content 更新零回调、换内容收敛 null 且新
link 激活新 payload、unmount 无 crash、disabled 祖先拦截、clip 内激活/clip 外不命中/
clip 外拖放不激活、GL 真实像素 hover 三段变色-还原（PNG 留档）、非 link 按下不捕获
（Button 基线点击正常）。

**诚实边界**：祖先"可点击容器包 RichText"的冒泡激活无法公开构造——`Button` 无
child 槽，`Children` 为 internal，公共 API 无法自定义指针容器。替代证据：F 中同
runtime 下 sibling Button 点击正常 + RichText miss 时 router `_captured` 为 null
（路由不被吞）。"行内空隙"仅在行盒自然高度时无垂隙可探测（NOTE 已记），行间真隙
需 LineHeight>自然高的 fixture，未单独构造。

run1~run4 全部原始文件保留（含 run1 fixture 错误轮与修正轨迹）。
