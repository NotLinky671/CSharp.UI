using CSharp.UI;
using Xunit;

namespace CSharp.UI.Tests;

/// <summary>
/// TI-02：编辑与文本输入契约。范围/选区/快照/操作的构造期规则必须显式、可诊断，不做静默纠正。
/// </summary>
public class TextEditingContractTests
{
    [Fact]
    public void TextRange_NegativeOffsetsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TextRange(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TextRange(0, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => TextRange.FromLength(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => TextRange.FromLength(0, -1));
    }

    [Fact]
    public void TextRange_ReversedEndpointsAreRejected_NotSilentlySwapped()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => new TextRange(5, 2));
        Assert.Contains("终点不能小于起点", exception.Message);
    }

    [Fact]
    public void TextRange_EmptyRangeIsValid()
    {
        var range = new TextRange(3, 3);
        Assert.True(range.IsEmpty);
        Assert.Equal(0, range.Length);
        Assert.Equal(TextRange.FromLength(3, 0), range);
        Assert.Equal(3, range.Start);
        Assert.Equal(3, range.End);
    }

    [Fact]
    public void TextRange_FromLengthAndMembership()
    {
        var range = TextRange.FromLength(2, 3);
        Assert.Equal(2, range.Start);
        Assert.Equal(5, range.End);
        Assert.Equal(3, range.Length);
        Assert.False(range.IsEmpty);

        Assert.True(range.Contains(2));
        Assert.True(range.Contains(4));
        Assert.False(range.Contains(5)); // 终点不含
        Assert.False(range.Contains(1));

        Assert.True(range.IsWithin(5));
        Assert.False(range.IsWithin(4));
    }

    [Fact]
    public void TextRange_ClampAndIntersectArePure()
    {
        var range = new TextRange(4, 9);
        Assert.Equal(new TextRange(4, 6), range.Clamp(6));
        Assert.Equal(new TextRange(4, 9), range); // 原值不变

        Assert.Equal(new TextRange(4, 6), range.Intersect(new TextRange(0, 6)));
        Assert.Equal(new TextRange(7, 9), new TextRange(7, 12).Intersect(new TextRange(5, 9)));
        Assert.True(range.Intersect(new TextRange(0, 1)).IsEmpty);

        Assert.Equal(new TextRange(6, 11), range.Offset(2));
    }

    [Fact]
    public void TextSelection_KeepsDirectionAndNormalizesRange()
    {
        var forward = new TextSelection(2, 7);
        Assert.Equal(TextSelectionDirection.Forward, forward.Direction);
        Assert.False(forward.IsCollapsed);
        Assert.Equal(5, forward.Length);
        Assert.Equal(new TextRange(2, 7), forward.Range);
        Assert.Equal(2, forward.SelectionStart);
        Assert.Equal(7, forward.SelectionEnd);

        var backward = new TextSelection(7, 2);
        Assert.Equal(TextSelectionDirection.Backward, backward.Direction);
        Assert.Equal(new TextRange(2, 7), backward.Range);
        Assert.Equal(forward.Range, backward.Range); // 同一范围，方向不同

        var caret = TextSelection.Caret(4);
        Assert.True(caret.IsCollapsed);
        Assert.Equal(TextSelectionDirection.None, caret.Direction);
        Assert.Equal(0, caret.Length);
    }

    [Fact]
    public void TextSelection_NegativeEndpointsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TextSelection(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TextSelection(0, -1));
    }

    [Fact]
    public void TextSelection_ClampPreservesDirection()
    {
        var backward = new TextSelection(9, 4);
        var clamped = backward.Clamp(6);
        Assert.Equal(new TextSelection(6, 4), clamped);
        Assert.Equal(TextSelectionDirection.Backward, clamped.Direction);
    }

    [Fact]
    public void TextSelection_SnapsToGraphemeBoundaries_WithSurrogatePair()
    {
        // "a😀b"：😀 是代理对，占据 1..3；字素边界为 0、1、3、4。
        var layout = new TextLayoutResult(
            "a\U0001F600b",
            FontDescription.Default,
            1.0,
            new Size(40, 20),
            [],
            [0, 1, 3, 4],
            [0, 5, 10, 15]);

        var midSurrogate = new TextSelection(2, 2).SnapToGraphemeBoundaries(layout);
        Assert.Equal(1, midSurrogate.Active);

        var end = new TextSelection(4, 4).SnapToGraphemeBoundaries(layout);
        Assert.Equal(4, end.Active);

        var beyond = new TextSelection(0, 99).SnapToGraphemeBoundaries(layout);
        Assert.Equal(4, beyond.Active);
    }

    [Fact]
    public void TextSnapshot_CarriesVersionSelectionAndComposingFlag()
    {
        var snapshot = new TextSnapshot("你好", new TextSelection(0, 2), version: 7, isComposing: true);
        Assert.Equal("你好", snapshot.Text);
        Assert.Equal(2, snapshot.Length);
        Assert.Equal(7, snapshot.Version);
        Assert.True(snapshot.IsComposing);
        Assert.True(snapshot.IsConsistent);

        Assert.Throws<ArgumentOutOfRangeException>(() => new TextSnapshot("x", TextSelection.Caret(0), version: -1));
    }

    [Fact]
    public void TextSnapshot_NullTextIsEmpty_AndClampRepairsOutOfRangeSelection()
    {
        var snapshot = new TextSnapshot(null, new TextSelection(2, 9), version: 3);
        Assert.Equal(string.Empty, snapshot.Text);
        Assert.False(snapshot.IsConsistent);

        var clamped = snapshot.Clamp();
        Assert.Equal(new TextSelection(0, 0), clamped.Selection);
        Assert.Equal(3, clamped.Version);
        Assert.True(clamped.IsConsistent);
    }

    [Fact]
    public void TextSnapshot_NextAdvancesExactlyOneVersion()
    {
        var first = TextSnapshot.Empty;
        var second = first.Next("a", TextSelection.Caret(1));
        Assert.Equal(1, second.Version);
        Assert.Equal("a", second.Text);

        var composing = second.WithComposing(true);
        Assert.True(composing.IsComposing);
        Assert.Equal(second.Version, composing.Version);
        Assert.Equal(second.Text, composing.Text);
    }

    [Fact]
    public void EditOperation_DefaultsToTypingUserInput_AndScrubs()
    {
        var operation = new EditOperation(new TextRange(0, 0), "a\rb\nc\td");
        Assert.Equal("abcd", operation.Replacement); // 单行净化
        Assert.True(operation.Scrubbed);
        Assert.Equal(TextEditKind.Typing, operation.Kind);
        Assert.Equal(TextEditReason.UserInput, operation.Reason);
        Assert.Equal(4, operation.LengthDelta);
        Assert.False(operation.IsDeletion);
        Assert.False(operation.IsNoOp);
    }

    [Fact]
    public void EditOperation_KindNoneIsRejected()
    {
        Assert.Throws<ArgumentException>(() => new EditOperation(new TextRange(0, 0), "x", kind: TextEditKind.None));
    }

    [Fact]
    public void EditOperation_TryCreateReports_FullyScrubbedNonEmptyReplacement()
    {
        // 显式区分：非空插入串净化后为空既可能是“粘贴净化后为空”，也可能是数据损坏，不能当删除执行。
        var ok = EditOperation.TryCreate(
            new TextRange(0, 1),
            "\n\t",
            TextEditKind.Paste,
            TextEditReason.Clipboard,
            scrubSingleLine: true,
            out _,
            out var error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Contains("净化", error!);

        // 本来就空的插入串不是错误（删除语义），显式删除请用 Delete。
        Assert.True(EditOperation.TryCreate(
            new TextRange(0, 1),
            string.Empty,
            TextEditKind.Backspace,
            TextEditReason.UserInput,
            scrubSingleLine: true,
            out var deletion,
            out _));
        Assert.True(deletion.IsDeletion);
        Assert.Equal(1, deletion.Range.Length);
        Assert.Equal(0, deletion.Replacement.Length);
    }

    [Fact]
    public void EditOperation_DeleteAndNoOp()
    {
        var deletion = EditOperation.Delete(new TextRange(2, 5), TextEditKind.Backspace);
        Assert.True(deletion.IsDeletion);
        Assert.Equal(TextEditKind.Backspace, deletion.Kind);
        Assert.False(deletion.Scrubbed);
        Assert.Equal(-3, deletion.LengthDelta);

        var noOp = new EditOperation(new TextRange(4, 4), string.Empty);
        Assert.True(noOp.IsNoOp);
        Assert.False(noOp.IsDeletion);
    }

    [Fact]
    public void EditOperation_ProgrammaticInsertionCanSkipScrubbing()
    {
        var operation = new EditOperation(new TextRange(0, 0), "a\nb", scrubSingleLine: false);
        Assert.Equal("a\nb", operation.Replacement);
        Assert.False(operation.Scrubbed);
    }

    [Fact]
    public void EditOperation_ReplacementIsNonRotatable()
    {
        var operation = new EditOperation(new TextRange(0, 0), "x");
        var copy = operation with { Replacement = "y" };
        Assert.Equal("x", operation.Replacement);
        Assert.Equal("y", copy.Replacement);
    }

    [Fact]
    public void EditResult_AcceptedMismatchWithAfterSnapshotIsRejected()
    {
        var before = TextSnapshot.Empty;
        var after = before.Next("abc", TextSelection.Caret(3));

        Assert.Throws<ArgumentException>(() => new EditResult(
            TextEditStatus.Accepted,
            before,
            after,
            acceptedText: "different",
            documentVersion: 1));
    }

    [Fact]
    public void EditResult_UnchangedRequiresIdenticalText()
    {
        var before = new TextSnapshot("abc", TextSelection.Caret(3), 2);
        var after = new TextSnapshot("abd", TextSelection.Caret(3), 2);
        Assert.Throws<ArgumentException>(() => new EditResult(
            TextEditStatus.Unchanged,
            before,
            after,
            acceptedText: "abd",
            documentVersion: 2));

        var unchanged = new EditResult(TextEditStatus.Unchanged, before, before, "abc", 2, "setter 拒绝写入");
        Assert.False(unchanged.Changed);
        Assert.Equal("setter 拒绝写入", unchanged.Reason);
    }

    [Fact]
    public void EditResult_RejectedKeepsVersionButAcceptedAdvancesIt()
    {
        var before = new TextSnapshot("a", TextSelection.Caret(1), 5);
        var after = before.Next("ab", TextSelection.Caret(2));

        var accepted = new EditResult(TextEditStatus.Accepted, before, after, "ab", after.Version);
        Assert.True(accepted.Changed);
        Assert.Equal(6, accepted.DocumentVersion);
        Assert.Null(accepted.Reason);

        var rejected = new EditResult(TextEditStatus.Rejected, before, before, "a", before.Version, "超出 MaxLength");
        Assert.False(rejected.Changed);
        Assert.Equal(5, rejected.DocumentVersion);
        Assert.Equal("a", rejected.AcceptedText);
        Assert.Equal(rejected.Before.Text, rejected.After.Text);
    }

    [Fact]
    public void EditResult_DefaultStatusNoneIsRejected_AndFallbackResultIsAvailable()
    {
        Assert.Throws<ArgumentException>(() => new EditResult(
            TextEditStatus.None,
            TextSnapshot.Empty,
            TextSnapshot.Empty,
            string.Empty,
            0));

        Assert.Equal(TextEditStatus.Rejected, EditResult.NotHandled.Status);
        Assert.Contains("回退", EditResult.NotHandled.Reason!);
    }
}
