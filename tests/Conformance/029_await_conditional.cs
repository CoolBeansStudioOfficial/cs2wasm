using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

static class Program
{
    static async Task<int> Val(int v) { await Task.Yield(); return v; }
    static async Task<string> Str(string s) { await Task.Delay(1); return s; }
    static async Task<bool> Flag(bool b, string tag) { await Task.Yield(); Console.WriteLine("flag " + tag); return b; }

    sealed class Box { public int N; public Box Next; }

    static async Task<int> Run(bool c, string maybe)
    {
        int a = c ? await Val(1) : await Val(2);
        int b = (c ? 10 : await Val(20)) + await Val(3);
        string s = maybe ?? await Str("fallback");
        bool both = await Flag(c, "x") && await Flag(true, "y");
        bool either = await Flag(!c, "p") || await Flag(false, "q");
        var box = c ? new Box { N = 5 } : null;
        int? n = box?.Next?.N ?? await Val(7);
        var list = new List<int> { await Val(1), c ? await Val(2) : 0 };
        Console.WriteLine(Describe(await Val(4), c ? "yes" : await Str("no")));
        Console.WriteLine(a + " " + b + " " + s + " " + both + " " + either + " " + n + " " + list.Count + list[1]);
        return a + b + (both ? 100 : 0);
    }

    static string Describe(int v, string w) => v + ":" + w;

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        Console.WriteLine(Run(true, null).Result);
        Console.WriteLine(Run(false, "given").Result);
    }
}
