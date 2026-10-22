using Xunit;

namespace CSharp.UI.TsfHostCheck.Tests;

/// <summary>
/// CLI 解析：R17 的 --hidden-host 必须默认 MTA、可显式选 STA，
/// 且与真实输入场景/自检模式互斥（冲突要如实报错，不能静默忽略其中一项）。
/// </summary>
public sealed class ProgramArgumentsTests
{
    [Fact]
    public void HiddenHostDefaultsToMta()
    {
        var ok = Program.TryParse(new[] { "--hidden-host" }, out var parsed, out var error);

        Assert.True(ok, error);
        Assert.NotNull(parsed.Options);
        Assert.True(parsed.Options!.HiddenHost);
        Assert.False(parsed.Options.Sta);
        Assert.Null(parsed.Options.Scenario);
    }

    [Fact]
    public void HiddenHostAcceptsExplicitSta()
    {
        var ok = Program.TryParse(new[] { "--hidden-host", "--apartment", "sta" },
            out var parsed, out var error);

        Assert.True(ok, error);
        Assert.True(parsed.Options!.HiddenHost);
        Assert.True(parsed.Options.Sta);
        Assert.True(parsed.ApartmentFilter);
    }

    [Fact]
    public void VisibleRunKeepsMtaDefaultAndExplicitSta()
    {
        var defaultOk = Program.TryParse(Array.Empty<string>(), out var defaultParsed, out var defaultError);
        Assert.True(defaultOk, defaultError);
        Assert.False(defaultParsed.Options!.HiddenHost);
        Assert.False(defaultParsed.Options.Sta);

        var staOk = Program.TryParse(new[] { "--scenario", "scroll-follow", "--apartment", "sta" },
            out var staParsed, out var staError);
        Assert.True(staOk, staError);
        Assert.False(staParsed.Options!.HiddenHost);
        Assert.True(staParsed.Options.Sta);
        Assert.Equal("scroll-follow", staParsed.Options.Scenario);
    }

    [Fact]
    public void HiddenHostRejectsScenarioInsteadOfIgnoringIt()
    {
        var ok = Program.TryParse(new[] { "--hidden-host", "--scenario", "scroll-follow" },
            out _, out var error);

        Assert.False(ok);
        Assert.Contains("hidden-host", error, StringComparison.Ordinal);
    }

    [Fact]
    public void HiddenHostRejectsSelfTestModes()
    {
        Assert.False(Program.TryParse(new[] { "--hidden-host", "--selftest" }, out _, out var selfError));
        Assert.Contains("hidden-host", selfError, StringComparison.Ordinal);

        Assert.False(Program.TryParse(new[] { "--hidden-host", "--native-selftest" },
            out _, out var nativeError));
        Assert.Contains("hidden-host", nativeError, StringComparison.Ordinal);
    }

    [Fact]
    public void HelpShortCircuitsAndUnknownArgumentFails()
    {
        Assert.True(Program.TryParse(new[] { "--help" }, out var help, out _));
        Assert.True(help.Help);

        Assert.False(Program.TryParse(new[] { "--not-a-switch" }, out _, out var error));
        Assert.Contains("--not-a-switch", error, StringComparison.Ordinal);
    }

    [Fact]
    public void UiElementEnabledOnlyDefaultsOffAndIsOptIn()
    {
        var defaultOk = Program.TryParse(Array.Empty<string>(), out var defaultParsed, out var defaultError);
        Assert.True(defaultOk, defaultError);
        Assert.False(defaultParsed.Options!.UiElementEnabledOnly);

        var enabledOk = Program.TryParse(
            new[] { "--scenario", "scroll-follow", "--uielement-enabled-only" },
            out var enabledParsed, out var enabledError);
        Assert.True(enabledOk, enabledError);
        Assert.True(enabledParsed.Options!.UiElementEnabledOnly);
        // 只切激活方式，不得顺带打开其他诊断。
        Assert.False(enabledParsed.Options.ProbeContextView);
        Assert.False(enabledParsed.Options.TraceLayoutChain);
        Assert.False(enabledParsed.Options.SuppressCandidateUi);

        // 隐藏宿主与原生自检都能显式选择该模式（由 Codex 执行真实原生复核）。
        var hiddenOk = Program.TryParse(
            new[] { "--hidden-host", "--uielement-enabled-only" },
            out var hiddenParsed, out var hiddenError);
        Assert.True(hiddenOk, hiddenError);
        Assert.True(hiddenParsed.Options!.UiElementEnabledOnly);
        Assert.True(hiddenParsed.Options.HiddenHost);
    }

