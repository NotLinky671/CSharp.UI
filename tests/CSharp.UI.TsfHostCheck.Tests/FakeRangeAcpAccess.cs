namespace CSharp.UI.TsfHostCheck.Tests;

/// <summary>
/// 纯托管 `ITfRangeACP` 访问替身：替代 vtable 槽 25/26 的原生调用，使
/// “extent 失败”“extent 与请求范围不一致”等真实分支可以在无 COM 环境执行。
/// 它不模拟任何原生 ABI，只提供探针需要的三个结果。
/// </summary>
internal sealed class FakeRangeAcpAccess : ContextViewEditSession.IRangeAcpAccess
{
    private readonly int _queryHr;
    private readonly int _setExtentHr;
    private readonly int _getExtentHr;
    private readonly int _actualAcp;
    private readonly int _actualCch;

    internal FakeRangeAcpAccess(int getExtentHr, int actualAcp, int actualCch,
        int queryHr = TsfHResult.S_OK, int setExtentHr = TsfHResult.S_OK)
    {
        _getExtentHr = getExtentHr;
        _actualAcp = actualAcp;
        _actualCch = actualCch;
        _queryHr = queryHr;
        _setExtentHr = setExtentHr;
    }

    internal int GetExtentCalls { get; private set; }

    internal int ReleaseCalls { get; private set; }

    internal (int Anchor, int Cch)? LastSetExtent { get; private set; }

    public int Query(IntPtr range, out IntPtr rangeAcp)
    {
        rangeAcp = _queryHr < 0 ? IntPtr.Zero : new IntPtr(0x5000);
        return _queryHr;
    }

    public int SetExtent(IntPtr rangeAcp, int acpAnchor, int cch)
    {
        LastSetExtent = (acpAnchor, cch);
        return _setExtentHr;
    }

    public int GetExtent(IntPtr rangeAcp, out int acpAnchor, out int cch)
    {
        GetExtentCalls++;
        acpAnchor = _actualAcp;
        cch = _actualCch;
        return _getExtentHr;
    }

    public void Release(IntPtr pointer) => ReleaseCalls++;
}
