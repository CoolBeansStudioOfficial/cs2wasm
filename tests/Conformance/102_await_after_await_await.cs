// A return with an await after a statement with two awaits (`await await x`), and sums of awaits.
using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
static class Program
{
    static async Task<int> V(int v, int ms = 1) { await Task.Delay(ms); return v; }
    static async Task<int> P1() { var lazy = new Lazy<Task<int>>(() => V(7)); return await lazy.Value + await lazy.Value; }
    static async Task<int> P2() { int first = 1; var t = V(7); return first + await t + await t; }
    static async Task<int> P3() { var first = await await Task.WhenAny(V(100, 50), V(200, 1)); return first; }
    static async Task<int> P4() { var all = new[] { 1, 2 }; int first = 1; var t = V(7); return all.Sum() + first + await t + await t; }
    static async Task<int> P5() { var t = V(7); return await t + await t + await t; }
    static async Task<int> P6() { var t = V(7); int a = 1 + await t + await t; return a; }
    static async Task<int> R1() { var first = await await Task.WhenAny(V(100, 50), V(200, 1)); var lazy = new Lazy<Task<int>>(() => V(7)); return first + await lazy.Value + await lazy.Value; }
    static async Task<int> R2() { var first = await await Task.WhenAny(V(100, 50), V(200, 1)); var t = V(7); return first + await t; }
    static async Task<int> R3() { var first = await await Task.WhenAny(V(100, 50), V(200, 1)); var t = V(7); return await t; }
    static async Task<int> R4() { var first = await await Task.WhenAny(V(100, 50), V(200, 1)); return await V(7); }
    static async Task<int> R5() { await await Task.WhenAny(V(100, 50), V(200, 1)); await Task.Yield(); return 5; }
    static async Task<int> R6() { var w = await Task.WhenAny(V(100, 50), V(200, 1)); var first = await w; return first + await V(7); }
    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        Console.WriteLine(R1().Result + " " + R2().Result + " " + R3().Result + " " + R4().Result + " " + R5().Result + " " + R6().Result); Console.WriteLine(P1().Result + " " + P2().Result + " " + P3().Result + " " + P4().Result + " " + P5().Result + " " + P6().Result);
    }
}