    [Fact]
    public void LayoutChainTraceDefaultsOffAndIsOptIn()
    {
        var defaultOk = Program.TryParse(Array.Empty<string>(), out var defaultParsed, out var defaultError);
        Assert.True(defaultOk, defaultError);
        Assert.False(defaultParsed.Options!.TraceLayoutChain);

        var enabledOk = Program.TryParse(
            new[] { "--scenario", "scroll-follow", "--trace-layout-chain" },
            out var enabledParsed, out var enabledError);
        Assert.True(enabledOk, enabledError);
        Assert.True(enabledParsed.Options!.TraceLayoutChain);
        Assert.False(enabledParsed.Options.ProbeContextView);

        // 可与隐藏宿主组合（只验证接线，不显示窗口、不输入）。
        var hiddenOk = Program.TryParse(
            new[] { "--hidden-host", "--trace-layout-chain" },
            out var hiddenParsed, out var hiddenError);
        Assert.True(hiddenOk, hiddenError);
        Assert.True(hiddenParsed.Options!.TraceLayoutChain);
        Assert.True(hiddenParsed.Options.HiddenHost);
    }

    [Fact]
    public void ContextViewProbeDefaultsOffAndIsOptIn()
    {
        var defaultOk = Program.TryParse(Array.Empty<string>(), out var defaultParsed, out var defaultError);
        Assert.True(defaultOk, defaultError);
        Assert.False(defaultParsed.Options!.ProbeContextView);

        var enabledOk = Program.TryParse(
            new[] { "--scenario", "scroll-follow", "--probe-context-view" },
            out var enabledParsed, out var enabledError);
        Assert.True(enabledOk, enabledError);
        Assert.True(enabledParsed.Options!.ProbeContextView);
        Assert.Equal("scroll-follow", enabledParsed.Options.Scenario);
        Assert.False(enabledParsed.Options.HiddenHost);

        // 隐藏宿主同样可显式启用（空文档接线验证），仍保持 STA 选择可用。
        var hiddenOk = Program.TryParse(
            new[] { "--hidden-host", "--apartment", "sta", "--probe-context-view" },
            out var hiddenParsed, out var hiddenError);
        Assert.True(hiddenOk, hiddenError);
        Assert.True(hiddenParsed.Options!.ProbeContextView);
        Assert.True(hiddenParsed.Options.HiddenHost);
        Assert.True(hiddenParsed.Options.Sta);
    }

    // ---- R29B --child-input-surface：本步只接线 child 创建 + hidden-host ------

    [Fact]
    public void ChildInputSurfaceDefaultsOff()
    {
        var ok = Program.TryParse(Array.Empty<string>(), out var parsed, out var error);

        Assert.True(ok, error);
        Assert.False(parsed.Options!.ChildInputSurface);
    }

    [Fact]
    public void ChildInputSurfaceIsAcceptedWithHiddenHost()
    {
        var ok = Program.TryParse(new[] { "--hidden-host", "--child-input-surface" },
            out var parsed, out var error);

        Assert.True(ok, error);
        Assert.True(parsed.Options!.ChildInputSurface);
        Assert.True(parsed.Options.HiddenHost);
    }

