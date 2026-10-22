using Xunit;

namespace CSharp.UI.TsfHostCheck.Tests;

/// <summary>
/// R26 失败控制流：用假 <see cref="ITfThreadMgrEx"/> 驱动真实的
/// <c>ActivateUiElementEnabledOnly</c> 与 <c>Engage</c> 分支，验证
/// <list type="bullet">
/// <item>ActivateEx 原生成功后，即使 flags 校验失败，也已记录激活所有权（清理链会唯一 Deactivate）；</item>
/// <item>ActivateEx 失败则**不**记录激活所有权；</item>
/// <item>QI（类型转换）失败返回 E_NOINTERFACE 且不激活；</item>
/// <item>默认模式完全不走扩展接口。</item>
/// </list>
/// <para>
/// 假对象是纯托管实现，不涉及原生 COM；这里只验证控制流与所有权，不声称真实 flags 行为。
/// </para>
/// </summary>
public sealed class UiElementActivationControlFlowTests
{
    /// <summary>可控的假 ThreadMgrEx：分别设定 ActivateEx 与 GetActiveFlags 的返回值。</summary>
    private sealed class FakeThreadMgrEx : ITfThreadMgrEx
    {
        private readonly int _activateExHr;
        private readonly int _flagsHr;
        private readonly uint _flags;

        internal FakeThreadMgrEx(int activateExHr, int flagsHr, uint flags)
        {
            _activateExHr = activateExHr;
            _flagsHr = flagsHr;
            _flags = flags;
        }

        internal int ActivateExCalls { get; private set; }

        internal int GetActiveFlagsCalls { get; private set; }

        internal int DeactivateCalls { get; private set; }

        public int ActivateEx(out uint ptid, uint dwFlags)
        {
            ActivateExCalls++;
            ptid = 42;
            return _activateExHr;
        }

        public int GetActiveFlags(out uint pdwFlags)
        {
            GetActiveFlagsCalls++;
            pdwFlags = _flagsHr >= 0 ? _flags : 0;
            return _flagsHr;
        }

        // ---- 其余成员：本测试路径不会调用，如实返回 E_NOTIMPL，绝不伪造成功 ----

        public int Activate(out uint ptid)
        {
            ptid = 0;
            return TsfHResult.E_NOTIMPL;
        }

        public int Deactivate()
        {
            DeactivateCalls++;
            return TsfHResult.S_OK;
        }

        public int CreateDocumentMgr(out ITfDocumentMgr ppdim)
        {
            ppdim = null!;
            return TsfHResult.E_NOTIMPL;
        }

        public int EnumDocumentMgrs(out IntPtr ppEnum)
        {
            ppEnum = IntPtr.Zero;
            return TsfHResult.E_NOTIMPL;
        }

        public int GetFocus(out ITfDocumentMgr ppdimFocus)
        {
            ppdimFocus = null!;
            return TsfHResult.E_NOTIMPL;
        }

        public int SetFocus(ITfDocumentMgr? pdimFocus) => TsfHResult.E_NOTIMPL;

        public int AssociateFocus(IntPtr hwnd, ITfDocumentMgr? pdimNew, out ITfDocumentMgr? ppdimPrev)
        {
            ppdimPrev = null;
            return TsfHResult.E_NOTIMPL;
        }

        public int IsThreadFocus(out int pfThreadFocus)
        {
            pfThreadFocus = 0;
            return TsfHResult.E_NOTIMPL;
        }

        public int GetFunctionProvider(Guid clsid, out IntPtr ppFuncProv)
        {
            ppFuncProv = IntPtr.Zero;
            return TsfHResult.E_NOTIMPL;
        }

        public int EnumFunctionProviders(out IntPtr ppEnum)
        {
            ppEnum = IntPtr.Zero;
            return TsfHResult.E_NOTIMPL;
        }

        public int GetGlobalCompartment(out IntPtr ppCompMgr)
        {
            ppCompMgr = IntPtr.Zero;
            return TsfHResult.E_NOTIMPL;
        }
    }

    private static (TsfSessionHost Session, AcpTextStore Store) NewSession(bool uiElementOnly)
    {
        var store = new AcpTextStore(StoreTestHelpers.NoTrace);
        var session = new TsfSessionHost(store, IntPtr.Zero, null, StoreTestHelpers.NoTrace, false,
            () => { }, uiElementEnabledOnly: uiElementOnly);
        return (session, store);
    }

    [Fact]
    public void ActivateExSuccessThenFlagsFailureStillOwnsActivation()
    {
        var (session, _) = NewSession(uiElementOnly: true);
        // ActivateEx 成功，但 GetActiveFlags 失败。
        var fake = new FakeThreadMgrEx(TsfHResult.S_OK, TsfHResult.E_FAIL, 0);

        var hr = session.ActivateForTest(fake);

        Assert.Equal(TsfHResult.E_FAIL, hr);
        // 关键：原生 ActivateEx 已成功 → 必须记录激活所有权，供清理链唯一 Deactivate。
        Assert.True(session.ActivationOwned);
        Assert.Equal(1, fake.ActivateExCalls);
        Assert.Equal("(failed)", session.ActivationMode);
    }

