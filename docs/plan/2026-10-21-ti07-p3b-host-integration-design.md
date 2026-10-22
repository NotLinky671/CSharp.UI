# TI-07 P3b 正式宿主集成设计（ActivateEx/UIElement/几何/同帧 overlay）

设计日期：2026-10-21
状态：设计冻结，等待基础模块独立 GPU 验收后再实现；本文件不修改 session/bridge/host 代码。
依据：[正式接入计划](2026-10-21-ti07-production-tsf-integration-plan.md)、[TI-07](../task/07-windows-ime-and-host-routing.md)、隔离原型 R26/R27/R34/R36 的已验收控制流。

## 1. 目标与边界

在真实 OpenTK 宿主的 STA UI 线程上完成：

1. 只调用一次 `ITfThreadMgrEx::ActivateEx(TF_TMAE_UIELEMENTENABLEDONLY)`，并用真实 `GetActiveFlags` 证明 UI-less 能力；失败逆序释放并明确回退。
2. 注册/退役 `ITfUIElementSink`；Begin 只识别类型，Update 才按页读取快照；End/替换/focus/epoch 立即失效。
3. 候选页快照进入 `CandidateOverlay`，在 `UiRuntime.FrameOverlay` 中同帧、内容之后、提交之前绘制；不另开 `BeginFrame`。
4. `TextInputGeometry` 与 `ICompositionCharGeometrySource` 快照映射为 ACP `GetTextExt`/`GetScreenExt` 的只读缓存；原生布局通知只从真实安全阶段发出。
5. 失败路径与能力报告如实：不做双激活、不伪造几何、不保留旧候选。

不在本设计内：候选鼠标选择（未验证前不提供）、TI-08、任何 tools/GDI 依赖。

## 2. 激活与会话（TsfHostSession.cs）

### 2.1 创建顺序（TsfHostSession.TryCreate）

```
lease = StaComApartmentLease.Acquire()            // 已实现
TF_CreateThreadMgr -> ITfThreadMgr
if (threadMgr is not ITfThreadMgrEx ex) -> Fail(E_NOINTERFACE)   // 不调用 Activate
hr = ex.ActivateEx(out clientId, TF_TMAE_UIELEMENTENABLEDONLY)
if (hr < 0) -> Fail(ActivateFailed)
activated = true                                   // 原生成功即拥有激活所有权
flagsHr = ex.GetActiveFlags(out flags)
require flagsHr >= 0
require (flags & TF_TMF_ACTIVATED) != 0
require (flags & TF_TMAE_UIELEMENTENABLEDONLY) != 0
                                                   // 任一不满足 -> Fail(UiElementUnavailable)
CreateDocumentMgr / AcpGeometryProvider / AcpTextStore(provider) / owner / sink
CreateContext / edit sink / 初始权威同步          // 现有顺序
uiElementMgr = threadMgr as ITfUIElementMgr
uiElementSource = uiElementMgr as ITfSource
hr = AdviseSink(TsfIids.UiElementSink, uiElementSink, out cookie)
advised = hr >= 0 && cookie != TF_INVALID_COOKIE (0xFFFFFFFF)   // 0 是合法 cookie，不假设非 0
                                                   // QI/Advise 失败（含无效 cookie）-> Fail(UiElementUnavailable)
session = new TsfHostSession(... provider, uiElementSink, cookie, flags ...)
```

