# TI-07 P3b 正式宿主候选接线执行记录

日期：2026-10-21
状态：实现与自测完成，等待独立验证；P3b、P3、TI-07 均**未**通过。
持久证据：[`2026-10-21-ti07-p3b-host-integration-evidence/`](2026-10-21-ti07-p3b-host-integration-evidence/README.md)（正式 Host 1/1、Release 101/28 的 summary/exit 原文；原始进程 stdout 已不可取，其余仅计数记录）。
范围：正式 OpenTK 宿主的 ActivateEx/UIElement sink/几何/同帧 overlay 接线，以及 sink/overlay 生命周期修正。

## 1. 实现

| 文件 | 改动 |
|---|---|
| `src/CSharp.UI.OpenTK/WindowsTsf/TsfHostSession.cs` | `TryCreate` 改为单次 `ITfThreadMgrEx::ActivateEx(TF_TMAE_UIELEMENTENABLEDONLY)` + `GetActiveFlags` 校验（`TF_TMF_ACTIVATED|TF_TMAE_UIELEMENTENABLEDONLY`）；QI/Advise `ITfUIElementSink`，cookie 0 合法、显式 `_uiElementAdvised`；失败逆序释放（UIElement sink → edit sink → owner/store → Deactivate → Pop → RCW → lease）；公开 internal 诊断 `ActiveFlags/ActivationMode/UiElementAdvised/CandidatePresentationAvailable/GeometryProvider/UiElementSink`；`Dispose` 在 Deactivate 前撤销 UIElement sink、清几何与 overlay。 |
| `src/CSharp.UI.OpenTK/WindowsTsf/TsfUiElementSink.cs` | Begin 仅用调用栈内 `PendingBegin` 取消/按引用移除（无永久墓碑）；`GetUIElement` 成功即以 `object ownedElement` 进入 finally，QI/构造/提交异常也恰好释放一次；Update 全局单调序号，嵌套旧读取不得覆盖新提交；Clear 防嵌套、清期间禁止注册；Dispose 先置 disposed 再 Clear；BuildSnapshot 用数组所有权构造避免二次复制。 |
| `src/CSharp.UI.OpenTK/WindowsTsf/CandidateOverlay.cs` | clean cutover 为 `SetSnapshot/SetGeometry/ClearSnapshot/Clear`（无 Present shim）；测量缓存按（快照引用、字体、PixelScale、可用文本宽度）；几何移动复用缓存；非有限矩形、行高<=0、锚点与客户区不相交、上下放不下、宽度不足均零命令；输出最后与 client 求交；`PushClip` 后 `try/finally PopClip`。几何入口明确只接受逻辑像素。 |
| `src/CSharp.UI.OpenTK/WindowsTsf/AcpGeometryProvider.cs` | `IAcpGeometryProvider.TryGetTextRect` 增加 `in AcpGeometryQueryIdentity(NativeGeneration, DocumentVersion, Text)`（正式/测试实现全部迁移，无旧重载）；新增 `HostAcpGeometryProvider`（不可变文本缓存 + **独立**显示表面缓存）；文本身份不匹配/越界/完全裁出如实失败；显示表面在文本失效后仍有效，只有窗口表面失效才清；最小化/隐藏=零矩形+S_OK，原生查询失败=负 HRESULT。 |
| `src/CSharp.UI.OpenTK/WindowsTsf/AcpTextStore.cs` | `GetTextExt` 在同一读锁内构造只读身份；新增 `NotifyLayoutChange()`（`TS_AS_LAYOUT_CHANGE` mask，持锁/非安全阶段入队，锁外/safe flush 发送；**2026-10-22 独立审核修正**：延迟项不单独携带 sink 代次，撤销/释放清空延迟队列，已有连接时禁止换新 sink）与 `NotificationKind.LayoutChange`。 |
| `src/CSharp.UI.OpenTK/WindowsTsf/TsfInputBridge.cs` | 真实 `PublishGeometry`：安全阶段、只读身份全量核对（epoch/核心版本/store 版本/native 代次/预编辑/完整显示串/LayoutSerial/ScrollX）、ClientToScreen + PixelScale 转屏幕物理像素；只写缓存不通知；`DrawCandidates` 同帧绘制前只读复核当前 char 快照、LayoutSerial/ScrollX、store 版本/代次，失效立即 `ClearGeometry`；`NotifyGeometryInvalidated` 撤销文本几何并**同步刷新独立显示表面**；最小化发布零矩形；`HasPendingSafeWork`；能力按真实 UI-less 状态报告。 |
| `src/CSharp.UI.OpenTK/WindowsTsf/WindowsTextInputRouter.cs` | 注入并复用单一 `CandidateOverlay`；`DrawCandidates`、`HasPendingSafeWork`、`NotifySurfaceUnavailable`；TSF 建桥传入 overlay/单次重绘回调；释放清 overlay。 |
| `src/CSharp.UI.OpenTK/WindowsTsf/NativeCompositionOwner.cs` | 只读 `HasPendingSafeWork`（事实/终止/取消/基线回读）。 |
| `src/CSharp.UI.OpenTK/Hosting/OpenTkHost.cs` | 创建 overlay 并挂 `UiRuntime.FrameOverlay`（`Emit` 后、`ToArray` 前，同一 BeginFrame）；DPI 变化/移动/resize/minimize 撤销或刷新几何；恢复唤醒同时考虑 `Router.HasPendingSafeWork`；释放先解绑 FrameOverlay 并清 overlay。 |
| `src/CSharp.UI.OpenTK/WindowsTsf/CandidateSnapshot.cs` | 内部所有权构造（`takeEntriesOwnership`），外部构造仍防御复制。 |
| `src/CSharp.UI.OpenTK/WindowsTsf/TsfHostBridgeResult.cs` | 新增 `UiElementUnavailable` 失败分类。 |

