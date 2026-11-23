// The stack has room for ordinary recursion with large frames, and stackalloc of a size the program computes.
// (Running out of it ends the instance, as it ends the process under .NET: tests/Il2Wasm.Tests/ApiChecks.cs.)
using System;
using System.Runtime.InteropServices;

static class P
{
    static int s_guard = 12345;
    static int Deep(int n) { Span<byte> s = stackalloc byte[2048]; s[0] = (byte)n; s[2047] = 1; return n == 0 ? s[0] : Deep(n - 1) + s[2047]; }
    static long Sum(int n) { Span<long> s = stackalloc long[n]; for (int i = 0; i < n; i++) s[i] = i; long t = 0; foreach (var v in s) t += v; return t; }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        Console.WriteLine(Deep(200));
        Console.WriteLine(Sum(int.Parse("20000")));
        Console.WriteLine(s_guard);
    }
}
