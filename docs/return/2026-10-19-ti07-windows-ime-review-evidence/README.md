# TI-07 独立审核证据

审核日期：2026-10-19

对应[审核记录](../2026-10-19-ti07-windows-ime-review.md)。当前是实施中证据，不是TI-07最终交付。

`Program.cs`、`audit.csproj` 为Codex独立检查，引用Debug生产DLL，不重新构建生产项目。托管检查使用真实UiRuntime/TextField与IMM替身；`--native`另创建不可见的自有Win32窗口，直接调用生产窗口过程并注入上下文失败。它不选择输入法、不发送用户键盘或鼠标输入、不控制其他窗口，不能代替真实微软拼音验收。

先构建生产DLL与`tools/CSharp.UI.ImeHostCheck`，再执行：

```powershell
dotnet run --project docs/return/2026-10-19-ti07-windows-ime-review-evidence/audit.csproj -- --native --tool
```

`native-audit.txt`记录最新25/25通过。检查范围：公开方法组兼容、字符消息来源、取消判据、组合归属、下一段属性、缩放坐标、系统组合窗掩码、降级能力、不可变分段集合、实际HWND默认链和窗口过程恢复，以及验证工具Query/Scenario的取消传播（`--tool`，不打开可见窗口）。

源码已包含无START隐式组合及Unicode标量检查。`implicit-audit.txt`保留R13返修前的失败证据；其取消与跨焦点问题已在最新23项中复测通过。

真实候选窗移动/滚动/裁剪、隐藏和完整关闭矩阵仍须后续复测；首次预编辑/选词证据见下方。

## 生产宿主与正式回归

- `regression/`：Codex独立构建Debug/Release及四套正式测试，两种配置各950项（545核心+67文本+53集成+285 OpenTK），零失败、零跳过。
- `production-nihao/`：生产MTA、DPI144、实际微软拼音，真实预编辑与候选截图，精确“你好”唯一提交及Enter原拼音唯一提交通过。
- `production-matrix-round1/`：完整矩阵的首轮真实日志与截图，退出1；不能直接把所有脚本pass当作验收。英文模式未切换、滚动与断点之后前置状态失效等问题见审核记录R15，仍待返修复测。
