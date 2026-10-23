# 滚动条任务组审核与实施记录（2026-10-23）

日期：2026-10-23。任务组：SB-01（Scrollbar 内部几何/绘制基础）、SB-03（TextEditor 集成）、SB-04（消费方测试迁移）、SB-05（示例接入）。审核：texteditor-plan / 主审。状态：**SB-01~SB-06 门全过**——本驱动 Windows IMM 验收 24/24（raw 见下）、offscreen GPU 6 配置 78 像素检查 PASS、Gallery w320/w1024 真实指针+滚轮+按钮切换证据 PASS；SB-07 正式 Debug+Release 双配置 build/test/smoke 证据在 delivery manifest（两配置各 1434 项、0 警告、samples 6 smoke）。提交推送关联本次实现提交，见 Git 历史。

## SB-01 已修复问题

SB-01 初版 `Scrollbar`/`ScrollbarMetrics`/`ScrollbarPainter` 引入后在审核中暴露四类缺陷，修复均已合入：

- **几何 guard 缺失（真实失败 5 项）**：初版 `Show`/`HitTest` 已有 guard，但 `ThumbLeading`/`DragToOffset` 漏掉对 `contentExtent`/`viewportExtent`/`trackLength` 的非有限/负值保护——`C=+Inf` 时 `DragToOffset` 产生 `Inf`；`C`/`V=NaN` 时 `DragToOffset`/`ThumbLeading` 产生 `NaN`，5 项实测失败。修复后两处同样经有限性/正数检查收口（不含"负尺寸 Rect"式叙述——本轮未观察到负 Rect，观察到的是 Inf/NaN offset）。
- **clip restore 错目标**：真实缺陷是 `ScrollView.Emit` 在滚动条分支后 `PopClip(contentRect)` 恢复到 **子内容 clip** 而非 frame 级 clip，使后续绘制落在错误裁剪域（不是"barEnabled 早退未配对 push track"）。修复为对称 pop 到 frame clip。
- **range/Dragging 状态错误**：`BeginThumbDrag`/`BeginTrackPress`/`ReleaseGesture` 只在真实按下时迁移状态机；`SyncGeometry` 修成四元组（axis/track/contentExtent/viewportExtent）且**排除自身 offset**——初版只比较 axis/track 且仅覆盖 drag 不含 contentRange/trackPress。**pointer-leave 不结束 capture**——只有真实 Up/Cancel/几何变化/disabled/unmount 结束手势。
- **palette 不一致**：track 按交互态在 `ControlBackground`/`ControlHoverBackground`/`Pressed` 间切换；thumb 使用 `Accent`+`Lerp` 提亮；disabled 走 `Disabled*`。统一由 `ScrollbarPainter.Draw` 提供，ScrollView/TextEditor 共享。
- **文档修复**：`ScrollbarPainter` 类 `<summary>` 中 `<paramref name="node"/>` 改为 `<c>node</c>`（`paramref` 在类型 summary 上无效，避免将来开 `GenerateDocumentationFile` 后 CS1734 警告）。

## SB-03 已修复问题

**TextEditor 集成**引入 persistent right gutter：`InnerRect(frame)` = `frame − padding − right gutter(min(12, padded.Width))`；`TrackRect(frame)` 同几何。

