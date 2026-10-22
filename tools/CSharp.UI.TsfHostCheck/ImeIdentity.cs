using CSharp.UI.ImeProbe;

namespace CSharp.UI.TsfHostCheck;

/// <summary>
/// 一次只读的当前活动 profile 快照；字段来自 ITfInputProcessorProfileMgr::GetActiveProfile，
/// 附带同一安全点内读取的自有前台/焦点/线程核对。
/// </summary>
internal readonly record struct ActiveProfileSnapshot(
    int HResult,
    uint ProfileType,
    ushort LangId,
    Guid Clsid,
    Guid ProfileGuid,
    bool OwnForeground,
    IntPtr Focus,
    uint Win32ThreadId);

/// <summary>
/// 只读身份判定：只有 Query 成功、类型为 input processor、语言为简体中文、
/// CLSID/GUID 同时匹配已审核的 Microsoft Pinyin profile，才允许把本轮中文场景
/// 称为 Microsoft Pinyin 测试前提。不激活、不启用/禁用、不修改任何 profile。
/// </summary>
internal static class ImeIdentity
{
    /// <summary>不可验证身份的快照值；执行器基线必须拒绝它（见 <see cref="IsVerifiableMicrosoftPinyin"/>）。</summary>
    internal const string UnverifiableProfile = "unverifiable";

    internal static bool IsMicrosoftPinyin(in ActiveProfileSnapshot snapshot)
        => IsMicrosoftPinyin(snapshot.HResult, snapshot.ProfileType, snapshot.LangId,
            snapshot.Clsid, snapshot.ProfileGuid);

    /// <summary>完整身份判定：HResult/类型/语言（0x0804）/CLSID/ProfileGuid 全部匹配才算。</summary>
    internal static bool IsMicrosoftPinyin(int hResult, uint profileType, ushort langId,
        Guid clsid, Guid profileGuid)
        => hResult == 0
           && profileType == TsfProfile.TfProfileTypeInputProcessor
           && langId == TsfProfile.LangIdChineseSimplified
           && clsid == TsfProfile.MicrosoftPinyinClsid
           && profileGuid == TsfProfile.MicrosoftPinyinProfile;

    internal static bool SameIdentity(in ActiveProfileSnapshot left, in ActiveProfileSnapshot right)
        => left.HResult == 0
           && right.HResult == 0
           && left.ProfileType == right.ProfileType
           && left.LangId == right.LangId
           && left.Clsid == right.Clsid
           && left.ProfileGuid == right.ProfileGuid;

    /// <summary>
    /// 完整身份标签（不丢字段）：hresult/类型/langid/clsid/profile-guid 全部编码进字符串。
    /// 即使同样是 Microsoft Pinyin，语言或类型不同也会产生不同标签；
    /// 比较 <see cref="LifecycleSnapshot.Profile"/> 等价于比较完整身份。
    /// </summary>
    internal static string Describe(int hResult, uint profileType, ushort langId,
        Guid clsid, Guid profileGuid)
        => $"hr={hResult:X8}|type=0x{profileType:X4}|lang=0x{langId:X4}"
            + $"|clsid={clsid:B}|profile={profileGuid:B}";

    /// <summary>
    /// 只有当快照确认为真实 Microsoft Pinyin（0x0804 input processor + 双 GUID）时，
    /// 才返回 <see cref="Describe"/> 的完整身份标签；否则返回 <see cref="UnverifiableProfile"/>，
    /// 绝不把不完整标签塞进快照（不完整标签可能被比较接受为“稳定”）。
    /// </summary>
    internal static string VerifiedIdentityTag(in ActiveProfileSnapshot snapshot)
        => IsMicrosoftPinyin(snapshot)
            ? Describe(snapshot.HResult, snapshot.ProfileType, snapshot.LangId,
                snapshot.Clsid, snapshot.ProfileGuid)
            : UnverifiableProfile;

    /// <summary>
    /// 已核验 Microsoft Pinyin 身份的**规范化**标签（<see cref="Describe"/> 输出字段
    /// 顺序与大小写固定）。任何字段不符——非零 hr、lang/type/CLSID/ProfileGuid——
    /// 都会产生不同标签；前缀匹配或截断串永远不会与全串 ordinal 相等。
    /// </summary>
    internal static readonly string ExpectedMicrosoftPinyinTag = Describe(
        0, TsfProfile.TfProfileTypeInputProcessor, TsfProfile.LangIdChineseSimplified,
        TsfProfile.MicrosoftPinyinClsid, TsfProfile.MicrosoftPinyinProfile);

    /// <summary>
    /// 判断一个 <see cref="LifecycleSnapshot.Profile"/> 值是否已通过完整身份核验。
    /// 与 <see cref="ExpectedMicrosoftPinyinTag"/> 做 ordinal **全串**相等比较：
    /// 不是宽松前缀——错误 lang/type/GUID、hr 失败、缺字段或截断标签一律被拒绝。
    /// </summary>
    internal static bool IsVerifiableMicrosoftPinyin(string profileTag)
        => string.Equals(profileTag, ExpectedMicrosoftPinyinTag, StringComparison.Ordinal);

    internal static string Describe(in ActiveProfileSnapshot snapshot)
        => Describe(snapshot.HResult, snapshot.ProfileType, snapshot.LangId,
            snapshot.Clsid, snapshot.ProfileGuid)
            + $" own_foreground={snapshot.OwnForeground} focus=0x{snapshot.Focus.ToInt64():X}"
            + $" win32_tid={snapshot.Win32ThreadId}";
}
