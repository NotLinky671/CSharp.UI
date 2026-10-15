namespace CSharp.UI;

/// <summary>滚轮增量的单位；见 <see cref="WheelEvent.Mode"/>。默认是归一化步数，不根据增量大小猜测。</summary>
public enum WheelDeltaMode
{
    /// <summary>归一化步数：一格为 1（触摸板等设备可以是小数）。滚轮视图按系统滚动设置换算成逻辑像素。</summary>
    Steps,

    /// <summary>逻辑像素距离：增量已经是逻辑像素，直接使用，不再按行/字符换算。</summary>
    Pixels,
}

/// <summary>
/// 系统滚轮步长设置（平台无关）：每格滚动的文本行数与字符数，以及步数到逻辑距离的换算。
/// </summary>
/// <remarks>
/// <para>
/// 取值语义与 Windows <c>SPI_GETWHEELSCROLLLINES</c> / <c>SPI_GETWHEELSCROLLCHARS</c> 一致：
/// <see cref="Disabled"/>（0）表示该方向禁用滚轮，<see cref="PageScroll"/>（<see cref="uint.MaxValue"/>）表示每格滚动一页；
/// 其余值为每格的行数/字符数。非 Windows 或查询失败时由宿主使用 <see cref="Default"/>（每格 3 行/3 字符）。
/// </para>
/// <para>
/// 换算只使用逻辑像素，与 DPI 无关：每行 <see cref="LogicalPixelsPerLine"/>、每字符
/// <see cref="LogicalPixelsPerCharacter"/>，单个滚轮步的距离按视口大小封顶。
/// </para>
/// </remarks>
public readonly record struct WheelScrollSettings(uint LinesPerNotch, uint CharsPerNotch)
{
    /// <summary>系统设置值：该方向禁用滚轮。</summary>
    public const uint Disabled = 0;

    /// <summary>系统设置值：每格滚动一页（视口大小）。</summary>
    public const uint PageScroll = uint.MaxValue;

    /// <summary>查询失败或平台不支持时的默认每格行数/字符数。</summary>
    public const uint DefaultNotches = 3;

    /// <summary>每个文本行对应的逻辑像素。</summary>
    public const double LogicalPixelsPerLine = 16;

    /// <summary>每个字符对应的逻辑像素。</summary>
    public const double LogicalPixelsPerCharacter = 8;

    /// <summary>默认设置：每格 3 行、3 字符（非 Windows 或查询失败的回退值）。</summary>
    public static WheelScrollSettings Default => new(DefaultNotches, DefaultNotches);

    /// <summary>指定方向是否禁用滚轮。</summary>
    public bool IsDisabled(bool horizontal) => (horizontal ? CharsPerNotch : LinesPerNotch) == Disabled;

    /// <summary>
    /// 一个滚轮步在指定方向上的逻辑像素距离：按行/字符换算，并按视口大小封顶。
    /// 返回 0 表示该方向被系统设置禁用或视口尺寸无效。
    /// </summary>
    /// <param name="horizontal">true 表示水平滚动（用字符数与 <see cref="LogicalPixelsPerCharacter"/>）。</param>
    /// <param name="viewportExtent">该方向的视口大小（逻辑像素），也作为整页滚动的距离。</param>
    public double StepDistance(bool horizontal, double viewportExtent)
    {
        var extent = double.IsFinite(viewportExtent) && viewportExtent > 0 ? viewportExtent : 0;
        var notches = horizontal ? CharsPerNotch : LinesPerNotch;
        if (notches == Disabled)
        {
            return 0;
        }

        if (notches == PageScroll)
        {
            return extent;
        }

        var pixelsPerNotch = horizontal ? LogicalPixelsPerCharacter : LogicalPixelsPerLine;
        return Math.Min(notches * pixelsPerNotch, extent);
    }
}

/// <summary>
/// 系统滚轮设置服务契约：核心只依赖本接口，不引用任何平台或窗口类型。
/// </summary>
/// <remarks>
/// 宿主实现（例如 Windows 宿主只读查询 <c>SystemParametersInfo</c>）在每次访问时返回当前设置；
/// 非 Windows 或查询失败时返回 <see cref="WheelScrollSettings.Default"/>。
/// </remarks>
public interface IWheelScrollSettingsService
{
    /// <summary>当前系统滚轮设置。</summary>
    WheelScrollSettings Settings { get; }
}

/// <summary>默认实现：始终返回 <see cref="WheelScrollSettings.Default"/>（每格 3 行/3 字符）。</summary>
public sealed class DefaultWheelScrollSettingsService : IWheelScrollSettingsService
{
    /// <summary>共享实例。</summary>
    public static DefaultWheelScrollSettingsService Instance { get; } = new();

    /// <inheritdoc />
    public WheelScrollSettings Settings => WheelScrollSettings.Default;
}
