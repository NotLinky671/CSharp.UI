# TextEditor 滚动回跳修复计划

计划日期：2026-10-23

状态：已实现并验收通过——fast 独立真实窗口验证 8/8 PASS(inner 到顶后外层滚动不回弹、键盘 ensure、宽度 resize ensure,证据 [2026-10-23-text-editor-scroll-bounce-evidence](../return/2026-10-23-text-editor-scroll-bounce-evidence/));Debug/Release 回归 594/85/53 + OTK 目标 192 全绿,复审见 [scroll-bounce-review](../return/2026-10-23-text-editor-scroll-bounce-review.md)。主审已批准修复与验收并授权提交。本文为修复实施后的记录性计划(非实施前预测),事实以代码与修复记录为准。

关联：[修复记录](../return/2026-10-23-texteditor-scroll-bounce-repair.md)、[文本编辑器计划](2026-10-23-text-editor-plan.md)。

## 用户报告与根因

用户报告：「当行数超过 TextEditor 的 height 时，滚动到最上面之后会突然回到最后一行。」

根因：`TextEditor.Arrange` 在 `node.IsFocused` 时对**每次**排列无条件 `EnsureCaretVisible`。真实触发链：

1. 编辑器内滚轮向上滚到 `ScrollY=0`（编辑器自消费，caret 留在文档末行）；
2. 继续滚轮在边界返回 `false` → 冒泡到外层 `ScrollView` → `ScrollOffsetY` 变化 → `InvalidateLayout`；
3. 父级重排把编辑器节点**纯位置平移**（内容区尺寸/字体/DPI/padding 均未变）→ `TextEditor.Arrange` 重跑；
4. 无条件 ensure 按 caret（末行）把 `ScrollY` 拉回 `maxScroll`（实测 0 → 647.5），用户看到"回到最后一行"。

既有 `UserWheelScroll_IsNotBouncedBackByNextFrame` 只覆盖单控件纯重绘帧（无外层冒泡/父级重排），未覆盖该触发链。

## 最小决策

在 `Arrange` 内对 caret 可见性判据建几何键 `EnsureGeometryKey(InnerSize, Font, PixelScale, HorizontalPadding, VerticalPadding)`（值类型 record struct，比较零分配）：

- 键覆盖 caret 行位置与可见带的全部决定量：内容区宽高（reflow/视口高）、字体、DPI、padding；
- **绝对位置不入键**：外层滚动平移、同 bounds 重建重排不再触发 ensure；
- 仅在 `previous != key`（非空旧键）时 ensure；**首次记录（prev==null）不 ensure**——焦点获得已由 `OnFocusChanged` 收敛，不需要框架级"准备"语义；键只在焦点分支更新，失焦排列不写入（非 blur-reset，仅聚焦窗口内判据）；
- 显式 ensure 路径全部保留：键盘导航/编辑/指针/焦点/ApplyEdit/撤销重做/`SetCaret`/`OnFocusChanged`/组合期发布；
- 宽度 reflow、窗口缩放、DPI/字体变化仍 ensure（键不同）；同 frame 下字体/DPI 变化由 Font/PixelScale 维度覆盖。

## 边界

- 不引入通用滚动管理、重试、telemetry；不新增公开 API；
- `TextField` 无垂直滚动，不受影响；
- "用户滚允许 caret 离视口"契约不变，guide 不更新；
- 嵌套 ScrollView 双向冒泡、组合期 IME 锚点路径不变。

## 验收状态

- 回归测试 `OuterScroll_BubbledWheel_DoesNotBounceScrollYBackToCaret`（同文件惯例 `TestApp` + `StubTextService`）：
  fails-before（恢复无条件 ensure）FAIL `Expected 0 / Actual 647.5`；passes-after 通过；
  覆盖外层真实冒泡滚动（断言 `ScrollOffsetY` 下降）、重复滚轮/时钟/重绘/同 bounds 失效重排不回拉、显式键盘导航仍 ensure。
- 自测：`CSharp.UI.Tests` 594/594、`CSharp.UI.Text.Tests` 85/85、`CSharp.UI.Integration.Tests` 53/53、`CSharp.UI.OpenTK.Tests` 678/678，`dotnet build CSharp.UI.slnx` 0 警 0 错。
- **完成**:fast 独立真实窗口验收通过(exit=0,8/8 CHECK;inner 0 保持、outer 真实 360→0、键盘 ensure 969.266、宽度 resize 再 ensure 969.266;原始 log 与 PNG 见证据目录),主审已批准并授权提交。
