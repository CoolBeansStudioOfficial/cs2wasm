// Lifted operations whose result is null (in a loop, after a turn in which it was not), and `is` on a nullable.
using System;
using System.Runtime.InteropServices;
static class Program
{
    static int? Get(int? b) => b > 2 ? b : null;
    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        int? b = 3, a = null;
        Console.WriteLine((b > 2 ? b : null) is int);
        var c = b > 2 ? b : null;
        Console.WriteLine(c is int);
        Console.WriteLine(c.HasValue + " " + c);
        Console.WriteLine((a > 2 ? a : null) is int);
        Console.WriteLine(Get(5) is int v ? v : -1);
        object o = b > 2 ? b : null;
        Console.WriteLine(o is int);
        Console.WriteLine((b ?? a) is int);
        Console.WriteLine((true ? b : a) is { } z ? z : -2);
        for (int i = 0; i < 3; i++) { int? x = i == 1 ? null : i; int? y = x + 1; Console.Write((y.HasValue ? y.Value.ToString() : "null") + " "); }
        Console.WriteLine();
        More.Run();
    }
}
static class More
{
    public static void Run()
    {
        for (int i = 0; i < 3; i++)
        {
            int? x = i == 1 ? null : i + 1;
            long? wide = x; int? neg = -x; bool? b = i == 1 ? null : i == 0; bool? nb = !b;
            object? o = i == 1 ? null : (object)(i * 2); int? un = (int?)o; double? d = x; int? inc = x; inc++;
            object ob = i == 1 ? "s" : i; int? asn = ob as int?; var len = (i == 1 ? null : "abc")?.Length;
            System.Console.Write($"[{wide}|{neg}|{nb}|{un}|{d}|{inc}|{x * 2}|{x == null}|{asn}|{len}] ");
        }
        System.Console.WriteLine();
    }
}
