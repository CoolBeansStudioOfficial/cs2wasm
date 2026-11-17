// An interpolated string with an await in a hole (the holes before it are evaluated first and kept), a list
// pattern after a type pattern, lambdas with optional parameters.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
static class Program
{
    static string Shape(object? o) => o switch { int[] and [var first, .. var rest] => $"arr{first}+{rest.Length}", string { Length: var l } s and not "" => s[0] + l.ToString(), IList<int> and { Count: 0 } => "empty", _ => "?" };
    static async Task<string> Aw() { int ms = 5; var vt = await new ValueTask<int>(3); var r = "x"; return $"{ms}{vt}{r}{await Later()}"; static async ValueTask<int> Later() { await Task.Yield(); return 9; } }
    static async Task<string> Aw2() { var a = await Task.FromResult(1); return $"{a,3}|{await Task.FromResult(2.5):F2}|{a}{await Task.FromResult("s")}"; }
    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        Console.WriteLine(Shape(new[] { 7, 8, 9 }) + Shape("hello") + Shape(new int[0]) + Shape(new List<int>()) + Shape(1.5));
        var lam = (int x = 3, params int[] more) => x * 2 + more.Length;
        var lam2 = (string s = "d", int n = 2) => s + n;
        Console.WriteLine(lam() + " " + lam(1, 7, 8) + " " + lam2() + lam2("z"));
        Console.WriteLine(Aw().Result + " " + Aw2().Result);
    }
}
