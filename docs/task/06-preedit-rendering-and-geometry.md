# TI-06：预编辑绘制、命中与候选几何

创建日期：2026-10-17

状态：已完成，2026-10-19经Codex最终独立复审通过；提交推送记录见审核文末。

阶段：C

设计依据：[文本输入与编辑基础计划](../plan/2026-10-17-text-input-and-editing-plan.md)（接口冻结前均为设计草案）。

[返回任务索引](README.md)

## 目标与范围

实际排版、caret、命中、滚动和宿主锚点共用准确几何。

边界：平台无关绘制和最终布局后的锚点发布，不绘制系统候选UI。

## 前置依赖

[TI-04：组合会话与预编辑状态机](04-composition-state-machine.md)均须Codex复审通过后再实施；使用前置任务已冻结契约。

## 实施清单与接口接入

- [x] 使用真实Skia显示串布局绘制预编辑下划线、活动段及组合caret，区分文档与显示缓存。
- [x] 命中映射与原选区替换显示一致；对组合期间鼠标改选区执行先取消后操作的固定策略。
- [x] 最终排列发布有效逻辑caret/排除矩形，涵盖ScrollX、祖先滚动、Padding/Frame、裁剪、像素吸附。
- [x] 完全隐藏/裁出撤销锚点；字体/DPI/窗口位置/布局变化更新，同值不不断请求重绘。

## 测试与验收

- [x] 真实Skia/GL断言预编辑墨迹、下划线与caret相对位置、嵌套裁剪；不能只断言命令非空。
- [x] DPI1/1.25/1.5/2和320/480/768/1024宽下文字纹理1:1，长预编辑横向滚动及跨断点同节点保持。
- [x] 实际排版原点+caret可见，不能只检查夹取后的矢量线；R16/R17与已有caret回归通过。

## 实施记录与审核门槛

实施时记录实际签名、涉及模块、命令/退出码/测试计数、证据路径及平台边界。DSH自测通过后标“待Codex复审”；Codex审核问题写docs/return带日期文件，DSH返修至通过后才英文提交并推送。状态改“已完成”须有Codex复审结论，不在Build/Measure中执行外部副作用。

本轮不修改既有archive文件；关键证据不只放可清理artifacts。后续交付记录由Codex创建，当前不预先标记验收通过。

## Codex 实施中独立检查（2026-10-19）

首批已编译版本已发现 R1～R5（客户端几何回读、撤销通知归属、纯重绘外部同步、150%/200%长预编辑末尾锚点、取消后鼠标按下滚动映射），见[TI-06审核与返修要求](../archive/2026-10-23-completed-reviews/2026-10-19-ti06-preedit-geometry-review.md)。DSH完成初步自测后必须读取并修复，再等待Codex最终复审；这些早期证据不表示最终实现结果。不得在未关闭问题前提交或标已完成。


2026-10-19追加复测：原142项已通过，但R3的“长预编辑滚动→外部短文档→纯重绘”仍有2项失败（144项中142通过）。详见审核文末，发布锚点前仍须收敛ScrollX，不能等Emit才恢复滚动。最终复审尚未通过。

## DSH 实施与返修记录（2026-10-19，待Codex复审）

### 实际接入与最小内部变更

核心程序集仍无 Skia/OpenTK/平台类型；未新增或扩大公开 API。`TextInputGeometry.CaretBounds` 仅更新文档语义：组合期间发布组合 caret；API 形状不变。

- `src/CSharp.UI/Views/TextField.cs`
  - `TextFieldDisplayLayout` / `TextFieldDisplayCache` / `TextFieldState.DisplayLayoutCache`：显示串（前缀+预编辑+后缀）使用独立真实排版缓存，文档排版仍由原有 `TextFieldCache` 保存；命中/编辑始终使用正式文档排版，预编辑不写 Binding。
  - `Emit` 组合期间绘制显示串、每段下划线（活动段强调色 2px、非活动 1px）、组合 caret；组合期间不绘制文档选区。组合 caret 与文字纹理共用同一吸附原点。
  - `GetDisplayLayout` 以当前真实绑定文本 + 活动替换区构造显示串；`MapDisplayToDocument` 继续使用 TI-04 冻结映射。
  - `EnsureCaretVisible`：组合期间滚动预算包含 caret 宽度 + 1 物理像素吸附余量，保证真实排版原点 + caret 矩形完整可见；已提交 caret 保留原滚动语义（R12 回归断言不变）。
  - `ComputeInputGeometry`：最终布局后以显示串排版原点、`ScrollX`、祖先 `GetChildClip`、`Padding/Frame`、内容区裁剪计算 caret/组合/排除矩形；完全裁出发布 `Unavailable`；不再使用矢量夹取冒充可见。
  - `PrepareTextInputGeometry` / `PublishTextInputGeometry`：新增内部安全阶段入口；发布前同步真实文档并收敛 `ScrollX`，再生成锚点。
  - 组合中 `PointerDown`：先取消组合，随后用正式文档布局与恢复后的选区收敛 `ScrollX`，再计算命中；不依赖 PointerUp 掩盖。
