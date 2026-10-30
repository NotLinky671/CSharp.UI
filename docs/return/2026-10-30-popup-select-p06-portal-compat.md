# P-06/P-07 兼容收口开发记录（2026-10-30）

关联任务：P-06 Gallery 集成、P-07 全量验收。本记录只覆盖两项文本/绑定生命周期兼容缺口与一次 Debug 全量验证；不代表最终 Release/GL/OS/真实窗口验收（见“边界”）。

## 1. portal 内缓存 Binding 反应性（已修）

- **缺口**：普通 `Popup` 内容里的**缓存 `Binding`**（如字段 `_binding ??= text.ToBinding()`，只在首次 `Build` 读一次值）在打开 popup 后，外部 `State` 变化不会驱动重绘/文本更新。根因：portal 子树挂在 `_layer → OverlayHost → PopupSurfaceView` 下，父链上没有宿主 `Component`；`UiRuntime.RegisterBindingDependency` 沿 `Parent` 找最近 `ComponentState` 登记依赖，找不到则 `leaf` 控件的 `Binding.Read` 依赖补登失败 → 外部 `State.Value` 改变不请求 `NeedsDraw`。
- **修法（采纳主审首选）**：internal `PopupContentScope : Component`（`src/CSharp.UI/Runtime/PopupSurfaceView.cs`），`PopupSurfaceView` 的 `_children = [new PopupContentScope(content)]`。portal 内容现在有一个真实 Component 归属点，`RegisterBindingDependency` 正常登记；内容描述保持原对象、节点身份与内部组件状态按原有协调规则保留（不丢 state）。未引入通用逻辑树/每帧 poll/每控件手订阅。
- **永久消费者断言**（`tests/CSharp.UI.Tests/PopupViewTests.cs` 新增 `C14_PopupContentCachedBindingDrivesRedrawAndUpdatesDisplayedText`，缓存 `Binding` + 卸载后依赖清理）：
  - 打开 popup → `Text.Value = "after"` → `NeedsDraw == true` → `Frame` 后 `TextField` 显示 `after`（真反应性，不是 `BuildFrame` 重读 getter 的假通过）；
  - 关闭卸载后 `Text.Value = "unmounted"` → `NeedsDraw` 保持 `false`（依赖随节点卸载释放，无残留订阅）。
- **证据**：`p06-portal-before EXIT=1`（同测试原始 FAIL）→ `p06-portal-after EXIT=0`（PASS）。临时探针 `TempPortalReactivityProbe.cs` 已删除。

## 2. 80px 极窄 Markdown Copy 按钮（已修）

- **缺口**：`MarkdownCodeBlockView` 顶部 `HStack(Spacer, copy)` 在 80px 视口下 copy 按钮 `Frame.Right` 超出视口（`ButtonCopy(32,17565.747,51.883×24.621)`）。**baseline 249ca96 raw 即同样溢出，非 Popup 引入**。
- **修法**：`copy.Flex(grow:0, shrink:1, min:0).Clip()`——正常宽保持原样；极窄/本地化长 `Label` 时按钮可收缩到 0 最小宽并对绘制裁剪（文字随 Frame 截断，按钮仍可点击复制）。未改 padding、未隐藏 copy、未借 HorizontalScroll 豁免、未动全局 HStack。
- **证据**：`p06-copy-before EXIT=1`（80px `right 112.4>80` + 320px 断言差异两项 FAIL）→ `p06-copy-after EXIT=0`（2/2 PASS）。临时探针 `TempCopyNarrowProbe.cs` 已删除。

## 3. 结构 pin 测试删除（按规范，非产品回归修复）

- `GalleryNestedScrollTests.控件展示的滚动容器是视口在外的真实内层滚动视图` 整个方法删除：主体是硬 pin 结构（`FrameView`/`KeyedView` 类型、固定 41 children、`14×1.25` stub 行高、spacing 4、**全树 0 个 `ClipView`**）。按开发者规范“既有 wording/implementation/incidental-behavior 测试 MUST DELETE, NEVER repin”，且其全树 `ClipView` 扫描与 Markdown 复制按钮合法的局部 `Clip` 冲突（上一轮 OpenTK 唯一 FAIL：`Assert.Empty` 收集到该 Clip）。
- **未删任何消费者断言**：后续真实功能测试全部保留——嵌套滚轮一格 48 逻辑像素且外层不动、到底冒泡、首尾行可见、视口外行真实裁剪、DPI 1/1.5 一致。这些才是功能证明。
- 计数变化：OpenTK `684 → 683`（仅删此一项）。

## 4. Debug 全量验证（唯一 fresh，源码 frozen）

唯一 `dotnet build CSharp.UI.slnx -c Debug`：**0 警告 0 错误**（`p07-debug-build.stdout.txt`）。五个受影响工程 fresh raw（UTF-8 stdout/stderr/exit，全部 `EXIT=0`）：

| 工程 | Total | Failed | 日志 |
|---|---:|---:|---|
| CSharp.UI.Tests | 787 | 0 | `p07-debug-Tests.*` |
| CSharp.UI.Integration.Tests | 56 | 0 | `p07-debug-Integration.*` |
| CSharp.UI.Markdown.Tests | 50 | 0 | `p07-debug-Markdown.*` |
| CSharp.UI.Text.Tests | 112 | 0 | `p07-debug-Text.*` |
| CSharp.UI.OpenTK.Tests | 683 | 0 | `p07-debug-OpenTK.*` |

p06-post2 的失败 raw 未覆盖保留；本轮日志以 `p07-debug-` 前缀新写。

## 边界（未验收，不预填）

- **UI/OS 12-matrix 尚未验收**：仅 Debug 单配置 + 无窗口 headless 证据；Release、GL 表面像素、真实窗口 IME、DPI 矩阵、samples smoke 未跑。后续由 legacy fast 唯一 Release Gallery+GL/OS 门补证。
- 未 commit/push：等主审 final 门。
- fast `commit-agents-md` 独占 Guide/双 README/sample 代码，本记录未触碰。
