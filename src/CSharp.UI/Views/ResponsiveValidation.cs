namespace CSharp.UI;

/// <summary>
/// 响应式布局容器（Flow、AdaptiveStack）共用的公开参数校验：
/// 间距必须有限且非负，断点必须有限且为正，枚举必须是已定义取值。
/// </summary>
/// <remarks>校验在声明时执行（构造器与 <c>init</c> 访问器），非法输入立即抛出明确异常，不进入布局阶段。</remarks>
internal static class ResponsiveValidation
{
    /// <summary>校验并返回有限非负值。</summary>
    public static double NonNegativeFinite(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value < 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "必须是有限的非负值（禁止 NaN、正/负无穷与负值）。");
        }

        return value;
    }

    /// <summary>校验并返回有限正值。</summary>
    public static double PositiveFinite(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "必须是有限的正值（禁止 NaN、正/负无穷与零/负值）。");
        }

        return value;
    }

    /// <summary>校验并返回已定义的枚举值。</summary>
    public static T DefinedEnum<T>(T value, string parameterName)
        where T : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "未知的枚举取值。");
        }

        return value;
    }
}
