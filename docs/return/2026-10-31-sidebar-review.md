# PS-03 Sidebar 实施复核记录（2026-10-31）

日期：2026-10-31。对象：`src/CSharp.UI/Views/Sidebar.cs`（tag 4EE4）、runtime 接缝
（`UiRuntime` 最小单 slot bring-into-view，tag AAFF）、
`tests/CSharp.UI.Tests/` Sidebar 测试、GL 实图证据。
本记录为复核纪实；交付记录待全功能完成后由 PS-05 统一收口。

## 结论：PS-03 主审门通过（2026-10-31）

- **产品源码已读接受**：`Sidebar<T>`/`SidebarItem<T>`（tag 4EE4）与 runtime
  ensure-visible 接缝（tag AAFF）结构经主审 read 接受——与冻结契约及批准的
  单 slot 稳定环消费机制一致。
- **永久测试**：首轮 Sidebar 定向 **25 绿**（`CSharp.UI.Tests` Core 当时 820）。
  修订强化后为 **Sidebar 27 绿**——新增：三种 pointer press 跨 items
  mutation（Down 后重排/删除/禁用/替换，Up 不写错值，0 次写回错误项）、真实
  viewport 缩小的 geometry dirty ensure-visible、Tab 真实焦点顺序。
  **如实记录**：新 27 项未做 Core 全量复跑——旧计数 820 对应 25 版本，不捏造
  "822 全绿"；最终五套件 fresh 计数在 PS-05 交付统一产出。
- **GL 原生 smoke 16/16 PASS**（RESULT PASS）：light/dark ×
  mounted / select-write / selected-fill / disabled-not-sel / kbd-commit /
  end-frame-visible / end-row-drawn / wheel-moved-up；raw stdout 与 8 张 PNG
  归档于 `2026-10-31-sidebar-review-evidence/`（含冻结 producer 源
  `sidebar-smoke-Program.cs` + `sidebar-smoke.csproj`，harness 本体已删）。
  main 已实读 dark selected、light end-visible 等真图。
- **行为边界如实声明**：Sidebar 为常驻列表——**无 expanded/collapse/opening
  状态**；active 初始化于首次键盘交互（有效 enabled 当前绑定值优先，否则首个
  可用项）；提交唯一路径 pointer 同行 Up / `Enter`/`Space`；`Up`/`Down`/
  `Home`/`End` 只移 active；`Tab` 不消费；`Ctrl`/`Meta`/`Alt` 组合不导航；
  用户滚轮/平移/外部写值不触发 ensure（无回弹）。

## GL 证据清单（无改字节复制自 `%TEMP%/sb-smoke-ev/`）

| 文件 | 尺寸 | SHA-256（前16） |
|---|---|---|
| `a-light-selected.png` | 360×450 | `04903ec7d1dbbc8f` |
| `a-light-active.png` | 360×450 | `fe0b5ffd31f71331` |
| `a-light-end-visible.png` | 360×450 | `90e3169c903776d7` |
| `a-light-wheel-browse.png` | 360×450 | `9efe9b161fa84ab2` |
| `a-dark-selected.png` | 360×450 | `c87f23afca003f78` |
| `a-dark-active.png` | 360×450 | `f7219407b69a8165` |
| `a-dark-end-visible.png` | 360×450 | `a428d28224699abe` |
| `a-dark-wheel-browse.png` | 360×450 | `311ab0166258f946` |
| `driver.stdout.txt` | 1119 B | `2a1896bc5f1fbdd4` |
| `sidebar-smoke-Program.cs` | 8205 B | `694e86760d9a3d30` |
| `sidebar-smoke.csproj` | 1030 B | `cc99e90cc144909a` |

完整性：8 PNG 签名/逐 chunk CRC32/IDAT 解压/IEND 全部通过（静态字节校验）。
stdout 原始 16 PASS + RESULT PASS 逐行如上归档文件，未改写。

## 分工状态

- good（panel-sidebar-implementation）：PS-02、PS-03 均已通过；当前 PS-04
  Gallery 集成进行中（独占 samples/src/tests；原生 instrumented Gallery 多宽/
  主题/长 sidebar 真实鼠标键盘滚动实证由其实施）。
- 本代理：docs-only；`api-reference.md` Panel/Sidebar 节已就位（Guide 8D63 主审
  已读可用），最终随 Gallery 真实接口收口；README scope 列表待全过门后补。
- main：PS-03 已接受；PS-04 完成后独立 read 复审。
