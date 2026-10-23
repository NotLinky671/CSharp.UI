using System.Text;
using CSharp.UI;
using CSharp.UI.Samples;

namespace CSharp.UI.Samples.ControlGallery;

/// <summary>
/// 控件展示示例：覆盖首版全部控件、布局、修饰符、主题、图片与中英文文本，
/// 并用通用弹性 / 换行 / 自适应 / 响应式能力让整页在 320、480、768、1024 逻辑宽度下都可用。
/// </summary>
/// <remarks>
/// <para>
/// 页面不读取窗口或全局尺寸：每个区域只依赖父容器分配的逻辑宽度。
/// 页头与“标签 + 控件”表单行使用 <see cref="AdaptiveStack"/>（稳定身份切换排列），
/// 按钮组与修饰符标签使用 <see cref="Flow"/>（按可用宽度换行），
/// 输入框 / 滑块使用 <see cref="Flex"/> 的 <c>.FillWidth()</c>（占满父容器剩余宽度）。
/// </para>
/// <para>
/// 图片使用 <c>shared/Assets/logo.png</c>（仓库自带、自行生成的资源）；字体使用系统字体回退，
/// 不重新分发第三方字体二进制文件（详见 docs/guide/platform-notes.md）。
/// </para>
/// </remarks>
public sealed class GalleryApp : Component
{
    /// <summary>页头与表单行切换横/纵排列的演示阈值（父级可用逻辑宽度）。</summary>
    private const double FormBreakpoint = 560;

    /// <summary>“布局”区域切换横/纵排列的演示阈值。</summary>
    private const double LayoutBreakpoint = 840;
    /// <summary>“滚动条”演示区多行编辑器的初始文本（仅首次 UseState 求值用，避免每次重建分配）。</summary>
    private static readonly string ScrollbarDemoText =
        string.Join("\n", Enumerable.Range(1, 20).Select(i => $"滚动条演示第 {i} 行：内容超视口时右侧显示滚动条"));

