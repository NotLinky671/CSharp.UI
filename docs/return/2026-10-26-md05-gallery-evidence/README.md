# MD-05 sample surface — real-GL verification evidence

日期:2026-10-26 · 范围:`MarkdownDemo` 真实 surface 消费者行为(链接命中回显 + 表格横滚 + 主题)
环境:Windows x64 · OpenGL 3.3 · Intel RaptorLake-S · 真机 DPI 1.5 · runtime dispatch 输入(非 OS 物理鼠标)

## Harness

- 外 repo runner:`C:\Users\nothh\AppData\Local\Temp\md05-surface\`(`md05.csproj` + `Program.cs`)。
- 引用真实 `ControlGallery.dll`(`AssemblyName=ControlGallery`)+ 真实 `CSharp.UI`/`CSharp.UI.Text`/`CSharp.UI.Markdown`/`CSharp.UI.OpenTK` dll(同 `samples/CSharp.UI.Samples.ControlGallery/bin/Debug/net10.0/`)。
- 真实 `UiApplication.Run(new MarkdownDemo(logoPath))` — 实际 GL 窗口、实际 Skia/HarfBuzz、实际 GLFW framebuffer。
- 输入:`ctx.Runtime.DispatchPointer`/`DispatchWheel`(runtime 输入管线,不经 OS 硬件路径)。
- 像素:`GL.ReadPixels` 抓 framebuffer 转 PNG(SkiaSharp `SKImage.Encode` 不依赖产品代码)。
- 链接定位:`rt.HitTest(point)` 网格扫面(`runtime.HitTest` public API)— 直接命中含
  `MarkdownLinkPayload.Url.Contains("github.com")` 的 `RichText` 节点即视为目标像素;不经私有反射。

## 覆盖矩阵(4 运行 × exit=0)

| 配置 | viewport | theme | 结果 | 实测 echo |
|---|---|---|---|---|
| `md05-light-900` | 900×700 | Light | 7 PASS / 0 FAIL,exit=0 | `点击 URL:https://github.com/NotLinky671/CSharp.UI` |
| `md05-dark-900` | 900×700 | Dark | 7 PASS / 0 FAIL,exit=0 | 同上 |
| `md05-light-380` | 380×700 | Light | 7 PASS / 0 FAIL,exit=0 | 同上;380 窄宽下表格 frame `(-263,255,631×170)`,X<0 表示外层 ScrollView 已横滚 |
| `md05-dark-1200` | 1200×700 | Dark | 7 PASS / 0 FAIL,exit=0 | 同上;1200 宽下表格 frame `(0,357,1188×76.6)` 自然展开无横滚 |

每次 run 输出:

- `md05-{cfg}-stdout.txt` / `-stderr.txt` / `-exit.txt`(全部 exit=0)。
- `md05-link-echo-{cfg}.png`:点击后链接回显像素证据(回显文本 = 真 URL,不再是 `尚未点击链接`)。
- `md05-doc-bottom-{cfg}.png`:内层 doc ScrollView 滚到底时表格 render 像素证据。
  - `*-380x700*` 表格 PNG 显示横向已滚动(表格 X=-263,左缘在视口外,末列完整在右缘内)。
  - `*-1200x700*` 表格 PNG 为 natural 1024+ 宽度,no横滚。

## 实测行为

- **`DispatchWheel` 垂直滚动** doc ScrollView:`DeltaY:-1` 每次向下若干行。链接 paragraph 滚动到视口内。
- **`HitTest` public API**:扫 viewport grid 命中含 `MarkdownLinkPayload` 的 `RichText` 节点一次即得候选点。
- **`DispatchPointer Move/Down/Up`** 同点 → `MarkdownBlockView.OnLinkActivate` 触发 → `MarkdownDemo` 把 `link.Url` 写入 `activatedUrl` state → 页内 `Text("点击 URL:...")` 回显真实 `https://github.com/NotLinky671/CSharp.UI`。
- 表格节点(internal `MarkdownTable`)在 doc ScrollView 底部就位;380 视口表格 natural=631 > viewport=380 处于横滚态(外层 ScrollView);1200 视口自然展开。
- 主题 Light/Dark 渲染差异:PNG 字节 SHA-256 不同(见 sha256 段,每帧 framebuffer 不同主题颜色)。

## DPI 边界

`dpi` 实测显示 `1.5`(系统缩放 150%,framebuffer 1350×1050 for logical 900×700;380×700 570×1050;1200×700 1800×1050)。harness `pixelScale` argv 标签记录运行声明,**不代表真实 DPI**——UiApplication 始终取系统实际内容缩放;本机仅 1.5 一档,不能声称覆盖 dpi 1/2 行为。`md05-*-x1.png` 文件名中的 `x1` 是 argv 标签,实际 DPI 见 PNG 尺寸(1350/570/1800 横向 = logical×1.5)。

## SHA-256(PNG payload)

```
0e56b68893d37e78e44373bc35bd56f29bdf78fa78f25e37246b096064d73233  md05-doc-bottom-dark-1200x700x1.png
df08f669d1851e0d88c10919b178c0b003f382b47518037b20a46bb6a23d1f3a  md05-doc-bottom-dark-900x700x1.png
e629b67f349f4c669de75c26fbffd0ab62c2567ad759deb648abed7b9c3773ec  md05-doc-bottom-light-380x700x1.png
d402767b802bc737f43dc8dcf64d1a00888fee7cc727c8193a10f3060b610a67  md05-doc-bottom-light-900x700x1.png
ef6f40cfbc942fc1fd868992fd26cd523f492553ca62b28607016dcfe8a1aa9a  md05-link-echo-dark-1200x700x1.png
e1c00bd9fcee4ee22ea7e161afe8fd84459a1e275efcf432a8272d06510e0924  md05-link-echo-dark-900x700x1.png
9b27e05965b62c720baff86122f0e8a2c491d0d3f2de1583e67e3fdc88de1aa4  md05-link-echo-light-380x700x1.png
5d755b1588239e56c9f8ff56329e853df62a154faab2e014510e4212885a13ed  md05-link-echo-light-900x700x1.png
```

## 备注

- **输入层级**:`DispatchPointer`/`DispatchWheel` 是 runtime 输入管线注入,不经 OS 硬件鼠标/滚轮消息;与 `MarkdownDemo` 的 `OnLinkActivate` 回调真实链路一致,但不验证 OS 原生鼠标通道(后者由 RT-04 既有验收覆盖)。
- **路径**:harness 不写 fixture/sample —— 挂的是 `CSharp.UI.Samples.ControlGallery.MarkdownDemo` public 类型与真实 `samples/Shared/Assets/logo.png`;MarkdownDocument 由 sample 自身 `BuildMarkdownDocument()` 生成,无 adapter 复刻。
- 历史 PNG 曾被 `MD05_EV` 环境变量错投到无 `#` 的 `C:\Users\nothh\code\CSharp.UI\` 路径(目录已建立但 PNG 写入失败后丢失);纠正后重写此目录于真实 repo。其他 run1/2/3 raw 已并入 `docs/return/2026-10-26-md0{3,4}-*-evidence/`。
