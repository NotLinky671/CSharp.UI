# Popup 覆盖层与 Select 控件交付记录（2026-10-30）

日期：2026-10-30。任务组：[P-01~P-07](../archive/2026-10-31-popup-select-tasks/README.md)（计划 [2026-10-29-popup-and-select-plan](../plan/2026-10-29-popup-and-select-plan.md)）。

**状态：验收通过（代码与文档收口完成；提交信息见 git 历史）。** 双配置 Debug/Release 各 1689 项、0 警告 0 错误；原生 900 47-case、窄宽 320/1024×light/dark 4 run、外层滚动跟随/裁出关闭均实证，raw 见 `docs/delivery/2026-10-30-popup-select-delivery-evidence/`。

## 一、交付内容（源码冻结；提交信息见 git 历史）

### 1. 根级 overlay

- `UiRuntime` 内部固定 `UiRootLayerView`（子树 `[userRoot, OverlayHost]`，经 `Reconciler.MountRoot` 一次挂载）；`UiRuntime.Root` 仍返回**用户根节点**（`userNode`）。
- **兼容事实（如实记录）**：用户根的 `Parent` 现为内部层节点（非 `null`）、`Depth` 相应 +1；`RootLayer` `IsHitTestVisible=true` 且显式 `HitTestSelf=false`；`OverlayHost` `IsHitTestVisible=(entry!=null || 吞并手势进行中)`、显式 `HitTestSelf=false`。
- `OverlayManager`：单活动 entry（`AnchorNode`/`PopupNode`/`SavedFocus`），`Show` 前 `CloseAll()`；嵌套 `Show` 请求被拒绝并输出运行时诊断，外层保持。

### 2. `Popup`

`public Popup(View anchor, View content, Binding<bool> isOpen, PopupPlacement preference = Auto)`；init `Margin`（有限非负，默认 4）、`MaxHeight`（正或 +∞）、`CloseOnOutsidePress`/`CloseOnEscape`（默认 true）。

- **内容挂根级 overlay**：真实 `ViewNode` 子树（完整 Measure/Arrange/Emit/HitTest/焦点），不受中间祖先 clip；`PopupSurfaceView` 经内部 `PopupContentScope : Component` 提供 Component 归属点——portal 内**缓存 `Binding`**（如字段缓存的 `ToBinding()`）对外部 `State` 变化能正常驱动 `NeedsDraw`/文本更新，卸载后依赖释放（`PopupViewTests.C14` 实证）。
- **环境**：弹层内容继承锚**当前** `Environment`（Theme/Font/Disabled/Opacity 每轮读现值，非快照）；`Popup` 不自动画内容底色，由调用方按主题提供 `Background`/`Border`。
- **placement 两阶段有界**：主树布局 → overlay measure（约束 `viewport.Inset(Margin)`）→ 纯计算选 side（`Auto` 两向 flip；`Above`/`Below` 强制方向不翻转）→ 最终尺寸与 Desired 不符时有界二次 measure 至多一次。`anchorVisible` 仅用于锚可见资格（空→关、部分→保持），与 `popupBounds` 独立。
- **`IsOpen` 双向纪律**：资格门（锚 mounted/enabled/anchorVisible 非空/popupBounds 非空/窗口激活）；owner open-request 状态机——内部关闭（外点/Esc/锚失效/disabled/失活/挤占）写 `false` 一次后记“待 false→true 边沿”，恒 `true` 不再重开、无自激；`IsOpen` binding 实例更换不重置已观察状态；读取走 `Binding.Read(node)` 登记依赖。
- **输入**：外点关闭并吞并整次 gesture（锚子树豁免）；Esc 复用 `DispatchKey` 顺序——**组合期间焦点编辑控件消费该 Esc 按键**（组合取消由输入法/平台负责，框架不自 Cancel），**平台显式取消生效后下一次 Esc 才关弹层**；未消费时关 + 焦点恢复（SavedFocus→锚子树首 focusable→null）；外点/外写/替换/失活/挤占关闭不主动设焦；Up(同点) 打开，opening gesture 完整落 trigger。
- **非 modal**：无遮罩、无 focus trap；选项行**不是 focusable**（不入 `GetFocusOrder`/Tab 序），`BuildOrder` DFS 只收 focusable 节点——popup 内 focusable 控件 Tab 自由进出。

### 3. `Select<T>`

