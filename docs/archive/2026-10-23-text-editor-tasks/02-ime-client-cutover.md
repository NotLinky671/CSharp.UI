# TE-02：IME 客户端泛化与桥单行假设审计

创建日期：2026-10-23

状态：已完成（2026-10-23 主审核准；独立复审记录见 [2026-10-23-te02-ime-client-cutover-review.md](../../return/2026-10-23-te02-ime-client-cutover-review.md)：22/22 场景 smoke + 234/179 定向回归；最终复验计入[交付证据](../../delivery/2026-10-23-text-editor-delivery-evidence/)）。设计依据 [TextEditor 计划](../../plan/2026-10-23-text-editor-plan.md) §3.3。

[返回任务索引](README.md)

## 目标

把 IME 组合客户端的宿主面从 `TextFieldState`/`TextField` 解耦为内部窄接口 `ITextEditSessionOwner`，`TextInputServiceSession`/`UiRuntime` 跟随泛化；完成 IMM 桥单行假设源码审计。`TextField` 的 IME/编辑语义零变化。

## 文件范围

- `src/CSharp.UI/Editing/ITextEditSessionOwner.cs`（新）：内部窄接口。
- `src/CSharp.UI/Editing/TextFieldCompositionClient.cs`：构造签名与内部访问面改为接口。
- `src/CSharp.UI/Editing/TextInputServiceSession.cs`：形参类型泛化。
- `src/CSharp.UI/Editing/CompositionCharGeometry.cs`：`CompositionCharGeometrySnapshot` 增 `ScrollY`。
- `src/CSharp.UI/Runtime/UiRuntime.cs`：三处 `is TextFieldState`→接口。
- `src/CSharp.UI/Views/TextField.cs`：`TextFieldState : ITextEditSessionOwner`；`TextField` 提供接口所需回调实现（仅把现有私有成员接到接口，不改行为）。
- `src/CSharp.UI.OpenTK/Hosting/{WindowsImeBridge,ImeEventTranslator}.cs` + `src/CSharp.UI.OpenTK/WindowsTsf/WindowsTextInputRouter.cs`：只审计，仅在发现实际单行 bug 时最小修复并记录。
- `tests/CSharp.UI.Tests/` + `tests/CSharp.UI.OpenTK.Tests/`：泛化后 TextField IME/编辑回归；接口契约测试。

## 契约

1. `ITextEditSessionOwner`（internal，`CSharp.UI`）暴露面按 `TextFieldCompositionClient` 现有调用点冻结，至少含：
   - `TextEditSession Editor`、`TextEditHistory History`、`TextInputServiceSession InputSession`、`object NativeKeyToken`。
   - 组合客户端所需控件回调：原子事务入口（等价 `ApplyEdit`/`ApplyCompositionCommit`）、取消组合后的选区恢复、失效/重绘/布局通知、几何快照刷新、`TrySetCommittedSelection` 对应“设置已提交选区”。
   - 成员清单按调用点逐一列出后写入本任务记录；不写未使用的成员。
2. `TextFieldCompositionClient`：构造改 `(ViewNode, ITextEditSessionOwner, int)`；5 个既有接口实现（`ITextInputClient`/`ICompositionCharGeometrySource`/`ICompositionIdentitySource`/`IGeometryPublishInvalidationSink`/`ITextInputSelectionTarget`）经窄接口对两控件可用；协议、事件顺序、几何快照身份键（epoch/preedit/documentVersion/layoutSerial/scroll）语义不变。
3. `TextInputServiceSession`：`_owner`/`_factory`/`ActiveClient`/`IsCurrentOwner`/`EndForOwner`/`RequestEndForOwner`/`ApplyFromClient`/`PublishGeometry` 形参类型整体替换（类名是否改 `TextCompositionClient` 由实现定，internal 自由）；行为零改动。
4. `UiRuntime`：`NotifyWindowActivation`(:702)、`NotifyCompositionKeyOrigin`(:723)、`ReleaseTextInputOwnershipFor`(:757) 的 `is TextFieldState`→`is ITextEditSessionOwner`，成员经接口（`CancelCompositionForRelease`/`EndInputSession`/`TrackOriginatingKey`/`History.BreakMerge` 上接口）。
5. `CompositionCharGeometrySnapshot` 增 `ScrollY` 身份键；`ScrollX` 保留（TextEditor 恒 0、TextField 真实值，语义兼容）。
6. `ITextInputClient`/`CompositionEvent`/`TextInputGeometry`/`ITextInputService`/`WindowsImeBridge`/`TSF`/`TextInputForwarder` 公开/宿主契约零改动。

