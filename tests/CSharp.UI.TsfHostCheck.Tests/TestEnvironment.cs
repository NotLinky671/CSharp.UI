using System.Runtime.CompilerServices;

namespace CSharp.UI.TsfHostCheck.Tests;

internal static class TestEnvironment
{
    /// <summary>只为本测试进程重定向 TEMP/TMP 到 workspace，不修改全局设置。</summary>
    [ModuleInitializer]
    internal static void Initialize()
    {
        try
        {
            var root = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
            var temp = Path.Combine(root, "artifacts", "ime-work", "ti07-tsf-prototype", "temp");
            Directory.CreateDirectory(temp);
            Environment.SetEnvironmentVariable("TEMP", temp);
            Environment.SetEnvironmentVariable("TMP", temp);
            Console.WriteLine($"tsf-prototype tests: TEMP/TMP -> {temp} (this process only)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"tsf-prototype tests: TEMP redirect skipped ({ex.GetType().Name}).");
        }
    }
}
