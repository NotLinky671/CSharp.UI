# R15：控件展示的“滚动容器”区域没有创建滚动视图

记录日期：2026-10-15

状态：DeepSeek Harness 已修复并提交，待 Codex 复审。

优先级：P2

审核版本：`863a532`

## 触发及原因

用户确认无法滚动的是控件展示示例中标题“滚动容器”、包含 40 行的小区域。

`samples/CSharp.UI.Samples.ControlGallery/Program.cs` 中该区域实际是 `VStack(...).Spacing(4).Frame(height: 120).Clip()`：固定高度并裁剪不会赋予滚动行为。页面仅有一个最外层 `ScrollView`，滚轮只能交给外层，无法独立查看内部被裁剪的 40 行。R14 滚轮步长修复没有覆盖实际 Gallery 视图树，是上轮验收遗漏。

## 返修范围

- 用真实内层 `ScrollView` 包含说明与 40 行内容，将 120 逻辑像素的视口约束放在 `ScrollView` 外面，而不是固定内部内容高度。
- 保持外层整页滚动、现有内容与布局；不改动已通过的 R14 单位换算，不扩大到框架布局改写。
- 回归必须实例化实际 `GalleryApp`，不要复制一个简化视图替代：滚动区进入外层可见范围后，内层收到 `-1` 步移动 48 逻辑像素，外层偏移不变；内层到达底部后继续向下交给外层；向上可回到第一行。
- 核对首尾行能进入内层可见区域且裁剪仍有效，DPI 1 与 1.5 距离一致；已有核心嵌套滚动测试继续通过。
- 构建控件展示 Debug/Release，五帧冒烟；运行相关回归及完整测试。仅提交本次修改，英文 commit，不推送。

## 验收状态

- [x] 实际 Gallery 内外两个滚动视图与正确视口约束。
- [x] 实际 Gallery 嵌套滚动回归通过。
- [x] 完整测试与示例构建、冒烟通过。
- [ ] Codex 独立复审通过。

---

## R15 实施与验证记录（DeepSeek Harness 追加）

执行模型：`opencode-go/deepseek-flash`。以下命令与结果均为本机实际执行；未改动 R14 的滚轮单位换算、
未重构框架布局、未修改任何系统设置、未推送。

### 视图树修复（最小改动）

`samples/CSharp.UI.Samples.ControlGallery/Program.cs` 的“滚动容器”一节由

```csharp
new VStack(说明, ForEach(40 行)).Spacing(4).Frame(height: 120).Clip()
```

改为

```csharp
new ScrollView(new VStack(说明, ForEach(40 行)).Spacing(4)).Frame(height: 120)
```

- 内层是真实 `ScrollView`：120 逻辑像素视口由滚动视图**外面**的 `FrameView` 约束，内部内容保持自然高度（实测 877.5，远大于视口）。
- `Frame` 仍使用默认 `Center` 对齐，内容矩形、宽度、裁剪高度与修改前一致，因此整页外观不变；视口裁剪现在由 `ScrollView` 自身完成，多余的 `Clip` 修饰符已移除。
- 外层仍是唯一的整页 `ScrollView`，其余内容、间距、主题与图片章节未改动。

该写法与既有指南 `docs/guide/modifier-order.md`（`new ScrollView(content).Frame(height: 240)`）一致。

### 新增回归（真实示例程序集，非简化复制树）

`tests/CSharp.UI.OpenTK.Tests/CSharp.UI.OpenTK.Tests.csproj` 增加对
`samples/CSharp.UI.Samples.ControlGallery` 的 `ProjectReference`；
新增 `tests/CSharp.UI.OpenTK.Tests/GalleryNestedScrollTests.cs`（7 例），直接实例化 `GalleryApp`，
只替换平台服务（文本、纹理、图片、时钟、滚轮设置替身），布局与滚动逻辑全部为真实实现，
滚轮通过真实 `OpenTkInputAdapter`（GLFW 归一化后普通一格 `-1`）投递。

