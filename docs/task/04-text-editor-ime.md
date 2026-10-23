# TE-04：TextEditor IME 接入

创建日期：2026-10-23

状态：已完成并经主审批准（2026-10-23；独立源码审查 + 独立 29 项 API smoke + TextEditor 41 项核心 / 18 项文本几何定向测试全绿，证据见 ~/.omp/tmp/te04-smoke 输出与主审记录）。设计依据 [TextEditor 计划](../plan/2026-10-23-text-editor-plan.md) §3.3、§3.4、§3.5、§3.7。

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

## 实施记录（2026-10-23）

### 改动清单

| 文件 | 改动 |
|---|---|
| `src/CSharp.UI/Views/TextEditor.cs` | `PrepareTextInputGeometry` + `PublishTextInputGeometry` + `ComputeInputGeometry` + `EffectiveClip` + `Offset`（多行版几何发布，:1735-1901）；`HandleKeyDown` 组合吞咽块（:646-653）；`DrawCompositionDecorations` 段下划线改 `GetRangeRects` 逐行绘制（:342-360）；`EnsureCaretVisible` 组合期间用显示串 caret（:1438-1445）；类注释更正 IME 接缝状态（:17-19）。`PublishTextInputGeometry` 收敛 ScrollY 不做 caret ensure（用户滚允许 caret 离视口；组合期间才 ensure 以锚定候选窗）。 |
| `src/CSharp.UI/Editing/CompositionCharGeometry.cs` | 新增 `internal TextAffinity Affinity`（默认 `Unspecified`，非 required）；`TryResolveCharPosition` 改用 `GetCaretBounds(snapped, affinity)`——预编辑内字符恒 `LineStart`（折行后首字符归下行行首）、无预编辑 charPos=0 用快照 `Affinity` 应答真实 caret。TextField 单行无共享 offset，行为逐位不变。 |
| `src/CSharp.UI/Editing/ITextEditSessionOwner.cs` | 新增 `TextAffinity CaretAffinity { get; }`。 |
| `src/CSharp.UI/Views/TextField.cs` | `TextFieldState.CaretAffinity => Unspecified`（:1909-1911）。 |
| `src/CSharp.UI/Editing/TextCompositionClient.cs` | 快照身份键加 `snapshot.Affinity == _state.CaretAffinity`（:130）。 |
| `docs/task/03-text-editor-core.md` | 更正「Composing 恒 null」误记——TE-03 已接 `InputSession.Start`/`RequireComposing`，TE-04 补的是安全阶段几何发布与快照。 |

### 桥/契约面零改动确认

`WindowsImeBridge`/`TsfInputBridge`/`AcpGeometryProvider` 只经 `ICompositionCharGeometrySource.CompositionCharGeometry` 读快照（`Affinity` 非 required、默认 `Unspecified`，`HostAcpGeometryProviderTests` 旧初始化式不变）。`ITextInputClient`/`ITextInputService`/`CompositionEvent` 公开契约无增改。IMM 默认、TSF 内部开关均未动。

### 已核实的生命周期通路（代码走查）

焦点 `InputSession.Start`→`RequireComposing`；失焦 `CancelCompositionForHostAction`+`EndInputSession`+折叠选区；卸载同序加 `ClearComposing`；禁用经 `RequestDeferredEndInputSession`（安全阶段调平台）；窗口失活 `UiRuntime.NotifyWindowActivation`→`_focus.CancelTextInput`、复活 `ResumeTextInput` 换新 epoch；外部绑定/来源/MaxLength `SyncCompositionDocument`→`InvalidateForExternalChange`；编辑命令（Cut/Paste/SelectAll/Undo/Redo）与鼠标改选区先取消；复制不取消。快照失配键：Epoch/DocumentVersion/LayoutSerial/ScrollX/ScrollY/Affinity/Preedit/CaretIndex。

### 测试

新增 `tests/CSharp.UI.Tests/TextEditorCompositionTests.cs`（11 项，真实 `UiRuntime`+假 `ITextInputService` 驱动真实 client）：跨行 Result 一次事务落地、`\r\n` 仅归一化 replacement、取消恢复选区、组合吞咽非命令键/命令键先取消再执行、stale session 拒、外部写入失效、失焦重聚焦新 epoch 旧 client 拒、ScrollY 快照失配刷新、发布原点与 Emit 顶部对齐原点一致（回归）。新增 `tests/CSharp.UI.Text.Tests/CompositionCharGeometrySnapshotTests.cs`（4 项，真实 `SkiaTextService` 多行排版）：硬换行行首归下行、软折行共享 offset 归下行、预编辑末偏移插入点、无预编辑 charPos=0 按 Affinity 应答 caret。

### 复审修复（2026-10-23 第二轮）

- `ComputeInputGeometry` 原点改与 Emit 相同的 `TextOrigin`（textBounds 左上吸附、顶部对齐）：原实现用 `TextPainter.ResolveOrigin`，内容短于内容区时垂直居中，会把发布锚点 Y 相对绘制位置下推 `(inner.Height − layout.Height)/2`。`PublishGeometry_CaretAnchorMatchesEmitTopAlignedOrigin` 钉住该回归。
- 组合矩形注释更正：对外契约是单个 `Rect`，只能取逐行边界包围盒；逐行不填空的实际绘制在 Emit 段下划线与选区（`GetRangeRects`）。

回归：`CSharp.UI.Tests` 591/591、`CSharp.UI.Text.Tests` 85/85、`CSharp.UI.OpenTK` 编译 0 警。`CSharp.UI.OpenTK.Tests` 因 fast TE-05 在改的 `ControlGallery/Program.cs` 暂不可编译（非本任务文件），以其 TE-04 之前构建的 dll 跑 668/668 绿。

### 边界

- 本轮不接真实 IMM/TSF 桥到 TextEditor 的真机验收——归 TE-06。
- 组合期非命令键一律吞咽（含 Tab），与 TextField 协议一致；公开「组合期 TAB 是否走焦点遍历」未单独定制。
- `EnsureCaretVisible` 组合期 pinned 组合 caret、非组合期只收敛越界 ScrollY；用户在非组合期 wheel 把 caret 滚出视口后，快照按真实 ScrollY 刷新、caret 几何裁出报 `Unavailable`。
