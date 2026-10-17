using System.Runtime.InteropServices;

namespace CSharp.UI.ImeProbe;

/// <summary>
/// 探针内部的结构化日志出口。恢复路径只依赖这个接口，且所有调用都经过不抛异常的包装，
/// 因此“日志写入失败”绝不可能阻断 profile 恢复。
/// </summary>
internal interface IProbeEventLog
{
    void Record(string stage, string source, string kind, IDictionary<string, object?>? fields);
}

/// <summary>
/// <see cref="TsfProfile.TemporaryProfileScope"/> 依赖的原生后端。真实实现调用 msctf COM；
/// 测试注入一个假后端即可在无 Windows COM 的情况下验证恢复义务与判据。
/// </summary>
internal interface ITsfProfileBackend
{
    int GetActiveProfile(out TsfProfile.InputProcessorProfile profile);

    int GetProfile(
        uint profileType, ushort langid, Guid clsid, Guid profileGuid, out TsfProfile.InputProcessorProfile profile);

    int ActivateProfile(
        uint profileType, ushort langid, Guid clsid, Guid profileGuid, IntPtr hkl, uint flags);

    int DeactivateProfile(
        uint profileType, ushort langid, Guid clsid, Guid profileGuid, IntPtr hkl, uint flags);

    void Release();
}

/// <summary>后端工厂：返回 CoCreateInstance 的 HRESULT 与后端（失败时为 null）。</summary>
internal delegate (int HResult, ITsfProfileBackend? Backend) TsfProfileBackendFactory();

/// <summary>
/// <see cref="TsfProfile"/> 的 profile 身份查询与受控临时启用部分。
/// </summary>
/// <remarks>
/// <para>
/// 只读查询使用 <c>ITfInputProcessorProfileMgr::GetActiveProfile</c>（槽 10）与 <c>GetProfile</c>（槽 5），
/// 记录的是系统报告的实际活动 CLSID/GUID/flags/语言，而不是 <c>--profile</c> 参数或
/// <c>ImmGetDescription</c>（对 TSF 输入法常为空）。
/// </para>
/// <para>
/// 受控临时启用只在显式 <c>--temporary-enable-mspinyin</c> 下发生：先快照原活动 profile 与目标
/// profile 的启用标志，再 <c>ActivateProfile(FORPROCESS|ENABLEPROFILE)</c> 并核对实际活动身份；
/// <c>Dispose</c> 时 <c>DeactivateProfile(FORPROCESS|DISABLEPROFILE)</c> 恢复目标原启用状态、再恢复原活动
/// profile，并用 <c>GetProfile</c>/<c>GetActiveProfile</c> 校验。绝不使用 FORSESSION、不改默认输入法、
/// 不安装输入法；校验失败会显式记录而不是静默改配置。
/// </para>
/// <para>本文件不实现任何 TSF 文本存储/组合接收器（那属于 TI-07 范围）。</para>
/// </remarks>
internal static partial class TsfProfile
{
    internal const uint TfProfileTypeKeyboardLayout = 0x0002;

    // TF_INPUTPROCESSORPROFILE.Flags 的状态位（msctf.idl TF_IPP_FLAG_*）：0x1 = ACTIVE，0x2 = ENABLED。
    // 这与 ActivateProfile 的参数位 TF_IPPMF_ENABLEPROFILE / DISABLEPROFILE 不是同一枚举，不能复用。
    internal const uint TfIppFlagActive = 0x00000001;
    internal const uint TfIppFlagEnabled = 0x00000002;

    // ActivateProfile / DeactivateProfile 的参数位（msctf.idl TF_IPPMF_*）。
    internal const uint TfIppmEnableProfile = 0x00000001;
    internal const uint TfIppmDisableProfile = 0x00000002;

    private const int EFail = unchecked((int)0x80004005);