    [Fact]
    public void ChildInputSurfaceHiddenHostKeepsStaSelectable()
    {
        var ok = Program.TryParse(
            new[] { "--hidden-host", "--child-input-surface", "--apartment", "sta" },
            out var parsed, out var error);

        Assert.True(ok, error);
        Assert.True(parsed.Options!.ChildInputSurface);
        Assert.True(parsed.Options.Sta);
        Assert.True(parsed.ApartmentFilter);
    }

    [Fact]
    public void ChildInputSurfaceRejectsUnqualifiedDefaultMatrix()
    {
        // 未限定场景（默认跑全部）不能带 child flag——本步不做整矩阵 child 运行。
        var ok = Program.TryParse(new[] { "--child-input-surface" }, out _, out var error);

        Assert.False(ok);
        Assert.Contains("child-input-surface", error, StringComparison.Ordinal);
        Assert.Contains("hidden-host", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("scroll-follow")]
    [InlineData("clip-out")]
    [InlineData("move-window")]
    public void ChildInputSurfaceAcceptsWiredVisibleScenarios(string scenario)
    {
        // R29B 只接线三个可见几何执行器；它们通过 --scenario 显式点名时如实接受。
        var ok = Program.TryParse(
            new[] { "--child-input-surface", "--scenario", scenario }, out var parsed,
            out var error);

        Assert.True(ok, error);
        Assert.True(parsed.Options!.ChildInputSurface);
        Assert.Equal(scenario, parsed.Options.Scenario);
    }

    [Theory]
    [InlineData("nihao-commit")]
    [InlineData("escape-cancel")]
    [InlineData("enter-select")]
    [InlineData("resize")]
    [InlineData("uielement-hide-probe")]
    [InlineData("native-ui-lifecycle")]
    [InlineData("blur-close")]
    [InlineData("ascii-plain")]
    [InlineData("unicode-direct")]
    public void ChildInputSurfaceRejectsUnwiredVisibleScenarios(string scenario)
    {
        // 除三个已接线执行器外，其余显式可见场景一律如实拒绝（不静默降级为 root surface）。
        var ok = Program.TryParse(
            new[] { "--child-input-surface", "--scenario", scenario }, out _, out var error);

        Assert.False(ok);
        Assert.Contains("only wires scroll-follow, clip-out and move-window", error,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ChildInputSurfaceRejectsHiddenHostPlusScenario()
    {
        // hidden-host 自身也拒绝 --scenario；无论哪条拒绝规则先命中，
        // 该组合都必须失败且不能静默忽略任一标志。
        var ok = Program.TryParse(
            new[] { "--hidden-host", "--child-input-surface", "--scenario", "clip-out" },
            out _, out var error);

        Assert.False(ok);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void ChildInputSurfaceRejectsIncompatibleFlags()
    {
        Assert.False(Program.TryParse(
            new[] { "--hidden-host", "--child-input-surface", "--suppress-candidate-ui" },
            out _, out var suppressError));
        Assert.Contains("child-input-surface", suppressError, StringComparison.Ordinal);

        Assert.False(Program.TryParse(
            new[] { "--hidden-host", "--child-input-surface", "--uielement-enabled-only" },
            out _, out var uiElementError));
        Assert.Contains("child-input-surface", uiElementError, StringComparison.Ordinal);

        Assert.False(Program.TryParse(
            new[] { "--hidden-host", "--child-input-surface", "--probe-context-view" },
            out _, out var probeError));
        Assert.Contains("child-input-surface", probeError, StringComparison.Ordinal);
    }

    [Fact]
    public void ChildInputSurfaceRejectsSelfTestModes()
    {
        Assert.False(Program.TryParse(new[] { "--child-input-surface", "--selftest" },
            out _, out var selfError));
        Assert.Contains("child-input-surface", selfError, StringComparison.Ordinal);

        Assert.False(Program.TryParse(new[] { "--child-input-surface", "--native-selftest" },
            out _, out var nativeError));
        Assert.Contains("child-input-surface", nativeError, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidApartmentValueIsRejected()
    {
        Assert.False(Program.TryParse(new[] { "--apartment", "neutral" }, out _, out var error));
        Assert.Contains("--apartment", error, StringComparison.Ordinal);
    }

    // ---- R30 --child-hide-when-clipped：唯一允许组合是
    //      --child-input-surface --scenario clip-out；其余一律解析期拒绝 ------

    [Fact]
    public void ChildHideWhenClippedDefaultsOff()
    {
        var ok = Program.TryParse(Array.Empty<string>(), out var parsed, out var error);

        Assert.True(ok, error);
        Assert.False(parsed.Options!.ChildHideWhenClipped);
    }

    [Fact]
    public void ChildHideWhenClippedAcceptedInAllowedCombination()
    {
        var ok = Program.TryParse(
            new[] { "--child-input-surface", "--scenario", "clip-out",
                    "--child-hide-when-clipped" },
            out var parsed, out var error);

        Assert.True(ok, error);
        Assert.True(parsed.Options!.ChildInputSurface);
        Assert.True(parsed.Options.ChildHideWhenClipped);
        Assert.Equal("clip-out", parsed.Options.Scenario);
    }

    [Fact]
    public void ChildHideWhenClippedRequiresChildInputSurface()
    {
        var ok = Program.TryParse(
            new[] { "--scenario", "clip-out", "--child-hide-when-clipped" },
            out _, out var error);

        Assert.False(ok);
        Assert.Contains("child-hide-when-clipped", error, StringComparison.Ordinal);
        Assert.Contains("child-input-surface", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ChildHideWhenClippedRejectsHiddenHost()
    {
        var ok = Program.TryParse(
            new[] { "--hidden-host", "--child-input-surface", "--child-hide-when-clipped" },
            out _, out var error);

        Assert.False(ok);
        Assert.Contains("child-hide-when-clipped", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("scroll-follow")]
    [InlineData("move-window")]
    public void ChildHideWhenClippedRejectsOtherWiredScenarios(string scenario)
    {
        var ok = Program.TryParse(
            new[] { "--child-input-surface", "--scenario", scenario,
                    "--child-hide-when-clipped" },
            out _, out var error);

        Assert.False(ok);
        Assert.Contains("child-hide-when-clipped", error, StringComparison.Ordinal);
        Assert.Contains("clip-out", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("nihao-commit")]
    [InlineData("resize")]
    [InlineData("native-ui-lifecycle")]
    public void ChildHideWhenClippedRejectsUnwiredScenarios(string scenario)
    {
        // child-input-surface 的接线白名单先行拒绝（无论哪条先命中，组合都必须失败）。
        var ok = Program.TryParse(
            new[] { "--child-input-surface", "--scenario", scenario,
                    "--child-hide-when-clipped" },
            out _, out var error);

        Assert.False(ok);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void ChildHideWhenClippedRejectsUnqualifiedDefaultMatrix()
    {
        var ok = Program.TryParse(
            new[] { "--child-input-surface", "--child-hide-when-clipped" },
            out _, out var error);

        Assert.False(ok);
        Assert.Contains("child-input-surface", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ChildHideWhenClippedRejectsConflictingExperiments()
    {
        Assert.False(Program.TryParse(
            new[] { "--child-input-surface", "--scenario", "clip-out",
                    "--child-hide-when-clipped", "--suppress-candidate-ui" },
            out _, out var suppressError));
        Assert.False(string.IsNullOrEmpty(suppressError));

        Assert.False(Program.TryParse(
            new[] { "--child-input-surface", "--scenario", "clip-out",
                    "--child-hide-when-clipped", "--probe-context-view" },
            out _, out var probeError));
        Assert.False(string.IsNullOrEmpty(probeError));
    }

    // ---- R31：--child-suspend-tsf-focus-when-clipped 解析规则 -------------------

    [Fact]
    public void ChildSuspendTsfFocusParsesValidCombo()
    {
        var ok = Program.TryParse(
            new[] { "--child-input-surface", "--scenario", "clip-out",
                    "--apartment", "sta",
                    "--child-suspend-tsf-focus-when-clipped" },
            out var parsed, out var error);

        Assert.True(ok, error);
        Assert.True(parsed.Options.ChildSuspendTsfFocusWhenClipped);
        Assert.True(parsed.Options.ChildInputSurface);
        Assert.Equal("clip-out", parsed.Options.Scenario);
        Assert.True(parsed.Options.Sta);
    }

    [Fact]
    public void ChildSuspendTsfFocusRejectsWithoutChildInputSurface()
    {
        var ok = Program.TryParse(
            new[] { "--child-suspend-tsf-focus-when-clipped" },
            out _, out var error);

        Assert.False(ok);
        Assert.Contains("child-input-surface", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ChildSuspendTsfFocusRejectsHiddenHost()
    {
        var ok = Program.TryParse(
            new[] { "--child-input-surface", "--hidden-host",
                    "--child-suspend-tsf-focus-when-clipped" },
            out _, out var error);

        Assert.False(ok);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void ChildSuspendTsfFocusRejectsMutualExclusionWithHide()
    {
        var ok = Program.TryParse(
            new[] { "--child-input-surface", "--scenario", "clip-out",
                    "--apartment", "sta",
                    "--child-suspend-tsf-focus-when-clipped",
                    "--child-hide-when-clipped" },
            out _, out var error);

        Assert.False(ok);
        Assert.Contains("mutually exclusive", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("scroll-follow")]
    [InlineData("move-window")]
    [InlineData("nihao-commit")]
    [InlineData("resize")]
    public void ChildSuspendTsfFocusRejectsNonClipOutScenarios(string scenario)
    {
        var ok = Program.TryParse(
            new[] { "--child-input-surface", "--scenario", scenario,
                    "--apartment", "sta",
                    "--child-suspend-tsf-focus-when-clipped" },
            out _, out var error);

        Assert.False(ok);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void ChildSuspendTsfFocusRejectsUnqualifiedDefaultMatrix()
    {
        var ok = Program.TryParse(
            new[] { "--child-input-surface",
                    "--child-suspend-tsf-focus-when-clipped" },
            out _, out var error);

        Assert.False(ok);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void ChildSuspendTsfFocusRequiresStaApartment()
    {
        // 显式 MTA 拒绝。
        Assert.False(Program.TryParse(
            new[] { "--child-input-surface", "--scenario", "clip-out",
                    "--apartment", "mta",
                    "--child-suspend-tsf-focus-when-clipped" },
            out _, out var mtaError));
        Assert.Contains("sta", mtaError, StringComparison.Ordinal);

        // 未指定 apartment（默认 MTA）同样拒绝。
        Assert.False(Program.TryParse(
            new[] { "--child-input-surface", "--scenario", "clip-out",
                    "--child-suspend-tsf-focus-when-clipped" },
            out _, out var defaultError));
        Assert.Contains("sta", defaultError, StringComparison.Ordinal);
    }

    [Fact]
    public void ChildSuspendTsfFocusRejectsConflictingExperiments()
    {
        Assert.False(Program.TryParse(
            new[] { "--child-input-surface", "--scenario", "clip-out",
                    "--apartment", "sta",
                    "--child-suspend-tsf-focus-when-clipped",
                    "--suppress-candidate-ui" },
            out _, out var suppressError));
        Assert.False(string.IsNullOrEmpty(suppressError));

        Assert.False(Program.TryParse(
            new[] { "--child-input-surface", "--scenario", "clip-out",
                    "--apartment", "sta",
                    "--child-suspend-tsf-focus-when-clipped",
                    "--uielement-enabled-only" },
            out _, out var uiElementError));
        Assert.False(string.IsNullOrEmpty(uiElementError));

        Assert.False(Program.TryParse(
            new[] { "--child-input-surface", "--scenario", "clip-out",
                    "--apartment", "sta",
                    "--child-suspend-tsf-focus-when-clipped",
                    "--probe-context-view" },
            out _, out var probeError));
        Assert.False(string.IsNullOrEmpty(probeError));
    }
}
