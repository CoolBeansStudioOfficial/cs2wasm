// Objects with a mark that end inside a loop (WasmModule.ExpandLeafReleases writes a sealed leaf class's end out
// where a loop lets its last reference go, rt.on_destroy's call with it: the function that clears the weak
// references to an object with a mark): the targets of weak references (WeakReference, WeakReference<T>, a weak
// GCHandle), the keys of a ConditionalWeakTable, strings among them, let go by a loop that stores over their slots,
// by a loop that moves the survivors down, by a list's Clear and RemoveRange and by an array's own end, mixed with
// objects without a mark. After a collection (.NET clears weak references then) every weak reference to an object
// that ended must be dead and every one to an object still held alive, with its fields as they were. (Written for
// a test of the mark's bit before the call, which gave the churn scene nothing and was not kept: with another bit
// tested there the dead ones counted as alive on eighteen lines.)
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

sealed class Leaf { public int Id; public string Name = ""; }
sealed class Pair { public int Id; public string A = "", B = ""; public long Big; }

static class Program
{
    static readonly List<WeakReference<Leaf>> s_leaves = new();
    static readonly List<WeakReference> s_any = new();
    static readonly List<GCHandle> s_handles = new();
    static readonly ConditionalWeakTable<object, StringBuilder> s_table = new();
    static readonly List<WeakReference<string>> s_strings = new();
    static readonly List<Leaf> s_kept = new();

    static string Made(int i) => new StringBuilder().Append("name").Append(i).ToString();

