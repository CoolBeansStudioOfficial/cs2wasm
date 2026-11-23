// tools/corelibs-check.mjs: BigInteger named in full, with a delegate, a tuple and an interpolated string, and no
// Console (System.Runtime.Numerics: 10 libraries).
using System;
using System.Runtime.InteropServices;

static class Program
{
    [UnmanagedCallersOnly(EntryPoint = "main")]
    static int Main()
    {
        Func<int, int> f = x => x + 1;
        var t = (a: 1, b: "s");
        return f(2) + t.a + $"{t.b}{f(1)}".Length + System.Numerics.BigInteger.Pow(2, 70).ToString().Length;
    }
}
