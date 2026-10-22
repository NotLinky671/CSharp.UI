using System.Runtime.InteropServices;
using Xunit;

namespace CSharp.UI.TsfHostCheck.Tests;

/// <summary>
/// 几何查询留痕：被拒绝的 GetTextExt/GetScreenExt/GetACPFromPoint 也必须记录 HRESULT、
/// 锁类型、view 与布局代数，否则真实 JSONL 无法区分“TIP 读到新矩形”与
/// “TIP 的查询被拒绝后沿用旧候选位置”。同时验证 OnLayoutChange 重入深度标记，
/// 用来区分 TIP 是否在我们布局通知的同步回调内查询几何。
/// </summary>
public sealed class GeometryTraceTests
{
    private sealed record Entry(string Stage, string Kind, Dictionary<string, object?> Fields);

    private static (AcpTextStore Store, List<Entry> Entries) NewTracedStore()
    {
        var entries = new List<Entry>();
        var store = new AcpTextStore((stage, kind, fields) => entries.Add(new Entry(
            stage,
            kind,
            fields is null ? new Dictionary<string, object?>() : new Dictionary<string, object?>(fields))));
        return (store, entries);
    }

    private static IEnumerable<Entry> Rejections(IEnumerable<Entry> entries)
        => entries.Where(entry => entry.Stage == "geometry" && entry.Kind == "rejected");

    [Fact]
    public void TextExtWithoutReadLockIsTracedAsRejected()
    {
        var (store, entries) = NewTracedStore();

        // 未持锁：官方契约要求 TS_E_NOLOCK；该拒绝必须留痕，不能静默返回。
        var hr = store.GetTextExt(1, 0, 0, out _, out _);

        Assert.Equal(TsfHResult.TS_E_NOLOCK, hr);
        var rejected = Assert.Single(Rejections(entries));
        Assert.Equal("GetTextExt", rejected.Fields["method"]);
        Assert.Equal("TS_E_NOLOCK", rejected.Fields["hresult"]);
        Assert.Equal("0x0", rejected.Fields["lock_type"]);
        Assert.Equal(0, (int)rejected.Fields["layout_notify_depth"]!);
    }

    [Fact]
    public void StaleLayoutRejectionIsTracedWithLockAndGeneration()
    {
        var (store, entries) = NewTracedStore();
        var sink = new RecordingTextStoreSink();
        Assert.Equal(TsfHResult.S_OK, store.AdviseSink(TsfIids.TextStoreAcpSink, sink, TsfFlags.TS_AS_ALL_SINKS));
        StoreTestHelpers.UnderLock(store, sink, TsfFlags.TS_LF_READWRITE,
            () => StoreTestHelpers.SetText(store, 0, 0, "ab"));

        entries.Clear();

        // 有文本但尚未发布布局：读锁已授予，拒绝原因必须是 TS_E_NOLAYOUT 而非 TS_E_NOLOCK。
        var (hr, _, _) = StoreTestHelpers.TextExt(store, sink, 0, 1);

        Assert.Equal(TsfHResult.TS_E_NOLAYOUT, hr);
        var rejected = Assert.Single(Rejections(entries));
        Assert.Equal("GetTextExt", rejected.Fields["method"]);
        Assert.Equal("TS_E_NOLAYOUT", rejected.Fields["hresult"]);
        Assert.Equal("0x2", rejected.Fields["lock_type"]);
        Assert.Equal(store.LayoutGeneration, (long)rejected.Fields["layout_generation"]!);
        Assert.Equal(store.Document.Version, (long)rejected.Fields["document_version"]!);
    }

    [Fact]
    public void InvalidViewRejectionIsTracedWithBothViewCookies()
    {
        var (store, entries) = NewTracedStore();
        var sink = new RecordingTextStoreSink();
        Assert.Equal(TsfHResult.S_OK, store.AdviseSink(TsfIids.TextStoreAcpSink, sink, TsfFlags.TS_AS_ALL_SINKS));
        StoreTestHelpers.UnderLock(store, sink, TsfFlags.TS_LF_READWRITE,
            () => StoreTestHelpers.SetText(store, 0, 0, "ab"));
        StoreTestHelpers.Publish(store, new[] { 10, 10 });

        entries.Clear();

        var hr = 0;
        StoreTestHelpers.UnderLock(store, sink, TsfFlags.TS_LF_READ,
            () => hr = store.GetScreenExt(99, out _));

        // R24：非法 view 为 E_INVALIDARG（官方样例），且该拒绝仍必须留痕。
        Assert.Equal(TsfHResult.E_INVALIDARG, hr);
        var rejected = Assert.Single(Rejections(entries));
        Assert.Equal("GetScreenExt", rejected.Fields["method"]);
        Assert.Equal("E_INVALIDARG", rejected.Fields["hresult"]);
        Assert.Equal(99u, (uint)rejected.Fields["view"]!);
        Assert.Equal(store.ViewCookie, (uint)rejected.Fields["expected_view"]!);
    }

