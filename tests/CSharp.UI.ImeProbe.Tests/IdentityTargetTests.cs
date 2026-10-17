using CSharp.UI.ImeProbe;
using Xunit;

namespace CSharp.UI.ImeProbe.Tests;

/// <summary>
/// 身份核对目标与预热参数的确定性测试。
/// </summary>
/// <remarks>
/// Codex 复审指出两点必须固定下来：
/// 身份匹配不能永远写死成微软拼音（<c>--profile wetype/current</c> 下语义是错的）；
/// <c>--warmup ctrl-space</c> 必须被明确拒绝，而不是保留一条声称“失败也恢复键态”但实际做不到的路径。
/// </remarks>
public sealed class IdentityTargetTests
{
    [Fact]
    public void MicrosoftPinyinTarget_MatchesOnlyMicrosoftPinyinIdentity()
    {
        var target = ProbeTargetProfile.MicrosoftPinyin;

        var ms = new TsfProfile.InputProcessorProfile
        {
            ProfileType = TsfProfile.TfProfileTypeInputProcessor,
            LangId = TsfProfile.LangIdChineseSimplified,
            Clsid = TsfProfile.MicrosoftPinyinClsid,
            ProfileGuid = TsfProfile.MicrosoftPinyinProfile,
        };
        var weType = new TsfProfile.InputProcessorProfile
        {
            ProfileType = TsfProfile.TfProfileTypeInputProcessor,
            LangId = TsfProfile.LangIdChineseSimplified,
            Clsid = TsfProfile.WeTypeClsid,
            ProfileGuid = TsfProfile.WeTypeProfile,
        };

        Assert.True(target.Matches(ms));
        Assert.False(target.Matches(weType));
    }

    [Fact]
    public void WeTypeTarget_MatchesItsOwnProfile_NotMicrosoftPinyin()
    {
        var target = ProbeTargetProfile.WeType;

        var weType = new TsfProfile.InputProcessorProfile
        {
            ProfileType = TsfProfile.TfProfileTypeInputProcessor,
            LangId = TsfProfile.LangIdChineseSimplified,
            Clsid = TsfProfile.WeTypeClsid,
            ProfileGuid = TsfProfile.WeTypeProfile,
        };
        var ms = new TsfProfile.InputProcessorProfile
        {
            ProfileType = TsfProfile.TfProfileTypeInputProcessor,
            LangId = TsfProfile.LangIdChineseSimplified,
            Clsid = TsfProfile.MicrosoftPinyinClsid,
            ProfileGuid = TsfProfile.MicrosoftPinyinProfile,
        };

        Assert.True(target.Matches(weType));
        Assert.False(target.Matches(ms));
    }

    [Fact]
    public void CurrentTarget_MatchesByClsidAndGuid_EvenForKeyboardLayoutType()
    {
        // --profile current 的目标来自启动时的实际活动项，可能不是 INPUTPROCESSOR 类型；
        // 这种目标只按 CLSID/ProfileGuid 比对，不能因为类型不同就永远判不匹配。
        var keyboardClsid = new Guid("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var keyboardProfile = Guid.Empty;
        var target = new ProbeTargetProfile("current-active-profile", 0, 0, keyboardClsid, keyboardProfile);

        var actual = new TsfProfile.InputProcessorProfile
        {
            ProfileType = TsfProfile.TfProfileTypeKeyboardLayout,
            LangId = 0x0409,
            Clsid = keyboardClsid,
            ProfileGuid = keyboardProfile,
        };

        Assert.True(target.Matches(actual));
    }

    [Fact]
    public void CurrentTarget_DoesNotMatchADifferentProfile()
    {
        var target = new ProbeTargetProfile(
            "current-active-profile", 0, 0, ProbeTargetProfile.WeType.Clsid, ProbeTargetProfile.WeType.ProfileGuid);

        var ms = new TsfProfile.InputProcessorProfile
        {
            ProfileType = TsfProfile.TfProfileTypeInputProcessor,
            LangId = TsfProfile.LangIdChineseSimplified,
            Clsid = TsfProfile.MicrosoftPinyinClsid,
            ProfileGuid = TsfProfile.MicrosoftPinyinProfile,
        };

        Assert.False(target.Matches(ms));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("shift")]
    public void Warmup_AllowsOnlyTheDocumentedValues(string warmup)
        => Assert.Equal(warmup, ProbeOptions.ParseForTests(["--warmup", warmup]).Warmup);

    [Fact]
    public void Warmup_CtrlSpaceIsRejectedInsteadOfClaimingKeyStateRecovery()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => ProbeOptions.ParseForTests(["--warmup", "ctrl-space"]));

        Assert.Contains("ctrl-space", ex.Message);
        Assert.Contains("key-up", ex.Message);
    }

    [Fact]
    public void Warmup_UnknownValueIsRejected()
        => Assert.Throws<ArgumentException>(() => ProbeOptions.ParseForTests(["--warmup", "ctrl+space"]));
}
