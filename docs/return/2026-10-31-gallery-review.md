# PS-04 Gallery 集成与原生实证复核记录（2026-10-31）

日期：2026-10-31。对象：`samples/CSharp.UI.Samples.ControlGallery/` PanelSidebarDemo
集成（good 实施，源码冻结）、原生实证双通道证据（self + 独立复跑）。

## 结论：PS-04 主审门通过（2026-10-31）

### 证据来源一：self 原生矩阵（good 实施期产出，main 已实读）

冻结 producer（`native-self/producer/Program.cs` sha256 `f045f7d7…`）驱动真实
Release `GalleryApp` + 生产 `OpenTkHost`（`UiApplication.Run`，仅观察回调），
owned-HWND SendInput、`GL.ReadPixels(FRONT)`、P_EV/P_WIDTH/P_THEME 环境参数：

| run | `^PASS ` 检查 | 结果 |
|---|---:|---|
| 1024-light | 19 | RESULT PASS，`UiApplication.Run exit=0` |
| 1024-dark | 21 | RESULT PASS（含真实 Header 主题点击切换） |
| 320-light | 19 | RESULT PASS |
| 320-dark | 21 | RESULT PASS |

（计数口径：`^PASS ` 行首锚定，`RESULT PASS` 非 check；早前宽松口径
20/22/20/22 已修正。）
Enter 提交、Tab 离开 sidebar、长列表（140px 视口）End 末行完全在 clip 内 →
真实鼠标提交 nav-30、wheel-up 偏移减小**无回弹**、`host-exited=False`。
原始字节归档：`docs/delivery/2026-10-31-panel-sidebar-delivery-evidence/native-self/`。

### 证据来源二：独立复跑（final gate，本代理执行）

- **`native-independent/`**（原 self producer 原样，`P_EV/P_WIDTH=1024/P_THEME=dark`）：
  21 项 `^PASS ` 检查、RESULT PASS、真实 shell EXIT=0、`host-exited=False`、7 PNG 全部
  decode/CRC 通过。
- **`native-independent-down/`**（copied producer 扩展，源+csproj 冻结于
  `native-independent-down-producer/`，Program.cs sha256 `ae6f6cee…`——与 self
  producer 不同源，独立留痕）：补齐 task04 要求的 **Down+Space→settings 精确值**
  与 **Tab→离开、Shift+Tab→回 sidebar owner**——23 PASS、RESULT PASS、EXIT=0。
  `kbd-space-commit-value`、`shifttab-returns-sidebar` 为真实 SendInput
  （ShiftDown+Tab+ShiftUp 批次、own-HWND/foreground/WindowFromPoint 校验）。

### 计数纪律（如实）

- setter"恰好一次写"由永久测试 Binding 计数断言承载；native 侧只断言精确值
  （不冒充计数断言）。
- self 四组与独立两组**互不替代**：self 已归档原始字节，独立为 final gate
  复跑证据。
- UI 输入未遇失败；无重试、无 fake fallback。

## 分工与后续

- good 实施会话已结束（completed），产品/tests/samples 冻结。
- PS-05 收口由本代理执行：fresh 双 config 构建、五套件 direct-exe、文档收口、
  交付记录；main 读最终 evidence/docs 后授权单次英文 commit/push。
