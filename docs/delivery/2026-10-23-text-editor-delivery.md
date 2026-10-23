# TextEditor(TE-01~TE-07)交付记录

日期:2026-10-23。实现基线 `bda88f2`(`docs: plan multiline TextEditor implementation`);最终提交号见 Git 历史,本记录随实现一起提交。设计依据 [text-editor-plan](../plan/2026-10-23-text-editor-plan.md) 与 [editing-layout-review](../return/2026-10-23-te01-editing-layout-review.md)、[te03-core-review](../return/2026-10-23-te03-core-review.md)。

## 最终验证(本轮重新串行执行,原始输出全部落盘)

证据目录 `docs/delivery/2026-10-23-text-editor-delivery-evidence/`;其中每个文件(排除 manifest 自身,共 19 份含 `.gitattributes` 与 `SOURCE-SHA256SUMS.txt`)的 SHA-256 **原始字节**散列见 [SHA256SUMS.txt](2026-10-23-text-editor-delivery-evidence/SHA256SUMS.txt)(raw bytes 已复核 19/19;目录 `.gitattributes` 禁换行转换)。[SOURCE-SHA256SUMS.txt](2026-10-23-text-editor-delivery-evidence/SOURCE-SHA256SUMS.txt) 覆盖 `src/`、`samples/`、`tests/` 全部源文件(排除 `bin/`/`obj/`,共 223 份)冻结后原始字节散列——记录最终验证工作树原始字节;不构成前后源码差异证明。

### 构建

命令:`dotnet build CSharp.UI.slnx -c <Debug|Release> --no-incremental --nologo -v minimal -m:1 -nodeReuse:false`,逐条串行、原始 stdout/stderr 重定向到文件后立即取 `$?`。

| 配置 | 警告 | 错误 | exit | 日志 |
|---|---|---|---|---|
| Debug | 0 | 0 | 0 | build-Debug.log |
| Release | 0 | 0 | 0 | build-Release.log |

### 正式测试(进程内 xUnit runner,`dotnet run --project tests/<p> --no-build -c <cfg>`)

| 套件 | Debug | Release |
|---|---|---|
| CSharp.UI.Tests | 593/593, exit 0 | 593/593, exit 0 |
| CSharp.UI.Text.Tests | 85/85, exit 0 | 85/85, exit 0 |
| CSharp.UI.Integration.Tests | 53/53, exit 0 | 53/53, exit 0 |
| CSharp.UI.OpenTK.Tests | 678/678, exit 0 | 678/678, exit 0 |
| 合计 | **1409** | **1409** |

其中含本轮新增的 `TextEditor*` 单元/集成套件、修复后的 `TextEditorPreeditRenderingGpuTests`(GL 真实 ReadPixels)与 `GalleryBreakpointInputTests` 4 项滚入视口修复。

### 示例冒烟(`CSHARPUI_SMOKE_FRAMES=5`,--no-build,6 次串行)

| 示例 × 配置 | 帧 | 逻辑视口 | framebuffer | DPI | GL | exit |
|---|---|---|---|---|---|---|
| Counter Debug / Release | 5 | 480×320 | 720×480 | 1.5 | 3.3.0 Intel | 0 / 0 |
| TodoList Debug / Release | 5 | 760×480 | 1140×720 | 1.5 | 3.3.0 Intel | 0 / 0 |
| ControlGallery Debug / Release | 5 | 900×700 | 1350×1050 | 1.5 | 3.3.0 Intel | 0 / 0 |

冒烟结束后无示例进程残留。

## Windows 真实输入法验收(第三轮修正跑;原始证据见 [te06-real-ime-evidence](../return/2026-10-23-te06-real-ime-evidence/),交付侧仅留指引 [te06-real-ime-README](2026-10-23-text-editor-delivery-evidence/te06-real-ime-README.md))


源码差异说明:经实施变更记录审核,真 IME 三轮验收之后的改动限于 good 最后一轮小修(stub `LineIndexForStop`/`legacyCaretXs` 恢复 + `MaxScrollY` 未用参数清理 + `GalleryBreakpointInputTests` 测试维护),无新生产语义变更;[SOURCE-SHA256SUMS.txt](2026-10-23-text-editor-delivery-evidence/SOURCE-SHA256SUMS.txt) 单独用于当前源码识别,不声称前后 hash 对比。

