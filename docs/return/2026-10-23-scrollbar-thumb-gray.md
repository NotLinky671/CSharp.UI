# Scrollbar 滑块配色调整（2026-10-23）

日期：2026-10-23。范围：仅 `src/CSharp.UI/Views/Scrollbar.cs`（共享 `ScrollbarPainter.Draw`，同时影响 `ScrollView` 与 `TextEditor`）。无交互/布局/API 变化。

## 变更

原滑块配色为 `theme.Accent`（normal）/`Accent.Lerp(White,0.15)`（hover）/`Accent.Lerp(Black,0.2)`（press）。用户反馈"颜色太深"，按用户反馈改为主题中性灰：

- **normal**: `theme.ControlBackground.Lerp(theme.Foreground, 0.40)`
- **hover**: `theme.ControlBackground.Lerp(theme.Foreground, 0.48)`
- **pressed**: `theme.ControlBackground.Lerp(theme.Foreground, 0.56)`
- **disabled**: `theme.DisabledForeground`（不变）
- **track**: `theme.ControlBackground`/`ControlHoverBackground`/`ControlPressedBackground`（不变）

Light 主题实测：normal `#A4A5A6`、hover `#929394`、pressed `#7F8083`（逐渐加深）。
Dark 主题实测：normal `#72757B`、hover `#82858A`、pressed `#929499`（逐渐提亮以对深底形成对比）。

`theme.Accent` 不再用于滑块，保留给真正需要强调色的控件（主按钮、Toggle、选中态等）。track 与其他控件调色板未动。

## 验证

- primary 复跑：`dotnet run --project tests/CSharp.UI.Tests --no-build -c Debug`（618/618 exit 0）与 `dotnet run --project tests/CSharp.UI.OpenTK.Tests --no-build -c Debug`（678/678 exit 0）；另 primary GPU 驱动（throwaway）对 TextEditor 40 行溢出做两主题 normal/hover/pressed/release+move-off 8 态像素断言全 PASS，raw log：[`2026-10-23-scrollbar-thumb-gray-run.txt`](2026-10-23-scrollbar-thumb-gray-run.txt)。
- 独立表面视觉探针（本记录，throwaway `~/.omp/tmp/scrollbar-gray/`）：真实 `GlOffscreenSurface`+`GlRenderer`+`SkiaTextService`+`UiRuntime.DispatchPointer`，overflow `ScrollView` 与 `TextEditor` 同场景、Light/Dark × normal/hover/pressed（hover/press 每 owner 单独帧，pointer 不同时悬双方）。实测像素与上表一致：Light normal `#A4A5A6` / hover `#929394` / pressed `#7F8083`；Dark normal `#72757B` / hover `#82858A` / pressed `#929499`，均为中性灰非 `Accent` 蓝；bar 点击不夺焦（focused=False）。截图与原始输出：[2026-10-23-scrollbar-gray-evidence/](2026-10-23-scrollbar-gray-evidence/)（10 PNG + stdout/stderr + SHA256SUMS；加载 CSharp.UI.dll SHA256=`b0b80b19…`）。
- 标准示例：`samples/CSharp.UI.Samples.ControlGallery` Debug/Release 各 `--no-incremental` fresh build exit 0；`CSHARPUI_SMOKE_FRAMES=5` Debug 真实建窗渲染 5 帧 exit 0（900×700、DPI 1.5、GL 3.3.0 Intel）。边界：offscreen pixelScale=1.5 参数化非 OS DPI；用户运行中的 Gallery 未关闭、未触碰。
