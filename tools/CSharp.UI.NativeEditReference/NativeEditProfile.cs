using CSharp.UI.ImeProbe;

namespace CSharp.UI.NativeEditReference;

/// <summary>
/// 只读核对当前活动键盘 profile 是否为微软拼音，并要求前后一致。
/// 复用 ImeProbe 的只读查询；**绝不**切换/启用/禁用任何 profile。
/// 必须在 UI 线程（STA）调用。
/// </summary>
internal static class NativeEditProfile
{
    internal readonly record struct Snapshot(
        int HResult, Guid Clsid, Guid ProfileGuid, bool IsActive, bool IsInputProcessor)
    {
        internal bool IsMicrosoftPinyin => HResult == 0
            && IsInputProcessor
            && Clsid == TsfProfile.MicrosoftPinyinClsid
            && ProfileGuid == TsfProfile.MicrosoftPinyinProfile;

        internal bool SameAs(Snapshot other) => HResult == other.HResult
            && Clsid == other.Clsid
            && ProfileGuid == other.ProfileGuid
            && IsActive == other.IsActive;
    }

    /// <summary>只读读取当前活动 profile。返回的 HResult 是系统原值，不改写。</summary>
    internal static Snapshot Read(IProbeEventLog log, string label)
    {
        var (hr, profile) = TsfProfile.ReadActiveProfile(log, label);
        return new Snapshot(hr, profile.Clsid, profile.ProfileGuid, profile.IsActive,
            profile.ProfileType == TsfProfile.TfProfileTypeInputProcessor);
    }

    internal static string Describe(Snapshot snapshot)
        => $"hr=0x{snapshot.HResult:X8} clsid={snapshot.Clsid} profile={snapshot.ProfileGuid} "
            + $"active={snapshot.IsActive} input_processor={snapshot.IsInputProcessor} "
            + $"microsoft_pinyin={snapshot.IsMicrosoftPinyin}";
}
