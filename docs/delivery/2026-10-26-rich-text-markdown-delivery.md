# 富文本 + Markdown 模块交付记录

日期：2026-10-26
状态：**已完成并验收**——RT-01~RT-04 + MD-01~MD-06 全部交付；fast MD-06 final-* 跑批全绿。

## 任务范围与验收

| 阶段 | 任务 | 范围 | 验收 |
|------|------|------|------|
| RT-01 | `StyledText` 模型 | 展示文本+样式/交互两表；字素边界校验；值相等+预算哈希+分段端点拓扑 | 已验收（commit `a03d9bd`） |
| RT-02 | `IRichTextService` 契约 + Skia | `RichLayoutCore` 共享缓存；handle AddRef/Release；几何/paint/interaction 键分离 | 已验收（commit `817f5cd`） |
| RT-03 | `RichText` 视图 | paint 解算缓存；装饰顺序；纹理键含 paint；plain fast path 走 `ITextService` | 已验收 |
| RT-04 | 链接 hover/激活 | pointer enter/leave + press/release + capture；`OnLinkHover`/`OnLinkActivate` | 已验收 |
| MD-01 | Markdig 管道 + `MarkdownDocument` | `Markdown.Parse` LRU+`CacheLock`;`MarkdownPipelineOptions` 5 字段 | 已验收 |
| MD-02 | AST→视图 adapter | `MarkdownBlockView`;block/inline 映射；unsupported literal 降级 | 已验收 |
| MD-03 | `MarkdownTable` | `Responsive`+`ScrollView(Horizontal)`;Σmin>available 横滚；`LayoutPropertiesEquivalent` 换档失效 | 已验收 |
| MD-04 | 图片/链接策略 | `MarkdownImagePolicy`/`MarkdownLinkPolicy`/`MarkdownLinkPayload`;remote 默认 alt | 已验收 |
| MD-05 | Gallery `MarkdownDemo` + 文档 + NOTICE | 公共 component 挂入 Gallery;markdown guide/api/platform-notes 更新；Markdig BSD-2 NOTICE 入包 | 已验收（offscreen 50 checks 通过） |
| MD-06 | 交付验收 | 双 config 套件/3 sample 真窗/NOTICE nupkg/manifest+hash | 已验收（1592 tests/config,0 fail) |

## 最终矩阵证据（`final-` 前缀）

