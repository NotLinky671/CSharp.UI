using Xunit;

namespace CSharp.UI.TsfHostCheck.Tests;

public sealed class DpiUnitsTests
{
    [Theory]
    [InlineData(7, 96, 7)]
    [InlineData(7, 120, 9)]
    [InlineData(7, 144, 11)]
    [InlineData(7, 192, 14)]
    [InlineData(-7, 144, -11)]
    [InlineData(0, 144, 0)]
    public void LogicalDeltaScalesToPhysicalPixels(int logical, uint dpi, int expected)
    {
        Assert.Equal(expected, DpiUnits.LogicalToPhysical(logical, dpi));
    }

    [Fact]
    public void FontHeightUsesRealDpiWithoutHardcoded150Percent()
    {
        Assert.Equal(20, DpiUnits.FontPixelHeight(96));
        Assert.Equal(30, DpiUnits.FontPixelHeight(144));
        Assert.Equal(40, DpiUnits.FontPixelHeight(192));
    }
}