`public Select<T>(Binding<T> value, IReadOnlyList<SelectItem<T>> items, string? placeholder = null)`；`SelectItem<T>(T Value, string? Label = null, bool Disabled = false)`；init `MaxPopupHeight`（有限正数，默认 240，超限内部滚动）。

- `Items` 构造时快照且 `Value` 唯一（重复值 `ArgumentException`）；值在但项 disabled 仍显示该项 Label；items 空/全 disabled/Select disabled → 不打开；当前值不在列表 → 显示 placeholder 且不写回；`T` 可空（`Select<string?>` 合法）。
- **提交**：pointer Up 同行提交、`Enter`/`Space` 提交 active、`Up`/`Down` 跳过 disabled、`Home`/`End` 首末可用项；`Select.Value` 只在选后写一次（同值重选仍写一次）；打开/关闭/`Escape`/`Tab`/外点/失活不写。
- **键盘**：`Escape` 取消不写；`Tab`/`Shift+Tab` 先关闭再按下/上一个焦点；重复 `Enter`/`Space` 忽略，重复方向键继续导航；`Select` 不把 `Ctrl`/`Meta` 组合键当导航/确认。
- **焦点**：打开时焦点恒在 trigger；选项行**不是 focusable**，不入 `GetFocusOrder`/Tab 顺序。
- **长列表**：`MaxPopupHeight` 限高 + 内部 `ScrollView`；首开 ensure-visible 当前项/重排/resize 有界重试（`PendingEnsure` 代际）。

## 二、示例

`PopupSelectDemo`（`Section("弹出与选择")`）：`Select<string?>`（UseState 绑定+回显）、disabled `Select`、30 项 `MaxPopupHeight=120` 长列表（截断/滚动）、内层 `ScrollView` 内锚定（滚动跟随/裁出关闭/底部向上翻）、通用 `Popup`（`TextField`+`关闭`，外层 `State` 回显做普通绑定演示——**不覆盖缓存 Binding 场景**；portal 缓存 Binding 反应性由 `PopupViewTests.C14` 专测证明）。静态数据 `private static readonly` 不可变；弹层底色按传入 `Theme` 提供。

## 三、已冻结的双配置验收事实（源码冻结）

`dotnet build CSharp.UI.slnx`：**Debug 与 Release 各 0 警告、0 错误**（修复后 raw：`post-esc-fix-build-{debug,release}.{stdout,stderr}.txt`）。五工程 fresh 全绿、全部 `EXIT=0`（修复后 raw：`post-esc-fix-{debug,release}-*.{stdout,stderr}.txt` + `post-esc-fix-exits.txt`）：

| 工程 | Total | Failed |
|---|---:|---:|
| CSharp.UI.Tests | 787 | 0 |
| CSharp.UI.Integration.Tests | 56 | 0 |
| CSharp.UI.Markdown.Tests | 50 | 0 |
| CSharp.UI.Text.Tests | 112 | 0 |
| CSharp.UI.OpenTK.Tests | 684 | 0 |
| **合计** | **1689** | **0** |

计数说明：新 host Esc-Down/Up 回归使 `CSharp.UI.OpenTK.Tests` 由 683→684，合计 1688→1689。**修复前的 Debug 1688 与 GL/OS 证据为旧源码快照**（历史 raw 未删），当前生效计数为修复后 **1689**。

真实窗口/渲染器实证（真实 Release `GalleryApp` + 生产 `OpenTkHost`，仅 `Started`/`FrameRendered` 观察回调包装 + `GL.ReadPixels(FRONT)`，**非原始 ControlGallery.exe 本身，也非旧轮未通过验收的 GDI 采集通道**）：

- **原生 900 47-case**（fast 独立执行）：`native-independent.stdout.txt` 47 PASS 0 FAIL，含 Esc 关闭后 `app-alive-after-esc` 与 `host-exited=False`。
- **窄宽 320/1024 × light/dark 4 run**：`native-widths/`（requested-width、PixelScale 1.5、fb `W×1.5`、popup 锚定 + `[4,W-4]` + 视口、`标准模式→紧凑模式（窄屏）`、disabled 不 open/不写、dark 经真实 Header 主题点击）。
- **外层滚动跟随/裁出关闭**（P-06 E1/E4）：`native-outer/`——`outer off 2112→2124`、`anchorΔY==popupΔY=-12`（跟随）；滚出根视口 `anchor top=-45` → overlay 关、binding 不变、host 活。