- `ITfThreadMgrEx` 是同一 `ITfThreadMgr` RCW 的托管接口视图：**不** `ReleaseComObject`，`Deactivate` 仍经 `ITfThreadMgr` 调用且只调用一次（`activated` 在 ActivateEx 成功后立即置真，flags 校验失败也由清理链停用）。
- `BeginUIElement` 的 `pbShow` 只在“确属候选列表且自绘通路可用”时写 0；UIElement QI/sink 不可用则整个 TSF 会话失败并回退 IMM，绝不出现“已抑制系统候选但无法自绘”。
- 能力字段（internal 诊断，不扩公开 API）：`ActivationMode`、`ActiveFlags`、`UiElementAdvised`、`CandidatePresentationAvailable`。
- 失败回退在现有 finally 中扩展为逆序：
  `Unadvise UIElement sink -> edit sink unadvise -> sink.Dispose -> owner/store -> parking -> Deactivate(仅 activated) -> Pop -> ReleaseCom(keystroke/threadMgr/context/dm) -> lease -> writer lease`。
  新加的 `uiElementMgr/uiElementSource` 是 threadMgr 的 QI 别名，不单独 ReleaseCom。

### 2.2 会话内新增状态与入口

- `_uiElementMgr`、`_uiElementSource`、`_uiCookie`、`_uiElementSink`（managed CCW）、`_uiElementAdvised`。
- `internal void NotifyLayoutChangedIfPending()`：仅当 `Store.IsSafePhase` 且几何代次未通知时调用 `Store.NotifyLayoutChange()`；不排队、不轮询。
- `internal void InvalidateCandidateGeometry(string reason)`：清 provider/overlay 几何并标记待通知（供 Move/Resize/失焦/epoch 调用）。
- `Dispose` 在 `BlurCore()` 后、`_sink.UnadviseEditSink()` 前后先 `UnadviseSink(cookie)` 并 `uiElementSink.Dispose()`（清 overlay + 释放元素 RCW），再走现有 Deactivate/Pop。

## 3. UIElement sink（新 TsfUiElementSink.cs）

`internal sealed class TsfUiElementSink : ITfUIElementSink, IDisposable`

### 3.1 状态

```
ITfUIElementMgr? _manager;
ITfSource? _source;
uint _cookie; bool _advised; bool _disposed;
int _epoch; Func<bool> _isCurrentOwner;
CandidateOverlay _overlay; Action _requestRedraw;
Dictionary<uint, Entry> _elements; uint _nextGeneration = 1;
uint _currentId; uint _currentGeneration;
```

`Entry`：`Id`、`Generation`、`ITfUIElement? Element`、`ITfCandidateListUIElement? Candidates`、`ICandidateListOperations Ops`（构造时缓存）、`bool Ended`、`int InFlight`、`bool Released`、`CandidateSnapshot? LastSnapshot`。

所有字段仅 UI 线程访问；Entry 的“Ended/InFlight/Released”用于同步 End 重入。

### 3.2 Begin（只识别，不读内容；先退休再 GetUIElement）

```
BeginUIElement(id, ref pbShow):
  incoming = pbShow
  try:
    if disposed/!isCurrentOwner -> return S_OK（不触碰 pbShow）
    life = _lifeSerial
    token = ++_nextSerial; _beginToken[id] = token
    endBefore = _endToken.get(id)
    // 1) 先退休同 id 旧 Entry 并撤销旧快照——必须在可能失败/重入的 GetUIElement 之前。
    if _entries.remove(id, out old):
        old.MarkEnded(); if current -> Publish(null)/clear; ReleaseIfIdle(old)
    // 2) 再取新元素（GetUIElement 可能同步重入 End 或同 id Begin）。
    hr = manager.GetUIElement(id, out element)
    if hr < 0 || element null -> return S_OK（旧快照已清；pbShow 不变）
    if element is not ITfCandidateListUIElement:
        ReleaseOnce(element); return S_OK（pbShow 不变，不抑制非候选）
    // 3) 提交点：本次仍是该 id 最新 Begin、期间无 End、生命周期未失效。
    if !CanUse || life != _lifeSerial || _beginToken[id] != token
       || _endToken.get(id) != endBefore:
        ReleaseOnce(element); return S_OK（不注册、不复活、不写 pbShow）
    _entries[id] = Entry(id, ++_nextGeneration, element, cachedOps)
    pbShow = 0
  catch: pbShow 保持 incoming；return S_OK
```

