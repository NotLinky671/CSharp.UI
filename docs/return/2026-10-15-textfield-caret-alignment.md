# R12：TextField 光标与文字上下位置不一致

记录日期：2026-10-15  
审核人：Codex  
用户确认的现象：输入框光标上下位置偏移。  
状态：已复现，待 DeepSeek Harness 修复与 Codex 复审。  
指定执行模型：`opencode-go/deepseek-flash`。

## 问题与证据

- 优先级：P2。
- 位置：[TextField.cs](../../src/CSharp.UI/Views/TextField.cs) 的 `Emit()`、`IndexFromPosition()`；[Controls.cs](../../src/CSharp.UI/Views/Controls.cs) 的 `TextPainter.Paint()`。
- 原因：文本纹理在输入内容区内垂直居中，而光标与选择背景直接以 `inner.Y` 为原点，遗漏 `(inner.Height - layout.Size.Height) / 2`。输入框变高或字体改变后偏差随之变化。

使用当前核心运行时、`StubTextService`、`RecordingTextureService` 和可控时钟，生成实际绘制命令，聚焦输入框并比较文本纹理与光标的纵坐标：

```text
height=32: textY=7.25, caretY=2, verticalOffset=5.25
height=48: textY=15.25, caretY=2, verticalOffset=13.25
```

复现程序位于本地忽略的 `artifacts/caret-review/`。它不是项目测试的一部分，返修需新增可提交的回归测试。

## 修复范围

1. 让文本绘制、光标、选择高亮和鼠标定位使用一致的文本坐标原点；保留现有垂直居中外观。
2. 保持横向滚动偏移和逻辑像素/DPI 换算正确，不用固定像素补偿或仅改变默认高度掩盖问题。
3. 覆盖空文本、普通中英文、不同字体大小、不同控件高度、选择及横向滚动后的情况；高度不足时应保持统一原点并由现有裁剪处理。
4. 保留 R01—R11 的修复，不扩大到完整 IME 或无关的文本排版重构。

## 回归验收

- [ ] 默认高度 32 和较高输入框 48 中，光标与文本行使用相同纵向原点。
- [ ] 选择背景与文本行纵向对齐，而不是粘在输入框顶部。
- [ ] 空文本与 placeholder、不同字体大小、DPI 1 / 1.5 / 2、横向滚动后均验证相同坐标约定。
- [ ] 使用绘制命令或真实排版结果断言几何关系，避免只测试编辑状态或复制实现公式。
- [ ] 现有输入、选择、剪贴板替身、光标闪烁、节点复用及长度限制回归测试通过。
- [ ] 增加至少一个真实 Skia 文本排版与 TextField 组合的集成用例，确认替身与实际文字高度下都正确。

## 交付要求

- DeepSeek Harness 读取 `AGENTS.md`、`README.md` 后，完成实现和回归测试。
- 串行构建与运行测试，避免 DLL 文件锁；构建使用 `-m:1 -nodeReuse:false`。
- 四个测试工程都需要验证；系统剪贴板若因环境失败如实记录，不能跳过或放宽断言。
- 把修复说明、测试名称及实际结果追加到本文。代码完全通过后按项目要求提交，提交信息使用英文。
- 只暂存本次修复的代码、测试与本文；保留既有的 `docs/agents-notebook/README.md` 和 `docs/agents-notebook/acp/` 工作区变更，不替用户提交这些文件。

## R12 返修记录（DeepSeek Harness，opencode-go/deepseek-flash）

### 根因

`TextField.Emit()` 把文本纹理交给 `TextPainter.Paint()` 绘制，而 `Paint()` 会在传入矩形内做垂直居中：
文本顶边 = `bounds.Y + (bounds.Height - layout.Size.Height) / 2`。TextField 传入的是 `inner`（内容区），
因此文字纹理实际落在内容区垂直中心；但同一处的选择高亮、光标与 `IndexFromPosition()` 命中仍以 `inner.Y` 为原点，
遗漏了 `(inner.Height - layout.Size.Height) / 2` 这一项。输入框越高或字号越大，偏差越大。

复现程序实测（`StubTextService`，字号 14，行高 17.5）：高度 32 偏差 5.25，高度 48 偏差 13.25。

### 修复

统一“文本原点”，不做固定像素补偿：