| 验证 | 结果 | 证据 |
|------|------|------|
| Debug / Release `dotnet build CSharp.UI.slnx --no-incremental` | 0 warn / 0 err ×2 | [build-debug.stdout](2026-10-26-rich-text-markdown-delivery-evidence/final-build-debug.stdout.txt) / [build-release.stdout](2026-10-26-rich-text-markdown-delivery-evidence/final-build-release.stdout.txt) |
| `CSharp.UI.Tests` Debug+Release | **698+698 PASS** | [Debug](2026-10-26-rich-text-markdown-delivery-evidence/final-suite-csharp-ui-tests-Debug.stdout.txt) / [Release](2026-10-26-rich-text-markdown-delivery-evidence/final-suite-csharp-ui-tests-Release.stdout.txt) |
| `CSharp.UI.Text.Tests` Debug+Release | **109+109 PASS** | [Debug](2026-10-26-rich-text-markdown-delivery-evidence/final-suite-text-tests-Debug.stdout.txt) / [Release](2026-10-26-rich-text-markdown-delivery-evidence/final-suite-text-tests-Release.stdout.txt) |
| `CSharp.UI.Integration.Tests` Debug+Release | **53+53 PASS** | [Debug](2026-10-26-rich-text-markdown-delivery-evidence/final-suite-integration-tests-Debug.stdout.txt) / [Release](2026-10-26-rich-text-markdown-delivery-evidence/final-suite-integration-tests-Release.stdout.txt) |
| `CSharp.UI.OpenTK.Tests` Debug+Release | **684+684 PASS**(前 66 fail 已修) | [Debug](2026-10-26-rich-text-markdown-delivery-evidence/final-suite-opentk-tests-Debug.stdout.txt) / [Release](2026-10-26-rich-text-markdown-delivery-evidence/final-suite-opentk-tests-Release.stdout.txt) |
| `CSharp.UI.Markdown.Tests` Debug+Release | **48+48 PASS**(含 EmphasisExtras flag 回归) | [Debug](2026-10-26-rich-text-markdown-delivery-evidence/final-suite-markdown-tests-Debug.stdout.txt) / [Release](2026-10-26-rich-text-markdown-delivery-evidence/final-suite-markdown-tests-Release.stdout.txt) |
| **总测试数** | **1592 / config** | — |
| 3 sample fresh build × Debug/Release | 0 warn/0 err ×6 | `final-sample-build-{ControlGallery,Counter,TodoList}-{Debug,Release}.{stdout,stderr,exit}.txt` |
| 3 sample 真窗 GL smoke × Debug/Release | 5 帧自停 exit 0 ×6 | `final-sample-smoke-{ControlGallery,Counter,TodoList}-{Debug,Release}.{stdout,stderr,exit}.txt` |
| `dotnet list src/CSharp.UI package` | 无包引用 | [final-list-package-core.stdout](2026-10-26-rich-text-markdown-delivery-evidence/final-list-package-core.stdout.txt) |
| `dotnet list src/CSharp.UI.Markdown package` | Markdig 1.5.0 only | [final-list-package-md.stdout](2026-10-26-rich-text-markdown-delivery-evidence/final-list-package-md.stdout.txt) |
| `dotnet pack` Core Release | nupkg 只 nuspec + dll(无 NOTICE,正确) | [final-pack-CSharp.UI.stdout](2026-10-26-rich-text-markdown-delivery-evidence/final-pack-CSharp.UI.stdout.txt) / [nuspec](2026-10-26-rich-text-markdown-delivery-evidence/final-pack-CSharp.UI-nuspec.xml) |
| `dotnet pack` Markdown Release | nupkg 含 `NOTICE` 1701B,sha256 `58905395…` 与 repo NOTICE byte-identical | [final-pack-CSharp.UI.Markdown.stdout](2026-10-26-rich-text-markdown-delivery-evidence/final-pack-CSharp.UI.Markdown.stdout.txt) / [nuspec](2026-10-26-rich-text-markdown-delivery-evidence/final-pack-CSharp.UI.Markdown-nuspec.xml) / [NOTICE.bin](2026-10-26-rich-text-markdown-delivery-evidence/final-pack-CSharp.UI.Markdown-NOTICE.bin) |
| Offscreen render 12-matrix | 50 PASS / 0 FAIL(2 themes × 3 scales × 2 widths) | [final-md06-offscreen-stdout](2026-10-26-rich-text-markdown-delivery-evidence/final-md06-offscreen-stdout.txt) + `md06-{light,dark}-{320,1024}x{1,1.5,2}-{link-echo,doc-bottom}.png` 24 张 |
| API quick-example on final DLLs | 6 RichText + 1 MarkdownTable + 1 link node + 9 draw cmds | [final-md06-api-example-stdout](2026-10-26-rich-text-markdown-delivery-evidence/final-md06-api-example-stdout.txt) |

## 包元数据诚实性声明

- `nupkg` 是在 **commit `817f5cd` 基线**的工作树上构建产物——nuspec `<repository commit>` 字段
  反映构建时 HEAD，不冒称包含本组新 feature 的新 commit hash（新 commit 尚未创建）。
- 本地 `dotnet pack` 验证通过,不发布 NuGet.org;包仍为预发布 `0.1.0-preview.1`。
- Markdig BSD-2 NOTICE（1701 B）sha256 `58905395…` 与 `NOTICE` 根文件一致——fast 解包验证。
- `final-md06-offscreen-*` 用 `UiRuntime.SetViewport` + `BuildFrame` 输出 framebuffer，非物理 OS 鼠标。
- 真实 monitor 仅 DPI 1.5;offscreen 覆盖 pixelScale 1/1.5/2 的真实物理像素渲染。

## 关键源码清单（SHA256 manifest)

`SOURCE-SHA256.txt` 是冻结工作树 source bytes 快照（共 270 文件:src 129 / tests 122 / samples 10 / tools 2 /
根 7)——文档冻结后、提交前由 fast 重核对一次,条数以 `SOURCE-COUNT.txt` 与 verification summary 为准。