## 审计项（结论写入本任务记录）

- `WindowsImeBridge`：`PublishGeometry`（:522-607）消费的 `TextInputGeometry` 矩形是否有单行假设；`SetCandidatePosition`/`SetCandidateExclude`/`SetCompositionWindow`/`IMR_QUERYCHARPOSITION`（:889-940）应答路径是否依赖单行 caret；`ImeEventTranslator` 事件的 `Selection`/`Replacement`/`caret` 是否有单行断言；`WindowsTsf/WindowsTextInputRouter` 路由是否假设单一控件类型。
- 结论如实记录“无显式单行假设/发现 X 处需修”，修复仅限实际 bug。

## 验收

- `TextField` 既有 IME/编辑测试全绿：`TextFieldCompositionTests`、`TextFieldPreeditGeometryTests`、`TextFieldAtomicEditTests`、`TextFieldUndoRedoTests`、`TextInputServiceContractTests`、`TextInputCompositionSessionTests`、`TsfHostIntegrationTests`、`WindowsImeBridgeTests`、`RouterFallbackTests` 等 Debug+Release 全绿。
- `CompositionCharGeometrySnapshot` 身份键含 `ScrollY` 且旧场景（ScrollY=0）行为不变。
- 审计结论记录于本任务文件；泛化后 `UiRuntime` 三处对 `TextEditorState` 同样生效（TE-03 才可运行时验证，本任务以接口编译+TextField 回归为门槛）。

## 实施记录（2026-10-23）

### 接口成员清单（按 `TextFieldCompositionClient` 现有调用点冻结）

| 成员 | 消费方 | 用途 |
|---|---|---|
| `TextEditSession Editor { get; }` | client | 已提交文本/版本读（`Editor.Text`/`Editor.Version`），detached 快照 |
| `TextEditHistory History { get; }` | client + UiRuntime | `BreakMerge()` 批次边界、`Clear()` 历史失效（异常/外部变化） |
| `TextInputServiceSession InputSession { get; }` | client | `ApplyFromClient`/`RequestNativeCancel`/`IsCurrentOwner`/`PublishGeometry` 回路由 |
| `object NativeKeyToken { get; }` | client + UiRuntime | TI-07 归属接缝 token（`TrackOriginatingKey`/`IsExplicitlyConsumedKey`） |
| `int Caret { get; set; }`、`int Anchor { get; set; }` | client | `TrySetCommittedSelection`/`ApplyRestoredSelection` 选区写，快照 caret 校验读 |
| `double ScrollX { get; }`、`double ScrollY { get; }` | client | 字符几何快照身份键校验（TextField `ScrollY` 恒 0） |
| `bool SyncCompositionDocument(ViewNode)` | client | 事件前真实绑定同步；`out invalidated` 改为返回值 |
| `EditResult ApplyCompositionCommit(ViewNode, TextRange, string)` | client | 组合结果的真实原子事务入口（原 `TextField.ApplyCompositionCommit`） |
| `int MaxLength(ViewNode)`、`object TextIdentity(ViewNode)` | client | `CompositionDocument` 上下文（原 `TextField.MaxLength`/`Text.Identity`） |
| `bool IsTextEditNode(ViewNode)` | client + session | 替代 `ResolveField`/`RearmFor` 的 `node.View is TextField`（挂载+类型，不含禁用） |
| `bool IsEditableNode(ViewNode)` | client | 替代 `IsSelectionTargetValid` 的 `IsMounted && !IsEffectivelyDisabled && View is TextField` |
| `bool EndInputSession()`、`void CancelCompositionForRelease()`、`bool TrackOriginatingKey(object, bool)` | UiRuntime | 3 处 `is TextFieldState` 泛化所需成员（既有实现直接暴露） |

### 变更

