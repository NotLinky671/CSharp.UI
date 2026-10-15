using System.Text;
using CSharp.UI;
using CSharp.UI.Samples;

namespace CSharp.UI.Samples.ControlGallery;

/// <summary>
/// 控件展示示例：覆盖首版全部控件、布局、修饰符、主题、图片与中英文文本。
/// </summary>
/// <remarks>
/// 图片使用 <c>shared/Assets/logo.png</c>（仓库自带、自行生成的资源）；字体使用系统字体回退，
/// 不重新分发第三方字体二进制文件（详见 docs/guide/platform-notes.md）。
/// </remarks>
public sealed class GalleryApp : Component
{
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
                        new Text("默认字体：中文与 English 混排，自动使用系统字体回退。"),
                        new Text("更大字号（28）").WithFont(FontDescription.Default.WithSize(28)),
                        new Text("加粗（Bold）").WithFont(FontDescription.Default.Bold()),
                        new Text("有限宽度内自动换行：CSharp.UI 用声明式视图描述界面，框架负责布局、输入与绘制。").Wrap(3).Frame(width: 360, alignment: Alignment.Start),
                        new Text("超出可用宽度时以省略号截断：这是一段很长的说明文本用于演示省略行为。").Ellipsis().Frame(width: 320, alignment: Alignment.Start))),
                    Section("布局", new HStack(
                        new VStack(new Text("VStack 1"), new Text("VStack 2")).Spacing(4),
                        new VStack(new Text("HStack 左"), new Spacer(), new Text("HStack 右")).Frame(width: 240, alignment: Alignment.Start),
                        new ZStack(
                            new Rectangle(Color.Parse("#2563EB")).WithCornerRadius(6).Opacity(0.25),
                            new Text("ZStack 层叠").Frame(width: 140, height: 44))).Spacing(16)),
                    Section("基础控件", new VStack(
                        new HStack(
                            new Button($"点击计数：{clicks.Value}", () => clicks.Value++),
                            new Button("主操作按钮", () => clicks.Value = 0).Primary(),
                            new Button("禁用按钮").Disabled()).Spacing(8),
                        new Toggle(enabled.ToBinding(), "开关（绑定布尔状态）"),
                        new HStack(new Text("滑块"), new Slider(volume.ToBinding(), 0, 1, 0.05, 220).Frame(width: 220, alignment: Alignment.Start), new Text($"{volume.Value:0.00}")).Spacing(8),
                        new HStack(new Text("单行输入"), new TextField(name.ToBinding(), "请输入文本").Frame(width: 240, alignment: Alignment.Start)).Spacing(8)).Spacing(8)),
                    Section("修饰符", new ZStack(
                        new Rectangle(current.Surface).WithCornerRadius(10),
                        new VStack(
                            new Text("背景 / 圆角 / 边框 / 内边距 / 透明度"),
                            new HStack(
                                new Text("不透明").Padding(8).Background(Color.Parse("#2563EB")).Foreground(Color.White).CornerRadius(6),
                                new Text("50% 透明").Padding(8).Background(Color.Parse("#2563EB")).Foreground(Color.White).CornerRadius(6).Opacity(0.5),
                                new Text("仅边框").Padding(8).Border(current.Danger, 1.5, 6)).Spacing(8))
                        .Spacing(8)
                        .Padding(12)).Frame(height: 110)),
                    Section("图片", new HStack(
                        new Image(logoPath).Resize(ImageResizeMode.Fit).Frame(width: 96, height: 96, alignment: Alignment.Start).Border(current.Border, 1, 8),
                        new VStack(
                            new Text("Image 支持自然尺寸、Fit / Fill / Stretch / Center 缩放。"),
                            new Text(File.Exists(logoPath) ? $"资源：{Path.GetFileName(logoPath)}" : "未找到 shared/Assets/logo.png，示例使用占位图形。")
                                .WithFont(FontDescription.Default.WithSize(12)))
                        .Spacing(4)).Spacing(12)),
                    // 真实内层滚动视图：120 逻辑像素的视口由外层 Frame 约束，内容保持自然高度；
                    // 视口外的行由 ScrollView 自身裁剪，滚轮先把事件交给内层、到边界后由整页外层接手。
                    Section("滚动容器", new ScrollView(
                        new VStack(
                            new Text($"多行内容用于演示单轴滚动；下面共 {40} 行。"),
                            new ForEach<int>(Enumerable.Range(1, 40), i => new Text($"第 {i} 行：滚动内容"), i => i))
                        .Spacing(4))
                        .Frame(height: 120)),
                    Section("状态与绑定", new VStack(
                        new Text($"开关状态：{(enabled.Value ? "开" : "关")}，滑块值：{volume.Value:0.00}，按钮点击：{clicks.Value} 次"),
                        new TextField(notes.ToBinding(), "试试中文输入法与 Ctrl+V 粘贴"),
                        new Text("后台线程更新状态请使用 UiDispatcher.Post(action)。").WithFont(FontDescription.Default.WithSize(12))).Spacing(6)))
                .Spacing(18)
                .Padding(20))
            .Background(current.Background)
            .Foreground(current.Foreground)
            .Theme(current)
            .Font(FontDescription.Default.WithSize(14));
    }

    private static View Header(Theme theme, State<Theme> themeState)
        => new HStack(
                new Text("CSharp.UI 控件展示").WithFont(FontDescription.Default.WithSize(24)),
                new Spacer(),
                new Button(theme.IsDark ? "明亮主题" : "暗色主题", () => themeState.Value = theme.IsDark ? Theme.Light : Theme.Dark))
            .Spacing(12);

    private static View Section(string title, View content)
        => new VStack(
                new Text(title).WithFont(FontDescription.Default.Bold().WithSize(16)),
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