## 四、关键真实修复与删除（如实记录，不夸大）

- **portal 缓存 Binding**：before `p06-portal EXIT=1`（同测试 FAIL）→ 引入 `PopupContentScope` 后 `EXIT=0` PASS——真实反应性修复。
- **80px Markdown Copy**：80px 视口下 copy 按钮 `Frame.Right` 越界——baseline（249ca96，独立 `independent-baseline-class.stdout`）实测 `right = 51.883 + 32 = 83.883 > 80`；本轮自建 80px 探针（`p06-copy-before`）实测 `right 112.4 > 80`（探针用更长标签，两者各自 raw 保留，不混数值）。→ `.Flex(0,1,min0).Clip()` 后 `EXIT=0` PASS（可收缩裁剪仍可点击）。**非 Popup 引入**。
- **结构 pin 测试删除**：`GalleryNestedScrollTests.控件展示的滚动容器…`（全树 0 `ClipView` + 硬 pin 结构）按“incidental/implementation 测试 DELETE”规范删除（684→683）；其真实消费者断言（嵌套滚轮 48px/外层不动/裁剪/DPI 一致）保留并全绿——**非产品回归修复，是删除过度结构断言**。
- `OverlayRootTests.A4_PopupUsesAnchorCurrentEnvironment_NotSnapshot` 曾一轮 FAIL：锚 Theme 改 Dark 后内容节点仍拿到 Light——新引入的 `PopupContentScope` 节点缓存命中只刷新自身 `Environment`，锚的新环境不向下传播。源码修：`PopupSurfaceView.Measure` 对 scope 用 `context.MeasureFresh(child, …)`（surface 每轮强制重测 + scope 强制访问），把锚当前 env 传播到内容节点；**改源码未改断言**，随 post2 Debug 787 全绿。

- **宿主 Esc-Down/Up 误退（真实 OS 窗口验收发现的真 bug）**：原生 900 窗口里，popup 打开时一次真实 Esc 手势（SendInput Down+Up）关闭了 popup 却**同时退出应用**——`UiApplication.Run exit=0`、`host-exited=True` 出现在“已消费 Esc”之后（before raw：`native-esc-before/native-esc-before-stdout.txt`，含当时 RESULT PASS 但 host-exited=True 的假绿记录）。根因在 `OpenTkInputAdapter.DeliverCapturedCore`：退出快捷键判定 `(!handled && !ConsumedByIme && (Esc|Ctrl+Q))` 未限定 `KeyEventKind.Down`——Esc-Down 被 overlay 消费关弹层（handled=true），同手势 Esc-Up 到达时 popup 已无 → handled=false → 误触 `ExitOnEscape`/`RequestExit`。最小修复：该退出条件加 `key.Kind == KeyEventKind.Down`（Esc 与 Ctrl/Cmd+Q 共用），不引入新状态/时间窗/抑制周期，保留 handled/IME 捕获标志/FIFO 路径。回归测试 `OpenTkInputAdapterTests.Esc完整DownUp关闭popup不退出_第二次无popupEsc才退出`（真实 `UiRuntime`+`Popup(Binding<bool>)`+adapter 完整 Down/Up，非 mock）：第一对关 popup+`IsOpen=false`+`ExitRequestCount==0`；第二对无 popup `==1`；裸 `KeyUp` 不再退。修复后原生 900 复验 `app-alive-after-esc` PASS、`host-exited=False`，其后仅 harness 自身 `RequestExit(0)` 得 `exit=0`。

## 五、功能范围边界（F4）

**不含 / 不宣称**：可键入过滤 ComboBox、多选、typeahead、分组头、item 自定义模板、`Alt+Down`；modal/focus trap/dialog/context menu/tooltip/多层 popup/动画/虚拟化/公共滚动 API/多窗口/系统无障碍桥接；popup 内嵌 popup（嵌套 open 拒绝+诊断）。

**契约摘要（本轮交付的明确取舍）**：单活动 popup（`Show` 先 `CloseAll`）；非 modal、无遮罩无 focus trap；外点 = dismiss + **吞并整次 gesture**（锚子树豁免）；Esc 复用 `DispatchKey`——**仅未消费的 `KeyEventKind.Down`** 才落到宿主 `ExitOnEscape`/Ctrl(Cmd)+Q 退出（本轮修复；同手势 `KeyUp` 不再误退）；`IsOpen` 双向纪律内部关闭写 `false` 一次。

