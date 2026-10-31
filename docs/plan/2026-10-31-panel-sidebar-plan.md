# Panel 容器与 Sidebar 导航实施计划

日期：2026-10-31。

状态：**验收通过——PS-01~PS-05 全部主审门过；交付记录 [2026-10-31-panel-sidebar-delivery](../delivery/2026-10-31-panel-sidebar-delivery.md)；提交信息见 git 历史。**

背景：Popup 覆盖层与 `Select<T>` 计划（[2026-10-29-popup-and-select-plan](2026-10-29-popup-and-select-plan.md)）已验收交付（[delivery](../delivery/2026-10-30-popup-select-delivery.md)），任务组归档于 [docs/archive/2026-10-31-popup-select-tasks/](../archive/2026-10-31-popup-select-tasks/README.md)。用户提出下一阶段能力需求："panel 和 sidebar"。本计划冻结两个公开控件的 API 与行为契约，按既有任务/审核/交付流程推进。

## 模块所有权

- **产品代码与测试（src/tests/samples）**：good 实现，含 PS-02~PS-04 全部产品面改动。
- **docs 规划与流程文件（plan/task/return/delivery/README 指向）**：本代理（panel-sidebar-plan）维护。
- **主审（main agent）**：每个任务独立 read 复审，通过后开下一依赖任务；PS-05 通过后单次英文 commit/push。
- 源码未完成前**不创建** delivery 文件，不虚报进度。

## §1 目标与范围

### 包含

- **`Panel`**：单子装饰容器——内边距、背景色、边框、圆角；把当前样例里 `FrameView + VStack + Padding + Border + Background` 手工拼装模式（`samples/CSharp.UI.Samples.ControlGallery/NestedResponsiveExample.cs:54` 的 `Panel()` helper）收敛为框架控件。
- **`Sidebar<T>`**：纵向选择导航列表——绑定当前值、项集合（值/标签/disabled）、可选 header、键盘与指针提交、active 项键盘导航与 ensure-visible；复用 `Select<T>` 的单选纪律但不走 popup/overlay。
- Gallery 真实集成演示（HStack/Flex/AdaptiveStack 应用侧布局组合），窄宽 320/1024、light/dark、1.5 DPI 实证。
- 双 config 构建 + 五测试套件 + 文档 + 交付记录。

### 不包含（范围外，不实现不 stub）

- drawer/modal/popup 式 sidebar（`OverlayManager` 本轮不改动；sidebar 是主树常驻布局控件）。
- Router/Shell/Frame 导航框架、面包屑、页面栈。
- 内置折叠/断点逻辑（窄宽分支由调用方用既有 `Responsive`/`AdaptiveStack` 自行组合）。
- 图标/动画/分组 section header 列表/多选/层级树。
- Panel 的 header/footer 槽位（调用方 `VStack` 组合）、`ShowBorder` 开关（用 `BorderThickness=0` 表达）。
- 多子 Panel 叠放语义（既有 `ZStack` 已覆盖）。
- 公共滚动 API（`Sidebar` 超高内容滚动交给调用方 `ScrollView`，仅内部 ensure-visible 复用）。

## §2 现有基础

- **视图/修饰符**：`View`（`src/CSharp.UI/Views/View.cs`）；`PaddingView`/`FrameView`/`BackgroundView`/`BorderView`/`CornerRadiusView`/`ClipView`/`DisabledView`/`ThemeView`（`Views/Modifiers.cs:44,88,265,326,375,419,458,483`）；`VStack`/`HStack`/`ZStack`（`Views/Stacks.cs:16,71,127`）；`FlexView`/`FillWidthView`（`Views/Flex.cs:30,115`）；`AdaptiveStack`（`Views/AdaptiveStack.cs:20`）；`Responsive`（`Views/Responsive.cs:83`）；`ScrollView`（`Views/ScrollView.cs`）。
- **选择纪律参照**：`Select<T>`（`Views/Select.cs`）——items 构造快照、`Value` 唯一校验 `ArgumentException`、`Label==null` 回退 `Value?.ToString() ?? ""`、`T` 可空、disabled 项跳过、同值重选写一次、焦点恒在 owner（选项行非 focusable）、`Binding.Read(node)` 依赖登记、items 替换按 equality 重定位 active。
- **主题**：`ResolvedTheme` token 族（Button/Toggle/Select 同一套 hover/pressed/focused/disabled/accent/surface/border 颜色）。
- **测试基座**：`src/CSharp.UI/Testing/TestDoubles.cs` 无窗口布局/输入替身；`tests/CSharp.UI.Tests/` 消费者可观察断言规范（不 pin 内部字段/常量字面）。
- **原生实证 harness**：`docs/delivery/2026-10-30-popup-select-delivery-evidence/` 下 `native-observed-producer/`、`native-widths/` 冻结模板（真实 Release `GalleryApp` + 生产 `OpenTkHost`、owned-HWND SendInput、`GL.ReadPixels(FRONT)`）。

