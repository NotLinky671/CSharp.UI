using System.Runtime.InteropServices;

using CSharp.UI.ImeProbe;

namespace CSharp.UI.TsfHostCheck;

/// <summary>结构化追踪；实现必须吞掉日志自身异常，绝不让日志故障穿过原生回调。</summary>
internal delegate void TsfTrace(string stage, string kind, IDictionary<string, object?>? fields);

internal sealed class ProbeTsfTrace
{
    private readonly ProbeLog _log;

    internal ProbeTsfTrace(ProbeLog log) => _log = log;

    internal void Write(string stage, string kind, IDictionary<string, object?>? fields = null)
    {
        try
        {
            _log.Record(stage, "tsf-prototype", kind, fields);
        }
        catch (Exception)
        {
            // 日志失败不能影响原生调用；失败计数由 ProbeLog 自己维护。
        }
    }
}
