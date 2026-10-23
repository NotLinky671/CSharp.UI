# TE-06：图形集成与 Windows 真实验收

创建日期：2026-10-23

状态：未开始。设计依据 [TextEditor 计划](../plan/2026-10-23-text-editor-plan.md) §3.2、§3.4、§3.6、§5。

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
