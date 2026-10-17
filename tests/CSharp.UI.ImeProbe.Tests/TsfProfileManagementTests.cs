using CSharp.UI.ImeProbe;
using System.Text.Json;
using Xunit;

namespace CSharp.UI.ImeProbe.Tests;

/// <summary>
/// 受控临时 profile 工具的状态位语义与恢复义务测试。全部使用注入的假后端，不触碰真实 Windows 输入法配置。
/// </summary>
public sealed class TsfProfileManagementTests
{
    private const uint ForProcess = 0x10000000;
    private static readonly Guid OriginalClsid = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OriginalProfile = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid TargetClsid = new("33333333-3333-3333-3333-333333333333");
    private static readonly Guid TargetProfile = new("44444444-4444-4444-4444-444444444444");

    [Theory]
    [InlineData(0u, false, false)]
    [InlineData(1u, true, false)]
    [InlineData(2u, false, true)]
    [InlineData(3u, true, true)]
    public void ProfileFlags_ActiveIs0x1AndEnabledIs0x2(uint flags, bool active, bool enabled)
    {
        var profile = new TsfProfile.InputProcessorProfile { Flags = flags };

        Assert.Equal(active, profile.IsActive);
        Assert.Equal(enabled, profile.IsEnabled);
    }

    [Fact]
    public void ProfileFlags_EnabledProfileIsNotTreatedAsDisabled()
    {
        // 回归：flags=2（已启用但未活动）曾因复用 ActivateProfile 的参数位 0x1 被当成禁用。
        var enabledButNotActive = new TsfProfile.InputProcessorProfile { Flags = 2 };
        Assert.True(enabledButNotActive.IsEnabled);
        Assert.False(enabledButNotActive.IsActive);
    }

    [Fact]
    public void TryCreate_Succeeds_WhenActivationIdentityMatches()
    {
        var backend = FakeBackend.Create(targetEnabled: false);
        var log = new RecordingLog();

        var attempt = TsfProfile.TemporaryProfileScope.TryCreate(
            log, "test", TargetClsid, TargetProfile, 0x0804, () => (0, backend));

        Assert.True(attempt.Activated);
        Assert.NotNull(attempt.Scope);
        Assert.True(attempt.Scope!.Activated);
        Assert.True(backend.Target.IsEnabled);
        Assert.Equal(TargetClsid, backend.Active.Clsid);

        attempt.Scope.Dispose();

        Assert.True(attempt.Scope.Restored, attempt.Scope.Failure);
        Assert.Empty(attempt.Scope.Failure);
        Assert.True(backend.Released);
        Assert.Single(backend.DeactivateCalls);
        Assert.Equal((OriginalClsid, OriginalProfile), (backend.Active.Clsid, backend.Active.ProfileGuid));
        Assert.False(backend.Target.IsEnabled);
    }

    [Fact]
    public void TryCreate_Success_DoesNotClaimRestoreVerified_UntilScopeIsDisposed()
    {
        var backend = FakeBackend.Create(targetEnabled: false);
        var log = new RecordingLog();

        var attempt = TsfProfile.TemporaryProfileScope.TryCreate(
            log, "test", TargetClsid, TargetProfile, 0x0804, () => (0, backend));

        // scope 仍存活：恢复尚未执行，RestoreVerified 必须为 null，Active 状态是临时的。
        Assert.True(attempt.Activated);
        Assert.True(attempt.ConfigurationTouched);
        Assert.Null(attempt.RestoreVerified);
        Assert.NotNull(attempt.Scope);
        Assert.False(attempt.Scope!.Restored);
        Assert.Empty(backend.DeactivateCalls);
        Assert.Equal(TargetClsid, backend.Active.Clsid);
        Assert.False(backend.Released);

        attempt.Scope.Dispose();

        // Dispose 后恢复义务才履行：Scope.Restored 反映真实回读。
        Assert.True(attempt.Scope.Restored, attempt.Scope.Failure);
        Assert.True(backend.Released);
        Assert.Single(backend.DeactivateCalls);
        Assert.Equal((OriginalClsid, OriginalProfile), (backend.Active.Clsid, backend.Active.ProfileGuid));
    }

