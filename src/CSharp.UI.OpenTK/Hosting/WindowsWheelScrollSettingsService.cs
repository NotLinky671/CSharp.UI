using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace CSharp.UI.OpenTK;

/// <summary>
/// Windows 宿主滚轮设置：只读查询系统的每格行数（<c>SPI_GETWHEELSCROLLLINES</c>）与字符数
/// （<c>SPI_GETWHEELSCROLLCHARS</c>），不修改任何系统配置。
/// </summary>
/// <remarks>
/// <para>
/// 取值语义：0 表示该方向禁用滚轮，<see cref="uint.MaxValue"/> 表示每格滚动一页，其余为每格行数/字符数。
/// 查询失败或不在 Windows 上运行时回退到 <see cref="WheelScrollSettings.Default"/>（每格 3 行/3 字符）。
/// </para>
/// <para>核心只看到 <see cref="IWheelScrollSettingsService"/>，Windows 类型仅存在于本程序集。</para>
/// </remarks>
internal sealed class WindowsWheelScrollSettingsService : IWheelScrollSettingsService
{
    /// <summary><c>SPI_GETWHEELSCROLLLINES</c>：每格滚动的行数。</summary>
    internal const uint SpiGetWheelScrollLines = 0x0068;

    /// <summary><c>SPI_GETWHEELSCROLLCHARS</c>：每格滚动的字符数。</summary>
    internal const uint SpiGetWheelScrollChars = 0x006C;

    /// <summary>共享实例；<see cref="Settings"/> 每次访问都读取当前系统设置。</summary>
    public static WindowsWheelScrollSettingsService Instance { get; } = new();

    /// <inheritdoc />
    public WheelScrollSettings Settings => Read();

    /// <summary>读取当前系统滚轮设置；非 Windows 或单项查询失败时按默认值回退。</summary>
    internal static WheelScrollSettings Read()
    {
        if (!OperatingSystem.IsWindows())
        {
            return WheelScrollSettings.Default;
        }

        var lines = TryQuery(SpiGetWheelScrollLines, out var lineCount)
            ? lineCount
            : WheelScrollSettings.DefaultNotches;
        var chars = TryQuery(SpiGetWheelScrollChars, out var charCount)
            ? charCount
            : WheelScrollSettings.DefaultNotches;
        return new WheelScrollSettings(lines, chars);
    }

    /// <summary>只读查询一项系统滚轮设置；失败时返回 false（调用方使用默认值）。</summary>
    internal static bool TryQuery(uint action, out uint value)
    {
        value = 0;
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        return SystemParametersInfo(action, 0, out value, 0);
    }

    [SupportedOSPlatform("windows")]
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, out uint pvParam, uint fWinIni);
}
