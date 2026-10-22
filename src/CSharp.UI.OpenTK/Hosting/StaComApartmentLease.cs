namespace CSharp.UI.OpenTK;

/// <summary>
/// 一次 <c>CoInitializeEx(COINIT_APARTMENTTHREADED)</c> 租约的结果分类（TI-07 P1）。
/// </summary>
/// <remarks>
/// 分类是<b>显式结果</b>，调用方不需要（也不允许）自己解释 HRESULT 决定要不要反初始化：
/// 只有 <see cref="Initialized"/> 与 <see cref="AlreadyInitialized"/> 拥有需要成对释放的 COM 引用。
/// </remarks>
internal enum StaComApartmentStatus
{
    /// <summary><c>S_OK</c>：本租约首次把当前线程初始化为 STA，退出时必须 <c>CoUninitialize</c>。</summary>
    Initialized,

    /// <summary>
    /// <c>S_FALSE</c>：当前线程已由别人以<b>同一模型</b>（STA）初始化；本次调用仍增加一次引用，
    /// 必须由本租约成对 <c>CoUninitialize</c> 释放自己那一份（不会解除别人的初始化）。
    /// </summary>
    AlreadyInitialized,

    /// <summary>
    /// <c>RPC_E_CHANGED_MODE</c>：线程已被别人以不同套间模型（通常是 MTA）占用。本调用<b>没有</b>拿到引用，
    /// 如实失败；绝不调用 <c>CoUninitialize</c>，也绝不把该线程当成 STA。
    /// </summary>
    ChangedMode,

    /// <summary>其它失败（负 HRESULT 或原生调用抛异常）：没有引用，不反初始化。</summary>
    Failed,

    /// <summary>当前平台没有 Windows COM（非 Windows 宿主）：完全没有调用原生入口。</summary>
    PlatformNotSupported,
}

/// <summary>
/// 当前线程的 STA COM 套间租约（TI-07 P1 生产基础，不启用 TSF）。
/// </summary>
/// <remarks>
/// <para>
/// <b>作用域：</b>租约只描述“<b>本租约在当前线程上</b>拿到的那一份引用”。它不做任何跨线程迁移，
/// 也不试图把已经被别人初始化成 MTA 的线程“转换”成 STA——那种情况必须显式失败
/// （<see cref="StaComApartmentStatus.ChangedMode"/>），由调用方决定降级或换线程。
/// </para>
/// <para>
/// <b>引用所有权：</b>
/// </para>
/// <list type="bullet">
/// <item><description><c>S_OK</c>：本租约初始化成功，<see cref="Dispose"/> 必须调用一次 <c>CoUninitialize</c>。</description></item>
/// <item><description><c>S_FALSE</c>：线程已由别人以同一模型初始化；本次调用仍增加一次引用，同样由本租约释放。</description></item>
/// <item><description>其它任何结果：本租约没有引用，<see cref="Dispose"/> 绝不调用 <c>CoUninitialize</c>（否则会解除别人的初始化）。</description></item>
/// </list>
/// <para>
/// <b>线程约束：</b>释放必须在获取它的那个线程上发生（COM 套间是线程状态）。在别的线程调用
/// <see cref="Dispose"/> 会被拒绝：<b>不释放引用、不标记已释放</b>，只记录一次
/// <see cref="WrongThreadDisposeCount"/>，让拥有线程仍能正确成对释放。
/// </para>
/// <para>
/// <b>可测性：</b>原生入口经 <see cref="IComApartmentNative"/> 注入，测试用替身可以确定性地产生
/// <c>S_OK</c>/<c>S_FALSE</c>/<c>RPC_E_CHANGED_MODE</c>/失败/异常，而不触碰真实 COM 或线程套间状态。
/// 生产实现只在 Windows 上调用 COM（<see cref="WindowsComApartmentNative.IsSupported"/>）。
/// </para>
/// </remarks>
internal sealed class StaComApartmentLease : IDisposable
{
    /// <summary><c>S_OK</c>：成功，第一次初始化当前线程的套间。</summary>
    internal const int S_OK = 0;

    /// <summary><c>S_FALSE</c>：成功，但线程已被同一模型初始化过；本次仍持有一次引用。</summary>
    internal const int S_FALSE = 1;

    /// <summary><c>E_FAIL</c>：通用失败。</summary>
    internal const int E_FAIL = unchecked((int)0x80004005);

    /// <summary><c>RPC_E_CHANGED_MODE</c>：线程已被不同套间模型占用；没有引用。</summary>
    internal const int RPC_E_CHANGED_MODE = unchecked((int)0x80010106);

