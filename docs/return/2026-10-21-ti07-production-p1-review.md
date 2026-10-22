# TI-07 P1：正式接入基础代码审核

审核日期：2026-10-21

状态：DSH 返修后，Codex 独立复审通过 P1；R1、R2 已关闭。只审核 P1，不代表 TI-07 整体结论。

设计依据：[生产接入计划](../plan/2026-10-21-ti07-production-tsf-integration-plan.md)。

## R1：ole32 导入入口名称错误（P1）

文件：`src/CSharp.UI.OpenTK/Hosting/StaComApartmentLease.cs`，`WindowsComApartmentNative` 的两处 DllImport。

声明使用 `ExactSpelling = true`，托管方法名却是 `CoInitializeExNative`、`CoUninitializeNative`，且没有 `EntryPoint`。ole32 的实际导出是 `CoInitializeEx`、`CoUninitialize`。真实 Windows 调用会产生 EntryPointNotFoundException；初始化被 catch 转成 Failed，导致后续 TSF 永远无法正常启动。仅替身测试不能发现该问题。

返修：为两处声明指定正确 EntryPoint，或使用与导出完全相同的方法名。保持 S_OK/S_FALSE 的引用归属和原线程释放语义。

Codex 已在本机使用 NativeLibrary.TryGetExport 独立确认：`CoInitializeEx`、`CoUninitialize` 均存在；`CoInitializeExNative`、`CoUninitializeNative` 均不存在。

独立真实线程复现：`artifacts/ime-work/p1-native-audit/Audit.csproj` 构建成功；直接运行生成程序集退出 1。STA 的 CoGetApartmentType=MAINSTA(3)，租约却为 Failed/E_FAIL；MTA 的真实类型=1，租约也为 Failed/E_FAIL，无法识别 ChangedMode。共 2 个真实场景失败。

验收：增加真实 Windows 专用 STA 线程 smoke，通过生产默认 native 实现获取租约，并用真实 CoGetApartmentType 确认 STA；嵌套租约获取及释放后外层 STA 仍有效。在独立 MTA 线程确认显式 ChangedMode、无反初始化。测试应避免创建可见窗口或修改全局输入法，并完整 Join/传播线程错误。

## R2：消费标记测试使用不会触发退出的按键（P2）

文件：`tests/CSharp.UI.OpenTK.Tests/WindowsInputBridgeContractTests.cs`，`假桥声明的消费标记阻止宿主退出`。

用 `Keys.Unknown` 断言不退出，即使适配器完全忽略消费标记仍然通过，不能证明接口替换保留退出抑制。返修使用有退出语义的 Escape 或命令 Q，并用无消费对照确认相同按键会退出；为单独验证适配器的消费标记分支，可使用无活动文本客户端且不声明运行时 token 归属的场景。保留另一个真实 token/组合结束同键的核心归属测试。

## 最终复审

R1 已指定正确 EntryPoint，永久测试覆盖真实 CoGetApartmentType、嵌套引用、精确 ChangedMode 和不释放外部引用。Codex 独立真实 STA/MTA 审计 8/8 通过。

R2 已改成 Escape/命令 Q 的消费与未消费对照，独立于运行时 token 分支；DSH 做了变异验证，临时忽略消费标记时 3 项失败，恢复源码并强制重建后通过。最终源码保留 `!consumedByIme` 判断。

Codex 独立运行结果（两个配置各运行一次，不把重复执行累加成新增测试）：

| 检查 | Debug | Release |
|---|---|---|
| CSharp.UI.slnx 构建 | 0 错误、0 警告 | 0 错误、0 警告 |
| 核心测试 | 545/545 | 545/545 |
| 文本测试 | 67/67 | 67/67 |
| 集成测试 | 53/53 | 53/53 |
| OpenTK 宿主/图形/输入测试 | 335/335 | 335/335 |
| 合计 | 1000/1000 | 1000/1000 |

所有上述测试退出码为 0，跳过数为 0。最初沙箱内剪贴板测试失败；非沙箱单独复测及最终两套完整宿主测试均通过，未将环境失败算作通过。

构建命令为 `dotnet build CSharp.UI.slnx -c Debug|Release --no-restore -m:1 -nr:false -p:NuGetAudit=false -v:q`；测试直接执行各配置生成的 DLL，使用 `-noLogo -noColor -parallel none -xml <报告路径>`。完整日志、XML 及独立原生审计源码见[审核证据目录](2026-10-21-ti07-production-p1-review-evidence/)。

DSH 另报两配置全部 7 个测试程序集通过，以及 ImeHostCheck 双配置构建通过。Codex 核对旧 IMM 观测入口保留，宿主只通过契约接线，初始化失败和关闭释放路径仍在窗口销毁前释放桥；P1 未启用 TSF、未改变 Run 线程策略或核心契约。

下一批仅放行 P2a 权威编辑会话适配。不得从“必须在启动前选择套间”推导为“必须迁移到专用线程”；支持主线程 `[STAThread]`，专用线程仍须另验 GLFW 限制。TI-07 总框保持未完成，不提前提交生产实现。
