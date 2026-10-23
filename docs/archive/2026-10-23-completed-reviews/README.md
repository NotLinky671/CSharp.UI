# 已完成审核记录归档

归档日期：2026-10-23。本目录保存原 `docs/return/` 下全部已完成审核记录及配套证据，按原相对结构整体迁入（1741 个文件、23 个证据子树），仅供历史追溯，不作为当前实施指令。

## 阅读约定

- 文档内出现的 `docs/return/...` 字面路径，以及 stdout/JSONL/日志/源码快照中的历史命令与路径，均指本目录迁移前位置（`docs/return/`），请按本目录对应相对路径解析；未逐条改写以保持记录原义。
- 文档正文中"待修/审核中/未通过"等历史状态，以同文件最终结论段或对应 `docs/delivery/` 交付记录为准；本归档不重写旧状态。
- 证据文件（PNG、stdout/stderr、JSONL、`.cs.txt` 快照、SHA256SUMS 清单）逐字节保留，未做内容修改或去重。
- 测试源码 XML 注释中的旧 `docs/return/` 路径为历史出处引用，本轮未改。

## 首版框架任务（2026-10-14）

| 记录 | 结论 |
|---|---|
| [代码审核](2026-10-14-code-review.md) | R01–R09 全部修复 |
| [返修跟进审核](2026-10-14-code-review-follow-up.md) | 复审通过 |

## 问题返修 R12–R18 与响应式（2026-10-15～16）

| 记录 | 结论 |
|---|---|
| [光标准线与文字对齐](2026-10-15-textfield-caret-alignment.md) | 复审通过 |
| [系统 DPI 缩放](2026-10-15-system-dpi-scaling.md) | 复审通过 |
| [滚轮单位换算](2026-10-15-scroll-wheel-units.md) | 复审通过 |
| [Gallery 嵌套滚动](2026-10-15-gallery-nested-scroll.md) | 复审通过 |
| [字体栅格采样](2026-10-15-font-raster-sampling.md) | 复审通过（含字形外伸已知限制） |
| [文本遮罩颜色](2026-10-16-text-mask-contrast.md) | 复审通过（外伸限制保留） |
| [窗口缩放黑区](2026-10-16-window-resize-black-area.md) | 复审通过 |
| [响应式布局复审](2026-10-16-responsive-layout-review.md) | RL-09 及全套响应式任务最终复审通过 |

## 文本输入与编辑任务 TI-01～TI-10（2026-10-17～23）

任务索引见 [docs/task/README.md](../2026-10-23-text-input-editing-tasks/README.md)。按最终交付记录：TI-01～TI-08 经 Codex 独立复审通过；TI-09、TI-10 于 2026-10-23 由用户验收收口（无独立复审记录，已知边界见 [TI-08～TI-10 交付记录](../../delivery/2026-10-23-ti09-ti10-delivery.md)）；仓库收尾见[清理交付记录](../../delivery/2026-10-23-cleanup-delivery.md)。

### TI-01～TI-06 契约、编辑、组合、撤销、几何

- [TI-01～TI-02 输入与编辑审核](2026-10-17-text-input-and-editing-review.md)（[证据](2026-10-17-text-input-and-editing-review-evidence/)）
- [TI-02 契约审核](2026-10-17-ti02-contract-review.md)
- [TI-03 原子编辑审核](2026-10-17-ti03-atomic-editing-review.md)（[证据](2026-10-17-ti03-atomic-editing-review-evidence/)）
- [TI-04 组合状态机审核](2026-10-18-ti04-composition-review.md)（[证据](2026-10-18-ti04-composition-review-evidence/)）
- [TI-05 撤销重做审核](2026-10-19-ti05-undo-redo-review.md)（[证据](2026-10-19-ti05-undo-redo-review-evidence/)）
- [TI-06 预编辑几何审核](2026-10-19-ti06-preedit-geometry-review.md)（[证据](2026-10-19-ti06-preedit-geometry-review-evidence/)）

### TI-07 前期路线与原型审核（R29～R34）

