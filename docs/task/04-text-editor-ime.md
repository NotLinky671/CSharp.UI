# TE-04：TextEditor IME 接入

创建日期：2026-10-23

状态：未开始。设计依据 [TextEditor 计划](../plan/2026-10-23-text-editor-plan.md) §3.3、§3.4、§3.5、§3.7。

[返回任务索引](README.md)

## 目标

`TextEditor` 经 `ITextEditSessionOwner` 接入既有 `ITextInputService` 协议：组合客户端、几何发布（含 ScrollY/affinity/逐字符快照）、跨行预编辑显示串与装饰。IMM 桥/TSF 桥/服务契约零改动；**桥协议零改动但客户端泛化与二维几何是必需工作**（不是“桥自动获得”）。

## 文件范围

- `src/CSharp.UI/Views/TextEditor.cs`：`OnFocusChanged` 建会话、`Prepare`/`PublishTextInputGeometry`、组合显示串排版缓存、预编辑装饰绘制、组合 caret。
- `src/CSharp.UI/Editing/TextFieldCompositionClient.cs`：`ITextEditSessionOwner` 适配收尾（TE-02 已改签名，本任务接 TextEditor 调用点）。
- `src/CSharp.UI/Editing/TextInputServiceSession.cs`：如 TE-02 有遗漏调用点，仅补。
- `tests/CSharp.UI.Tests/` + `tests/CSharp.UI.Integration.Tests/`：TextEditor IME 核心/集成测试；TextField IME 回归。

## 契约

1. **会话**：`OnFocusChanged(focused)`→`state.InputSession.Start(node, epoch => state.RequireComposing(node, epoch))`；`!focused`→`CancelCompositionForHostAction`+`EndInputSession`+折叠选区。同 TE-02 后 `TextField` 同协议。
2. **显示串**：组合期间 `displayText = committedPrefix + preedit + committedSuffix`（`Replacement` 锚定当前真实绑定文档版本）；文档排版缓存与显示排版缓存严格分离（各含 `Editing=true` 键）。
3. **几何**：`PublishTextInputGeometry` 在最终布局后输出 `TextInputGeometry`——caret 矩形（`GetCaretBounds(displayOffset, affinity)`）+ 组合矩形（预编辑段 `GetRangeRects` 首行/跨行 union）+ 排除区；坐标=Frame 原点+文本原点−(0,ScrollY)，经祖先裁剪链；完全裁出→`Unavailable`。
4. **逐字符快照**：`CompositionCharGeometrySnapshot` 含 `ScrollY`（TE-02 已加）+ `Origin`/`EditableClip`/`Epoch`/`DocumentVersion`/`LayoutSerial`/`Preedit`；身份键不匹配即拒答。
5. **预编辑装饰**：分段下划线逐行绘制（`GetRangeRects` 逐段 rect）；组合 caret 用 affinity 消歧位置；活动段按组合 caret 所在段加粗。
6. **取消接缝**：切焦/失活/卸载/禁用/外部文本来源 MaxLength 变化/Undo/Redo/Cut/Paste/SelectAll/鼠标改选区→先取消；复制不取消；同节点同绑定/纯重布局不取消。
7. **归一化映射**：IME Result 的 `Range` 用真实文档快照 offset；`Replacement` 归一化；不用归一化长度回裁原 range。分段 Result 逐项原子事务+历史；取消不写绑定不推版本。
8. **非 Windows**：`TextInputCapabilities` 声明 committed-only（`Composition`/`CandidateAnchor` 恒 false），TextEditor 仍走已提交文本通道可用。

## 验收

- 核心/集成测试：`TextEditor` 组合显示串逐行正确；跨行 Replacement 的 Result 精确落地（含 `\n` 替换区）；组合 caret/分段下划线跨行；几何发布含 ScrollY 与 affinity；`Snapshot` 身份键含 ScrollY。
- 组合期间 Enter/方向/空格/Backspace/Esc 归输入法；非组合 Enter 插 `\n`；外部绑定写 `\r\n` 原文档后组合回调读真实快照不串行。
- `TextField` IME 回归全绿（同 TE-02 门槛）。
- 非 Windows committed-text 通道多行可用。
