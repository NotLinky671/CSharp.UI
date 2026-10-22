# TI-07 P3b 交付证据索引（2026-10-22）

本目录只做**索引/指向**，不复制截图与大日志；原始证据全部保留在
`docs/return/2026-10-22-ti07-p3b-real-ime-evidence/` 与
`docs/return/2026-10-21-ti07-p3b-evidence/`。

## 最终 Release（原始 stdout/stderr/exit）

- `release-build-final/`：构建 exit 0。
- `release-final-<Class>/`：AcpTextStoreProtocolTests 75、CandidateSnapshotContractTests 38、TsfUiElementSinkTests 18、HostAcpGeometryProviderTests 5、CandidateOverlayRenderingTests 4、TsfCancellationTests 4、TsfSessionLifetimeTests 1、TsfLayoutNotificationRegressionTests 1、TsfHostIntegrationTests 1、RouterFallbackTests 7、WindowsInputBridgeContractTests 12；合计 **166/166**、Failed 0、exit 0、stderr 空。
- `release-TsfHostIntegrationTests-final/`、`release-TsfLayoutNotificationRegressionTests-final/`：加非 Windows 守卫后的 Windows 复跑 1/1、1/1。
- `tool-selftest-final/`：all pass、exit 0。
- 执行记录：[2026-10-22-ti07-p3b-real-ime-review.md](../../return/2026-10-22-ti07-p3b-real-ime-review.md)。

## 真实微软拼音与宿主（原始 JSONL/PNG/exit）

- `nihao-visible-baseline/`：640 高窗 9 项候选可见、预编辑零写、Space 精确“你好”+1，exit 0。
- `container-scroll/`：0→7→324→0；同一 session/composition/element generation/preedit；裁出隐藏、恢复重发；setter 0；Space“你好”+1；四张 PNG；exit 0。
- `escape-cancel/`、`digit-select/`、`segmented/`：分别零写取消、精确“你好”+1、精确“你好世界”+2；exit 0。
- `enter-select-candidate-ready-final/`：候选 ready=true、原始拼音 `nihao` +1、无泄漏、组合结束、host 存活；exit 0。
- `default-imm-unicode-direct/`：`中😀`、2 次标量写入；合成字符通道，非 IME 证据；exit 0。

## 修复轨迹（原始）

- `build-scroll-helper/`、`build-enter-ready-bounded/`、`build-enter-end-wait/`（失败）与对应 `-retry`（成功）。
- `enter-select/`、`enter-select-candidate-ready/`：候选 ready 前置的首失败与过程证据。

## 非原始（会话转录）

- `docs/return/2026-10-22-ti07-p3b-independent-review.md` 的 Debug 147 项计数：real-ime 会话执行转录，原始 stdout 未持久；不伪造原文。
- 21 日宿主接线 summary 转录见 `docs/return/2026-10-21-ti07-p3b-host-integration-evidence/`（目录 README 已标注）。

## 边界

本索引不改变交付结论：P3b 范围通过；TI-07/P4 未通过，未覆盖矩阵见
[P3b 交付记录](../2026-10-22-ti07-p3b-delivery.md) §5。未提交、未推送。
