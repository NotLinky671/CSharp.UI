using CSharp.UI.ImeProbe;
using Xunit;

namespace CSharp.UI.ImeProbe.Tests;

/// <summary>
/// 提交内容判定的确定性测试。
/// </summary>
/// <remarks>
/// 起因是真实证据：一次受控微软拼音运行里 seq140 收到了 <c>GCS_RESULTSTR="niha"</c>（纯 ASCII，失焦自动回吐），
/// 而“段数恰好为 1”的旧断言会把它判成 PASS。这里的测试固定住三件事：
/// 段数正确 != 内容正确；固定测试串要求明确预期内容；拼音只能从<b>本次组合会话的预编辑</b>核对归因。
/// </remarks>
public sealed class CommitOutcomeTests
{
    [Theory]
    [InlineData("niha", true)]
    [InlineData("nihao", true)]
    [InlineData("abc123", true)]
    [InlineData("你好", false)]
    [InlineData("你好n", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsAsciiOnly_ClassifiesRealEvidenceShapes(string? value, bool expected)
        => Assert.Equal(expected, CommitOutcome.IsAsciiOnly(value));

    [Theory]
    [InlineData("你好", true)]
    [InlineData("你好nihao", true)]
    [InlineData("niha", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ContainsNonAscii_DetectsCjk(string? value, bool expected)
        => Assert.Equal(expected, CommitOutcome.ContainsNonAscii(value));

    [Fact]
    public void NormalizePinyin_DropsSyllableSeparatorsAndKeepsAlphanumerics()
    {
        // 真实组合串里微软拼音会插入 ' 作为音节分隔符（seq110 = ni'h）。
        Assert.Equal("nih", CommitOutcome.NormalizePinyin("ni'h"));
        Assert.Equal("nihao", CommitOutcome.NormalizePinyin("ni'hao"));
        Assert.Equal("niha", CommitOutcome.NormalizePinyin("ni ha"));
    }

    [Theory]
    [InlineData("nihao", "你好")]
    [InlineData("ni", "你")]
    [InlineData("ma", "吗")]
    [InlineData("nihao2", null)]
    [InlineData(null, null)]
    public void ExpectedResultFor_OnlyFixedTestStringsHaveFixedExpectations(string? keys, string? expected)
        => Assert.Equal(expected, CommitOutcome.ExpectedResultFor(keys));

    [Fact]
    public void Evaluate_NihaoPreeditAndHelloResult_Succeeds()
    {
        // 正确路径：本会话预编辑里出现过 nihao（含分隔符形态），结果是“你好”。
        var verdict = CommitOutcome.Evaluate(
            result: "你好",
            expectedResult: "你好",
            requireChinese: true,
            expectedPinyin: "nihao",
            preeditHistory: ["n", "ni", "ni'h", "ni'ha", "ni'hao"]);

        Assert.True(verdict.Satisfied, verdict.Reason);
        Assert.True(verdict.PinyinSeenInPreedit);
        Assert.True(verdict.ContentMatched);
    }

    [Fact]
    public void Evaluate_NihaoPreeditButAsciiEcho_Fails()
    {
        // 真实失败形态：一次带标志的提交，内容是未转换的 nihao 回吐。
        var verdict = CommitOutcome.Evaluate(
            result: "nihao",
            expectedResult: "你好",
            requireChinese: true,
            expectedPinyin: "nihao",
            preeditHistory: ["n", "ni", "ni'hao"]);

        Assert.False(verdict.Satisfied);
        Assert.Contains("plain ASCII", verdict.Reason);
    }

    [Fact]
    public void Evaluate_RealFocusLossShapeNiha_Fails()
    {
        // 真实证据 seq140：失焦自动提交 "niha"，不是空格选词结果。
        var verdict = CommitOutcome.Evaluate(
            result: "niha",
            expectedResult: "你好",
            requireChinese: true,
            expectedPinyin: "nihao",
            preeditHistory: ["n", "ni", "ni'h", "ni'ha"]);

        Assert.False(verdict.Satisfied);
        Assert.Contains("plain ASCII", verdict.Reason);
    }

    [Fact]
    public void Evaluate_ChineseResultWithoutExpectedContent_Fails()
    {
        var verdict = CommitOutcome.Evaluate(
            result: "世界",
            expectedResult: "你好",
            requireChinese: true,
            expectedPinyin: "nihao",
            preeditHistory: ["n", "ni", "ni'hao"]);

        Assert.False(verdict.Satisfied);
        Assert.Contains("does not equal the expected content", verdict.Reason);
    }

    [Fact]
    public void Evaluate_ResultNotAttributableToThisSessionPreedit_Fails()
    {
        // 没有本会话预编辑证据：不能把结果归因到本场景（可能来自旧会话）。
        var verdict = CommitOutcome.Evaluate(
            result: "你好",
            expectedResult: null,
            requireChinese: true,
            expectedPinyin: "nihao",
            preeditHistory: ["m", "ma"]);

        Assert.False(verdict.Satisfied);
        Assert.Contains("not attributable", verdict.Reason);
    }

    [Fact]
    public void Evaluate_NonAsciiButNotCjk_IsNotTreatedAsTheFixedChineseResult()
    {
        // Codex 指出：é/全角标点都是“非 ASCII”，不能一概当成正确的中文结果。
        // 固定测试串走明确预期内容核对，因此 "é" 不算“你好”。
        var verdict = CommitOutcome.Evaluate(
            result: "é",
            expectedResult: "你好",
            requireChinese: true,
            expectedPinyin: "nihao",
            preeditHistory: ["n", "ni", "ni'hao"]);

        Assert.False(verdict.Satisfied);
    }

    [Fact]
    public void Evaluate_NoFixedExpectation_AcceptsNonAsciiAttributedToThisSession()
    {
        var verdict = CommitOutcome.Evaluate(
            result: "吗",
            expectedResult: null,
            requireChinese: true,
            expectedPinyin: "ma",
            preeditHistory: ["m", "ma"]);

        Assert.True(verdict.Satisfied, verdict.Reason);
        Assert.True(verdict.PinyinSeenInPreedit);
    }

    [Fact]
    public void Evaluate_EmptyResult_IsRejected()
    {
        var verdict = CommitOutcome.Evaluate(
            null, "你好", requireChinese: true, expectedPinyin: "nihao", preeditHistory: ["ni"]);

        Assert.False(verdict.Satisfied);
        Assert.Contains("empty", verdict.Reason);
    }

    [Fact]
    public void Evaluate_PlainKeyboardPath_OnlyRequiresPresence()
    {
        // 美国键盘布局路径不应该因为“结果不是中文”被判失败。
        var verdict = CommitOutcome.Evaluate("abc", null, requireChinese: false);

        Assert.True(verdict.Satisfied);
    }

    [Fact]
    public void Evaluate_PlainKeyboardPath_StillRejectsEmptyResult()
    {
        var verdict = CommitOutcome.Evaluate(string.Empty, null, requireChinese: false);

        Assert.False(verdict.Satisfied);
    }

    [Fact]
    public void Evaluate_FixedExpectationWithEmptyHistory_Fails()
    {
        // 内容对，但本次组合会话没有任何预编辑证据：不能判定为通过，也不能声称 observed。
        var verdict = CommitOutcome.Evaluate(
            result: "你好",
            expectedResult: "你好",
            requireChinese: true,
            expectedPinyin: "nihao",
            preeditHistory: []);

        Assert.False(verdict.Satisfied);
        Assert.False(verdict.PinyinSeenInPreedit);
        Assert.True(verdict.ContentMatched);
        Assert.Contains("was NOT observed in this session's preedit", verdict.Reason);
    }

    [Fact]
    public void Evaluate_FixedExpectationWithHistoryNotContainingPinyin_Fails()
    {
        var verdict = CommitOutcome.Evaluate(
            result: "你好",
            expectedResult: "你好",
            requireChinese: true,
            expectedPinyin: "nihao",
            preeditHistory: ["m", "ma", "ma'?"]);

        Assert.False(verdict.Satisfied);
        Assert.False(verdict.PinyinSeenInPreedit);
        Assert.Contains("cannot be attributed", verdict.Reason);
    }

    [Fact]
    public void Evaluate_FixedExpectationWithMatchingHistory_Succeeds()
    {
        var verdict = CommitOutcome.Evaluate(
            result: "你好",
            expectedResult: "你好",
            requireChinese: true,
            expectedPinyin: "nihao",
            preeditHistory: ["n", "ni", "ni'h", "ni'hao"]);

        Assert.True(verdict.Satisfied);
        Assert.True(verdict.PinyinSeenInPreedit);
        Assert.True(verdict.ContentMatched);
    }
}
