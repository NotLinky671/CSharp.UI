# Panel 容器与 Sidebar 导航交付记录（2026-10-31）

日期：2026-10-31。任务组：[PS-01~PS-05](../task/README.md)（计划
[2026-10-31-panel-sidebar-plan](../plan/2026-10-31-panel-sidebar-plan.md)）。

**状态：代码与文档验收通过（PS-01~PS-05 主审门全过；提交信息见 git 历史）。**

## 一、交付内容

### 1. `Panel`（`src/CSharp.UI/Views/Panel.cs`）

`public Panel(View content)`，只读 `Content`；init `ContentPadding`（默认
`Thickness(12)`，逐边有限非负校验）、`BackgroundColor`/`BorderColor`（nullable，
`null`→当前 `Theme.Surface`/`Theme.Border` 按轮现值）、`BorderThickness`（默认 1，
`0` 即无边框）、`CornerRadius`（默认 8）。

- `ContentPadding + BorderThickness` 内缩参与 Desired/Arrange/
  `GetLayoutFeatures`（`PaddingView` 模式）；颜色/圆角 paint-only——变更只重绘
  不 re-layout；绘制顺序背景→子→边框。
- `GetChildClip` 不变：**圆角为装饰、无 rounded mask**——`ContentPadding=0`
  时子内容可覆盖圆角像素（与既有 `Border` 修饰符同语义，如实声明）。
- 无 `ShowBorder`/header/footer/多子叠放（`ZStack` 已覆盖）。

### 2. `Sidebar<T>`（`src/CSharp.UI/Views/Sidebar.cs`）

`public Sidebar<T>(Binding<T>, IReadOnlyList<SidebarItem<T>>, string? header = null)`；
`SidebarItem<T>(T Value, string? Label = null, bool Disabled = false)`；
`Value`/`Items`/`Header` 只读；`Width` init 默认 200 为**自然偏好**（有限正数
校验，父约束可缩小，`Frame`/`Flex`/`FillWidth` 可覆盖）。

- 数据：`Items` 构造快照、`Value` 按 `EqualityComparer<T>.Default` 唯一
  （重复 `ArgumentException`）、`Label==null` 回退 `Value?.ToString() ?? ""`、
  `T` 可空；`null` 行身份经 Sidebar 局部 non-null sentinel（不改全局
  `ForEach`/`Select`、不以 `ToString` 为 key）。
- 交互：单 focus owner（行非 focusable 不入 Tab）；pointer 同行 Up /
  `Enter`/`Space` 提交写一次（同值重选仍写一次、重复键忽略）；
  `Up`/`Down`/`Home`/`End` 只移 active（跳 disabled、两端停）；`Tab` 不消费；
  `Ctrl`/`Meta`/`Alt` 组合不导航/确认；items 空/全 disabled/控件 disabled/未知
  当前值不写不纠正；指针手势跨 items mutation（Down 后重排/删除/禁用/替换→Up）
  不提交错误项（身份按 `Value` 跟踪）。
- 布局：行高 min 32（大字体增高）、label 单行 ellipsis、header 为文本头不参与
  导航；超高滚动由调用方 `ScrollView` 提供（无内置容器、无公共滚动 API）。
- **ensure-visible**：仅键盘 active 移动触发，经 runtime 最小单 slot 按需
  bring-into-view 请求在 `ProcessPendingWork` 稳定环 `!progressed` 时消费
  （写最近祖先 `ScrollView` offset、沿 `_layoutDirty` 收敛链）；滚轮/平移/
  外部写值不触发（无回弹）；无祖先/空视口 no-op。

### 3. 范围边界（如实记录，不夸大）

**不含/不宣称**：drawer/modal/popup 式 sidebar、Router/Shell/页面栈、内置折叠
断点（用 `Responsive`/`AdaptiveStack` 组合）、图标、动画、分组 header 列表、
多选、层级树、`Panel` 多子叠放、圆角内容裁剪、公共滚动 API。Sidebar 为常驻
列表——**无 expanded/collapse/opening 状态**。

**平台**：本轮已验证面仅 **Windows** 真实窗口/渲染器（1.5 DPI）；macOS/Linux
只承诺源码级一致路径，未实际运行、不虚报结果。IME/剪贴板未做真实 OS 端到端。

**借鉴**：Avalonia / FluentAvalonia / wpfui（均 MIT）仅设计借鉴——不复制代码、
不新增依赖、NOTICE 未变。

## 二、验收事实（全部实测）

### 构建（fresh，`--no-incremental -m:1 -nodeReuse:false`，真实 EXIT）

| 配置 | 结果 | raw |
|---|---|---|
| Debug | 0 警告 0 错误，EXIT=0 | `build-Debug.stdout.txt`（stderr 0B） |
| Release | 0 警告 0 错误，EXIT=0 | `build-Release.stdout.txt`（stderr 0B） |

### 五测试工程 direct-exe 全量（双配置，真实 EXIT=0）

| 工程 | Debug | Release |
|---|---:|---:|
| CSharp.UI.Tests | 822 | 822 |
| CSharp.UI.Text.Tests | 112 | 112 |
| CSharp.UI.Integration.Tests | 56 | 56 |
| CSharp.UI.Markdown.Tests | 50 | 50 |
| CSharp.UI.OpenTK.Tests | 684 | 684 |
| **合计** | **1724** | **1724** |

