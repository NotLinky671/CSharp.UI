using CSharp.UI;

namespace CSharp.UI.Markdown;

/// <summary>
/// 主题感知装饰矩形：<see cref="MarkdownStyleOptions.QuoteBarColor"/>/<see cref="MarkdownStyleOptions.ThematicBreakColor"/>
/// 的 null 槽位在 Emit 时解 <see cref="EnvironmentValues.ResolvedTheme"/>.<see cref="Theme.Border"/>；
/// 非 null 直接用显式值。仅 internal——不新增公开框架 API。
/// </summary>
internal sealed class ThemedBar : View
{
    /// <summary>显式色；null → Emit 时解 <see cref="Theme.Border"/>。</summary>
    public Color? Fill { get; init; }

    /// <summary>显式尺寸（宽或高，由调用方 .Width/.Height 包 FrameView）。</summary>
    public Size? FixedSize { get; init; }

    internal override void Measure(ViewNode node, LayoutConstraints constraints, LayoutContext context)
    {
        var desired = FixedSize ?? new Size(
            constraints.IsWidthTight ? constraints.MaxWidth : Math.Max(0, constraints.MinWidth),
            constraints.IsHeightTight ? constraints.MaxHeight : Math.Max(0, constraints.MinHeight));
        node.DesiredSize = desired;
    }

    internal override void Emit(ViewNode node, DrawContext context)
    {
        if (!context.IsVisible(node) || node.Frame.IsEmpty)
        {
            return;
        }

        var fill = Fill ?? node.Environment.ResolvedTheme.Border;
        context.Commands.Add(new RectangleCommand(
            node.Frame,
            fill,
            CornerRadius: 0,
            Stroke: null,
            StrokeWidth: 0,
            Opacity: node.Environment.Opacity));
    }
}