    protected override View Build()
    {
        var theme = UseState("theme", Theme.Light);
        var name = UseState("name", "中文 English 混排");
        var volume = UseState("volume", 0.4);
        var enabled = UseState("enabled", true);
        var clicks = UseState("clicks", 0);
        var notes = UseState("notes", string.Empty);
        var editDemo = UseState("editDemo", "选中这段文字后输入、粘贴或按 Ctrl+Z 撤销");
        var notesMulti = UseState("notesMulti", "第一行：输入或粘贴多行文本\n第二行：Enter 换行、Shift+方向键选区、Ctrl+Z 撤销");
        var scrollbarLong = UseState("scrollbarLong", true);
        var scrollbarTall = UseState("scrollbarTall", false);
        var scrollbarFloating = UseState("scrollbarFloating", false);
        var scrollbarText = UseState("scrollbarText", ScrollbarDemoText);

        var current = theme.Value;
        var logoPath = Path.Combine(AppContext.BaseDirectory, "shared", "Assets", "logo.png");

        return new ScrollView(
                new VStack(
                    Header(current, theme),
                    Section("文本与字体", new VStack(
                        new Text("默认字体：中文与 English 混排，自动使用系统字体回退。").Wrap(),
                        new Text("更大字号（28）").WithFont(FontDescription.Default.WithSize(28)).Wrap(),
                        new Text("加粗（Bold）").WithFont(FontDescription.Default.Bold()).Wrap(),
                        // maxWidth 只提供展示用的宽度上限：实际换行宽度取父容器可用宽度与上限的较小者。
                        new FrameView(
                            new Text("有限宽度内自动换行：CSharp.UI 用声明式视图描述界面，框架负责布局、输入与绘制。").Wrap(3),
                            maxWidth: 360,
                            alignment: Alignment.Start),
                        // 省略同样按实际容器宽度计算：窄窗口先缩到容器宽度，再在可用宽度内截断。
                        new FrameView(
                            new Text("超出可用宽度时以省略号截断：这是一段很长的说明文本用于演示省略行为。").Ellipsis(),
                            maxWidth: 320,
                            alignment: Alignment.Start))
                        .Spacing(6)),
                    Section("布局", new AdaptiveStack(
                        LayoutBreakpoint,
                        new VStack(new Text("VStack 1"), new Text("VStack 2")).Spacing(4),
                        // 原来的固定 240 改为有下限的可伸缩范围：宽模式保留演示宽度，窄模式不会撑破父容器。
                        new FrameView(
                            new VStack(new Text("HStack 左"), new Spacer(), new Text("HStack 右")),
                            minWidth: 200,
                            maxWidth: 240,
                            alignment: Alignment.Start),
                        new ZStack(
                            new Rectangle(Color.Parse("#2563EB")).WithCornerRadius(6).Opacity(0.25),
                            new Text("ZStack 层叠").Frame(width: 140, height: 44)))
                        .Spacing(16)
                        .VerticalSpacing(12)),
                    Section("基础控件", new VStack(
                        // 按钮组使用 Flow：空间不足时按声明顺序换行，不创建新的运行时父节点。
                        new Flow(
                            new Button($"点击计数：{clicks.Value}", () => clicks.Value++),
                            new Button("主操作按钮", () => clicks.Value = 0).Primary(),
                            new Button("禁用按钮").Disabled())
                            .Spacing(8)
                            .LineSpacing(8),
                        new Toggle(enabled.ToBinding(), "开关（绑定布尔状态）"),
                        // 滑块行：窄到低于断点时标签、滑块与数值改为上下排列，滑块始终占满可用宽度。
                        new AdaptiveStack(
                            FormBreakpoint,
                            new Text("滑块"),
                            new Slider(volume.ToBinding(), 0, 1, 0.05).FillWidth(),
                            new Text($"{volume.Value:0.00}"))
                            .Spacing(8)
                            .VerticalSpacing(6),
                        new AdaptiveStack(
                            FormBreakpoint,
                            new Text("单行输入"),
                            new TextField(name.ToBinding(), "请输入文本").FillWidth())
                            .Spacing(8)
                            .VerticalSpacing(6))
                        .Spacing(8)),
                    Section("修饰符", new ZStack(
                        new Rectangle(current.Surface).WithCornerRadius(10),
                        new VStack(
                            new Text("背景 / 圆角 / 边框 / 内边距 / 透明度").Wrap(),
                            // 修饰符标签组同样换行，ZStack 高度随内容增长，不再用固定高度裁剪。
                            new Flow(
                                new Text("不透明").Padding(8).Background(Color.Parse("#2563EB")).Foreground(Color.White).CornerRadius(6),
                                new Text("50% 透明").Padding(8).Background(Color.Parse("#2563EB")).Foreground(Color.White).CornerRadius(6).Opacity(0.5),
                                new Text("仅边框").Padding(8).Border(current.Danger, 1.5, 6))
                                .Spacing(8)
                                .LineSpacing(8))
                            .Spacing(8)
                            .Padding(12))),
                    Section("图片", new AdaptiveStack(
                        FormBreakpoint,
                        new Image(logoPath).Resize(ImageResizeMode.Fit)
                            .Frame(width: 96, height: 96, alignment: Alignment.Start)
                            .Border(current.Border, 1, 8),
                        new VStack(
                            new Text("Image 支持自然尺寸、Fit / Fill / Stretch / Center 缩放。").Wrap(),
                            new Text(File.Exists(logoPath)
                                    ? $"资源：{Path.GetFileName(logoPath)}"
                                    : "未找到 shared/Assets/logo.png，示例使用占位图形。")
                                .WithFont(FontDescription.Default.WithSize(12))
                                .Wrap())
                            .Spacing(4))
                        .Spacing(12)
                        .VerticalSpacing(8)
                        .Align(Alignment.Start)),
                    // 真实内层滚动视图：120 逻辑像素的视口由外层 Frame 约束，内容保持自然高度；
                    // 视口外的行由 ScrollView 自身裁剪，滚轮先把事件交给内层、到边界后由整页外层接手。
                    Section("滚动容器", new ScrollView(
                        new VStack(
                            new Text($"多行内容用于演示单轴滚动；下面共 {40} 行。").Wrap(),
                            new ForEach<int>(Enumerable.Range(1, 40), i => new Text($"第 {i} 行：滚动内容"), i => i))
                        .Spacing(4))
                        .Key("gallery-nested-scroll")
                        .Frame(height: 120)),
                    // SB-05 滚动条演示：同一批节点在 40 行与 4 行、标准/浮动样式之间切换，
                    // 长→短展示滚动条隐藏并 clamp 偏移；浮动样式不占槽、实际滚动显示约 1 秒后隐藏。
                    Section("滚动条", new VStack(
                        new Flow(
                            new Button(scrollbarLong.Value ? "切换为短内容（隐藏滚动条）" : "切换为长列表（显示滚动条）",
                                () => scrollbarLong.Value = !scrollbarLong.Value),
                            new Toggle(scrollbarTall.ToBinding(), "加高视口（尺寸变化回归）"),
                            new Toggle(scrollbarFloating.ToBinding(), "浮动滚动条"))
                            .Spacing(8)
                            .LineSpacing(8),
                        new Text(scrollbarFloating.Value
                                ? "纵向 ScrollView（浮动）：滚动条覆盖内容右缘不占 12 像素槽；实际滚动后显示，停止约 1 秒隐藏；悬停在条上或拖拽期间不隐藏；隐藏后同一位置点击落到内容。"
                                : "纵向 ScrollView（标准）：内容溢出显示滚动条；缩内容后隐藏但轨道槽恒占 12 逻辑像素（persistent gutter，无布局回流）。拖动滑块或点轨道翻页一次（无自动重复）。").Wrap(),
                        new ScrollView(
                                new VStack(
                                    new ForEach<int>(
                                        Enumerable.Range(1, scrollbarLong.Value ? 40 : 4),
                                        i => new Text($"纵向滚动第 {i} 行"), i => i))
                                .Spacing(4),
                                ScrollAxis.Vertical)
                            {
                                ScrollbarStyle = scrollbarFloating.Value ? ScrollbarStyle.Floating : ScrollbarStyle.Standard,
                            }
                            .Key("scrollbar-vertical-demo")
                            .Frame(height: scrollbarTall.Value ? 180 : 120),
                        new Text("横向 ScrollView：宽内容溢出在底部显示横向滚动条（同一套共享几何/交互）。").Wrap(),
                        new ScrollView(
                            new HStack(
                                new ForEach<int>(Enumerable.Range(1, 30), i => new Text($"列{i:D2}").Padding(8), i => i))
                            .Spacing(4),
                            ScrollAxis.Horizontal)
                            {
                                ScrollbarStyle = scrollbarFloating.Value ? ScrollbarStyle.Floating : ScrollbarStyle.Standard,
                            }
                            .Frame(height: 56),
                        new Text("多行 TextEditor：内容超过视口显示纵向滚动条；组合期间用滑块浏览时候选锚点跟随 caret——caret 被裁出视口时锚点撤除但组合保活，真实新预编辑/caret 推进后重新锚定（同值通知不回弹）。Ctrl+End 到底后滚轮上滚到顶，继续滚则冒泡给外层整页滚动。").Wrap(),
                        new TextEditor(scrollbarText.ToBinding(), "行数超视口出滚动条")
                        {
                            MinLines = 4,
                            Height = 140,
                            ScrollbarStyle = scrollbarFloating.Value ? ScrollbarStyle.Floating : ScrollbarStyle.Standard,
                        }
                            .FillWidth())
                        .Spacing(6)),
                    // TextEditor 多行编辑区：固定 MinLines 视口 + 内部 ScrollY（不随内容增高），
                    // 行数超过视口时滚轮先由编辑器消费、到边界后冒泡给外层整页 ScrollView。
                    Section("多行编辑", new VStack(
                        new TextEditor(notesMulti.ToBinding(), "多行输入：Enter 换行、Ctrl+Z 撤销、拖选、滚轮滚动")
                        {
                            MinLines = 4,
                            Height = 160,
                        }
                            .FillWidth(),
                        new Text($"多行内容长度：{notesMulti.Value.Length}（外部读取同一 Binding）")
                            .WithFont(FontDescription.Default.WithSize(12)))
                        .Spacing(6)),
                    Section("状态与绑定", new VStack(
                        new Text($"开关状态：{(enabled.Value ? "开" : "关")}，滑块值：{volume.Value:0.00}，按钮点击：{clicks.Value} 次").Wrap(),
                        new TextField(notes.ToBinding(), "试试中文输入法与 Ctrl+V 粘贴").FillWidth(),
                        new Text("后台线程更新状态请使用 UiDispatcher.Post(action)。").WithFont(FontDescription.Default.WithSize(12)).Wrap())
                        .Spacing(6)),
                    // TI-09：只演示既有快捷键与输入法通道，不新增公开命令入口。
                    Section("文本编辑与输入法", new VStack(
                        new TextField(editDemo.ToBinding(), "输入、粘贴、选区替换、撤销重做").FillWidth(),
                        new Text("Windows/Linux：Ctrl+Z 撤销，Ctrl+Y 或 Ctrl+Shift+Z 重做；macOS：Command+Z 撤销，Command+Shift+Z 重做。").Wrap(),
                        new Text("中文输入法的预编辑与候选显示取决于平台，详见 docs/guide/platform-notes.md。").Wrap(),
                        new Text($"当前文本长度：{editDemo.Value.Length}")
                            .WithFont(FontDescription.Default.WithSize(12)))
                        .Spacing(6)))
                .Spacing(18)
                .Padding(20))
            .Background(current.Background)
            .Foreground(current.Foreground)
            .Theme(current)
            .Font(FontDescription.Default.WithSize(14))
            .Key("gallery-root-scroll");
    }

