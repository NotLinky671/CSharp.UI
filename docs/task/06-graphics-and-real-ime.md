# TE-06：图形集成与 Windows 真实验收

创建日期：2026-10-23

状态：完成。真实 Windows IMM + 微软拼音验收通过（2026-10-23 第三轮修正跑：27 项场景 check 全过,outcome=PASS,exit 0；证据 [2026-10-23-te06-real-ime-evidence](../return/2026-10-23-te06-real-ime-evidence/)。S8 三快照证明:WM_MOUSEWHEEL 送达→ScrollY 0→96 持久→锚点 (132,223)→(132,79) 按 `TextOrigin−ScrollY` 重发 publish 70→71)。图形集成段随 good 修复收口:`TextEditorPreeditRenderingGpuTests`/`GalleryBreakpointInputTests` 已全绿,OpenTK.Tests 678/678 双配置过（汇总见[交付记录](../delivery/2026-10-23-text-editor-delivery.md)）。设计依据 [TextEditor 计划](../plan/2026-10-23-text-editor-plan.md) §3.2、§3.4、§3.6、§5。

[返回任务索引](README.md)

## 目标

离屏/真实图形验证 TextEditor 多行排版、选区、装饰、嵌套滚动；Windows IMM + mspinyin 真机完成多行组合、候选锚点、取消、唯一提交、跨行 Replacement、resize/滚动/焦点/外部绑定验收；TSF 内部路径保持现状回归通过（不新增开关、非交付前置）。问题写 `docs/return/` 循环返修。

## 文件范围

- `tests/CSharp.UI.Integration.Tests/` + `tests/CSharp.UI.OpenTK.Tests/`：真实 Skia/GL 集成与离屏测试。
- 验收记录：`docs/return/` 带日期文件 + `docs/agents-notebook/` 必要证据（真机截图/日志）。
- 不改 `src/`（除返修实际 bug）与 samples（TE-05 已落）。

## 契约（可观察验收）

1. **离屏**：DPI 1/1.25/1.5/2 下多行排版各 DPI 逻辑几何精确（行 Bounds/caret 矩形/逐行选区矩形/行末 marker 与排版模型一致），缩放后实际像素对齐无漂移（caret 不偏离其逻辑行、选区矩形不跨行错位）；纹理/排版缓存键含 `Editing`+wrap 宽，宽度变化重排不错位。**不要求**不同 DPI 位图字节相同。
2. **嵌套滚动**：Gallery 示例内 ScrollView 嵌套 TextEditor——内容可滚消费、到顶/底冒泡外层；拖动边缘自动滚动；resize/AdaptiveStack 断点切换后 caret/affinity/锚点保持。
3. **IMM mspinyin 真机**（150% 单 DPI 同 TI-07 环境前提）：
   - 第二行起 `nihao`→预编辑显示（逐行下划线+组合 caret），候选锚点在组合 caret（`PublishGeometry`→`SetCandidatePosition`/`SetCandidateExclude`/`SetCompositionWindow` 发布位置，系统避让如实记录，不机械要求像素相等）。
   - 选“你好”唯一提交不重复；Esc 取消不退出窗口。
   - 跨行 Replacement 的 Result 精确落地（替换区含 `\n`）。
   - 组合期间 resize/滚动/换焦点/窗口失活/外部绑定更新（含外部写 `\r\n`）按 §3.7 取消接缝。
   - 分段 Result 逐项历史；`a\n` 末尾空行、`a \n` 行尾空白 caret 场景实机核对。
4. **TSF 内部路径**：既有 `UseTsfInputBridge` 经现存测试套件回归通过（`TsfHostIntegrationTests`/`TsfCancellationTests`/`TsfSessionLifetimeTests`/`NativeCompositionOwnerTests` 等现存 TextField TSF 测试，覆盖共享 cutover 影响面）；**不**新增多行 TSF 真机验收门槛，不复活已删除工具，不公开开关。
5. **TextField 回归**：IME/编辑/几何测试全绿为放行前提。
6. **如实记录**：非 Windows committed-only/待真机；多 DPI/多显示器为环境边界；缺证据项如实待验证不标通过。

## 返修

首个真实失败交主审从原始日志/截图/源码定位根因；只修确证缺陷；`docs/return/` 记录每轮问题→修复→复审，不预写交付结论。

## 本轮回归维护（2026-10-23）

