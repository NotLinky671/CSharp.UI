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

    protected override View Build()
    {
        var theme = UseState("theme", Theme.Light);
        var name = UseState("name", "中文 English 混排");
        var volume = UseState("volume", 0.4);
        var enabled = UseState("enabled", true);
        var clicks = UseState("clicks", 0);
        var notes = UseState("notes", string.Empty);

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
                        .Frame(height: 120)),
                    Section("状态与绑定", new VStack(
                        new Text($"开关状态：{(enabled.Value ? "开" : "关")}，滑块值：{volume.Value:0.00}，按钮点击：{clicks.Value} 次").Wrap(),
                        new TextField(notes.ToBinding(), "试试中文输入法与 Ctrl+V 粘贴").FillWidth(),
                        new Text("后台线程更新状态请使用 UiDispatcher.Post(action)。").WithFont(FontDescription.Default.WithSize(12)).Wrap())
                        .Spacing(6)))
                .Spacing(18)
                .Padding(20))
            .Background(current.Background)
            .Foreground(current.Foreground)
            .Theme(current)
            .Font(FontDescription.Default.WithSize(14));
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
