# PS-02 Panel 实施复核记录（2026-10-31）

日期：2026-10-31。对象：`src/CSharp.UI/Views/Panel.cs`（tag AC80，good 实现）、
`tests/CSharp.UI.Tests/PanelTests.cs`（修订后定稿）、GL 实图证据。
本记录为复核纪实；交付记录待全功能完成后由 PS-05 统一收口。

## 结论：PS-02 主审门通过（2026-10-31）

- **产品源码 AC80 已接受**：`Panel(View content)` API、padding+border 内缩参与
  布局、`CornerRadius`/颜色 paint-only（不 re-layout）、背景→子→边框绘制顺序、
  `GetChildClip` 不变/无 rounded mask/0-padding 可盖圆角取舍——与冻结契约一致，
  无产品阻塞。
- **GL 实图已由主审实读**（3 张，见下）：light 主题化面板（圆角 surface+border、
  文本内缩）、dark 主题差异正确、DPI 1.5 缩放正确；harness 为 throwaway
  （`%TEMP%/panel-smoke`，已按清理规则删除），PNG 原始字节归档于本目录
  `2026-10-31-panel-review-evidence/`。
- **永久测试修订完成并复跑全绿**：按 main 首轮复审要求去 incidental/弱断言——
  - 原 P1 硬 pin stub 默认字体常量（CharWidth 8.4/LineHeight 17.5）→ 改显式
    固定尺寸 child（`Rectangle`/`Frame` 40×20 等）验证 padding+border 后 Desired；
  - 原 P7 "ellipsis" 名不符实（未显式 Ellipsis、只查 child 宽、与 P2/P3 重复）
    → **删除**；文本截断属既有 `Text` 职责，不由 Panel 重复证明；
  - 原 P5 三次 `commands.ToList()` → 改单次遍历/索引查找。
  - **最终定向 8/8 绿**：命令
    `tests/CSharp.UI.Tests/bin/Debug/net10.0/CSharp.UI.Tests.exe -class "CSharp.UI.Tests.PanelTests"`；
    结果由 main 从 good 会话 history 实读（本记录不复制 raw）。**首轮 9 项为
    修订前快照，现行有效计数为 8**，两者不混淆；正式五套件 raw 在 PS-05
    交付证据统一链接。

## GL 证据清单（无改字节复制自 `%TEMP%/panel-smoke-ev/`）

| 文件 | 尺寸 | SHA-256（前16） | PNG 校验 |
|---|---|---|---|
| `ps02-light-themed.png` | 320×240 | `f6d806814f85dc2d` | 签名/IHDR/CRC/IDAT 解压/IEND 全部通过 |
| `ps02-light-dark.png` | 320×240 | `f9d3f2e89a0d9546` | 同上通过 |
| `ps02-dpi15-scaled.png` | 300×180 | `c30e8852ae48cbc0` | 同上通过 |

完整性校验方式：PNG 8 字节签名、逐 chunk CRC32 校验、IDAT 流 zlib 解压成功、
IEND 收尾在位（静态字节校验，未启动 GUI/build/test）。good 首轮 console 记录
12 项 GL 检查 RESULT PASS（见 `history://panel-sidebar-implementation` 回合摘要）；
原始 stdout 未单独留存——如实记"首轮截图已主审实读"，不伪造输出。

## PS-03 ensure-visible 机制（main 已批准方向，写入 plan/PS-01 契约）

- **已核实事实**：`UiRuntime.DispatchKeyHandled`（`UiRuntime.cs` 约 :870-880）
  只设 originToken→`_router.DispatchKey`→finally 清，**无** `ProcessPendingWork`；
  `InputRouter.cs` 约 :365-394 同样无布局同步——不能假设 key 前必有最新 layout。
- **批准的挂法**：Runtime **单 slot 按需 bring-into-view 请求**——仅键盘 active
  移动挂目标；在既有 `ProcessPendingWork` 稳定 while 内、**仅当 `!progressed`**
  （布局/响应式/overlay 已收敛）时消费一次：清 slot、target 合法（mounted/
  enabled/仍 focused）且 delta 非 0 → 写最近祖先 `ScrollView` offset 并返回
  `progressed=true`，沿既有 `_layoutDirty` 收敛链重排；最终 focus/router
  validate/hover/text-geometry 均在最终 frame 上运行。无请求时单 null check，
  无全树扫描、无 poll/timer、无公共 API。
- **禁止**：Arrange 内对祖先 invalidate（`RunLayout` 末尾清 dirty 会吃掉）；
  `_focus.Validate` 后手工 `RunLayout` 绕过 responsive/overlay 收敛链。
- pending 目标可存 owner + 按当前描述/active `Value` 解析最新 row；旧 target
  unmounted/disabled/失焦自然 drop，不持续重试；消费后二次布局不得再次请求
  （无回弹）。具体方法名/字段由 good 证明后报主审，文档不 pin 私有名。

### 机制补充（main 定稿，2026-10-31）

- main 已核查 **key dispatch 无布局同步**（`DispatchKeyHandled` 无
  `ProcessPendingWork`，见上节）并批准**单 slot 按需请求**：消费点在
  `ProcessPendingWork` 既有稳定 while 内 `!progressed` 时——语义为"布局/
  响应式/overlay 收敛链已停稳"的帧内时机，不是 pending 对象/string/delegate
  intent 的泛指。消费即清 slot、合法 target 写最近祖先 `ScrollView` offset 并
  返回 `progressed=true` 复用既有 `_layoutDirty` 收敛链。
- 请求不挂未来内容：按**发起时** items/描述建立的目标，不随后续未 reconcile
  的描述重解释；旧 target unmounted/disabled/失焦自然 drop。
- 公开文档只写可观察行为（键盘 ensure-visible、无公共滚动 API），不公开内部
  hook/挂点名。

## 分工状态

- good（panel-sidebar-implementation）：**PS-02 已通过**；当前实施 Sidebar +
  runtime 最小接缝与消费者测试（PS-03 进行中：首轮 Sidebar 25/Core 820 绿，
  结构 runtime AAFF/Sidebar 4EE4 已 read 接受；仍等强化 pointer 跨 mutation、
  真实 geometry dirty 与 GL smoke，**未验收**）。
- 本代理：docs-only；`docs/guide/api-reference.md` 已加 Panel/Sidebar 节
  （冻结 API 与行为说明，不宣称整轮交付）。任务状态见 `docs/task/README.md`。
- main：每任务独立 read 复审；PS-03 过门后才开 PS-04 Gallery 集成。

