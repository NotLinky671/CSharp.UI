using System.Runtime.InteropServices;
using Xunit;

namespace CSharp.UI.TsfHostCheck.Tests;

internal static class StoreTestHelpers
{
    internal static readonly TsfTrace NoTrace = (_, _, _) => { };

    internal static (AcpTextStore Store, RecordingTextStoreSink Sink) NewStore(
        uint mask = TsfFlags.TS_AS_ALL_SINKS)
    {
        var store = new AcpTextStore(NoTrace);
        var sink = new RecordingTextStoreSink();
        Assert.Equal(TsfHResult.S_OK, store.AdviseSink(TsfIids.TextStoreAcpSink, sink, mask));
        return (store, sink);
    }

    internal static int UnderLock(AcpTextStore store, RecordingTextStoreSink sink, uint flags,
        Func<int> body)
    {
        var result = StoreTestHarness.UnderLock(store, sink, flags, body,
            out var requestHr, out _);
        Assert.Equal(TsfHResult.S_OK, requestHr);
        return result;
    }

    internal static int SetText(AcpTextStore store, int start, int end, string text)
    {
        var pointer = Marshal.StringToHGlobalUni(text);
        try
        {
            return store.SetText(0, start, end, pointer, (uint)text.Length, out _);
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    internal static int InsertText(AcpTextStore store, string text)
    {
        var pointer = Marshal.StringToHGlobalUni(text);
        try
        {
            return store.InsertTextAtSelection(0, pointer, (uint)text.Length,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    internal static string ReadText(AcpTextStore store, RecordingTextStoreSink sink, int start, int end)
    {
        var capacity = Math.Max(1, end - start);
        var buffer = Marshal.AllocHGlobal(capacity * 2);
        try
        {
            var fetched = 0u;
            var hr = 0;
            UnderLock(store, sink, TsfFlags.TS_LF_READ, () =>
            {
                hr = store.GetText(start, end, buffer, (uint)capacity, out fetched,
                    IntPtr.Zero, 0, out _, out _);
                return hr;
            });
            Assert.Equal(TsfHResult.S_OK, hr);
            return fetched == 0 ? string.Empty : Marshal.PtrToStringUni(buffer, (int)fetched) ?? string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static AcpLayoutSnapshot Publish(AcpTextStore store, int[] advances,
        int rowHeight = 20, TsfRect? client = null, int originX = 0, int originY = 0,
        int scrollX = 0, int scrollY = 0, bool notify = true)
    {
        var snapshot = AcpLayoutSnapshot.Build(
            store.LayoutGeneration,
            store.Document.Version,
            store.Document.Text,
            advances,
            rowHeight,
            client ?? TsfRect.FromLtrb(0, 0, 400, 100),
            originX,
            originY,
            scrollX,
            scrollY);
        store.PublishLayout(snapshot, notify);
        return snapshot;
    }

    internal static (int Hr, TsfRect Rect, int Clipped) TextExt(
        AcpTextStore store, RecordingTextStoreSink sink, int start, int end)
    {
        var rect = default(TsfRect);
        var clipped = 0;
        var hr = 0;
        UnderLock(store, sink, TsfFlags.TS_LF_READ, () =>
        {
            hr = store.GetTextExt(1, start, end, out rect, out clipped);
            return hr;
        });
        return (hr, rect, clipped);
    }

    internal static (int Hr, TsfRect Rect) ScreenExt(AcpTextStore store, RecordingTextStoreSink sink)
    {
        var rect = default(TsfRect);
        var hr = 0;
        UnderLock(store, sink, TsfFlags.TS_LF_READ, () =>
        {
            hr = store.GetScreenExt(1, out rect);
            return hr;
        });
        return (hr, rect);
    }

    internal static (int Hr, int Acp) HitTest(
        AcpTextStore store, RecordingTextStoreSink sink, int x, int y, uint flags = 0)
    {
        var point = new TsfPoint { X = x, Y = y };
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<TsfPoint>());
        try
        {
            Marshal.StructureToPtr(point, pointer, false);
            var acp = 0;
            var hr = 0;
            UnderLock(store, sink, TsfFlags.TS_LF_READ, () =>
            {
                hr = store.GetACPFromPoint(1, pointer, flags, out acp);
                return hr;
            });
            return (hr, acp);
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }
}