- 不调用 `GetCount/GetString/GetPageIndex`；内容未就绪不影响 Begin。
- id 0 与其它 id 完全同路径（无 `id == 0` 特殊分支）。
- 不调用 `ITfUIElement::Show`。
- `EndUIElement`/`Clear` 会递增 per-id `_endToken`/全局 `_lifeSerial`，因此“GetUIElement 期间同步 End”或“同 id 新 Begin 先完成”都会使旧 Begin 的提交失败；旧 Begin 取得的 RCW 在失败路径释放一次，不会把旧 Entry/旧列表复活。
- 失败路径（GetUIElement 失败/非候选/NULL）只留下“已清屏、无 Entry”的状态，pbShow 保持调用前值（不抑制无法自绘的元素）。

### 3.3 Update（首次读取）

```
UpdateUIElement(id):
  if disposed/!isCurrentOwner -> S_OK
  entry = lookup(id); if null/Ended -> S_OK
  entry.InFlight++
  try:
    read = CandidatePageReader.Run(entry.Ops, () => _disposed || !_isCurrentOwner() || entry.Ended)
    if !read.Ok:
        publish Invalid(snapshot, reason: read.Failure)   // 隐藏，不保留旧列表
        return S_OK
    entries = read.Entries.Select((text,i) => new CandidateSnapshotEntry(read.PageStart+i, text))
    snapshot = new CandidateSnapshot(id, entry.Generation, entries,
        PageLocalSelection(read.Selection, read.PageStart, entries.Count),
        read.Selection, read.Count, read.PageStart, read.CurrentPage, true, null)
    // 提交点：再次核对 entry 仍为当前代、未 Ended、epoch 未变
    if (current && !Ended): _currentId/_currentGeneration = id/generation;
        overlay.SetSnapshot(snapshot); requestRedraw()
  catch: overlay.SetSnapshot(invalid);          // fail closed
  finally:
    entry.InFlight--
    if entry.Ended: ReleaseElementOnce(entry)   // End 曾同步到达
  return S_OK
```

- `sourceEnded` 在每次原生调用前后由读取器复核；End 在 Update 内同步到达时只置 Ended，读取器立即中断，提交点拒绝发布。
- 同 id 重复 Begin 已替换 Entry：旧代 Update 提交点因 generation 不匹配拒绝。
- 非当前候选的 Update 也读取，但只有“当前代”允许发布；多候选时后一次成功 Update 接管显示。

### 3.4 End / Dispose

```
EndUIElement(id):
  entry = remove(id)
  entry.MarkEnded()
  if (id == _currentId && generation == _currentGeneration):
      _currentId = 0; _currentGeneration = 0; overlay.ClearSnapshot(); requestRedraw()
  ReleaseElementOnce(entry)   // 若 InFlight==0；否则在 Update finally 释放
  return S_OK
```

- End 后绝不再调用该元素的任何 COM 方法（唯一例外是延迟到借用退出的 `ReleaseComObject`）。
- `Dispose`：`_disposed=true`；清空并释放全部 Entry、`overlay.Clear()`；不调用 Unadvise（由 Session 成对调用）；不抛异常。
- 焦点丢失/epoch 失效：Session 调用 `uiElementSink.Clear("focus-lost"|"epoch")`，立即隐藏并结束所有元素借用；迟到回调见 `_disposed/epoch` 直接返回 S_OK。

### 3.5 RCW 所有权与 cookie（GetUIElement / AdviseSink）

