// checked
// The reachability rules (src/Il2Wasm/IL/Reach): each part's output is wrong if its rule folds what it must not.
// Constant propagation and pure calls (IL2WASM_NO_SCCP, IL2WASM_NO_PURE_CALLS), substitutions (IL2WASM_NO_SUBSTITUTIONS),
// unwritten statics (IL2WASM_NO_UNWRITTEN_STATICS), constant arguments (IL2WASM_NO_CONSTANT_ARGS), return values
// (IL2WASM_NO_RETURN_VALUES), field values (IL2WASM_NO_FIELD_VALUES), virtual calls' values (IL2WASM_NO_DEVIRT_VALUES).
using System;
using System.Diagnostics.Tracing;
using System.Threading;

sealed class Src : EventSource { public static readonly Src Log = new(); }

abstract class Shape { public abstract int Sides { get; } }
sealed class Tri : Shape { public override int Sides => 3; }
sealed class Sq : Shape { public override int Sides => 4; }

class Holder
{
    public object? Item;
    public int Count;
    public int ByRef;
}

class Finalized
{
    public static int Done;
    ~Finalized() { Done++; }
}

static class Program
{
    static int s_onlyByRef;
    static int s_written;
    static object? s_never;

    // pure: folds under constant arguments; not pure: a division by zero throws, and a loop may not end
    static int Add(int a, int b) => a + b;
    static int Div(int a, int b) => a / b;
    static int Twice(int x) => x * 2;
    static void Check(int v) { if (v < 0) throw new ArgumentOutOfRangeException(nameof(v)); }

    static Holder MakeHolder() => new Holder();
    static Shape Pick(int i) => i == 0 ? new Tri() : new Sq();

    static void Bump(ref int x) => x++;

    static void Main()
    {
        // constant arithmetic and branches (a folded division by zero would be wrong: it must throw)
        Console.WriteLine(Add(2, 3) == 5 ? "add folded" : "add wrong");
        try { Console.WriteLine(Div(1, 0)); } catch (DivideByZeroException) { Console.WriteLine("div by zero throws"); }
        try { Check(-1); } catch (ArgumentOutOfRangeException e) { Console.WriteLine("check throws " + e.ParamName); }
        Check(1);

        // a substitution: EventSource is off on this target, so IsEnabled is false
        Console.WriteLine("event source enabled: " + Src.Log.IsEnabled());

        // statics: one written only through its address, one written, one never (its default)
        Bump(ref s_onlyByRef);
        Interlocked.Increment(ref s_onlyByRef);
        Console.WriteLine("by ref: " + s_onlyByRef);
        if (Environment.TickCount64 >= 0) s_written = 7;
        Console.WriteLine("written: " + s_written + ", never: " + (s_never == null));

        // constant arguments: Twice gets 21 here and another value through a delegate
        Func<int, int> f = Twice;
        Console.WriteLine("twice: " + Twice(21) + " " + f(5));

        // return values and field values: a field starts null and is set later; one written through ref
        var h = MakeHolder();
        Console.WriteLine("item null: " + (h.Item == null) + ", count " + h.Count);
        h.Item = "x";
        h.Count = 3;
        Bump(ref h.ByRef);
        Console.WriteLine("item: " + h.Item + ", count " + h.Count + ", by ref " + h.ByRef);

        // a virtual call whose implementations differ
        Console.WriteLine("sides: " + Pick(0).Sides + " " + Pick(1).Sides);

        // a field of a class no object is made of (Array.Length reads Unsafe.As<RawArrayData>(array).Length) is not its
        // default (r53: a box of an empty Nullable<T> is null)
        Array markers = new[] { DayOfWeek.Friday, DayOfWeek.Monday, DayOfWeek.Wednesday };
        Array.Sort(markers);
        Console.WriteLine("sorted: " + string.Join(",", (DayOfWeek[])markers) + ", length " + markers.Length);

        // a call that keeps its argument alive until it is made (GC.KeepAlive: an empty body)
        var fin = new Finalized();
        var weak = new WeakReference(fin);
        GC.KeepAlive(fin);
        fin = null;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        Console.WriteLine("finalized: " + Finalized.Done + ", weak alive: " + weak.IsAlive);
    }
}
