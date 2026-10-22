using CSharp.UI.ImeProbe;
using Xunit;

namespace CSharp.UI.TsfHostCheck.Tests;

public sealed class ImeIdentityTests
{
    [Fact]
    public void MatchesReviewedMicrosoftPinyinProfile()
    {
        Assert.True(ImeIdentity.IsMicrosoftPinyin(
            0,
            TsfProfile.TfProfileTypeInputProcessor,
            TsfProfile.LangIdChineseSimplified,
            TsfProfile.MicrosoftPinyinClsid,
            TsfProfile.MicrosoftPinyinProfile));
    }

    [Fact]
    public void RejectsWrongClsidGuidTypeLangOrFailedQuery()
    {
        Assert.False(ImeIdentity.IsMicrosoftPinyin(
            0,
            TsfProfile.TfProfileTypeInputProcessor,
            TsfProfile.LangIdChineseSimplified,
            Guid.NewGuid(),
            TsfProfile.MicrosoftPinyinProfile));

        Assert.False(ImeIdentity.IsMicrosoftPinyin(
            0,
            TsfProfile.TfProfileTypeInputProcessor,
            TsfProfile.LangIdChineseSimplified,
            TsfProfile.MicrosoftPinyinClsid,
            Guid.NewGuid()));

        Assert.False(ImeIdentity.IsMicrosoftPinyin(
            0,
            TsfProfile.TfProfileTypeKeyboardLayout,
            TsfProfile.LangIdChineseSimplified,
            TsfProfile.MicrosoftPinyinClsid,
            TsfProfile.MicrosoftPinyinProfile));

        // 非简体中文（0x0804）即使双 GUID 匹配也不算 Microsoft Pinyin 前提。
        Assert.False(ImeIdentity.IsMicrosoftPinyin(
            0,
            TsfProfile.TfProfileTypeInputProcessor,
            0x0409,
            TsfProfile.MicrosoftPinyinClsid,
            TsfProfile.MicrosoftPinyinProfile));

        Assert.False(ImeIdentity.IsMicrosoftPinyin(
            unchecked((int)0x80004005),
            TsfProfile.TfProfileTypeInputProcessor,
            TsfProfile.LangIdChineseSimplified,
            TsfProfile.MicrosoftPinyinClsid,
            TsfProfile.MicrosoftPinyinProfile));

        // S_FALSE 不能作为已确认身份，即使 CLSID/GUID 匹配。
        Assert.False(ImeIdentity.IsMicrosoftPinyin(
            1,
            TsfProfile.TfProfileTypeInputProcessor,
            TsfProfile.LangIdChineseSimplified,
            TsfProfile.MicrosoftPinyinClsid,
            TsfProfile.MicrosoftPinyinProfile));
    }

    [Fact]
    public void SameIdentityComparesProfileFields()
    {
        var left = new ActiveProfileSnapshot(
            0,
            TsfProfile.TfProfileTypeInputProcessor,
            0x0804,
            TsfProfile.MicrosoftPinyinClsid,
            TsfProfile.MicrosoftPinyinProfile,
            OwnForeground: true,
            Focus: IntPtr.Zero,
            Win32ThreadId: 1);

        var sameButDifferentThreadAndForeground = left with
        {
            OwnForeground = false,
            Win32ThreadId = 2,
        };
        Assert.True(ImeIdentity.SameIdentity(left, sameButDifferentThreadAndForeground));

        Assert.False(ImeIdentity.SameIdentity(left, left with { ProfileGuid = Guid.NewGuid() }));
        Assert.False(ImeIdentity.SameIdentity(left, left with { Clsid = Guid.NewGuid() }));
        Assert.False(ImeIdentity.SameIdentity(left, left with { LangId = 0x0409 }));
        Assert.False(ImeIdentity.SameIdentity(left, left with { ProfileType = TsfProfile.TfProfileTypeKeyboardLayout }));
        Assert.False(ImeIdentity.SameIdentity(left, left with { HResult = -1 }));
        Assert.False(ImeIdentity.SameIdentity(left, left with { HResult = 1 }));
    }

