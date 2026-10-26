# RT-03 独立验证证据（基线快照诊断 + 冻结二进制两轮正式运行）

日期：2026-10-26
验证代理：rich-markdown-verification（独立验证工作流，非实现方）
状态：**准备完成，等待源码 freeze 通知后正式运行**。本目录当前只含入口调查与环境探针原始输出；
无任何 RT-03 验收断言（被测快照内 `CSharp.UI.RichText` 尚不存在，属 RT-02 基线二进制，probe 已如实记录）。

## 已验证的运行入口（全部实测，非源码推断）

- `dotnet --version` → 10.0.303；.NET 10 / net10.0。
- 现有测试 exe 直跑可行（xunit v3 in-proc runner）：
  `tests/CSharp.UI.OpenTK.Tests/bin/Debug/net10.0/CSharp.UI.OpenTK.Tests.exe -class <ClassName> -noLogo`。
  已实测 `GlContextTests`：Total 3 / Failed 0，exit 0（`glcontext-probe-stdout.txt`）——
  本机可创建真实 OpenGL 3.3 Core 上下文（Intel RaptorLake-S，GLFW 隐藏窗口）。
- **真实 GL 像素验证入口（不依赖产品源码改动，外置 harness 可用）**：
  `CSharp.UI.OpenTK` 公开面——`GlOffscreenSurface(width,height)`（隐藏 GLFW 窗口+离屏 FBO，
  `AllowNonMainThreadCreation()`）、`GlTextureService(capacity)`、`GlRenderer(textures)`、
  `surface.Render(renderer, RenderFrame)` → `GlFrameStatistics`、`surface.ReadPixels()` →
  `PixelBuffer`（`GetColor(x,y)` 非预乘）。`UiRuntime` 公开：`new UiRuntime(View, UiRuntimeServices)`、
  `SetEnvironment/SetViewport/Start/ProcessPendingWork/BuildFrame/DispatchPointer/DispatchWheel`、
  `Root.FindDescendants<T>()`、`node.ViewCache/Frame`。测试替身全公开：
  `HeadlessUiDispatcher`、`StubTextService`（实现 `IRichTextService`）、`StubImageService`、
  `RecordingTextureService`（`CreatedCount`/`FactoryInvocations`/`LiveTextureCount`——纹理键行为可外部观测）。
- **真实窗口 smoke**：`CSHARPUI_SMOKE_FRAMES=N` + `dotnet run --project samples/<Sample>` —
  `samples/Shared/SmokeTestHook.cs` 渲染 N 帧打印视口/DPI/GL 描述后 `RequestExit(0)`。
- 服务计数器（`IRichTextService` 实现已核实）：`CachedLayoutCount`、`CachedRasterCount`、
  `LayoutCacheCapacity`、`ClearCaches()`；`Layout` 每次返回独立 `IDisposable` handle。

## 外置 harness（repo 外，不污染产品/测试）