    /// <summary>msctf.idl <c>TF_INPUTPROCESSORPROFILE</c> 的托管镜像（字段顺序与原生一致）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct InputProcessorProfile
    {
        public uint ProfileType;
        public ushort LangId;
        public Guid Clsid;
        public Guid ProfileGuid;
        public Guid CategoryGuid;
        public IntPtr SubstituteHkl;
        public uint Caps;
        public IntPtr Hkl;
        public uint Flags;

        /// <summary>状态位 0x2（TF_IPP_FLAG_ENABLED）表示该 profile 已启用；0x1 是活动位，含义不同。</summary>
        internal readonly bool IsEnabled => (Flags & TfIppFlagEnabled) != 0;

        /// <summary>状态位 0x1（TF_IPP_FLAG_ACTIVE）表示系统报告该 profile 当前活动。</summary>
        internal readonly bool IsActive => (Flags & TfIppFlagActive) != 0;

        internal readonly string ProfileTypeName => ProfileType switch
        {
            1 => "TF_PROFILETYPE_INPUTPROCESSOR",
            2 => "TF_PROFILETYPE_KEYBOARDLAYOUT",
            _ => $"0x{ProfileType:X}",
        };
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetProfileDelegate(
        IntPtr self, uint profileType, ushort langid, ref Guid clsid, ref Guid profile, IntPtr hkl,
        out InputProcessorProfile result);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetActiveProfileDelegate(IntPtr self, ref Guid category, out InputProcessorProfile result);

    /// <summary>真实 msctf COM 后端工厂（默认）。</summary>
    internal static readonly TsfProfileBackendFactory DefaultBackendFactory = CreateComBackend;

    private static int CreateManager(out IntPtr manager)
    {
        var clsid = ClsidInputProcessorProfiles;
        var iid = IidInputProcessorProfileMgr;
        return CoCreateInstance(ref clsid, IntPtr.Zero, ClsctxInprocServer, ref iid, out manager);
    }

    private static T Method<T>(IntPtr manager, int slot) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(
            Marshal.ReadIntPtr(Marshal.ReadIntPtr(manager), slot * IntPtr.Size));

    /// <summary>写入日志；日志抛出绝不改变恢复/查询控制流。</summary>
    private static void SafeLog(
        IProbeEventLog log, string stage, string source, string kind, IDictionary<string, object?>? fields)
    {
        try
        {
            log.Record(stage, source, kind, fields);
        }
        catch (Exception)
        {
            // 故意忽略：恢复义务不因日志失败而改变。
        }
    }

    private static (int HResult, ITsfProfileBackend? Backend) CreateComBackend()
    {
        var hr = CreateManager(out var manager);
        if (hr < 0 || manager == IntPtr.Zero)
        {
            return (hr, null);
        }

        return (hr, new ComProfileBackend(manager));
    }

    /// <summary>真实 COM 后端：所有调用都针对同一个 ITfInputProcessorProfileMgr 实例。</summary>
    private sealed class ComProfileBackend : ITsfProfileBackend
    {
        private IntPtr _manager;

        internal ComProfileBackend(IntPtr manager) => _manager = manager;

        public int GetActiveProfile(out InputProcessorProfile profile)
        {
            var category = GuidTfCatTipKeyboard;
            return Method<GetActiveProfileDelegate>(_manager, 10)(_manager, ref category, out profile);
        }

        public int GetProfile(
            uint profileType, ushort langid, Guid clsid, Guid profileGuid, out InputProcessorProfile profile)
        {
            var c = clsid;
            var p = profileGuid;
            return Method<GetProfileDelegate>(_manager, 5)(
                _manager, profileType, langid, ref c, ref p, IntPtr.Zero, out profile);
        }

        public int ActivateProfile(
            uint profileType, ushort langid, Guid clsid, Guid profileGuid, IntPtr hkl, uint flags)
        {
            var c = clsid;
            var p = profileGuid;
            return Method<ActivateProfileDelegate>(_manager, 3)(
                _manager, profileType, langid, ref c, ref p, hkl, flags);
        }

        public int DeactivateProfile(
            uint profileType, ushort langid, Guid clsid, Guid profileGuid, IntPtr hkl, uint flags)
        {
            var c = clsid;
            var p = profileGuid;
            return Method<ActivateProfileDelegate>(_manager, 4)(
                _manager, profileType, langid, ref c, ref p, hkl, flags);
        }

        public void Release()
        {
            if (_manager == IntPtr.Zero)
            {
                return;
            }

            var manager = _manager;
            _manager = IntPtr.Zero;
            Marshal.Release(manager);
        }
    }

    /// <summary>只读查询当前活动的键盘输入法 profile，并记录系统报告的实际身份。</summary>
    internal static (int HResult, InputProcessorProfile Profile) ReadActiveProfile(IProbeEventLog log, string label)
    {
        var (hrCreate, backend) = CreateComBackend();
        if (hrCreate < 0 || backend is null)
        {
            SafeLog(log, "tsf", "msctf-api", "active-profile-unavailable", new Dictionary<string, object?>
            {
                ["label"] = label,
                ["stage"] = "cocreate",
                ["hresult"] = $"0x{hrCreate:X8}",
                ["clsid"] = ClsidInputProcessorProfiles.ToString(),
                ["iid"] = IidInputProcessorProfileMgr.ToString(),
            });
            return (hrCreate, default);
        }

        try
        {
            var hr = backend.GetActiveProfile(out var profile);
            SafeLog(log, "tsf", "msctf-api", "active-profile", new Dictionary<string, object?>
            {
                ["label"] = label,
                ["stage"] = "GetActiveProfile",
                ["hresult"] = $"0x{hr:X8}",
                ["succeeded"] = hr >= 0,
                ["profile_type"] = profile.ProfileTypeName,
                ["langid"] = $"0x{profile.LangId:X4}",
                ["clsid"] = profile.Clsid.ToString(),
                ["profile_guid"] = profile.ProfileGuid.ToString(),
                ["catid"] = profile.CategoryGuid.ToString(),
                ["hkl"] = $"0x{profile.Hkl.ToInt64():X}",
                ["substitute_hkl"] = $"0x{profile.SubstituteHkl.ToInt64():X}",
                ["caps"] = $"0x{profile.Caps:X}",
                ["flags"] = $"0x{profile.Flags:X}",
                ["flags_active_profile"] = profile.IsActive,
                ["flags_enabled_profile"] = profile.IsEnabled,
                ["current_keyboard_layout"] = $"0x{NativeMethods.GetKeyboardLayout(0).ToInt64():X8}",
                ["unit"] = "TSF TF_INPUTPROCESSORPROFILE",
                ["identity_source"] = "actual active profile from GetActiveProfile; the --profile argument and ImmGetDescription are not used as identity",
            });
            return (hr, profile);
        }
        catch (Exception ex)
        {
            SafeLog(log, "tsf", "msctf-api", "active-profile-unavailable", new Dictionary<string, object?>
            {
                ["label"] = label,
                ["stage"] = "invoke",
                ["exception"] = $"{ex.GetType().Name}: {ex.Message}",
            });
            return (EFail, default);
        }
        finally
        {
            backend.Release();
        }
    }

    /// <summary>读取指定 profile 的注册状态（flags 的状态位 0x2 用于判断是否启用）。</summary>
    internal static (int HResult, InputProcessorProfile Profile) ReadProfile(
        IProbeEventLog log, string label, Guid clsid, Guid profileGuid, ushort langid)
    {
        var (hrCreate, backend) = CreateComBackend();
        if (hrCreate < 0 || backend is null)
        {
            SafeLog(log, "tsf", "msctf-api", "profile-unavailable", new Dictionary<string, object?>
            {
                ["label"] = label,
                ["stage"] = "cocreate",
                ["hresult"] = $"0x{hrCreate:X8}",
            });
            return (hrCreate, default);
        }

        try
        {
            var hr = backend.GetProfile(TfProfileTypeInputProcessor, langid, clsid, profileGuid, out var profile);
            SafeLog(log, "tsf", "msctf-api", "profile-state", new Dictionary<string, object?>
            {
                ["label"] = label,
                ["stage"] = "GetProfile",
                ["hresult"] = $"0x{hr:X8}",
                ["succeeded"] = hr >= 0,
                ["langid"] = $"0x{langid:X4}",
                ["clsid"] = clsid.ToString(),
                ["profile_guid"] = profileGuid.ToString(),
                ["flags"] = $"0x{profile.Flags:X}",
                ["flags_active_profile"] = profile.IsActive,
                ["flags_enabled_profile"] = profile.IsEnabled,
                ["unit"] = "TSF TF_INPUTPROCESSORPROFILE",
            });
            return (hr, profile);
        }
        catch (Exception ex)
        {
            SafeLog(log, "tsf", "msctf-api", "profile-unavailable", new Dictionary<string, object?>
            {
                ["label"] = label,
                ["stage"] = "invoke",
                ["exception"] = $"{ex.GetType().Name}: {ex.Message}",
            });
            return (EFail, default);
        }
        finally
        {
            backend.Release();
        }
    }

    /// <summary>
    /// <see cref="TryCreate"/> 的结果。
    /// </summary>
    /// <param name="Scope">激活成功且恢复义务尚未履行时非空；调用方 <c>Dispose</c> 后读取 <see cref="TemporaryProfileScope.Restored"/>。</param>
    /// <param name="Activated">激活已按实际活动身份核对通过。</param>
    /// <param name="ConfigurationTouched">是否尝试过原生配置变更（即是否建立了恢复义务）。</param>
    /// <param name="RestoreVerified">
    /// 恢复回读结论：<c>true</c>=回读匹配，<c>false</c>=回读不匹配，
    /// <c>null</c>=未执行恢复（未变更配置，或恢复义务仍挂在存活的 <see cref="Scope"/> 上等待 Dispose）。
    /// 绝不以 <c>true</c> 表示“稍后会恢复”或“无需恢复”。
    /// </param>
    /// <param name="Failure">失败说明（不为空表示配置可能仍处于临时状态或激活未核对通过）。</param>
    internal sealed record TemporaryProfileAttempt(
        TemporaryProfileScope? Scope,
        bool Activated,
        bool ConfigurationTouched,
        bool? RestoreVerified,
        string Failure);

    /// <summary>
    /// 受控临时启用一个已安装输入法 profile，并在 <see cref="IDisposable.Dispose"/> 中恢复并回读验证。
    /// </summary>
    internal sealed class TemporaryProfileScope : IDisposable
    {
        private readonly IProbeEventLog _log;
        private readonly string _label;
        private readonly ITsfProfileBackend _backend;
        private readonly Guid _clsid;
        private readonly Guid _profileGuid;
        private readonly ushort _langid;
        private readonly InputProcessorProfile _originalActive;
        private readonly InputProcessorProfile _targetBefore;
        private bool _restoreObligation;
        private bool _disposed;

        private TemporaryProfileScope(
            IProbeEventLog log, string label, ITsfProfileBackend backend, Guid clsid, Guid profileGuid, ushort langid,
            InputProcessorProfile originalActive, InputProcessorProfile targetBefore)
        {
            _log = log;
            _label = label;
            _backend = backend;
            _clsid = clsid;
            _profileGuid = profileGuid;
            _langid = langid;
            _originalActive = originalActive;
            _targetBefore = targetBefore;
        }

        /// <summary>激活已按实际活动身份核对通过。</summary>
        internal bool Activated { get; private set; }

        /// <summary>恢复已执行且回读验证通过；只有真正回读匹配才为 true。</summary>
        internal bool Restored { get; private set; }

        /// <summary>恢复失败时的说明（不为空表示配置可能仍处于临时状态）。</summary>
        internal string Failure { get; private set; } = string.Empty;

        /// <summary>日志写入，但日志抛出绝不改变恢复控制流。</summary>
        private void SafeRecord(string stage, string source, string kind, IDictionary<string, object?>? fields)
            => SafeLog(_log, stage, source, kind, fields);

        /// <summary>
        /// 快照并临时启用目标 profile。
        /// </summary>
        /// <remarks>
        /// 恢复义务在调用任何原生变更之前建立：激活失败、激活部分成功、日志抛异常或后端抛异常，
        /// 都会走同一恢复路径并回读验证。返回的 <see cref="TemporaryProfileAttempt.RestoreVerified"/>
        /// 只在本次调用真正执行过恢复回读时取 true/false；成功创建但 scope 仍存活时为 null
        /// （恢复尚未执行），调用方必须 Dispose 后读取 <see cref="TemporaryProfileScope.Restored"/>。
        /// 未变更配置的失败路径同样返回 null，不谎称“已回读验证”。
        /// </remarks>
        internal static TemporaryProfileAttempt TryCreate(
            IProbeEventLog log, string label, Guid clsid, Guid profileGuid, ushort langid)
            => TryCreate(log, label, clsid, profileGuid, langid, DefaultBackendFactory);

        internal static TemporaryProfileAttempt TryCreate(
            IProbeEventLog log, string label, Guid clsid, Guid profileGuid, ushort langid,
            TsfProfileBackendFactory backendFactory)
        {
            var (hrCreate, backend) = backendFactory();
            if (hrCreate < 0 || backend is null)
            {
                SafeLog(log, "tsf", "msctf-api", "temporary-profile-abort", new Dictionary<string, object?>
                {
                    ["label"] = label,
                    ["stage"] = "cocreate",
                    ["hresult"] = $"0x{hrCreate:X8}",
                    ["configuration_touched"] = false,
                });
                return new TemporaryProfileAttempt(null, false, false, null,
                    $"CoCreateInstance hr=0x{hrCreate:X8}; no configuration was touched");
            }

            TemporaryProfileScope? scope = null;
            try
            {
                var hrActive = backend.GetActiveProfile(out var original);
                if (hrActive < 0 || original.ProfileType is not (TfProfileTypeInputProcessor or TfProfileTypeKeyboardLayout))
                {
                    SafeLog(log, "tsf", "msctf-api", "temporary-profile-abort", new Dictionary<string, object?>
                    {
                        ["label"] = label,
                        ["stage"] = "snapshot-active",
                        ["hresult"] = $"0x{hrActive:X8}",
                        ["profile_type"] = original.ProfileTypeName,
                        ["configuration_touched"] = false,
                    });
                    backend.Release();
                    return new TemporaryProfileAttempt(null, false, false, null,
                        $"active-profile snapshot failed hr=0x{hrActive:X8}; no configuration was touched");
                }

                var hrProfile = backend.GetProfile(
                    TfProfileTypeInputProcessor, langid, clsid, profileGuid, out var targetBefore);
                if (hrProfile < 0)
                {
                    SafeLog(log, "tsf", "msctf-api", "temporary-profile-abort", new Dictionary<string, object?>
                    {
                        ["label"] = label,
                        ["stage"] = "snapshot-target",
                        ["hresult"] = $"0x{hrProfile:X8}",
                        ["clsid"] = clsid.ToString(),
                        ["profile_guid"] = profileGuid.ToString(),
                        ["configuration_touched"] = false,
                    });
                    backend.Release();
                    return new TemporaryProfileAttempt(null, false, false, null,
                        $"target-profile snapshot failed hr=0x{hrProfile:X8}; no configuration was touched");
                }

                SafeLog(log, "tsf", "msctf-api", "temporary-profile-snapshot", new Dictionary<string, object?>
                {
                    ["label"] = label,
                    ["original_active_clsid"] = original.Clsid.ToString(),
                    ["original_active_guid"] = original.ProfileGuid.ToString(),
                    ["original_active_type"] = original.ProfileTypeName,
                    ["original_active_hkl"] = $"0x{original.Hkl.ToInt64():X}",
                    ["target_flags_before"] = $"0x{targetBefore.Flags:X}",
                    ["target_was_active"] = targetBefore.IsActive,
                    ["target_was_enabled"] = targetBefore.IsEnabled,
                    ["flags_state_bits"] = "0x1=TF_IPP_FLAG_ACTIVE, 0x2=TF_IPP_FLAG_ENABLED",
                    ["scope"] = "snapshot only; no configuration changed yet",
                });

                scope = new TemporaryProfileScope(
                    log, label, backend, clsid, profileGuid, langid, original, targetBefore);
                if (!scope.ActivateAndVerify(out var activationFailure))
                {
                    // 激活未核对通过：恢复义务已建立，必须恢复并回读；结果按实际回读报告。
                    scope.Dispose();
                    return new TemporaryProfileAttempt(
                        null, false, true, scope.Restored,
                        $"activation not verified: {activationFailure}; {scope.Failure}");
                }

                var created = scope;
                scope = null;
                // scope 仍存活：恢复尚未执行，RestoreVerified 必须为 null，调用方 Dispose 后读 Scope.Restored。
                return new TemporaryProfileAttempt(created, true, true, null, string.Empty);
            }
            catch (Exception ex)
            {
                var failure = $"{ex.GetType().Name}: {ex.Message}";
                SafeLog(log, "tsf", "msctf-api", "temporary-profile-abort", new Dictionary<string, object?>
                {
                    ["label"] = label,
                    ["stage"] = "exception",
                    ["exception"] = failure,
                });

                if (scope is not null)
                {
                    // 恢复义务可能已经建立：走统一恢复路径并回读，绝不只 Release。
                    scope.Dispose();
                    return new TemporaryProfileAttempt(
                        null, false, true, scope.Restored, $"{failure}; {scope.Failure}");
                }

                backend.Release();
                return new TemporaryProfileAttempt(null, false, false, null,
                    $"{failure}; no configuration was touched");
            }
        }

        /// <summary>
        /// 尝试原生激活并核对实际活动身份。进入本方法前就建立恢复义务，因此无论返回 true/false，
        /// 调用方都必须执行恢复并回读。
        /// </summary>
        private bool ActivateAndVerify(out string failure)
        {
            failure = string.Empty;
            // 关键顺序：恢复义务先于任何原生变更尝试，避免“激活已改状态但日志/校验抛错”导致漏恢复。
            _restoreObligation = true;

            var flags = TfIppmForProcess | (_targetBefore.IsEnabled ? 0u : TfIppmEnableProfile);
            int hr;
            try
            {
                hr = _backend.ActivateProfile(
                    TfProfileTypeInputProcessor, _langid, _clsid, _profileGuid, IntPtr.Zero, flags);
            }
            catch (Exception ex)
            {
                failure = $"{ex.GetType().Name}: {ex.Message}";
                SafeRecord("tsf", "msctf-api", "temporary-profile-activate", new Dictionary<string, object?>
                {
                    ["label"] = _label,
                    ["stage"] = "ActivateProfile",
                    ["exception"] = failure,
                    ["flags"] = $"0x{flags:X8}",
                    ["configuration_touched_maybe"] = true,
                });
                return false;
            }

            SafeRecord("tsf", "msctf-api", "temporary-profile-activate", new Dictionary<string, object?>
            {
                ["label"] = _label,
                ["stage"] = "ActivateProfile",
                ["hresult"] = $"0x{hr:X8}",
                ["succeeded"] = hr == 0,
                ["clsid"] = _clsid.ToString(),
                ["profile_guid"] = _profileGuid.ToString(),
                ["langid"] = $"0x{_langid:X4}",
                ["flags"] = $"0x{flags:X8}",
                ["target_was_enabled"] = _targetBefore.IsEnabled,
                ["scope"] = "FORPROCESS only (no FORSESSION); session default input method is not modified",
            });

            // 激活可能已部分改动启用状态，因此不因 hr 非 S_OK 就跳过恢复；仅在此处返回，Dispose 负责恢复。
            if (hr != 0)
            {
                failure = hr == 1
                    ? "ActivateProfile returned S_FALSE (the language profile is not enabled)"
                    : $"ActivateProfile hr=0x{hr:X8}";
                return false;
            }

            int hrActive;
            InputProcessorProfile now;
            try
            {
                hrActive = _backend.GetActiveProfile(out now);
            }
            catch (Exception ex)
            {
                failure = $"GetActiveProfile threw {ex.GetType().Name}: {ex.Message}";
                return false;
            }

            // 真实激活身份核对包含类型与语言：只比对 GUID 会把不同语言/类型的活动项误判为已激活。
            var verified = hrActive >= 0
                && now.ProfileType == TfProfileTypeInputProcessor
                && now.LangId == _langid
                && now.Clsid == _clsid
                && now.ProfileGuid == _profileGuid;
            SafeRecord("tsf", "msctf-api", "temporary-profile-activate-verify", new Dictionary<string, object?>
            {
                ["label"] = _label,
                ["stage"] = "GetActiveProfile",
                ["hresult"] = $"0x{hrActive:X8}",
                ["verified_actual_active"] = verified,
                ["expected_profile_type"] = "TF_PROFILETYPE_INPUTPROCESSOR",
                ["expected_langid"] = $"0x{_langid:X4}",
                ["expected_clsid"] = _clsid.ToString(),
                ["expected_profile_guid"] = _profileGuid.ToString(),
                ["actual_profile_type"] = now.ProfileTypeName,
                ["actual_langid"] = $"0x{now.LangId:X4}",
                ["actual_clsid"] = now.Clsid.ToString(),
                ["actual_profile_guid"] = now.ProfileGuid.ToString(),
                ["actual_flags"] = $"0x{now.Flags:X}",
                ["actual_flags_active"] = now.IsActive,
                ["actual_flags_enabled"] = now.IsEnabled,
                ["note"] = verified ? "the requested profile is the actual active profile" : "requested profile is NOT the actual active profile",
            });

            if (!verified)
            {
                failure = $"active profile identity mismatch (hr=0x{hrActive:X8})";
                return false;
            }

            Activated = true;
            return true;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            // 即使 RestoreCore 意外抛出，后端的 COM 引用也必须释放。
            try
            {
                RestoreCore();
            }
            finally
            {
                try
                {
                    _backend.Release();
                }
                catch (Exception)
                {
                    // 释放失败不影响已得到的恢复判据。
                }
            }
        }

        /// <summary>
        /// 恢复义务的实际履行：多个步骤独立执行，任一步失败/异常/日志失败都不阻断其余步骤；
        /// 最终只按回读结果设置 <see cref="Restored"/>。
        /// </summary>
        private void RestoreCore()
        {
            if (!_restoreObligation)
            {
                // 从未尝试任何原生变更：无需恢复，也不谎称“恢复了改动”。
                Restored = true;
                SafeRecord("tsf", "msctf-api", "temporary-profile-restored", new Dictionary<string, object?>
                {
                    ["label"] = _label,
                    ["RESTORED"] = true,
                    ["configuration_touched"] = false,
                    ["note"] = "no native change was attempted, so there was nothing to restore",
                    ["original_active_clsid"] = _originalActive.Clsid.ToString(),
                    ["original_active_guid"] = _originalActive.ProfileGuid.ToString(),
                    ["target_flags_before"] = $"0x{_targetBefore.Flags:X}",
                });
                return;
            }

            // 步骤 1：如果目标原本未启用（我们可能启用了它），恢复为未启用。
            var disableAttempted = false;
            var hrDisable = int.MinValue;
            if (!_targetBefore.IsEnabled)
            {
                disableAttempted = true;
                try
                {
                    hrDisable = _backend.DeactivateProfile(
                        TfProfileTypeInputProcessor, _langid, _clsid, _profileGuid, IntPtr.Zero,
                        TfIppmForProcess | TfIppmDisableProfile);
                }
                catch (Exception ex)
                {
                    hrDisable = EFail;
                    Failure = $"DeactivateProfile threw {ex.GetType().Name}: {ex.Message}";
                }

                SafeRecord("tsf", "msctf-api", "temporary-profile-disable-target", new Dictionary<string, object?>
                {
                    ["label"] = _label,
                    ["stage"] = "DeactivateProfile",
                    ["hresult"] = $"0x{hrDisable:X8}",
                    ["succeeded"] = hrDisable == 0,
                    ["flags"] = $"0x{TfIppmForProcess | TfIppmDisableProfile:X8}",
                    ["note"] = "restores the target profile's enabled flag that this scope temporarily changed",
                });
            }

            // 步骤 2：恢复原活动 profile（独立于步骤 1，即使步骤 1 失败/抛异常也执行）。
            var hrRestore = int.MinValue;
            try
            {
                var oc = _originalActive.Clsid;
                var op = _originalActive.ProfileGuid;
                var hkl = _originalActive.ProfileType == TfProfileTypeKeyboardLayout ? _originalActive.Hkl : IntPtr.Zero;
                hrRestore = _backend.ActivateProfile(
                    _originalActive.ProfileType, _originalActive.LangId, oc, op, hkl, TfIppmForProcess);
            }
            catch (Exception ex)
            {
                hrRestore = EFail;
                SafeRecord("tsf", "msctf-api", "temporary-profile-restore-active", new Dictionary<string, object?>
                {
                    ["label"] = _label,
                    ["stage"] = "ActivateProfile(original)",
                    ["exception"] = $"{ex.GetType().Name}: {ex.Message}",
                });
            }

            // 步骤 3：回读验证（独立于前两步；即使它们抛异常也要读）。
            var targetReadOk = false;
            var targetAfter = default(InputProcessorProfile);
            try
            {
                targetReadOk = _backend.GetProfile(
                    TfProfileTypeInputProcessor, _langid, _clsid, _profileGuid, out targetAfter) >= 0;
            }
            catch (Exception ex)
            {
                SafeRecord("tsf", "msctf-api", "temporary-profile-restore-readback", new Dictionary<string, object?>
                {
                    ["label"] = _label,
                    ["stage"] = "GetProfile(readback)",
                    ["exception"] = $"{ex.GetType().Name}: {ex.Message}",
                });
            }

            var activeReadOk = false;
            var activeAfter = default(InputProcessorProfile);
            try
            {
                activeReadOk = _backend.GetActiveProfile(out activeAfter) >= 0;
            }
            catch (Exception ex)
            {
                SafeRecord("tsf", "msctf-api", "temporary-profile-restore-readback", new Dictionary<string, object?>
                {
                    ["label"] = _label,
                    ["stage"] = "GetActiveProfile(readback)",
                    ["exception"] = $"{ex.GetType().Name}: {ex.Message}",
                });
            }

            var targetRestored = targetReadOk && targetAfter.IsEnabled == _targetBefore.IsEnabled;
            var activeRestored = activeReadOk && SameActiveIdentity(activeAfter, _originalActive);
            var warnings = Failure;
            Restored = targetRestored && activeRestored;
            Failure = Restored
                ? string.Empty
                : (warnings.Length > 0 ? warnings + "; " : string.Empty)
                    + $"restore hr=0x{hrRestore:X8}, disable={(!disableAttempted ? "not-needed" : $"0x{hrDisable:X8}")}, "
                    + $"targetReadOk={targetReadOk}, targetEnabledBefore={_targetBefore.IsEnabled}, "
                    + $"targetFlagsAfter=0x{targetAfter.Flags:X}, activeReadOk={activeReadOk}, "
                    + $"activeAfter={activeAfter.ProfileTypeName}/0x{activeAfter.LangId:X4}/"
                    + $"{activeAfter.Clsid}/{activeAfter.ProfileGuid}/hkl=0x{EffectiveHkl(activeAfter).ToInt64():X}, "
                    + $"expectedActive={_originalActive.ProfileTypeName}/0x{_originalActive.LangId:X4}/"
                    + $"{_originalActive.Clsid}/{_originalActive.ProfileGuid}/hkl=0x{EffectiveHkl(_originalActive).ToInt64():X}";

            SafeRecord("tsf", "msctf-api", "temporary-profile-restored", new Dictionary<string, object?>
            {
                ["label"] = _label,
                ["RESTORED"] = Restored,
                ["configuration_touched"] = true,
                ["restore_hresult"] = $"0x{hrRestore:X8}",
                ["disable_target_hresult"] = disableAttempted ? $"0x{hrDisable:X8}" : "(not needed: target was already enabled before the scope)",
                ["target_was_enabled"] = _targetBefore.IsEnabled,
                ["target_enabled_after"] = targetReadOk && targetAfter.IsEnabled,
                ["target_read_ok"] = targetReadOk,
                ["original_active_type"] = _originalActive.ProfileTypeName,
                ["original_active_langid"] = $"0x{_originalActive.LangId:X4}",
                ["original_active_clsid"] = _originalActive.Clsid.ToString(),
                ["original_active_guid"] = _originalActive.ProfileGuid.ToString(),
                ["original_active_hkl"] = $"0x{EffectiveHkl(_originalActive).ToInt64():X}",
                ["active_after_type"] = activeAfter.ProfileTypeName,
                ["active_after_langid"] = $"0x{activeAfter.LangId:X4}",
                ["active_after_clsid"] = activeAfter.Clsid.ToString(),
                ["active_after_guid"] = activeAfter.ProfileGuid.ToString(),
                ["active_after_hkl"] = $"0x{EffectiveHkl(activeAfter).ToInt64():X}",
                ["active_identity_matched"] = activeRestored,
                ["active_read_ok"] = activeReadOk,
                ["restore_warnings"] = warnings,
                ["failure"] = Failure,
                ["scope"] = "FORPROCESS only; no FORSESSION and no default-input-method change",
            });
        }

        /// <summary>键盘布局身份的有效 HKL：优先 Hkl，为空时用 SubstituteHkl。</summary>
        private static IntPtr EffectiveHkl(in InputProcessorProfile profile)
            => profile.Hkl != IntPtr.Zero ? profile.Hkl : profile.SubstituteHkl;

        /// <summary>
        /// 活动身份是否与原始快照一致。只比较 CLSID/ProfileGuid 不够：键盘布局这两者常为空，
        /// 不同语言/HKL 会被误判为恢复成功。这里比较 ProfileType、LangId；
        /// 键盘布局比较 HKL（必要时 SubstituteHkl），输入处理器比较 CLSID/ProfileGuid。
        /// </summary>
        private static bool SameActiveIdentity(in InputProcessorProfile actual, in InputProcessorProfile expected)
        {
            if (actual.ProfileType != expected.ProfileType || actual.LangId != expected.LangId)
            {
                return false;
            }

            return expected.ProfileType == TfProfileTypeKeyboardLayout
                ? EffectiveHkl(actual) == EffectiveHkl(expected)
                : actual.Clsid == expected.Clsid && actual.ProfileGuid == expected.ProfileGuid;
        }
    }
}
