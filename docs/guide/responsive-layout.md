# 响应式布局：使用与迁移

本指南说明如何用通用 API 让界面按**父容器分配的逻辑宽度**自适应，以及如何把原来的固定尺寸写法迁移过来。
所有能力都不读取窗口、屏幕分辨率或 DPI；同一逻辑宽度在 100%/125%/150%/200% 下得到相同布局选择。

示例都在 `CSharp.UI` 命名空间下，代码片段使用真实的公开 API。

## 1. 空间从父容器来

每个容器只根据自己的约束决定尺寸。断点、弹性分配和换行都基于**父级扣除 `Padding`/`Frame` 后提供的逻辑可用宽度**：

- 有界宽度：`LayoutConstraints.MaxWidth` 是有限值。
- 无界宽度（例如横向 `ScrollView` 的内容、或未给上限的容器）：宽度以 `PositiveInfinity` 表达，不使用 0、屏幕宽度或任意大常数冒充。

因此同一窗口里不同父宽度的两个容器可以同时选择不同布局，见[独立嵌套示例](#7-独立嵌套示例)。

## 2. 弹性长度：`Flex` 与 `FillWidth`

```csharp
using CSharp.UI;

static View BuildForm(State<string> name) =>
    new HStack(
        new Text("名称"),
        // 通常 240、可以压缩到 120 的输入框
        new TextField(name.ToBinding()).Flex(basis: 240, min: 120))
    .Spacing(8);

static View BuildSearch(State<string> query) =>
    new HStack(
        new Text("搜索"),
        // 标签固定、输入框占满剩余宽度
        new TextField(query.ToBinding()).FillWidth())
    .Spacing(8);
```

- `.Flex(grow, shrink, basis, min, max, axis)` 的参数在声明时校验：权重与显式 basis/min 必须有限且非负，`max` 允许正无穷，禁止 NaN，且 `min ≤ max`。
- `.Frame(width: 240)` 始终是固定尺寸，不会被弹性分配压缩；需要“通常 240、可以变窄”时使用 `.Flex(basis: 240, min: 120)`。
- `.FillWidth()` 在有界宽度下占满可用宽度：横向 Stack 主轴上参与伸缩，纵向 Stack 交叉轴上占满；宽度无界时回退自然尺寸。
- **同轴外层优先**：外层 `.Flex()`/`.FillWidth()` 完整覆盖内层的弹性声明（grow/shrink、basis、min/max、FillWidth 标记都不叠加），只有 `.Frame(...)` 的硬性固定/最小/最大声明会被保留。不同轴独立处理。
- 固定项与不可压缩项之和仍超出容器时会输出带节点路径的溢出诊断，不产生负尺寸；需要可压缩就改用 `basis`。

## 3. 自动换行：`Flow`

```csharp
static View BuildActions(Action save, Action saveAs, Action cancel) =>
    new Flow(
        new Button("保存", save),
        new Button("另存为", saveAs),
        new Button("取消", cancel))
    .Spacing(8)
    .LineSpacing(8);
```

- 按声明顺序从左到右贪心分行；行宽来自父容器可用宽度，行高取该行最高项，整体高度按 `LineSpacing` 累加。
- 分行只改变子节点的 `Frame`，不创建“每行运行时父节点”：子项身份、焦点、输入状态与 Tab 顺序保持。
- 第一版不做逐行弹性权重分配：直接子项的 `.Flex()` 会给出可定位诊断，请把弹性表单项放进内嵌 `HStack`/`AdaptiveStack`；`.FillWidth()` 作为“占整行”请求独占一行。
- 宽度无界时全部项排成单行、使用自然尺寸。

## 4. 稳定的横纵切换：`AdaptiveStack`

```csharp
static View BuildHeader(State<Theme> theme, Theme current) =>
    new AdaptiveStack(
            560,
            new Text("标题"),
            new Spacer(),
            new Button(
                current.IsDark ? "明亮主题" : "暗色主题",
                () => theme.Value = current.IsDark ? Theme.Light : Theme.Dark))
        .Spacing(12)
        .VerticalSpacing(8)
        .Align(Alignment.Start);
```

- 父级可用逻辑宽度 `< breakpoint` 为纵向，`>= breakpoint` 为横向；断点必须有限且为正。
- 容器类型、运行时父节点与子项集合始终不变，只切换测量/排列策略，因此输入框文本、光标、选区、焦点、组件 `UseState` 与滚动状态在宽窄切换时保持。
- `FlexAxis.Main` 随两种主轴解释；显式 `FlexAxis.Horizontal` 的宽度 basis 在纵向模式下不会变成高度。Spacer 只在主轴有界时吸收剩余空间。

## 5. 任意内容分支：`Responsive`

```csharp
public sealed class Form : Component
{
    protected override View Build()
    {
        var name = UseState("name", string.Empty);   // 持久数据由外层组件持有

        return new Responsive(info =>
            info.IsWidthBounded && info.AvailableWidth < 560
                ? new VStack(
                    new Text("窄分支"),
                    new TextField(name.ToBinding()).FillWidth()).Spacing(6)
                : new HStack(
                    new Text("宽分支"),
                    new TextField(name.ToBinding()).FillWidth()).Spacing(8));
    }
}
```

- `ResponsiveInfo` 只读提供 `AvailableWidth` / `IsWidthBounded`；无界宽度以 `PositiveInfinity` + `false` 表达，应用必须能选择自然尺寸分支。
- **只在安全阶段构建**：测量（含探测与自然尺寸测量）只登记最终有效提案，不执行 builder；`UiRuntime` 在重建与布局之间构建内容并重新布局到稳定后才绘制。短时间连续的状态与宽度变化合并为一次构建。
- **builder 是纯描述构建**：可以读取已有的 `State<T>`（自动登记为动态内容依赖），但不得写状态、创建 `UseState`、执行 IO 或操作窗口。
- **分支切换沿用协调规则**：同父级同 Key/类型的节点复用；跨父级或类型变化会真正卸载并清理焦点、指针捕获、选区与绑定依赖。

## 6. 状态身份与输入焦点边界

| 场景 | 行为 | 建议 |
|---|---|---|
| 同一个 `AdaptiveStack` 内切换横/纵 | 同一批子节点、同一运行时父节点 | 需要跨断点继续编辑时优先用它 |
| `Responsive` 同父级同 Key/类型 | 节点复用 | 需要保留列表项身份时给稳定 Key |
| `Responsive` 跨父级或类型变化 | 旧分支卸载、新分支新建，焦点不迁移 | 持久数据放外层 `State`/`Binding`；不要假设焦点自动跟随 |
| 组件重建 | `UseState` 按名称取回原状态 | 数据放在外层组件，而不是分支内部 |

示例：外层持有输入状态，宽/窄分支都用同一个 `Binding`，切换分支后内容仍在，但**输入焦点不会自动跨父级迁移**：

```csharp
public sealed class SubmitForm : Component
{
    protected override View Build()
    {
        var name = UseState("name", string.Empty);

        return new Responsive(info => info.AvailableWidth < 480
            ? new VStack(
                new TextField(name.ToBinding()),
                new Button("提交", () => Submit(name))).Spacing(6)
            : new HStack(
                new Text("名称"),
                new TextField(name.ToBinding()),
                new Button("提交", () => Submit(name))).Spacing(8));
    }

    private static void Submit(State<string> name)
    {
        // 处理提交；name.Value 保存着跨分支保留的文本。
    }
}
```

需要焦点与本地编辑状态稳定时改用 `AdaptiveStack`：

```csharp
static View BuildStableRow(State<string> name) =>
    new AdaptiveStack(
            560,
            new Text("名称"),
            new TextField(name.ToBinding()).FillWidth(),
            new Button("提交", () => Submit(name)))
        .Spacing(8)
        .VerticalSpacing(6);

static void Submit(State<string> name)
{
    // 处理提交。
}
```

## 7. 独立嵌套示例

`samples/CSharp.UI.Samples.ControlGallery` 里的 `NestedResponsiveExample` 可直接实例化，在同一个窗口并排展示宽/窄两个父容器：

```csharp
using CSharp.UI;
using CSharp.UI.Samples.ControlGallery;

public sealed class Host : Component
{
    protected override View Build() => new NestedResponsiveExample();
}
```

- 两个面板宽度分别有 `maxWidth` 上限（640 / 320），宽度来自 `Flow` 当前行宽分配，不读取窗口尺寸。
- 同一窗口里宽面板选择横向布局与宽内容分支，窄面板选择纵向布局与窄内容分支。
- 每个面板内的 `AdaptiveStack` 保持“稳定输入”的输入框节点、焦点、选区与内层 `ScrollView` 滚动位置。

## 8. 从固定尺寸迁移

| 旧写法 | 新写法 | 说明 |
|---|---|---|
| `text.Wrap().Frame(width: 360)` 作为唯一约束 | `new FrameView(text.Wrap(), maxWidth: 360, alignment: Alignment.Start)` | 上限在窄容器里自动收缩为父级可用宽度 |
| `text.Ellipsis().Frame(width: 320)` | `new FrameView(text.Ellipsis(), maxWidth: 320, alignment: Alignment.Start)` | 省略按实际可用宽度计算 |
| `new HStack(标签, 控件.Frame(width: 240))` | `new AdaptiveStack(560, 标签, 控件.FillWidth())` | 窄到断点以下改为上下排列，控件仍占满宽度 |
| 固定宽度的按钮行 `HStack` | `new Flow(按钮…).Spacing(8).LineSpacing(8)` | 空间不足时按声明顺序换行 |
| `VStack`/`HStack` 子树互换做响应式 | `new AdaptiveStack(...)` 或 `new Responsive(...)` | 切换不重建子树，或改用受控的安全阶段构建 |

> `Frame` 的范围重载参数较多：`new FrameView(content, maxWidth: 320, alignment: Alignment.Start)` 是等价且更易读的写法；
> 若使用扩展方法需要写全参数，例如 `content.Frame(width: null, height: null, minWidth: null, minHeight: null, maxWidth: 320, maxHeight: null, alignment: Alignment.Start)`。
> 声明的 `maxWidth`/`maxHeight` 只是上限，实际可用尺寸取父级约束与声明的较小者。

## 9. 已知边界

- 宽度无界时：`FillWidth` 回退自然尺寸，`AdaptiveStack` 使用横向自然尺寸，`Flow` 排成单行，`Responsive` 必须能选择自然尺寸分支。
- 本轮只支持**宽度**断点；高度按内容自然展开，兼容纵向 `ScrollView`。通用高度断点、自动网格与容器查询样式语言不在范围内。
- 未声明弹性或换行的硬固定内容仍可能溢出，框架会给出可定位诊断，而不是暗中压缩固定 `Frame`。
- `Responsive` 不承诺跨父级迁移焦点或本地编辑状态；需要时使用 `AdaptiveStack`，并把数据放在外层。
- DPI 1 / 1.25 / 1.5 / 2 下同一逻辑宽度得到相同布局选择；文字纹理始终按当前 DPI 重新栅格化（1 纹素 = 1 物理像素），跨断点或 DPI 变化后不会缩放旧文字纹理。以上在本机 Windows / 真实 Skia 排版 / OpenGL 3.3 上验证，macOS/Linux 真机与 LCD/ClearType 不在结论内。
- 跨断点输入（按钮、Toggle、Slider 拖动与指针捕获、Tab、TextField 中文输入与选区、内外双层滚轮）在本机按真实命中与真实排版验证；真实宿主连续跨断点是**程序化** resize + 刷新回调，Windows 原生 SC_SIZE 模态循环由独立探针单独记录，人工鼠标拖拽与其他平台未作验证。
- 静止时不重新构建、测量或重绘；连续宽度变化时布局轮数与测量量有界。纹理缓存容量是**长期目标**：本帧与上一帧引用的条目受保护，可临时超过容量，并在后续帧边界回收；本轮样例的存活峰值不是全局硬上限。未做帧率承诺。
