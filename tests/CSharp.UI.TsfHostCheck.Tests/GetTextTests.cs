using System.Runtime.InteropServices;
using Xunit;

namespace CSharp.UI.TsfHostCheck.Tests;

public sealed class GetTextTests
{
    [Fact]
    public void PlainOnlyReturnsPartialBuffer()
    {
        var (store, sink) = StoreTestHelpers.NewStore();
        StoreTestHelpers.UnderLock(store, sink, TsfFlags.TS_LF_READWRITE,
            () => StoreTestHelpers.SetText(store, 0, 0, "abc"));

        var result = Read(store, sink, 0, 3, plainCapacity: 2, runCapacity: 0);

        Assert.Equal(TsfHResult.S_OK, result.Hr);
        Assert.Equal("ab", result.Plain);
        Assert.Equal(2u, result.PlainRet);
        Assert.Equal(0u, result.RunRet);
        Assert.Equal(2, result.Next);
    }

    [Fact]
    public void RunOnlyAdvancesThroughDocument()
    {
        var (store, sink) = StoreTestHelpers.NewStore();
        StoreTestHelpers.UnderLock(store, sink, TsfFlags.TS_LF_READWRITE,
            () => StoreTestHelpers.SetText(store, 0, 0, "abc"));

        var result = Read(store, sink, 0, -1, plainCapacity: 0, runCapacity: 1);

        Assert.Equal(TsfHResult.S_OK, result.Hr);
        Assert.Equal(string.Empty, result.Plain);
        Assert.Equal(0u, result.PlainRet);
        Assert.Equal(1u, result.RunRet);
        Assert.Equal(3u, result.RunCount);
        Assert.Equal(3, result.Next);
    }

    [Fact]
    public void BothBuffersDescribeCopiedText()
    {
        var (store, sink) = StoreTestHelpers.NewStore();
        StoreTestHelpers.UnderLock(store, sink, TsfFlags.TS_LF_READWRITE,
            () => StoreTestHelpers.SetText(store, 0, 0, "abc"));

        var result = Read(store, sink, 0, 3, plainCapacity: 2, runCapacity: 1);

        Assert.Equal(TsfHResult.S_OK, result.Hr);
        Assert.Equal("ab", result.Plain);
        Assert.Equal(2u, result.PlainRet);
        Assert.Equal(1u, result.RunRet);
        Assert.Equal(2u, result.RunCount);
        Assert.Equal(2, result.Next);
    }

    [Fact]
    public void EndOfDocumentDoesNotAdvance()
    {
        var (store, sink) = StoreTestHelpers.NewStore();
        StoreTestHelpers.UnderLock(store, sink, TsfFlags.TS_LF_READWRITE,
            () => StoreTestHelpers.SetText(store, 0, 0, "abc"));

        var result = Read(store, sink, 3, 3, plainCapacity: 2, runCapacity: 1);

        Assert.Equal(TsfHResult.S_OK, result.Hr);
        Assert.Equal(string.Empty, result.Plain);
        Assert.Equal(0u, result.RunRet);
        Assert.Equal(3, result.Next);
    }

    [Fact]
    public void NullPlainBufferWithCapacityIsRejected()
    {
        var (store, sink) = StoreTestHelpers.NewStore();
        var hr = 0;
        StoreTestHelpers.UnderLock(store, sink, TsfFlags.TS_LF_READ, () =>
        {
            hr = store.GetText(0, 0, IntPtr.Zero, 1, out _, IntPtr.Zero, 0, out _, out _);
            return hr;
        });
        Assert.Equal(TsfHResult.E_INVALIDARG, hr);
    }

    [Fact]
    public void NullRunBufferWithCapacityIsRejected()
    {
        var (store, sink) = StoreTestHelpers.NewStore();
        var hr = 0;
        StoreTestHelpers.UnderLock(store, sink, TsfFlags.TS_LF_READ, () =>
        {
            hr = store.GetText(0, 0, IntPtr.Zero, 0, out _, IntPtr.Zero, 1, out _, out _);
            return hr;
        });
        Assert.Equal(TsfHResult.E_INVALIDARG, hr);
    }

    [Fact]
    public void InvalidRangeIsRejected()
    {
        var (store, sink) = StoreTestHelpers.NewStore();
        var hr = 0;
        StoreTestHelpers.UnderLock(store, sink, TsfFlags.TS_LF_READ, () =>
        {
            hr = store.GetText(1, 0, IntPtr.Zero, 0, out _, IntPtr.Zero, 0, out _, out _);
            return hr;
        });
        Assert.Equal(TsfHResult.TS_E_INVALIDPOS, hr);
    }

    private static (int Hr, string Plain, uint PlainRet, uint RunCount, uint RunRet, int Next) Read(
        AcpTextStore store, RecordingTextStoreSink sink, int start, int end,
        int plainCapacity, int runCapacity)
    {
        var plainPtr = plainCapacity > 0 ? Marshal.AllocHGlobal(plainCapacity * 2) : IntPtr.Zero;
        var runPtr = runCapacity > 0
            ? Marshal.AllocHGlobal(Marshal.SizeOf<TsRunInfo>() * runCapacity)
            : IntPtr.Zero;
        try
        {
            var hr = 0;
            var plainRet = 0u;
            var runRet = 0u;
            var next = 0;
            StoreTestHelpers.UnderLock(store, sink, TsfFlags.TS_LF_READ, () =>
            {
                hr = store.GetText(start, end, plainPtr, (uint)plainCapacity, out plainRet,
                    runPtr, (uint)runCapacity, out runRet, out next);
                return hr;
            });

            var plain = plainPtr != IntPtr.Zero && plainRet > 0
                ? Marshal.PtrToStringUni(plainPtr, (int)plainRet) ?? string.Empty
                : string.Empty;
            var runCount = runPtr != IntPtr.Zero && runRet > 0
                ? Marshal.PtrToStructure<TsRunInfo>(runPtr).Count
                : 0u;
            return (hr, plain, plainRet, runCount, runRet, next);
        }
        finally
        {
            if (plainPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(plainPtr);
            }

            if (runPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(runPtr);
            }
        }
    }
}
