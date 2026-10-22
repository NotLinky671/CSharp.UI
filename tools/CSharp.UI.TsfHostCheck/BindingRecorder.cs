using CSharp.UI.ImeProbe;

namespace CSharp.UI.TsfHostCheck;

internal readonly record struct BindingWrite(string Text, string Source, long Version, long Sequence);

/// <summary>
/// 绑定写入归属：组合期间的 TSF 写入是预编辑、不计数；组合结束的非空最终文本恰好计一次；
/// 无组合的 TSF 结果按会话计一次；应用字符每次计一次。
/// </summary>
internal sealed class BindingRecorder
{
    private readonly AcpTextStore _store;
    private readonly TsfTrace _trace;
    private readonly object _gate = new();
    private readonly List<BindingWrite> _entries = new();
    private long _sequence;

    internal BindingRecorder(AcpTextStore store, TsfTrace trace)
    {
        _store = store;
        _trace = trace;
        store.ChangeObserver = OnChange;
        store.CommitObserver = OnCommit;
    }

    internal int Writes
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    internal IReadOnlyList<BindingWrite> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToArray();
            }
        }
    }

    internal string CommittedText
    {
        get
        {
            lock (_gate)
            {
                return string.Concat(_entries.Select(e => e.Text));
            }
        }
    }

    private void OnChange(AcpTextChange change, AcpEditOrigin origin, bool composing, string inserted)
    {
        if (origin == AcpEditOrigin.Tsf && composing)
        {
            _trace("binding", "preedit-suppressed", new Dictionary<string, object?>
            {
                ["start"] = change.Start,
                ["new_end"] = change.NewEnd,
                ["version"] = change.Version,
            });
            return;
        }

        Record(inserted, origin == AcpEditOrigin.Tsf ? "tsf-direct" : "app", change.Version);
    }

    private void OnCommit(string text)
    {
        if (text.Length == 0)
        {
            _trace("binding", "cancel-no-write", null);
            return;
        }

        Record(text, "tsf-composition", _store.Document.Version);
    }

    private void Record(string text, string source, long version)
    {
        BindingWrite entry;
        lock (_gate)
        {
            entry = new BindingWrite(text, source, version, ++_sequence);
            _entries.Add(entry);
        }

        _trace("binding", "write", new Dictionary<string, object?>
        {
            ["sequence"] = entry.Sequence,
            ["source"] = source,
            ["version"] = version,
            ["text_utf16_units"] = text.Length,
            ["text"] = ImeFormat.EscapeUtf16(text),
        });
    }
}
