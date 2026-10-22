using CSharp.UI.ImeProbe;

namespace CSharp.UI.TsfHostCheck;

/// <summary>
/// 实际宿主线程的 COM 套间租约（R17）。
/// 宿主入口必须在创建任何 HWND/COM 对象之前、在真实运行线程上调用一次 <see cref="Prepare"/>：
/// MTA 是默认，只有显式请求才使用 STA。窗口创建、TSF 接入、消息循环与清理始终留在该线程。
/// </summary>
/// <remarks>
/// 引用所有权规则（不做“强制反初始化别人初始化”的事）：
/// <list type="bullet">
/// <item><c>S_OK</c>：本租约初始化成功，退出时必须 <c>CoUninitialize</c>。</item>
/// <item><c>S_FALSE</c>：线程已由别人以同一模型初始化；本调用仍然增加一次引用，
/// 必须由本租约成对 <c>CoUninitialize</c> 释放自己的那一份（不会解除别人的初始化）。</item>
/// <item>其它负值（如 <c>RPC_E_CHANGED_MODE</c>）：线程已被别人以不同模型占用，本调用没有拿到引用，
/// 如实失败且绝不调用 <c>CoUninitialize</c>。</item>
/// </list>
/// </remarks>
internal sealed class HostApartment : IDisposable
{
    // TSF 期望的线程套间：STA 在主线程上是 APTTYPE_MAINSTA，在专用线程上是 APTTYPE_STA。
    internal const int AptTypeSta = 0;
    internal const int AptTypeMta = 1;
    internal const int AptTypeNa = 2;
    internal const int AptTypeMainSta = 3;

    /// <summary>
    /// 宿主线程的选择方式（R17）。运行时在 Main 之前就按入口属性把进程入口线程固定为 MTA
    /// （无 <c>[STAThread]</c> 时默认 MTA，入口实测 <c>type=1</c>），所以条件性 STA 不可能在入口线程上实现：
    /// MTA 沿用入口线程，STA 必须在启动前设置套间的自选线程上运行整条宿主路径。
    /// </summary>
    internal enum HostThreadSelection
    {
        ProcessEntry,
        DedicatedSta,
    }

    internal static HostThreadSelection SelectHostThread(bool sta)
        => sta ? HostThreadSelection.DedicatedSta : HostThreadSelection.ProcessEntry;

    private readonly TsfTrace? _trace;
    private readonly string _label;
    private readonly string? _entryApartment;
    private bool _ownsReference;
    private bool _disposed;

    private HostApartment(TsfTrace? trace, string label, string? entryApartment)
    {
        _trace = trace;
        _label = label;
        _entryApartment = entryApartment;
    }

    internal int HResult { get; private set; }

    internal bool OwnsReference => _ownsReference;

    internal bool RequestedSta { get; private set; }

    /// <summary>初始化后的真实套间描述（来自 <c>CoGetApartmentType</c>）。</summary>
    internal string Description { get; private set; } = "(unknown)";

    /// <summary>初始化前的状态：<c>CO_E_NOTINITIALIZED</c> 说明本租约是第一个。</summary>
    internal string BeforeDescription { get; private set; } = "(unknown)";

    internal int ApartmentType { get; private set; } = -1;

    internal bool ReachedRequestedApartment
        => HResult >= 0 && ApartmentType >= 0 && MatchesExpectedApartment(RequestedSta, ApartmentType);

    internal uint Win32ThreadId { get; private set; }

    internal int ManagedThreadId { get; private set; }

    internal string Label => _label;

    internal static HostApartment Prepare(bool sta, TsfTrace? trace, string label,
        string? entryApartment = null)
    {
        var lease = new HostApartment(trace, label, entryApartment);
        lease.Initialize(sta);
        return lease;
    }

