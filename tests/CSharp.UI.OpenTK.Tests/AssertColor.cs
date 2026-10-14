using Xunit;

namespace CSharp.UI.OpenTK.Tests;

/// <summary>
/// 颜色断言辅助：离屏读回的像素经过预乘/反预乘与 GPU 舍入，逐步比较会产生 ±1—2 的误差。
/// </summary>
internal static class AssertColor
{
    /// <summary>默认允许的分量误差。</summary>
    internal const int DefaultTolerance = 2;

    /// <summary>断言两个颜色在容差内相等（含 alpha）。</summary>
    internal static void Equal(Color expected, Color actual, int tolerance = DefaultTolerance, string? because = null)
    {
        var detail = $"期望 {expected}，实际 {actual}"
            + (because is null ? string.Empty : $"（{because}）");
        Assert.True(
            Math.Abs(expected.R - actual.R) <= tolerance
            && Math.Abs(expected.G - actual.G) <= tolerance
            && Math.Abs(expected.B - actual.B) <= tolerance
            && Math.Abs(expected.A - actual.A) <= tolerance,
            detail);
    }

    /// <summary>断言颜色分量不小于给定下限（用于验证“没有色晕/变暗”）。</summary>
    internal static void AtLeast(Color actual, int r, int g, int b, string? because = null)
    {
        var detail = $"实际 {actual} 应至少为 ({r},{g},{b})"
            + (because is null ? string.Empty : $"（{because}）");
        Assert.True(actual.R >= r && actual.G >= g && actual.B >= b, detail);
    }
}
