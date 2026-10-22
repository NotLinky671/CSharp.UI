using CSharp.UI.ImeProbe;

namespace CSharp.UI.NativeEditReference;

/// <summary>对照场景：只做几何动作，不改变内容、焦点或候选窗口。</summary>
internal static class NativeEditScenario
{
    internal const string InternalScroll = "internal-scroll";
    internal const string ChildMove = "child-move";
    internal const string ChildClip = "child-clip";

    /// <summary>
    /// R28 有界对照：只把**自有** EDIT 高度改为 0（宽/原点不变），观察零可视面积是否
    /// 在不结束组合的前提下让系统候选消失。仅显式 <c>--scenario child-collapse</c> 可触发；
    /// 不进入 <see cref="All"/>，默认三场景与 hidden-smoke 行为完全不变。
    /// </summary>
    internal const string ChildCollapse = "child-collapse";

    internal static readonly string[] All = { InternalScroll, ChildMove, ChildClip };

    /// <summary>opt-in 场景被解析接受，但绝不会被默认 <see cref="All"/> 矩阵执行。</summary>
    internal static bool IsKnown(string? name)
        => name is InternalScroll or ChildMove or ChildClip or ChildCollapse;

    /// <summary>该场景是否有 restore 阶段（真实恢复后截图）。</summary>
    internal static bool HasRestore(string scenario) => scenario is ChildClip or ChildCollapse;

    /// <summary>该场景允许的几何动作标识，供日志与审计核对“只做了被允许的动作”。</summary>
    internal static string ActionKind(string scenario) => scenario switch
    {
        InternalScroll => "em-linescroll",
        ChildMove => "setwindowpos-child",
        ChildClip => "setwindowpos-child-clip",
        ChildCollapse => "setwindowpos-child-collapse",
        _ => "none",
    };
}

/// <summary>
/// 执行结论。退出码只说明“执行与数据完整性”，**不是**视觉验收结论：
/// 视觉一律 <see cref="CandidateVisual"/> = pending，由人工看截图判定。
/// </summary>
internal static class NativeEditOutcome
{
    internal const int Pass = 0;
    internal const int Failed = 1;
    internal const int Precondition = 2;
    internal const int VisualPending = 3;

    /// <summary>
    /// 只有“所有检查通过且无待视觉项”才允许 0。
    /// 本工具全部场景都要求人工视觉判定，因此成功路径恒为 <see cref="VisualPending"/>。
    /// </summary>
    internal static int Resolve(bool checksPass, bool visualPending, bool preconditionMissing, bool aborted)
    {
        if (preconditionMissing)
        {
            return Precondition;
        }

        if (aborted || !checksPass)
        {
            return Failed;
        }

        return visualPending ? VisualPending : Pass;
    }
}

/// <summary>
/// 观测到的记录不变量：用于自我核对“数据完整”，不用于判断候选是否跟随。
/// </summary>
internal static class NativeEditObservation
{
    /// <summary>
    /// 未提交字符串是否表明组合仍活动。仅凭 WM_GETTEXT 无法判断，
    /// 必须结合 GCS_COMPSTR 的 composition 计数与 result/end 计数。
    /// 注意：`start>0 && end==0` 本身不足以证明存在**非空**中文组合串，
    /// 因此这里额外要求非空 CompStr。
    /// </summary>
    internal static bool CompositionActive(int compositionEvents, int endEvents, string? uncommitted)
        => compositionEvents > 0 && endEvents == 0 && uncommitted is { Length: > 0 };

    /// <summary>组合中出现过 result 字符串或结束事件，说明提交/清理发生。</summary>
    internal static bool HasCommittedOrEnded(int resultStringCount, int endEvents)
        => resultStringCount > 0 || endEvents > 0;

    /// <summary>基线之后才应出现输入；基线之前出现 EN_CHANGE 视为前置污染。</summary>
    internal static bool BaselineDocumentIntact(int enChangeBeforeInput, string baselineText,
        string observedText)
        => enChangeBeforeInput == 0 && string.Equals(baselineText, observedText, StringComparison.Ordinal);
}