    /// <summary><c>COINIT_APARTMENTTHREADED</c>：STA 初始化标志（0x2）。</summary>
    internal const uint CoInitApartmentThreaded = 0x2;

    private readonly IComApartmentNative _native;
    private readonly int _owningThreadId;

    private bool _ownsReference;
    private bool _disposed;
    private int _uninitializeCount;
    private int _wrongThreadDisposeCount;

    private StaComApartmentLease(IComApartmentNative native, int owningThreadId)
    {
        _native = native;
        _owningThreadId = owningThreadId;
    }

    /// <summary>本次租约的分类结果。</summary>
    internal StaComApartmentStatus Status { get; private set; } = StaComApartmentStatus.Failed;

    /// <summary><c>CoInitializeEx</c> 的原始返回值；平台不支持时为 <see cref="E_FAIL"/>（未调用原生入口）。</summary>
    internal int HResult { get; private set; } = E_FAIL;

    /// <summary>本租约是否持有一次需要成对释放的 COM 引用（只有 S_OK/S_FALSE 才是）。</summary>
    internal bool OwnsReference => _ownsReference;

    /// <summary>当前线程是否已被本租约成功初始化为 STA（含“本来就已经是 STA”）。</summary>
    internal bool Succeeded
        => Status is StaComApartmentStatus.Initialized or StaComApartmentStatus.AlreadyInitialized;

    /// <summary>本租约是否已经释放（成功释放或本就没有引用）。</summary>
    internal bool IsDisposed => _disposed;

    /// <summary>获取租约的托管线程 ID；释放只允许在该线程上发生。</summary>
    internal int OwningThreadId => _owningThreadId;

    /// <summary>本租约实际调用 <c>CoUninitialize</c> 的次数（应当最多为 1）。</summary>
    internal int UninitializeCount => _uninitializeCount;

    /// <summary>因跨线程释放被拒绝的次数；被拒绝时引用仍然保留。</summary>
    internal int WrongThreadDisposeCount => _wrongThreadDisposeCount;

    /// <summary>失败原因（脱敏诊断文本）；成功时为 null。</summary>
    internal string? FailureReason { get; private set; }

    /// <summary>该 HRESULT 是否代表“拿到了一次需要成对释放的引用”。</summary>
    internal static bool IsReferenceOwned(int hresult) => hresult is S_OK or S_FALSE;

    /// <summary>HRESULT 的简短可读名（诊断用，不输出用户数据）。</summary>
    internal static string Describe(int hresult) => hresult switch
    {
        S_OK => "S_OK",
        S_FALSE => "S_FALSE",
        RPC_E_CHANGED_MODE => "RPC_E_CHANGED_MODE",
        E_FAIL => "E_FAIL",
        _ => $"HRESULT 0x{hresult:X8}",
    };

    /// <summary>
    /// 在当前线程上申请一次 STA COM 引用。
    /// </summary>
    /// <param name="native">原生入口替身；null 使用 Windows 生产实现。</param>
    /// <returns>
    /// 租约对象。失败<b>不抛出</b>：调用方读取 <see cref="Status"/>/<see cref="Succeeded"/>/<see cref="FailureReason"/>
    /// 得到显式结果，并据此决定降级或换线程。
    /// </returns>
    /// <remarks>本方法不会把已经初始化为 MTA 的线程改成 STA；那种情况返回 <see cref="StaComApartmentStatus.ChangedMode"/>。</remarks>
    internal static StaComApartmentLease Acquire(IComApartmentNative? native = null)
    {
        var lease = new StaComApartmentLease(
            native ?? WindowsComApartmentNative.Instance,
            Environment.CurrentManagedThreadId);
        lease.Initialize();
        return lease;
    }

    private void Initialize()
    {
        if (!_native.IsSupported)
        {
            // 非 Windows：完全没有可调用的 COM 入口，如实报告“平台不支持”。
            Status = StaComApartmentStatus.PlatformNotSupported;
            HResult = E_FAIL;
            FailureReason = "当前平台没有 Windows COM 套间；未调用 CoInitializeEx。";
            return;
        }

        int hresult;
        try
        {
            hresult = _native.CoInitializeEx(CoInitApartmentThreaded);
        }
        catch (Exception ex)
        {
            // 原生入口抛异常等价于“没有拿到引用”：绝不反初始化，也不把异常抛给宿主线程。
            Status = StaComApartmentStatus.Failed;
            HResult = E_FAIL;
            FailureReason = $"CoInitializeEx 抛出 {ex.GetType().Name}；本租约没有 COM 引用。";
            return;
        }

        HResult = hresult;
        _ownsReference = IsReferenceOwned(hresult);
        Status = hresult switch
        {
            S_OK => StaComApartmentStatus.Initialized,
            S_FALSE => StaComApartmentStatus.AlreadyInitialized,
            RPC_E_CHANGED_MODE => StaComApartmentStatus.ChangedMode,
            _ => StaComApartmentStatus.Failed,
        };

        FailureReason = Status switch
        {
            StaComApartmentStatus.Initialized => null,
            StaComApartmentStatus.AlreadyInitialized => null,
            StaComApartmentStatus.ChangedMode =>
                "线程已被不同套间模型占用（RPC_E_CHANGED_MODE）；本租约没有 COM 引用，"
                + "不反初始化，也不把该线程当作 STA。",
            _ => $"CoInitializeEx 失败（{Describe(hresult)}）；本租约没有 COM 引用。",
        };
    }