raw：`test-{Debug,Release}-<suite>.{stdout,stderr}.txt`（stderr 均 0B）。
计数说明：Core 787(旧) + Panel 8 + Sidebar 27 = **822**；合计 1689→1724。

### Gallery 原生实证（真实 Release `GalleryApp` + 生产 `OpenTkHost`，owned-HWND
SendInput、`GL.ReadPixels(FRONT)`、own PID/foreground/WindowFromPoint 校验）

- **self 四组**（good 实施期，main 已实读；producer sha `f045f7d7…`）：
  1024-light 19 / 1024-dark 21 / 320-light 19 / 320-dark 21 项 `^PASS ` 检查，
  RESULT PASS、`UiApplication.Run exit=0`、`host-exited=False`。raw：`native-self/`（**26 PNG**
  + 4 stdout + 冻结 producer 源/csproj/SHA256）。
- **独立复跑**（final gate，本代理执行，原 self producer 原字节）：
  `native-independent/` 1024-dark——21 项 `^PASS ` 检查、RESULT PASS、真实进程
  EXIT=0、7 PNG decode 通过。
- **独立扩展**（copied producer `native-independent-down-producer/`，Program.cs
  sha `ae6f6cee…`）：补 **真实 Down+Space→settings 精确提交** 与
  **Tab→离开、Shift+Tab→回 sidebar owner**——`native-independent-down/`
  23 项 `^PASS ` 检查、RESULT PASS、EXIT=0、8 PNG decode 通过。

覆盖语义：主题经真实 Header 按钮点击（state+ambient 交叉核验）、真实鼠标
Down+Up 精确提交（settings/nav-30）、disabled 行点击不写、键盘 `Up`/`Down`/
`Enter`/`Space`/`End`、`Tab`/`Shift+Tab` 真实焦点序、长列表（140px 视口）
`End` 末行完全在 clip 内、wheel-up 偏移减小无回弹。（`Home` 键未在原生
通道驱动——首/末边界由 Core 永久测试覆盖；setter 恰好一次写由永久测试
Binding 计数断言承载，native 侧只断言精确值。）

## 三、关键修复与取舍（如实记录）

- **行身份**：`Sidebar<int?>` `[null,0]` 场景 null-key 与 index 回退碰撞——
  Sidebar 局部 non-null sentinel 解决，不改全局 `ForEach`/`Select`。
- **ensure-visible 机制**：`DispatchKeyHandled` 无 `ProcessPendingWork`（已核实），
  故采用批准的**单 slot 按需请求**在稳定环 `!progressed` 消费；未采用被证伪的
  "Arrange 内 invalidate"（`RunLayout` 末尾清 dirty）或 `_focus.Validate` 后手工
  `RunLayout`（绕过 responsive/overlay 收敛链）。
- Panel 永久测试经复审去 incidental：字体常量 pin → 固定尺寸 child；删除弱
  ellipsis 测试；`ToList()` 复制 → 单遍历（最终 8/8，首轮 9 为旧快照不混淆）。

## 四、文档

- `docs/guide/api-reference.md`：新增 "面板 Panel 与侧栏导航 Sidebar" 节
  （API 表、行为契约、可编译示例、范围外条款）。
- `README.md`/`README.zh-CN.md`：控件列表补 `Panel`/`Sidebar<T>`；
  设计/任务行更新归档指向。
- 任务索引 `docs/task/README.md`：PS-01~PS-05 状态随主审门更新。

## 五、复核记录

- [计划复核](../return/2026-10-31-panel-sidebar-plan-review.md)（PS-01 契约 + 机制边界）
- [Panel 复核](../return/2026-10-31-panel-review.md)（AC80 + GL 3 图 + 8/8）
- [Sidebar 复核](../return/2026-10-31-sidebar-review.md)（4EE4/AAFF + 27 绿 + GL 16/16）
- [Gallery 复核](../return/2026-10-31-gallery-review.md)（self 四组 + 独立复跑）

## 六、证据文件（raw）

`docs/delivery/2026-10-31-panel-sidebar-delivery-evidence/`：
`native-self/`（4 组 25 PNG + stdout + producer 冻结源+SHA256）、
`native-independent/`（7 PNG）、`native-independent-down/`（8 PNG）、
`native-independent-down-producer/`（扩展 producer 源+csproj）、
`build-{Debug,Release}.{stdout,stderr}.txt`、
`test-{Debug,Release}-*.{stdout,stderr}.txt`。
另：首轮 GL smoke 归档于 `docs/return/2026-10-31-panel-review-evidence/` 与
`docs/return/2026-10-31-sidebar-review-evidence/`。
原生 harness 源位于仓库外 Temp（可复现：同 csproj ProjectReference +
`dotnet build -c Release`），冻结源码快照已随证据归档。

校验文件：`exits.txt`（命令/真实 shell 退出码/check 计数口径）、
`image-integrity.txt`、`doc-link-integrity.txt`（52 PNG 与受影响链接全通过，
由独立复核生成，本记录引用其结果）。

— 验收通过。提交信息见 git 历史。