    [MethodImpl(MethodImplOptions.NoInlining)] static Leaf[] Leaves(int n, int from)
    {
        var a = new Leaf[n];
        for (int i = 0; i < n; i++)
        {
            var x = new Leaf { Id = from + i, Name = (i & 1) == 0 ? Made(from + i) : "lit" };
            a[i] = x;
            if (i % 3 == 0) s_leaves.Add(new WeakReference<Leaf>(x));
            if (i % 5 == 0) s_any.Add(new WeakReference(x));
            if (i % 7 == 0) s_handles.Add(GCHandle.Alloc(x, GCHandleType.Weak));
            if (i % 4 == 0) s_table.Add(x, new StringBuilder("of ").Append(x.Id));
            if (i % 6 == 0) s_strings.Add(new WeakReference<string>(x.Name));
            if (i % 11 == 0) s_kept.Add(x);
        }
        return a;
    }
    // a loop that stores over every slot
    [MethodImpl(MethodImplOptions.NoInlining)] static int StoreOver(Leaf[] a, Leaf[] with)
    {
        int n = 0;
        for (int i = 0; i < a.Length; i++) { a[i] = with[i]; n += i; }
        return n;
    }
    // a loop that moves the survivors down
    [MethodImpl(MethodImplOptions.NoInlining)] static int Compact(List<Leaf> list, int mod)
    {
        int w = 0;
        for (int i = 0; i < list.Count; i++)
        {
            var p = list[i];
            p.Id += 1000;
            if (p.Id % mod != 0) list[w++] = p;
        }
        list.RemoveRange(w, list.Count - w);
        return w;
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static int Nulls(Leaf?[] a)
    {
        int n = 0;
        for (int i = 0; i < a.Length; i++) { if ((i & 3) != 3) { a[i] = null; n++; } }
        return n;
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static int StringsOver(string?[] a, int from)
    {
        var dead = new string?[a.Length];
        for (int i = 0; i < a.Length; i++) { a[i] = Made(from + i); if ((i & 1) == 0) s_strings.Add(new WeakReference<string>(a[i]!)); }
        int n = 0;
        for (int i = 0; i < a.Length; i++) { if (i % 3 != 0) a[i] = "over"; n += a[i]!.Length; }
        return n;
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static int Pairs(int n)
    {
        var a = new Pair[n];
        for (int i = 0; i < n; i++)
        {
            a[i] = new Pair { Id = i, A = Made(i), B = (i & 1) == 0 ? "b" : Made(-i), Big = i * 1000003L };
            if (i % 3 == 0) s_any.Add(new WeakReference(a[i]));
            if (i % 4 == 0) s_table.Add(a[i], new StringBuilder("pair ").Append(i));
            if (i % 5 == 0) s_strings.Add(new WeakReference<string>(a[i].A));
        }
        var b = new Pair[n];
        int sum = 0;
        for (int i = 0; i < n; i++) { b[i] = a[n - 1 - i]; sum += b[i].Id; }
        for (int i = 0; i < n; i++) { a[i] = b[i]; }
        for (int i = 0; i < n; i++) { if ((i & 1) == 0) b[i] = null!; }
        for (int i = 0; i < n; i++) { if ((i & 1) == 0) a[i] = null!; sum += i; }
        return sum;     // the array's own end lets the rest go
    }

    static string Count()
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        int leaves = 0, any = 0, handles = 0, strings = 0;
        long ids = 0;
        foreach (var w in s_leaves) if (w.TryGetTarget(out var x)) { leaves++; ids += x.Id + x.Name.Length; }
        foreach (var w in s_any) if (w.Target is { } o) { any++; ids += o is Leaf l ? l.Id : ((Pair)o).Id; }
        foreach (var h in s_handles) if (h.Target is Leaf l) { handles++; ids += l.Id; }
        foreach (var w in s_strings) if (w.TryGetTarget(out var s)) { strings++; ids += s.Length; }
        int inTable = 0;
        foreach (var k in s_kept) if (s_table.TryGetValue(k, out var sb)) inTable += sb.Length;
        return leaves + " of " + s_leaves.Count + ", " + any + " of " + s_any.Count + ", " + handles + " of " + s_handles.Count + ", strings " + strings + " of " + s_strings.Count + ", table " + inTable + ", " + ids;
    }

    // (what holds the objects between the steps: statics, set to null where a step lets them go; a local of a method
    // that is still running may hold its object as long as the method runs under .NET)
    static Leaf[]? s_a, s_with, s_b;
    static List<Leaf>? s_list;
    [MethodImpl(MethodImplOptions.NoInlining)] static void Step(int k)
    {
        switch (k)
        {
            case 0: s_a = Leaves(60, 0); s_with = Leaves(60, 100); Console.Write("made: "); break;
            case 1: Console.Write("stored over " + StoreOver(s_a!, s_with!) + ": "); break;
            case 2: s_list = new List<Leaf>(s_with!); s_with = null; s_a = null; Console.Write("moved down " + Compact(s_list, 3) + ": "); break;
            case 3: Console.Write("moved down " + Compact(s_list!, 2) + ": "); break;
            case 4: s_b = Leaves(40, 200); Console.Write("nulls " + Nulls(s_b) + ": "); break;
            case 5: s_list!.RemoveRange(3, s_list.Count - 6); Console.Write("range: "); break;
            case 6: s_list!.Clear(); s_b = null; Console.Write("cleared: "); break;
            case 7: Console.Write("strings " + StringsOver(new string?[30], 300) + ": "); break;
            case 8: Console.Write("pairs " + Pairs(36) + ": "); break;
        }
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Rounds()
    {
        for (int k = 0; k <= 8; k++) { Step(k); Console.WriteLine(Count()); }
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static long Round(int round)
    {
        s_a = Leaves(200, 1000 * round);
        s_with = Leaves(200, 1000 * round + 500);
        long total = StoreOver(s_a, s_with);
        s_list = new List<Leaf>(s_with);
        s_a = null; s_with = null;
        total += Compact(s_list, 2 + round % 4);
        s_list = null;
        return total;
    }

    [UnmanagedCallersOnly(EntryPoint = "main")] static void Main()
    {
        Rounds();
        Console.WriteLine("after: " + Count());
        s_kept.Clear();
        Console.WriteLine("kept let go: " + Count());
        // many, so that pages fill and empty while marked objects end in them
        long total = 0;
        for (int round = 0; round < 30; round++)
        {
            total += Round(round);
            if (round % 10 == 9) { s_kept.Clear(); Console.WriteLine("round " + round + ": " + Count()); }
        }
        Console.WriteLine("total " + total);
    }
}
