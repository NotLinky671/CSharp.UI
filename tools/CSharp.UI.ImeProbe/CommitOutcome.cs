using System.Text;

namespace CSharp.UI.ImeProbe;

/// <summary>
/// 提交结果语义校验（纯托管，可用确定性测试覆盖）。
/// </summary>
/// <remarks>
/// <para>
/// “段数恰好为 1”只证明<b>计数</b>正确，不证明提交内容符合预期。真实证据里出现过
/// <c>GCS_RESULTSTR="niha"</c>（拉丁字母）这类结果：那是失焦时把未转换的拼音串直接吐回，
/// 与“按空格选词提交出中文”是两件事。因此场景必须同时核对：
/// </para>
/// <list type="number">
/// <item><description>结果非空；</description></item>
/// <item><description>当要求中文时，结果里含有非 ASCII 字符（本轮真实证据用的就是纯 ASCII 回吐 vs 中文）；</description></item>
/// <item><description>当给定明确预期内容（例如固定测试串 <c>nihao</c> → <c>你好</c>）时，结果必须等于该预期内容；</description></item>
/// <item><description>拼音串是<b>预编辑历史</b>的证据：只有当本次组合会话的预编辑里出现过该拼音（规范化后
/// 匹配，忽略 <c>'</c> 等音节分隔符），这个结果才能归因于本场景的输入。</description></item>
/// </list>
/// <para>
/// 注意：拼音子串<b>绝不能</b>要求出现在中文结果里 —— “你好”不含 <c>nihao</c>，那样会把正确的中文选词判成失败。
/// </para>
/// <para>这里只做判定，不修改任何输入状态。</para>
/// </remarks>
internal static class CommitOutcome
{
    /// <summary>
    /// 固定测试串的明确预期结果（只覆盖本轮真实验证用的固定串，不做通用拼音转换）。
    /// </summary>
    /// <remarks>
    /// 固定测试串才写死预期内容；其他输入返回 null，此时判定退化为“非 ASCII + 必须由本会话预编辑归因”。
    /// </remarks>
    internal static string? ExpectedResultFor(string? keys)
        => keys?.Trim().ToLowerInvariant() switch
        {
            "nihao" => "你好",
            "ni" => "你",
            "ma" => "吗",
            _ => null,
        };