    [Fact]
    public void HitTestRejectionIsTraced()
    {
        var (store, entries) = NewTracedStore();
        var sink = new RecordingTextStoreSink();
        Assert.Equal(TsfHResult.S_OK, store.AdviseSink(TsfIids.TextStoreAcpSink, sink, TsfFlags.TS_AS_ALL_SINKS));
        StoreTestHelpers.UnderLock(store, sink, TsfFlags.TS_LF_READWRITE,
            () => StoreTestHelpers.SetText(store, 0, 0, "ab"));
        StoreTestHelpers.Publish(store, new[] { 10, 10 });

        entries.Clear();

        // 行外且未给 GXFPF_NEAREST：合法拒绝，同样必须留痕。
        Assert.Equal(TsfHResult.TS_E_INVALIDPOINT, StoreTestHelpers.HitTest(store, sink, -5, 5).Hr);
        var rejected = Assert.Single(Rejections(entries));
        Assert.Equal("GetACPFromPoint", rejected.Fields["method"]);
        Assert.Equal("TS_E_INVALIDPOINT", rejected.Fields["hresult"]);
    }

    [Fact]
    public void SuccessfulGeometryRecordsLockTypeAndZeroDepth()
    {
        var (store, entries) = NewTracedStore();
        var sink = new RecordingTextStoreSink();
        Assert.Equal(TsfHResult.S_OK, store.AdviseSink(TsfIids.TextStoreAcpSink, sink, TsfFlags.TS_AS_ALL_SINKS));
        StoreTestHelpers.UnderLock(store, sink, TsfFlags.TS_LF_READWRITE,
            () => StoreTestHelpers.SetText(store, 0, 0, "ab"));
        StoreTestHelpers.Publish(store, new[] { 10, 10 }, notify: false);

        entries.Clear();
        StoreTestHelpers.TextExt(store, sink, 0, 1);

        var textExt = Assert.Single(entries, entry => entry.Kind == "text-ext");
        Assert.Equal("0x2", textExt.Fields["lock_type"]);
        Assert.Equal(0, (int)textExt.Fields["layout_notify_depth"]!);
    }

    [Fact]
    public void GeometryQueriedInsideLayoutNotificationReportsDepthOne()
    {
        var entries = new List<Entry>();
        var store = new AcpTextStore((stage, kind, fields) => entries.Add(new Entry(
            stage,
            kind,
            fields is null ? new Dictionary<string, object?>() : new Dictionary<string, object?>(fields))));
        var sink = new LayoutQuerySink();
        Assert.Equal(TsfHResult.S_OK,
            store.AdviseSink(TsfIids.TextStoreAcpSink, sink, TsfFlags.TS_AS_LAYOUT_CHANGE));
        store.ResetDocument("ab");
        sink.Grant = _ => TsfHResult.S_OK;

        var queryGeometry = () =>
        {
            var rect = default(TsfRect);
            var clipped = 0;
            store.GetTextExt(1, 0, 2, out rect, out clipped);
        };

        // 模拟真实 TIP：在 OnLayoutChange 回调内重新取得读锁并查询几何。
        sink.OnLayout = () =>
        {
            sink.Grant = _ =>
            {
                queryGeometry();
                return TsfHResult.S_OK;
            };
            store.RequestLock(TsfFlags.TS_LF_READ | TsfFlags.TS_LF_SYNC, out _);
            sink.Grant = _ => TsfHResult.S_OK;
        };

        store.PublishLayout(AcpLayoutSnapshot.Build(
            store.LayoutGeneration, store.Document.Version, "ab", new[] { 10, 10 }, 20,
            TsfRect.FromLtrb(0, 0, 200, 60), 0, 0, 0, 0), notify: true);

        var textExt = entries.Last(entry => entry.Kind == "text-ext");
        Assert.Equal(1, (int)textExt.Fields["layout_notify_depth"]!);
        Assert.Equal("0x2", textExt.Fields["lock_type"]);

        // 通知自身也记录重入深度。
        var layout = entries.Last(entry => entry.Kind == "layout");
        Assert.Equal(1, (int)layout.Fields["layout_notify_depth"]!);

        // 通知返回后深度归零：无关的读锁查询必须记录 0。
        entries.Clear();
        sink.Grant = _ =>
        {
            queryGeometry();
            return TsfHResult.S_OK;
        };
        store.RequestLock(TsfFlags.TS_LF_READ | TsfFlags.TS_LF_SYNC, out _);
        var afterNotification = Assert.Single(entries, entry => entry.Kind == "text-ext");
        Assert.Equal(0, (int)afterNotification.Fields["layout_notify_depth"]!);
    }

    private sealed class LayoutQuerySink : ITextStoreACPSink
    {
        internal Func<uint, int>? Grant { get; set; }

        internal Action? OnLayout { get; set; }

        public int OnTextChange(uint dwFlags, ref TsTextChange pChange) => TsfHResult.S_OK;

        public int OnSelectionChange() => TsfHResult.S_OK;

        public int OnLayoutChange(int lcode, uint vcView)
        {
            OnLayout?.Invoke();
            return TsfHResult.S_OK;
        }

        public int OnStatusChange(uint dwFlags) => TsfHResult.S_OK;

        public int OnAttrsChange(int acpStart, int acpEnd, uint cAttrs, IntPtr paAttrs)
            => TsfHResult.S_OK;

        public int OnLockGranted(uint dwLockFlags) => Grant?.Invoke(dwLockFlags) ?? TsfHResult.S_OK;

        public int OnStartEditTransaction() => TsfHResult.S_OK;

        public int OnEndEditTransaction() => TsfHResult.S_OK;
    }
}