测试改动：`TsfSessionLifetimeTests`/`TsfCancellationTests` 适配新 `TryCreate` 签名；`AcpTextStoreProtocolTests.ScriptedGeometry` 迁移 identity 签名；`CandidateOverlayRenderingTests` 迁移到新 overlay API 并增加内容/几何独立清除用例；`TsfUiElementSinkTests` 增加嵌套 Update 新旧顺序、Clear 重入注册阻断、未知 id End 用例；新增 `HostAcpGeometryProviderTests`、`TsfHostIntegrationTests`。

## 2. 正式宿主运行（真实断言）

命令：

```
dotnet build tests/CSharp.UI.OpenTK.Tests/CSharp.UI.OpenTK.Tests.csproj -c Debug
tests/CSharp.UI.OpenTK.Tests/bin/Debug/net10.0/CSharp.UI.OpenTK.Tests.exe -class CSharp.UI.OpenTK.Tests.TsfHostIntegrationTests
```

结果：`Total: 1, Errors: 0, Failed: 0, Skipped: 0`，进程退出码 0。
场景：独立 STA 线程真实 `UiApplication.Run` + 隐藏 GL 窗口 + 真实 `TextField` 焦点 + `UseTsfInputBridge=true`。断言通过：

- `Router.ActiveBackend == Tsf`；
- 真实 `GetActiveFlags` 同时含 `TF_TMF_ACTIVATED` 与 `TF_TMAE_UIELEMENTENABLEDONLY`；
- `UiElementAdvised == true`、`CandidatePresentationAvailable == true`；
- 至少渲染 1 帧；宿主退出后 `Session.IsDisposed == true`。

## 3. 自测计数（Debug）