    [Fact]
    public void TryCreate_DoesNotDisableTarget_ThatWasAlreadyEnabledButNotActive()
    {
        // 原 flags=2：scope 未改变启用位，恢复时不得调用 DISABLEPROFILE。
        var backend = FakeBackend.Create(targetEnabled: true);
        var log = new RecordingLog();

        var attempt = TsfProfile.TemporaryProfileScope.TryCreate(
            log, "test", TargetClsid, TargetProfile, 0x0804, () => (0, backend));

        Assert.True(attempt.Activated);
        var activationFlags = backend.ActivateCalls[0].Flags;
        Assert.Equal(ForProcess, activationFlags); // 已启用，因此不请求 ENABLEPROFILE

        attempt.Scope!.Dispose();

        Assert.True(attempt.Scope.Restored, attempt.Scope.Failure);
        Assert.Empty(backend.DeactivateCalls);
        Assert.True(backend.Target.IsEnabled);
        Assert.Equal((OriginalClsid, OriginalProfile), (backend.Active.Clsid, backend.Active.ProfileGuid));
    }

    [Fact]
    public void Restore_VerifiesKeyboardLayoutIdentity_ByHkl()
    {
        // 原活动项为键盘布局：CLSID/ProfileGuid 均为空，身份只能靠 LangId + HKL。
        var originalHkl = new IntPtr(0x04090409);
        var backend = FakeBackend.CreateKeyboardLayoutActive(originalHkl, targetEnabled: false);
        var log = new RecordingLog();

        var attempt = TsfProfile.TemporaryProfileScope.TryCreate(
            log, "test", TargetClsid, TargetProfile, 0x0804, () => (0, backend));
        Assert.True(attempt.Activated);

        attempt.Scope!.Dispose();

        Assert.True(attempt.Scope.Restored, attempt.Scope.Failure);
        // 恢复请求带上了原键盘布局 HKL，回读也匹配，所以 FakeBackend 的 HKL 模拟正确。
        Assert.Equal(originalHkl, backend.ActivateCalls[^1].Hkl);
        Assert.Equal(originalHkl, backend.Active.Hkl);
    }

    [Fact]
    public void Restore_DoesNotClaimRestored_WhenKeyboardLayoutReadbackIsAnotherHkl()
    {
        var originalHkl = new IntPtr(0x04090409);
        var otherHkl = new IntPtr(0x04070407);
        var backend = FakeBackend.CreateKeyboardLayoutActive(originalHkl, targetEnabled: false);
        var log = new RecordingLog();

        var attempt = TsfProfile.TemporaryProfileScope.TryCreate(
            log, "test", TargetClsid, TargetProfile, 0x0804, () => (0, backend));
        Assert.True(attempt.Activated);

        // 恢复回读到另一 HKL：CLSID/ProfileGuid 都为空，只比较 GUID 会误判成功。
        backend.ActiveReadbackOverride = FakeBackend.KeyboardLayout(otherHkl);
        attempt.Scope!.Dispose();

        Assert.False(attempt.Scope.Restored);
        Assert.NotEmpty(attempt.Scope.Failure);
        // 恢复仍然请求了原 HKL，只是回读不一致。
        Assert.Equal(originalHkl, backend.ActivateCalls[^1].Hkl);
    }

    [Fact]
    public void Restore_DoesNotClaimRestored_WhenActiveLangIdDiffers()
    {
        var backend = FakeBackend.Create(targetEnabled: false);
        var log = new RecordingLog();

        var attempt = TsfProfile.TemporaryProfileScope.TryCreate(
            log, "test", TargetClsid, TargetProfile, 0x0804, () => (0, backend));
        Assert.True(attempt.Activated);

        // GUID 完全一致，只有 LangId 不同：身份比较必须包含 LangId。
        backend.ActiveReadbackOverride = new TsfProfile.InputProcessorProfile
        {
            ProfileType = 1,
            LangId = 0x0409,
            Clsid = OriginalClsid,
            ProfileGuid = OriginalProfile,
            Flags = TsfProfile.TfIppFlagEnabled | TsfProfile.TfIppFlagActive,
        };
        attempt.Scope!.Dispose();

        Assert.False(attempt.Scope.Restored);
        Assert.Equal(0x0804, backend.Active.LangId); // 恢复请求仍是原语言
    }

    [Fact]
    public void Restore_StillRuns_WhenEveryLogWriteThrows()
    {
        var backend = FakeBackend.Create(targetEnabled: false);
        var log = new ThrowingLog();

        var attempt = TsfProfile.TemporaryProfileScope.TryCreate(
            log, "test", TargetClsid, TargetProfile, 0x0804, () => (0, backend));

        Assert.True(attempt.Activated);
        attempt.Scope!.Dispose();

        Assert.True(attempt.Scope.Restored, attempt.Scope.Failure);
        // 恢复步骤仍然全部执行：禁用目标 + 恢复原活动 + 两次回读。
        Assert.Single(backend.DeactivateCalls);
        Assert.Equal(2, backend.ActivateCalls.Count);
        Assert.Equal(2, backend.GetProfileCalls);
        Assert.Equal(3, backend.GetActiveCalls); // 快照原活动 + 激活后核对 + 恢复回读
    }

