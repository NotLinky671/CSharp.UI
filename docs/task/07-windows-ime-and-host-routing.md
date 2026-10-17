# TI-07：Windows IME 桥与宿主输入联调

创建日期：2026-10-17

状态：待开始

阶段：C

设计依据：[文本输入与编辑基础计划](../plan/2026-10-17-text-input-and-editing-plan.md)（接口冻结前均为设计草案）。

[返回任务索引](README.md)

## 目标与范围

按已验证路线接入真实Windows输入法，保证单次提交、候选定位及按键不泄漏。

边界：Windows宿主桥与路由处理结果；TI-01路线变更先复审。

## 前置依赖

[TI-01：平台可行性与 Windows 原型](01-platform-feasibility.md)、[TI-05：有界撤销重做与编辑命令](05-undo-redo-and-edit-commands.md)、[TI-06：预编辑绘制、命中与候选几何](06-preedit-rendering-and-geometry.md)均须Codex复审通过后再实施；使用前置任务已冻结契约。

## 实施清单与接口接入

- [ ] 实现TI-01确定的IMM或经批准TSF路径，固定委托生命周期、消息转发与原生资源成对释放。
- [ ] 规范化宽接口字节量/UTF-16单位与回调序列；每个结果只有一个提交来源，不用字符串/时间全局去重。
- [ ] InputRouter处理结果回到OpenTkInputAdapter，Enter/Escape/候选导航与结束同键后续回调不触发表单、控件或退出。
- [ ] 把最终逻辑caret变换为平台要求的客户区/屏幕坐标，DPI/移动/滚动/失焦/关闭按规则更新或撤销候选锚点。

## 测试与验收

- [ ] 真实微软拼音预编辑、空格/数字/Enter选词、Esc取消、连续分段及英文/Unicode普通输入仅一次；记录完整事件序列。
- [ ] 150%真实候选窗跟随caret，窗口移动/resize、滚动、跨断点、失焦/失活、绑定外改和关闭释放。
- [ ] 合成平台事件覆盖错误/乱序/重复与资源计数；原生输入证据不可仅用DispatchComposition代替。

## 实施记录与审核门槛

实施时记录实际签名、涉及模块、命令/退出码/测试计数、证据路径及平台边界。DSH自测通过后标“待Codex复审”；Codex审核问题写docs/return带日期文件，DSH返修至通过后才英文提交并推送。状态改“已完成”须有Codex复审结论，不在Build/Measure中执行外部副作用。

本轮不修改既有archive文件；关键证据不只放可清理artifacts。后续交付记录由Codex创建，当前不预先标记验收通过。