- [TI-07 Windows IME 审核](2026-10-19-ti07-windows-ime-review.md)（[证据](2026-10-19-ti07-windows-ime-review-evidence/)）
- [R29a 子表面审核](2026-10-20-ti07-r29a-child-surface-review.md)
- [R29b 连续性审核](2026-10-20-ti07-r29b-continuity-review.md)、[R29b 真实候选审核](2026-10-20-ti07-r29b-real-candidate-review.md)、[R29b 可见执行器审核](2026-10-20-ti07-r29b-visible-executor-review.md)
- [R30 可见性审核](2026-10-20-ti07-r30-visibility-review.md)
- [R31 文档焦点审核](2026-10-20-ti07-r31-document-focus-review.md)、[R31 真实焦点审核](2026-10-20-ti07-r31-real-focus-review.md)
- [R32 路线研究审核](2026-10-20-ti07-r32-route-research-review.md)
- [R33 既有宿主审核](2026-10-20-ti07-r33-established-host-review.md)
- [R34 呈现器审核](2026-10-20-ti07-r34-presenter-review.md)、[R34e 恢复审核](2026-10-21-ti07-r34e-restore-review.md)
- [TSF 原型审核](2026-10-20-ti07-tsf-prototype-review.md)（[证据](2026-10-20-ti07-tsf-prototype-review-evidence/)）

### TI-07 自绘候选与宿主实测

- [候选呈现器审核证据](2026-10-21-ti07-candidate-presenter-review-evidence/)
- [宿主自绘输入法就绪](2026-10-21-ti07-hostdraw-profile-readiness.md)
- [宿主自绘真实布局审核](2026-10-21-ti07-hostdraw-real-layout-review.md)
- [宿主自绘真实视觉审核](2026-10-21-ti07-hostdraw-real-visual-review.md)

### TI-07 正式生产接入 P1～P4

- [P1 审核](2026-10-21-ti07-production-p1-review.md)（[证据](2026-10-21-ti07-production-p1-review-evidence/)）
- [P2a 审核](2026-10-21-ti07-production-p2a-review.md)（[证据](2026-10-21-ti07-production-p2a-review-evidence/)）
- [P2b owner 审核](2026-10-21-ti07-production-p2b-owner-review.md)（[证据](2026-10-21-ti07-production-p2b-owner-review-evidence/)）
- [P2b 协议审核](2026-10-21-ti07-production-p2b-protocol-review.md)（[证据](2026-10-21-ti07-production-p2b-protocol-review-evidence/)）
- [P3a 宿主路由审核](2026-10-21-ti07-production-p3a-host-routing-review.md)（[证据](2026-10-21-ti07-production-p3a-host-routing-review-evidence/)）
- [接续审核](2026-10-21-ti07-continuation-review.md)（[证据](2026-10-21-ti07-continuation-review-evidence/)）
- [P3b 宿主接线](2026-10-21-ti07-p3b-host-integration.md)（[证据](2026-10-21-ti07-p3b-host-integration-evidence/)）
- P3b 证据目录：[batch-a](2026-10-21-ti07-p3b-batch-a-evidence/)、[p3b](2026-10-21-ti07-p3b-evidence/)、[real-ime(10-21)](2026-10-21-ti07-p3b-real-ime-evidence/)、[real-ime(10-22)](2026-10-22-ti07-p3b-real-ime-evidence/)
- [P3b 独立审核](2026-10-22-ti07-p3b-independent-review.md)、[P3b 真实 IME 审核](2026-10-22-ti07-p3b-real-ime-review.md)
- [P4 取消安全审核](2026-10-22-ti07-p4-cancel-safety-review.md)、[P4 产品边界审核](2026-10-22-ti07-p4-product-boundary-review.md)、[P4 证据审计](2026-10-22-ti07-p4-evidence-audit.md)、[P4 恢复核心审核](2026-10-22-ti07-p4-restored-core-review.md)
- P4 记录与验证：[P4 交付记录](2026-10-22-ti07-p4-delivery-record/)、[P4 真实验证](2026-10-22-ti07-p4-real-validation/)

### TI-08～TI-10

- [TI-08 验证证据](2026-10-22-ti08-validation-evidence/)
- [TI-10 真实 IME 证据](2026-10-23-ti10-real-ime-evidence/)（18 场景 JSONL/截图/SHA256SUMS）

## 关联交付记录（docs/delivery）

- [响应式布局交付](../../delivery/2026-10-16-responsive-layout-delivery.md)
- [TI-07 候选原型交付](../../delivery/2026-10-21-ti07-candidate-prototype-delivery.md)
- [TI-07 P3b 交付](../../delivery/2026-10-22-ti07-p3b-delivery.md)
- [TI-07 P4 交付](../../delivery/2026-10-22-ti07-p4-delivery.md)
- [TI-08～TI-10 交付](../../delivery/2026-10-23-ti09-ti10-delivery.md)
- [收尾清理交付](../../delivery/2026-10-23-cleanup-delivery.md)
