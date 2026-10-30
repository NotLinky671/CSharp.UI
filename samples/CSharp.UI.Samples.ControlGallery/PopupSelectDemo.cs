using CSharp.UI;

namespace CSharp.UI.Samples.ControlGallery;

/// <summary>
/// “弹出与选择”示例：普通/禁用 Select、限高长列表、滚动区域内锚定，以及带 TextField
/// 的通用 Popup。全部使用公开 API 与真实状态绑定，不包含测试钩子。
/// </summary>
public sealed class PopupSelectDemo : Component
{
    private const double FormBreakpoint = 560;

    private static readonly SelectItem<string?>[] ModeItems =
    [
        new("标准模式"),
        new("紧凑模式（窄屏）"),
        new("演示模式（长标签用于截断验证）"),
    ];

    private static readonly SelectItem<string?>[] DisabledItems =
    [
        new("仅展示（不可操作）"),
    ];

    private static readonly SelectItem<string?>[] LongItems = Enumerable.Range(1, 30)
        .Select(i => new SelectItem<string?>($"选项 {i:00} · 长列表项标签"))
        .ToArray();

    private static readonly SelectItem<string?>[] ScrollItems =
    [
        new("区域内选项一（长标签用于截断）"),
        new("区域内选项二"),
        new("区域内选项三"),
        new("区域内选项四"),
    ];

    private readonly State<Theme> _theme;

    public PopupSelectDemo(State<Theme> theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        _theme = theme;
    }

    protected override View Build()
    {
        var current = _theme.Value;
        var mode = UseState<string?>("popupSelectMode", "标准模式");
        var disabledValue = UseState<string?>("popupSelectDisabled", "仅展示（不可操作）");
        var longValue = UseState<string?>("popupSelectLong", null);
        var scrollValue = UseState<string?>("popupSelectScroll", null);
        var settingsOpen = UseState("popupSelectSettingsOpen", false);
        var settingsText = UseState("popupSelectSettingsText", string.Empty);

        var settingsContent = new VStack(
                new Text("设置内容").WithFont(FontDescription.Default.Bold()).Wrap(),
                new TextField(settingsText.ToBinding(), "输入设置内容").FillWidth(),
                new Button("关闭", () => settingsOpen.Value = false))
            .Spacing(8)
            .Padding(12)
            .Background(current.Surface)
            .Border(current.Border, 1, 8)
            .CornerRadius(8);

        return new VStack(
                FormRow("选择模式", new Select<string?>(
                        mode.ToBinding(),
                        ModeItems,
                        "请选择模式").FillWidth()),
                new Text($"当前选择：{mode.Value}").WithFont(FontDescription.Default.WithSize(12)).Wrap(),
                FormRow("禁用选择", new Select<string?>(
                        disabledValue.ToBinding(),
                        DisabledItems,
                        "不可操作").Disabled().FillWidth()),
                FormRow("长列表（30 项，限高）", new Select<string?>(
                        longValue.ToBinding(),
                        LongItems,
                        "请选择长列表项")
                    {
                        MaxPopupHeight = 120,
                    }.FillWidth()),
                new Text("滚动区域内锚定：滚动时弹层跟随，锚点移出可视区会关闭，靠近底部时自动向上翻转。")
                    .WithFont(FontDescription.Default.WithSize(12)).Wrap(),
                new ScrollView(
                        new VStack(
                                new Select<string?>(
                                    scrollValue.ToBinding(),
                                    ScrollItems,
                                    "滚动区域选择"),
                                new Spacer(400))
                            .Spacing(8),
                        ScrollAxis.Vertical)
                    .Frame(height: 140)
                    .FillWidth(),
                FormRow("设置弹层", new Popup(
                        new Button("打开设置", () => settingsOpen.Value = !settingsOpen.Value),
                        settingsContent,
                        settingsOpen.ToBinding())),
                new Text($"设置输入回显：{settingsText.Value}").WithFont(FontDescription.Default.WithSize(12)).Wrap())
            .Spacing(10);
    }

    private static View FormRow(string label, View control)
        => new AdaptiveStack(FormBreakpoint, new Text(label), control)
            .Spacing(8)
            .VerticalSpacing(6);
}
