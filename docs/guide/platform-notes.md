# 平台、字体与原生依赖

## 三平台前提

| 平台 | 架构 | 前提 |
|---|---|---|
| Windows | x64 | 桌面会话 + 支持 OpenGL 3.3 的驱动（本项目在 Windows 上完成真机验证，见下） |
| Linux | x64 | X11 或 Wayland 会话 + Mesa（OpenGL 3.3）；无显示器时用 Xvfb + `LIBGL_ALWAYS_SOFTWARE=1` |
| macOS | x64 / arm64 | 10.15+，创建 Forward Compatible 的 OpenGL 3.3 Core 上下文 |

渲染基线为 OpenGL 3.3 Core / GLSL 330，只使用三平台共有能力；macOS 上以 `ContextFlags.ForwardCompatible` 创建上下文。

## 原生依赖

| 依赖 | 来源 | 用途 |
|---|---|---|
| GLFW | `OpenTK 4.9.4`（`runtimes/<rid>/native/glfw3.dll` / `libglfw.so.3` / `libglfw.3.dylib`） | 窗口、输入、剪贴板、事件循环 |
| libSkiaSharp | `SkiaSharp 3.119.4` + `SkiaSharp.NativeAssets.*` | CPU 文本栅格化、图片解码 |
| libHarfBuzzSharp | `HarfBuzzSharp 8.3.1.5` + `HarfBuzzSharp.NativeAssets.*` | 文本 shaping |

发布检查覆盖 Windows x64、Linux x64、macOS x64/arm64 四种架构（命令与结果见 [../task/14-cross-platform-validation.md](../task/14-cross-platform-validation.md)）。
核心库 `CSharp.UI` **不引用** OpenTK、SkiaSharp 或任何原生库，因此核心单元测试不需要图形上下文。

## 字体与回退

字体解析顺序：

1. `FontFamily.FromFile(path)`：应用指定的字体文件（文件不存在或无效时给出带路径的诊断并回退）；
2. `FontFamily.FromName(name)`：系统字体家族名；
3. `FontFamily.Default`：平台默认字体；
4. 缺字回退：`SKFontManager.MatchCharacter`，保证中文可显示。

`FontFamily.WithFallbacks(...)` 可按顺序追加备选家族。

> **字体许可**：本仓库**不重新分发第三方字体二进制文件**。示例使用系统字体与其回退（Windows: Microsoft YaHei / Segoe UI；macOS: PingFang SC / Helvetica；Linux: Noto Sans CJK）。
> 需要跨机器可复现的截图时，请自行把许可允许分发的字体放到应用资源目录，并用 `FontFamily.FromFile` 指定。

## 示例资源

`samples/Shared/Assets/logo.png` 由仓库自带的工具绘制生成（不使用任何第三方素材，可随仓库分发）：

```bash
dotnet run --project tools/CSharp.UI.AssetGenerator
```

## 文本输入的实际边界

首版支持：

- 宿主提交的 Unicode 文本（例如中文输入法上屏后的结果）与剪贴板粘贴（含中文）；
- 按字素边界的光标移动、删除、选择、剪切/复制/粘贴与横向滚动；
- 光标闪烁（仅在获得焦点时定时重绘）。

首版**不包含**：

- 完整的 IME 组合文本、候选窗定位与预编辑高亮（输入法组合期间的中间状态不保证）；
- 多行编辑、富文本、彩色 Emoji 字形图集、完整双向文本排版；
- 系统无障碍桥接、动画系统、列表虚拟化、移动端与浏览器目标。

因此“支持中文显示与粘贴”不等于“完整 IME 支持”。

## 已知限制

- 单窗口：同一进程只允许一个 `UiApplication.Run`。
- `Opacity` 是逐图元相乘，不做离屏分组合成；子项重叠时与整体透明度的视觉效果略有差异。
- `ScrollView` 首版不绘制滚动条，也不做虚拟化，内容全部布局。
- `Rectangle` 圆角为矩形圆角，不支持每角独立半径。
- 图形验证受运行环境影响：缺少 OpenGL 上下文的机器上，图形集成测试会真实失败而不是被跳过。