- `GetUIElement` 的输出是调用方拥有的 RCW：Entry 持有；非候选/识别失败/提交被重入取代时立即 `ReleaseComObject` 恰好一次。
- 同一 Entry 只释放一次（`Released`）；借用期间 End 只延迟到借用退出。
- **Advise cookie**：成功条件是 `hr >= 0 && cookie != TF_INVALID_COOKIE (0xFFFFFFFF)`；cookie `0` 合法，不假设非 0。用显式 `_advised` 标志追踪连接，不用 cookie 数值代表是否连接。
- `UnadviseSink` 只在 `_advised` 时调用；返回 `S_OK`、`E_INVALIDARG` 或 `CONNECT_E_NOCONNECTION`（无连接/已失效）都清除标志；其它失败保留 `_advised` 并计数、由 Dispose 前重试一次。
- `uiElementMgr/uiElementSource` 来自 threadMgr 的 QI 别名，不释放；sink CCW 由 `AdviseSink` 引用，Unadvise 后由 Session 释放托管引用。

## 4. 快照到 overlay 与帧末挂接

### 4.1 Overlay 接口修正（与几何解耦）

当前 `Present(snapshot, client, anchor)` 把两个独立来源耦合在一起（快照来自 sink，几何来自 `PublishGeometry`）。集成前改为：

```
internal void SetSnapshot(CandidateSnapshot? snapshot);   // sink 调用；null=隐藏
internal void SetGeometry(Rect clientBounds, Rect? anchorRow); // bridge/host 调用；null=几何缺失
internal void ClearSnapshot();                             // End：仅清候选，保留几何
internal void Clear();                                     // 失焦/epoch/卸载：两者都清
internal void Draw(DrawContext context);                   // FrameOverlay
```

同帧挂接（OpenTkHost）：

```
_candidateOverlay = new CandidateOverlay();
_runtime = new UiRuntime(...);
_runtime.FrameOverlay = _candidateOverlay.Draw;            // BuildFrame: Emit 后、ToArray 前
_router.AttachCandidateOverlay(_candidateOverlay);         // 会话惰性创建时传入
```

`DrawFrame` 的 `_frameInProgress=true` 覆盖整个 `BuildFrame + Submit + SwapBuffers`；候选只在 BuildFrame 内追加，满足同一 `TextureService.BeginFrame` 保护、内容之后、提交之前，不额外 BeginFrame。宿主 Dispose 顺序：`_runtime.FrameOverlay = null; overlay.Clear();` 再释放运行时/输入桥。

### 4.2 按需唤醒

- sink 成功发布/隐藏快照时调用 `_requestRedraw`（`UiRuntime.InvalidateRender` 的委托），只在内容或可见性真正变化时调用。
- `PublishGeometry` 更新逻辑几何时若 `(caret, composition, client, scale)` 与上次相等则不唤醒；窗口 Move/Resize/DPI 由宿主 `NotifyGeometryInvalidated()` 唤醒并撤销。
- 安全阶段待办：`TsfInputBridge.HasPendingSafeWork`（原生事实/取消/布局通知）。`OpenTkHost.RequestDeferredInputRecoveryWake` 除现有延后输入外，也在此为真时 `RequestWakeup(TimeSpan.Zero)` 一次；不周期轮询。

## 5. 几何：TextInputGeometry / char snapshot → ACP

### 5.1 新增绑定提供方（AcpGeometryProvider.cs 或新 HostAcpGeometryProvider.cs）

实现 `IAcpGeometryProvider`，由 `TsfInputBridge.PublishGeometry(TextInputGeometry)` 在安全阶段调用 `Publish`：

```
internal readonly record struct AcpGeometryQueryIdentity(
    long NativeGeneration, long DocumentVersion, string Text);   // 由 store 在查询时只读提供

internal readonly record struct HostGeometryCache(
    TextInputGeometry Logical,                 // 逻辑客户区
    CompositionCharGeometrySnapshot? Char,      // 同一安全阶段捕获
    string DisplayText, string Preedit,         // 精确身份（不是只比长度）
    int Epoch, int CoreDocumentVersion,
    long StoreDocumentVersion, long NativeGeneration,
    long MirrorRevision, long NativeRevision,
    int LayoutSerial, double ScrollX,
    TsfRect ClientScreen, TsfRect SurfaceScreen,
    double PixelScale, long Serial);
```