    [Fact]
    public void TryCreate_RestoresAndVerifies_WhenActivationFailedButChangedEnabledState()
    {
        // 原生激活返回失败，却已部分改动启用状态：不能预设“绝无改动”，必须恢复并回读。
        var backend = FakeBackend.Create(targetEnabled: false);
        backend.ActivateHr = unchecked((int)0x80004005);
        backend.MutateTargetOnActivate = true;
        var log = new RecordingLog();

        var attempt = TsfProfile.TemporaryProfileScope.TryCreate(
            log, "test", TargetClsid, TargetProfile, 0x0804, () => (0, backend));

        Assert.False(attempt.Activated);
        Assert.Null(attempt.Scope);
        Assert.True(attempt.ConfigurationTouched);
        Assert.True(attempt.RestoreVerified == true, attempt.Failure);
        Assert.Single(backend.DeactivateCalls);
        Assert.False(backend.Target.IsEnabled);
        Assert.Equal((OriginalClsid, OriginalProfile), (backend.Active.Clsid, backend.Active.ProfileGuid));
        Assert.True(backend.Released);
    }

    [Fact]
    public void TryCreate_RecoversAndReadsBack_WhenActivateProfileReturnsSFalse()
    {
        // 微软官方语义：S_FALSE 表示 profile 未启用，不是激活成功，但仍可能已改动配置。
        var backend = FakeBackend.Create(targetEnabled: false);
        backend.ActivateHr = 1; // S_FALSE
        var log = new RecordingLog();

        var attempt = TsfProfile.TemporaryProfileScope.TryCreate(
            log, "test", TargetClsid, TargetProfile, 0x0804, () => (0, backend));

        Assert.False(attempt.Activated);
        Assert.Null(attempt.Scope);
        Assert.True(attempt.ConfigurationTouched);
        Assert.True(attempt.RestoreVerified == true, attempt.Failure);
        Assert.Contains("S_FALSE", attempt.Failure);
        Assert.True(backend.Released);
        Assert.Single(backend.DeactivateCalls);
        Assert.Equal(2, backend.ActivateCalls.Count); // 激活尝试 + 恢复原活动
    }

    [Fact]
    public void TryCreate_RecoversAndReadsBack_WhenActivateProfileThrows()
    {
        // 激活调用抛异常：异常不能变成成功，也不能跳过恢复；仍按回读报告。
        var backend = FakeBackend.Create(targetEnabled: false);
        backend.ActivateThrows = true;
        var log = new RecordingLog();

        var attempt = TsfProfile.TemporaryProfileScope.TryCreate(
            log, "test", TargetClsid, TargetProfile, 0x0804, () => (0, backend));

        Assert.False(attempt.Activated);
        Assert.Null(attempt.Scope);
        Assert.True(attempt.ConfigurationTouched);
        Assert.True(attempt.RestoreVerified == true, attempt.Failure);
        Assert.Contains("InvalidOperationException", attempt.Failure);
        Assert.True(backend.Released);
        Assert.Single(backend.DeactivateCalls);
        Assert.Equal(2, backend.ActivateCalls.Count);
        Assert.Equal((OriginalClsid, OriginalProfile), (backend.Active.Clsid, backend.Active.ProfileGuid));
    }

    [Fact]
    public void Restore_RunsRemainingSteps_WhenDisableThrows_AndDoesNotClaimRestored()
    {
        var backend = FakeBackend.Create(targetEnabled: false);
        var log = new RecordingLog();

        var attempt = TsfProfile.TemporaryProfileScope.TryCreate(
            log, "test", TargetClsid, TargetProfile, 0x0804, () => (0, backend));
        Assert.True(attempt.Activated);

        // 恢复第 1 步抛异常：第 2 步（恢复原活动）与回读仍必须执行。
        backend.DeactivateThrows = true;
        attempt.Scope!.Dispose();

        Assert.False(attempt.Scope.Restored);
        Assert.NotEmpty(attempt.Scope.Failure);
        Assert.Single(backend.DeactivateCalls);
        Assert.Equal(2, backend.ActivateCalls.Count);
        Assert.Equal((OriginalClsid, OriginalProfile), (backend.Active.Clsid, backend.Active.ProfileGuid));
        Assert.True(backend.GetProfileCalls >= 1);
        Assert.True(backend.GetActiveCalls >= 1);
    }

