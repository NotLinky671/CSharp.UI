# TE-02：IME 客户端泛化与桥单行假设审计

创建日期：2026-10-23

状态：未开始。设计依据 [TextEditor 计划](../plan/2026-10-23-text-editor-plan.md) §3.3。

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