- `src/CSharp.UI/Text/StyledText.cs`、`src/CSharp.UI/Text/RichTextContracts.cs`、`src/CSharp.UI.Text/RichTextLayoutBuilder.cs` — RT-01/02。
- `src/CSharp.UI/Views/RichText.cs` — RT-03 视图。
- `src/CSharp.UI.Text/SkiaTextService.Rich.cs` — RT-02 Skia 富文本实现。
- `src/CSharp.UI.Markdown/*.cs` — MD-01~MD-04 模块。
- `samples/CSharp.UI.Samples.ControlGallery/MarkdownDemo.cs` — MD-05 sample。
- `NOTICE` 根 — Markdig BSD-2-Clause 归属。

## 边界（明确不实现)

| 范围 C | 行为 |
|--------|------|
| 代码高亮/LaTeX/Mermaid/远程图加载/富文本编辑/WYSIWYG/文本选择/复制/通用 Grid/滚动 API | 不实现 |
| `HtmlBlock`/`HtmlInline` | `Literal`→原文 literal 段落；`Strip`→丢弃（`MarkdownPipelineOptions.Html` 控制） |
| `^`/`~`单字符/`++`/`==` extra delimiter | 不启用 parser(`UseEmphasisExtras(Strikethrough)` only)——literal 文本可见，不误渲染 italic/bold |
| 行内图片 | `[alt]` literal 区间；远程默认 alt 降级 |
| TaskList checkbox | `[ ]`/`[x]` literal marker，非交互 |
| 链接键盘/focus 激活 | 范围 C，无 UI |

## 平台验证范围

- **Windows x64 实测**:.NET 10.0.11,OpenGL 3.3 Core,Intel RaptorLake-S，真窗 DPI 1.5,offscreen DPI 1/1.5/2。
- **macOS/Linux**：源码兼容设计（统一 GL 3.3/Skia 路径），未在对应 OS 实机编译/运行——**不宣称跨平台实测**。
- 物理鼠标键盘未直接驱动；`UiRuntime` 经 `DispatchPointer`/`DispatchWheel` 注入，验证 `RichText` interaction hit-testing 正确。

## 历史失败透明记录

- **MD-06 首轮**：`CSharp.UI.OpenTK.Tests` 66/684 fail——测试宿主缺 `RichTextService` 注入，已修；raw
  `suite-opentk-tests-{Debug,Release}.stdout.txt` 952/957KB 保留原始 fail trace,`exit=1` 文件同目录。
- **MD-03 三轮**:token 宽度/port 行高/`LayoutPropertiesEquivalent` 换档失效——`docs/return/2026-10-26-md03-markdown-table.md` 详录。
- **MD-05 三处修正**：虚构 URL→真实远端、CJK 长 cell→不可断拉丁 token、构造缓存；hover state 删除。
- **RT-04 多轮**:clip 外 pointer-down 不激活、同 URL 不同 Id 不互激、Up 后 capture 置 null、`OnUpdate`/unmount
  清 hover——`2026-10-26-rt04-rich-text-link-hit.md`。
- 旧 light-380 链接图（滚动后 screenshot）主审注明不能当 echo 视觉 proof;offscreen 序列已补正确证据。

## 交付文件

- 实现:`src/CSharp.UI/Text/`、`src/CSharp.UI/Views/RichText.cs`、`src/CSharp.UI.Markdown/`、
  `samples/CSharp.UI.Samples.ControlGallery/MarkdownDemo.cs`、`samples/…/Program.cs` Markdown Section 挂载、
  `NOTICE`、双 `README*.md`、三 `docs/guide/*.md`。
- 测试:`tests/CSharp.UI.Markdown.Tests/`(48 项测试/配置)、`tests/CSharp.UI.Tests/RichTextViewTests.cs` 等、
  `tests/CSharp.UI.OpenTK.Tests/` 29 文件 host 装配修复。
- 证据:`docs/return/` RT-01~MD-06 各 `.md` + `-evidence/` 子目录,本文档
  `2026-10-26-rich-text-markdown-delivery-evidence/`。
- 审计:无 `TODO`/`NotImplementedException`/`AdaptTableFallback`/`phantomStub`/probe.cs/fsx/csx 残留。

## 版本控制

本记录描述验收工作树;英文实现提交与推送在主审门通过后执行,提交号以 Git 历史为准(本文件不含未来
commit sha)。
