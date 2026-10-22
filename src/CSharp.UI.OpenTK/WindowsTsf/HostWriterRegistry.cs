namespace CSharp.UI.OpenTK.WindowsTsf;

/// <summary>占用某个窗口的输入写入来源种类（TI-07 P3a）。</summary>
internal enum HostWriterKind
{
    /// <summary>IMM32 子类化路径（现有默认实现）。</summary>
    Imm = 0,

    /// <summary>TSF 会话路径。</summary>
    Tsf = 1,
}

/// <summary>
/// 一次写入来源<b>租约</b>的唯一身份（TI-07 P3a R8）。
/// </summary>
/// <remarks>
/// <b>为什么需要身份而不是 kind 计数：</b>上一版允许同一 kind 任意重复占用（引用计数），
/// 于是<b>两个不同的 TSF 会话</b>都能对同一 HWND 成功占用并各自建立 store/owner，
/// 违背“一个窗口一个实际 writer”。同 kind 不等于同 owner。
/// 现在只有<b>同一个租约实例</b>可以幂等重复，第二个独立实例必须被拒绝。
/// </remarks>
internal sealed class HostWriterLease
{
    private int _released;

    internal HostWriterLease(IntPtr hwnd, HostWriterKind kind, long leaseId)
    {
        Hwnd = hwnd;
        Kind = kind;
        LeaseId = leaseId;
    }

    /// <summary>租约对应的窗口。</summary>
    internal IntPtr Hwnd { get; }

    /// <summary>租约对应的来源种类。</summary>
    internal HostWriterKind Kind { get; }

    /// <summary>全局唯一的租约序号（诊断与身份比较用）。</summary>
    internal long LeaseId { get; }

    /// <summary>是否已释放。</summary>
    internal bool IsReleased => Volatile.Read(ref _released) != 0;

    internal bool MarkReleased() => Interlocked.Exchange(ref _released, 1) == 0;

    /// <summary>只输出身份，不含用户内容。</summary>
    public override string ToString() => $"lease#{LeaseId} {Kind} hwnd=0x{Hwnd:X}";
}

/// <summary>
/// “每个窗口只有一个输入写入来源”的进程内登记表（TI-07 P3a R8）。
/// </summary>
/// <remarks>
/// <para>
/// <b>语义：</b>以 HWND 为键保存<b>唯一的活动租约</b>。
/// </para>
/// <list type="bullet">
/// <item><description>同一租约实例重复占用：幂等成功。</description></item>
/// <item><description><b>任何</b>第二个独立实例（无论同 kind 与否）：<b>明确拒绝</b>，绝不抢占。</description></item>
/// <item><description>释放必须由真实持有者用自己的租约调用；不能凭 kind 释放别人的占用。</description></item>
/// </list>
/// <para>
/// <b>IMM 也登记：</b>默认 IMM 桥在真实活动时同样持有一个租约，因此 TSF 与 IMM 的冲突
/// 是<b>实际活动</b>的冲突，而不是只对 TSF 生效的声明。
/// </para>
/// </remarks>
internal static class HostWriterRegistry
{
    private static readonly object Gate = new();
    private static readonly Dictionary<IntPtr, HostWriterLease> Owners = [];
    private static long _nextLeaseId;

    /// <summary>
    /// 为窗口申请一个写入来源租约。
    /// </summary>
    /// <param name="hwnd">窗口句柄。</param>
    /// <param name="kind">来源种类。</param>
    /// <param name="lease">成功时返回租约；失败时为 null。</param>
    /// <param name="reason">失败原因（脱敏）。</param>
    /// <returns>true 表示获得；false 表示已被其它独立实例占用。</returns>
    internal static bool TryAcquire(IntPtr hwnd, HostWriterKind kind, out HostWriterLease? lease, out string? reason)
    {
        lease = null;
        reason = null;

        if (hwnd == IntPtr.Zero)
        {
            reason = "窗口句柄为空。";
            return false;
        }

        lock (Gate)
        {
            if (Owners.TryGetValue(hwnd, out var existing))
            {
                if (existing.IsReleased)
                {
                    // 已释放但未移除：清理后允许新占用。
                    Owners.Remove(hwnd);
                }
                else
                {
                    // 已有活动租约：任何第二个独立实例都拒绝（同 kind 也不行）。
                    reason = $"该窗口已被 {existing} 占用；不允许第二个独立写入来源。";
                    return false;
                }
            }

            lease = new HostWriterLease(hwnd, kind, ++_nextLeaseId);
            Owners[hwnd] = lease;
            return true;
        }
    }

    /// <summary>
    /// 由真实持有者释放租约（必须传入自己的租约实例）。
    /// </summary>
    /// <param name="lease">持有者的租约。</param>
    /// <returns>true 表示确实由该租约释放；false 表示不是当前持有者或已释放。</returns>
    internal static bool Release(HostWriterLease? lease)
    {
        if (lease is null)
        {
            return false;
        }

        lock (Gate)
        {
            if (!Owners.TryGetValue(lease.Hwnd, out var existing))
            {
                return false;
            }

            // 身份校验：只有真实持有者能释放，不能凭 kind 释放别人的占用。
            if (!ReferenceEquals(existing, lease))
            {
                return false;
            }

            Owners.Remove(lease.Hwnd);
            lease.MarkReleased();
            return true;
        }
    }

    /// <summary>查询窗口当前的活动租约（null 表示空闲）。</summary>
    internal static HostWriterLease? CurrentLease(IntPtr hwnd)
    {
        lock (Gate)
        {
            return Owners.TryGetValue(hwnd, out var existing) && !existing.IsReleased ? existing : null;
        }
    }

    /// <summary>查询窗口当前被哪种来源占用（null 表示空闲）。</summary>
    internal static HostWriterKind? CurrentOwner(IntPtr hwnd) => CurrentLease(hwnd)?.Kind;

    /// <summary>当前登记的窗口数量（测试核对不泄漏）。</summary>
    internal static int RegisteredWindowCount
    {
        get
        {
            lock (Gate)
            {
                return Owners.Count;
            }
        }
    }
}
