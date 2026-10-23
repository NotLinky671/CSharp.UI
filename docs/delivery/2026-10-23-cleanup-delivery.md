# 收尾清理交付记录

日期：2026-10-23

## 已完成

- 用户工作流修改 `AGENTS.md` 已在 `ac59d51` 提交；按用户后续要求删除 `docs/agents-notebook/acp/check-devin.mjs`，并移除 ACP 笔记中的当前运行指令。历史验收记录保留。
- 六个历史输入法工具（ImeProbe、ImeHostCheck、TsfHostCheck、NativeEditReference、TsfHostBridgeCheck、HostRoutingSpike）与三个配套测试工程归档到标签 `archive/tools-before-removal`。当前分支删除其源码及本地 bin/obj，保留 AssetGenerator、生产源码、四个正式测试工程和既有验收证据。删除生产程序集对已归档工具的 InternalsVisibleTo，并更新来源注释。
- 历史工具复跑方式：`git worktree add --detach ../CSharp.UI-ime-archive archive/tools-before-removal`，在独立目录使用原工具和测试。标签记录归档前完整提交，历史文档中的工具路径按该快照解析。
- 字体/图片测试的 `TextTestHelpers.UniqueTempPath` 改用测试输出目录下 `scratch/<进程ID>-<GUID>/`，不再依赖系统 Temp。用例仍自行删除文件，文件写入失败仍直接报错。没有修改用户系统 TEMP、ACL 或防护设置。调查能确定测试宿主在系统 Temp 写入被拒而仓库目录可写；独立探针能写相同路径，因此具体进程限制来源尚未确定，本修复消除了测试对该环境权限的依赖。
- 清理源码可空值、无效私有字段、Windows COM 调用平台分析警告，以及测试中的可空值、原生指针常量、xUnit 断言和阻塞 Task 用法。保留线程投递测试的 UI 线程约束，使用独立 Thread 进行后台投递。没有 NoWarn 或降低分析级别。

## 最终验证

构建命令：`dotnet build CSharp.UI.slnx -c <Debug|Release> --no-incremental --nologo -v minimal -m:1 -nodeReuse:false`。

测试命令：`dotnet run --project tests/<工程> --no-build -c <Debug|Release>`。

验证进程的 TEMP/TMP 明确设为本机系统默认目录（LocalAppData/Temp），未重定向到仓库。Text.Tests 的文件由改后的 fixture 管理。

| 验证 | Debug | Release |
|---|---|---|
| 非增量全量构建 | 0 警告、0 错误，exit 0 | 0 警告、0 错误，exit 0 |
| CSharp.UI.Tests | 551/551，exit 0 | 551/551，exit 0 |
| CSharp.UI.Text.Tests | 67/67，exit 0 | 67/67，exit 0 |
| CSharp.UI.Integration.Tests | 53/53，exit 0 | 53/53，exit 0 |
| CSharp.UI.OpenTK.Tests | 668/668，exit 0 | 668/668，exit 0 |

三个 Release 示例设置 `CSHARPUI_SMOKE_FRAMES=5` 后均实际建窗渲染并自动退出（exit 0）：Counter 480×320、TodoList 760×480、ControlGallery 900×700，DPI 1.5。没有新增永久测试。

完整命令输出在配套的 [2026-10-23-cleanup-evidence](2026-10-23-cleanup-evidence/) 目录，构建日志为最终无警告版本；此前发现测试项目仍有 25 个警告后已修复并重建。

## 边界

本次没有改变用户已确认的 TI-09/TI-10 验收边界，没有新增多 DPI 或 macOS/Linux 真机输入法证据。归档的三套工具测试用于复验旧原型，不计入现有四套正式测试的 1339 项，也未将旧测试声明为已迁移。原始日志和截图没有修改。