- `Publish` 只写不可变缓存（同 UI 线程），不排版、不通知、不建帧；发布前必须通过全部身份校验，否则清空缓存（NOLAYOUT），绝不发布近似几何：
  - `Char != null`；`Char.Epoch == client.Session.SessionId` 且当前归属谓词为真。
  - `Char.DocumentVersion == authoritative.Committed.Version == client.Document.Version`（三者都是核心编辑版本，int）。
  - `Char.Preedit == authoritative.Preedit`（序数相等）。
  - `Char.Layout.Text == authoritative.DisplayText`（**完整串相等**，不是长度）；`store.ReadAuthoritativeDocument().Text == authoritative.DisplayText`。
  - `Char.LayoutSerial == runtime.LayoutSerial`；`Char.ScrollX` 与快照一致。
  - 同时捕获 **store 自己的** `_document.Version`（long，独立单调计数）与 `NativeGeneration`；核心 `Committed.Version` 与 store 版本语义不同，不得直接比较，只通过“同一文本+同一次发布”绑定。
  - 记录 `MirrorRevision/NativeRevision`。
  - 身份来源：`PublishGeometry` 时可读 `NativeCompositionOwner` 暴露的只读 `AuthoritativeSnapshot`（内部访问器）与 `store.ReadAuthoritativeDocument()`；两者只在安全阶段读取，provider 不在查询时访问客户端。
- `TryGetTextRect(acpStart, acpEnd, in AcpGeometryQueryIdentity identity, ...)`：
  - 先做只读身份比较：`identity.NativeGeneration == Cache.NativeGeneration`、`identity.DocumentVersion == Cache.StoreDocumentVersion`（long）、`identity.Text == Cache.DisplayText`（序数）；任一不等返回 false（`TS_E_NOLAYOUT`）。**provider 查询不读取客户端/活动会话**，只比较 store 传入的只读身份与不可变缓存。
  - `acpEnd > Cache.Char.Layout.Text.Length` 返回 false；不钳制后伪造。
  - 零长度：`caret = Layout.SnapToCaretStop(acpStart)`，矩形 `Offset(Layout.GetCaretBounds(caret, 1.5), Char.Origin)`；与 `EditableClip` 求交。
  - 非零：`Offset(Layout.GetRangeBounds(acpStart, acpEnd), Char.Origin)` ∩ `EditableClip`。
  - 交集为空：caret 完全裁出时 TextField 已发布 `Unavailable`，provider 应返回 false；若仍收到范围查询，返回 `knownEmpty=true` 对应 `S_OK + 零矩形 + clipped=1`，不返回旧坐标。
  - 部分裁剪：`clipped=true`；结果按 `ClientScreenOrigin + Round(logical * PixelScale)` 转屏幕物理像素。
  - epoch 由 Bridge 生命周期与 publish 校验；Provider 实例随会话创建/销毁，不跨 epoch 复用。
- `TryGetScreenRect`：返回 `SurfaceScreen`（不要求读锁，与文本布局版本解耦）；窗口最小化/客户区为零时 `true + 全零 + S_OK`；无 hwnd/无法查询时 `false + TS_E_NOLAYOUT`。
- `IAcpGeometryProvider.TryGetTextRect` 的内部签名增加 `in AcpGeometryQueryIdentity`：`AcpTextStore.GetTextExt` 在已持有的读锁内用 `_document.Text/_document.Version/_nativeGeneration` 构造该值；`UnboundGeometryProvider` 同步更新。只扩 internal 契约，不扩公开 API。
- 线程：只允许 UI 线程写；原生 `GetTextExt/GetScreenExt` 在同线程读；用 `Volatile.Read` 交换不可变引用，不加锁、不在 COM 调用栈排版。

### 5.2 坐标换算与失效