    /// <summary>请求的 <c>CoInitializeEx</c> 标志：MTA 为默认，STA 只在显式请求时使用。</summary>
    internal static uint CoInitFlag(bool sta)
        => sta ? ComNative.CoInitApartmentThreaded : ComNative.CoInitMultithreaded;

    /// <summary>是否拿到一次需要成对释放的 COM 引用（只有 S_OK/S_FALSE 才是）。</summary>
    internal static bool OwnsReferenceFor(int hresult)
        => hresult is TsfHResult.S_OK or TsfHResult.S_FALSE;

    /// <summary>真实套间是否满足请求：MTA 必须是 APTTYPE_MTA；STA 必须是 STA 或主 STA。</summary>
    internal static bool MatchesExpectedApartment(bool sta, int apartmentType)
        => sta
            ? apartmentType is AptTypeSta or AptTypeMainSta
            : apartmentType == AptTypeMta;

    internal static string DescribeRequested(bool sta) => sta ? "STA" : "MTA";

    internal static string DescribeApartment(int apartmentType, int apartmentQualifier)
        => $"type={apartmentType} qualifier={apartmentQualifier}";

    private void Initialize(bool sta)
    {
        RequestedSta = sta;
        Win32ThreadId = NativeMethods.CurrentWin32ThreadId();
        ManagedThreadId = Environment.CurrentManagedThreadId;

        // 初始化前的真实状态先留痕：如果这里已经是 MTA，说明有更早的代码抢先初始化了线程套间。
        var beforeHr = NativeMethods.CoGetApartmentType(out var beforeType, out var beforeQualifier);
        BeforeDescription = beforeHr >= 0
            ? DescribeApartment(beforeType, beforeQualifier)
            : TsfHResult.Describe(beforeHr);

        var coHr = ComNative.CoInitializeEx(IntPtr.Zero, CoInitFlag(sta));
        HResult = coHr;
        _ownsReference = OwnsReferenceFor(coHr);

        var afterHr = NativeMethods.CoGetApartmentType(out var afterType, out var afterQualifier);
        ApartmentType = afterHr >= 0 ? afterType : -1;
        Description = afterHr >= 0
            ? DescribeApartment(afterType, afterQualifier)
            : $"CoGetApartmentType failed {TsfHResult.Describe(afterHr)}";

        Trace("env", "host-apartment", new Dictionary<string, object?>
        {
            ["label"] = _label,
            ["requested"] = DescribeRequested(sta),
            ["coinit_flags"] = $"0x{CoInitFlag(sta):X}",
            ["hresult"] = TsfHResult.Describe(coHr),
            ["succeeded"] = coHr >= 0,
            ["owns_reference"] = _ownsReference,
            ["apartment_before"] = BeforeDescription,
            ["apartment_at_main_entry"] = _entryApartment ?? "(not probed)",
            ["apartment"] = Description,
            ["apartment_type"] = ApartmentType,
            ["matches_requested"] = ReachedRequestedApartment,
            ["win32_thread_id"] = Win32ThreadId,
            ["managed_thread_id"] = ManagedThreadId,
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!_ownsReference)
        {
            // RPC_E_CHANGED_MODE / 其它失败：本租约没有引用，绝不反初始化别人的初始化。
            Trace("cleanup", "couninitialize-skipped", new Dictionary<string, object?>
            {
                ["label"] = _label,
                ["reason"] = "no owned COM reference",
                ["hresult"] = TsfHResult.Describe(HResult),
            });
            return;
        }

        _ownsReference = false;
        ComNative.CoUninitialize();
        Trace("cleanup", "couninitialize", new Dictionary<string, object?>
        {
            ["label"] = _label,
            ["owner"] = "host-apartment",
            ["win32_thread_id"] = Win32ThreadId,
        });
    }

    private void Trace(string stage, string kind, IDictionary<string, object?>? fields)
    {
        try
        {
            _trace?.Invoke(stage, kind, fields);
        }
        catch (Exception)
        {
            // 日志失败不能影响套间初始化/清理。
        }
    }
}
