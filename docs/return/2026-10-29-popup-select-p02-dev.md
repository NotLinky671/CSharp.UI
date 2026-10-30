# P-02 开发记录（2026-10-29）

任务：[P-02 PopupPlacementCalculator](../task/02-popup-placement.md)
计划：[2026-10-29 Popup 覆盖层与 Select 实施计划](../plan/2026-10-29-popup-and-select-plan.md) §3
基线：P-01 主审通过（独立证据见 `docs/return/2026-10-29-popup-select-p01-evidence/`）。
状态：**已实施、自测通过，待主审验收**；未提交、未推送、未勾 P-02。

## 改动

新增：

- `src/CSharp.UI/Runtime/PopupPlacementCalculator.cs`：internal 纯几何 `Resolve(anchorRect, contentDesired, popupBounds, preference, maxHeight) → Rect?`；
  内部 `PopupPlacementSide { Auto, Above, Below }`（P-03 再迁 public `PopupPlacement`，本轮不建 alias）。
  - `popupBounds` 已内缩 margin，函数不再二次扣；
  - Auto：`desiredH ≤ spaceBelow` 或 `spaceBelow ≥ spaceAbove` → Below，否则 Above；Above/Below 强制不翻转；
  - `h = min(desiredH, maxHeight, sideAvail, boundsH)`（side 可用优先）；`w = clamp(desiredW, 0, boundsW)`；
  - 零/负 bounds、零尺寸内容、非有限输入、`h/w ≤ 0` → `null`（关闭信号）。
- `tests/CSharp.UI.Tests/PopupPlacementTests.cs`：B1–B8 纯函数 + B9 系列 runtime（宽文本/限高 ScrollView/强制 Above/零空间安全关闭）。

修改：

- `src/CSharp.UI/Runtime/OverlayManager.cs`：
  - `ActiveOverlayEntry`/`PendingShow` 增加 `PopupPlacementSide Preference`；`Show(..., PopupPlacementSide preference = Auto)`（可选末参，P-03 使用）；
  - `ArrangeEntry` 改用 calculator；最终 `(w,h)` 与 DesiredSize 不同时最多一次 `MeasureFresh`；
  - 新增 `_geometryInvalid`：Resolve null 时 Arrange 只排 `Rect.Empty` 并置关闭意图，安全阶段 `ConsumePending` 关闭（不在 Arrange 内 unmount）；`HasPendingWork` 纳入该意图。
- `tests/CSharp.UI.Tests/OverlayRootTests.cs`（仅 A11 fixture）：`LayoutShowHost.Arrange` 让宿主锚占据 120×40 @ (20,20) 的小矩形——P-02 下“锚覆盖整个视口 ⇒ 零可用空间 ⇒ 关闭”是新契约，A11 只验证布局内 Show 延迟消费；断言未改。
- `docs/task/02-popup-placement.md`、`docs/task/README.md`：契约/状态更新；P-01 勾选并引用独立证据，P-02 标“已实施待验收”，后续不预勾。

## 命令与结果（原始日志在 `docs/return/2026-10-29-popup-select-p02-evidence/`）

| 命令 | 结果 |
|---|---|
| `dotnet build src/CSharp.UI/CSharp.UI.csproj -c Debug` | 0 警告 0 错误，exit 0 → `core-build.stdout/stderr.txt` |
| `dotnet build tests/CSharp.UI.Tests/CSharp.UI.Tests.csproj -c Debug` | 0 警告 0 错误，exit 0 → `tests-build*.stdout/stderr.txt` |
| `dotnet run --no-build --project tests/CSharp.UI.Tests -c Debug` | Total 746, Failed 0, exit 0 → `core-tests3.stdout/stderr.txt`（首轮 A11 因零空间语义失败，fixture 调整后全绿；`core-tests.stdout.txt` 保留失败原始；`core-tests2.*` 为 745 例、加入 B9e 前） |
| 外 probe `dotnet build/run -c Release` | build exit 0；run exit 0，RESULT PASS → `probe-build2/probe-run2.*`（`probe-run.*` 为 adapter 未适配 6 参数 Show 的一次失败原始） |
| `dotnet run --no-build ... -- -class "CSharp.UI.Tests.PopupPlacementTests"` | Total 14, Failed 0, exit 0 → `placement-tests.stdout/stderr.txt`（含 B9e 零尺寸内容关闭） |

未运行：全 OpenTK 套件（已知 80px 基线失败，P-07 统一）、Integration/Markdown（无改动）。

## 备注

- `PopupPlacementSide` 暂为 internal，仅几何使用；P-03 干净迁移到 public `PopupPlacement`，无 legacy alias。
- 零空间关闭：`ProcessPendingWork` 返回后 `HasActiveEntry=false`，popup 节点卸载；A11 首轮失败即该契约生效的证据原始保留。
