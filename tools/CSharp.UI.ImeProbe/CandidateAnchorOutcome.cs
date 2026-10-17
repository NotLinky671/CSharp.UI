namespace CSharp.UI.ImeProbe;

/// <summary>
/// 候选锚点场景的判定（纯托管，可用确定性测试覆盖）。
/// </summary>
/// <remarks>
/// <para>
/// 本判定<b>不</b>声称“系统候选窗口已经出现在正确位置”。它只回答两件事：
/// 两次锚点发布是否都被 API 接受并读回；两张截图是否在完全相同的窗口矩形内采集。
/// 只有在人工核对截图后，才可能进一步得出“候选可见且随锚点移动”的结论。
/// </para>
/// <para>
/// 因为探针不会枚举或抓取任何非自有窗口，所以本判定最高只到 OBSERVED。
/// </para>
/// </remarks>
internal static class CandidateAnchorOutcome
{
    /// <param name="requestCount">场景内候选锚点发布次数。</param>
    /// <param name="setOkCount">ImmSetCandidateWindow 成功次数。</param>
    /// <param name="getOkCount">ImmGetCandidateWindow 成功次数。</param>
    /// <param name="windowRectStable">两次截图之间窗口矩形是否完全一致。</param>
    /// <param name="composing">发布锚点时是否处于真实组合中。</param>
    internal static CandidateAnchorVerdict Evaluate(
        int requestCount, int setOkCount, int getOkCount, bool windowRectStable, bool composing)
    {
        if (requestCount < 2 || setOkCount < 2 || getOkCount < 2)
        {
            return new CandidateAnchorVerdict(
                "FAIL",
                "anchor publish/readback did not succeed for both in-window anchor points");
        }

        if (!windowRectStable)
        {
            return new CandidateAnchorVerdict(
                "UNVERIFIED",
                "the window rect changed between the two anchor screenshots, so any difference cannot be "
                + "attributed to the anchor point alone");
        }

        if (!composing)
        {
            return new CandidateAnchorVerdict(
                "UNVERIFIED",
                "no composition was active while the anchors were published; there was nothing to position "
                + "a candidate window for");
        }

        return new CandidateAnchorVerdict(
            "OBSERVED",
            "two in-window anchor publishes were accepted and read back, and both own-window screenshots share the "
            + "same window rect. The actual on-screen system candidate position is NOT machine-detected: candidate "
            + "localization stays UNVERIFIED until the two PNGs are inspected and show the candidate and its movement");
    }
}

/// <summary><see cref="CandidateAnchorOutcome.Evaluate"/> 的结论。</summary>
/// <param name="Status">场景状态（FAIL / UNVERIFIED / OBSERVED）。</param>
/// <param name="Reason">状态说明，直接写入场景备注。</param>
internal sealed record CandidateAnchorVerdict(string Status, string Reason);
