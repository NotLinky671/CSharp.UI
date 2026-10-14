namespace CSharp.UI;

/// <summary>
/// 明暗主题调色板。首版手动切换：应用在根视图通过 <c>.Theme(...)</c> 或
/// <see cref="UiApplicationOptions.Theme"/> 选择，不自动同步系统主题。
/// </summary>
public sealed record Theme
{
    /// <summary>窗口背景。</summary>
    public required Color Background { get; init; }

    /// <summary>卡片/面板背景。</summary>
    public required Color Surface { get; init; }

    /// <summary>正文前景色。</summary>
    public required Color Foreground { get; init; }

    /// <summary>次要文本前景色。</summary>
    public required Color MutedForeground { get; init; }

    /// <summary>强调色（主按钮、选中态）。</summary>
    public required Color Accent { get; init; }

    /// <summary>强调色上的前景色。</summary>
    public required Color AccentForeground { get; init; }

    /// <summary>分隔线与边框。</summary>
    public required Color Border { get; init; }

    /// <summary>输入类控件（TextField、Slider 轨道）背景。</summary>
    public required Color ControlBackground { get; init; }

    /// <summary>控件悬停背景。</summary>
    public required Color ControlHoverBackground { get; init; }

    /// <summary>控件按下背景。</summary>
    public required Color ControlPressedBackground { get; init; }

    /// <summary>禁用状态前景色。</summary>
    public required Color DisabledForeground { get; init; }

    /// <summary>禁用状态背景。</summary>
    public required Color DisabledBackground { get; init; }

    /// <summary>文本选择背景。</summary>
    public required Color SelectionBackground { get; init; }

    /// <summary>危险/删除操作颜色。</summary>
    public required Color Danger { get; init; }

    /// <summary>焦点描边颜色。</summary>
    public required Color FocusRing { get; init; }

    /// <summary>该主题是否偏暗，用于选择文本抗锯齿策略与默认字体颜色。</summary>
    public required bool IsDark { get; init; }

    /// <summary>明亮主题。</summary>
    public static Theme Light { get; } = new()
    {
        Background = Color.Parse("#F5F6F8"),
        Surface = Color.Parse("#FFFFFF"),
        Foreground = Color.Parse("#1B1D21"),
        MutedForeground = Color.Parse("#6B7280"),
        Accent = Color.Parse("#2563EB"),
        AccentForeground = Color.Parse("#FFFFFF"),
        Border = Color.Parse("#D6DAE1"),
        ControlBackground = Color.Parse("#FFFFFF"),
        ControlHoverBackground = Color.Parse("#EDF1F7"),
        ControlPressedBackground = Color.Parse("#DCE3ED"),
        DisabledForeground = Color.Parse("#A3A9B5"),
        DisabledBackground = Color.Parse("#EDEFF3"),
        SelectionBackground = Color.Parse("#BBD3FB"),
        Danger = Color.Parse("#DC2626"),
        FocusRing = Color.Parse("#2563EB"),
        IsDark = false,
    };

    /// <summary>暗色主题。</summary>
    public static Theme Dark { get; } = new()
    {
        Background = Color.Parse("#15171C"),
        Surface = Color.Parse("#1D2026"),
        Foreground = Color.Parse("#E8EAED"),
        MutedForeground = Color.Parse("#9AA1AD"),
        Accent = Color.Parse("#4C8DFF"),
        AccentForeground = Color.Parse("#0B1220"),
        Border = Color.Parse("#333842"),
        ControlBackground = Color.Parse("#24272F"),
        ControlHoverBackground = Color.Parse("#2C313A"),
        ControlPressedBackground = Color.Parse("#3A404B"),
        DisabledForeground = Color.Parse("#5C626D"),
        DisabledBackground = Color.Parse("#22252B"),
        SelectionBackground = Color.Parse("#2F4B7C"),
        Danger = Color.Parse("#F87171"),
        FocusRing = Color.Parse("#4C8DFF"),
        IsDark = true,
    };
}

/// <summary>
/// 沿视图树向下继承的环境值：字体、前景色、主题、圆角、透明度与禁用状态。
/// </summary>
/// <remarks>
/// 继承规则：子级显式修饰（例如对某个子树调用 <c>.Foreground(...)</c>）覆盖父级环境；
/// 父级默认值不会覆盖子级已显式设置的属性。环境对象不可变，修饰符返回新对象。
/// </remarks>
public sealed record EnvironmentValues
{
    /// <summary>默认环境：明亮主题，无显式字体与前景色（使用主题与平台默认字体）。</summary>
    public static readonly EnvironmentValues Default = new();

    /// <summary>继承的字体；null 表示由文本服务按平台默认解析。</summary>
    public FontDescription? Font { get; init; }

    /// <summary>继承的前景色；null 表示使用主题前景色。</summary>
    public Color? Foreground { get; init; }

    /// <summary>继承的主题；null 表示使用运行时默认主题。</summary>
    public Theme? Theme { get; init; }

    /// <summary>继承的圆角半径，供 Background / Border / Rectangle 使用。</summary>
    public double CornerRadius { get; init; }

    /// <summary>继承的透明度倍数（0—1，逐层相乘）。</summary>
    public double Opacity { get; init; } = 1.0;

    /// <summary>祖先是否处于 Disabled 状态；Disabled 会阻止命中、焦点与键盘处理。</summary>
    public bool Disabled { get; init; }

    /// <summary>解析后的主题（未显式设置时使用运行时默认）。</summary>
    public Theme ResolvedTheme => Theme ?? RuntimeDefaultTheme;

    /// <summary>解析后的前景色。</summary>
    public Color ResolvedForeground => Foreground ?? ResolvedTheme.Foreground;

    /// <summary>运行时默认主题；由宿主或测试在启动前设置。</summary>
    public static Theme RuntimeDefaultTheme { get; set; } = Theme.Light;
}
