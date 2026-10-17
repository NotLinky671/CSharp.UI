namespace CSharp.UI.ImeProbe;

/// <summary>
/// 本探针期望的输入法目标（用于身份核对的身份语句）。
/// </summary>
/// <remarks>
/// 身份核对不能写死成“只有微软拼音才算匹配”：用 <c>--profile wetype</c> 或 <c>--profile current</c> 时，
/// 目标是微信输入法或“启动时实际活动的那个 profile”。这里只描述“期望什么”，不做任何激活。
/// </remarks>
/// <param name="Name">便于阅读的名称（microsoft-pinyin / wetype / current-active-profile）。</param>
/// <param name="ProfileType">TF_PROFILETYPE_*；current 时未知，核对时只比实际值。</param>
/// <param name="LangId">期望语言 ID。</param>
/// <param name="Clsid">期望 CLSID；current 时为空。</param>
/// <param name="ProfileGuid">期望 ProfileGuid；current 时为空。</param>
internal sealed record ProbeTargetProfile(
    string Name,
    uint ProfileType,
    ushort LangId,
    Guid Clsid,
    Guid ProfileGuid)
{
    /// <summary>是否把某个实际活动 profile 视为“就是本次核对的目标”。</summary>
    internal bool Matches(TsfProfile.InputProcessorProfile actual)
        => ProfileType == 0
            ? actual.Clsid == Clsid && actual.ProfileGuid == ProfileGuid
            : actual.ProfileType == ProfileType && actual.LangId == LangId
                && actual.Clsid == Clsid && actual.ProfileGuid == ProfileGuid;

    /// <summary>微软拼音目标。</summary>
    internal static ProbeTargetProfile MicrosoftPinyin => new(
        "microsoft-pinyin",
        TsfProfile.TfProfileTypeInputProcessor,
        TsfProfile.LangIdChineseSimplified,
        TsfProfile.MicrosoftPinyinClsid,
        TsfProfile.MicrosoftPinyinProfile);

    /// <summary>微信输入法目标（对照用）。</summary>
    internal static ProbeTargetProfile WeType => new(
        "wetype",
        TsfProfile.TfProfileTypeInputProcessor,
        TsfProfile.LangIdChineseSimplified,
        TsfProfile.WeTypeClsid,
        TsfProfile.WeTypeProfile);
}