/// <summary>
/// EM_POSFROMCHAR 返回值解析：低 16 位 x、高 16 位 y，各自为**有符号** 16 位。
/// 官方：https://learn.microsoft.com/en-us/windows/win32/controls/em-posfromchar
/// </summary>
internal static class EditPosFromChar
{
    internal const long Failure = -1;

    internal static bool IsFailure(long result) => result == Failure;

    internal static (short X, short Y) Unpack(long result)
        => ((short)(result & 0xFFFF), (short)((result >> 16) & 0xFFFF));
}

/// <summary>
/// 决定 collapse 流程**该调用哪一次** ResizeEditChild、传什么尺寸、用什么标志。
/// 与托管实现解耦：测试可以不建窗口直接验证“动作把高度压成 0、恢复还原原尺寸”，
/// 而不是只靠枚举/HasRestore 注册断言。
/// </summary>
internal static class CollapseResizePlan
{
    /// <summary>
    /// collapse 的目标尺寸：宽度不变、高度 0。
    /// 返回 null = 原几何不可用（宽/高任意一边 ≤0），此时不得发起 resize。
    /// </summary>
    internal static (int Width, int Height)? CollapseTarget(int originalWidth, int originalHeight)
        => originalWidth > 0 && originalHeight > 0 ? (originalWidth, 0) : null;

    /// <summary>restore 的目标尺寸：原宽高原样回写。</summary>
    internal static (int Width, int Height) RestoreTarget(int originalWidth, int originalHeight)
        => (originalWidth, originalHeight);

    /// <summary>collapse 的 SetWindowPos 标志：只改尺寸，不动位置/z序/激活。</summary>
    internal static uint Flags => NativeMethods.SWP_NOMOVE
        | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE;

    /// <summary>collapse 是否真正完成：窗口与 client 的高度都必须是 0。</summary>
    internal static bool CollapsedToZeroHeight(int windowHeight, int clientHeight)
        => windowHeight == 0 && clientHeight == 0;

    /// <summary>collapse 后几何是否全部不变：原点、宽度、父窗与 viewport。</summary>
    internal static bool GeometryPreservedAfterCollapse(
        int windowHeight, int clientHeight,
        int leftBefore, int topBefore, int widthBefore,
        int leftAfter, int topAfter, int widthAfter,
        bool parentUnchanged, bool viewportUnchanged)
        => CollapsedToZeroHeight(windowHeight, clientHeight)
            && leftAfter == leftBefore && topAfter == topBefore
            && widthAfter == widthBefore && parentUnchanged && viewportUnchanged;

    /// <summary>restore 是否真正完成：原点与宽/高都回到记录的原始值。</summary>
    internal static bool RestoredToOriginal(
        int leftOrig, int topOrig, int widthOrig, int heightOrig,
        int leftNow, int topNow, int widthNow, int heightNow)
        => leftNow == leftOrig && topNow == topOrig
            && widthNow == widthOrig && heightNow == heightOrig;
}

/// <summary>基线文本与目标行位置（供运行器与测试共用，避免“行末”算错）。</summary>
internal static class EditBaseline
{
    internal const int LineCount = 80;

    internal const int TargetLine = 3;

    internal static string Text()
    {
        var lines = new List<string>(LineCount);
        for (var i = 0; i < LineCount; i++)
        {
            lines.Add(LineText(i));
        }

        return string.Join("\r\n", lines);
    }

    internal static string LineText(int line) => $"line{line}";

    internal static int LineLength(int line) => LineText(line).Length;

    /// <summary>目标行在文档中的起始 ACP（每行等长，加 CRLF 两个码元）。</summary>
    internal static int TargetLineStart() => (LineLength(0) + 2) * TargetLine;

    /// <summary>目标行行末 ACP。</summary>
    internal static int TargetLineEnd() => TargetLineStart() + LineLength(TargetLine);
}