- `src/CSharp.UI/Editing/TextFieldCompositionClient.cs`
  - 保存当前有效几何，`GetState` 返回与发布值相同的几何；释放/epoch 失效/隐藏返回 `Unavailable`，查询路径不排版。
  - `PublishGeometry` 同值不重复调用平台；`MarkGeometryRevoked` 在归属撤销后立即使查询失效。
- `src/CSharp.UI/Editing/TextInputServiceSession.cs`
  - `EndCore` 先让客户端失效并撤销几何查询值，再按所有者发布 `TextInputGeometry.Unavailable`，最后 `EndSession`；代际号守护撤销回调的同步重入，不结束重入建立的新会话。
- `src/CSharp.UI/Runtime/UiRuntime.cs`
  - 安全阶段顺序改为 `PrepareTextInputGeometry` → 消费 epoch/取消通知 → `PublishTextInputGeometry`；不在 Build/Measure/Emit 或原生回调中调用平台。
- `src/CSharp.UI/Views/View.cs`
  - 新增两个 `internal virtual` 安全阶段钩子；默认无操作，供焦点文本控件覆盖。
- `src/CSharp.UI/Editing/TextInputServiceContracts.cs`
  - 仅更新 `CaretBounds` 文档：发布的是有效逻辑 caret（组合时为组合 caret）。

### R1～R5 与追加 R3 返修

- **R1 客户端几何回读**：`GetState().Geometry` 与最近发布/实际显示同源；释放/epoch 失效/隐藏不泄漏旧值；查询不排版。覆盖四 DPI/四宽度、长预编辑、隐藏与释放回归。
- **R2 撤销通知归属**：撤销通知改到“客户端失效之后”由会话接线发出，代际号守护同步重入；失焦、窗口失活、真正卸载与同步旧客户端 Result 均不能写入。保留 TI-04 先切断归属后通知的边界。
- **R3 纯重绘外部变化**：发布前先同步真实文档/来源、取消组合并消费 epoch 变化；长预编辑滚动后外部改成短文档时，在安全阶段准备正式显示串并收敛 `ScrollX`，同一帧锚点与 Emit 显示一致，不依赖下一帧恢复。`Prepare` 入口注释已改为“安全阶段允许读取/建立文本排版缓存收敛选区与滚动，但不写 Binding/不重建/不调用平台”。
- **R4 长预编辑/普通 caret 末尾与切换**：组合与已提交 caret 统一使用“caret 宽度 + 1 物理像素吸附余量”的滚动可见性预算；删除 `ClampCaretIntoClip`，发布几何直接与真实裁剪求交，完全可见时保留原始矩形（避免 `Intersect` 舍入导致“发布几何 == 绘制 caret”失真）。审计的四 DPI、空文档/非空文档末尾、窄框、普通长文本 End、长预编辑 Result 确认后场景均通过真实 Skia 原点+caret 可见性检查；R16/R17 纹理 1:1 与既有 caret 可见/纵向居中回归保留。`FlexIntegrationTests`/`FlowIntegrationTests` 中“文本末端贴死右边缘”的断言改为“真实 caret 矩形完整可见 + 文字纹理不越界”的用户可见不变量，未降低验收目标。
- **R5 PointerDown 命中**：取消组合后先按正式文档与恢复选区收敛 `ScrollX`，再计算按下命中；新增按下即时断言。
- **追加 R3 长滚动外部缩短**：`PublishTextInputGeometry` 在计算锚点前调用 `EnsureCaretVisible`；新增“长预编辑滚动→外部短文档”和“普通长文档滚动→外部短文档”核心回归，以及真实 Skia 同场景回归。

### 命令、退出码与测试计数（cwd = 仓库根）

| 命令 | 退出码 | 结果 |
|---|---|---|
| `dotnet build CSharp.UI.slnx --no-restore -m:1 -nodeReuse:false` | 0 | 0 警告 / 0 错误（Debug） |
| `dotnet build CSharp.UI.slnx -c Release --no-restore -m:1 -nodeReuse:false` | 0 | 0 错误；仅既有警告（增量构建 8 条，首次全量 17 条：TI-02 CS8601 与既有 xUnit 分析器） |
| `dotnet run --project tests/CSharp.UI.Tests -c Debug --no-build` | 0 | 545 通过 / 0 失败 / 0 跳过（TI-05 基线 529 + 16） |
| `dotnet run --project tests/CSharp.UI.Text.Tests -c Debug --no-build` | 0 | 67 通过 / 0 失败 |
| `dotnet run --project tests/CSharp.UI.Integration.Tests -c Debug --no-build` | 0 | 53 通过 / 0 失败（基线 30 + 23，真实 Skia） |
| `dotnet run --project tests/CSharp.UI.OpenTK.Tests -c Debug --no-build` | 0 | 251 通过 / 0 失败（基线 242 + 9，真实窗口/GL/剪贴板） |
| `dotnet run --project artifacts/ime-work/ti06-audit -c Debug` | 0 | 162 项独立检查全部通过（含普通长文本 End / 长预编辑 Result 的四 DPI 复查） |
| `dotnet run --project artifacts/ime-work/ti04-audit -c Debug` | 0 | TI-04 兼容性 41 项全部通过 |
| `dotnet run --project artifacts/ime-work/ti05-audit -c Debug` | 0 | TI-05 兼容性 49 项全部通过 |

