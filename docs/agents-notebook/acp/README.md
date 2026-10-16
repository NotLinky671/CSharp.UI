# DeepSeek Harness ACP 本地连接

验证日期：2026-10-15  
执行者：Codex  
适用范围：当前 Windows 桌面版安装；本工具是 Agent 工作流辅助，不属于 UI 框架运行时。

## 已验证结果

已通过实际 stdio JSON-RPC 连接验证：

- `initialize` 协商 ACP v1 成功。
- `session/list` 按当前项目工作目录查询成功，未恢复或关闭已有会话。
- 新建一个专用验证会话，提交不使用工具、只回复固定文本的提示词。
- 收到 assistant 文本 `ACP_OK`，提示词结果为 `stopReason: end_turn`。
- `session/close` 成功，服务进程正常退出：退出码 0，无强制终止。

以上验证证明本地任务提交与回复链路可用，不代表已经派发或完成框架开发任务。

## 本机位置

| 项目 | 位置或版本 |
|---|---|
| 安装目录 | `C:\Users\nothh\AppData\Local\Programs\DeepSeek Harness` |
| 桌面版 CLI 启动器 | 安装目录下的 `resources\runtime\cli\bin\dsh.cmd` |
| ACP profile | `C:\Users\nothh\.dsh\profiles\acp` |
| 已检查的内置 ACP 包 | `@deepseek-ai/dsh-acp`，`0.2.0-rc.2` |
| 协议报告的 agent | `deepseek-harness-acp`，`0.0.1` |

启动器使用 Electron 的 Node 模式运行内置 CLI。本检查器采取相同方式，通过参数数组启动进程，不拼接 shell 命令。初始化时由 Harness 自身创建 ACP profile；没有手动复制凭据或修改桌面 profile。

## 使用检查器

从项目根目录运行；需要可用的 Node.js。

只验证握手与当前项目会话列表，不创建会话、不调用模型：

```powershell
node docs/agents-notebook/acp/check-connection.mjs
```

查询全部持久会话并按工作目录汇总工作区，不输出会话标题或消息正文，也不调用模型：

```powershell
node docs/agents-notebook/acp/check-connection.mjs --list-workspaces
```

该模式遍历 `session/list` 的所有分页，返回各工作目录及会话数量。它表示持久会话中出现过的目录，不是桌面侧单独注册的工作区列表；尚无会话的空工作区不会出现。不能与 `--smoke` 同时使用。

验证完整收发链路，会创建一个验证会话并产生一次简短模型调用，完成后关闭该会话：

```powershell
node docs/agents-notebook/acp/check-connection.mjs --timeout-ms 60000 --smoke
```

模型沿用 ACP profile 的配置。验证提示词明确要求不调用工具、不读写文件、不执行命令或创建提交，只回复 `ACP_OK`。客户端对收到的权限请求返回取消，并且不提供客户端文件系统或终端能力。

安装位置或工作目录不同可显式指定：

```powershell
node docs/agents-notebook/acp/check-connection.mjs --install-dir 'C:\Users\nothh\AppData\Local\Programs\DeepSeek Harness' --workspace 'C:\Users\nothh\code\C#\CSharp.UI'
```

成功输出包含 `ok: true`；完整验证还必须包含 `acknowledgment: ACP_OK`、`ownedSessionClosed: true`、`serverExitCode: 0`、`forcedTermination: false`。关闭会话不等同于删除持久记录，验证会话可能仍出现在后续列表中。

检查器只输出协议能力、计数、固定验证回复及退出状态，不输出会话标题、推理内容、凭据或原始 stderr。若握手或列表查询失败，不会报告连接成功；原始请求与阶段错误使用退出码 1 表示。

## 沙箱与进程清理

首次默认权限启动时，实际遇到 `EPERM`：无法创建 `C:\Users\nothh\.dsh\profiles\acp`。在获准访问 Harness 自身配置目录后，握手、列表和完整收发验证成功。普通桌面终端与 Codex 沙箱的文件访问权限可能不同；应按实际错误处理，不把权限失败误判为 ACP 不支持。

检查器优先关闭自己创建的会话，再关闭子进程 stdin。若进程未在宽限内退出，只终止本次启动的子进程，并在输出中标记 `forcedTermination: true`；不会终止已有桌面进程或关闭其他会话。首次只读检查需要终止子进程退出；后续完整收发检查是退出码 0 的正常退出。两者不能混同记录。

## 后续任务派发边界

当前脚本仅用于连通检查与固定文本验证，尚不提供任意开发任务派发、恢复会话或自动允许工具执行。真正派发开发任务时，仍由用户明确给出任务范围；Codex 负责规划与审核，DeepSeek Harness 负责实现和测试，沿用项目工作流。

此连接不依赖额外 MCP 桥接。协议依据：[DeepSeek Harness ACP 文档](https://github.com/deepseek-ai/deepseek-harness/blob/master/packages/acp/acp/README.zh.md)。本机内置包版本与远程 master 可能不同，实际能力以握手结果及本机包为准。

## 桌面界面的会话归属（2026-10-15 核实）

ACP `session/new` 的 `cwd` 只确定会话运行目录，不等于将该会话登记进桌面端同路径项目的会话分组。此前“在 CSharp.UI 工作区中新建会话”的说法应精确理解为“新建 cwd 为 CSharp.UI 的 ACP 会话”，不能据此承诺桌面侧边栏可见。

核实本机内置实现：ACP 通过 Agent/Session 服务创建会话，没有调用 `workspaceRegistry`；桌面侧的 Session Controller 在创建项目内会话后，会额外调用 `workspace.attachSession(sessionId)`。Workspace 的有序 `sessionIds` 才是项目成员归属依据，注册表初始化完成后不会每次启动重新从磁盘补录所有会话。会话持久化与项目分组是不同的数据关系。

测试会话的压缩日志已经保存在 `~/.dsh/sessions/` 下 CSharp.UI 对应的项目目录，和已有桌面会话使用同一存储根；`session/close` 释放会话而不删除日志。看不到项目分组中的记录不能据此认定会话创建失败。

若需求是“在桌面 CSharp.UI 项目下面可见的新会话”，还需要桌面 Host 的会话创建或项目关联接口；当前 ACP 检查器没有实现这项桌面集成。应分别报告“ACP 收发成功”和“桌面项目登记成功”，不能混为一项。
