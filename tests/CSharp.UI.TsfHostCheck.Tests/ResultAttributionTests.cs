using Xunit;

namespace CSharp.UI.TsfHostCheck.Tests;

public sealed class ResultAttributionTests
{
    [Fact]
    public void CompositionCommitsExactlyOnce()
    {
        var (store, sink) = StoreTestHelpers.NewStore();
        var recorder = new BindingRecorder(store, StoreTestHelpers.NoTrace);
        store.ResetDocument(string.Empty);
        store.BeginComposition(0, 0);

        StoreTestHelpers.UnderLock(store, sink, TsfFlags.TS_LF_READWRITE, () =>
        {
            var hr = StoreTestHelpers.InsertText(store, "ni");
            return hr < 0 ? hr : StoreTestHelpers.SetText(store, 0, 2, "你好");
        });

        var committed = store.EndComposition();
        Assert.Equal("你好", committed);
        Assert.Equal("你好", store.Document.Text);
        Assert.Equal(1, recorder.Writes);
        Assert.Equal("tsf-composition", recorder.Entries[0].Source);
    }

    [Fact]
    public void RepeatedEndCompositionIsIdempotent()
    {
        var (store, sink) = StoreTestHelpers.NewStore();
        var recorder = new BindingRecorder(store, StoreTestHelpers.NoTrace);
        store.ResetDocument(string.Empty);
        store.BeginComposition(0, 0);
        StoreTestHelpers.UnderLock(store, sink, TsfFlags.TS_LF_READWRITE,
            () => StoreTestHelpers.InsertText(store, "a"));

        store.EndComposition();
        store.EndComposition();
        store.EndComposition();
        Assert.Equal(1, recorder.Writes);
    }

    [Fact]
    public void CancelledCompositionWritesNothing()
    {
        var (store, sink) = StoreTestHelpers.NewStore();
        var recorder = new BindingRecorder(store, StoreTestHelpers.NoTrace);
        store.ResetDocument(string.Empty);
        store.BeginComposition(0, 0);

        StoreTestHelpers.UnderLock(store, sink, TsfFlags.TS_LF_READWRITE, () =>
        {
            var hr = StoreTestHelpers.InsertText(store, "ni");
            return hr < 0 ? hr : StoreTestHelpers.SetText(store, 0, 2, "");
        });

        var committed = store.EndComposition();
        Assert.Equal(string.Empty, committed);
        Assert.Equal(0, recorder.Writes);
        Assert.Equal(string.Empty, store.Document.Text);
    }

    [Fact]
    public void DirectTsfResultWithoutCompositionWritesOnce()
    {
        var (store, sink) = StoreTestHelpers.NewStore();
        var recorder = new BindingRecorder(store, StoreTestHelpers.NoTrace);
        store.ResetDocument(string.Empty);

        StoreTestHelpers.UnderLock(store, sink, TsfFlags.TS_LF_READWRITE,
            () => StoreTestHelpers.InsertText(store, "A"));

        Assert.Equal(1, recorder.Writes);
        Assert.Equal("tsf-direct", recorder.Entries[0].Source);
    }

    [Fact]
    public void PreeditChangesAreSuppressed()
    {
        var (store, sink) = StoreTestHelpers.NewStore();
        var recorder = new BindingRecorder(store, StoreTestHelpers.NoTrace);
        store.ResetDocument(string.Empty);
        store.BeginComposition(0, 0);

        StoreTestHelpers.UnderLock(store, sink, TsfFlags.TS_LF_READWRITE, () =>
        {
            var hr = StoreTestHelpers.InsertText(store, "n");
            if (hr < 0)
            {
                return hr;
            }

            hr = StoreTestHelpers.InsertText(store, "i");
            if (hr < 0)
            {
                return hr;
            }

            return StoreTestHelpers.InsertText(store, "h");
        });

        Assert.Equal(0, recorder.Writes);
        Assert.Equal("nih", store.Document.Text);
    }

    [Fact]
    public void ApplicationCharactersWriteOnceEach()
    {
        var (store, sink) = StoreTestHelpers.NewStore();
        var recorder = new BindingRecorder(store, StoreTestHelpers.NoTrace);
        store.ResetDocument(string.Empty);

        StoreTestHelpers.UnderLock(store, sink, TsfFlags.TS_LF_READWRITE,
            () => store.ApplyAppText("a"));
        StoreTestHelpers.UnderLock(store, sink, TsfFlags.TS_LF_READWRITE,
            () => store.ApplyAppText("b"));

        Assert.Equal(2, recorder.Writes);
        Assert.Equal("ab", store.Document.Text);
        Assert.Equal((2, 2), store.Document.Selection);
    }

    [Fact]
    public void MidDocumentInsertionAdvancesCaret()
    {
        var (store, sink) = StoreTestHelpers.NewStore();
        store.ResetDocument("ab");
        StoreTestHelpers.UnderLock(store, sink, TsfFlags.TS_LF_READWRITE,
            () => store.ApplyAppSelection(1, 1));
        StoreTestHelpers.UnderLock(store, sink, TsfFlags.TS_LF_READWRITE,
            () => store.ApplyAppText("X"));

        Assert.Equal("aXb", store.Document.Text);
        Assert.Equal((2, 2), store.Document.Selection);
    }

    [Fact]
    public void BackspaceCountsAsApplicationWrite()
    {
        var (store, sink) = StoreTestHelpers.NewStore();
        var recorder = new BindingRecorder(store, StoreTestHelpers.NoTrace);
        store.ResetDocument("ab");
        StoreTestHelpers.UnderLock(store, sink, TsfFlags.TS_LF_READWRITE,
            () => store.ApplyAppSelection(2, 2));
        StoreTestHelpers.UnderLock(store, sink, TsfFlags.TS_LF_READWRITE,
            () => store.ApplyAppBackspace());

        Assert.Equal(1, recorder.Writes);
        Assert.Equal("a", store.Document.Text);
    }
}
