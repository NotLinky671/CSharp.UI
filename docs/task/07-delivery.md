# SB-07：交付（构建 + 真实 smoke + 记录）

创建日期：2026-10-23

状态：待开始

阶段：D

设计依据：[Scrollbar 计划](../plan/2026-10-23-scrollbar-plan.md)；前置依赖见 [任务索引](README.md)（SB-06）。

[返回任务索引](README.md)

前置依赖：SB-06。

## 目标

Debug/Release 全 suite、3 sample 双配置标准 bin、Windows 真实窗口 smoke（drag/track/hide/横竖 + light/dark + 多 DPI 离屏 + real-150% + default-IMM MSPinyin 锚点/裁出不回弹）、docs/delivery 带日期记录、英文 commit/push 门。

## 交付内容

- **构建**：`dotnet build CSharp.UI.slnx -c Debug` 与 `-c Release` 均 0 警 0 错。
- **测试**：`CSharp.UI.Tests` / `CSharp.UI.Text.Tests` / `CSharp.UI.Integration.Tests` / `CSharp.UI.OpenTK.Tests` **两套配置各**全绿（基线 594+85+53+678 + 本组新增）。
- **Sample**：`Counter`/`TodoList`/`ControlGallery` 三 sample `dotnet build -c Debug` 与 `-c Release` **各**产生标准 `bin/{Debug,Release}/net10.0/` 产物（6 个 bin 目录）；**6 次 smoke**（三 sample × 两配置，`CSHARPUI_SMOKE_FRAMES=5`）exit 0。
- **真实窗口 smoke**（Windows，fast 记录）：overflow show/hide、thumb drag、track 翻页、boundary 冒泡、IME anchor 跟随——**纵/横**、**light/dark** 主题、**多 DPI 离屏**（100%/150%/200% offscreen 几何 GL 实际 surface 验证 thumb 位置/尺寸/吸附）与 **real-150% 窗口**区分记录；**default IMM MSPinyin** 组合期 scrollbar 拖拽→`ScrollY` 变、caret 裁出 `Unavailable` 期间组合保活、回视口重锚不回弹。
- **交付记录**：`docs/delivery/2026-10-23-scrollbar-delivery.md`（命令/退出码/测试计数/证据路径/平台边界）。
- **提交**：主审批准后英文 commit + push（非实施者自主）。

## 平台边界（如实）

- Windows：真实窗口 + mouse/wheel/IME smoke + real-150% + 多 DPI offscreen。
- macOS/Linux：**仅本机平台无关 core/CPU 模拟验证**（不承诺 macOS/Linux 原生 build、原生 scrollbar/IME 行为或不可用环境验证；scrollbar 为纯视图逻辑无平台特化）。

## 边界

- 不把 CPU 测试绿当真机通过；不把 sample 编译当 smoke；不把 offscreen 多 DPI 当 real-150%。
- 验收命令逐条可复现；证据不只放可清理 artifacts。

## 验收

- 上述交付项齐备；主审 read 通过；commit 英文 message；push 后状态"已完成"。
