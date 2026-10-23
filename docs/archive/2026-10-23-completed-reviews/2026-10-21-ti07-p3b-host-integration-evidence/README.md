# TI-07 P3b 宿主接线证据

日期：2026-10-21
说明：`proc://` 作业完成后不再保留原始 stdout；本目录保存的是执行时在会话记录中实际出现的
summary/exit 行（逐字），不是事后重建。完整控制台流未取得，已在对应文件标注。

- `raw-host-smoke.txt`：正式宿主 `TsfHostIntegrationTests` 的 summary 与 exit。
- `raw-release-summary.txt`：Release 合并回归的 summary 与 exit。
- 其余 Debug 类级计数（sink/geometry/overlay/store/cancel/lifetime）只有执行记录中的计数，
  原始 stdout 未持久；下一轮 real-ime 独立运行会自然产生新日志。

对应执行记录：[host-integration.md](../2026-10-21-ti07-p3b-host-integration.md)。
