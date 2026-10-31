# P-06 Gallery 集成开发记录（2026-10-29）

任务：[P-06 Gallery 集成](../archive/2026-10-31-popup-select-tasks/06-gallery-integration.md)
状态：**源码已写，未构建、未运行、未验收**。等待主审核查后唯一 Release 构建/GL/OS 门。

## 改动

- 新增 `samples/CSharp.UI.Samples.ControlGallery/PopupSelectDemo.cs`
  - 普通 `Select<string?>` 绑定 `UseState`，Section 内显示“当前选择：…”回显；
  - disabled `Select`（`.Disabled()`），保留当前 Label，不可打开；
  - 30 项长列表 `Select`，`MaxPopupHeight = 120`，长标签可验证截断；
  - 内层 `ScrollView(height: 140)` 内锚定 `Select` + `Spacer(400)`：滚动跟随/裁出关闭/近底部向上翻；
  - 通用 `Popup`：`Button("打开设置")` 切换，内容为真实 `TextField` + `Button("关闭")`，
    外层 `Text("设置输入回显：…")` 用同一 `UseState`，验证父重建内容状态；
  - 弹层内容背景使用当前 `Theme.Surface` / `Border`（取自传入的 `State<Theme>`），
    不硬编码 Light；Section 内行使用 `AdaptiveStack(560, label, control)` + `.FillWidth()`，无固定 120 宽；
  - `ModeItems`/`DisabledItems`/`LongItems`/`ScrollItems` 是 `private static readonly` 不可变数据，
    每次 Build 不再 `Enumerable.Range`/new 30 条记录；View/Binding 仍按 Build 新建（不做跨 Build 缓存）。
- 修改 `samples/CSharp.UI.Samples.ControlGallery/Program.cs`
  - 在“文本编辑与输入法”之后、`Markdown` 之前插入 `Section("弹出与选择", new PopupSelectDemo(theme))`。

未改 csproj、资产、Core/Frame、私有不变量；无测试钩子/Started 日志/驱动 API。

## 消费者入口

- Section 位置：`Program.cs` 的 Section 列表，`文本编辑与输入法` → **`弹出与选择`** → `Markdown`。
- 可见标签：`选择模式`、`当前选择：…`、`禁用选择`、`长列表（30 项，限高）`、
  `滚动区域内锚定…`、`设置弹层`、`打开设置`、`关闭`、`设置输入回显：…`。

## 待验收（未结论）

- 未运行 `dotnet build`；未生成截图/日志。
- 待唯一 Release 门：Gallery 320/1024、Light/Dark、DPI 144；Select 打开/翻转/滚动跟随/
  裁出关闭；长列表 End 可见；Popup TextField 输入/关闭/回显；GL/OS 证据按 P06 计划。
