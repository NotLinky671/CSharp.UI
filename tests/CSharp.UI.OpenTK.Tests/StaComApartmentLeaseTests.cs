using Xunit;

namespace CSharp.UI.OpenTK.Tests;

/// <summary>
/// TI-07 P1 STA COM 套间租约：引用所有权、显式失败结果、幂等释放与跨线程释放拒绝。
/// </summary>
/// <remarks>
/// 全部使用注入的原生替身，<b>不初始化真实 COM</b>、不改动当前线程套间、不创建窗口：
/// 断言的是“本租约拿到了什么引用、什么时候释放”，而不是平台行为。
/// </remarks>
public sealed class StaComApartmentLeaseTests
{
    // ------------------------------------------------------------ 原生替身

    private sealed class FakeComApartment : IComApartmentNative
    {
        public bool IsSupported { get; set; } = true;
        public int HResult { get; set; } = StaComApartmentLease.S_OK;
        public bool ThrowOnInitialize { get; set; }
        public bool ThrowOnUninitialize { get; set; }
        public int InitializeCalls;
        public int UninitializeCalls;
        public uint LastFlags;

        public int CoInitializeEx(uint coInitFlags)
        {
            InitializeCalls++;
            LastFlags = coInitFlags;
            if (ThrowOnInitialize)
            {
                throw new InvalidOperationException("native initialize failed");
            }

            return HResult;
        }

        public void CoUninitialize()
        {
            UninitializeCalls++;
            if (ThrowOnUninitialize)
            {
                throw new InvalidOperationException("native uninitialize failed");
            }
        }
    }

    // ------------------------------------------------------------ 成功路径

    [Fact]
    public void S_OK拿到一份引用且释放恰好反初始化一次()
    {
        var native = new FakeComApartment { HResult = StaComApartmentLease.S_OK };
        var lease = StaComApartmentLease.Acquire(native);

        Assert.Equal(StaComApartmentStatus.Initialized, lease.Status);
        Assert.Equal(StaComApartmentLease.S_OK, lease.HResult);
        Assert.True(lease.OwnsReference);
        Assert.True(lease.Succeeded);
        Assert.False(lease.IsDisposed);
        Assert.Null(lease.FailureReason);
        Assert.Equal(1, native.InitializeCalls);
        // 只请求 STA：绝不传 COINIT_MULTITHREADED。
        Assert.Equal(StaComApartmentLease.CoInitApartmentThreaded, native.LastFlags);
        Assert.Equal(0x2u, native.LastFlags);

        lease.Dispose();

        Assert.Equal(1, native.UninitializeCalls);
        Assert.Equal(1, lease.UninitializeCount);
        Assert.True(lease.IsDisposed);
        Assert.False(lease.OwnsReference);
        Assert.Equal(0, lease.WrongThreadDisposeCount);

        // 重复释放不再触碰原生入口。
        lease.Dispose();
        lease.Dispose();
        Assert.Equal(1, native.UninitializeCalls);
    }

    [Fact]
    public void S_FALSE仍持有自己那一份引用并成对释放()
    {
        // S_FALSE：线程已由别人以同一模型初始化；本次调用仍增加一次引用，必须由本租约释放。
        var native = new FakeComApartment { HResult = StaComApartmentLease.S_FALSE };
        var lease = StaComApartmentLease.Acquire(native);

        Assert.Equal(StaComApartmentStatus.AlreadyInitialized, lease.Status);
        Assert.True(lease.OwnsReference);
        Assert.True(lease.Succeeded);
        Assert.Null(lease.FailureReason);

        lease.Dispose();

        Assert.Equal(1, native.UninitializeCalls);
        Assert.Equal(1, lease.UninitializeCount);
    }

    // ------------------------------------------------------------ 失败路径（不得反初始化）

    [Fact]
    public void 模式冲突显式失败且绝不反初始化()
    {
        var native = new FakeComApartment { HResult = StaComApartmentLease.RPC_E_CHANGED_MODE };
        var lease = StaComApartmentLease.Acquire(native);

        Assert.Equal(StaComApartmentStatus.ChangedMode, lease.Status);
        Assert.Equal(StaComApartmentLease.RPC_E_CHANGED_MODE, lease.HResult);
        Assert.False(lease.OwnsReference);
        Assert.False(lease.Succeeded);
        Assert.NotNull(lease.FailureReason);
        // 明确写出“不把该线程当作 STA”，不假装转换成功。
        Assert.Contains("RPC_E_CHANGED_MODE", lease.FailureReason!, StringComparison.Ordinal);
        Assert.Contains("STA", lease.FailureReason!, StringComparison.Ordinal);

        lease.Dispose();
        lease.Dispose();

        Assert.Equal(0, native.UninitializeCalls);
        Assert.Equal(0, lease.UninitializeCount);
        Assert.True(lease.IsDisposed);
    }