- **same-length preedit advance 不重锚**：真实根因是 anchor 变化判定只比较 `PreeditLength`/`CaretDisplay` 而**不比较 preedit 字符串内容**——同长推进（'ni'→'ha'）内容变了但长度/caret 位置不变 → 不重锚。修复：比较 ordinal 实际 preedit 串快照 + `PreeditCaret` + `Replacement.Start` + `CompositionId`，真实内容变化才置 `RequiresCaretReveal`。
- **Measure 排版宽与 Arrange/GetLayout 不一致（本次 cutover 已修）**：修复前 Measure 用 `frameWidth − 2×HorizontalPadding` 计算 `wrapWidth` 并写入 `TextEditorMeasureCache`；`InnerRect` 已恒减 12 → Arrange 调用 `GetLayout` 以 `innerWidth = InnerRect(node.Frame).Width` 作 key 找不到匹配 → 每次 Measure 之后**首次** `GetLayout` 必再排一次（同帧后续命中刚写入的 key，非"每帧重排"）。
  - 修法：Measure 用 `InnerRect(new Rect(0, 0, frameWidth, 0)).Width` 作 `wrapWidth`（值类型无分配；与 GetLayout 同 key，含 0 宽情形——修复后 Measure 直接经 `InnerRect` 得 wrap 宽，不再保旧 `Math.Max(1,…)` floor）；`DesiredSize.Width` 非 tight/非显式 Width 分支改 `Math.Clamp(layout.Size.Width + 2*HorizontalPadding + Math.Min(ScrollbarMetrics.Thickness, layout.Size.Width + 2*HorizontalPadding), minimum, available)`，候选 frame 与最终 inner 同源。未抽 helper。
  - 复核依据：**修复前诊断来自主代理会话内 `--probe` 工具读取记录（transcript，非独立可校验 raw）**：当时同一路径 `sb34-driver/bin/Debug/net10.0/sb34-measure-probe.log` 内容为 `frame140/HP10 → actualInner=108.0、request MaxWidth=120.0、requests total=2 misses=2`；该临时文件随后被修复后再次运行的输出覆盖（归档中的 `sb34-measure-probe-duplicate-after.log` 与 `sb34-measure-probe-after.log` 现字节相同、均为 after 内容），因此**不存在可独立校验的"修前 raw 文件"**，不作 raw before 证明。修复后唯一运行时原始证据：`sb34-measure-probe-after.log` 单编辑器 `MaxWidth=108.0 == actualInner 108.0、requests=1`（frame140 − 2×HP10 − gutter12）。诊断判定以修前源码 `wrapWidth = frameWidth − 2×HorizontalPadding` vs `InnerRect = frame − padding − gutter` 不同源为据 + 会话内 transcript 观察值 + 修后 runtime raw 三重一致。

## SB-04 消费方迁移

Gallery 示例 `ScrollView` 共 4 个：原有外层 `gallery-root-scroll`、旧内层 `gallery-nested-scroll`，本轮新增 `scrollbar-vertical-demo`（纵向 40 行↔4 行切换）和横向 demo。迁移路径：

- `samples/…/Program.cs` 注入 `.Key("gallery-root-scroll")` 与 `.Key("gallery-nested-scroll")`（instrument，不是行为契约；只供测试定位）。
- `tests/CSharp.UI.OpenTK.Tests/GalleryIntegrationHost.cs` 新增 `GalleryScrollLookup.RootScroll`/`NestedScroll`：按 `HasIdentityKey`+`IdentityKey` 在 `Root.Descendants()` 命中节点；命中 `FontView`/修饰符则向其下取首个 `ScrollView`。
- 受影响测试文件全部走 `GalleryScrollLookup`：`GalleryNestedScrollTests`/`GalleryTextSamplingTests`/`GalleryBreakpointInputTests`/`GalleryDpiLayoutTests`/`GalleryGpuIntegrationTests`/`GalleryResponsiveTests`。
- 结构性 incidental 断言删除（不 re-pin）：
  - `ScrollViews.Count == 2`/`scrollViews[0|1]` 索引字面量
  - `Assert.Equal("gallery-nested-scroll", keyed.Key)` —— Key 是定位 instrument
  - `Assert.Equal(300−12, …)` / `Assert.Equal(160−12, …)` / `Assert.Equal(180−12, …)` / `Assert.Equal(180−12−60, …)` 这类"视口宽 − gutter"的公式化重钉 → 改为语义断言（`widths[^1] == body.Frame.Width`、`label.X >= body.Right − ε`、`body.Right <= stack.Right + ε`、`widths[^1] < 200` 阈值）。
- 真实行为约束保留：横向滚动容器内叶子允许越过视口（被 clip）；右上角像素探针左移 12 **逻辑像素**（轨道槽占据非清屏色；逻辑 12 按当次 DPI 映射为物理像素，非固定 12 物理像素）；`InnerViewportCommands` 现在先找 frame 外层 clip 再找内层 content-rect clip（`frame.Width − ScrollbarMetrics.Thickness`），逐行可见性仍与 `AssertRowsMatchClip` 一致。
## 验收证据

（以下 tmp 日志为开发过程 Debug 单配置中间证据——已按原始字节复制到 [review-evidence/](2026-10-23-scrollbar-review-evidence/) 持久化，SHA256 见 `SHA256SUMS.txt`；不代表最终双配置/真实 mspinyin 验收，正式证据以 fast 独立验收产物为准）

