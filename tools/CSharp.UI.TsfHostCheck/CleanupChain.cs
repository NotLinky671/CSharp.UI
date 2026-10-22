namespace CSharp.UI.TsfHostCheck;

internal readonly record struct CleanupStepResult(string Name, int HResult, Exception? Exception);

internal sealed record CleanupReport(int Failures, IReadOnlyList<CleanupStepResult> Steps);

/// <summary>
/// 顺序清理链：单步失败（负 HRESULT 或托管异常）不会跳过后续步骤，全部执行后再报告失败数。
/// 纯托管，可对失败注入路径做正式测试。
/// </summary>
internal sealed class CleanupChain
{
    private readonly List<(string Name, Func<int> Step)> _steps = new();

    internal void Add(string name, Func<int> step) => _steps.Add((name, step));

    internal CleanupReport Run()
    {
        var results = new List<CleanupStepResult>(_steps.Count);
        foreach (var (name, step) in _steps)
        {
            try
            {
                results.Add(new CleanupStepResult(name, step(), null));
            }
            catch (Exception ex)
            {
                results.Add(new CleanupStepResult(name, TsfHResult.E_FAIL, ex));
            }
        }

        var failures = results.Count(r => r.HResult < 0 || r.Exception is not null);
        return new CleanupReport(failures, results);
    }
}