- 逻辑 → 屏幕物理：`origin = ClientToScreen(hwnd, (0,0))`；`screen = origin + Round(logical * PixelScale)`；`ClientScreen = (origin, origin + Round(clientLogical * PixelScale))`。PixelScale 由宿主注入（`ViewportMetrics.PixelScale`，不是 PointerScale），通过 Router 新入口 `AttachHostPixelScale(Func<double>)` 跟随 DPI 更新。
- 失效来源：窗口 Move/Resize、DPI/视口变化、焦点丢失/客户端释放、外部绑定变更、组合结束。宿主现有 `NotifyGeometryInvalidated()` → Bridge 清 provider + `overlay.SetGeometry(client, null)` + 标记待布局通知；下一次安全布局由 TextField 重新发布。
- `PublishGeometry` 本身发生在 `UiRuntime.ProcessPendingWork`（宿主 `_processingWork=true`）：**只更新 provider/overlay 缓存**；`Store.NotifyLayoutChange()` 延后到下一次 `DrainTsfSafePhase`（宿主工作结束、store `IsSafePhase`）。
- 原生通知：`AcpTextStore` 新增 `internal void NotifyLayoutChange()`，复用现有通知机制（mask `TS_AS_LAYOUT_CHANGE`、持锁入队、锁外/safe phase 发送）→ `ITextStoreACPSink::OnLayoutChange(TS_LC_CHANGE, _viewCookie)`。同一几何代次只通知一次；未变化不通知。延迟项不单独携带 sink 代次：`UnadviseSink`/`Dispose` 清空延迟队列，且已有连接时 `AdviseSink` 返回 `CONNECT_E_ADVISELIMIT`，所以 flush 的项必属当前连接（发送仍按当前 mask 门控）。

## 6. 能力、失败与重入矩阵

| 场景 | 行为 |
|---|---|
| QI `ITfThreadMgrEx` 失败 | 不调用 Activate；完整释放；TSF 失败回退 IMM，原因写明 |
| ActivateEx 失败 | 不 Deactivate（未拥有）；回退 |
| ActivateEx 成功但 flags 缺 `TF_TMF_ACTIVATED`/UIElement-only | `activated=true`；清理链 Deactivate 一次；失败回退，能力报告 false |
| UIElementMgr/QI/Advise 失败 | 整个 TSF 会话失败；清理链 unadvise(若已)/Deactivate/Pop/Release；回退 IMM |
| Begin 非候选 / GetUIElement 失败 | pbShow 不变；不注册；不抑制系统候选 |
| Begin 候选 | pbShow=0；只注册；不读内容 |
| Update 内同步 End | 读取器逐调用中断；提交点拒绝；Entry 借用退出时释放一次 |
| 页面/字段边界损坏 | 快照 Invalid + 隐藏；不回退旧页/第一页 |
| 确认提交 | 快照不可变 + 当前代 + epoch 校验；旧代/End 后不得覆盖 |
| 失焦/epoch/卸载 | 立即清 overlay 与全部 Entry；迟到回调快速返回 |
| 几何缺失/完全裁出 | overlay 隐藏；ACP GetTextExt `TS_E_NOLAYOUT`（不伪造原点） |
| 窗口移动/resize/DPI | 清几何缓存 + 待通知；下一次安全布局重发 |
| Deactivate 前 | UIElement sink 已 Unadvise、元素 RCW 已释放、overlay 已清 |

## 7. 文件范围

