using CSharp.UI;
using CSharp.UI.Markdown;

namespace CSharp.UI.Samples.ControlGallery;

/// <summary>
/// Markdown 文档展示组件：标题/嵌套强调/列表/引用/分隔线/围栏代码/链接回调/本地图/表格横滚。
/// 链接回调用页内状态回显 URL（不打开外部浏览器）；remote 图默认 alt 降级。
/// 供 Gallery 主页 Section 与独立 harness 共同复用。
/// </summary>
public sealed class MarkdownDemo : Component
{
    private readonly string _logoPath;
    private readonly MarkdownDocument _document;

    public MarkdownDemo(string logoPath)
    {
        _logoPath = logoPath;
        _document = global::CSharp.UI.Markdown.Markdown.Parse(BuildMarkdownDocument());
    }

    protected override View Build()
    {
        var activatedUrl = UseState("mdLink", "(尚未点击链接)");

        var view = new MarkdownBlockView
        {
            Document = _document,
            ImagePolicy = new MarkdownImagePolicy
            {
                Resolve = src => File.Exists(src) ? src : null,
                AllowRemote = false, // remote 默认 alt 降级
            },
            OnLinkActivate = payload =>
            {
                if (payload is MarkdownLinkPayload link)
                {
                    activatedUrl.Value = link.Url;
                }
            },
            // LinkHoverStyle 保留默认 paint 覆盖；不写 hover state（重建会自抹显示）。
        };

        return new VStack(
            new Text("Markdown 演示").WithFont(FontDescription.Default.Bold().WithSize(16)).Wrap(),
            new Text("链接激活回显").WithFont(FontDescription.Default.WithSize(12)),
            new Text($"点击 URL：{activatedUrl.Value}").WithFont(FontDescription.Default.WithSize(12)),
            new ScrollView(view, ScrollAxis.Vertical)
                .Frame(height: 360, alignment: Alignment.Stretch)
                .FillWidth())
            .Spacing(8);
    }

    private string BuildMarkdownDocument()
    {
        var nl = Environment.NewLine;
        var sb = new System.Text.StringBuilder();

        sb.Append("# Markdown 展示").Append(nl).Append(nl);
        sb.Append("## 嵌套强调").Append(nl).Append(nl);
        sb.Append("**粗体 + *嵌套斜体* + `行内代码`** 与 ~~删除线~~ 混排。").Append(nl).Append(nl);

        sb.Append("## 列表").Append(nl).Append(nl);
        sb.Append("- 无序第一项").Append(nl);
        sb.Append("- 无序第二项（**含粗体**）").Append(nl);
        sb.Append("  - 嵌套子项").Append(nl).Append(nl);
        sb.Append("1. 有序第一项").Append(nl);
        sb.Append("2. 有序第二项").Append(nl).Append(nl);

        sb.Append("## 引用").Append(nl).Append(nl);
        sb.Append("> 引用一段文字——边条随主题换色，内容正常排版。").Append(nl).Append(nl);

        sb.Append("## 分隔线").Append(nl).Append(nl);
        sb.Append("---").Append(nl).Append(nl);

        sb.Append("## 围栏代码").Append(nl).Append(nl);
        sb.Append("```csharp").Append(nl);
        sb.Append("var x = new StyledText(\"code\", ranges, interactions);").Append(nl);
        sb.Append("```").Append(nl).Append(nl);

        sb.Append("## 链接").Append(nl).Append(nl);
        sb.Append("[CSharp.UI GitHub](https://github.com/NotLinky671/CSharp.UI) 点击后回显到上方状态；")
          .Append("[本地锚点](#section) 也走同一回调。").Append(nl).Append(nl);

        sb.Append("## 本地图片").Append(nl).Append(nl);
        sb.Append($"![logo]({_logoPath.Replace('\\', '/')})").Append(nl).Append(nl);
        sb.Append("## 远端图片（默认 alt 降级）").Append(nl).Append(nl);
        sb.Append("![remote](https://example.com/logo.png)").Append(nl).Append(nl);

        sb.Append("## 表格").Append(nl).Append(nl);
        sb.Append("| 名称 | 类型 | 说明 |").Append(nl);
        sb.Append("|---|---|---|").Append(nl);
        sb.Append("| StyledText | 模型 | 单根文本 + 样式/交互两表 |").Append(nl);
        sb.Append("| IRichTextService | 服务 | 排版/栅格化/命中分离 |").Append(nl);
        // 不可断拉丁长标识符：Σmin 超 320 窄宽 → 横滚；Σpref 在 1024 内自然展开。
        sb.Append("| MarkdownTableUnbreakableIdentifierForHorizontalScrollingAtNarrowWidths | 长 token | 不可断拉丁标识符撑大 min-content |").Append(nl);

        return sb.ToString();
    }
}
