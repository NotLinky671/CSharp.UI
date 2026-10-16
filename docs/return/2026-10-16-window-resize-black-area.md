# R18：交互调整窗口大小期间未及时绘制新区域

记录日期：2026-10-16

审核人：Codex

审核版本：`b3fc734`

优先级：P2

状态：已定位宿主重绘缺口并独立验证回调行为，待修复；本轮未修改框架实现。

## 用户现象

用户用鼠标调整 ControlGallery 窗口大小时，旧内容保留在左上角，新扩出的右侧和底部为黑色。截图的标题栏已经覆盖新窗口宽度，UI 背景却只覆盖较小的旧矩形，符合窗口已变大但没有及时提交新帧的表现。

## 代码原因与证据

- [OpenTkHost.HookWindowEvents](../../src/CSharp.UI.OpenTK/Hosting/OpenTkHost.cs) 的 Resize/FramebufferResize 只调用 UpdateViewport，Refresh 只调用 InvalidateRender；两者均不在回调中执行绘制和 SwapBuffers。
- Run 只在 PumpEvents 返回、处理排队动作和布局后调用 DrawFrame。Windows 默认窗口过程在鼠标拖拽边框期间进入移动/调整大小的模态循环，不能假设外层 PollEvents/WaitEvents 会及时返回并继续普通绘制迭代。
- 因此尺寸可以已更新、NeedsDraw 也为 true，但新帧仍未提交。系统扩出的缓冲区没有被应用背景色覆盖，可以呈现用户截图中的黑带。

官方依据：[Windows WM_ENTERSIZEMOVE](https://learn.microsoft.com/en-us/windows/win32/winmsg/wm-entersizemove) 说明默认窗口过程会进入调整大小模态循环；[GLFW 窗口刷新指南](https://www.glfw.org/docs/3.4/window_guide.html#window_refresh) 的刷新回调示例在回调里绘制并交换缓冲。GLFW 3.4 的 Win32 源码把 WM_PAINT 转为窗口 damage/refresh 通知。这里确认的是宿主缺少回调期间绘制路径，未把隐藏窗口的程序化事件测试宣称为鼠标拖拽全过程截图复现。

## 独立隐藏窗口验证

使用真实 OpenTkHost、真实 GL 上下文与默认文本服务。在首帧完成后投递一个 UI 调度动作，确保不在 DrawFrame 内重入，然后扩大 NativeWindow 客户区并显式触发 NativeWindow.OnRefresh，观察回调返回后的状态：

```text
before: frames=1,windowFb=(450, 300),rendererFb=450×300,needsDraw=False
afterResizeAndRefresh: frames=1,windowFb=(690, 460),rendererFb=450×300,logical=460×306.667,needsDraw=True
```

本机 DPI 1.5。窗口 framebuffer 已变为 690×460，运行时逻辑尺寸也更新了，但渲染器仍停留在 450×300，帧数仍为 1。这个结果独立确认“收到尺寸/刷新通知不等于已绘制新区域”。测试随后请求退出，没有启动普通循环的下一次绘制。

复现位于本地忽略目录 `artifacts/resize-review/Verify.csproj`、`Program.cs`，输出为 `result.txt`。没有操作用户当前窗口或更改系统设置。

## 修复要求

1. 在窗口刷新/交互调整大小期间增加主线程即时绘制路径，刷新实际 framebuffer、逻辑视口与 DPI，处理必要布局后完整绘制并交换缓冲。保持空闲时按需重绘，不依赖持续忙循环或修改背景色来掩盖问题。
2. 该路径需防止构造/启动未就绪、最小化/零尺寸、正在绘制、嵌套尺寸事件以及退出/销毁时重入；保留后续重绘请求，不能递归绘制或从后台线程直接操作 GL。避免重入式排空整个事件循环或任意执行全部队列动作。
3. 核对 NativeWindow 缓存尺寸的回调时序，必要时查询当前 GLFW framebuffer，不能在新 framebuffer 上继续提交旧尺寸的 frame。GlRenderer 当前在清屏前启用并设置 scissor，清屏也受它限制；需确保清屏区域对应实际新 framebuffer，不能仅更新 GL.Viewport 后仍用旧 scissor 清屏。
4. 覆盖放大/缩小、两轴连续拖拽、150% DPI、恢复窗口、关闭流程以及嵌套裁剪；验证新增区域的实际背景像素与新视口布局，不只断言 NeedsDraw。
5. 增加回调返回前已提交正确尺寸帧的回归及重入/生命周期保护验证；鼠标拖拽过程须单独验证并区分程序化 resize 与真实原生模态循环。三个示例 Debug/Release 更新，四套完整测试及启动检查通过后英文提交，由 Codex 复审。
