// tools/corelibs-check.mjs: a `dynamic` operation and no Console: nothing in the text names Microsoft.CSharp, whose
// binders the C# compiler calls for it (Roslyn reports the reference as used: 14 libraries).
using System.Runtime.InteropServices;

static class Program
{
    [UnmanagedCallersOnly(EntryPoint = "main")]
    static int Main()
    {
        dynamic d = 3;
        d = d + 4;
        return (int)d;
    }
}