    [Theory]
    [InlineData(StaComApartmentLease.E_FAIL)]
    [InlineData(unchecked((int)0x8007000E))]
    [InlineData(unchecked((int)0x80004002))]
    public void 其它失败不持有引用且释放不反初始化(int hresult)
    {
        var native = new FakeComApartment { HResult = hresult };
        var lease = StaComApartmentLease.Acquire(native);

        Assert.Equal(StaComApartmentStatus.Failed, lease.Status);
        Assert.Equal(hresult, lease.HResult);
        Assert.False(lease.OwnsReference);
        Assert.False(lease.Succeeded);
        Assert.NotNull(lease.FailureReason);

        lease.Dispose();

        Assert.Equal(0, native.UninitializeCalls);
        Assert.True(lease.IsDisposed);
    }

    [Fact]
    public void 原生初始化抛异常时如实失败且不反初始化()
    {
        var native = new FakeComApartment { ThrowOnInitialize = true };
        var lease = StaComApartmentLease.Acquire(native);

        Assert.Equal(StaComApartmentStatus.Failed, lease.Status);
        Assert.Equal(StaComApartmentLease.E_FAIL, lease.HResult);
        Assert.False(lease.OwnsReference);
        Assert.NotNull(lease.FailureReason);
        Assert.Contains("InvalidOperationException", lease.FailureReason!, StringComparison.Ordinal);

        lease.Dispose();

        Assert.Equal(0, native.UninitializeCalls);
    }

    [Fact]
    public void 非Windows平台不调用任何原生入口并显式报告不支持()
    {
        var native = new FakeComApartment { IsSupported = false };
        var lease = StaComApartmentLease.Acquire(native);

        Assert.Equal(StaComApartmentStatus.PlatformNotSupported, lease.Status);
        Assert.False(lease.Succeeded);
        Assert.False(lease.OwnsReference);
        Assert.NotNull(lease.FailureReason);
        Assert.Equal(0, native.InitializeCalls);

        lease.Dispose();

        Assert.Equal(0, native.UninitializeCalls);
    }

    // ------------------------------------------------------------ 跨线程释放

    [Fact]
    public void 跨线程释放被拒绝且引用仍归拥有线程()
    {
        var native = new FakeComApartment { HResult = StaComApartmentLease.S_OK };
        var lease = StaComApartmentLease.Acquire(native);
        Assert.True(lease.OwnsReference);
        Assert.Equal(Environment.CurrentManagedThreadId, lease.OwningThreadId);

        var wrongThreadFinished = false;
        var thread = new Thread(() =>
        {
            lease.Dispose();
            wrongThreadFinished = true;
        });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        Assert.True(wrongThreadFinished);

        // 拒绝：不释放引用、不标记已释放、不触碰原生入口。
        Assert.Equal(1, lease.WrongThreadDisposeCount);
        Assert.True(lease.OwnsReference);
        Assert.False(lease.IsDisposed);
        Assert.Equal(0, native.UninitializeCalls);
        Assert.Equal(0, lease.UninitializeCount);

        // 拥有线程仍能正确成对释放（引用没有因为误用而丢失）。
        lease.Dispose();
        Assert.Equal(1, native.UninitializeCalls);
        Assert.Equal(1, lease.UninitializeCount);
        Assert.False(lease.OwnsReference);
        Assert.True(lease.IsDisposed);
    }

