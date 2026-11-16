// An async method with a Debug.Assert whose message is an interpolated string handler (the control-flow graph's
// untyped `out bool` capture), stackalloc as a call's receiver (`stackalloc T[n].Slice(...)`), and the generic
// interfaces an array has (IList<T>, ICollection<T>, IEnumerable<T>, IReadOnlyList<T>, IReadOnlyCollection<T>, and
// not ISet<T> or the others): type tests and ISet<T>.Add dispatched over the library's sets. Switch statements on
// nullable values (`case null:`, constants of the underlying type). Arrays' ToString, Equals and GetHashCode called
// virtually. Ref locals in iterators and async methods (one conditional), written through.
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

static class Program
{
    static int s_n = 3;

    static async Task<int> Go()
    {
        await Task.Yield();
        int readBytes = s_n;
        Debug.Assert(readBytes >= 0, $"Expected a non-negative number of bytes, got {readBytes}");
        Span<int> v = s_n <= 8 ? stackalloc int[8].Slice(0, s_n) : new int[s_n];
        v[0] = 5;
        v[^1] = 7;
        Console.WriteLine(v.Length + " " + v[0] + " " + v[1] + " " + v[^1]);
        await Task.Yield();
        return readBytes * 2;
    }

    static void Add(ISet<string> s)
    {
        try { Console.WriteLine(s.GetType().Name + " " + s.Add("a") + " " + s.Add("a") + " " + s.Add("b") + " " + s.Count); }
        catch (Exception e) { Console.WriteLine(s.GetType().Name + " " + e.GetType().Name); }
    }

    static object[] s_vals = { "a", "raw" };
    static Dictionary<int, object> s_d = new() { [1] = "raw" };
    static void Set(ref object slot, object v) => slot = v;

    static IEnumerable<int> RefInIterator(bool live)
    {
        ref object r = ref live ? ref s_vals[1] : ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(s_d, 1);
        Set(ref r, "it" + live);
        yield return 1;
        ref object r0 = ref s_vals[0];
        r0 = "first";
        yield return 2;
    }

    // a ref conditional over parameters (they live in memory, not registers)
    static string PickParam(bool first, string? a, string? b)
    {
        ref string? r = ref first ? ref a : ref b;
        r ??= "filled";
        return a + "/" + b;
    }

    // collection expressions into a HashSet<T> (Add returns bool), in a conditional
    static HashSet<string> MakeSet(string? s) => s == null ? [] : [s, s, s + "x"];

    static async Task RefInAsync()
    {
        await Task.Yield();
        ref object r = ref s_vals[1];
        Set(ref r, "async");
    }

    const byte Oblivious = 0, NotAnnotated = 1, Annotated = 2;

    static int Kind(byte? value)
    {
        switch (value)
        {
            case null: return -1;
            case Oblivious: return 10;
            case NotAnnotated: return 11;
            case Annotated: return 12;
            default: return 99;
        }
    }

    static string Day(int? d) { switch (d) { case null: return "none"; case 0: return "sun"; case 6: return "sat"; default: return "mid"; } }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        Console.WriteLine("go " + Go().Result);
        foreach (var _ in RefInIterator(true)) { }
        foreach (var _ in RefInIterator(false)) { }
        Console.WriteLine(s_vals[0] + " " + s_vals[1] + " " + s_d[1]);
        Console.WriteLine(PickParam(true, null, "b") + " " + PickParam(false, "a", null));
        Console.WriteLine(MakeSet(null).Count + " " + MakeSet("a").Count);
        RefInAsync().Wait();
        Console.WriteLine(s_vals[1]);

        object arr = new string[] { "x", "y" };
        Console.WriteLine($"{arr is IList<string>} {arr is ICollection<string>} {arr is IEnumerable<string>} {arr is IReadOnlyList<string>} {arr is IReadOnlyCollection<string>}");
        object o2 = new int[2];
        Console.WriteLine(arr.ToString() + " " + o2.ToString() + " " + arr.Equals(arr) + " " + o2.Equals(arr) + " " + (arr.GetHashCode() == arr.GetHashCode()));
        Console.WriteLine($"{arr is IList<object>} {arr is IReadOnlyList<object>} {arr is ISet<string>} {arr is IComparer<string>} {arr is IEqualityComparer<string>} {arr is IAsyncEnumerable<string>} {arr is IReadOnlySet<string>}");

        Console.WriteLine($"{Kind(null)} {Kind(0)} {Kind(1)} {Kind(2)} {Kind(3)} {Day(null)} {Day(0)} {Day(6)} {Day(3)}");

        Add(new HashSet<string>());
        Add(new SortedSet<string>());
        Add(ImmutableHashSet<string>.Empty);
        Add(ImmutableSortedSet<string>.Empty);
        Add(ImmutableHashSet.CreateBuilder<string>());
        Add(ImmutableSortedSet.CreateBuilder<string>());
    }
}
