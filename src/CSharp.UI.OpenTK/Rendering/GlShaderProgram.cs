using global::OpenTK.Graphics.OpenGL4;

namespace CSharp.UI.OpenTK;

/// <summary>着色器编译或链接失败；消息中包含 OpenGL 驱动程序给出的诊断日志。</summary>
public sealed class GlShaderException : Exception
{
    /// <summary>使用阶段、日志与源码构造。</summary>
    public GlShaderException(string stage, string log, string shaderSource, Exception? innerException = null)
        : base(BuildMessage(stage, log), innerException)
    {
        Stage = stage;
        Log = log;
        ShaderSource = shaderSource;
    }

    /// <summary>失败阶段（“顶点着色器”“片元着色器”“程序链接”）。</summary>
    public string Stage { get; }

    /// <summary>GL 驱动程序返回的信息日志。</summary>
    public string Log { get; }

    /// <summary>参与编译的源码，便于诊断。</summary>
    public string ShaderSource { get; }

    private static string BuildMessage(string stage, string log)
    {
        var detail = string.IsNullOrWhiteSpace(log) ? "（驱动未返回日志）" : log.Trim();
        return $"OpenGL 着色器{stage}失败：{detail}";
    }
}

/// <summary>
/// 已编译并链接的 GLSL 着色器程序。
/// </summary>
/// <remarks>
/// 生命周期：必须在拥有当前 GL 上下文的线程上创建与释放。编译失败时
/// <see cref="Compile"/> 会删除已创建的部分对象（着色器/程序），并抛出带诊断日志的 <see cref="GlShaderException"/>。
/// </remarks>
public sealed class GlShaderProgram : IDisposable
{
    private readonly int _handle;
    private bool _disposed;

    private GlShaderProgram(int handle, string name)
    {
        _handle = handle;
        Name = name;
    }

    /// <summary>GL 程序名。</summary>
    public int Handle => _handle;

    /// <summary>用于诊断的程序名称。</summary>
    public string Name { get; }

    /// <summary>是否已释放。</summary>
    public bool IsDisposed => _disposed;

    /// <summary>编译并链接着色器程序；失败时抛出 <see cref="GlShaderException"/>。</summary>
    /// <param name="vertexSource">顶点着色器源码（GLSL 330）。</param>
    /// <param name="fragmentSource">片元着色器源码（GLSL 330）。</param>
    /// <param name="name">诊断用名称。</param>
    public static GlShaderProgram Compile(string vertexSource, string fragmentSource, string name = "program")
    {
        ArgumentNullException.ThrowIfNull(vertexSource);
        ArgumentNullException.ThrowIfNull(fragmentSource);

        var vertex = 0;
        var fragment = 0;
        var program = 0;
        try
        {
            vertex = CompileStage(ShaderType.VertexShader, vertexSource, "顶点着色器");
            fragment = CompileStage(ShaderType.FragmentShader, fragmentSource, "片元着色器");

            program = GL.CreateProgram();
            GlResourceCounters.ReportCreated(GlResourceKind.Program);
            GL.AttachShader(program, vertex);
            GL.AttachShader(program, fragment);
            GL.LinkProgram(program);
            GL.GetProgram(program, GetProgramParameterName.LinkStatus, out var linked);
            if (linked == 0)
            {
                var programLog = GL.GetProgramInfoLog(program);
                throw new GlShaderException("程序链接", programLog, vertexSource + "\n---\n" + fragmentSource);
            }

            // 链接完成后着色器对象已不再需要；先分离再删除，避免占用驱动资源。
            GL.DetachShader(program, vertex);
            GL.DetachShader(program, fragment);
            DeleteShader(ref vertex);
            DeleteShader(ref fragment);

            var result = new GlShaderProgram(program, name ?? "program");
            program = 0;
            return result;
        }
        catch
        {
            // 失败路径：清理已创建的部分资源，保证资源计数回落。
            DeleteShader(ref vertex);
            DeleteShader(ref fragment);
            if (program != 0)
            {
                GL.DeleteProgram(program);
                GlResourceCounters.ReportDeleted(GlResourceKind.Program);
            }

            throw;
        }
    }

    /// <summary>查询 uniform 位置；不存在时返回 -1。</summary>
    public int GetUniformLocation(string uniformName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uniformName);
        ThrowIfDisposed();
        return GL.GetUniformLocation(_handle, uniformName);
    }

    /// <summary>查询顶点属性位置；不存在时返回 -1。</summary>
    public int GetAttributeLocation(string attributeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attributeName);
        ThrowIfDisposed();
        return GL.GetAttribLocation(_handle, attributeName);
    }

    /// <summary>设为当前程序。</summary>
    public void Use()
    {
        ThrowIfDisposed();
        GL.UseProgram(_handle);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        GL.DeleteProgram(_handle);
        GlResourceCounters.ReportDeleted(GlResourceKind.Program);
    }

    private static int CompileStage(ShaderType type, string source, string stageName)
    {
        var shader = GL.CreateShader(type);
        GlResourceCounters.ReportCreated(GlResourceKind.Shader);
        try
        {
            GL.ShaderSource(shader, source);
            GL.CompileShader(shader);
            GL.GetShader(shader, ShaderParameter.CompileStatus, out var compiled);
            if (compiled == 0)
            {
                var log = GL.GetShaderInfoLog(shader);
                throw new GlShaderException(stageName, log, source);
            }

            return shader;
        }
        catch
        {
            GL.DeleteShader(shader);
            GlResourceCounters.ReportDeleted(GlResourceKind.Shader);
            throw;
        }
    }

    private static void DeleteShader(ref int shader)
    {
        if (shader == 0)
        {
            return;
        }

        GL.DeleteShader(shader);
        GlResourceCounters.ReportDeleted(GlResourceKind.Shader);
        shader = 0;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