Release 四套测试同样为 545/67/53/251，均 0 失败。单配置合计 916 项工程测试，Debug/Release 两配置均通过；基线能力保留；Flex/Flow两处纹理贴边断言已按统一caret余量修订，详见Codex最终复审。

### 证据路径

- `artifacts/ime-work/ti06/build-debug-r4.log`、`build-release-r4.log`
- `artifacts/ime-work/ti06/r4-tests-core-debug.log`、`r4-tests-text-debug.log`、`r4-tests-integration-debug.log`、`r4-tests-opentk-debug.log`
- `artifacts/ime-work/ti06/r4-tests-*-release.log`
- `artifacts/ime-work/ti06/final-audit-162.log`（当前 162 项独立检查全部通过；工具源码只读引用 `artifacts/ime-work/ti06-audit/Program.cs`）
- `artifacts/ime-work/ti06/final-audit-146.log`（早期 146 项快照；其最后 2 项新增检查是“撤销回调新会话重入”，不是长滚动外部缩短）
- `artifacts/ime-work/ti06/r4-ti04-compat.log`、`r4-ti05-compat.log`（41/49 兼容性检查）
- Codex 审核与过程证据：`docs/archive/2026-10-23-completed-reviews/2026-10-19-ti06-preedit-geometry-review.md` 及同名 `-evidence` 目录（本轮未改动）。

### 新增/修改测试文件

- 新增 `tests/CSharp.UI.Tests/TextFieldPreeditGeometryTests.cs`（16 项：显示串/文档缓存分离、下划线/活动段/组合 caret、选区替换、命中取消顺序、长预编辑滚动、几何发布与同值去重、隐藏/失焦/失活/卸载撤销、纯重绘外部变化、字体/DPI、R1/R2/R5、外部缩短滚动）。
- 新增 `tests/CSharp.UI.Integration.Tests/TextFieldPreeditRenderingTests.cs`（23 项：真实 Skia 四 DPI 下划线/caret 相对位置、四宽度同节点、长预编辑滚动、映射、嵌套裁剪、四 DPI 末尾可见性、普通长文本 End、长预编辑 Result 确认、外部缩短同帧锚点）。
- 新增 `tests/CSharp.UI.OpenTK.Tests/PreeditRenderingGpuTests.cs`（9 项：真实 GL 墨迹/下划线/caret 与 DPI、四宽度长预编辑裁剪、嵌套裁剪）。

### 平台边界与未验证项

- 未实现 TI-07 Windows 原生 IME 桥，未接入真实输入法组合/候选窗，未安装、启用或测试微软拼音，未修改系统输入法设置。
- GL 像素测试使用真实 Skia + OpenTK/OpenGL 离屏 framebuffer，但组合事件来自测试服务替身，不等于真实 IME 验收；真实候选锚点/平台单位换算仍属 TI-07/TI-10。
- 窗口位置/屏幕坐标转换由宿主负责；核心只发布内容区逻辑像素。
- `docs/task/README.md` 索引仍不标 TI-06 完成；本记录不宣称验收通过，等待 Codex 复审。未提交、未推送，未修改 Codex 审核记录/证据/审计源码。


2026-10-19最终提交前追加：R4仍影响普通长文本End与长预编辑Result确认后的caret几何。162项独立检查150通过、12失败，详见审核文末，必须继续返修，不能提交或标已完成。

2026-10-19 R4追加返修（DSH，待Codex复审）：已把组合与已提交 caret 的滚动预算统一为“caret 宽度 + 1 物理像素吸附余量”，移除仅组合分支的旧 `committedMaxScroll`；完全可见的发布 caret 保留原始矩形，避免 `Intersect` 舍入影响精确一致。新增四 DPI 普通长文本 End、长预编辑 Result 确认后回归；Flex/Flow 中“文本末端贴死右边缘”的断言改为真实 caret 完整可见+文字不越界的用户可见不变量。最新独立审计 162/162 通过，Debug/Release 四套工程测试 545/67/53/251 全通过，TI-04/TI-05 兼容性 41/49 通过。仍待 Codex 复审，不提交、不标完成。

## Codex 最终复审（2026-10-19）

已通过。Debug/Release各916项工程测试、162项独立运行时检查及TI-04/TI-05兼容41/49项全部通过。R1～R5及追加长滚动、普通caret和组合结果切换问题均关闭；批准DSH英文提交和推送。完整结论、边界及持久证据见[TI-06审核](../archive/2026-10-23-completed-reviews/2026-10-19-ti06-preedit-geometry-review.md)。上述待复审/失败内容保留为实施历史，不代表最终状态；整个文本输入计划尚未完成。