**平台**：框架目标 Windows/macOS/Linux；**本轮已验证面仅 Windows 真实窗口/渲染器路径**——macOS/Linux 只承诺源码级一致路径，**未实际运行**、不虚报结果。IME 组合优先级与剪贴板经 runtime 注入服务接口实测，**未做真实 OS IME 或系统剪贴板端到端验收**（框架既有 IMM/GLFW 生产路径保留）。

**来源边界**：正向实机证据来自真实 Release `GalleryApp` + 生产 `OpenTkHost`（`UiApplication.Run`，仅附加 `Started`/`FrameRendered` 观察回调）+ `GL.ReadPixels(FRONT)`——**不是原始 `ControlGallery.exe` 本身，也不是旧轮未通过验收的 GDI 采集通道**；legacy GDI 轮 FAIL 与修复前 `native-esc-before`（RESULT PASS 但 `host-exited=True`）作为历史失败/假绿事实原样保留，不掩盖、不改成 PASS。commit SHA：**见 git 历史**（F5 单次英文 commit/push 由主审授权后执行，本记录不提前写 SHA）。

## 六、证据文件（raw）

本目录（`docs/delivery/2026-10-30-popup-select-delivery-evidence/`）：

- **独立 native 900**（fast 独立执行）：`native-independent.stdout.txt` + `native-independent/`（18 PNG + popup/longlist tree）——修复后 `app-alive-after-esc` PASS、`host-exited=False`、其后 harness 自身 `RequestExit(0)` 得 exit 0，47 PASS 0 FAIL。
- **900 生产者快照**：`native-observed-producer/Program.cs` + `native-observed.csproj`（`UiApplicationOptions.Width=900/Height=700`、`GalleryApp`、默认生产 services、owned-HWND SendInput 驱动）。原工程位于 `Temp/popup-select-acceptance/native-observed/`（仓库外 Temp，可复现：同 ProjectReference + `dotnet build -c Release`）。
- **修复前后对照**：`native-esc-before/native-esc-before-stdout.txt` + `native-16-esc-closed.png` —— 修复前同 gesture：popup 关、`UiApplication.Run exit=0`、`host-exited=True`（“已消费 Esc 仍退出”的真 bug 原始字节，未改）。修复后见 `native-independent.stdout.txt`（`app-alive-after-esc` PASS）。
- **窄宽 320/1024 × light/dark 4 run**：`native-widths/{320-light,320-dark,1024-light,1024-dark}/driver.stdout.txt` + 各 `native-*.png`；driver `native-widths/Program.cs` + `native-widths.csproj`（冻结 hash 见其 README）。每 run：requested-width 命中、PixelScale 1.5、framebuffer `W×1.5`（480/1536×1050）、popup 锚定且 `Frame` 在 `[4,W-4]` 与视口内、键盘 Down+Enter 提交 `标准模式→紧凑模式（窄屏）`、disabled select 真实点击不 open/不写、dark 经真实 Header 主题按钮（SendInput 非 State 写）、`host-exited=False`、exit=0。
- **打开态外层滚动（P-06 E1/E4 补证）**：`native-outer/driver.stdout.txt` + 4 PNG；driver `native-outer/Program.cs`（900×700 @1.5）。小负 delta 在 `page.Right-20` 使 `outer off 2112→2124`、`anchorΔY==popupΔY=-12`（非零相等）、popup 仍开、binding 不变（**外层滚动时跟随**）；继续 wheel 至 anchor 完全离根视口（`top=-45`，与 `[0,H]` 不相交）则 `overlay-closed-on-clipout`、binding 不变、`host-alive`（**滚出即关闭**）。至此外层与内层（47-case inner `0→24 随动、24→216 裁空关`）各有明确原生证据。
- 既有：`docs/return/2026-10-30-popup-select-p06-portal-evidence/`（`p07-debug-*`、`p06-portal-*`、`p06-copy-*`、`p06-post*`）；GL smoke `gl-*.png`、os `os/`、`post-esc-fix-*`（修复后 Debug/Release 5-suite raw）。历史失败 raw 未改。

— 代码与文档验收通过。历史失败 raw（legacy GDI、native-esc-before 假绿）原样保留；提交信息见 git 历史。
