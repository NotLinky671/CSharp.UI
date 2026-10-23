# TE-01：编辑排版模式与多行净化契约

创建日期：2026-10-23

状态：已完成（2026-10-23 主审核准；R1–R7 返修全部关闭，见[返修复审记录](../return/2026-10-23-te01-editing-layout-review.md)）。设计依据 [TextEditor 计划](../plan/2026-10-23-text-editor-plan.md) §3.1、§3.2、§3.5。

[返回任务索引](README.md)

## 目标

在 `TextLayoutRequest`/`TextLayoutResult` 上落地“编辑排版模式”契约，并提供多行文本归一化入口；`Text`/`TextField` 显示路径零变化。

## 文件范围

- `src/CSharp.UI/Text/TextContracts.cs`：`TextLayoutRequest.Editing`、`CaretPosition`、`TextAffinity`、`CaretsByLine`、新查询 API、`GetRangeRects`。
- `src/CSharp.UI.Text/TextLayoutBuilder.cs`：尾随空段、行内 caret 表（共享 offset 双坐标）、行定向命中、行首/行尾、逐行选区矩形。
- `src/CSharp.UI/Input/InputEvents.cs`：`TextInputEvent.SanitizeMultiLine`。
- `src/CSharp.UI/Editing/TextInputContracts.cs`：`EditOperation` 归一化入口——`TryCreate` 增加 `normalizeLineEndings` 参数（与 `scrubSingleLine` 并列的显式开关），调用方传入真实文档 offset 的 `Range` 与未归一化插入串，内部先归一化再构造；`Range` 不被归一化长度回裁。
- `tests/CSharp.UI.Text.Tests/TextEditorEditingLayoutTests.cs`：真实 Skia 排版行为测试（尾随空段、软换行双坐标、行尾空白、CRLF、跨行 selection clip）。
- `tests/CSharp.UI.Tests/TextEditorEditingLayoutTests.cs`：核心契约测试（值模型边界、selection 查询、EditOperation 边界、SanitizeMultiLine）。

## 契约（实现规格，不留选项）

1. `TextLayoutRequest.Editing`：`public bool Editing { get; init; }`，默认 `false`。`Editing=false` 路径行为与现有完全一致（`TrimEnd` 夹 caret、共享 offset 归上行、Trim/Ellipsis/MaxLines 语义不变）。`Editing=true`：
   - `SplitParagraphs` 末尾换行生成尾随空段：`"a\n"`→`[0,2)`+`[2,2)`；空文本仍一行空段。
   - caret X 按未裁剪内容宽度（行尾空白推进）。
   - 调用方保证 `Trimming=None`、`MaxLines=int.MaxValue`；builder 不省略。
2. `CaretPosition`：`public readonly record struct CaretPosition(int Stop, double X, int LineIndex)`；`CaretsByLine`：`public IReadOnlyList<IReadOnlyList<CaretPosition>>`——每行 caret 子表，共享 offset 在两行各产一项真实坐标（上行 `X=行内容宽`，下行 `X=OffsetX`）。
3. `TextAffinity`：`public enum TextAffinity { Unspecified=0, LineEnd=1, LineStart=2 }`（非 [Flags]）。
4. 新查询 API（public，`TextLayoutResult` 上，签名如下）：
   ```csharp
   Rect GetCaretBounds(int index, TextAffinity affinity, double width = 1);
   int HitTestIndex(Point position, out TextAffinity affinity);
   int LineIndexForIndex(int index, TextAffinity affinity);
   int LineStartIndex(int lineIndex);
   int LineEndIndex(int lineIndex);          // 该行换行符前的 caret stop
   int HitTestLine(int lineIndex, double x, out TextAffinity affinity);  // 返回 caret stop 的 UTF-16 offset
   IReadOnlyList<Rect> GetRangeRects(int start, int end);  // 逐行矩形；行末窄 marker 宽由 TextEditor 常量定
   double GetCaretX(int index, TextAffinity affinity);     // affinity 消歧的 X
   ```
   `LineEndIndex` 硬换行行返回 terminator 前 caret stop（Skia 行 `End` 含换行符）；terminator 后 offset 唯一下行 `LineStart`——任何 affinity（含 `Unspecified`/`LineEnd`）都解析到该行真实 X，`LineIndexForStop` 按 `Lines.Start` 真二分（display 语义不变，边界归上行）。`GetCaretX`/`LineIndexForIndex`/`GetCaretBounds` 零分配：行内 `CaretsByLine` 二分查真实 X，非歧义 offset 忽略 affinity。`CaretsByLine` 构造惰性（显式表直入，否则首个访问生成默认表），display 布局不重复付编辑表成本。`GetRangeRects` 三区段独立：内容区 `[line.Start, LineEndIndex]`∩selection 产内容 rect（行内表真实 X clip）；terminator 区 `[LineEndIndex, line.End]`∩selection 且 `text[end-1]` 是换行时独立产零宽 marker（不依赖内容 rect、软换行无 marker）；纯空段被覆盖给零宽全高 marker；行间无填充；end==下一行 Start 不误选；空 range 无 rect。
