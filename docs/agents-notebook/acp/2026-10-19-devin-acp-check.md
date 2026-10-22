# Devin CLI ACP 接替 DSH 的连接验证

验证日期：2026-10-19。执行者：Codex。

## 已验证

- 本机命令：`devin.exe`，版本 `3000.11.92 (7f4e64980e22)`。
- `devin acp` 原生使用 stdio JSON-RPC，无需安装 MCP 桥。
- 使用真实客户端名称 `codex-devin-acp-check`，ACP v1 握手成功，无需伪装 Windsurf。
- 在当前仓库目录新建验证会话 `flawless-fibula`。
- 默认模型由 Devin 自身选择，握手后会话配置为 `swe-2-high`、`high`；未修改用户配置。
- 提交不调用工具的固定文本提示词，收到 `ACP_OK`，`stopReason: end_turn`。
- 工具调用数 0，权限请求数 0；服务进程退出码 0，未强制终止。
- 服务声明支持 `loadSession`、会话列表、关闭及删除；提供 `accept-edits`、`ask`、`plan`、`bypass` 模式。这些声明不等同于各功能均已实测。

本次成功运行使用 stdin EOF 退出，没有调用 `session/close`；持久会话记录可能保留。脚本后续增加了关闭自己创建会话的逻辑，这部分尚待再次执行验证。

## 检查器

从仓库根目录运行：

```powershell
node docs/agents-notebook/acp/check-devin.mjs
node docs/agents-notebook/acp/check-devin.mjs --smoke
node docs/agents-notebook/acp/check-devin.mjs --inspect
```

默认只握手；`--smoke` 创建会话并调用模型一次；`--inspect` 创建会话、请求 `ask` 模式，并让 Devin 只读查看四份文件：`AGENTS.md`、`README.md`、`docs/task/README.md`、`docs/task/07-windows-ime-and-host-routing.md`，返回任务交接分析。

检查器不提供客户端文件或终端能力，所有显式权限请求均返回取消。Devin 的内置工具能力由服务自身实现；`--inspect` 另有只读提示词和 Ask 模式约束，不是操作系统沙箱。脚本不是通用编码任务执行器，不启用 bypass，也不授权提交或推送。

## 当前限制与后续交接

Codex 沙箱内启动 Devin 时，CLI 因无法创建自身日志文件而退出；沙箱外同一连通测试成功。这是运行权限限制，不能解释为 ACP 不支持。

只读任务交接检查被自动审批拒绝：项目规则与任务可能是私有内容，向 Devin 服务发送之前需要用户明确授权。尚未实际验证项目文件读取、代码修改、构建测试、会话恢复或提交推送，不据此宣称完整 DSH 替代流程已经验收。

连通层面已经具备接替条件。正式采用时仍沿用仓库分工：Codex 规划和审核；Devin CLI 按 `docs/task` 实现、测试并返回证据；问题写入 `docs/return` 后返修；仅 Codex 审核通过后由 Devin 使用英文提交并推送。当前下一项为 TI-07，但本次没有派发实现。

官方产品说明：[Devin CLI](https://devin.ai/cli)。具体 ACP 方法和模式以本机帮助与本次实际协议响应为准。