| 类 | 结果 |
|---|---|
| `TsfUiElementSinkTests` | 18/18（未接入 GPU） |
| `CandidateOverlayRenderingTests` | 4/4（真实 GPU/像素） |
| `HostAcpGeometryProviderTests` | 5/5 |
| `AcpTextStoreProtocolTests` | 72/72 |
| `TsfCancellationTests` | 4/4（真实 TSF 原生取消） |
| `TsfSessionLifetimeTests` | 1/1（真实 STA + 隐藏 HWND + 真实 ActivateEx/UIElement Advise） |
| `TsfHostIntegrationTests` | 1/1（正式宿主/GL） |

命令形式：`CSharp.UI.OpenTK.Tests.exe -class <FQCN>`；以上均 exit 0。先前 validation 的基础 overlay GPU 3/3 证据由独立验证保存。

Release 回归（同一组类合并运行）：

```
dotnet build tests/CSharp.UI.OpenTK.Tests/CSharp.UI.OpenTK.Tests.csproj -c Release
tests/CSharp.UI.OpenTK.Tests/bin/Release/net10.0/CSharp.UI.OpenTK.Tests.exe \
  -class ...TsfUiElementSinkTests -class ...HostAcpGeometryProviderTests \
  -class ...CandidateOverlayRenderingTests -class ...TsfHostIntegrationTests \
  -class ...TsfSessionLifetimeTests -class ...AcpTextStoreProtocolTests
```

结果：`Total: 101, Errors: 0, Failed: 0, Skipped: 0`，exit 0（真实 TSF 生命周期日志两次 TryCreate 成功、两次 Dispose 归还租约、final registeredWindows=0）。

最终增量（独立显示表面初始化 + sink 两处边界修正后）Release 复核：

```
...Tests.exe -class ...TsfHostIntegrationTests -class ...TsfUiElementSinkTests \
  -class ...HostAcpGeometryProviderTests -class ...CandidateOverlayRenderingTests
Total: 28, Errors: 0, Failed: 0, Skipped: 0; exit 0
```

最终增量 Debug（宿主集成/sink/geometry 三类）= `Total: 24, Failed: 0, Errors: 0`，exit 0；overlay GPU 4 项见上表单独通过。

## 4. 未完成 / 边界

- 新增 `tests/CSharp.UI.OpenTK.Tests/TsfLayoutNotificationRegressionTests.cs`（真实宿主行为回归：
  有效布局 → 1×1 不可布局仅一次撤销通知 → 多个安全阶段/帧计数稳定、待办清空 → 恢复布局再通知一次）；
  本轮未构建，交 real-ime 统一运行。
- 2026-10-22 更新：该回归已由 real-ime 在 Debug 独立运行 1/1（Failed 0 / exit 0）；Release 与原始日志待其补齐，见[22 日独立审核](2026-10-22-ti07-p3b-independent-review.md)。
- 静态复核发现并最小修正了“不可用几何每次安全阶段重复置 pending”的布局通知自激风险
  （`TsfInputBridge.PublishGeometry`/`DrawCandidates` 仅在“从可见变为不可用”时排队一次）；
  该修改在本轮**未构建/未运行**（dotnet 已交 real-ime），需下一轮独立验证。
- 2026-10-22 更新：Debug 已由 real-ime 运行（ACP 75/75、通知回归 1/1、host/sink/geometry/GPU/取消/生命周期/reader 全绿，Failed 0 / exit 0）；Release 原始日志待补。静态与运行证据见[22 日独立审核](2026-10-22-ti07-p3b-independent-review.md)。
- 2026-10-22 更新：真实微软拼音 640 高窗口已产生可见 9 项候选并截图（预编辑零写、空格唯一精确提交“你好”、exit 0），证据见 [`2026-10-22-ti07-p3b-real-ime-evidence/nihao-visible-baseline/`](2026-10-22-ti07-p3b-real-ime-evidence/nihao-visible-baseline/ime-host-check.jsonl)。滚动/裁出恢复、移动/DPI、失活等 P4 场景仍待验；测试 fake/合成候选仍不作为真实输入证据。
- 候选鼠标选择未实现（未验证前不提供）。
- 未提交、未推送；整体 TI-07 未通过。
