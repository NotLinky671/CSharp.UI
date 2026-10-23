# TI-09：输入示例、使用指南与验收工具

创建日期：2026-10-17

状态：自测通过，待 Codex 复审（真实输入法回归与工具安全性核对未做，见下方未完成项）

阶段：D

设计依据：[文本输入与编辑基础计划](../plan/2026-10-17-text-input-and-editing-plan.md)（接口冻结前均为设计草案）。

[返回任务索引](README.md)

## 目标与范围

让开发者能使用并复验组合输入、历史与回退，示例保持响应式。

边界：改造现有Gallery/示例和正式测试工具，不新增多行编辑器。

## 前置依赖

[TI-08：跨平台能力查询与兼容回退](08-platform-capabilities-and-fallback.md)均须Codex复审通过后再实施；使用前置任务已冻结契约。

## 实施清单与接口接入

- [x] Gallery补组合输入、选区替换、撤销重做演示，沿用父宽度布局与原有全部控件；需要的提交演示用受审核的最小命令入口。（方案 A：不新增公开命令入口，演示区只使用 `TextField` 与既有快捷键，说明文字写明各平台快捷键。）
- [x] 更新中英README入口、API、平台/输入边界、Binding/历史生命周期和快捷键说明，示例代码可编译。（新增 `api-reference.md` 的“文本编辑快捷键”一节与中英 README 各一段；Binding/历史生命周期未新增内容，沿用 `state-lifecycle.md`。）
- [x] 建立可复用最小验证入口及真实输入法测试步骤，明确自动消息回放与真实候选UI证据区别。（新增 `docs/guide/ime-manual-acceptance.md`；TSF 真实验收入口沿用 `tools/CSharp.UI.ImeHostCheck`，未新增工具。）
- [x] 准备窗口失活/焦点切换/节点卸载/外部Binding、DPI/滚动/resize、分段结果/限制边界验收清单。（同上文档，清单项均未在本轮由真人执行。）

## 测试与验收

- [ ] 普通英文、中文IME、粘贴、选区、撤销重做与响应式布局的真实示例集成回归。（未做：需要真人在真实窗口中执行，清单已备好。）
- [ ] 检查当前平台能力说明不夸大、所有文档链接/代码围栏/UTF-8正确。（部分：新文档内链接均指向存在的文件，代码围栏成对；未逐页复核其余文档。）
- [ ] 验证工具不记录用户日常输入或启动后失控占用全局键盘；关闭后不遗留窗口/钩子。（未做：本轮没有新增或修改任何验证工具。）

## 实施记录与审核门槛

实施时记录实际签名、涉及模块、命令/退出码/测试计数、证据路径及平台边界。DSH自测通过后标“待Codex复审”；Codex审核问题写docs/return带日期文件，DSH返修至通过后才英文提交并推送。状态改“已完成”须有Codex复审结论，不在Build/Measure中执行外部副作用。

本轮不修改既有archive文件；关键证据不只放可清理artifacts。后续交付记录由Codex创建，当前不预先标记验收通过。

### 实施记录（2026-10-23）

- 涉及文件：`samples/CSharp.UI.Samples.ControlGallery/Program.cs`（新增 `editDemo` 状态与“文本编辑与输入法”区域，无新增公开 API）、`docs/guide/api-reference.md`、`README.md`、`README.zh-CN.md`、`docs/guide/ime-manual-acceptance.md`。
- 快捷键表依据 `TextEditCommandResolver.Resolve`：Ctrl/Command+Z 撤销，Shift 变重做；Ctrl+Y 仅 Windows/Linux 重做；X/C/V/A 不带 Shift；Alt/AltGr、错误平台主修饰键与额外修饰键不触发。
- 命令与结果：Debug、Release 构建均 0 错误；两种配置下 CSharp.UI.Tests 551、Text.Tests 67（`TEMP`/`TMP` 重定向到仓库内目录）、Integration.Tests 53、OpenTK.Tests 668 全部通过；控件展示示例 `CSHARPUI_SMOKE_FRAMES=5` 两种配置均渲染 5 帧、退出码 0（900×700 逻辑视口、DPI 1.5）。
- Release 全量重建有 40 个警告，位于本任务未修改的文件（如 `CompositionContracts.cs`、`TsfHostSession.cs` 及部分测试），控件展示示例自身 0 警告。
- 平台边界：冒烟只覆盖 900×700 一个窗口尺寸，**未**验证 320/480/768/1024 逻辑宽度下新区域的布局；没有真实输入法证据。
