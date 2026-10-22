namespace CSharp.UI.TsfHostCheck;

/// <summary>
/// R27 场景 teardown（与真实场景共用，非测试专用实现）。
/// <para>
/// 职责：恢复 <c>SuppressCandidates</c> 原值，并在**仍组合**时做一次受控清理。
/// 失去自有前台时**绝不**发 Esc（那会打到其他应用），而是交由 host 的 session cleanup，
/// 并如实记录；teardown 自身失败也不吞掉被测阶段的结果。
/// </para>
/// </summary>
internal static class NativeUiLifecycleTeardown
{
    internal enum Outcome
    {
        /// <summary>不需要清理（已无组合）。</summary>
        NothingToClean,

        /// <summary>已在本场景内完成清理。</summary>
        Cleaned,

        /// <summary>失去前台，未发键，交给 session cleanup。</summary>
        DeferredToSession,

        /// <summary>清理回调返回后组合**仍然存在**：不得声称已清理，交由 session cleanup。</summary>
        CleanupIncomplete,

        /// <summary>teardown 抛异常，已记录。</summary>
        Failed,
    }

    internal readonly record struct Result(Outcome Outcome, string? Error)
    {
        internal bool ShouldReportCleanup
            => Outcome is Outcome.DeferredToSession or Outcome.CleanupIncomplete or Outcome.Failed;
    }

    /// <summary>
    /// 执行 teardown。<paramref name="restoreSuppress"/> 与 <paramref name="cleanupComposition"/>
    /// 由调用方注入，使真实场景与测试走同一段决策逻辑。
    /// </summary>
    internal static Result Run(
        bool previousSuppress,
        Action<bool> restoreSuppress,
        Func<bool> isComposing,
        Func<bool> ownForeground,
        Action cleanupComposition,
        Action<string, IDictionary<string, object?>?> record)
    {
        try
        {
            restoreSuppress(previousSuppress);
            record("native-ui-lifecycle-restore", new Dictionary<string, object?>
            {
                ["suppress_restored_to"] = previousSuppress,
            });

            if (!isComposing())
            {
                return new Result(Outcome.NothingToClean, null);
            }

            if (!ownForeground())
            {
                // 不夺焦点、不向其他应用发键。
                record("native-ui-lifecycle-teardown-deferred", new Dictionary<string, object?>
                {
                    ["reason"] = "own foreground lost; Esc not sent",
                    ["handled_by"] = "session-cleanup",
                });
                return new Result(Outcome.DeferredToSession, null);
            }

            cleanupComposition();

            // 清理回调返回**不代表**组合真的结束了（例如失焦后 Esc 未生效）：
            // 必须回读，仍组合就如实报 Incomplete 并交给 session cleanup，绝不谎报 Cleaned。
            if (isComposing())
            {
                record("native-ui-lifecycle-teardown-incomplete", new Dictionary<string, object?>
                {
                    ["reason"] = "composition still active after cleanup attempt",
                    ["handled_by"] = "session-cleanup",
                });
                return new Result(Outcome.CleanupIncomplete,
                    "composition still active after cleanup");
            }

            record("native-ui-lifecycle-teardown", new Dictionary<string, object?>
            {
                ["composing_after"] = false,
            });
            return new Result(Outcome.Cleaned, null);
        }
        catch (Exception ex)
        {
            // teardown 失败不得掩盖被测阶段的结果。
            try
            {
                record("native-ui-lifecycle-teardown-failed", new Dictionary<string, object?>
                {
                    ["exception"] = ex.GetType().Name,
                    ["message"] = ex.Message,
                    ["handled_by"] = "session-cleanup",
                });
            }
            catch (Exception)
            {
                // 连日志都失败时保持静默，绝不影响宿主。
            }

            return new Result(Outcome.Failed, $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
