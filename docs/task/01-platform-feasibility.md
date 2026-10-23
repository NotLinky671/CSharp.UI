# TI-01：平台可行性与 Windows 原型

创建日期：2026-10-17

状态：接入路线经Codex审核通过；原生完整验收移交TI-07/TI-10

阶段：A

设计依据：[文本输入与编辑基础计划](../plan/2026-10-17-text-input-and-editing-plan.md)。

[返回任务索引](README.md)

## 目标与范围

核对实际窗口依赖和输入消息来源，验证最小接入路线并记录三平台能力边界。原型独立于生产框架，不等于完整TextField交付。

2026-10-17用户修订：微软拼音已被用户移除且无需测试；不得继续安装、启用或测试它。当前使用已有WeType证据推进，不要求用户恢复输入法。

## 前置依赖

无。读取README、AGENTS和原计划，历史archive仅供参考。

## 实施清单与接口接入

- [x] 核对OpenTK 4.9.4、实际GLFW原生依赖3.4.0.44及消息线程/来源。
- [x] 自有HWND子类化和IMM32宽接口路线取得WeType真实预编辑及单次“你好”结果；识别GLFW字符重复路径，生产接入需明确所有权。
- [x] 调查macOS marked text和Linux X11/Wayland限制，记录分项回退矩阵。
- [x] 探针持久化到tools及tests，决策与关键证据持久化到docs。
- [x] Codex按修订范围通过路线审核，允许TI-02开始；不扩大TSF应用客户端范围。

## 测试与验收

- [x] 四套历史基线639项通过；最终独立探针86项通过、0失败/跳过，记录来自各自轮次。
- [x] 复审STA、profile状态/恢复、结果归因、按结果冻结并清理预编辑历史、回调异常和截图范围。
- [x] 用真实WeType预编辑/结果支持IMM候选路线，合成测试仅支持状态边界。
- 系统候选窗实际位置、取消、连续分段及结果和新预编辑同消息的真实形态尚未完整验证，移交TI-07/TI-10；不因本任务放行而标为通过。
- 微软拼音不在当前验收范围。保留旧日志作为历史，不以历史profile记录推断当前安装状态或失败根因。

## 实施记录与审核门槛

工具：`tools/CSharp.UI.ImeProbe`与`tests/CSharp.UI.ImeProbe.Tests`，不引用生产项目。

独立测试命令：`dotnet exec tests/CSharp.UI.ImeProbe.Tests/bin/Debug/net10.0/CSharp.UI.ImeProbe.Tests.dll`；exit 0，86项通过。

[平台决策](../agents-notebook/2026-10-17-ime-platform-decision.md)及其同名前缀-evidence目录保留路线依据；[Codex审核](../archive/2026-10-23-completed-reviews/2026-10-17-text-input-and-editing-review.md)与[独立证据](../archive/2026-10-23-completed-reviews/2026-10-17-text-input-and-editing-review-evidence/README.md)记录最终工具审查与范围修订。

后续DSH自测后等待Codex复审，只有审核通过才英文提交推送。Windows完整输入能力仍须TI-07/TI-10验收；macOS/Linux仅声明能力与回退，不声称完整组合支持。