路径：`%TEMP%\rt03-verify\`（`lib/` = 产品+依赖二进制快照，`app/` = 控制台项目，纯 `<Reference>` 指向 lib）。

- 快照来源：`src/*/bin/Debug/net10.0/*.dll`（CSharp.UI sha256 `262169c1…`、CSharp.UI.Text
  `01c64555…`、CSharp.UI.OpenTK `8151dcbe…`——即 RT-02 freeze 基线）+ `tests/CSharp.UI.OpenTK.Tests/bin`
  里的 SkiaSharp/HarfBuzz/OpenTK 托管与 `runtimes/win-x64/native/*.dll`（需平铺到输出目录——
  裸 `<Reference>` 无 deps.json 不解 RID 布局，已踩过并修正）。
- `env-probe-stdout.txt`：5/5 PASS exit 0——headless runtime 出帧、pointer dispatch、
  **真实 GL 渲染出像素墨迹**（256×256 surface，ink=217px），并如实记录 `RichText ABSENT`。
- freeze 后流程：good 实现落盘 → 用实现方构建产物刷新 `lib/` 快照（记 SHA）→ 扩展 Program.cs
  加 RT-03 场景 → `dotnet run` 采集原始 stdout/stderr/exit + PNG 到本目录（新文件名，不覆盖历史）。

## RT-03 计划验证项（freeze 后执行，行为级）

1. 多样式段落逐 segment 渲染：GL ReadPixels 分段取色（粗/斜/变色/背景），与
   `Rasterize(layout,paint)` 直绘参考对照；装饰顺序 bg→text→underline→strike 由像素矩形断言。
2. 三键行为：`RichTextService.CachedLayoutCount/CachedRasterCount` 外部观测——换色 layout 计数
   不变 + raster +1；改 payload 两不变；改端点/字体/宽 → 重排。
3. Emit 无重建：N 帧 `BuildFrame` 后 `RecordingTextureService.FactoryInvocations` 不再增长
   （纹理缓存命中）；`ViewCache` 持 handle 生命周期（OnUpdate 换 Content / unmount Dispose）。
4. plain fast path：`StyledText.Create` → `ITextService` 路径；`RichTextService=null` 有样式 →
   `InvalidOperationException`。
5. hover/链接 hit 留 RT-04 gate（本任务只验证 hover=null 时 paint 稳定）。
6. 窗口级：`CSHARPUI_SMOKE_FRAMES` 跑真实窗口（若 RT-03 有 demo/sample 入口；否则 headless
   runtime + GL surface 已属真实 GL 观察，窗口 smoke 归 MD-06）。

## MD-06 全套件范围（届时范围预告）

Debug+Release fresh slnx build、4→5 测试工程全量（Markdown.Tests 待 MD-01 后出现）、
3 samples×2 config build+真窗 smoke、GL 像素证据（light/dark×pixelScale、链接命中矩形、
表格 320/1024）、`dotnet list package --include-transitive` 依赖核验、nuget 包内 NOTICE 展开核验、
filesystem-walk SOURCE-SHA256（含 untracked——RT-02 曾漏 5 个新文件的教训）、manifest 每步 exit 记录。

## 已知边界

- 不与 good/其他实现代理通信；不改 repo 内产品/测试文件；不提交不推送。
- 本机仅 Windows x64；macOS/Linux 不承诺。
- `dotnet test` MSBuild 节点在 RT-02 曾有 runner 基础设施失败史——统一用测试 exe 直跑。

## 正式脚本状态（2026-10-26 第二轮，PRE-FREEZE）

`%TEMP%\rt03-verify\app\Program.cs` 已重写为正式 RT-03 场景脚本（约 550 行）：

- **只用公开 API**：`ViewNode.View`/`ViewCache`/`RichTextViewCache` 为 internal——内容更新、
  unmount、plain↔rich 切换全部经 `Component`+`State<T>.Value` 真实 reconciliation 驱动；
  segment 几何经 `richService.Layout(同一StyledText)` 公开调用获取（共享服务缓存 core，
  非内部读取）；纹理缓存行为经 `RecordingTextureService.FactoryInvocations` 公开计数；
  布局/栅格计数经 `CachedLayoutCount`/`CachedRasterCount` 公开属性。
- **真实 GL**：`GlOffscreenSurface`+`GlTextureService`+`GlRenderer` 直接构造（`GlTestScope`
  是测试程序集类型，不可引用）；PNG 按 RT-02 同款 `SKBitmap`/`SKImage.Encode` 路径写盘。
- 覆盖场景：plain fast path（guard service 计数+DesiredSize 等价+mask texture 命令）、
  多样式像素+装饰顺序（bg 矩形在 texture 前、underline/strike 在后）+trim 尾空白、
  三键（色/envfg/payload/端点/宽）、10 帧 factory 平坦、两 view 共享 core+state 卸载存活、
  Flow/Flex probe resize、light/dark×ps1/1.5/2 PNG、半透明 fg 混合色、祖先 Clip 不外溢、
  斜体 fj/jf RasterBounds+墨迹。
- **编译状态**：除 `RichText`/`Flex` 外的全部符号已验证可解析；CS0246 仅剩 RichText
  类型引用（基线快照无此类型，freeze 后刷新 lib 快照即编译通过）。未对未复现问题
  下结论；82 式计数仅统计真实行为断言，环境探针 PASS 不计入。
- 等待源码 freeze 通知：届时刷新 `lib/` 快照（新 SHA 记录）→ 编译 → 运行 →
  原始 stdout/stderr/exit/PNG 落本目录（新文件名，不覆盖 env-probe 历史）。

### 第二轮修订（按主审反馈消除假绿）

- 装饰顺序：不再用 `||count!=2` 恒真式。独立 `"UUUU SSSS"` 场景——同行 underline-only
  段（红）与 strikethrough-only 段（蓝）颜色可区分，断言全部红色 U 矩形命令下标 < 全部
  蓝色 S 矩形最小下标。
- trim 尾空白：不再用 `line.Bounds.Right-seg.Right` 间隙探针（常态为 0）。专用
  `"KEEP   "`（样式覆盖含尾空白）断言每条 bg/deco 矩形 `Bounds.Right ≤ GetCaretX(trimEnd)`
  （公开 caret API）。
- underline 墨迹：不再 pin 行底-6。按 segment 的 `ResolvedFonts[FontId].Size` 在行
  `Baseline` 下方 5%~35% 字号区间内搜墨（装饰位置由实现按 baseline/字号派生）。
- 三键：改用 `SkiaTextService.RichLayoutMisses/RichLayoutHits` 与
  `RichRasterMisses/RichRasterHits`（公开单调计数器；`CachedLayoutCount` 是 core 存量
  非调用次数，不能表达 layout 是否发生）。字号变化附加 DesiredSize 实际变高断言。
- 共享 core：`layouts==1` 不 pin（父 probe 产生额外 handle 属合法）——断言两 view
  DesiredSize 一致 + 卸载后存活方渲染 + 缓存仍活，miss/cached 值仅 Note 记录。
- 组件类改用静态 `MixedBuilder` 委托（顶级 local function 不能跨 class 引用）；预编译
  仅剩 CS0246 RichText（基线快照无此类型，符合预期）。

## 冻结二进制正式运行（诊断轮 + 修正轮）

被测快照：`final-lib-sha.txt`——CSharp.UI `8e46746e` / CSharp.UI.Text `01c64555` /
CSharp.UI.OpenTK `8151dcbe`（实现方 RT-03 freeze 后 Debug 产物，Assembly.Location 打印于
各 stdout 顶部；注意早前误把 `CSharp.UI.Text/bin` 里的旧 `CSharp.UI.dll 262169c1` 拷入 lib——
属快照源错误，已用各项目自身 bin 重拷更正；这正是 good 侧"frame commands=0"疑似诱因之一
——TestApp 未注入 TextureService 已由主审另行定位，本轮不复现）。

- `rt03-frozen-final-stdout.txt`：**47/47 PASS，exit 0**。
  - plain fast path：guard IRichTextService 0 调用；DesiredSize 与 Text 完全相同；
    mask texture 命令含真实 tint。
  - 多样式逐 segment GL 像素：bold-red/blue 各段 dominant 色命中（seg 按文本区间
    containment 取样，非索引）。
  - 装饰顺序专项：`"AAAA BBBB"` 两段均 U+S 仅色不同，命令序列实测 `[UR, UB, SR, SB]`——
    全 U 在全 S 前（朴素逐段 U,S,U,S 会被此断言拒绝）。
  - trim 尾空白：`"KEEP   "` 带样式尾空白，全部 bg/deco 矩形 right=30.13 ==
    GetCaretX(4)，无越界。
  - underline 墨迹：baseline+5%~35% 字号区间实测命中（117.5,15.8 带内）。
  - 三键（RichLayout/RasterMisses 公开单调计数）：style 色/环境前景 → layout 无 miss、
    raster +1；payload 变 → 两 miss 均不增；端点/字号（DesiredSize 实测变高）/宽度 →
    layout miss 增长。
  - Emit 10 帧 FactoryInvocations 平坦（1→1）。
  - 共享 core：两 view 同 StyledText → layoutMisses 总量 8（含父级 probe 合法重复），
    cachedLayouts=1，DesiredSize 一致；state 卸载其一后存活方照常渲染、缓存仍活。
  - Flow/Flex probe resize 后 textureCommands=3 正常。
  - GL 矩阵 light/dark × ps 1/1.5/2 全部出墨迹 PNG（diff 2129~8117px 随 scale 增长）。
  - 半透明 fg(α=90)：dominant #E69FA0 与理论混合值一致——**run alpha 真实入像素**，
    未被 Tint=White 强不透明（修复了我首版的错误期望边界：bg 为浅色时 R 通道往 bg
    混合不下降）。
  - 祖先 Clip：PushClip=(40,0,120×18.6) 实际矩形外无墨迹（注：harness 用
    `.Clip().Width(120)`——Width 为外层改 ClipView 自身尺寸；反序是裁剪整个视口盒，
    首版 fixture 写反过一次，PNG/DIAG 行保留历史）。
  - 斜体 jf/fj：RasterBounds 覆盖负 bearing（(-2,0,10.39) vs advance 7.77），GL 墨迹
    224px 实测无裁。
- `rt03-frozen-final-stderr.txt`：空（exit 0），真实捕获。
- 历史诊断轮：`run-rt03-diag1.txt`/`run-rt03-diag2.txt` 为合并流（`2>&1 | tee`），
  其中含崩溃 `Sequence contains no matching element` @Program.cs:296 的原始文本。
  早前声明的独立文件 `rt03-diag-crash-stderr.txt` 现 0B——属路径复用被后续空 stderr
  运行覆盖，崩溃 stderr 独立 raw 未保留；只以合并流文本为准，不重建、不重跑旧失败。
  （教训：每轮 raw 唯一文件名，最终轮已落实 rt03-frozen-final-*。）


## 第二轮正式运行（rt03-r2-*，修正+补项后）

- **51/51 PASS，真实 exit 0**（`rt03-r2-exit.txt` 记录 `$?` 捕获的真实返回码，
  `rt03-r2-stderr.txt` 0B，`rt03-r2-stdout.txt` 全文；SHA256SUMS.txt 覆盖本轮 raw+PNG+lib sha）。
- 新增断言通过：
  - `nullsvc-plain-works`：RichTextService=null + plain 内容 → 正常出 texture 命令；
  - `nullsvc-styled-throws`：含样式 → `InvalidOperationException`，
    `nullsvc-message-names-injection`：消息点名 `UiRuntimeServices.RichTextService` 注入点；
  - `shared-core-same-texture-handle`：两 view 同 core 同 paint → 两条 TextureCommand
    的 `Texture` handle 相等（同 core 共享纹理缓存的可见证据）。
- 前一轮 `rt03-frozen-final-*`（47/47）保留不覆盖；exit 0 本轮起有独立 exit 文件。
## 诚实边界（本轮不声明项）

- hover/link 交互未验（RT-04 范围；当前 DLL `OnLinkActivate` 尚未存在，Rt04.cs 已
  `#if RT04`+csproj `Compile Remove` 条件排除，不阻塞 RT-03 运行）。
- `RichTextService=null` 分支（plain 可用 + 含样式 InvalidOperationException 点名注入点）
  与多 view 同 core 同 texture handle 相等断言已在 r2 轮补齐并 PASS；
  此前"未覆盖"记录随断言落地更新，非隐瞒历史。