- 中间轮原始日志：[opentk-fresh-run.log](2026-10-23-scrollbar-review-evidence/opentk-fresh-run.log)（旧测试 DLL + literal index 迁移前多失败）、[opentk-r2.log](2026-10-23-scrollbar-review-evidence/opentk-r2.log)/`r3.log`/`r4.log`（逐步迁移）、[integration-r1.log](2026-10-23-scrollbar-review-evidence/integration-r1.log)/`r2.log`/`r3.log`/`r4.log`、`core-final.log`；SB-02 真实窗口交互探针输出：[sb02-real-window.log](2026-10-23-scrollbar-review-evidence/sb02-real-window.log)（10 项 PASS）。未列入的 r3/r4/core-final/opentk-build 等完整 scaffold 轮次仍在 `~/.omp/tmp/scrollbar-regression/`（非持久路径）。
- **SB-03 Measure cutover 前 CPU 全量**（2026-10-23）：
  - `CSharp.UI.Tests`：Total 618，Failed 0。
  - `CSharp.UI.Integration.Tests`：Total 53，Failed 0。
  - `CSharp.UI.OpenTK.Tests`：Total 678，Failed 0。
- **SB-03 Measure cutover 后 CPU 全量**（本轮,源码 SHAs：core `f634c732…`、OpenTK `4a3308be…`）：
  - `CSharp.UI.Tests`：Total 618，Failed 0（`core-postfix.log`）。
  - `CSharp.UI.Integration.Tests`：Total 53，Failed 0（`integration-postfix.log`）。
  - `CSharp.UI.OpenTK.Tests`：Total 678，Failed 0（`opentk-postfix.log`）。
