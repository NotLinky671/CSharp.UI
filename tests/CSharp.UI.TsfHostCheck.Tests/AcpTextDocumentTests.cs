using Xunit;

namespace CSharp.UI.TsfHostCheck.Tests;

public sealed class AcpTextDocumentTests
{
    [Fact]
    public void SurrogatePairCountsAsTwoAcpUnits()
    {
        var doc = new AcpTextDocument();
        doc.Reset("😀");

        Assert.Equal(2, doc.Length);
        Assert.Equal('\uD83D', doc.GetCodeUnit(0));
        Assert.Equal('\uDE00', doc.GetCodeUnit(1));
        Assert.Equal("\uD83D", doc.GetText(0, 1));
        Assert.Equal("\uDE00", doc.GetText(1, 2));
        Assert.Equal("😀", doc.GetText(0, 2));
    }

    [Fact]
    public void ReplaceAcrossSurrogatePairKeepsUnitsExact()
    {
        var doc = new AcpTextDocument();
        doc.Reset("😀");

        Assert.True(doc.TryReplace(0, 2, "x", AcpEditOrigin.App, out var change));
        Assert.Equal("x", doc.Text);
        Assert.Equal(1, change.NewEnd);
    }

    [Fact]
    public void InsertInsideSurrogatePairIsAllowedAndNotNormalized()
    {
        var doc = new AcpTextDocument();
        doc.Reset("😀");

        Assert.True(doc.TryReplace(1, 1, "a", AcpEditOrigin.Tsf, out _));
        Assert.Equal(3, doc.Length);
        Assert.Equal("\uD83Da\uDE00", doc.Text);
        Assert.Equal("\uD83D", doc.GetText(0, 1));
        Assert.Equal("a", doc.GetText(1, 2));
        Assert.Equal("\uDE00", doc.GetText(2, 3));
    }

    [Fact]
    public void VersionAdvancesOnEverySuccessfulWrite()
    {
        var doc = new AcpTextDocument();
        doc.Reset("ab");
        var start = doc.Version;

        doc.TryReplace(1, 1, "X", AcpEditOrigin.App, out _);
        Assert.Equal(start + 1, doc.Version);
        doc.TryReplace(0, 1, "", AcpEditOrigin.App, out _);
        Assert.Equal(start + 2, doc.Version);
    }

    [Fact]
    public void InvalidRangesAreRejectedWithoutMutation()
    {
        var doc = new AcpTextDocument();
        doc.Reset("abc");

        Assert.False(doc.TryReplace(-1, 0, "x", AcpEditOrigin.App, out _));
        Assert.False(doc.TryReplace(2, 1, "x", AcpEditOrigin.App, out _));
        Assert.False(doc.TryReplace(0, 4, "x", AcpEditOrigin.App, out _));
        Assert.Equal("abc", doc.Text);
    }

    [Fact]
    public void SelectionIsMappedAcrossReplace()
    {
        var doc = new AcpTextDocument();
        doc.Reset("abcdef");
        Assert.True(doc.TrySetSelection(2, 4));

        doc.TryReplace(0, 6, "xy", AcpEditOrigin.App, out _);
        Assert.Equal((2, 2), doc.Selection);

        doc.Reset("abcdef");
        Assert.True(doc.TrySetSelection(4, 5));
        doc.TryReplace(0, 2, "ZZZ", AcpEditOrigin.App, out _);
        Assert.Equal((5, 6), doc.Selection);
    }

    [Fact]
    public void SelectionEventOnlyFiresOnActualChange()
    {
        var doc = new AcpTextDocument();
        doc.Reset("ab");
        var count = 0;
        doc.SelectionChanged += () => count++;

        Assert.True(doc.TrySetSelection(1, 1));
        Assert.True(doc.TrySetSelection(1, 1));
        Assert.Equal(1, count);
        Assert.False(doc.TrySetSelection(2, 1));
    }

    [Fact]
    public void GetTextRejectsOutOfRange()
    {
        var doc = new AcpTextDocument();
        doc.Reset("a");
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.GetText(0, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.GetCodeUnit(1));
    }
}
