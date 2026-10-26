# MD-03：MarkdownTable——`Responsive` + internal 专用表格布局 View

创建日期：2026-10-25

状态:**已完成并验收**（2026-10-26；二段列宽+嵌套块 min-content+LayoutPropertiesEquivalent 换档失效修复）

阶段：C

设计依据：[计划](../plan/2026-10-25-rich-text-and-markdown-plan.md) §决策十。

[返回任务索引](README.md)

前置依赖：MD-02（与 MD-04 可并行）。

## 文件范围

- `src/CSharp.UI.Markdown/MarkdownTable.cs`（新，internal）：专用表格布局 View。
- `src/CSharp.UI.Markdown/BlockAdapter.cs`：`TableBlock` → `Responsive`+`ScrollView`+`MarkdownTable`（替换降级路径）。
- `tests/CSharp.UI.Markdown.Tests/`：表格单测；`tests/CSharp.UI.Tests/`（`Responsive` 回归——本任务不改它，仅验证）。

## 实现契约

### 结构（唯一方案，零框架扩展）

```csharp
// BlockAdapter 对 TableBlock 恒产出（无 WrapTables 开关）：
new Responsive(info => new ScrollView(
    new MarkdownTable(rows, availableWidth:
        info.IsWidthBounded ? info.AvailableWidth : double.PositiveInfinity),
    ScrollAxis.Horizontal))
```

- `Responsive`（`Views/Responsive.cs:83-144` 现有公开机制）：`Measure` 在 `context.IsEffectiveProposal` 时把父最终有效宽经 `RequestResponsiveContent` 交给 builder；builder 回调产出的 child 结构在安全阶段一次声明齐全——**不是** Measure/Emit 期建 children。父宽变化 → 不同 `info` → `InvalidateResponsiveContent` 重建表。
- `MarkdownTable`（internal，`CSharp.UI.Markdown`）：构造参数 = 已建好的行/cell 视图（Build 期一次建全）+ immutable `availableWidth`；自身 `Measure` 在 HScroll 的 `MaxWidth=∞` 约束下**仍按构造时 `availableWidth` 分配列宽**；`Arrange` 收到超宽 bounds 不覆盖 captured available（`availableWidth=∞` → 列宽=pref）。
- 行/cell：行容器列表，每 cell `VStack` 承载递归 BlockAdapter 块序列；`MarkdownTable` 统一列宽向量与行高（非各行自定列宽的 HStack-of-VStack）。
- **不改** `ScrollView`/`Responsive`/`RequiresEffectiveProposal` 任何现有代码。

### 两段列宽（`Measure`，用 `context` 服务测 cell）

- **min-content**：每列 = max over cells（cell 最长**不可断单元**宽：拉丁最长词、CJK/emoji 单字素簇、code 最长 token）+ 2×cellPadding；cell 内多块取 max。
- **pref-content**：每列 = max over cells（NoWrap 自然宽）+ padding；`min ≤ pref`。
- **分配**（`W=availableWidth`）：
  - `Σpref ≤ W` → 列宽=pref；
  - `Σmin ≤ W < Σpref` → 按 `(pref−min)` 权重从 pref 向 min 压缩，clamp `[min,pref]`，`Σ=W`；
  - `Σmin > W` → 列宽=min，`DesiredWidth=Σmin`（自然超宽由外层 HScroll 承载——shell 有界、内容 DesiredSize 超出，横向滚动/clip/滚动条/边界 wheel 冒泡全复用 `ScrollView` 现状）。
- 列宽定后每 cell 按固定列宽 `context.Measure`（正常两遍布局）；行高=max(cell desired)；`Arrange` 按列宽向量分 bounds、行 stretch。
- header 行 `Weight=Bold`；`ColumnDefinition.Alignment`→cell `RichText.Alignment`；行分隔 `Rectangle` 1px；缺 cell 按最大列补齐。
- wheel：纵轮在横向可滚表格上横滚属现有 runtime wheel fallback 语义，不新增全局行为。

## 边界

- 不做列拖拽/排序/选择/sticky header；`MarkdownTable` 不演进为公开 Grid。
- `Responsive` builder 在父宽变化时整表重建（长表格只承诺正确性）。
- 两段测量多一次 cell measure。

## 验收

- 中英文混合表：全行同列同宽、header 粗体、行分隔、padding、`|:--|:-:|--:|` 对齐正确。
- 三档分配：`Σpref ≤ W` 自然宽不撑满；`Σmin ≤ W < Σpref` 压缩至恰 W 无横滚；`Σmin > W`（如 320 窄宽）DesiredWidth=Σmin，外层 HScroll 可滚见全列。
- 父宽变化 → `Responsive` 重建表、列宽重分配（320↔1024 切换断言）。
- cell 内嵌列表/引用/代码不破坏行列对齐；行高取最高 cell。
- `Responsive` 既有测试全绿；`ScrollView` wheel 边界冒泡不变。
