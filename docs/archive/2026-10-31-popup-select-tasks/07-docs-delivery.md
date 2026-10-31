# P-07 文档与交付验收

依据：[Popup 与 Select 实施计划](../../plan/2026-10-29-popup-and-select-plan.md) §6。
前置：P-06。状态：**代码与文档验收通过**（交付记录见 [2026-10-30-popup-select-delivery](../../delivery/2026-10-30-popup-select-delivery.md)；提交信息见 git 历史）。

## 目标

文档收口 + 双 config 构建 + 全测试套 + 真实 GL/窗口 smoke + 交付记录；
主审门通过后单次英文 commit/push。

## 改动

- `docs/guide/api-reference.md`：`Popup`/`PopupPlacement`/`Select<T>`/`SelectItem<T>`
  行 + light-dismiss/外点吞并/锚豁免/Esc/Tab/IsOpen 双向纪律简述。
- `docs/guide/`（现有文件内补节，不建新文件，倾向 `api-reference.md` 输入事件节附近）：
  overlay 层模型一句话（真实 view 子树、单 popup、锚环境按轮现值、外点吞并取舍、
  Up-activation、失活关闭写清）；`UiRuntime.Root` 的 `Parent`/`Depth` 变化
  （`Parent` 现为 internal 层节点、非 null）如实记录——兼容风险写 guide。
- `README.md` 控件列表补 Select/Popup（如有控件枚举处——任务内核对；
  仅在 P-05 实现后补，本轮计划不虚称已有）。
- `docs/delivery/2026-10-29-popup-select-delivery.md` + `-evidence/`：构建/test 原始输出、
  Gallery 截图、fast OS 验收原始记录（命名按交付实际日期）。
- `docs/return/` 审核记录按实际审核轮写。
- NOTICE/license：本轮无新增第三方依赖（placement 算法自写，Avalonia/Floating UI
  仅设计借鉴）——**不新增/不更新 NOTICE**；如实现中实际引入任何外部代码须先报主审。

## 验收

- F1：`dotnet build` Debug+Release 全 sln 0w0e。
- F2：全测试套件（Core/OpenTK/其它现存测试项目）fresh rebuild 后全绿；
  新测试含 P-01~P-04 验收点，计数如实记录（区分 infra 失败与断言失败）。
- F3：真实 Release Gallery 窗口 smoke（含 P-05 验收）；macOS/Linux 只承诺源码级
  一致路径，不虚报运行结果。
- F4：交付记录含 commit SHA（提交后补或写"见 git 历史"，按既有 delivery 约定）、
  边界与已知余项（单 popup、非 modal、外点吞并取舍、可编辑 ComboBox 等 scope 外）。
- F5：主审门 → 单次英文 commit+push（AGENTS §git）；工作区用户改动不纳入。