`events.jsonl` 真实计数:28 项 `check` 记录,28 pass、0 fail;`outcome=PASS`、app-exit=0。输入法身份非按参数猜:`ITfInputProcessorProfileMgr.GetActiveProfile` 回读 CLSID `81D4E9C9-…` / profile `FA550B04-…`(微软拼音,FORPROCESS 激活、`finally` 恢复、HIMC 会话保存/恢复、不改系统默认输入法)。


覆盖场景:profile 身份;真实点击 + `nihao` 预编辑 `'ni'hao'` 上屏且 0 绑定写;Space 单写提交 byte-exact `AA\nB你好B\nCC`;Esc 取消 0 写;**真实拖拽跨行选区 `[1,5)`='A\nBB' → 提交 `A你好\nCC` byte-exact 单写**(文本替换覆盖换行);组合中外部 CRLF 写取消 + 新 epoch + `zaijian` 重提交;deactivate/restore 0 写 + `huifu` 重提交;SetWindowPos 中组合 → `GeometryPublishCount` 59→61;组合中真实 `WM_MOUSEWHEEL` → `WheelEventCount` 0→2、`ScrollY` 0→96 持久、锚点 `(132,223)`→`(132,79)`(Δy=−144px=−96×DPI1.5,即 `anchor=TextOrigin−ScrollY` 重发、publish 70→71)、0 写、组合保持。

driver 路径说明:文本键经 `SendInput`(真实键);选区经真实指针拖拽(Shift+Arrow 被 mspinyin 中英切换吞掉,不用);滚轮经 `PostMessage(WM_MOUSEWHEEL)`(消息级注入,同 wndproc→GLFW→`HandleWheel`→`DispatchWheel` 路径——`SendInput` 滚轮在本环境反复投递为 0,adapter 计数证明,已记录,非 HID 级)。

### 未证明项(保持原样,不由验收动作补证)

- **单 DPI 真机**:全部真机证据在 150% DPI、单窗口、单 profile 会话;多 DPI/多显示器真机 IME 没有证据。多 DPI 仅离屏测试层覆盖。
- **macOS / Linux**:无真机输入法证据;两平台仅 `committed-only` 文本输入(`NoTextInputService` + `DescribeCurrentPlatformReason`),不宣称三平台完整输入法。
- **TSF 内部路径**:`UseTsfInputBridge` 仅由既有 `TsfHostIntegrationTests`/`TsfCancellationTests`/`TsfSessionLifetimeTests`/`NativeCompositionOwnerTests`(OpenTK.Tests 61 项,双配置过)覆盖 TextField;没有多行 TextEditor 的 TSF 真机验收。
- **原生分段 Result**:微软拼音未产生多段原生 Result 消息;分段历史由 `CompositionContracts`/`TextEditHistory`/`TextInputServiceSession` 协议层测试覆盖,无特指 TIP 证据。
- **滚轮为消息级注入**而非真实 HID 滚轮输入;同一消息路径但触发级别不同。

## 已交付范围

- `src/CSharp.UI/Views/TextEditor.cs`(新)、`ITextEditSessionOwner.cs`(新)、`TextCompositionClient`(由 `TextFieldCompositionClient` 改名共用)等;`TextField` 走同一组合客户端契约。
- `tests/` 新增 `TextEditorTests`、`TextEditorCompositionTests`、`TextEditorEditingLayoutTests`(×2)、`CompositionCharGeometrySnapshotTests`、`TextEditorPreeditRenderingGpuTests`;既有套件改名/接口同步。
- 控件展示示例 `Program.cs` 更新(TextEditor 相关展示)。
- 文档:task/return/guide/README 状态同步见下。

## 文档状态更新

- `docs/task/06-graphics-and-real-ime.md`:真实 IMM 段已验收 ☑(证据 `docs/return/2026-10-23-te06-real-ime-evidence/`);图形段随 good 修复已全绿(OpenTK 678/678 双配置)。
- `docs/task/README.md`:TE-01~TE-07 状态同步全部 ☑。
- `docs/return/README.md`:已关闭条目按实际链接更新。
- `README.md` / `docs/guide/platform-notes.md` / `testing.md` / `api-reference.md`:TE-06 之前的 "pending" 表述改为已验证事实并链接本交付。
- `docs/plan/2026-10-23-text-editor-plan.md` 状态:已实施,见本交付。

## 提交

待主审 gate 后由实现者英文提交并推送(本记录与实现同一提交)。
