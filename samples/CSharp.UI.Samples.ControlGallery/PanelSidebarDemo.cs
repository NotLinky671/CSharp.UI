using CSharp.UI;

namespace CSharp.UI.Samples.ControlGallery;

/// <summary>
/// “面板与导航”示例：<see cref="Sidebar{T}"/> 纵向导航 + <see cref="Panel"/> 装饰内容容器。
/// </summary>
/// <remarks>
/// <para>
/// 持久状态（当前导航值、内容输入文本）外持于本组件的 <c>State</c>：切换导航、宽窄布局
/// 或主题不会丢失输入内容。导航使用 <c>private static readonly</c> 不可变演示数据；
/// 页面色 / 边框全部按继承的 ambient <see cref="Theme"/> 取当前值，不传入颜色。
/// </para>
/// <para>
/// 布局：父级宽度超过断点时 sidebar 在左、Panel 主区占满剩余宽度；低于断点由
/// <see cref="AdaptiveStack"/> 切换为纵向（导航在上、内容在下），不做控件内置折叠。
/// 超长导航外裹调用方 <see cref="ScrollView"/>：键盘 End/方向键把 active 行带进视口，
/// 滚轮浏览不回弹。
/// </para>
/// </remarks>
public sealed class PanelSidebarDemo : Component
{
    /// <summary>sidebar 侧栏 + 内容横排 / 上下排列的父级宽度阈值。</summary>
    private const double LayoutBreakpoint = 640;

    /// <summary>主导航项（含 disabled 演示项），构造时快照传给 Sidebar。</summary>
    private static readonly SidebarItem<string?>[] MainItems =
    [
        new("home", "主页"),
        new("dashboard", "仪表盘"),
        new("projects", "项目"),
        new("reports", "报表"),
        new("settings", "设置"),
        new("billing", "账单与订阅"),
        new("security", "安全与权限（禁用演示）", Disabled: true),
        new("integrations", "集成与扩展（一个很长很长的标签用于演示单行省略）"),
        new("archive", "归档"),
        new("help", "帮助与支持"),
    ];

    /// <summary>长列表导航（30 项），外裹调用方 ScrollView 演示 ensure-visible。</summary>
    private static readonly SidebarItem<string?>[] LongNavItems = Enumerable.Range(1, 30)
        .Select(i => new SidebarItem<string?>($"nav-{i:00}", $"导航项 {i:00}"))
        .ToArray();

    private static readonly Dictionary<string, (string Title, string Body)> Pages = new()
    {
        ["home"] = ("主页", "这是主页内容。Panel 提供内边距、背景、边框与圆角，替代手工拼装 FrameView+Border+Background。"),
        ["dashboard"] = ("仪表盘", "仪表盘内容：可放置图表、统计卡片等。切换导航只改回显，外持 State 不丢。"),
        ["projects"] = ("项目", "项目列表区域。下面的 TextField 状态持有在外层 State，切到其它页再回来内容仍在。"),
        ["reports"] = ("报表", "报表内容区。窄宽时本 Panel 会随父容器收缩，内部 Wrap 文本自动换行。"),
        ["settings"] = ("设置", "设置页。可以在下方输入框编辑，验证状态持久。"),
        ["billing"] = ("账单与订阅", "账单内容。"),
        ["security"] = ("安全与权限", "安全设置（此项在导航中禁用，无法选中到达）。"),
        ["integrations"] = ("集成与扩展", "集成设置，长标签项。"),
        ["archive"] = ("归档", "归档内容。"),
        ["help"] = ("帮助与支持", "帮助内容。"),
    };

    protected override View Build()
    {
        // Panel/Sidebar 颜色全部按继承的 ambient Theme 取当前值，不自画主题色。
        var page = UseState<string?>("panelSidebarPage", "home");
        var notes = UseState("panelSidebarNotes", "在这里输入：切换导航或改变窗口宽度后内容仍然保留。");
        var longNav = UseState<string?>("panelSidebarLongNav", null);


        var (pageTitle, pageBody) = page.Value is { } v && Pages.TryGetValue(v, out var p)
            ? p
            : ("(未选择)", "左侧选择一个导航项。");

        // 右侧内容区：Panel 装饰容器，包含标题、说明与持久 TextField。
        var content = new Panel(
                new VStack(
                        new Text(pageTitle).WithFont(FontDescription.Default.Bold().WithSize(18)).Wrap(),
                        new Text(pageBody).Wrap(),
                        new TextField(notes.ToBinding(), "输入内容").FillWidth(),
                        new Text($"当前页：{page.Value ?? "(空)"} · 输入长度：{notes.Value.Length}")
                            .WithFont(FontDescription.Default.WithSize(12)).Wrap())
                    .Spacing(10))
            {
                CornerRadius = 8,
            };

        return new VStack(
                new Text("侧栏导航 + 内容面板：宽时左导航右内容，窄时上下排列；Panel 提供内边距/背景/边框/圆角。")
                    .WithFont(FontDescription.Default.WithSize(12)).Wrap(),
                new AdaptiveStack(
                        LayoutBreakpoint,
                        // 侧栏：自然偏好宽 200，父约束可收缩。
                        new Sidebar<string?>(page.ToBinding(), MainItems, "导航") { Width = 200 },
                        // 主区：占满剩余宽度。
                        content.FillWidth())
                    .Spacing(16)
                    .VerticalSpacing(12)
                    .Align(Alignment.Start),
                new Text("长列表导航（30 项，外裹调用方 ScrollView）：键盘 End/方向键把 active 行带进视口；滚轮浏览不回弹。")
                    .WithFont(FontDescription.Default.WithSize(12)).Wrap(),
                new ScrollView(
                        new Sidebar<string?>(longNav.ToBinding(), LongNavItems, "全部导航") { Width = 200 },
                        ScrollAxis.Vertical)
                    { ScrollbarStyle = ScrollbarStyle.Standard }
                    .Key("panel-sidebar-long-scroll")
                    .Frame(height: 140),
                new Text($"长列表当前：{longNav.Value ?? "(空)"}")
                    .WithFont(FontDescription.Default.WithSize(12)).Wrap())
            .Spacing(10);
    }
}
