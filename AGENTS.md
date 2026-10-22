# Notes for coding agents
项目详情去阅读 [README.md](README.md)

## Codex
如果你是 Codex 你负责项目规划、审核代码

## DeepSeek Harness
如何你是 DeepSeek Harness 你根据 Codex 的规划和任务进行代码编写和单元测试
- 代码可以完全通过之后要进行一次提交和推送

## docs
你可以在 `docs` 文件夹下找到关于本项目的计划与任务

- agents-notebook: Agent 可以在这里放自己的编码规范和错误记录等可以帮助后续工作的文件。
- guide：本项目的使用指南。
- plan：`Codex` 将计划放在本文件夹下，文件名字要求加上日期。
- task：`Codex` 将拆分 `plan` 中一个计划到本文件夹下，`Deepseek Harness` 根据这里面的 task 完成代码与测试。`Codex` 拆分 `plan` 之前需要把里面之前的 task 都删除再添加新的 task 和 README.md。
- return：当 `Codex` 审核完代码之后，如代码有问题，将发现的问题写在本文件夹下，文件名字中需要带时间。`DeepSeek Harness` 根据里面的文件来修改代码，这个文件夹下的文件不需要删除。
- delivery：一个计划完成并通过验收后，`Codex` 将交付记录放在本文件夹下，文件名字要求加上日期；配套的验收证据（截图、测试输出等）放在同名前缀的 `*-evidence` 子文件夹中。
- archive：已结束的旧计划、旧任务与完成记录归档在此（如首版框架任务），只作历史保留，不再修改，也不作为当前工作依据。

## 完整工作流
```text
developer
   |
   | 1. 提需求
   v
Codex
   |
   | 2. 规划：写 docs/plan、拆 docs/task、推送远程
   v
DeepSeek Harness  <-----------------------------------------+
   |                                                        |
   | 3. 按 task 编码 + 单元测试                             |
   v                                                        |
Codex                                                       | 不通过：回到第 3 步
   |                                                        | 写入 docs/return/...
   +----- 4. 审核代码 --------------------------------------+
   |
   | 5. 通过：DSH 提交代码并推送远程
   v
developer
```

循环的核心是第 3 ↔ 4 步：`DeepSeek Harness` 实现、`Codex` 审核来回迭代；只有审核通过才走出循环，由 `DeepSeek Harness` 提交并推送远程，再交付回 `developer`。

## git
1.提交默认使用英文