## §3 开源借鉴（设计借鉴，不复制、不新增依赖、不动 NOTICE）

| 项目 | 许可 | 借鉴点 | 取舍 |
|---|---|---|---|
| [Avalonia](https://github.com/AvaloniaUI/Avalonia) | MIT | `Panel` 多子容器基类语义 | 不采用多子叠放——`ZStack` 已等价；本 `Panel` 为单子装饰容器 |
| [FluentAvalonia](https://github.com/amwx/FluentAvalonia) | MIT | `NavigationView` 选中项模型（selected item + item disabled + 单选提交） | 仅借 selection 核心契约，不引入其 NavigationView 的 pane/drawer/分级 |
| [wpfui](https://github.com/lepoco/wpfui) | MIT | 导航项外观组织参考 | 不做 `Frame` 导航宿主/页面路由 |

good 已实际访问上述仓库取证；本轮不复制代码、不新增第三方依赖、**不新增/不更新 NOTICE**。如实现中实际引入外部代码须先报主审。

## §4 `Panel` API 与契约

```csharp
public sealed class Panel : View
{
    public Panel(View content);
    public View Content { get; }                    // 只读
    public Thickness ContentPadding { get; init; } = new(12);
    public Color? BackgroundColor { get; init; }    // null → 当前 Theme.Surface
    public Color? BorderColor { get; init; }        // null → 当前 Theme.Border
    public double BorderThickness { get; init; } = 1;
    public double CornerRadius { get; init; } = 8;
}
```

- **单子容器**：measure/arrange 将子内容内缩 `ContentPadding + BorderThickness`（仅 `ContentPadding`/`BorderThickness` 参与布局）；`GetLayoutFeatures` 复用 `PaddingView` 既有模式。
- **绘制顺序**：背景 → 子内容 → 边框；`CornerRadius`/`BackgroundColor`/`BorderColor` 为 paint-only——变更只触发重绘、不触发 re-layout。
- **无新增圆角内容裁剪**（`GetChildClip` 不变）：当前 renderer 仅矩形 clip；圆角为装饰语义，与既有 `Border`/`CornerRadius` 修饰符同语义，不做 rounded mask。遵守父 clip 与调用方显式 `.Clip()`；**不假称** `ContentPadding=0` 时子内容不会覆盖圆角视觉。
- **无 `ShowBorder` 开关**：无边框 = `BorderThickness = 0`。
- **无 header/footer 槽位**：标题/分组由调用方 `VStack` 组合。
- 主题色按轮读现值（`BackgroundColor`/`BorderColor` 为 null 时取当前 `Theme` 值），disabled/opacity 经既有环境继承路径。

## §5 `Sidebar<T>` API 与契约

```csharp
public sealed class Sidebar<T> : View
{
    public Sidebar(Binding<T> value, IReadOnlyList<SidebarItem<T>> items,
                   string? header = null);
    public Binding<T> Value { get; }
    public IReadOnlyList<SidebarItem<T>> Items { get; }
    public string? Header { get; }
    public double Width { get; init; } = 200;   // 自然偏好宽度，非硬宽
}
public sealed record SidebarItem<T>(T Value, string? Label = null, bool Disabled = false);
```

### 数据纪律（与 `Select<T>` 同契约）

- `items` 构造时快照；`Value` 按 `EqualityComparer<T>.Default` 唯一，重复 → `ArgumentException`。
- `Label == null` → `Value?.ToString() ?? ""`；`T` 可空合法（`Sidebar<string?>`、`SidebarItem<T>(null)`）。
- `items` 替换/重排：行身份按 `Value`（stable-by-value——可观察语义：active/选中高亮
  与提交跟随同一 `Value`，不强 pin 内部 VNode 实例），不留 stale index；
  `Binding` 实例更换后写当前绑定。**null/重复标签边界**：`Sidebar<int?>` 的
  `[null, 0]` 中 `null` 行身份不得与 `Value=0` 的 index 回退碰撞（实现可用
  Sidebar 局部非 null null-key sentinel，不改全局 `ForEach`/`Select` 语义）；
  不可用 `ToString` 作 key（不同值同 ToString 不冲突）。**指针手势跨 items
  变更**：某行 `PointerEventKind.Down` 按下后 items 重排/删除/禁用/替换，
  再 `PointerEventKind.Up` 释放——禁止写回另一 `Value`（命中行身份随 `Value`
  跟踪）。键盘 active 移动按 `Value` 保持，另行断言。
- items 空 / 全 disabled / Sidebar 整体 disabled：不提交不写；禁用态即时生效。

### 布局

- `Width`（init，默认 200）为**自然偏好宽度**：父约束更窄时可缩小；可被 `Frame`/`Flex`/`FillWidth` 覆盖。
- 自然高度 = header + 各行高度之和；label 单行 ellipsis；行高 min 32 逻辑像素，字号更大时可增。
- header 为可选文本头，非选中行；不参与导航/提交。

### 交互

- **单 focus owner**：`Sidebar` 控件本体 `IsFocusable`；选项行不是 focusable、不入 Tab 顺序（与 `Select` 选项行同纪律）。
- **提交**：pointer Up 同行提交写绑定一次；`Enter`/`Space` 提交 active 项写一次；同值重选仍写一次。
- **导航**：`Up`/`Down` 移动 active（跳过 disabled，两端停不环绕）；`Home`/`End` 首/末非 disabled 项——只 move active，不提交。
- **`Tab`/`Shift+Tab` 不消费**，正常焦点遍历。
- `Ctrl`/`Meta`/`Alt` 组合不当导航/确认键。重复 `Enter`/`Space` 不重复提交；重复方向键可继续移动。
- **写入纪律**：`Value` 只在提交路径写一次；导航、焦点进出、失焦、外部绑定变化均不写。

### 滚动与裁剪

- 超高内容滚动**交给调用方 `ScrollView`**，不新增公共滚动 API、不内置滚动容器。
- **ensure-visible 行为契约（实现挂点在 PS-03 由 good 用既有 runtime 安全边界证明，不作 stub/未来补）**：
  - 仅**键盘 active 移动**（`Up`/`Down`/`Home`/`End`）触发；用户滚轮、纯内容平移、外部绑定变化**不**重新 ensure，避免滚动回弹。
  - 作用对象为**最近祖先 `ScrollView`** 内目标行的可见性（行已完全在视口内则不动）。
  - 无 `ScrollView` 祖先或视口为空 → 安全 no-op。
  - 有限兑现：单次有界请求/帧内收敛，不忙等、不新增 poll/timer、不暴露公共滚动 API。
  - **批准的最小内部请求机制**：Runtime 单 slot 按需 bring-into-view 请求——仅键盘
    active 移动挂目标；在既有 `ProcessPendingWork` 稳定 while 内、仅当
    `!progressed`（布局/响应式/overlay 已收敛）时消费一次：清 slot、target 合法
    且 delta 非 0 → 写最近祖先 `ScrollView` offset 并返回 `progressed=true`，沿
    既有 `_layoutDirty` 收敛链重排；focus/validate/hover/text-geometry 在最终
    frame 上运行。无请求时单 null check、无全树扫描、无 poll/timer、无公共 API。
    pending 目标可按 owner + 当前描述/active `Value` 解析最新 row；unmounted/
    disabled/失焦自然 drop，不持续重试；消费后二次布局不得再次请求（无回弹）。
  - **已核实事实**：`UiRuntime` key dispatch（`UiRuntime.cs` 约 :870-880）路径上
    **没有** `ProcessPendingWork`（`InputRouter.cs` 约 :365-394 已读）——不能
    假设 key 前必有最新 layout。**禁止**：Arrange 内对祖先 invalidate
    （`RunLayout` 末尾清 dirty 吃掉）、`_focus.Validate` 后手工 `RunLayout`
    绕过 responsive/overlay 收敛链。具体方法名/字段由 good 证明后报主审，
    本契约只冻结行为与批准方向，不 pin 私有名。
- 视觉遵守父 clip 与显式 `.Clip()`。

## §6 Gallery 集成与应用布局

- `samples/CSharp.UI.Samples.ControlGallery/`：新 Demo 文件（沿用 `PopupSelectDemo.cs`/`MarkdownDemo.cs`/`NestedResponsiveExample.cs` 独立文件 + `Program.cs` `Section(...)` 接入模式）。
- 页面布局用 `HStack`/`Flex`/`AdaptiveStack` 组合 sidebar + 内容区；持久 `State` 外持于页面组件（沿用 `NestedResponsiveExample` 外层状态模式），不新建 Router/Shell。
- 窄宽分支由 `Responsive`/`AdaptiveStack` 既有能力表达，不做内置折叠。
- 面板底色/边框按传入 `Theme` 提供，与 Popup demo 同一纪律。

## §7 测试矩阵（永久测试只断言消费者可观察行为）

- **Panel**（低影响，不堆测试）：measure/arrange 内缩正确（padding+border 计入 Desired 与 child Frame）；`Thickness=0` 无边框路径；null 色取当前 Theme；换 Theme 后颜色现值。布局/渲染补充 throwaway smoke 证实（PNG raw 存 evidence，不转永久截图断言）。
- **Sidebar**（消费者边界永久测试）：常驻列表渲染行数与 ellipsis；pointer Up 提交一次/同值重选一次；Enter/Space 提交、Up/Down/Home/End 导航跳 disabled 不提交、Tab 不消费；disabled/空/全 disabled 不写；未知值无高亮不纠正；items 替换按 Value 保 active；绑定实例更换写当前；外部 State 驱动重绘；行非 focusable 不入 Tab 序；Theme 现值；指针手势跨 items 变更不提交错项（见 §5 数据纪律）。
- 不 pin 内部字段名/常量字面/源码字符串（沿用 task README 执行约定）。

## §8 原生实证入口（PS-04）

- 真实 Release `GalleryApp` + 生产 `OpenTkHost`（`UiApplication.Run`），owned-HWND SendInput 真实 MouseUp/键盘/滚轮；**own PID/own window 且记录进程信息，禁止按名称 kill**。
- 截图：既有 ZLib/`GL.ReadPixels(FRONT)` 通道；不用未验收的 GDI 采集通道。
- 覆盖：320/1024 逻辑宽 × light/dark × 1.5 DPI；真实 pointer 提交、键盘导航/提交、调用方 ScrollView 内 active ensure-visible。
- macOS/Linux 只承诺源码级一致路径，不虚报运行结果。

## §9 任务拆分（PS-01~PS-05）

| ID | 任务 | 前置 | 阶段 |
|---|---|---|---|
| PS-01 | 容器/导航契约与布局准则（文档任务：细化 plan §4/§5 为可执行契约清单） | 无 | A |
| PS-02 | `Panel` 实现 + 消费者边界测试 | PS-01 | B |
| PS-03 | `Sidebar<T>` 实现 + pointer/keys/binding/reorder/clip/scroll/focus 测试 | PS-01（可依赖 PS-02 的样式 token 用法，串行稳妥） | B |
| PS-04 | Gallery 真实集成与窄宽/主题/DPI/真实输入实证 | PS-02、PS-03 | C |
| PS-05 | 文档收口 + fresh 双 config 五套件 + 主审 → 单次英文 commit/push | PS-04 | C |

依赖链：PS-01 → PS-02 → PS-03 → PS-04 → PS-05（串行）；每个任务经主审独立复审通过才开下一项。

## §10 验收门槛

- A：契约清单经主审确认无歧义。
- B：`Panel`/`Sidebar` API 与上述行为契约闭合；永久测试断言消费者可观察行为全绿。
- C：Gallery 集成 + 原生实证 raw 齐全；`dotnet build` Debug+Release 0w0e；五测试工程 fresh 全绿；api-reference/README/guide 更新；交付记录 + evidence 目录；主审门通过后单次英文 commit/push。

## §11 执行约定

- 永久测试只承诺消费者可观察行为与既有计数器；不 pin 内部字段名/常量字面/源码字符串；不引入 wiring/mockecho 测试。
- 返修写 `docs/return/` 带日期文件；历史失败 raw 原样保留。
- 范围外条目（§1）不实现、不 stub、不假渲染。
- 工作区用户改动不纳入提交；进程管理只用 own PID/own window，禁止按名称 kill。
