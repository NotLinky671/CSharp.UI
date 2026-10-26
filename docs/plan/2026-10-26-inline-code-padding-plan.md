# Markdown 行内代码：真实排版内边距（pill padding）

日期：2026-10-26
来源：用户反馈上轮行内 code「字和边贴在一起」；参考图灰色圆角 pill 内文字左右/上下
均有明显 padding。上轮（d1f105d）只把背景矩形扩到字形 advance，未把 padding 计入
line advance/wrap/lineheight/caret——本轮做**真实排版内边距**，不是 RectangleCommand 扩张。

## 设计

### 新公开字段（TextStyle 最小增量）

`TextStyle` 增加两个几何字段（进 GeometryKey `StyledTextFontSignature`，不进 paint）：

- `InlineCodePaddingHorizontal`（`double?`；null=0）——行内盒左右单侧内边距。
- `InlineCodePaddingVertical`（`double?`；null=0）——行内盒上下单侧内边距。

命名遵循既有 `CodePadding`/`CellPadding` 标量模式。`InlineCode*` 前缀沿用 Markdown
域；Core 里是**任意行内盒**语义——任何 `TextStyle` 都可设（文档写明，非仅 code）。

### 几何语义（advance / lineheight / baseline / caret / hit / wrap）

- **左/右 pad**：都是真实 advance——`padShift[boundary]` 按行累计并入 `xAt`
  与 glyph points；canonical segment `X` = 盒原点起算的字形 X（左 pad 在其左侧），
  `Width` 已含右 pad，`InsetLeft/Right` 仅记盒宽分解——pill = `[X − IL, X + W)`。
- **折行**：每个 line fragment 各自一对 pad（首行 `InsetLeft=pad`，末行
  `InsetRight=pad`，中间行左右都有）。行共享 boundary 上的片段 pad 逐行拆开
  （行首只计本行片段左 pad，行尾只计上行片段右 pad），不把相邻行的 pad 计入本行盒宽。
- **行高**：`line.Height += 2×vPad`，`baseline += vPad`（字形在行盒内居中——
  上/下留白相等，普通字共享新 baseline 不受影响）。
- **caret/hit**：caret 按 xAt 走（含 pad）；`GetRangeRects` 段起于片段边界时左缘
  再减 `InsetLeft` —— 链接/选择命中与 Emit pill 同盒几何。
- **wrap**：`MeasuredWidth` 用盒宽预算——行首续片段左 pad（insidePad[lineStart]）
  + 行内 range Start/End pad 事件 + 行尾片段右 pad；每行 fragment 盒宽
  含完整左右 pad 且不越 MaxWidth（单 glyph 盒除外——无法更小时硬断）。
- **GeometryKey**：`InlineCodePaddingHorizontal`/`Vertical` 入
  `StyledTextFontSignature` —— 同拓扑仅 padding 值不同 → 重排版；同值 → 复用。

### Emit 变化（矩形 pill）

- 每段查 style.InlineCodePaddingHorizontal → `InsetLeft`/`InsetRight`；
- rect = `[seg.X − InsetLeft, seg.X + Width)`（`Width` 已含右 pad；
  `line.Bounds` 已含 vPad，不再单独调 Y/Height）；
- 合并键 = **逻辑盒身份**：同一带 padding 的 `TextStyleRange` 覆盖的 face/script
  子段同属一盒（fallback 切段不拆 pill）；无 padding 段按 inset 等值合并；
  不同 range（即使相邻同色同 pad）与跨行绝不合并。

### Hit 一致性

`HitLink` 矩形 = Emit pill 盒几何（`[seg.X − InsetLeft, seg.X + Width)`，Width 已含
右 pad）——左/右 pad 条带可点击，普通字形间隙不误激活；`GetRangeRects` 左缘同减
`InsetLeft`，hit/选择/绘制三处同盒。

### RichTextSegment 扩展

`RichTextSegment` 增 `InsetLeft`/`InsetRight` 两字段（默认 0；普通段不变）。
Emit/测试都读真实几何，不用按 source 重新推 padding。

## 取舍

- ✅ 所有消费方（wrap/caret/hit/link/emit）单一 xAt 数据源；无第二套 padding 通道。
- ✅ 非 padding 场景零新增 alloc/分支成本（盒宽表只在存在 padding 时构造；
  signature 字段默认 null 与 null 相等不重排版）。
- ✅ `CodeBackground = null` 时保留 padding 盒语义（几何盒仍占 advance/lineheight，
  只背景矩形 skip——CSS 盒语义：`background` 与 `padding` 独立）。
- ❌ 不支持 4 边独立 padding（上下左右不对称场景没有；Thickness 复用会过度设计）。
- ❌ padding 不能作为 paint-only 覆盖（它是几何，必触发重排版）——符合用户预期。

## 验证（消费者可观察）

- `行内代码_几何Padding_进LineAdvance`：相邻普通字与 code 在真实 glyph advance 后
  保持 ≥ inset 间距；段 `Width` 随 padding 线性变化（非 source 常量）。
- `行内代码_几何Padding_进LineHeight`：vPad>0 行高 = fontHeight + 2*vPad（按行
  计算，非 line.Bounds 常量断言）。
- `行内代码_几何Padding_跨行每行各一段`：窄宽折行后每行一个独立
  RectangleCommand，X/Right 都在 frame 内且各含本行 inset。
- `行内代码_几何Padding_caret落在字形`：code 段内任意 caret 在 pill 矩形内部
  （不在 padding 区）。
- `行内代码_几何Padding_cache按geometrykey`：padding 0→X 触发重排版；
  同 padding 同 topology 复用 layout（`LayoutIdentity`/`Assert.Same`）。
- `行内代码_几何Padding_twoAdjacentCodes`：两个相邻 code 段各自独立 pill，
  不合并（insets 各归各）。
- `行内代码_CodeBackgroundNull_padding盒仍存在`：背景 null 时 line advance/height
  仍含 padding；只 RectangleCommand 缺。

测试用真实 App/RichNode/`RectangleCommand.Bounds`/layout.Segments 几何，不
pin 默认常量/私字段/源码字符串/非空/notthrow。

## 实施范围

`TextStyle`（+2 字段 + signature）、`StyledTextSegment`（signature 载体）、
`StyledText`（BuildSegments 把 padding 塞进 signature）、`RichTextLayoutBuilder`
（xAt/行高/canonical segment inset）、`RichText` Emit（pill rect）、
`MarkdownStyleOptions`（+InlineCodePaddingHorizontal/Vertical 默认值）、
`InlineAdapter`（把 style padding 传给 ResolvedStyle）、`RichTextContracts`
（RichTextSegment + InsetLeft/Right）。