修改（基础 GPU 验收通过后）：
- `src/CSharp.UI.OpenTK/WindowsTsf/TsfHostSession.cs`：ActivateEx/flags、provider、sink 字段、通知与 Dispose 顺序。
- `src/CSharp.UI.OpenTK/WindowsTsf/TsfInputBridge.cs`：PublishGeometry 真实实现、几何缓存/失效、sink 生命周期、HasPendingSafeWork、overlay/runtime/redraw 接线。
- `src/CSharp.UI.OpenTK/WindowsTsf/WindowsTextInputRouter.cs`：AttachCandidateOverlay/AttachHostPixelScale、epoch 退役清屏、安全唤醒。
- `src/CSharp.UI.OpenTK/WindowsTsf/AcpTextStore.cs`：`NotifyLayoutChange`（延迟通知，复用现有 mask/lock 队列；连接撤销/释放清空队列）。
- `src/CSharp.UI.OpenTK/WindowsTsf/AcpGeometryProvider.cs`：`HostAcpGeometryProvider`（或同文件新增）。
- `src/CSharp.UI.OpenTK/WindowsTsf/CandidateOverlay.cs`：SetSnapshot/SetGeometry 拆分 + 下述性能/边界修订。
- `src/CSharp.UI.OpenTK/Hosting/OpenTkHost.cs`：创建 overlay、`FrameOverlay` 挂接/解绑、PixelScale/窗口原点注入、待办唤醒。
- `src/CSharp.UI/Runtime/UiRuntime.cs`：仅保留现有 `FrameOverlay` 入口（不再改）。

新增：
- `src/CSharp.UI.OpenTK/WindowsTsf/TsfUiElementSink.cs`：sink、Entry、RCW 借用/释放、快照提交。
- 测试：`tests/CSharp.UI.OpenTK.Tests/TsfUiElementSinkTests.cs`（托管 fake manager/element）、`HostAcpGeometryProviderTests.cs`、`CandidateOverlayContractTests.cs`；真实原生激活/sink 与图形 smoke 由独立验证新增。

不修改：核心编辑事务/组合状态机、TextField 绑定语义、IMM 路径、公开 API。

## 8. Overlay 修订清单（validation 首轮 GPU 后统一实施）

1. **测量缓存**：以 `(snapshot 引用, FontDescription, PixelScale, textMaxWidth)` 为 key 缓存 `TextLayoutResult[]`、footer layout、rowHeight、contentWidth、`EntryLabel[]`、footer 文本；几何移动/主题色变化不重排。仅在快照/字体/DPI/可用宽度变化时重建，不引入通用缓存抽象。
2. **可见交集**：`row.IntersectsWith(client)` 必须为真，否则隐藏（防止完全裁出的行被误传为锚点）。
3. **有限值**：client/anchor/layout 尺寸非有限、`row.Height<=0`、宽高无法形成有限矩形时隐藏；`Math.Clamp` 前先验证，避免 NaN/异常。
4. **最终 client 约束**：所有输出矩形最后与 `clientBounds` 求交/夹取；不继承 TextField 文字裁剪。
5. **裁剪保护**：`PushClip` 后的命令输出用 `try/finally { PopClip(previous); }`，异常也不泄漏裁剪。
6. 选中行颜色/页脚仍按主题解析；不改快照语义。

## 9. 验收（独立验证新增，不在本设计阶段执行）

- 真实隐藏 HWND：`ActivateEx`/`GetActiveFlags` 值与单次 Deactivate；sink Advise/Unadvise 配对；UIElement-only 能力报告。
- 托管 fake + 真实原生：Begin 不读内容、id0、Update 首次读取、Begin 重复 id、Update 内 End、End 后无 COM、GetUIElement RCW 恰一次释放、多元素切换、epoch 迟到回调。
- 图形：中文候选非空像素、内容背景后 overlay 命令顺序、选中着色、窄区不越界、上下不足隐藏、invalid/缺几何零命令、同帧/纹理帧保护。
- 几何：ACP 零/非零范围、组合显示串映射、部分/完全裁剪、GetScreenExt 无锁、Move/Resize/DPI/滚动失效、布局通知只在安全阶段且只发一次。
- 失败路径：flags 缺失、sink advise 失败、无 hwnd、MTA 回退；能力/原因与真实状态一致。