    /// <summary>是否整段都是可打印 ASCII（这类结果通常是未转换的拼音/英文回吐，不是中文选词）。</summary>
    internal static bool IsAsciiOnly(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        foreach (var ch in value)
        {
            if (ch > 0x7F)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>是否至少包含一个非 ASCII（CJK 及全角等）码元。</summary>
    internal static bool ContainsNonAscii(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        foreach (var ch in value)
        {
            if (ch > 0x7F)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>预期的拼音预编辑串是否作为子串出现在提交结果里（前缀匹配，允许 IME 把 'h' 规范化成分隔符）。</summary>
    internal static bool ContainsExpectedPinyinSubstring(string? result, string? expectedPinyin)
    {
        if (string.IsNullOrEmpty(result) || string.IsNullOrEmpty(expectedPinyin))
        {
            return false;
        }

        var normalized = NormalizePinyin(expectedPinyin);
        if (normalized.Length < 2)
        {
            return false;
        }

        // IME 会在歧义音节之间插入分隔符（例如 ni'h）；比较时把 ' / ` 等分隔符去掉。
        return NormalizePinyin(result).Contains(normalized, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>去掉拼音串里的音节分隔符，只保留字母数字（用于子串比较）。</summary>
    internal static string NormalizePinyin(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// 按“是否要求中文选词结果”判定提交内容是否满足预期。
    /// </summary>
    /// <param name="result">本条消息读到的 GCS_RESULTSTR。</param>
    /// <param name="expectedResult">
    /// 明确预期内容（固定测试串用，例如 <c>nihao</c> → <c>你好</c>）。为 null/空表示没有固定预期内容。
    /// </param>
    /// <param name="requireChinese">
    /// 是否要求结果是中文（含非 ASCII）。只有明确走中文输入法并期望选词时为 true；
    /// 美国键盘布局（普通英文/合成边界）场景为 false，避免把英文路径误判成失败。
    /// </param>
    /// <param name="expectedPinyin">
    /// 本场景注入的拼音串。它应当出现在本次组合会话的<b>预编辑</b>里，而不是出现在中文结果里。
    /// </param>
    /// <param name="preeditHistory">本次组合会话记录到的预编辑串序列（用于归因核对）。</param>
    internal static CommitContentVerdict Evaluate(
        string? result, string? expectedResult, bool requireChinese, string? expectedPinyin = null,
        IEnumerable<string>? preeditHistory = null)
    {
        if (string.IsNullOrEmpty(result))
        {
            return new CommitContentVerdict(
                false, false, false, "the result string is empty; there is nothing to commit");
        }

        var pinyinSeenInPreedit = false;
        if (!string.IsNullOrEmpty(expectedPinyin) && preeditHistory is not null)
        {
            var normalized = NormalizePinyin(expectedPinyin);
            foreach (var preedit in preeditHistory)
            {
                if (preedit is null)
                {
                    continue;
                }

                if (normalized.Length >= 2
                    && NormalizePinyin(preedit).Contains(normalized, StringComparison.OrdinalIgnoreCase))
                {
                    pinyinSeenInPreedit = true;
                    break;
                }
            }
        }

        if (!requireChinese)
        {
            return new CommitContentVerdict(
                true, pinyinSeenInPreedit, false,
                "the scenario does not require a Chinese commit result (plain keyboard path), so only presence is asserted");
        }

        if (IsAsciiOnly(result))
        {
            return new CommitContentVerdict(
                false, pinyinSeenInPreedit, pinyinSeenInPreedit,
                $"the commit result is plain ASCII (\"{result}\"): the IME returned the un-converted preedit "
                + "instead of a Chinese candidate selection, so this is NOT a verified Chinese commit");
        }

        if (!ContainsNonAscii(result))
        {
            return new CommitContentVerdict(
                false, pinyinSeenInPreedit, pinyinSeenInPreedit,
                "the commit result contains no non-ASCII code unit");
        }

        // 固定测试串：直接核对明确预期内容，而不是用“非 ASCII”一概代替中文。
        // 内容相符仍不够：必须同时能由本次组合会话的预编辑归因，否则可能是旧会话/别处来的结果。
        if (!string.IsNullOrEmpty(expectedResult))
        {
            var contentMatched = string.Equals(result, expectedResult, StringComparison.Ordinal);
            var satisfied = contentMatched && pinyinSeenInPreedit;
            var reason = contentMatched
                ? pinyinSeenInPreedit
                    ? $"the commit result equals the expected content \"{expectedResult}\" and the injected pinyin "
                        + $"\"{expectedPinyin}\" was observed in this session's preedit"
                    : $"the commit result equals the expected content \"{expectedResult}\", but the injected pinyin "
                        + $"\"{expectedPinyin}\" was NOT observed in this session's preedit, so the result cannot be "
                        + "attributed to this scenario"
                : $"the commit result \"{result}\" does not equal the expected content \"{expectedResult}\""
                    + (pinyinSeenInPreedit ? " (although the injected pinyin was seen in this session's preedit)" : string.Empty);
            return new CommitContentVerdict(satisfied, pinyinSeenInPreedit, contentMatched, reason);
        }

        // 没有固定预期内容时：只要不是纯 ASCII 回吐就接受内容，但必须由本会话预编辑归因。
        if (!pinyinSeenInPreedit)
        {
            return new CommitContentVerdict(
                false, false, true,
                $"the commit result \"{result}\" is not attributable to this scenario: the expected pinyin "
                + $"\"{expectedPinyin}\" was never observed in this composition session's preedit "
                + "(it may come from an earlier session)");
        }

        return new CommitContentVerdict(
            true, true, true,
            "the commit result contains non-ASCII text and the injected pinyin was observed in this session's preedit");
    }
}

/// <summary><see cref="CommitOutcome.Evaluate"/> 的判定结果。</summary>
/// <param name="Satisfied">是否满足本场景对提交内容的要求。</param>
/// <param name="PinyinSeenInPreedit">注入的拼音是否出现在<b>本次组合会话的预编辑</b>里（归因依据）。</param>
/// <param name="ContentMatched">结果内容是否与明确预期一致（无固定预期内容时为“非 ASCII 回吐”判定）。</param>
/// <param name="Reason">判定说明，直接写入场景备注与日志。</param>
internal sealed record CommitContentVerdict(
    bool Satisfied, bool PinyinSeenInPreedit, bool ContentMatched, string Reason);