| 用例 | 覆盖 |
|---|---|
| 控件展示的滚动容器是视口在外的真实内层滚动视图 | 页面恰有两个 `ScrollView`；内层父节点是固定 120 高度的 `FrameView`；内容 41 个子节点、自然高度远大于视口；已无 `ClipView` |
| 控件展示内层一格滚动48逻辑像素且外层不动（DPI 1、1.5） | 先用外层滚轮把内层视口带进可见范围，再在内层中心投递一格 `-1`：内层 `ScrollOffsetY` 恰为 48，外层偏移不变；适配器事件仍是 `Steps` 单位的 `-1` |
| 控件展示内层到底后外层接手且向上可回到首行（DPI 1、1.5） | 内层夹紧到最大偏移；再向下同一格由外层接手（外层 +48、内层不动）；向上由内层先消费并回到 0，首行完整落在内层视口与外层视口内 |
| 控件展示内层首尾行可见且裁剪正确（DPI 1、1.5） | 顶部首行完整可见、末行与视口不相交；底部末行完整可见、首行被滚出；逐行比对绘制命令：与 120 像素视口相交的行恰好就是被绘制的行，视口内没有画到裁剪区之外的内容 |

DPI 1 与 1.5 下逻辑距离一致（均为 48 逻辑像素）；滚轮设置注入每格 3 行的替身，不依赖本机系统设置。

回归灵敏度已实际核对：临时把示例改回旧的 `VStack…Frame…Clip` 视图树后，7 个新用例全部失败
（`控件展示页面应包含内外两个 ScrollView，实际找到 1 个`），恢复修复后重新构建即全部通过，
证明该回归确实覆盖实际 Gallery 视图树而不是空断言。

### 测试

四个测试工程（`dotnet run --project tests/<工程> --no-build`，Debug）：

| 测试工程 | Total | Failed | Skipped |
|---|---|---|---|
| CSharp.UI.Tests | 164 | 0 | 0 |
| CSharp.UI.Text.Tests | 58 | 0 | 0 |
| CSharp.UI.Integration.Tests | 13 | 0 | 0 |
| CSharp.UI.OpenTK.Tests | 151（新增 7） | 0 | 0 |
| 合计 | **386** | **0** | **0** |

核心既有嵌套滚动用例（`WheelScrollTests.嵌套滚动容器内层到边界后由外层接手` 等）继续通过，未弱化断言。

### 构建与冒烟

- `dotnet build CSharp.UI.slnx -m:1 -nodeReuse:false --no-restore`（Debug）：0 错误；仅离线 `NU1900` 警告，无新增代码警告。
- `dotnet build samples/CSharp.UI.Samples.ControlGallery/... -c Release -m:1 -nodeReuse:false --no-restore`：0 错误。
- Release 真实窗口冒烟 `CSHARPUI_SMOKE_FRAMES=5`：

| 示例 | 逻辑视口 | framebuffer | DPI | 退出码 |
|---|---|---|---|---|
| ControlGallery | 900×700 | 1350×1050 | 1.5 | 0 |

### 未验证项

- 未进行人工鼠标滚轮操作；内层滚动、边界交接与首尾可见性均由实际视图树 + 真实输入适配器路径验证，不是人工观察结论。
- DPI 仅覆盖 1 与 1.5（本机实际 150%），未修改系统显示设置。
- 旧的 `artifacts/publish/` 目录未重新发布；运行新版本需使用本次重新构建的示例输出。

### 提交

R15 修复、回归测试与本文合并为一次本地英文提交，未推送；未暂存 Codex 拥有的
`docs/agents-notebook/README.md`、`docs/agents-notebook/acp/`、
`docs/return/2026-10-15-scroll-wheel-units.md`、`docs/return/2026-10-15-system-dpi-scaling.md`
与 `docs/return/2026-10-15-textfield-caret-alignment.md`。提交 SHA 见交付报告与 `git log -1 --stat`。