    /// <summary>
    /// 成对释放本租约持有的那一份 COM 引用（幂等，最多调用一次 <c>CoUninitialize</c>）。
    /// </summary>
    /// <remarks>
    /// <para>没有引用（失败/已释放）时是无操作，绝不调用 <c>CoUninitialize</c>。</para>
    /// <para>
    /// 跨线程调用被拒绝：引用保留、<see cref="IsDisposed"/> 仍为 false，只增加
    /// <see cref="WrongThreadDisposeCount"/>；拥有线程随后仍可正确释放。
    /// </para>
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (!_ownsReference)
        {
            // 失败或 S_FALSE 之外的情形本来就没有引用：标记完成即可，绝不反初始化别人的初始化。
            _disposed = true;
            return;
        }

        if (Environment.CurrentManagedThreadId != _owningThreadId)
        {
            // COM 套间是线程状态：别的线程不能替拥有线程释放。拒绝且不丢引用。
            // 计数用原子自增：误用可能来自任意线程，诊断计数不能因此丢失。
            Interlocked.Increment(ref _wrongThreadDisposeCount);
            return;
        }

        _disposed = true;
        _ownsReference = false;
        Interlocked.Increment(ref _uninitializeCount);
        try
        {
            _native.CoUninitialize();
        }
        catch (Exception)
        {
            // 原生清理失败不能让宿主的释放路径抛出；引用已经交给平台，计数已如实记录。
        }
    }
}

/// <summary>
/// STA 租约使用的原生套间入口（可注入，便于确定性测试）。
/// </summary>
internal interface IComApartmentNative
{
    /// <summary>当前平台是否提供这些原生入口；false 时租约不会调用任何原生方法。</summary>
    bool IsSupported { get; }

    /// <summary>调用 <c>CoInitializeEx(null, coInitFlags)</c> 并返回 HRESULT。</summary>
    int CoInitializeEx(uint coInitFlags);

    /// <summary>调用 <c>CoUninitialize</c>（只在租约确实持有一份引用时调用）。</summary>
    void CoUninitialize();
}

/// <summary>
/// <see cref="IComApartmentNative"/> 的 Windows 生产实现：只在 Windows 上调用 ole32。
/// </summary>
/// <remarks>
/// 声明本身跨平台可编译；非 Windows 时 <see cref="IsSupported"/> 为 false，租约直接给出显式失败结果，
/// 不会走到任何 DllImport 调用。
/// </remarks>
internal sealed class WindowsComApartmentNative : IComApartmentNative
{
    /// <summary>进程级共享实例（无状态，只有两个原生入口）。</summary>
    internal static WindowsComApartmentNative Instance { get; } = new();

    private WindowsComApartmentNative()
    {
    }

    /// <inheritdoc />
    public bool IsSupported => OperatingSystem.IsWindows();

    /// <inheritdoc />
    public int CoInitializeEx(uint coInitFlags)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("CoInitializeEx 只在 Windows 上可用。");
        }

        return CoInitializeExNative(IntPtr.Zero, coInitFlags);
    }

    /// <inheritdoc />
    public void CoUninitialize()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("CoUninitialize 只在 Windows 上可用。");
        }

        CoUninitializeNative();
    }

    // EntryPoint 必须显式写出：托管方法名加了 Native 后缀用于区分，原生导出名仍是 ole32 的
    // CoInitializeEx/CoUninitialize；ExactSpelling 阻止运行库再尝试 A/W 变体。
    [System.Runtime.InteropServices.DllImport("ole32.dll", EntryPoint = "CoInitializeEx", ExactSpelling = true)]
    private static extern int CoInitializeExNative(IntPtr pvReserved, uint dwCoInit);

    [System.Runtime.InteropServices.DllImport("ole32.dll", EntryPoint = "CoUninitialize", ExactSpelling = true)]
    private static extern void CoUninitializeNative();
}