    [Fact]
    public void TryCreate_ReleasesBackend_WhenSnapshotThrows()
    {
        var backend = FakeBackend.Create(targetEnabled: false);
        backend.GetActiveThrows = true;
        var log = new RecordingLog();

        var attempt = TsfProfile.TemporaryProfileScope.TryCreate(
            log, "test", TargetClsid, TargetProfile, 0x0804, () => (0, backend));

        Assert.False(attempt.Activated);
        Assert.Null(attempt.Scope);
        Assert.False(attempt.ConfigurationTouched);
        // 未变更配置：恢复未执行，不能写成“回读已验证”。
        Assert.Null(attempt.RestoreVerified);
        Assert.True(backend.Released);
        Assert.Empty(backend.ActivateCalls);
        Assert.Empty(backend.DeactivateCalls);
    }

    [Fact]
    public void TryCreate_ReportsNoChange_WhenBackendCreationFails()
    {
        var log = new RecordingLog();

        var attempt = TsfProfile.TemporaryProfileScope.TryCreate(
            log, "test", TargetClsid, TargetProfile, 0x0804, () => (unchecked((int)0x80040154), null));

        Assert.False(attempt.Activated);
        Assert.False(attempt.ConfigurationTouched);
        // 后端都未创建成功：恢复未执行，RestoreVerified 为 null 而不是 true。
        Assert.Null(attempt.RestoreVerified);
        Assert.Contains("0x80040154", attempt.Failure);
    }

