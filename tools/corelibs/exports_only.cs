// tools/corelibs-check.mjs: a program that uses no library but CoreLib (no Console: it is compiled with CoreLib alone).
using System;
using System.Runtime.InteropServices;

static class Program
{
    static int Fib(int n) => n < 2 ? n : Fib(n - 1) + Fib(n - 2);

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static int Main()
    {
        string s = "";
        for (int i = 0; i < 10; i++) s += Fib(i).ToString() + (i % 3 == 0 ? "," : ";");
        int[] a = new int[16];
        for (int i = 0; i < a.Length; i++) a[i] = s[i % s.Length] * (i + 1);
        Array.Sort(a);
        try { checked { a[0] = int.MaxValue + a[1]; } } catch (OverflowException) { a[0] = -1; }
        return a[0] + a[15] + s.Length + (int)Math.Sqrt(a[7]);
    }
}
