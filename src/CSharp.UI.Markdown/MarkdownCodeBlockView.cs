using CSharp.UI;

namespace CSharp.UI.Markdown;

/// <summary>
/// 围栏/缩进代码块的宿主视图（internal）：给整个代码块画圆角背景，并在
/// <see cref="MarkdownStyleOptions.ShowCodeCopyButton"/> 开启时提供右上"Copy"工具行。
/// </summary>
/// <remarks>
/// <para>
/// 结构：单子节点 = <c>VStack(header?, scroll)</c>——header 为 <c>HStack(Spacer, Button)</c>
/// （Spacer 弹性吸收左侧空间把按钮推到最右），scroll 为代码内容横滚
/// <see cref="ScrollView"/>。header 是文档坐标系内节点，**不随代码横滚**，
/// 320 窄宽/长代码下按钮保持可见可点。
/// </para>
/// <para>
/// <see cref="Emit"/> 先画 <c>RectangleCommand(frame, bg, radius)</c> 再发子项——
/// 圆角底包整个块（含 header 行），代码横滚时底不动。
/// <see cref="MarkdownStyleOptions.CodeBackground"/> 为 null 遵现有语义：无底色矩形，
/// 按钮独立照常显示。
/// </para>
/// <para>
/// 复制复用普通 <see cref="Button"/>，经 internal <c>Button.OnClickContext</c>
/// （<c>Action&lt;InputContext&gt;</c>，与 <c>OnClick</c> 同一激活点派发）——
/// 激活时 <c>context.Runtime.Services.Clipboard?.SetText(text)</c>，事件回调内
/// 即取 runtime 服务，无跨节点绑定，同描述多 runtime 不互串。剪贴板服务为 null
/// 按框架既有契约 no-op，不伪造成功。捕获文本是本描述快照——文档变化重建时
/// 新描述带新文本。
/// </para>
/// </remarks>
internal sealed class MarkdownCodeBlockView : View
{
    private readonly View _inner;
    private readonly View[] _children;
    private readonly Color? _background;
    private readonly double _cornerRadius;
    private readonly string _text;
    private readonly bool _showButton;
    private readonly string _label;

    /// <param name="scroll">已装配的代码横滚 <see cref="ScrollView"/>。</param>
    /// <param name="text">原始代码文本（<see cref="Markdig.Syntax.CodeBlock.Lines"/> 输出，
    /// 复制时原样写入剪贴板，不经渲染回抽、不含 fence 语言标识）。</param>
    /// <param name="style">样式选项。</param>
    public MarkdownCodeBlockView(View scroll, string text, MarkdownStyleOptions style)
    {
        _text = text;
        _background = style.CodeBackground;
        _cornerRadius = Math.Max(0, style.CodeCornerRadius);
        _showButton = style.ShowCodeCopyButton;
        _label = style.CodeCopyLabel;

        if (_showButton)
        {
            var copy = new Button(_label)
            {
                HorizontalPadding = 10,
                VerticalPadding = 3,
                MinHeight = 0,
                OnClickContext = ctx => ctx.Runtime.Services.Clipboard?.SetText(_text),
            };
            // 极窄视口（如 80px）或本地化长标签时，工具行必须让按钮可收缩而不是越出代码块矩形：
            // 自身不增长、可压缩到 0 最小宽度，并对绘制做裁剪（文字随 Frame 被截断，按钮仍可点复制）。
            var header = new HStack(new Spacer(), copy.Flex(grow: 0, shrink: 1, min: 0).Clip())
                .Padding(left: 0, top: 4, right: 6, bottom: 4);
            _inner = new VStack(header, scroll);
        }
        else
        {
            _inner = scroll;
        }

        _children = [_inner];
    }

    internal override IReadOnlyList<View> Children => _children;

    internal override void Measure(ViewNode node, LayoutConstraints constraints, LayoutContext context)
    {
        var child = node.ChildList[0];
        context.Measure(child, constraints);
        node.DesiredSize = child.DesiredSize;
    }

    internal override void Arrange(ViewNode node, Rect bounds, LayoutContext context)
    {
        node.Frame = bounds;
        // 单 child 充满块矩形（背景与工具行同宽）。
        context.Arrange(node.ChildList[0], bounds);
    }

    internal override void Emit(ViewNode node, DrawContext context)
    {
        if (!context.IsVisible(node) || node.Frame.IsEmpty)
        {
            return;
        }

        if (_background is { } bg)
        {
            context.Commands.Add(new RectangleCommand(
                node.Frame,
                bg,
                CornerRadius: _cornerRadius,
                Stroke: null,
                StrokeWidth: 0,
                Opacity: node.Environment.Opacity));
        }

        context.EmitChild(node.ChildList[0]);
    }

    internal override bool LayoutPropertiesEquivalent(View other)
        => other is MarkdownCodeBlockView v
           && v._text == _text
           && v._background == _background
           && v._cornerRadius.Equals(_cornerRadius)
           && v._showButton == _showButton
           && v._label == _label;
}
