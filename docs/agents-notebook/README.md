# Agent 编码笔记

本目录记录实现本仓库时踩过的环境坑、约定的编码规范与验证方式，便于后续 Agent（与人工）少走弯路。
每条都注明“现象 / 原因 / 处理方式”，并标注适用范围。

## 执行与审核复盘

- [2026-10-21：Codex 的 TI-07 审核与推进失误复盘](2026-10-21-codex-ti07-review-and-execution-retrospective.md)：任务粒度、真实调用链、测试假通过、回归顺序、环境诊断和进度表达；后续 TI-07 执行前阅读。

---

## 1. 构建与测试环境

### 1.1 必须使用单节点 MSBuild

- 现象：`dotnet build` 退出码 1 并输出“生成失败”，但只有 `0 个错误`；`dotnet restore`/`dotnet run`（隐含构建）同样失败且无明细。
- 原因：本沙箱下 MSBuild 的多进程节点（命名管道通信）会被拒绝。
- 处理：统一使用
  ```bash
  dotnet build <项目> -m:1 -nodeReuse:false
  dotnet test  <项目> -m:1 -nodeReuse:false      # 或不带 -m:1，交由运行器
  ```
  测试运行优先用 `dotnet run --project tests/<项目> --no-build`（先单独构建，再运行）。

### 1.2 不使用 vstest 测试宿主

- 现象：`dotnet test` 报 `System.ComponentModel.Win32Exception (5): 拒绝访问`，栈为
  `Process.EnableRaisingEvents → DefaultEngineInvoker.SetParentProcessExitCallback`；解除沙箱后仍失败（进程句柄权限）。
- 处理：测试工程使用 **xunit.v3 + Microsoft.Testing.Platform 进程内运行器**（`<OutputType>Exe</OutputType>` + `xunit.v3` 包 + `<TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport>`），运行方式为 `dotnet run --project tests/<项目>`。
- 注意：`dotnet test` 在 MTP 模式下也可能因 MSBuild 桥使用命名管道而失败；以 `dotnet run --no-build` 为准。
- 过滤图形测试：MTP 的语法是**单横线** `-trait "Category=Graphics"`（`--filter-trait` 不存在）。

### 1.3 包还原需要网络

- 现象：`dotnet restore` 报 `NU1301 ... The SSL connection could not be established`（`SEC_E_NO_CREDENTIALS`）。
- 原因：受限沙箱内 TLS 凭据不可用；TCP 可达但 TLS 握手失败。
- 处理：仅在首次还原固定版本包时需要联网（在放宽权限的一次性命令中执行）；之后依赖本地全局包缓存离线构建。
- 离线构建会出现环境性警告 `NU1900 获取包漏洞数据时出错`：不影响构建结果，属于环境限制。

### 1.4 并发构建会互相抢锁

- 现象：`CS2012 无法打开 ...obj/.../X.dll 以进行写入`、`MSB3021`；或构建“失败”但无错误明细。
- 原因：多个 Agent/命令同时构建同一程序集。
- 处理：串行构建；遇到锁错误时等待重试（本仓库开发期间用 20s 退避重试）。

---

## 2. C# / API 设计约束

### 2.1 命名空间不能与公开类型同名

- 现象：`CS0434：“CSharp.UI.Text”中的命名空间与“CSharp.UI”中的类型“Text”冲突`（`using X = CSharp.UI.Text;` 与 `CSharp.UI.Text.SkiaTextService` 同样报错，`global::` 也无效）。
- 原因：核心有公开视图类型 `CSharp.UI.Text`；任何 `using CSharp.UI;` 的编译单元都无法再引用同名命名空间。另一个变体：若同一编译单元内有文件声明 `namespace CSharp.UI.Text.Tests;`，则简单名 `Text` 会解析成命名空间（`CS0435` + `CS0118`）。
- 结论：**文本实现命名空间固定为 `CSharp.UI.Skia`**（程序集仍为 `CSharp.UI.Text`）。新代码请 `using CSharp.UI.Skia;`。
- 推论：不要新增与核心公开类型同名的命名空间（例如不要创建 `CSharp.UI.View`、`CSharp.UI.Button` 之类的命名空间）。

### 2.2 视图描述用普通类，不用 record