    [Fact]
    public void DescribeEncodesCompleteIdentityFields()
    {
        var tag = ImeIdentity.Describe(0,
            TsfProfile.TfProfileTypeInputProcessor,
            TsfProfile.LangIdChineseSimplified,
            TsfProfile.MicrosoftPinyinClsid,
            TsfProfile.MicrosoftPinyinProfile);

        // 完整身份标签：hresult/类型/langid/clsid/profile-guid 一个不丢。
        Assert.Contains("hr=00000000", tag);
        Assert.Contains("type=0x0001", tag);
        Assert.Contains("lang=0x0804", tag);
        Assert.Contains(TsfProfile.MicrosoftPinyinClsid.ToString("B"), tag);
        Assert.Contains(TsfProfile.MicrosoftPinyinProfile.ToString("B"), tag);
    }

    /// <summary>
    /// 完整身份核验是 ordinal 全串相等：只接受规范化的完整标签，不接受
    /// 前缀、截断、尾部追加或任一字段（lang/type/CLSID/profile-guid/hr）被替换。
    /// </summary>
    [Fact]
    public void IsVerifiableMicrosoftPinyinRequiresExactCanonicalTag()
    {
        var canonical = ImeIdentity.Describe(0,
            TsfProfile.TfProfileTypeInputProcessor, TsfProfile.LangIdChineseSimplified,
            TsfProfile.MicrosoftPinyinClsid, TsfProfile.MicrosoftPinyinProfile);
        Assert.Equal(ImeIdentity.ExpectedMicrosoftPinyinTag, canonical);
        Assert.True(ImeIdentity.IsVerifiableMicrosoftPinyin(canonical));

        // 前缀/截断——以 hr=00000000| 开头但不完整。
        Assert.False(ImeIdentity.IsVerifiableMicrosoftPinyin("hr=00000000|"));
        Assert.False(ImeIdentity.IsVerifiableMicrosoftPinyin(
            "hr=00000000|type=0x0001|lang=0x0804"));
        Assert.False(ImeIdentity.IsVerifiableMicrosoftPinyin(
            canonical.Substring(0, canonical.Length - 2)));
        // 尾部追加。
        Assert.False(ImeIdentity.IsVerifiableMicrosoftPinyin(canonical + "|x"));
        Assert.False(ImeIdentity.IsVerifiableMicrosoftPinyin(canonical + " "));

        // 任一字段被替换即使前缀相同也不行。
        foreach (var wrong in new[]
        {
            ImeIdentity.Describe(0, TsfProfile.TfProfileTypeInputProcessor,
                0x0409, TsfProfile.MicrosoftPinyinClsid, TsfProfile.MicrosoftPinyinProfile),
            ImeIdentity.Describe(0, TsfProfile.TfProfileTypeKeyboardLayout,
                TsfProfile.LangIdChineseSimplified, TsfProfile.MicrosoftPinyinClsid,
                TsfProfile.MicrosoftPinyinProfile),
            ImeIdentity.Describe(0, TsfProfile.TfProfileTypeInputProcessor,
                TsfProfile.LangIdChineseSimplified, Guid.Empty, TsfProfile.MicrosoftPinyinProfile),
            ImeIdentity.Describe(0, TsfProfile.TfProfileTypeInputProcessor,
                TsfProfile.LangIdChineseSimplified, TsfProfile.MicrosoftPinyinClsid, Guid.Empty),
            ImeIdentity.Describe(1, TsfProfile.TfProfileTypeInputProcessor,
                TsfProfile.LangIdChineseSimplified, TsfProfile.MicrosoftPinyinClsid,
                TsfProfile.MicrosoftPinyinProfile),
        })
        {
            Assert.False(ImeIdentity.IsVerifiableMicrosoftPinyin(wrong));
        }

        Assert.False(ImeIdentity.IsVerifiableMicrosoftPinyin(ImeIdentity.UnverifiableProfile));
        Assert.False(ImeIdentity.IsVerifiableMicrosoftPinyin(string.Empty));
    }
}
