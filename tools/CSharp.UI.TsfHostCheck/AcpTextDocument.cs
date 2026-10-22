using System.Runtime.InteropServices;

namespace CSharp.UI.TsfHostCheck;

internal enum AcpEditOrigin
{
    /// <summary>应用自身（普通字符、退格、点击定位）。</summary>
    App,

    /// <summary>TSF/输入法在锁会话内写入（预编辑或结果）。</summary>
    Tsf,
}

internal readonly record struct AcpTextChange(int Start, int OldEnd, int NewEnd, long Version)
{
    internal int Delta => NewEnd - OldEnd;

    internal int InsertedLength => NewEnd - Start;
}

/// <summary>
/// ACP 文档模型：UTF-16 码元、文档版本、单一选区。纯托管、无 COM/窗口依赖，供正式单元测试直接驱动。
/// </summary>
/// <remarks>
/// ACP 偏移是 UTF-16 码元偏移，不是标量/字素；允许在代理对中间切分，读写都保持原码元。
/// 任何写入都必须由调用方（<see cref="AcpTextStore"/>）先取得写锁；本类只做边界与版本。
/// </remarks>
internal sealed class AcpTextDocument
{
    private readonly List<char> _text = new();
    private int _selectionStart;
    private int _selectionEnd;

    internal long Version { get; private set; }

    internal int Length => _text.Count;

    internal string Text => new(_text.ToArray());

    internal (int Start, int End) Selection => (_selectionStart, _selectionEnd);

    /// <summary>文本写入后触发；参数为变更与来源。</summary>
    internal event Action<AcpTextChange, AcpEditOrigin>? Changed;

    internal event Action? SelectionChanged;

    internal void Reset(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        _text.Clear();
        _text.AddRange(text);
        _selectionStart = 0;
        _selectionEnd = 0;
        Version++;
    }

    internal bool IsValidRange(int start, int end)
        => start >= 0 && end >= start && end <= _text.Count;

    internal string GetText(int start, int end)
    {
        if (!IsValidRange(start, end))
        {
            throw new ArgumentOutOfRangeException(nameof(start), $"ACP range [{start},{end}) is outside [0,{_text.Count}].");
        }

        return new string(CollectionsMarshal.AsSpan(_text)[start..end]);
    }

    internal char GetCodeUnit(int acp)
    {
        if (acp < 0 || acp >= _text.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(acp));
        }

        return _text[acp];
    }

    /// <summary>替换 [start,end) 为 text；返回 false 表示范围非法（调用方返回 TS_E_INVALIDPOS）。</summary>
    internal bool TryReplace(int start, int end, string text, AcpEditOrigin origin, out AcpTextChange change)
    {
        change = default;
        if (!IsValidRange(start, end) || text is null)
        {
            return false;
        }

        _text.RemoveRange(start, end - start);
        _text.InsertRange(start, text);
        Version++;

        _selectionStart = MapPoint(_selectionStart, start, end, text.Length);
        _selectionEnd = MapPoint(_selectionEnd, start, end, text.Length);

        change = new AcpTextChange(start, end, start + text.Length, Version);
        Changed?.Invoke(change, origin);
        return true;
    }

    internal bool TrySetSelection(int start, int end)
    {
        if (!IsValidRange(start, end))
        {
            return false;
        }

        if (start == _selectionStart && end == _selectionEnd)
        {
            return true;
        }

        _selectionStart = start;
        _selectionEnd = end;
        SelectionChanged?.Invoke();
        return true;
    }

    private static int MapPoint(int point, int start, int oldEnd, int insertedLength)
    {
        if (point <= start)
        {
            return point;
        }

        if (point >= oldEnd)
        {
            return point + insertedLength - (oldEnd - start);
        }

        return start + insertedLength;
    }
}