    [Fact]
    public void ProbeLog_RecordsCorrectManagedThreadId_AndGuardsKernel32ByPlatform()
    {
        var path = Path.Combine(Path.GetTempPath(), $"imeprobe-log-{Guid.NewGuid():N}.jsonl");
        try
        {
            using (var log = new ProbeLog(path, echo: false))
            {
                Assert.NotEqual(0, log.ManagedThreadId);
                Assert.Equal(Environment.CurrentManagedThreadId, log.ManagedThreadId);
                if (OperatingSystem.IsWindows())
                {
                    Assert.NotEqual(0u, log.NativeThreadId);
                }
                else
                {
                    // 非 Windows 不调用 kernel32，native tid 为空值且单独断言。
                    Assert.Equal(0u, log.NativeThreadId);
                }

                log.Record("probe", "test", "marker");
            }

            var openLine = File.ReadLines(path).First();
            using var document = JsonDocument.Parse(openLine);
            var root = document.RootElement;
            Assert.Equal(Environment.CurrentManagedThreadId, root.GetProperty("managed_thread_id").GetInt32());
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(0u, root.GetProperty("win32_thread_id").GetUInt32());
            }
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>记录所有日志调用；用于断言恢复路径即使日志失败也会完成。</summary>
    private sealed class RecordingLog : IProbeEventLog    {
        public List<string> Kinds { get; } = [];

        public void Record(string stage, string source, string kind, IDictionary<string, object?>? fields)
            => Kinds.Add(kind);
    }

    /// <summary>每次写入都抛异常：验证“日志抛异常不能阻断恢复”。</summary>
    private sealed class ThrowingLog : IProbeEventLog
    {
        public void Record(string stage, string source, string kind, IDictionary<string, object?>? fields)
            => throw new IOException("fault injection: log write failure");
    }

    /// <summary>注入的原生后端；模拟 Windows 的启用位变化与实际活动身份。</summary>
    private sealed class FakeBackend : ITsfProfileBackend
    {
        public TsfProfile.InputProcessorProfile Active;
        public TsfProfile.InputProcessorProfile Target;
        public int ActivateHr;
        public bool ActivateThrows;
        public bool DeactivateThrows;
        public bool GetActiveThrows;
        public bool MutateTargetOnActivate;

        /// <summary>
        /// 设置后在 <see cref="GetActiveProfile"/> 里替代真实活动项，用于模拟“恢复回读与原始快照不一致”。
        /// 测试应在 TryCreate 之后、Dispose 之前设置，避免影响快照与激活核对。
        /// </summary>
        public TsfProfile.InputProcessorProfile? ActiveReadbackOverride;

        public List<(Guid Clsid, Guid ProfileGuid, IntPtr Hkl, uint Flags)> ActivateCalls { get; } = [];
        public List<(Guid Clsid, Guid ProfileGuid, IntPtr Hkl, uint Flags)> DeactivateCalls { get; } = [];
        public int GetProfileCalls { get; private set; }
        public int GetActiveCalls { get; private set; }
        public bool Released { get; private set; }

        public static FakeBackend Create(bool targetEnabled)
        {
            var backend = new FakeBackend
            {
                Target =
                {
                    ProfileType = 1,
                    LangId = 0x0804,
                    Clsid = TargetClsid,
                    ProfileGuid = TargetProfile,
                    Flags = targetEnabled ? TsfProfile.TfIppFlagEnabled : 0u,
                },
            };
            backend.Active = new TsfProfile.InputProcessorProfile
            {
                ProfileType = 1,
                LangId = 0x0804,
                Clsid = OriginalClsid,
                ProfileGuid = OriginalProfile,
                Flags = TsfProfile.TfIppFlagEnabled | TsfProfile.TfIppFlagActive,
            };
            return backend;
        }

        /// <summary>原活动项为键盘布局（GUID 常为空，身份靠 LangId + HKL）。</summary>
        public static FakeBackend CreateKeyboardLayoutActive(IntPtr originalHkl, bool targetEnabled)
        {
            var backend = Create(targetEnabled);
            backend.Active = new TsfProfile.InputProcessorProfile
            {
                ProfileType = 2, // TF_PROFILETYPE_KEYBOARDLAYOUT
                LangId = 0x0409,
                Clsid = Guid.Empty,
                ProfileGuid = Guid.Empty,
                Hkl = originalHkl,
                Flags = TsfProfile.TfIppFlagEnabled | TsfProfile.TfIppFlagActive,
            };
            return backend;
        }

        public static TsfProfile.InputProcessorProfile KeyboardLayout(IntPtr hkl) => new()
        {
            ProfileType = 2,
            LangId = 0x0409,
            Clsid = Guid.Empty,
            ProfileGuid = Guid.Empty,
            Hkl = hkl,
            Flags = TsfProfile.TfIppFlagEnabled | TsfProfile.TfIppFlagActive,
        };

        public int GetActiveProfile(out TsfProfile.InputProcessorProfile profile)
        {
            GetActiveCalls++;
            if (GetActiveThrows)
            {
                throw new InvalidOperationException("fault injection: GetActiveProfile failure");
            }

            profile = ActiveReadbackOverride ?? Active;
            return 0;
        }

        public int GetProfile(
            uint profileType, ushort langid, Guid clsid, Guid profileGuid,
            out TsfProfile.InputProcessorProfile profile)
        {
            GetProfileCalls++;
            profile = Target;
            return 0;
        }

        public int ActivateProfile(
            uint profileType, ushort langid, Guid clsid, Guid profileGuid, IntPtr hkl, uint flags)
        {
            ActivateCalls.Add((clsid, profileGuid, hkl, flags));
            if (ActivateThrows)
            {
                throw new InvalidOperationException("fault injection: ActivateProfile failure");
            }

            if (ActivateHr == 0)
            {
                if (clsid == TargetClsid)
                {
                    if ((flags & TsfProfile.TfIppmEnableProfile) != 0)
                    {
                        Target.Flags |= TsfProfile.TfIppFlagEnabled;
                    }

                    Active = Target;
                    Active.Flags = Target.Flags | TsfProfile.TfIppFlagActive;
                }
                else
                {
                    Active.Clsid = clsid;
                    Active.ProfileGuid = profileGuid;
                    Active.ProfileType = profileType;
                    Active.LangId = langid;
                    Active.Hkl = hkl;
                    Active.SubstituteHkl = IntPtr.Zero;
                    Active.Flags = TsfProfile.TfIppFlagEnabled | TsfProfile.TfIppFlagActive;
                }
            }
            else if (MutateTargetOnActivate && clsid == TargetClsid
                && (flags & TsfProfile.TfIppmEnableProfile) != 0)
            {
                // 模拟“返回失败但已部分改动启用位”。
                Target.Flags |= TsfProfile.TfIppFlagEnabled;
            }

            return ActivateHr;
        }

        public int DeactivateProfile(
            uint profileType, ushort langid, Guid clsid, Guid profileGuid, IntPtr hkl, uint flags)
        {
            DeactivateCalls.Add((clsid, profileGuid, hkl, flags));
            if (DeactivateThrows)
            {
                throw new InvalidOperationException("fault injection: DeactivateProfile failure");
            }

            Target.Flags &= ~TsfProfile.TfIppFlagEnabled;
            return 0;
        }

        public void Release() => Released = true;
    }
}