- `TextFieldCompositionClient.cs` → `TextCompositionClient.cs`（`git mv`，internal 改名，契约允许）；构造 `(ViewNode, ITextEditSessionOwner, int)`；`ResolveField` 删除，`CurrentDocument`/`CurrentCompositionDocument`/`CommitResult`/`IsSelectionTargetValid` 改经 owner；快照校验补 `ScrollY`。
- `TextInputServiceSession`：全部形参 `TextFieldCompositionClient`→`TextCompositionClient`；`RearmFor` 的 `node.View is TextField` 改为 `InteractionState is ITextEditSessionOwner && owner.IsTextEditNode(node)`。
- `UiRuntime`：`NotifyWindowActivation`(:702)、`NotifyCompositionKeyOrigin`(:726)、`ReleaseTextInputOwnershipFor`(:760) 三处 `is TextFieldState`→`is ITextEditSessionOwner`；另 `InputRouter.ResumeTextInput`(:111) 的 `View is TextField` 门改为 `InteractionState is ITextEditSessionOwner && owner.IsEditableNode(focused)`（否则窗口重激活不恢复 TextEditor 会话——TE-03 静默拒）。
- `TextField`：`TextFieldState : ITextEditSessionOwner`，新增 `ScrollY=>0`、`SyncCompositionDocument`/`ApplyCompositionCommit`/`MaxLength`/`TextIdentity`/`IsTextEditNode`/`IsEditableNode` 委托（仅接线，不改行为）；快照构造补 `ScrollY = state.ScrollY`。
- `CompositionCharGeometrySnapshot`：新增 `required double ScrollY` 身份键。

### IMM/TSF 桥单行假设审计结论

**无显式单行假设，无需修复。** `WindowsImeBridge.PublishGeometry`(:522-607) 只消费 `TextInputGeometry` 的 caret/composition/exclusion 矩形并换算客户区，矩形本身不含行数假设；`IMR_CANDIDATEWINDOW`/`IMR_COMPOSITIONWINDOW`(:899-934) 应答 `_lastAnchorClient` 单点，与行数无关；`IMR_QUERYCHARPOSITION`(:889-891, :950+) 完全经 `CompositionCharGeometrySnapshot.TryResolveCharPosition` 应答。`ImeEventTranslator` 的 `Selection`/`Replacement`/`caret` 全是文档 UTF-16 偏移，无行索引/单行断言。`WindowsTextInputRouter` 与 `ImeEventTranslator` 全文 grep 无任何 `TextField`/视图类型引用，路由只面向 `ITextInputClient` 抽象，不绑定具体控件类型。**收敛说明**：`TryResolveCharPosition` 现仍调用 `Layout.GetLineForIndex`+`GetCaretX`（旧单行语义的查询方法）；TE-01 新增的 `GetCaretBounds(index, affinity)`/`HitTestLine`/`LineIndexForIndex` 等 affinity 消歧 API 尚未被快照消费——多行下组合字符 offset/硬换行/软 wrap 共享边界/ScrollY 的准确应答是 **TE-04 待校对项**（届时按 `Editing`+affinity 新查询改造快照解析），不因"桥无单行假设"即宣称快照多行就绪，也不视作 TE-04 已实现。

### 自测

Debug+Release 全绿（编译 0 警告/0 错误）：`CSharp.UI.Tests` 561/561、`CSharp.UI.OpenTK.Tests` 668/668、`CSharp.UI.Integration.Tests` 53/53、`CSharp.UI.Text.Tests` 81/81。真实 UiRuntime+TextField 组合 smoke（start/update/result/stale 拒）由既有 `TextFieldCompositionTests`（66 项，真实 `TestApp`→`UiRuntime`→`SetFocus`→`FakeTextInputService`→真实客户端）与 `TextInputServiceContractTests`（18 项）覆盖，泛化后全部通过——验证 `Start`/`EndForOwner`/`ApplyFromClient`/`IsTextEditNode`/`IsEditableNode`/`SyncCompositionDocument`/`ApplyCompositionCommit`/`TrySetCommittedSelection` 全链路。`TE-03` 的 `TextEditorState` 实现本接口即可复用同一客户端，无需改动会话/运行时。
