# TE-01：编辑排版模式与多行净化契约

创建日期：2026-10-23

状态：未开始。设计依据 [TextEditor 计划](../plan/2026-10-23-text-editor-plan.md) §3.1、§3.2、§3.5。

[返回任务索引](README.md)

## 目标

在 `TextLayoutRequest`/`TextLayoutResult` 上落地“编辑排版模式”契约，并提供多行文本归一化入口；`Text`/`TextField` 显示路径零变化。

## 文件范围

- `src/CSharp.UI/Text/TextContracts.cs`：`TextLayoutRequest.Editing`、`CaretPosition`、`TextAffinity`、`CaretsByLine`、新查询 API、`GetRangeRects`。
- `src/CSharp.UI.Text/TextLayoutBuilder.cs`：尾随空段、行内 caret 表（共享 offset 双坐标）、行定向命中、行首/行尾、逐行选区矩形。
- `src/CSharp.UI/Input/InputEvents.cs`：`TextInputEvent.SanitizeMultiLine`。
- `src/CSharp.UI/Editing/TextInputContracts.cs`：`EditOperation` 归一化入口——`TryCreate` 增加 `normalizeLineEndings` 参数（与 `scrubSingleLine` 并列的显式开关），调用方传入真实文档 offset 的 `Range` 与未归一化插入串，内部先归一化再构造；`Range` 不被归一化长度回裁。
- `tests/CSharp.UI.Text.Tests/` + `tests/CSharp.UI.Tests/`：本任务全部新契约的单测。

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
   `LineEndIndex` 硬换行行返回 terminator 前 caret stop；terminator 后 offset 属下行 `LineStart`。`GetRangeRects`：行间无中间空白填充；空行选中给该行全行高零宽 rect。
5. `TextInputEvent.SanitizeMultiLine(string)`：`\r\n`→`\n`、孤 `\r`→`\n`、去 `\t`；`SanitizeSingleLine` 不动。
6. `EditOperation` 归一化入口：`Range` 用真实文档 offset，`Replacement` 用归一化文本；**不用归一化长度回裁原 range**。
7. 排版/纹理缓存键含 `Editing`+wrap 宽+`LineHeight`（TextEditor 自有键类型，不改 `TextTextureKey` record 形状）。

## 实现边界

- 不复制第二套 shaper/Unicode 分段：复用 `Graphemes`/`FontRunSplitter`/`BuildLines`，只在 caret 表与空段上扩展。
- 不改 `Text`/`TextField` 任何行为（它们不传 `Editing`）。
- 不新增 `ScrollX`/`MaxVisibleLines`/`WrapText` 等配置。

## 验收（可观察）

- `StubTextService` 可构造多行 `TextLayoutResult` 并走全部新 API（无需真实字体）。
- 钉死三类用例：`"a\n"` 末尾空段产生零宽行且 caret 可停放；wrap 边界共享 offset 在 `CaretsByLine` 有上行末/下行首两个 `CaretPosition`；`"a \n"` 行尾空白 caret X > 内容宽（`TrimEnd` 不影响 caret）。
- `GetRangeRects("a\nbcd"[0..4])` 返回两行矩形，行间无填充；空行选中给零宽全高 rect。
- `SanitizeMultiLine("a\r\nb\rc\td")`→`"a\nb\ncd"`；`TryCreate` 归一化后 `Range` 仍是原文档 offset。
- `CSharp.UI.Text.Tests`+核心契约测试全绿；`Text`/`TextField` 既有排版断言（`PrimitiveTests`/`GraphemeMappingTests`/集成排版）全绿。
