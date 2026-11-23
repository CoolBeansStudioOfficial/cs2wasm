// tools/corelibs-check.mjs: an expression tree and no Console (System.Linq.Expressions: 13 libraries).
using System.Runtime.InteropServices;

static class Program
{
    [UnmanagedCallersOnly(EntryPoint = "main")]
    static int Main()
    {
        System.Linq.Expressions.Expression<System.Func<int, int>> e = x => x + 1;
        return e.Compile()(2) + e.Body.ToString().Length;
    }
}