- 原因：`Component` 必须能被普通 `class` 继承（设计文档的目标写法是 `public sealed class Counter : Component`），而 C# 的记录只能继承记录，因此 `View` 是普通类，全部视图描述也用普通类 + `init` 属性。
- 处理：需要“复制并改一个属性”的链式方法（如 `VStack.WithSpacing`）显式写副本构造；`Text` 等提供 `With*` 方法。
- 布局等价比较：`View.LayoutPropertiesEquivalent` 比较**自身布局属性**（不要包含只影响绘制的颜色）；`View.IsLayoutEquivalent` 默认递归比较子项结构。纯颜色变化返回 true 即可跳过重新测量。

### 2.3 环境继承只在测量阶段解析

- 现象：改了主题/前景色/字体，但控件仍显示旧颜色。
- 原因：`node.Environment` 在 `LayoutContext.Measure` 中解析；若协调更新判定“布局等价”，就不会重新解析。
- 处理：通过环境传播的修饰符（`Theme`、`Font`、`Foreground`、`Disabled`、`Opacity`、`CornerRadius`）在 `LayoutPropertiesEquivalent` 中比较自己的值，值变化时触发重新布局。

### 2.4 纹理缓存键必须按值比较

- 现象：每帧都新建纹理（`FactoryInvocations` 持续增长），静态画面资源不断增长。
- 原因：用 `ReferenceEqualityComparer` 缓存“记录结构体”类型的键（例如文本纹理键）会每次都装箱成新对象，永远不命中。
- 处理：纹理缓存统一用 `EqualityComparer<object>.Default`：值类型键按键值（文本纹理键），引用类型键按引用（`ImageResource`）。

### 2.5 无内容变化时不重新布局

- 约定：`LayoutContext.Measure` 只按**上限**收缩期望尺寸（`Math.Clamp(size, 0, max)`），最小尺寸由视图自己声明（`Frame` 的 Min/Max、Stack 的填充语义）。
- 原因：如果父级紧约束覆盖子项期望尺寸，子项就无法报告自然尺寸（例如 `HStack` 里的固定尺寸矩形会被拉伸到父级高度）。
- 约定：`ScrollView` 在**滚动轴**上声明 `FlexGrow = 1`（fill 语义），否则放进不定尺寸的 `VStack` 时会撑到内容高度而永远无法滚动。

### 2.6 结构变化必须让父级测量缓存失效

- 现象：往容器/组件里新增子项后，新子项一直是 `0×0`（尤其是从“空集合”变为“有内容”，或根是 `ForEach` 的情况）。
- 原因：只有经过属性更新的节点才会调用 `InvalidateLayout()`；**新建子项**与**卸载子项**都不走那条路径，于是父级命中旧的测量缓存。
- 处理：`Reconciler.ReconcileDescriptors` 在“子项数量变化、有创建/卸载、顺序变化”时统一调用 `parent.InvalidateLayout()`。回归测试见 `R04`/`R08`。

### 2.7 隐式容器与多子项组件

- 约定：根视图展开为多个同级条目时，`MountRoot` 会包一层 `ImplicitRootContainer`；**组件 `Build()` 直接返回集合视图（`ForEach`）时同样包裹**。
- 原因：`Component.Measure` 只测量 `Children[0]`，否则其余子项永远不参与布局。
- **容器边界必须与条目数量无关**：集合视图在 0/1/多项时都包裹同一层隐式容器。若“单项直接挂、多项才包裹”，数量跨越 1 的边界会卸载整棵子树，带键项目的状态/焦点/编辑状态全部丢失（稳定键只在同一父级内匹配，抵消不了层级变化）。判断依据是 `UnwrapKey(view) is ForEachView`。
- 语义：隐式容器按**垂直堆叠**（间距 0、交叉轴拉伸）排列，等价于 `VStack`；`ImplicitRootContainer.IsLayoutEquivalent` 恒为 `false`（其内容由描述列表携带，无法参与结构比较）。
- 普通单视图组件（返回 `VStack`/`Text` 等）不引入额外层级，保持既有行为。

### 2.8 长度上限只能截断“新增内容”