- **本驱动实机验收（Windows 150% DPI, IMM, 进程内 mspinyin profile 激活后还原；非Windows与TSF路径本轮未验证）**：最终 driver **24/24 PASS**（`actual-ime-exact-2-final24.log`，`host exit=0 fails=0`）。真实场景：profile 身份确认（`clsid=81d4e9c9… guid=fa550b04…`）、IM.precondition（HIMC open + 中文转换模式 + HKL 0x8040804 + 前台窗口 + 编辑器聚焦）、'nihao' 预编辑 + `anchorLive`、bar thumb-drag 使 caret 滚出视口 → `anchorLive=False`(Unavailable，ScrollY 60.3→2126.5)、release 后 3 快照 ScrollY 保持（2126.5 无回弹）、drag 回 baseline（anchor 时记录值 60.3）至 53.2 后 re-anchor 成功、Space 提交**字节级精确**（真实 `Replacement=(84,84)`，`expected = doc[..84]+"你好"+doc[84..]` Ordinal 相等，`writes 0->1`, `undoDelta=1`, `composing→False`）、caret/anchor/undo 跨 bar 拖拽不变、Esc 取消零写入。
  - 失败轮原始记录（同 binaries、同 loaded SHA，不删不修饰）：
    - `actual-run-first22-containsonly.log`（22/22 PASS，但 commit 断言仅 `Contains("你好")`——已按审核要求加强为精确比对，仅作历史轮证据）。
    - `actual-final-run-t3t4-grabmiss.log`（同 binaries；`T3.crossLineSelect anchor=2 caret=2`、`T4.barDragScrolls drag=False`：该轮 Down 未实际按中 thumb/文本区——原因字段仅含 `anchor=2 caret=2`/`drag=False` 观察值，**具体失败机理未定位**，不笼统归因 host flaky；全部 IME 检查该轮仍 PASS）。
    - `actual-ime-exact-foreground-notready.log`（SV1b–T4 全失败但 `IM.precondition`/`IM1`/`IM2`/`IM3` 全 PASS：该轮窗口在首批指针输入前未获前台，早期点击/滚轮未达控件；IME 段在 `SetForegroundWindow`+pump 后正常。此为启动时序窗口前台竞态，非产品断言回归）。
  - CPU 层（真实 `UiRuntime`+真实 `DispatchPointer`/`DispatchWheel`+`StubTextService` 排版）：`cpu-run.log`/`sb34-cpu-evidence.log`，C1–C8+C7b 全 PASS——轴翻转、禁用门、capture 取消、几何取消、内部 wheel、拖拽中卸载、嵌套 wheel（内层消费）、**真实捕获 ScrollView thumb-drag + capture 中 content extent 收缩 1050→52.5 → 手势取消 + offset 钳制到新 max + Up 正常**（C8）、headless 编辑器 bar-drag 经控件自身 `TrackRect/InnerRect/HitTest` 命中 Thumb 且 `ScrollY` 走 326.4（C7b，证明 headless 可真实驱动 bar 路径；先前 `press=None` 系 driver 误读 stale `InteractionState`+手算 track 偏差，非 stub 限制）。
  - Measure after-probe：`sb34-measure-probe-after.log`——单一编辑器场景 `MaxWidth=108.0 == 真实 innerRect.W=108.0`（frame140 − 2×HP10 − gutter12），确认 runtime Measure 换行宽与 Arrange/GetLayout 同源。
  - 初版 GPU harness（`gpu-initial-insufficient*.log`、`sb34-gpu-s*.png`）：仅证明轨道条带非背景色与渲染尺寸正确；其 inner-bottom 断言与 `SetEnvironment` 未真达 disabled/clip 语义验证标准，**不作为图形验收证据**。PNG 为真实 GL 帧面 320×240/480×360/640×480（scale 1.0/1.5/2.0 × Light/Dark），仅作面元数据留存。
  - **正式 GPU 验收（primary，offscreen framebuffer 逐像素比对）**：`gpu-proof-run.log` = **78 checks PASS, 0 fail**，6 配置（`s1`/`s1.5`/`s2` × Light/Dark，framebuffer 物理像素随 pixelScale 缩放 360×240/540×360/720×480），每配置断言：fb 尺寸匹配、normal/hover/pressed/released/moved-off thumb 逐像素命中主题色（`#2563EB` Accent、`Lerp(white,15)` hover、`Lerp(black,20)` pressed）、track=ControlBackground、**disabled thumb=`DisabledForeground`+track=`DisabledBackground`（真实 `.Disabled(true)` 视图状态）**、无溢出时 gutter 列无 accent 像素（bar 隐藏）、ScrollView gutter=track 色无红色越界（clip 生效）、frame 外为背景。36 张真实 surface PNG（`gpu-proof-sb-gpu-s{1,1.5,2}_{light,dark}-{normal,hover,pressed,clip,disabled,nooverflow}.png`）逐像素可复核。
  - **Gallery 集成验收（primary，真实 `DispatchPointer`/`DispatchWheel` + 按钮切换重建）**：`gallery-proof-run.log` = **32 checks PASS, 0 fail**，w320 与 w1024 各 16 项：fb 尺寸、keyed demo 命中 `ScrollView(Vertical)`、外层滚动将 demo `scrollIntoView`（w320 8 轮、w1024 5 轮迭代）、长内容 `content=900.8 > viewport=120.0` 显示 thumb=Accent/track=ControlBackground、内层 wheel `0.0→780.8`、thumb 随滚动位移（w320 `609.4→701.4`）、真实按钮点击（`hovered=Button("切换为短内容（隐藏滚动条）") pressed=True`）把内容缩到 `86.5<=120` → gutter 列无 accent（bar 隐藏）、再加高视口 `120.0→180.0` 回归正常。8 张真实渲染 PNG（`gallery-proof-sb-gallery-w{320,1024}-{long,scrolled,short,height}.png`）。stage 定位经 `.Key` instrument（`ScrollbarDemoStage`），与旧 `InnerViewportCommands` caller 路径不混用。
  - 视觉限制：GPU/Gallery 均为 **offscreen framebuffer** 证据，不等于 OS Input 级验收；真实 OS 输入（150% DPI Windows IMM）仅本驱动 `actual-ime-exact-2-final24.log` 一组（24/24）。

## 遗留与限制

- 右上角像素被 `ScrollbarPainter` 的固定轨道槽占据是产品变更（非清屏色），属真实 surface 修改而非回归。
- 已验证范围仅 **Windows 150% DPI 默认 IMM + mspinyin** 单配置实机证据（`actual-ime-exact-2-final24.log`）；非 Windows 平台本轮仅保持 committed-only 回退未实机验收，TSF 内部路径本轮未做多行 bar 验收——不暗示其余平台已由 fast 补齐。CPU 层不动产物（`TextEditorState.Bar`/`RequiresCaretReveal`）已就位。
