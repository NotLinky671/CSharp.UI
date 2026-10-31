# PS-05 文档收口与交付验收

依据：[plan](../plan/2026-10-31-panel-sidebar-plan.md) §10/§11。
前置：PS-04（主审通过）。状态：**代码与文档验收通过**——fresh 双配置各
1724 项 0w0e、原生 self+独立全 PASS、交付记录与证据见
[2026-10-31-panel-sidebar-delivery](../delivery/2026-10-31-panel-sidebar-delivery.md)；
提交信息见 git 历史。

## 目标

文档收口 + 双 config 构建 + 全测试套 + 交付记录；主审门通过后单次英文 commit/push。

## 改动

- `docs/guide/api-reference.md`：新增 Panel/Sidebar 节——`Panel`/`Sidebar<T>`/
  `SidebarItem<T>` API 表 + 行为契约（提交纪律、导航、disabled/空/未知值、
  滚动交调用方、非 focusable 行、宽度自然偏好、无 drawer/modal/路由）+ 简短示例。
- `README.md` / `README.zh-CN.md`：First-release scope 控件列表补 `Panel`/`Sidebar`
  （仅实现落地后补，本轮计划已写 "(in progress)" 表述随验收更新）；
  `docs/task` 行与归档指向已就位。
- `docs/delivery/2026-10-31-panel-sidebar-delivery.md`（或按实际交付日期命名）+
  `-evidence/`：构建/test 原始输出、PS-04 原生 raw、文档链接完整性检查。
- `docs/return/` 审核记录按实际审核轮写。
- NOTICE/license：本轮无新增第三方依赖（Avalonia/FluentAvalonia/wpfui 仅设计借鉴）——
  **不新增/不更新 NOTICE**；如实现中实际引入外部代码须先报主审。

## 验收

- E1：`dotnet build CSharp.UI.slnx` Debug+Release 全 sln 0w0e。
- E2：五测试工程 fresh rebuild 全绿（Core/OpenTK/Integration/Markdown/Text）；
  新增测试计数如实记录（区分 infra 失败与断言失败）。
- E3：PS-04 原生实证 raw 已归档且结论如实（含任何历史失败快照）。
- E4：交付记录含边界与已知余项（无 drawer/modal/路由/折叠/动画/多子 Panel、
  公共滚动 API 未提供、macOS/Linux 未实机验证等）、commit 信息按既有 delivery
  约定（"见 git 历史"或 SHA）。
- E5：受影响文档本地链接完整性检查（文件静态，非 build）。
- E6：主审门 → 单次英文 commit+push（AGENTS §git）；工作区用户改动不纳入。