1. `src/CSharp.UI/Views/Controls.cs`：新增 `TextPainter.VerticalOrigin(Rect bounds, Size textSize)`
   （返回文本块在矩形内垂直居中后的顶边 Y），并让 `TextPainter.Paint()` 改用它计算纵向位置。
   其他控件（Text/Button/Toggle）传入 `bounds` 后计算结果与原先完全一致，外观不变。
2. `src/CSharp.UI/Views/TextField.cs`：
   - `Emit()` 用 `TextPainter.VerticalOrigin(inner, layout.Size)` 得到唯一 `originY`；文本纹理、选择高亮、光标全部从该原点出发，
     文本纹理改为传入 `originY` 起、高度为 `layout.Size.Height` 的矩形（`Paint` 的居中偏移因此为 0，纹理与光标坐标由构造保证一致）。
   - `IndexFromPosition()` 的局部坐标纵向同样减去 `TextPainter.VerticalOrigin(inner, layout.Size)`。
   - 保留垂直居中外观、横向滚动偏移 `ScrollX`、逻辑像素/DPI 换算与 `PushClip(inner)` 裁剪；高度不足时偏移为负，仍由裁剪处理。

### 新增测试

核心回归 `tests/CSharp.UI.Tests/TextFieldCaretAlignmentRegressionTests.cs`（绘制命令断言几何关系，不复制实现公式）：

- `光标与文字纹理共享纵向原点且保持垂直居中(24/32/48/64 × 字号 14/20/24)`（Theory 7 组）
- `选择高亮与文字行纵向对齐`
- `空文本时光标仍使用居中的文本原点`
- `空文本与placeholder共享同一纵向中心`
- `不同DPI下几何关系一致且纹理按缩放栅格化(1 / 1.5 / 2)`
- `横向滚动后光标与文字仍共享纵向原点`
- `点击命中使用同一文本原点定位字素边界`

真实 Skia 集成 `tests/CSharp.UI.Integration.Tests/TextFieldCaretAlignmentTests.cs`
（`SkiaTextService` + `RecordingTextureService` + 核心运行时，无窗口）：

- `真实排版下光标与文字纹理共享纵向中心(32/48/64/40 × 字号 14/18/22)`
- `真实排版下选择高亮与文字行纵向对齐`
- `真实排版下空文本与placeholder使用居中文本原点`
- `真实排版下不同DPI保持同一几何约定(1 / 1.5 / 2)`
- `真实排版下横向滚动后光标仍与文字共享纵向原点`

### 实际结果

环境：.NET 10.0.11 / Windows；构建与测试串行（`-m:1 -nodeReuse:false`），未出现 DLL 文件锁。

```bash
dotnet build CSharp.UI.slnx -m:1 -nodeReuse:false   # 已成功生成，0 个错误
dotnet run --project tests/CSharp.UI.Tests --no-build
dotnet run --project tests/CSharp.UI.Text.Tests --no-build
dotnet run --project tests/CSharp.UI.Integration.Tests --no-build
dotnet run --project tests/CSharp.UI.OpenTK.Tests --no-build
```

| 测试工程 | 通过 | 失败 | 跳过 |
|---|---|---|---|
| CSharp.UI.Tests | 138 | 0 | 0 |
| CSharp.UI.Text.Tests | 58 | 0 | 0 |
| CSharp.UI.Integration.Tests | 13 | 0 | 0 |
| CSharp.UI.OpenTK.Tests | 93 | 0 | 0 |

- 未修改任何既有断言；R01—R11 回归全部保持通过。
- 系统剪贴板：`CSharp.UI.OpenTK.Tests` 的 GLFW 剪贴板往返用例已通过（真实写入并读回成功），本次没有剪贴板环境失败需要记录。
- 非空洞性验证：临时把 `TextField.cs` 的文本原点改回 `inner.Y`（并恢复旧纹理矩形）后重建，核心测试 15 个新用例中 14 个失败
  （唯一不涉及纵向断言的是点击命中用例），证明新增断言确实锁定该缺陷；随后已恢复修复代码并复测全绿。
- 本地复现程序 `artifacts/caret-review`（不提交）在修复后输出：
  `height=32: textY=7.25, caretY=7.25, verticalOffset=0`、`height=48: textY=15.25, caretY=15.25, verticalOffset=0`，
  文本纵向位置与修复前一致，说明垂直居中外观未被改变。

