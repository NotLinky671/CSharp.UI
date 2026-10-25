# 富文本与 Markdown 任务索引

创建日期：2026-10-25

设计依据：[只读富文本基础与 Markdown 接入实施计划](../plan/2026-10-25-rich-text-and-markdown-plan.md)。

状态：**实施中**（RT-01、RT-02 已完成并验收；其余任务未开始）。

## 核心决策（已冻结，见计划全文）

- **文本模型**：`StyledText` = 单根**展示** UTF-16 字符串 + 正交两表——`TextStyleRange`（字体/颜色/装饰，无 payload）+ `TextInteractionRange`（含稳定 `Id`+`Payload`）；构造时验证字素边界/拒重叠空区间（两表各自内部不重叠、表间可重叠）；值相等+预计算哈希+**分段端点拓扑**（Ranges∪Interactions 端点）。
- **四元分离**：shape/几何分段只由①分段端点拓扑 ②font identity ③回退 face ④script 决定——不按颜色/payload 值分合；GeometryKey（text+拓扑+每段字体度量）/PaintKey（resolved segment paint+段落前景，显式 `Rasterize(layout,paint)`）/InteractionKey（Start/End/Id/Payload）；换色或改 URL payload（端点不变）不重排版；改边界（含可点击范围）重排版合法；GPU 纹理键含 paint。
- **排版契约**：独立 `IRichTextService`；**所有权唯一方案**——服务缓存共享 `RichLayoutCore`（geometry+glyph payload+face lease，refcount），`Layout` 每次返回独立 `IDisposable` handle（AddRef），缓存 evict/ClearCaches 只还缓存那份，ViewCache 持份至 OnUpdate/OnUnmount/Measure 换键 Dispose；已 Dispose handle → `ObjectDisposedException`；measure/raster/hit 同 core 数据，raster 不重 shape；**缓存键 = `LayoutIdentity`（core 稳定身份）+ paint + PixelScale**，caller handle 不入键。
- **shaping 边界**：font/face/script 变化必断；paint-only 模型端点**分次 shape**——禁止跨边界 ligature、kerning 截断；一个 glyph 绝不吞两样式。
- **渲染**：每段落一次 premultiplied RGBA 栅格化；背景→文字→underline→strike；行尾 trim 无悬空矩形；**Emit 不额外构建/复制 runs/paint/glyph 集合**（resolvedPaint 缓存于 ViewCache，只在 Measure/OnUpdate/hover 边重解算）；layout 不持 StyledText/payload 引用，paint 按当前 Ranges 与 segment 区间交集归并；plain（无区间）走 `ITextService`+`TextLayoutResult` union 零 rich 分配。
- **链接**：interaction 区间按**行内 segment 矩形**命中（非 caret 吸附）；down/up 同 Id 激活、离区即取消不移回恢复；caller callback 无 OS OpenUrl；Markdown 层默认丢危险 scheme。
- **Markdown**：`src/CSharp.UI.Markdown`（Markdig 1.5.0，BSD-2-Clause，Core 零依赖）；`MarkdownBlockView : Component` `Build()` 内 AST→View 树；pipeline 具体枚举（PipeTables/EmphasisExtras/TaskLists/AutoLinks + HtmlPolicy Literal|Strip，解析 HTML 节点不 `DisableHtml`）；表格 = `Responsive(info→ScrollView(H,MarkdownTable(cells,info.AvailableWidth)))`——**复用现有 Responsive effective-proposal，零框架扩展**，min/pref 两段列宽、[min,pref] 压缩、Σmin>W 自然超宽走横滚；列表 hanging indent；不支持内容 literal 降级可见不吞；inline 逐字素单遍归一（内层栈优先）。
- **阶段门槛**：RT-01~RT-04（G1）→ MD-01~MD-04（G2）→ MD-05（G3）→ MD-06 交付。

## 任务与依赖

| 完成 | ID | 任务 | 前置依赖 | 阶段 |
|---|---|---|---|---|
| ☑ | RT-01 | [StyledText 模型：展示文本、样式区间、交互区间](01-styled-text-model.md) | 无 | A |
| ☑ | RT-02 | [IRichTextService 契约、Skia 排版实现与 face lease](02-rich-text-service.md) | RT-01 | A |
| ☐ | RT-03 | [RichText 视图：paint 解算、装饰、纹理缓存](03-rich-text-view.md) | RT-02 | B |
| ☐ | RT-04 | [链接 hit：run 矩形命中与 Id 身份状态机](04-rich-text-link-hit.md) | RT-03 | B |
| ☐ | MD-01 | [CSharp.UI.Markdown 模块、管线与解析缓存](05-markdown-module-pipeline.md) | RT-04（G1） | C |
| ☐ | MD-02 | [AST→视图适配：inline 样式栈与块映射](06-markdown-block-adapter.md) | MD-01 | C |
| ☐ | MD-03 | [MarkdownTable：Responsive + internal 专用表格布局 View](07-markdown-table.md) | MD-02 | C |
| ☐ | MD-04 | [图片/链接策略落地与不支持内容降级核对](08-markdown-assets-links.md) | MD-02 | C |
| ☐ | MD-05 | [示例、文档与 BSD notice](09-markdown-samples-docs.md) | MD-03、MD-04（G2） | D |
| ☐ | MD-06 | [交付验收：构建、套件、真实 smoke、NOTICE 与记录](10-markdown-delivery.md) | MD-05（G3） | D |

依赖链：RT-01→RT-02→RT-03→RT-04（G1）→ MD-01→MD-02→{MD-03, MD-04}（G2）→ MD-05（G3）→ MD-06。

## 阶段门槛

- A：`StyledText` 契约冻结（验证规则/相等/哈希）；`IRichTextService` 双实现可用；layout 内含 resolved run 几何，measure/raster/hit 同源。
- B：`RichText` 消费者可见行为（多样式渲染、装饰、链接 hit、plain fast path 回归）。
- D：Gallery 演示 + 文档/notice（MD-05）；双 config 构建+全测试套+真实 GL/窗口 smoke+manifest/source hash+零 stub 交付记录（MD-06）。

## 执行约定

- 每个任务经主审独立复审通过才开依赖任务；返修写 `docs/return/` 带日期文件。
- MD-03 与 MD-04 文件不同可并行（表格 vs 资源/链接策略）；MD-05 收尾。
- 所有任务只承诺消费者可观察行为与既有计数器，不承诺伪精确性能数字；不引入 wiring/mockecho/源码字符串测试；永久测试不 pin 内部字段名/常量字面。
- 范围外功能（代码高亮/远程图/LaTeX/Mermaid/富文本编辑/跨 run 选择/通用 Grid/滚动 API）不实现、不 stub、不假渲染。