5. `TextInputEvent.SanitizeMultiLine(string)`：`\r\n`→`\n`、孤 `\r`→`\n`、去 `\t`；`SanitizeSingleLine` 不动。
6. `EditOperation` 归一化入口：`Range` 用真实文档 offset，`Replacement` 用归一化文本；**不用归一化长度回裁原 range**。
7. 排版/纹理缓存键含 `Editing`+wrap 宽+`LineHeight`（TextEditor 自有键类型，不改 `TextTextureKey` record 形状）。

## 实现边界

- 不复制第二套 shaper/Unicode 分段：复用 `Graphemes`/`FontRunSplitter`/`BuildLines`，只在 caret 表与空段上扩展。
- 不改 `Text`/`TextField` 任何行为（它们不传 `Editing`）。
- 不新增 `ScrollX`/`MaxVisibleLines`/`WrapText` 等配置。

## 验收（可观察）

- 真实 Skia（`SkiaTextService`）钉死：`"a\n"` 末尾换行产零宽尾随空行（`Lines` 2 行、尾行 `Start==End`）且 caret 可停放；wrap 折行点共享 offset 在 `CaretsByLine` 有上行末（X=行内容宽右缘）与下行首（X=OffsetX）两个 `CaretPosition`；`"a \n"` 行尾空白 caret X 按未裁剪推进（> 'a' 后位置）；`"ab\r\ncd"` 视作单个硬换行（行 `End` 含 `\r\n`）。
- `GetRangeRects("abcd\nx",0,6)` 第一行 rect 右缘='d' 后真实 X（行内容宽右缘），不取下一行 start X；end==下一行 Start 不误选；选区覆盖硬换行符追加零宽 marker；空行被覆盖给零宽全高 rect；空 range 无 rect。
- `SanitizeMultiLine("a\r\nb\rc\td")`→`"a\nb\ncd"`；`TryCreate(normalizeLineEndings:true)` 归一化后 `Range` 仍是原文档 offset、全过滤插入拒绝。
- `CSharp.UI.Text.Tests`（真实 Skia）+核心契约测试全绿；`Text`/`TextField` 既有排版断言（`PrimitiveTests`/`GraphemeMappingTests`/集成排版）全绿。

### 实施记录补充（2026-10-23，R7 修复）

主审复审 4 指出 `ResolveLineIndex` 的软 wrap 共享 offset 三 affinity 归属不一致（`LineEnd` 的 X 取上行但 `LineIndexForIndex`/`GetCaretBounds` 的 Y/lineIndex 混归下行；`Unspecified` 同混）。已重写 `ResolveLineIndex`（`TextContracts.cs`）为三段判定：`isPostBreak`（`Text[stop-1]` 换行）任何 affinity 唯一下行；`isShared`（软 wrap 共享 `Lines[i-1].End==stop==Lines[i].Start`）`LineStart`→下行/其余（含 `Unspecified`/`LineEnd`）→上行；非共享忽略 affinity 归 `LineIndexForStop` 行。真实 Skia 复现：soft-wrap 共享 offset `aff=Unspecified`/`LineEnd` 现归上行（X=21.85/Y=0/line=0）、`LineStart` 归下行（X=0/Y=18.6/line=1）；`"ab\ncd"` offset 3 全 affinity 唯一下行。证据 `docs/agents-notebook/te01-affinity-repro/softwrap-affinity.txt`；新增真实 Skia 回归 `SoftWrapSharedOffset_AffinityResolvesConsistentLineXAndY`/`HardBreakPostOffset_ResolvesToLowerLineForAllAffinities`（`TextEditorEditingLayoutTests`）。自测：Text.Tests 81/81、core 561/561 全绿。
