# SB-01：共享滚动条几何与交互 helper

创建日期：2026-10-23

状态：待开始

阶段：A

设计依据：[Scrollbar 计划](../plan/2026-10-23-scrollbar-plan.md) §决策三（公式冻结，坐标绝对）。

[返回任务索引](README.md)

## 目标

新增 `internal` 共享 helper `ScrollbarMetrics`，为 ScrollView/TextEditor 提供统一轨道/滑块几何、命中判定、拖拽→偏移换算、track 点击方向——**不含** owner 状态、不接线、不画。

## 文件范围

- `src/CSharp.UI/Views/Scrollbar.cs`（新）：`ScrollbarMetrics` 静态方法 + 返回值类型（`Rect`/`bool`/`double`/`enum`）。
- `tests/CSharp.UI.Tests/` 对应单元测试。

## 冻结公式（实现者不得更改）

令（**全部绝对坐标**，`s = trackRect[axis].Start + inset` = **inset 后**实际轨道起点）：
- `T = max(0, trackRect[axis].Length − 2×inset)`，`inset=2`
- `V` = 视口沿滚动轴 extent，`C` = 内容沿滚动轴 extent，`M = max(0, C − V)`
- `ShowScrollbar = M > 0 && V > 0 && T > 0 && double.IsFinite(C) && double.IsFinite(V)`（`C<=0`/`M<=0`/`V<=0`/`T<=0`/非有限 → false）

显示时：
- `thumbLength = Math.Clamp(T * V / C, Math.Min(T, MinThumbLength), T)`
- `travel = T − thumbLength`；`travel<=0`（`thumbLength>=T`，如 `C>V` 但 `T<=24` 全满）→ 仍显示满 thumb（`thumbLeading=s`），**drag 无剩余行程**、track 点击无区域（非 `ShowScrollbar=false`）
- `thumbLeading = s + (clamp(offset, 0, M) / M) * travel`（`M>0` 保证；`travel<=0` 时退化为 `s`）
- `thumbRect = [thumbLeading, thumbLeading+thumbLength]`（cross 向居中 `trackRect`）
- 命中（`p` = pointer 沿滚动轴**绝对**坐标）：`p < thumbLeading` → `TrackHit.Before`；`p > thumbLeading+thumbLength` → `After`；`thumbLeading ≤ p ≤ thumbLeading+thumbLength` → `Thumb`；`travel<=0` → 仅 `Thumb` 全域
- drag：`grab = pDown − thumbLeading`（按下点距 thumb 起点的偏移，**含 `s`**）；`offset = clamp(((p − s − grab) / travel), 0, 1) * M`（`travel<=0` 时 `offset` 不变/返回当前值，不产生 NaN）
- track 点击：`Before` → `offset −= V`；`After` → `offset += V`；一次性，无 auto-repeat

## 常量（内部，非公开 API）

`ScrollbarThickness = 12`、`MinThumbLength = 24`、`inset = 2`。DPI 经 `pixelScale` 既有路径换算；gutter 宽由 caller `min(ScrollbarThickness, max(0, crossExtent))` 自适应（极窄 frame 不产负 rect）。

## 边界

- 零公开 API；零 owner 状态；零绘制；零无限 constraints 算术（`double.PositiveInfinity` 由 `ShowScrollbar` guard 拦截）。
- 返回类型用框架值类型；不引入 OpenTK/Skia。
- 不提供 `IsHitTestVisible` 语义——槽恒属 owner frame，命中判定归 caller `OnPointerDown` 查 `trackRect`+`ShowScrollbar`。

## 验收

- 单元：`C=2V`→thumb=T/2、有限大 `C`→thumb=MinThumbLength=24（`C→∞` 不显示见下行）、`C<=V`/`T<=0`/`V<=0`/`C=inf`/`V=inf`→不显示、track 三区、`grab` 含 `s` 防跳变、drag 夹取 `[0,M]`、`travel<=0` 显示满 thumb（`thumbLeading=s`）但 drag 不变/track 无区、零分配。
- 不接线：ScrollView/TextEditor 无改动；既有基线（594+85+53+678）绿。
