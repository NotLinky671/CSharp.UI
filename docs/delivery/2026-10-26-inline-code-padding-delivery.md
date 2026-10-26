# 行内代码真实排版内边距 —— 交付记录

日期：2026-10-26
计划：[docs/plan/2026-10-26-inline-code-padding-plan.md](../plan/2026-10-26-inline-code-padding-plan.md)
评审：[docs/return/2026-10-26-inline-code-padding-review.md](../return/2026-10-26-inline-code-padding-review.md)
证据：[docs/return/2026-10-26-inline-code-padding-evidence/](../return/2026-10-26-inline-code-padding-evidence/)
基线：d1f105d（行内 code 圆角 + accent 主题前景，已推送）

## 用户可见效果

- 行内 `` `code` `` 渲染为带真实内边距的盒：背景矩形左右/上下各扩
  `InlineCodePaddingHorizontal`（默认 **5**）/`InlineCodePaddingVertical`（默认 **2**）
  ——padding 是真实 advance / 行高，不是把背景矩形外扩。默认外观为参考效果：
  主题感知 accent 前景 + 中性半透明灰底 + 圆角 4。
- `MarkdownStyleOptions` 可调：H/V 任意值、`0` → 紧凑（无内边距）；
  `CodeBackground = null` → 盒几何保留、只不画背景（CSS：background 与 padding 正交）。
- 普通相邻文字不被 pill 覆盖——左 pad 是真实 advance 间隙，非叠加绘制。
- 折行：同一 code 跨行各片段独立成盒、各自带完整左右 pad，盒宽不越 MaxWidth
  （单字形盒装不下时硬断，边界情形）。
- 同一逻辑 `TextStyleRange` 的 face/script fallback 子段（如 `中Subagent文`）
  背景合并为一个无缝 pill；相邻独立 code range 仍是各自 pill。
- 左/右 pad 条带可点击、可悬停（`HitLink` 与 Emit pill 同盒几何）。
- 代码块圆角底与 Copy 工具行不变（`ShowCodeCopyButton`/`CodeCopyLabel`）。

## API

- `TextStyle` + `InlineCodePaddingHorizontal`/`InlineCodePaddingVertical`
  （`double?`，null=0）——**几何字段**，进 `StyledTextFontSignature`/GeometryKey：
  同拓扑仅 padding 不同 → 重排版；非 paint-only。
- `RichTextSegment` + `InsetLeft`/`InsetRight`（默认 0）——段盒宽分解：
  `X` 为盒原点起算的字形 X，`Width` 已含右 pad，pill = `[X − InsetLeft, X + Width)`。
- `MarkdownStyleOptions` + `InlineCodePaddingHorizontal`(5)/`InlineCodePaddingVertical`(2)；
  `InlineAdapter.ResolvedStyle`/`Overlay`/`ToTextStyle` 透传。

## 内部实现要点

- `RichTextLayoutBuilder`：`insidePad`/`cumPadAt` 盒宽表（无 padding → null，零分配；
  差分写入后原地转前缀和）。`ApplyPadShift` 按行累计片段 pad 并入 `xAt` 与
  glyph points，行共享 boundary 的上下行片段 pad 逐行拆开。
- wrap：`MeasuredWidth` 按盒宽预算（行首续片段左 pad + 行内 range 事件 +
  行尾片段右 pad）。
- Emit：合并键 = 逻辑盒身份（覆盖段的 padded range 下标）；无 padding 段按
  原 inset 等值合并。
- `StubTextService.BuildRich` 同几何契约——单元测试与真实 Skia 结果一致。

## 验收

- **fast run4：172 PASS / 0 FAIL（exit 0）**，真实 Skia + offscreen GL：
  左 pad 命中 FAIL→PASS、右 pad/second link/gap 正确；`'中Subagent文'`
  3 段同盒合并单 Rect（page seam=0）；盒宽差 +10；wrap 各 fragment
  IL/IR=5 且不越 MaxWidth；圆角 cache identity 相同、raster 无重算。
- 主审实图验收：light/dark @ps1.5 默认 H5/V2 参考效果 + 单完整 pill 接受。
- **Release 封口**：build 0 警告 0 错误（exit 0）；`CSharp.UI.Tests` 711/0、
  `Text.Tests` 112/0、`Markdown.Tests` 50/0、`Integration.Tests` 56/0——
  四套件实际运行全绿，evidence `logs/run5-test-*.txt`。

## 边界

- 平台：Windows x64 offscreen GL（非 OS 物理鼠标）；OS 剪贴板未触碰
  （copy 走 stub 边界）；macOS/Linux 未实跑，仅承诺同一源码路径设计兼容。
- `InlineCodePaddingHorizontal/Vertical` 为几何字段——改值必触发重排版。