    [Fact]
    public void ActivateExSuccessButFlagBitMissingStillOwnsActivation()
    {
        var (session, _) = NewSession(uiElementOnly: true);
        // 成功返回但 flags 不含 UIElement-only 位（例如 0x0）。
        var fake = new FakeThreadMgrEx(TsfHResult.S_OK, TsfHResult.S_OK, 0);

        var hr = session.ActivateForTest(fake);

        Assert.Equal(TsfHResult.E_FAIL, hr);
        Assert.True(session.ActivationOwned);
        Assert.Equal(1, fake.ActivateExCalls);
    }

    [Fact]
    public void ActivateExFailureDoesNotOwnActivation()
    {
        var (session, _) = NewSession(uiElementOnly: true);
        var fake = new FakeThreadMgrEx(TsfHResult.E_FAIL, TsfHResult.S_OK, 0x4);

        var hr = session.ActivateForTest(fake);

        Assert.Equal(TsfHResult.E_FAIL, hr);
        // 未激活成功 → 不得记录所有权（清理链因此不会 Deactivate）。
        Assert.False(session.ActivationOwned);
        Assert.Equal(1, fake.ActivateExCalls);
    }

    [Fact]
    public void ActivateExSuccessWithFlagOwnsActivationAndSucceeds()
    {
        var (session, _) = NewSession(uiElementOnly: true);
        var fake = new FakeThreadMgrEx(TsfHResult.S_OK, TsfHResult.S_OK,
            TsfFlags.TF_TMAE_UIELEMENTENABLEDONLY);

        var hr = session.ActivateForTest(fake);

        Assert.Equal(TsfHResult.S_OK, hr);
        Assert.True(session.ActivationOwned);
        Assert.Equal("activate-ex", session.ActivationMode);
        Assert.Equal(TsfFlags.TF_TMAE_UIELEMENTENABLEDONLY, session.ActiveActivationFlags);
    }

    [Fact]
    public void UnsupportedExtendedInterfaceFailsWithoutActivation()
    {
        var (session, _) = NewSession(uiElementOnly: true);
        // 传入一个不实现 ITfThreadMgrEx 的假 ThreadMgr。
        var plain = new FakeThreadMgrOnly();

        var hr = session.ActivateForTest(plain);

        Assert.Equal(TsfHResult.E_NOINTERFACE, hr);
        Assert.False(session.ActivationOwned);
        Assert.Equal("(failed)", session.ActivationMode);
        Assert.Equal(0, plain.ActivateCalls);
    }

    [Fact]
    public void DefaultModeNeverTouchesExtendedInterface()
    {
        var (session, _) = NewSession(uiElementOnly: false);
        var fake = new FakeThreadMgrEx(TsfHResult.S_OK, TsfHResult.S_OK, 0x4);

        // 默认模式不调用扩展 helper；此处显式验证 helper 在默认会话中不可达的行为。
        Assert.False(session.UiElementEnabledOnly);
        Assert.Null(session.ReadActiveActivationFlags("default-should-not-read"));
        // 默认会话没有 threadMgr，读取失败也不得伪造 flags。
        Assert.Null(session.ActiveActivationFlags);
    }

    /// <summary>只实现 ITfThreadMgr 的假对象（用于 QI 失败分支）。</summary>
    private sealed class FakeThreadMgrOnly : ITfThreadMgr
    {
        internal int ActivateCalls { get; private set; }

        public int Activate(out uint ptid)
        {
            ActivateCalls++;
            ptid = 7;
            return TsfHResult.S_OK;
        }

        public int Deactivate() => TsfHResult.S_OK;

        public int CreateDocumentMgr(out ITfDocumentMgr ppdim)
        {
            ppdim = null!;
            return TsfHResult.E_NOTIMPL;
        }

        public int EnumDocumentMgrs(out IntPtr ppEnum)
        {
            ppEnum = IntPtr.Zero;
            return TsfHResult.E_NOTIMPL;
        }

        public int GetFocus(out ITfDocumentMgr ppdimFocus)
        {
            ppdimFocus = null!;
            return TsfHResult.E_NOTIMPL;
        }

        public int SetFocus(ITfDocumentMgr? pdimFocus) => TsfHResult.E_NOTIMPL;

        public int AssociateFocus(IntPtr hwnd, ITfDocumentMgr? pdimNew, out ITfDocumentMgr? ppdimPrev)
        {
            ppdimPrev = null;
            return TsfHResult.E_NOTIMPL;
        }

        public int IsThreadFocus(out int pfThreadFocus)
        {
            pfThreadFocus = 0;
            return TsfHResult.E_NOTIMPL;
        }

        public int GetFunctionProvider(Guid clsid, out IntPtr ppFuncProv)
        {
            ppFuncProv = IntPtr.Zero;
            return TsfHResult.E_NOTIMPL;
        }

        public int EnumFunctionProviders(out IntPtr ppEnum)
        {
            ppEnum = IntPtr.Zero;
            return TsfHResult.E_NOTIMPL;
        }

        public int GetGlobalCompartment(out IntPtr ppCompMgr)
        {
            ppCompMgr = IntPtr.Zero;
            return TsfHResult.E_NOTIMPL;
        }
    }
}