`GalleryBreakpointInputTests.输入框跨断点缩窄后光标真实位置可见` ×4 DPI 在本轮全套回归中失败：`GalleryApp` 新增"多行编辑"节把目标 TextField 推到整页 ScrollView 视口外；完全滚出视口的节点 `PushClip` 全裁出、不产任何绘制命令，故 `CaretCommand` 收集为空（`Assert.Single` 失败）。测试此前隐含假设 1600 高视口足以显示目标（布局 incidental）。维护：聚焦前与 `Resize` 后经外层 ScrollView 真实滚轮（`BringInnerIntoView`，复用同文件既有实现）把字段滚入视口；caret 真实 X 可见性、像素对齐夹取界限、字素中点 caret 精度等既有断言不变量原样保留。产品无改动（未要求通用 focus-自动滚动，不顺手扩 scope）。

## 实施记录（2026-10-23，离屏图形阶段）

### 新增

`tests/CSharp.UI.OpenTK.Tests/TextEditorPreeditRenderingGpuTests.cs`（10 项，`GlOffscreenSurface` 真实 GL framebuffer + `ReadPixels` 逐像素断言 + `SkiaTextService` 真实排版；同 `PreeditRenderingGpuTests` 的 `AssertClosest`/`IsInk`/`IsAccent` 模式）：

| 测试 | 覆盖 |
|---|---|
| `跨行预编辑…逐行绘制下划线且不填行间` ×4 DPI | 单段跨软折行预编辑 → 逐行 `GetRangeRects` 各产一条下划线（>=2 行 Y），每条与该行段界精确一致；行间空隙保持 Surface 色（不 union 填空）；纹理原点在整数物理像素网格。 |
| `多行选区…逐行marker且不跨行填色` ×2 DPI | 跨三行选区 → >=3 个逐行矩形，各行 Y 与排版行界一致。 |
| `已发布caret几何与真实GL绘制像素一致` | `service.LastGeometry.CaretBounds` == 纹理原点 + `GetCaretBounds(9, affinity, 1.5)`；真实像素含 caret 墨迹（回归：发布原点与 Emit 顶部对齐原点一致，不走 ResolveOrigin 居中）。 |
| `编辑器宽度resize重排排版且缓存按wrap宽分键` | UseState 驱动同节点宽度重建 → 重排后 `Layout.Size.Width` 增大、纹理像素宽精确匹配、行数不增；节点同一（`Assert.Same`）。 |
| `嵌套裁剪把多行预编辑与装饰一起截断` | ClipView 边界以下编辑器范围内零强调色/墨迹（真实像素扫描）。 |
| `caret完全裁出时发布Unavailable不留旧锚点` | ScrollView 滚出后 `LastGeometry.HasCaret == false`。 |

### 自测

`dotnet build CSharp.UI.slnx` 0 警 0 错（含 samples）。`TextEditorPreeditRenderingGpuTests` 10/10 绿。

- **首轮全套**（gallery 节新增后、测试维护前）：`CSharp.UI.Tests` 592、`CSharp.UI.Text.Tests` 85、`CSharp.UI.Integration.Tests` 53 全绿；`CSharp.UI.OpenTK.Tests` 674/678——4 项失败见上节归因（目标 TextField 被新节推出视口）。
- **最终全套**（上文测试维护修正后 + good stub 小修冻结）：`dotnet build CSharp.UI.slnx` Debug/Release 均 0 警 0 错；`dotnet exec tests/CSharp.UI.OpenTK.Tests/bin/{Debug,Release}/net10.0/CSharp.UI.OpenTK.Tests.dll` **678/678、exit 0**；`CSharp.UI.Tests` 593/593、`CSharp.UI.Text.Tests` 85/85、`CSharp.UI.Integration.Tests` 53/53 全绿、exit 0（合计 1409×2）。原始完整 stdout/stderr 见交付证据日志 [test-*.log 与 build-*.log](../delivery/2026-10-23-text-editor-delivery-evidence/)(SHA256SUMS 校验)。

### 边界

断言的是几何与可见性不变量（逐行矩形、物理像素网格、颜色家族、裁剪），不是字形位图逐字节或特定 RGB 值——字体渲染随平台微差属环境边界。真实 IMM/TSF 桥到 TextEditor 的真机验收、嵌套 ScrollView wheel 冒泡、外部 `\r\n` 写不串行、分段 Result 逐项历史留待 TE-06 真机阶段（fast 接手 mspinyin 驱动）。
