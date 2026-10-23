# TE-07：交付

创建日期：2026-10-23

状态：完成——交付记录与证据目录已生成并验证（[text-editor-delivery](../../delivery/2026-10-23-text-editor-delivery.md) + `2026-10-23-text-editor-delivery-evidence/`）：构建 0 警 0 错 ×2、四套测试 1409×2 全绿、6 次 smoke exit 0、真 IME 28/28 check PASS、SHA-256 manifest 复核 OK。最终提交待主审 gate。设计依据 [TextEditor 计划](../../plan/2026-10-23-text-editor-plan.md) §5、§7。

[返回任务索引](README.md)

## 目标

全部前置任务审核通过后，按既有发布口径完成最终构建、测试、smoke 与交付记录；不预先写结论。

## 文件范围

- `docs/delivery/2026-10-23-text-editor-delivery.md` + `docs/delivery/2026-10-23-text-editor-delivery-evidence/`。
- 构建/测试命令与真实计数。

## 步骤

1. Debug/Release `dotnet build CSharp.UI.slnx` 0 警告 0 错误。
2. 四套测试（`CSharp.UI.Tests`/`CSharp.UI.Text.Tests`/`CSharp.UI.Integration.Tests`/`CSharp.UI.OpenTK.Tests`）两配置全绿。
3. 三示例 Debug/Release 构建 + `CSHARPUI_SMOKE_FRAMES=5` smoke 各通过。
4. 交付记录：实际命令/退出码/测试计数、功能清单、真实 IME 证据引用、已知边界（非 Windows committed-only、多 DPI、TSF 内部路径现状、鼠标候选选择不支持）。
5. 根 README 与 `docs/archive/2026-10-23-text-editor-tasks/README.md` 状态更新为已完成（链接交付记录）。

## 验收

- 交付文档如实记录证据与边界；无未验证即宣称的结论；任务索引全部勾选。
