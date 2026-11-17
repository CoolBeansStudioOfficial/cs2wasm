// Records with their own members, async forms off the usual path, and assorted statements.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

abstract record Shape(string Name) { public abstract double Area { get; } public virtual string Describe() => $"{Name} of {Area:F2}"; }
record Circle(double R) : Shape("circle") { public override double Area => Math.PI * R * R; }
sealed record Rect(double W, double H) : Shape("rect")
{
    public override double Area => W * H;
    public override string Describe() => base.Describe() + $" ({W}x{H})";
    public Rect Scale(double k) => this with { W = W * k, H = H * k };
    public bool Equals(Rect? other) => other is not null && Math.Abs(Area - other.Area) < 1e-9;
    public override int GetHashCode() => Area.GetHashCode();
    protected override bool PrintMembers(StringBuilder b) { b.Append("W=").Append(W).Append(", H=").Append(H); return true; }
}
record Tagged<T>(T Value, params string[] Tags) where T : notnull
{
    public int Count { get; init; } = Tags.Length;
    public string First => Tags.Length > 0 ? Tags[0] : "";
    public virtual bool Equals(Tagged<T>? o) => o is not null && EqualityComparer<T>.Default.Equals(Value, o.Value) && Tags.SequenceEqual(o.Tags);
    public override int GetHashCode() => Value.GetHashCode();
}
readonly record struct Point3(int X, int Y, int Z = 0) : IComparable<Point3>
{
    public static Point3 Origin { get; } = new(0, 0);
    public int Sum => X + Y + Z;
    public int CompareTo(Point3 o) => Sum.CompareTo(o.Sum);
    public static Point3 operator +(Point3 a, Point3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public void Deconstruct(out int x, out int y) { x = X; y = Y; }
}
record struct Mutable(int A) { public int B; public int Total => A + B; public void Bump() { A++; B += 2; } }
record class WithField { public int F; public string? S { get; set; } public required int R { get; init; } private int _hidden = 3; public int Hidden() => _hidden; }
record Derived(int A, int B) : Base(A) { public int C { get; init; } }
record Base(int A) { public virtual string Kind => "base"; }

sealed class Ticker : IAsyncDisposable, IDisposable
{
    string _n; public Ticker(string n) { _n = n; Console.WriteLine("open " + n); }
    public async ValueTask DisposeAsync() { await Task.Yield(); Console.WriteLine("async close " + _n); }
    public void Dispose() => Console.WriteLine("close " + _n);
}
static class Program
{
    static async IAsyncEnumerable<int> Count(int n, [EnumeratorCancellation] CancellationToken ct = default)
    {
        for (int i = 0; i < n; i++) { await Task.Yield(); ct.ThrowIfCancellationRequested(); yield return i; }
    }
    static async Task<int> Sum(IAsyncEnumerable<int> xs) { int s = 0; await foreach (var x in xs) s += x; return s; }
    static async ValueTask<string> Nested(int depth) => depth == 0 ? "leaf" : "(" + await Nested(depth - 1) + ")";
    static async Task Throws(int k) { await Task.Yield(); if (k % 2 == 1) throw new ArgumentException("odd " + k); }
    static async Task<int> Using()
    {
        await using var a = new Ticker("a");
        using var b = new Ticker("b");
        await using (var c = new Ticker("c")) { await Task.Delay(1); Console.WriteLine("inside"); }
        return 1;
    }
    static async Task<string> Aggregate()
    {
        var all = Task.WhenAll(Throws(1), Throws(2), Throws(3));
        try { await all; return "none"; }
        catch (ArgumentException e) { return e.Message.StartsWith("odd") + " of " + all.Exception!.InnerExceptions.Count + " " + all.Status; }
    }
    static async Task<string> Cancel()
    {
        using var cts = new CancellationTokenSource();
        int seen = 0;
        try { await foreach (var x in Count(10, cts.Token)) { seen++; if (x == 2) cts.Cancel(); } return "finished"; }
        catch (OperationCanceledException) { return "cancelled after " + seen; }
    }
    static async Task<int> Completion()
    {
        var tcs = new TaskCompletionSource<int>();
        var waiter = Task.Run(async () => await tcs.Task * 2);
        await Task.Yield();
        tcs.SetResult(21);
        var first = await Task.WhenAny(waiter, Task.Delay(5000));
        return first == waiter ? await waiter : -1;
    }
    static async Task<string> Locals()
    {
        var sb = new StringBuilder();
        async Task Add(string s) { await Task.Yield(); sb.Append(s); }
        Func<int, Task<int>> twice = async x => { await Task.Yield(); return x * 2; };
        await Add("a"); await Add("b");
        sb.Append(await twice(await twice(3)));
        int[] data = { 1, 2, 3 };
        foreach (var v in data) sb.Append(await twice(v));
        try { try { await Throws(1); } finally { await Add("f"); } }
        catch (Exception e) when (e.Message.StartsWith("odd")) { await Add("c"); }
        return sb.ToString();
    }
    static IEnumerable<int> Fib() { int a = 0, b = 1; while (true) { yield return a; (a, b) = (b, a + b); } }
    static IEnumerable Legacy() { yield return 1; yield return "two"; yield return 3.0; }
    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        Shape c = new Circle(1), r = new Rect(2, 3);
        Console.WriteLine(c + " | " + r + " | " + c.Describe() + " | " + r.Describe());
        var r2 = ((Rect)r).Scale(2);
        Console.WriteLine(r2 + " " + (r2 == new Rect(4, 6)) + " " + (r2 == new Rect(6, 4)) + " " + r2.Equals((object)new Rect(1, 24)) + " " + (r == c) + " " + (c == new Circle(1)) + " " + (c with { }).Equals(c));
        var t1 = new Tagged<int>(5, "x", "y"); var t2 = new Tagged<int>(5, "x", "y"); var t3 = t1 with { Count = 9 };
        Console.WriteLine((t1 == t2) + " " + (t1 == t3) + " " + t1.Count + " " + t3.Count + " " + t1.First + " " + new Tagged<string>("s").First.Length + " " + (t1.GetHashCode() == t2.GetHashCode()));
        var p = new Point3(1, 2) + new Point3(3, 4, 5); var (px, py) = p; var (qx, qy, qz) = p;
        Console.WriteLine(p + " " + px + py + " " + qx + qy + qz + " " + p.CompareTo(Point3.Origin) + " " + (p == new Point3(4, 6, 5)) + " " + p.GetHashCode().Equals(new Point3(4, 6, 5).GetHashCode()) + " " + default(Point3));
        var mu = new Mutable(1) { B = 2 }; mu.Bump(); var mu2 = mu with { A = 10 };
        Console.WriteLine(mu + " " + mu2 + " " + mu.Total + " " + (mu == mu2) + " " + mu.Equals(new Mutable(2) { B = 4 }));
        var wf = new WithField { R = 1, F = 2, S = "s" }; var wf2 = wf with { F = 2 };
        Console.WriteLine(wf + " " + (wf == wf2) + " " + (wf == wf2 with { S = null }) + " " + wf.Hidden());
        Base bd = new Derived(1, 2) { C = 3 }; Base bb = new Base(1);
        Console.WriteLine(bd + " " + bb + " " + (bd == bb) + " " + bd.Equals(bb) + " " + bb.Equals(bd) + " " + (bd with { A = 7 }) + " " + bd.Kind);
        var set = new HashSet<Shape> { new Circle(1), new Circle(1), new Rect(1, 6), new Rect(2, 3), new Rect(2, 4) };
        Console.WriteLine(set.Count + " " + new Dictionary<Point3, string> { [new(1, 2)] = "found" }[new Point3(1, 2, 0)]);

        Console.WriteLine(Sum(Count(5)).GetAwaiter().GetResult());
        Console.WriteLine(Nested(3).AsTask().Result);
        Console.WriteLine(Using().Result);
        Console.WriteLine(Aggregate().Result);
        Console.WriteLine(Cancel().Result);
        Console.WriteLine(Completion().Result);
        Console.WriteLine(Locals().Result);
        var faulted = Throws(1); try { faulted.Wait(); } catch (AggregateException e) { Console.WriteLine(e.InnerException!.Message + " " + faulted.IsFaulted); }

        Console.WriteLine(string.Join(",", Fib().Skip(10).Take(5)) + " " + string.Join("|", Legacy().Cast<object>()));
        int total = 0;
        for (int i = 0, j = 10; i < j; i++, j--) { if (i == 2) continue; total += i * j; }
        do { total--; } while (total > 60);
        var e2 = Fib().GetEnumerator(); while (e2.MoveNext() && e2.Current < 50) total += e2.Current; e2.Dispose();
        Console.WriteLine(total);
        object boxed = 5; lock (boxed) { total += (int)boxed; }
        checked { try { int big = int.MaxValue; big++; } catch (OverflowException) { total++; } }
        unsafe { int local = 7; int* ptr = &local; *ptr += 3; long* wide = stackalloc long[2]; wide[1] = *ptr; total += (int)wide[1]; }
        Console.WriteLine(total + " " + sizeof(decimal) + " " + nameof(Program.Main) + " " + default(DateTime).Year);
        string? maybe = null; maybe ??= "set"; maybe ??= "again";
        int[]? none = null; int? len = none?.Length; var pick = len is > 0 and < 10 ? "some" : "none";
        Console.WriteLine(maybe + " " + pick + " " + (len ?? -1) + " " + (none?[0]).HasValue);
        var ranges = "hello world"; int[] nums = { 0, 1, 2, 3, 4, 5 };
        Console.WriteLine(ranges[^5..] + ranges[..5] + ranges[2..^2] + " " + string.Join("", nums[1..^1]) + nums[^1] + " " + new Index(2, true).GetOffset(6) + " " + (1..4).GetOffsetAndLength(6));
        label:
        if (total > 0) { total = -total; goto label; }
        switch (total) { case < -100: Console.WriteLine("low"); break; case -30 or -31: Console.WriteLine("thirty"); goto default; default: Console.WriteLine("default " + total); break; }
    }
}
