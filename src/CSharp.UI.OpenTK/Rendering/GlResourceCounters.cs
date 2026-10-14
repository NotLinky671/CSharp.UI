using System.Threading;

namespace CSharp.UI.OpenTK;

/// <summary>OpenGL 资源种类，用于全局创建/释放计数。</summary>
public enum GlResourceKind
{
    /// <summary>着色器对象（<c>glCreateShader</c>）。</summary>
    Shader,

    /// <summary>着色器程序（<c>glCreateProgram</c>）。</summary>
    Program,

    /// <summary>缓冲对象（<c>glGenBuffers</c>）。</summary>
    Buffer,

    /// <summary>顶点数组对象（<c>glGenVertexArrays</c>）。</summary>
    VertexArray,

    /// <summary>纹理（<c>glGenTextures</c>）。</summary>
    Texture,

    /// <summary>framebuffer 对象（<c>glGenFramebuffers</c>）。</summary>
    Framebuffer,
}

/// <summary>
/// 进程级 OpenGL 资源创建/释放计数器：用于验证“重复提交不持续创建资源”和“退出时资源计数回落”。
/// </summary>
/// <remarks>
/// 计数器只在当前进程内累加，不读取 GL 状态，因此可以在没有 GL 上下文时查询；
/// 断言“已创建 == 已释放”即可确认没有泄漏。测试需要串行访问 GL 时才使用计数差值。
/// </remarks>
public static class GlResourceCounters
{
    private static readonly long[] Created = new long[6];
    private static readonly long[] Deleted = new long[6];

    /// <summary>记录一次资源创建。</summary>
    public static void ReportCreated(GlResourceKind kind) => Interlocked.Increment(ref Created[(int)kind]);

    /// <summary>记录一次资源释放。</summary>
    public static void ReportDeleted(GlResourceKind kind) => Interlocked.Increment(ref Deleted[(int)kind]);

    /// <summary>累计创建数量。</summary>
    public static long GetCreatedCount(GlResourceKind kind) => Interlocked.Read(ref Created[(int)kind]);

    /// <summary>累计释放数量。</summary>
    public static long GetDeletedCount(GlResourceKind kind) => Interlocked.Read(ref Deleted[(int)kind]);

    /// <summary>存活数量（创建减释放）。</summary>
    public static long GetLiveCount(GlResourceKind kind)
        => Interlocked.Read(ref Created[(int)kind]) - Interlocked.Read(ref Deleted[(int)kind]);

    /// <summary>清零全部计数（测试在基线处调用）。</summary>
    public static void Reset()
    {
        for (var i = 0; i < Created.Length; i++)
        {
            Interlocked.Exchange(ref Created[i], 0);
            Interlocked.Exchange(ref Deleted[i], 0);
        }
    }

    /// <summary>返回可读的计数快照，用于测试失败信息与诊断输出。</summary>
    public static string Describe()
        => string.Join(
            "、",
            Enum.GetValues<GlResourceKind>().Select(kind => $"{kind} {GetLiveCount(kind)}/{GetCreatedCount(kind)}"));
}
