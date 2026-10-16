using CSharp.UI;

namespace CSharp.UI.Samples.ControlGallery;

/// <summary>
/// 可独立实例化的嵌套响应式示例：在同一个窗口中并排展示“宽父容器”和“窄父容器”，
/// 两者按各自父级分配到的逻辑宽度选择不同的 <see cref="Responsive"/> 内容分支；
/// 每个面板内的 <see cref="AdaptiveStack"/> 只切换横/纵排列，保持同一批子节点。
/// </summary>
/// <remarks>
/// <para>
/// 示例刻意不读取窗口或全局尺寸：面板宽度来自父容器（<see cref="Flow"/> 的当前行宽）分配，
/// 因此同一窗口里不同父宽度会同时选择不同布局。
/// </para>
/// <para>
/// 持久数据（输入文本与计数）放在外层 <c>State</c>/<c>Binding</c>：<see cref="Responsive"/> 的任意结构分支切换
/// 会按协调规则卸载旧分支，框架不承诺跨父级迁移焦点或本地编辑状态；
/// 需要保持焦点与编辑状态时应使用 <see cref="AdaptiveStack"/>（本示例中的“稳定输入”）。
/// </para>
/// </remarks>
public sealed class NestedResponsiveExample : Component
{
    /// <summary>面板内 <see cref="AdaptiveStack"/> 的横/纵切换阈值（父级可用逻辑宽度）。</summary>
    private const double AdaptiveBreakpoint = 560;

    /// <summary>面板内 <see cref="Responsive"/> 选择内容结构的宽度阈值。</summary>
    private const double BranchBreakpoint = 480;

    /// <summary>宽面板的宽度上限。</summary>
    private const double WidePanelMaxWidth = 640;

    /// <summary>窄面板的宽度上限。</summary>
    private const double NarrowPanelMaxWidth = 320;

    protected override View Build()
    {
        var name = UseState("name", "中文 English 混排");
        var clicks = UseState("clicks", 0);

        return new VStack(
                new Text("嵌套响应式容器").WithFont(FontDescription.Default.Bold().WithSize(18)).Wrap(),
                new Text("同一个窗口里两个父容器宽度不同，因此各自选择不同的内容分支；持久数据放在外层绑定，切换分支只改变结构，不迁移输入焦点。")
                    .Wrap(),
                new Flow(
                        Panel("宽容器", WidePanelMaxWidth, name, clicks),
                        Panel("窄容器", NarrowPanelMaxWidth, name, clicks))
                    .Spacing(12)
                    .LineSpacing(12),
                new Text($"外层状态：输入值“{name.Value}”，按钮点击 {clicks.Value} 次。").Wrap())
            .Spacing(12)
            .Padding(20);
    }

    private static View Panel(string title, double maxWidth, State<string> name, State<int> clicks)
    {
        // 持久数据放在外层：两个面板共享同一个 State，任意分支切换都不会丢失输入内容。
        var binding = name.ToBinding();

        return new FrameView(
            new VStack(
                    new Text(title).WithFont(FontDescription.Default.Bold().WithSize(14)).Wrap(),
                    // 任意内容分支：按父容器最终分配到的逻辑宽度选择结构，分支间不迁移焦点。
                    new Responsive(info => info.IsWidthBounded && info.AvailableWidth < BranchBreakpoint
                        ? new VStack(
                            new Text("窄分支：标签与输入框上下排列").Wrap(),
                            new TextField(binding, "窄分支输入"),
                            new Button($"窄分支计数：{clicks.Value}", () => clicks.Value++))
                            .Spacing(6)
                        : new HStack(
                            new Text("宽分支：标签与输入框并排"),
                            new TextField(binding, "宽分支输入"),
                            new Button($"宽分支计数：{clicks.Value}", () => clicks.Value++))
                            .Spacing(8)),
                    // 稳定身份：同一批子节点只切换横/纵排列，输入框节点、焦点、选区与滚动位置保持。
                    new AdaptiveStack(
                            AdaptiveBreakpoint,
                            new Text("稳定标签"),
                            new TextField(binding, "稳定输入（切换布局后继续编辑）").FillWidth(),
                            new ScrollView(
                                    new VStack(
                                        new ForEach<int>(Enumerable.Range(1, 12), i => new Text($"滚动行 {i}")))
                                        .Spacing(4))
                                .Frame(width: 180, height: 64),
                            new Button("稳定按钮", () => clicks.Value++))
                        .Spacing(8)
                        .VerticalSpacing(6))
                .Spacing(8)
                .Padding(12)
                .Border(Color.Parse("#88888844"), 1, 8),
            maxWidth: maxWidth,
            alignment: Alignment.Start);
    }
}