- 现象：`MaxLength` 截断会写入半个代理项（😀 → `D83D`），或在替换选区时把未选中的尾部文本删掉。
- 处理：统一“先算可用容量（替换范围扣除后），再按 `StringInfo` 字素边界截断**插入内容**”；放不下的字素整体拒绝，绝不截断结果字符串的尾部。
- **拒绝 ≠ 删除**：插入内容因容量变成空时，必须保留原文、光标与选区（否则等价于“删掉选区”）。Backspace/Delete/剪切等显式删除仍走各自路径，语义不变。

---

## 3. OpenGL / GLFW 环境坑

1. GLFW 可能报 `Win32: Failed to create interface`（DirectInput/手柄初始化失败）。OpenTK 的默认错误回调会把任何 GLFW 错误抛成异常，导致连窗口都建不出来 → 宿主安装“记录不抛出”的错误回调，真正的窗口/上下文失败仍抛 `UiHostException`。
2. Intel 驱动**不接受 GLSL 源码中的非 ASCII 字符**（中文注释会导致词法错乱，报 `undeclared identifier` / `pre-mature EOF`）→ `GlShaderSources` 只用 ASCII 注释。
3. 纹理参数必须在纹理仍绑定时、且在 level 0 上传之后设置，否则默认的 mipmap min filter 会让纹理“不完整”，采样恒为 `(0,0,0,1)`（表现为纹理全黑）。
4. 图形测试在 xunit worker 线程运行，夹具需调用 `GlOffscreenSurface.AllowNonMainThreadCreation()` 放开 GLFW 的主线程检查；**macOS 仍要求 GLFW 在主线程**，在 macOS 上需要主线程测试宿主。
5. 像素约定（不要改）：CPU 侧位图预乘 alpha（`Rgba8888Premultiplied`/`Bgra8888Premultiplied`），文本遮罩为 `Alpha8`；GPU 侧统一 `glBlendFunc(GL_ONE, GL_ONE_MINUS_SRC_ALPHA)`；`PushClipCommand`/`PopClipCommand` 维护 scissor 栈，逻辑坐标 → framebuffer 需要乘 DPI 并翻转 Y。
6. 纹理缓存的帧语义：一帧的全部命令先生成、之后才提交。淘汰若在命令生成期间发生，会把前面命令引用的纹理删掉（表现为“同帧内文本数量超过容量时前面的文本不绘制”）。`ITextureService.BeginFrame()` 是帧边界，实现要对**本帧与上一帧引用**的纹理做保护（两代保护可同时避免“每帧重复上传”的抖动），容量在帧边界回收；显式 `Capacity` 变更立即生效。
7. 字距（`LetterSpacing`）必须同时进入**测量**与**栅格化**：`TextLayoutResult` 暴露 `LetterSpacing`，`SkiaTextService.DrawLine` 按字素簇序号附加字距并使用行内绝对位置。只累计 `glyph.Advance` 会让字距只改变测量宽度。
8. GLFW 剪贴板是进程间共享资源：`OpenClipboard` 被其他进程短暂占用时会读回空串。测试应重试读回并把环境信息写进失败消息，**不要跳过**。

---

## 4. 验证与记录约定

- 每完成一个任务：更新 `docs/task/<编号>-*.md`（状态、复选框、实施与验收记录），并在 `docs/task/completion-records.md` 与 `docs/task/README.md` 同步。
- 只记录**实际执行过**的命令与结果；未运行/未验证的项明确写“未运行/待验证”，不能把跳过当作通过。
- 图形测试失败不能被当作跳过成功：无 GL 上下文时应如实失败并保留诊断。
- 示例的窗口冒烟测试：`CSHARPUI_SMOKE_FRAMES=<帧数> dotnet run --project samples/<示例> --no-build`，渲染指定帧数后打印窗口/DPI/GL 上下文并自动退出（用于无人值守验证）。
- 验证入口：`build/verify.ps1`（Windows）与 `build/verify.sh`（Linux/macOS），报告写入 `artifacts/verification/`。

## 5. DeepSeek Harness ACP 连接（2026-10-15）

已验证本机桌面版的 ACP v1 握手、会话列表、固定文本任务收发及会话关闭。连接检查器、启动路径、沙箱权限问题和实际结果见 [ACP 本地连接说明](acp/README.md)。该工具不派发框架开发任务，也不自动允许工具执行。
