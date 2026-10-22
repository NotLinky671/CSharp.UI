namespace CSharp.UI.TsfHostCheck;

/// <summary>
/// 逻辑单位（96 DPI 基准）与真实物理像素的换算。滚动参数按逻辑单位解释，
/// 布局/截图/GetTextExt 始终使用真实 screen px；不硬编码 150%/144。
/// </summary>
internal static class DpiUnits
{
    internal const uint DefaultDpi = 96;

    internal static int LogicalToPhysical(int logical, uint dpi)
    {
        if (logical == 0)
        {
            return 0;
        }

        var effective = dpi == 0 ? DefaultDpi : dpi;
        // MulDiv 语义的四舍五入；避免 double 与银行家舍入带来的 10.5→10。
        var scaled = (long)logical * effective;
        return (int)(scaled >= 0
            ? (scaled + DefaultDpi / 2) / DefaultDpi
            : (scaled - DefaultDpi / 2) / DefaultDpi);
    }

    internal static int FontPixelHeight(uint dpi) => Math.Max(14, LogicalToPhysical(20, dpi));

    /// <summary>
    /// R36：候选 overlay 的独立字号——比输入行（20 逻辑 px）紧凑，14 逻辑 px；
    /// DPI 144 时为 21 物理 px。9 条候选 + 页脚在该字号下才能放进 760x420 客户区。
    /// 绝不与输入行共用字号：输入行字号由文本排版决定，候选只是展示。
    /// </summary>
    internal const int CandidateFontLogicalPx = 14;

    internal static int CandidateFontPixelHeight(uint dpi)
        => Math.Max(10, LogicalToPhysical(CandidateFontLogicalPx, dpi));
}
