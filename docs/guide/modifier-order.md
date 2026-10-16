# 修饰符顺序

修饰符返回**新的包装视图**，因此书写顺序决定包装结构，进而决定布局与绘制范围。

## Padding 与 Background

```csharp
// 背景覆盖内边距区域（背景包在外层）
new Text("内容").Padding(16).Background(Color.White)
// 布局：Background(Padding(Text)) → 背景矩形 = 内边距外框

// 背景只覆盖内容区域（背景包在内层）
new Text("内容").Background(Color.White).Padding(16)
// 布局：Padding(Background(Text)) → 背景矩形 = 内容框
```

上面的两种写法在相同文本下产生不同的背景范围，这是被测试覆盖的行为（`LayoutTests.ModifierOrder_PaddingThenBackground_CoversPadding`）。

## 常用组合

| 目标 | 写法 |
|---|---|
| 卡片 | `content.Padding(16).Background(surface).CornerRadius(10).Border(border, 1, 10)` |
| 半透明面板 | `content.Padding(12).Background(color).CornerRadius(8).Opacity(0.5)` |
| 固定尺寸居中内容 | `content.Frame(width: 240, height: 96)`（默认 `Alignment.Center`） |
| 固定尺寸左对齐 | `content.Frame(width: 240, alignment: Alignment.Start)` |
| 滚动区域裁剪 | `new ScrollView(content).Frame(height: 240)`（滚动容器自动裁剪到视口） |
| 禁用子树 | `content.Disabled()` |

## 顺序规则速查

1. **外到内**：先写的修饰符在外层。`Padding` 在外层时会给内层内容留出边距；在内层时边距位于背景之外。
2. **背景 vs 边框**：`Background` 在内容之前绘制，`Border` 在内容之后绘制（边框不会被内容覆盖）。
3. **Opacity 与 Clip 可继承**：`Opacity` 与父级透明度相乘并影响整个子树；`Clip` 把子树裁剪到自身矩形，嵌套 `Clip` 取交集，空裁剪区不产生任何可见命令。
4. **CornerRadius 影响子树的背景、边框与矩形**，直到被内层 `CornerRadius` 覆盖。
5. **Foreground / Font / Theme 通过环境继承**：子级显式设置优先于父级；改变它们会使子树重新解析环境（可能触发一次布局）。

## 弹性与固定尺寸（Flex / FillWidth）

```csharp
// 输入框通常 320，空间不足时压缩到 120
new TextField(name.ToBinding()).Flex(basis: 320, min: 120)

// 标签固定，输入框填满剩余宽度
new HStack(
    new Text("名称"),
    new TextField(name.ToBinding()).FillWidth())
    .Spacing(8)

// 宽度上限：窄容器里自动收缩到父级可用宽度
new FrameView(
    new Text("较长的说明文字").Wrap(),
    maxWidth: 360,
    alignment: Alignment.Start)
```

- **外层优先（完整覆盖）**：`.Flex(...).FillWidth()` 与 `.FillWidth().Flex(...)` 都以外层同轴声明为准；内层的 grow/shrink、basis、min/max 与 FillWidth 的填充标记都不叠加、不残留，只有 `.Frame(...)` 的硬性固定/最小/最大声明会被保留。不同轴独立处理。
- **FillWidth 的基准**：`.FillWidth()` 覆盖内层 `.Flex()` 的显式 basis，基准回到自然尺寸；宽度无界时因此回退自然宽度，而不是沿用内层 basis。
- **固定 Frame 不被取消**：`.Frame(width: 240)` 保持 240，即使外层再写 `.Flex(shrink: 1)` 或 `.FillWidth()`；需要可压缩时改用 `basis`。
- **上限仍生效**：`new FrameView(content, maxWidth: 300).FillWidth()` 与 `new FrameView(content.FillWidth(), maxWidth: 300)` 都把实际内容矩形限制在 300（不只是期望尺寸）。声明的 `maxWidth`/`maxHeight` 只是上限，子项实际可用尺寸取父级约束与声明的较小者，所以窄容器里文字按更窄的宽度换行/省略。范围重载参数较多时直接使用 `FrameView` 构造器（如上），或写全 `Frame(width: null, height: null, minWidth: null, minHeight: null, maxWidth: 300, maxHeight: null, alignment: Alignment.Start)`。
- **轴向**：`FlexAxis.Main` 由最近的堆叠容器解析（VStack 垂直、HStack 水平）；`FlexAxis.Horizontal`/`Vertical` 固定不变，容器切换主轴时不会把宽度 basis 变成高度。
- **旧语义保持**：未使用 `Flex`/`FillWidth` 的堆叠完全沿用原行为（例如 `Spacer` 吸收全部剩余空间）。
- 响应式组合（`Flow`、`AdaptiveStack`、`Responsive`）与固定尺寸迁移见[响应式布局：使用与迁移](responsive-layout.md)。

## 与布局无关的修饰符

`Background`、`Border`、`CornerRadius`、`Opacity`、`Clip`、`Theme`（颜色部分）等只影响绘制；
协调更新在比较新旧描述时会忽略这些差异，因此**只改颜色不会重新布局**。
`Padding`、`Frame`、`Foreground`、`Font`、`Text` 内容、`Flex`、`FillWidth` 等参与布局比较的差异会触发重新测量。