    /// <summary>页头：窄时纵向排列，主题按钮始终可见；宽时标题在左、按钮在右。</summary>
    private static View Header(Theme theme, State<Theme> themeState)
        => new AdaptiveStack(
                FormBreakpoint,
                new Text("CSharp.UI 控件展示").WithFont(FontDescription.Default.WithSize(24)).Wrap(),
                new Spacer(),
                new Button(theme.IsDark ? "明亮主题" : "暗色主题", () => themeState.Value = theme.IsDark ? Theme.Light : Theme.Dark))
            .Spacing(12)
            .VerticalSpacing(8)
            .Align(Alignment.Start);

    private static View Section(string title, View content)
        => new VStack(
                new Text(title).WithFont(FontDescription.Default.Bold().WithSize(16)).Wrap(),
                content)
            .Spacing(8)
            .Padding(12)
            .Border(Color.Parse("#88888844"), 1, 8);
}

internal static class Program
{
    private static int Main()
    {
        var assets = DescribeAssets();
        return UiApplication.Run(
            new GalleryApp(),
            new UiApplicationOptions
            {
                Title = "CSharp.UI — 控件展示",
                Width = 900,
                Height = 700,
                Started = context =>
                {
                    context.Runtime.ReportDiagnostic(assets);
                },
                FrameRendered = SmokeTestHook.CreateFrameHook("ControlGallery"),
            });
    }

    private static string DescribeAssets()
    {
        var builder = new StringBuilder("资源自检：");
        var logo = Path.Combine(AppContext.BaseDirectory, "shared", "Assets", "logo.png");
        builder.Append(File.Exists(logo) ? "已找到示例图片" : "缺少示例图片（使用占位图形）");
        return builder.ToString();
    }
}
