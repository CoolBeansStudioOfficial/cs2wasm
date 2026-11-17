// Awaits wherever an expression can be: both sides of ?:, ?? and &&, arguments, loop conditions, switch arms and
// guards, catch and finally, compound assignments, async streams, local functions and lambdas.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

static class Program
{
    static async Task<int> V(int v, int ms = 1) { await Task.Delay(ms); return v; }
    static async Task<string?> S(string? s) { await Task.Yield(); return s; }
    static async Task<bool> B(bool b) { await Task.Yield(); return b; }
    static async Task<string> Cond(bool c) => c ? "t" + await V(1) : "f" + await V(2);
    static async Task<string> Coalesce(string? s) => (await S(s) ?? await S("dflt"))!;
    static async Task<string> Logic(bool a, bool b) => $"{await B(a) && await B(b)}{await B(a) || await B(b)}{!await B(a)}";
    static async Task<int> NullCond(List<int>? l) => (l?.Count ?? await V(-1)) + (await S("abc"))!.Length;
    static async Task<int> Args() => Math.Max(await V(3), await V(7)) + new[] { await V(1), await V(2) }.Sum() + (await V(10), await V(20)).Item2;
    static async Task<int> Loop() { int s = 0; for (int i = 0; i < await V(4); i++) { if (i == await V(2)) continue; s += await V(i); } while (s < await V(10)) s += await V(3); do { s++; } while (await B(s < 15)); return s; }
    static async Task<string> Switch(int n) => await V(n) switch { 1 => "one", var x when x > 5 => "big" + await V(x), _ => "other" };
    static async Task<string> SwitchStmt(object o) { switch (o) { case int i when i > await V(0): return "pos" + await V(i); case string s: await Task.Yield(); return s + (await S("!")); default: return "none"; } }
    static async Task<int> TryAwait() { int r = 0; try { r += await V(1); throw new InvalidOperationException(); } catch (InvalidOperationException) { r += await V(10); } finally { r += await V(100); } return r; }
    static async Task<int> Using() { await using var a = new Res("a"); using var b = new Res("b"); await Task.Yield(); return a.N + b.N; }
    sealed class Res(string n) : IDisposable, IAsyncDisposable { public int N = n.Length; public void Dispose() => Console.Write("[d" + n + "]"); public async ValueTask DisposeAsync() { await Task.Yield(); Console.Write("[ad" + n + "]"); } }
    static async Task<int> Compound() { int[] a = new int[3]; int i = 0; a[i++] += await V(5); a[await V(1)] = await V(7) + a[0]; var d = new Dictionary<string, int> { ["k"] = 1 }; d["k"] += await V(4); (a[2], i) = (await V(9), await V(8)); return a[0] + a[1] * 10 + a[2] * 100 + i * 1000 + d["k"] * 10000; }
    static async Task<string> Struct() { var p = (x: 1, y: "a"); p.x += await V(2); var dt = new DateTime(2020, 1, 1).AddDays(await V(3)); Span<int> none = default; return p.x + p.y + dt.Day + none.Length; }
    static async IAsyncEnumerable<int> Gen(int n) { for (int i = 0; i < n; i++) { if (i == 3) yield break; yield return await V(i * i); } }
    static async Task<string> Enum() { var l = new List<int>(); await foreach (var x in Gen(10)) l.Add(x); await foreach (var x in Gen(2).ConfigureAwait(false)) l.Add(-x); var e = Gen(1).GetAsyncEnumerator(); await e.MoveNextAsync(); l.Add(e.Current + 50); await e.DisposeAsync(); return string.Join(",", l); }
    static async Task<int> Par() { var ts = Enumerable.Range(1, 5).Select(i => V(i, 6 - i)).ToArray(); var all = await Task.WhenAll(ts); var first = await await Task.WhenAny(V(100, 50), V(200, 1)); var lazy = new Lazy<Task<int>>(() => V(7)); return all.Sum() + first + await lazy.Value + await lazy.Value; }
    static async Task<string> Sync() { var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously); _ = Task.Run(async () => { await Task.Delay(5); tcs.SetResult(42); }); var sem = new SemaphoreSlim(0); _ = Task.Run(() => sem.Release()); await sem.WaitAsync(); var vt = new ValueTask<int>(Task.FromResult(3)); var timeout = await Task.WhenAny(tcs.Task, Task.Delay(2000)) == tcs.Task; return $"{await tcs.Task} {await vt} {timeout} {await Task.Run(() => Thread.CurrentThread.IsThreadPoolThread)}"; }
    static async Task<string> Nested() { Func<int, Task<int>> twice = async x => await V(x) * 2; async Task<int> Local(int x) => await twice(x) + await V(1); var r = await Local(await twice(await V(3))); var results = new List<int>(); foreach (var f in new Func<Task<int>>[] { () => V(1), async () => await V(2), () => Local(0) }) results.Add(await f()); return r + ":" + string.Join(",", results); }
    static void Run(string name, Func<Task<string>> f)
    {
        try { var t = f(); Console.WriteLine(name + ": " + (t.Wait(5000) ? t.Result : "HANG")); }
        catch (Exception e) { Console.WriteLine(name + " threw " + (e.InnerException ?? e).GetType().Name + ": " + (e.InnerException ?? e).Message); }
    }
    static Func<Task<string>> I(Func<Task<int>> f) => async () => (await f()).ToString();
    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        Run("cond", async () => await Cond(true) + await Cond(false));
        Run("coalesce", async () => await Coalesce("x") + await Coalesce(null));
        Run("logic", async () => await Logic(true, false) + await Logic(false, true));
        Run("nullcond", I(async () => await NullCond(null) * 100 + await NullCond(new List<int> { 1, 2 })));
        Run("args", I(Args)); Run("loop", I(Loop));
        Run("switch", async () => await Switch(1) + await Switch(7) + await Switch(3));
        Run("switchstmt", async () => await SwitchStmt(5) + await SwitchStmt("s") + await SwitchStmt(-1) + await SwitchStmt(1.5));
        Run("try", I(TryAwait)); Run("using", I(Using)); Run("compound", I(Compound)); Run("struct", Struct); Run("enum", Enum); Run("par", I(Par)); Run("sync", Sync); Run("nested", Nested);
    }
}
