using System.Reflection;
using System.Runtime.InteropServices;

var failed = 0;
var total = 0;
void Check(string name, bool value, object? detail = null)
{
    total++;
    if (!value) failed++;
    Console.WriteLine($"[{(value ? "PASS" : "FAIL")}] {name}: {detail}");
}
var assembly = Assembly.Load("CSharp.UI.TsfHostCheck");
var extended = assembly.GetType("CSharp.UI.TsfHostCheck.ITfThreadMgrEx");
Check("ThreadMgrEx exists", extended is not null);
if (extended is not null)
{
    Check("SDK IID", extended.GUID == new Guid("3e90ade3-7594-4cb0-bb58-69628f5f458c"), extended.GUID);
    Check("ComImport", extended.IsImport);
    var methods = extended.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly).OrderBy(m => m.MetadataToken).ToArray();
    Check("flattened 13 methods", methods.Length == 13, string.Join(",", methods.Select(m => m.Name)));
    var lastSlot = Marshal.GetEndComSlot(extended);
    Check("CLR actual last COM slot15", lastSlot == 15, lastSlot);
    Check("ActivateEx declared at slot14", Array.FindIndex(methods, m => m.Name == "ActivateEx") == 11, Array.FindIndex(methods, m => m.Name == "ActivateEx") + 3);
    Check("GetActiveFlags declared at slot15", Array.FindIndex(methods, m => m.Name == "GetActiveFlags") == 12, Array.FindIndex(methods, m => m.Name == "GetActiveFlags") + 3);
    Check("PreserveSig on every HRESULT", methods.All(m => m.ReturnType == typeof(int) && m.GetCustomAttribute<PreserveSigAttribute>() is not null));
    var baseType = assembly.GetType("CSharp.UI.TsfHostCheck.ITfThreadMgr")!;
    var baseMethods = baseType.GetMethods().OrderBy(m => m.MetadataToken).ToArray();
    Check("base signatures match SDK wrapper", methods.Length >= 11 && baseMethods.Select((m, index) => m.Name == methods[index].Name && m.GetParameters().Select(p => p.ParameterType).SequenceEqual(methods[index].GetParameters().Select(p => p.ParameterType))).All(v => v));
}
if (failed == 0) LifecycleAudit.Run(Check);
Console.WriteLine($"ABI/lifecycle audit {total - failed}/{total}; no native COM activation called");
return failed == 0 ? 0 : 1;