    [Fact]
    public void 跨线程重复释放只累计拒绝次数且不释放引用()
    {
        var native = new FakeComApartment { HResult = StaComApartmentLease.S_FALSE };
        var lease = StaComApartmentLease.Acquire(native);

        var thread = new Thread(() =>
        {
            lease.Dispose();
            lease.Dispose();
        });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));

        Assert.Equal(2, lease.WrongThreadDisposeCount);
        Assert.True(lease.OwnsReference);
        Assert.Equal(0, native.UninitializeCalls);

        lease.Dispose();
        Assert.Equal(1, native.UninitializeCalls);
    }

    [Fact]
    public void 无引用的租约跨线程释放直接完成不计数()
    {
        // 失败租约没有引用：任何线程释放都只是标记完成，既不需要拥有线程也不计数。
        var native = new FakeComApartment { HResult = StaComApartmentLease.RPC_E_CHANGED_MODE };
        var lease = StaComApartmentLease.Acquire(native);

        var thread = new Thread(lease.Dispose);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));

        Assert.True(lease.IsDisposed);
        Assert.Equal(0, lease.WrongThreadDisposeCount);
        Assert.Equal(0, native.UninitializeCalls);
    }

    // ------------------------------------------------------------ 原生清理失败

    [Fact]
    public void 原生反初始化抛异常不向调用方抛出且计数如实()
    {
        var native = new FakeComApartment { HResult = StaComApartmentLease.S_OK, ThrowOnUninitialize = true };
        var lease = StaComApartmentLease.Acquire(native);

        // 宿主释放路径不能因为原生清理失败而抛出。
        lease.Dispose();

        Assert.Equal(1, native.UninitializeCalls);
        Assert.Equal(1, lease.UninitializeCount);
        Assert.True(lease.IsDisposed);

        lease.Dispose();
        Assert.Equal(1, native.UninitializeCalls);
    }

    // ------------------------------------------------------------ 纯函数规则

    [Theory]
    [InlineData(StaComApartmentLease.S_OK, true)]
    [InlineData(StaComApartmentLease.S_FALSE, true)]
    [InlineData(StaComApartmentLease.RPC_E_CHANGED_MODE, false)]
    [InlineData(StaComApartmentLease.E_FAIL, false)]
    [InlineData(unchecked((int)0x80010108), false)]
    public void 只有成功结果拥有需要成对释放的引用(int hresult, bool expected)
    {
        Assert.Equal(expected, StaComApartmentLease.IsReferenceOwned(hresult));
    }

    [Fact]
    public void HRESULT诊断名可读()
    {
        Assert.Equal("S_OK", StaComApartmentLease.Describe(StaComApartmentLease.S_OK));
        Assert.Equal("S_FALSE", StaComApartmentLease.Describe(StaComApartmentLease.S_FALSE));
        Assert.Equal("RPC_E_CHANGED_MODE", StaComApartmentLease.Describe(StaComApartmentLease.RPC_E_CHANGED_MODE));
        Assert.Equal("E_FAIL", StaComApartmentLease.Describe(StaComApartmentLease.E_FAIL));
        Assert.Equal("HRESULT 0x80070005", StaComApartmentLease.Describe(unchecked((int)0x80070005)));
    }

    [Fact]
    public void 生产原生入口只在Windows上声明支持()
    {
        var native = WindowsComApartmentNative.Instance;
        Assert.Same(native, WindowsComApartmentNative.Instance);
        Assert.Equal(OperatingSystem.IsWindows(), native.IsSupported);
    }

    // ------------------------------------------------------------ 真实原生入口（仅 Windows，隔离线程）

    /// <summary>
    /// 真实 <c>CoGetApartmentType</c>（ole32 导出，与生产入口同一 DLL）：用来独立确认租约之后的真实套间，
    /// 而不是相信租约自己报告的状态。
    /// </summary>
    [System.Runtime.InteropServices.DllImport("ole32.dll", EntryPoint = "CoGetApartmentType", ExactSpelling = true)]
    private static extern int CoGetApartmentType(out int apartmentType, out int apartmentQualifier);

    private const int AptTypeSta = 0;
    private const int AptTypeMta = 1;
    private const int AptTypeMainSta = 3;

    /// <summary>
    /// 在专用线程上以指定套间执行 <paramref name="body"/>：套间是线程状态，只有在线程启动前声明才生效。
    /// 线程内异常会完整传播（不吞掉，也不伪装成断言失败）。
    /// </summary>
    private static void RunOnApartmentThread(ApartmentState state, Action body)
    {
        if (!OperatingSystem.IsWindows())
        {
            // 套间是 Windows 概念：非 Windows 上不创建线程，调用方已按平台跳过真实 COM 断言。
            return;
        }

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(state);
        thread.IsBackground = true;
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), $"{state} 线程未在超时内结束。");
        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException($"{state} 线程内失败：{failure.GetType().Name}: {failure.Message}");
        }
    }

    /// <summary>读取当前线程的真实套间类型；调用失败返回负值。</summary>
    private static int CurrentApartmentType()
        => OperatingSystem.IsWindows() && CoGetApartmentType(out var type, out _) >= 0 ? type : -1;

    [Fact]
    public void 真实STA租约经CoGetApartmentType确认STA且嵌套引用成对释放()
    {
        if (!OperatingSystem.IsWindows())
        {
            // 非 Windows：生产实现如实报告不支持（上面已用替身覆盖），这里没有真实 COM 可测。
            return;
        }

        RunOnApartmentThread(ApartmentState.STA, () =>
        {
            // 线程以 STA 启动后，CLR 已经持有一份套间引用：租约应得到 S_FALSE（同一模型的第二次引用）。
            Assert.True(
                CurrentApartmentType() is AptTypeSta or AptTypeMainSta,
                "STA 线程的真实套间类型不是 STA/MAIN_STA。");

            var outer = StaComApartmentLease.Acquire();
            try
            {
                // 生产默认 native 实现必须真的解析到 ole32 导出（R1：EntryPoint 写错会在这里变成 Failed）。
                Assert.True(outer.Succeeded, $"真实 CoInitializeEx(STA) 失败：{outer.Status} {outer.FailureReason}");
                Assert.True(outer.OwnsReference);
                Assert.Equal(StaComApartmentLease.S_FALSE, outer.HResult);
                Assert.Equal(StaComApartmentStatus.AlreadyInitialized, outer.Status);
                Assert.True(CurrentApartmentType() is AptTypeSta or AptTypeMainSta);
            }
            finally
            {
                outer.Dispose();
            }

            // 成对释放：恰好一次反初始化，且释放后线程仍是 STA（CLR 自己那份引用没有被解除）。
            Assert.Equal(1, outer.UninitializeCount);
            Assert.False(outer.OwnsReference);
            Assert.True(outer.IsDisposed);
            Assert.True(
                CurrentApartmentType() is AptTypeSta or AptTypeMainSta,
                "外层租约释放后线程不再是 STA：说明解除了不属于自己的引用。");
        });
    }

    [Fact]
    public void 真实STA嵌套租约各自持有并只释放自己那一份()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        RunOnApartmentThread(ApartmentState.STA, () =>
        {
            var outer = StaComApartmentLease.Acquire();
            try
            {
                Assert.True(outer.Succeeded, $"外层租约失败：{outer.Status}");

                var inner = StaComApartmentLease.Acquire();
                try
                {
                    // 同一线程上第二份租约同样持有引用（S_FALSE），不是“借用”外层的那一份。
                    Assert.True(inner.Succeeded, $"内层租约失败：{inner.Status}");
                    Assert.True(inner.OwnsReference);
                    Assert.Equal(StaComApartmentLease.S_FALSE, inner.HResult);
                }
                finally
                {
                    inner.Dispose();
                }

                // 内层释放后：内层计数为 1，外层仍持有引用，线程仍是 STA。
                Assert.Equal(1, inner.UninitializeCount);
                Assert.False(inner.OwnsReference);
                Assert.True(outer.OwnsReference);
                Assert.False(outer.IsDisposed);
                Assert.True(
                    CurrentApartmentType() is AptTypeSta or AptTypeMainSta,
                    "内层租约释放后线程不再是 STA。");
            }
            finally
            {
                outer.Dispose();
            }

            Assert.Equal(1, outer.UninitializeCount);
            Assert.True(CurrentApartmentType() is AptTypeSta or AptTypeMainSta);
        });
    }

    [Fact]
    public void 真实MTA线程得到精确ChangedMode且不反初始化别人的引用()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        RunOnApartmentThread(ApartmentState.MTA, () =>
        {
            // 线程以 MTA 启动：真实套间必须是 MTA，STA 请求必须被平台明确拒绝。
            Assert.Equal(AptTypeMta, CurrentApartmentType());

            var lease = StaComApartmentLease.Acquire();
            try
            {
                // 精确断言：不是“任意失败”，而是平台给出的 RPC_E_CHANGED_MODE。
                Assert.Equal(StaComApartmentStatus.ChangedMode, lease.Status);
                Assert.Equal(StaComApartmentLease.RPC_E_CHANGED_MODE, lease.HResult);
                Assert.False(lease.OwnsReference);
                Assert.False(lease.Succeeded);
                Assert.NotNull(lease.FailureReason);
                Assert.Contains("RPC_E_CHANGED_MODE", lease.FailureReason!, StringComparison.Ordinal);
            }
            finally
            {
                lease.Dispose();
            }

            // 绝不反初始化：没有引用、计数为 0，线程仍是 MTA（别人的那份引用完好）。
            Assert.Equal(0, lease.UninitializeCount);
            Assert.True(lease.IsDisposed);
            Assert.Equal(AptTypeMta, CurrentApartmentType());
        });
    }

    [Fact]
    public void 真实MTA线程上的失败租约跨线程释放也不会反初始化()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        RunOnApartmentThread(ApartmentState.MTA, () =>
        {
            var lease = StaComApartmentLease.Acquire();
            Assert.Equal(StaComApartmentStatus.ChangedMode, lease.Status);

            // 失败租约没有引用：任何线程释放都只是标记完成，不调用 CoUninitialize、不计跨线程拒绝。
            var disposeThread = new Thread(lease.Dispose);
            disposeThread.Start();
            Assert.True(disposeThread.Join(TimeSpan.FromSeconds(30)));

            Assert.True(lease.IsDisposed);
            Assert.Equal(0, lease.WrongThreadDisposeCount);
            Assert.Equal(0, lease.UninitializeCount);
            Assert.Equal(AptTypeMta, CurrentApartmentType());
        });
    }
}
