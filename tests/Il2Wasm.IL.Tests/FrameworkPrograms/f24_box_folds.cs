// Box folds in the importer (Ir/BoxFolds.cs): box then unbox.any of the same type is the value, a value type's box is
// not null, box then isinst of the same type is the box. What must not fold, and still behaves as .NET:
// - a Nullable<T> without a value boxes as null: its null test is true (IL2WASM_BOX_FOLD_BREAK=1 takes it for not
//   null: "nullable null: False");
// - a box kept and changed through an interface, then unboxed, is read as it is now ("kept 2 1"; the break twin
//   folds it to the value boxed: "kept 1 1");
// - unbox.any of a box of another type: a Nullable<int>'s box unboxed as int throws NullReferenceException for none,
//   and int's box unboxed as long throws InvalidCastException (the break twin folds the first: "0" and no exception).
// The box a fold consumed goes (no object made); a box that is used again stays and keeps its value from the box.
using System;
using System.Collections.Generic;
using System.Linq;

struct P { public double X, Y; public override string ToString() => X + "," + Y; }
interface IBump { void Bump(); }
struct Ctr : IBump { public int N; public object Tag; public void Bump() => N++; }
struct Named { public string Name; public int N; public override string ToString() => Name + N; }
enum Color { Red = 1, Green = 2 }

static class Program
{
    static T RoundTrip<T>(T v) => (T)(object)v!;
    static int AsInt<T>(T v) => (int)(object)v!;
    static long AsLong<T>(T v) => (long)(object)v!;
    static bool IsNull<T>(T v) => v == null;
    static bool NotNull<T>(T v) { bool b = v != null; return b; }
    static string Is<T>(T v) => v is int i ? "int " + i : v is P p ? "P " + p : "other";
    static int Eq<T>(T a, T b) => EqualityComparer<T>.Default.Equals(a, b) ? 1 : 0;
    static int Cmp<T>(T a, T b) => Comparer<T>.Default.Compare(a, b);
    static object Twice<T>(T v, out T back) { object o = v!; back = (T)o; return o; }
    static T AfterWrite<T>(T v, T other)
    {
        object o;
        try { o = v!; v = other; } finally { }
        return (T)o;
    }

    // a box kept in a local and changed through an interface before it is unboxed: the unbox reads the box
    static string Kept<T>(T v) where T : IBump
    {
        object boxed = v!;
        ((IBump)boxed).Bump();
        var back = (T)boxed;
        return back is Ctr c ? "kept " + c.N + " " + ((Ctr)(object)v!).N : "?";
    }

    static void Main()
    {
        Console.WriteLine(Kept(new Ctr { N = 1, Tag = "t" }));
        Console.WriteLine(RoundTrip(42) + " " + RoundTrip(7L) + " " + RoundTrip(new P { X = 1.5, Y = -2 }) + " " + RoundTrip(new Named { Name = "n", N = 3 }) + " " + RoundTrip(Color.Green));
        Console.WriteLine(RoundTrip<int?>(5) + " [" + RoundTrip<int?>(null) + "] " + RoundTrip<int?>(null).HasValue + " " + RoundTrip("str"));
        Console.WriteLine(AsInt(11) + " " + AsInt<int?>(12));
        try { Console.WriteLine("nullable none as int: " + AsInt<int?>(null)); } catch (NullReferenceException) { Console.WriteLine("nullable none as int: NullReferenceException"); }
        try { Console.WriteLine(AsLong(3)); } catch (InvalidCastException) { Console.WriteLine("int as long: InvalidCastException"); }
        Console.WriteLine("null tests: " + IsNull(0) + " " + IsNull(new P()) + " " + NotNull(1L) + " " + IsNull<string?>(null) + " " + IsNull("s"));
        Console.WriteLine("nullable null: " + IsNull<int?>(null) + " " + IsNull<int?>(4) + " " + NotNull<int?>(null) + " " + NotNull<P?>(new P()));
        Console.WriteLine(Is(9) + "; " + Is(9L) + "; " + Is(new P { X = 3 }) + "; " + Is<int?>(10) + "; " + Is<int?>(null) + "; " + Is("x"));
        Console.WriteLine("eq " + Eq(1, 1) + Eq(1, 2) + Eq<long?>(null, null) + Eq<long?>(null, 1) + Eq("a", "a") + " cmp " + Cmp(1, 2) + Cmp(2, 2) + Cmp(3L, 2L) + Cmp<int?>(null, 1) + Cmp<int?>(1, null));
        var o = Twice(new P { X = 8, Y = 9 }, out var back);
        Console.WriteLine("twice " + o + " " + back + " " + o.GetType().Name + " " + AfterWrite(1, 2) + " " + AfterWrite(new Named { Name = "a", N = 1 }, new Named { Name = "b", N = 2 }));
        // the comparers in bulk: a sort, a binary search, a dictionary, Linq over ints
        uint seed = 1;
        var list = new List<int>();
        for (int i = 0; i < 2000; i++) { seed = seed * 1664525u + 1013904223u; list.Add((int)(seed >> 8) % 1000); }
        list.Sort();
        var arr = list.Select(x => (long)x).ToArray();
        Array.Sort(arr, (a, b) => b.CompareTo(a));
        var d = new Dictionary<int, int>();
        foreach (var x in list) d[x] = d.TryGetValue(x, out var c) ? c + 1 : 1;
        Console.WriteLine(list[0] + " " + list[1999] + " " + list.BinarySearch(list[700]) / 10 + " " + arr[0] + " " + d.Count + " " + d[list[3]] + " " + list.Distinct().Count() + " " + list.Max() + " " + list.IndexOf(list[50]) / 10);
    }
}
