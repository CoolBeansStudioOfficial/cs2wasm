// Tuples of more than seven elements (nested ValueTuple's Rest), a task-like type with its own async method builder,
// positional patterns through ITuple.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

[AsyncMethodBuilder(typeof(BoxBuilder<>))]
sealed class Box<T>
{
    public T Value = default!; public Exception? Error; public bool Done;
    Action? _then;
    public BoxAwaiter<T> GetAwaiter() => new(this);
    internal void Complete() { Done = true; var t = _then; _then = null; t?.Invoke(); }
    internal void Then(Action a) { if (Done) a(); else _then += a; }
}
readonly struct BoxAwaiter<T>(Box<T> box) : INotifyCompletion
{
    public bool IsCompleted => box.Done;
    public T GetResult() => box.Error != null ? throw box.Error : box.Value;
    public void OnCompleted(Action a) => box.Then(a);
}
struct BoxBuilder<T>
{
    Box<T> _box;
    public static BoxBuilder<T> Create() { Console.WriteLine("builder: Create"); return new BoxBuilder<T> { _box = new Box<T>() }; }
    public Box<T> Task => _box;
    public void Start<TSM>(ref TSM sm) where TSM : IAsyncStateMachine { Console.WriteLine("builder: Start"); sm.MoveNext(); }
    public void SetStateMachine(IAsyncStateMachine sm) { }
    public void SetResult(T v) { Console.WriteLine("builder: SetResult " + v); _box.Value = v; _box.Complete(); }
    public void SetException(Exception e) { Console.WriteLine("builder: SetException " + e.Message); _box.Error = e; _box.Complete(); }
    public void AwaitOnCompleted<TA, TSM>(ref TA a, ref TSM sm) where TA : INotifyCompletion where TSM : IAsyncStateMachine
    { Console.WriteLine("builder: AwaitOnCompleted"); var m = sm; a.OnCompleted(() => m.MoveNext()); }
    public void AwaitUnsafeOnCompleted<TA, TSM>(ref TA a, ref TSM sm) where TA : ICriticalNotifyCompletion where TSM : IAsyncStateMachine
    { Console.WriteLine("builder: AwaitUnsafeOnCompleted"); var m = sm; a.UnsafeOnCompleted(() => m.MoveNext()); }
}
sealed class Pair(int a, string b) : ITuple
{
    public int Length => 2;
    public object? this[int i] => i == 0 ? a : b;
}
static class Program
{
    static (int, int, int, int, int, int, int, int, string) Nine(int k) => (k, k + 1, k + 2, k + 3, k + 4, k + 5, k + 6, k + 7, "nine" + k);
    static (int a, long b, string c, double d, char e, bool f, byte g, short h, int i, string j) Ten() => (1, 2L, "three", 4.5, '5', true, 7, 8, 9, "ten");
    static async Box<int> Twice(Box<int> source)
    {
        int v = await source;
        Console.WriteLine("got " + v);
        if (v < 0) throw new InvalidOperationException("negative");
        return v * 2;
    }
    static async Box<string> Immediate() { await Task.CompletedTask; return "now"; }
    static string Describe(object o) => o switch
    {
        (int x, string s) => "pair " + x + " " + s,
        (var x, var y, var z) => "three " + x + y + z,
        _ => "other",
    };
    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        var n = Nine(10);
        Console.WriteLine(n.Item1 + " " + n.Item8 + " " + n.Item9 + " " + n.Rest.Item1 + " " + n.Rest.Item2);
        Console.WriteLine(n);
        Console.WriteLine(n == Nine(10));
        Console.WriteLine(n != Nine(11));
        Console.WriteLine(n.Equals(Nine(10)) + " " + (n.GetHashCode() == Nine(10).GetHashCode()));
        var t = Ten();
        Console.WriteLine(t.a + " " + t.h + " " + t.i + " " + t.j + " " + t);
        var (a, b, c, d, e, f, g, h, i, j) = t;
        Console.WriteLine(a + b + c + d + e + f + g + h + i + j);
        t.j = "changed"; t.i += 5;
        Console.WriteLine(t.Item10 + " " + t.Rest.Item2);
        var dict = new Dictionary<(int, int, int, int, int, int, int, int), string> { [(1, 2, 3, 4, 5, 6, 7, 8)] = "eight" };
        Console.WriteLine(dict[(1, 2, 3, 4, 5, 6, 7, 8)] + " " + dict.ContainsKey((1, 2, 3, 4, 5, 6, 7, 9)));
        Console.WriteLine(((ITuple)n).Length + " " + ((ITuple)n)[8]);
        Console.WriteLine(Comparer<(int, int, int, int, int, int, int, int, int)>.Default.Compare((1, 2, 3, 4, 5, 6, 7, 8, 9), (1, 2, 3, 4, 5, 6, 7, 8, 10)));

        var src = new Box<int>();
        var r = Twice(src);
        Console.WriteLine("pending " + r.Done);
        src.Value = 21; src.Complete();
        Console.WriteLine("done " + r.Done + " " + r.Value);
        var src2 = new Box<int>(); var r2 = Twice(src2);
        src2.Value = -1; src2.Complete();
        Console.WriteLine("failed " + r2.Done + " " + r2.Error?.Message);
        var im = Immediate();
        Console.WriteLine(im.Done + " " + im.Value);

        Console.WriteLine(Describe(new Pair(4, "four")));
        Console.WriteLine(Describe((1, 2, 3)));
        Console.WriteLine(Describe((5, "five")));
        Console.WriteLine(Describe("x"));
        Console.WriteLine(Describe(Tuple.Create(6, "six")));
    }
}
