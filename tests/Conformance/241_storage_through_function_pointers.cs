// A span over a list's items or a `ref` to a dictionary's value that comes out of a call through a function
// pointer (`delegate*<...>`), held while the collection grows, is cleared or is dropped: .NET keeps the old array
// through the reference (test 222 for calls, virtual methods and delegates). By every way a call gives one out: the
// span returned, a `ref` returned, a ref struct with spans in it, an `out` and a `ref` argument; in a loop; through
// a generic method. Every item has a finalizer that marks it: an item read after it was freed shows in the sums.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

sealed class Item
{
    public static int Live;
    public int Id;
    public Item(int id) { Id = id; Live++; }
    ~Item() { Id = -1000000; System.Threading.Interlocked.Decrement(ref Live); }
    public static string Left() { for (int i = 0; i < 4; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); } return "live " + Live; }
    public override string ToString() => "#" + Id;
}

ref struct View
{
    public Span<Item> Items;
    public int N;
    public ReadOnlySpan<Item> Tail;
}

static unsafe class Program
{
    static List<Item> Make(int n, int from) { var l = new List<Item>(); for (int i = 0; i < n; i++) l.Add(new Item(from + i)); return l; }
    static long Sum(ReadOnlySpan<Item> s) { long t = 0; foreach (var it in s) t += it.Id; return t; }

    static Span<Item> Items(List<Item> l) => CollectionsMarshal.AsSpan(l);
    static ref Item Slot(Dictionary<int, Item> d, int key) => ref CollectionsMarshal.GetValueRefOrNullRef(d, key);
    static ref Item SlotOrAdd(Dictionary<int, Item> d, int key) => ref CollectionsMarshal.GetValueRefOrAddDefault(d, key, out _);
    static View MakeView(List<Item> l) => new View { Items = CollectionsMarshal.AsSpan(l), N = l.Count, Tail = CollectionsMarshal.AsSpan(l).Slice(1) };
    static bool TryItems(List<Item> l, out Span<Item> items) { items = CollectionsMarshal.AsSpan(l); return items.Length > 0; }
    static void Refill(List<Item> l, ref ReadOnlySpan<Item> items) { if (l.Count > 0) items = CollectionsMarshal.AsSpan(l); }
    static Span<T> ItemsOf<T>(List<T> l) => CollectionsMarshal.AsSpan(l);
    static long Through<T>(delegate*<List<T>, Span<T>> f, List<T> l, T more, Func<T, long> id)
    {
        var s = f(l);
        for (int i = 0; i < 40; i++) l.Add(more);
        long t = 0; foreach (var x in s) t += id(x);
        return t;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Spans()
    {
        delegate*<List<Item>, Span<Item>> f = &Items;
        var l = Make(4, 1);
        Span<Item> span = f(l);
        for (int i = 0; i < 40; i++) l.Add(new Item(100 + i));
        span[1] = new Item(999);
        Console.WriteLine($"span {span.Length} {span[0]} {span[1]} {span[3]} {Sum(span)} list {l.Count} {l[0]} {l[1]} {l[43]}");
        long total = 0;
        var grow = Make(3, 20);
        for (int r = 0; r < 12; r++)
        {
            var s = f(grow);
            for (int i = 0; i < s.Length; i++) { grow.Add(new Item(1000 + r * 100 + i)); total += s[i].Id; }
            total += Sum(s) * 3 + grow.Count;
        }
        Console.WriteLine($"loop {grow.Count} {total}");
        var cleared = Make(5, 30);
        var cs = f(cleared);
        cleared.Clear(); cleared.TrimExcess(); cleared.Add(new Item(77));
        Console.WriteLine($"cleared {cs.Length} {cs[0] == null} {cs[4] == null} {cleared.Count}");
        var dropped = Make(6, 40);
        var ds = f(dropped);
        dropped.Add(new Item(46)); dropped.Add(new Item(47)); dropped.Add(new Item(48));
        dropped = null!;
        Console.WriteLine($"dropped {Sum(ds)} {ds[5]}");
        // (the span declared before the loop that fills it)
        var outer = Make(3, 60);
        Span<Item> kept = default;
        for (int r = 0; r < 3; r++) { if (r == 0) kept = f(outer); for (int i = 0; i < 20; i++) outer.Add(new Item(70 + i)); }
        Console.WriteLine($"outer {kept.Length} {Sum(kept)} {outer.Count}");
        var g = Make(3, 80);
        Console.WriteLine($"generic {Through<Item>(&ItemsOf<Item>, g, new Item(5), x => x.Id)} {g.Count}");
        var gi = new List<int> { 1, 2, 3 };
        Console.WriteLine($"generic int {Through<int>(&ItemsOf<int>, gi, 9, x => x)} {gi.Count}");
        // (the call in a scope of its own, the reference used after it; found with this test: the keep ended with
        // the scope the call was in, for every kind of call)
        Nested(f, true);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Nested(delegate*<List<Item>, Span<Item>> f, bool yes)
    {
        var a = Make(3, 600); var b = Make(3, 610); var c = Make(3, 620); var d = Make(3, 630);
        Span<Item> sa = default, sb = default, sc = default; ReadOnlySpan<Item> sd = default;
        if (yes) sa = f(a);
        if (yes) { sb = Items(b); }
        for (int r = 0; r < 3; r++) { if (r == 1) sc = CollectionsMarshal.AsSpan(c); c.Add(new Item(640 + r)); }
        try { Refill(d, ref sd); } finally { d.Add(new Item(650)); }
        for (int i = 0; i < 40; i++) { a.Add(new Item(700 + i)); b.Add(new Item(700 + i)); c.Add(new Item(700 + i)); d.Add(new Item(700 + i)); }
        Console.WriteLine($"nested {Sum(sa)} {Sum(sb)} {Sum(sc)} {Sum(sd)} {a.Count + b.Count + c.Count + d.Count}");
        Func<List<Item>, long> lambda = l => { Span<Item> s = default; if (l.Count > 0) s = CollectionsMarshal.AsSpan(l); for (int i = 0; i < 40; i++) l.Add(new Item(800 + i)); return Sum(s); };
        long Local(List<Item> l) { Span<Item> s = default; if (l.Count > 0) { s = Items(l); } for (int i = 0; i < 40; i++) l.Add(new Item(900 + i)); return Sum(s); }
        Console.WriteLine($"lambda {lambda(Make(3, 660))} local {Local(Make(3, 670))}");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Refs()
    {
        delegate*<Dictionary<int, Item>, int, ref Item> slot = &Slot;
        delegate*<Dictionary<int, Item>, int, ref Item> add = &SlotOrAdd;
        var d = new Dictionary<int, Item>();
        for (int i = 0; i < 3; i++) d[i] = new Item(200 + i);
        ref Item r = ref slot(d, 1);
        for (int i = 3; i < 200; i++) d[i] = new Item(200 + i);
        Console.WriteLine($"ref {r} {d[1]} {d.Count}");
        r = new Item(555);
        Console.WriteLine($"ref written {r} {d[1]}");
        ref Item a = ref add(d, 1000);
        Console.WriteLine($"added {a == null} {d.Count}");
        for (int i = 1001; i < 1400; i++) d[i] = new Item(i);
        a = new Item(556);
        Console.WriteLine($"added written {a} {d[1000] == null} {d.Count}");
        // (the value read at once: no reference is left)
        Console.WriteLine($"read {slot(d, 2)} {slot(d, 199).Id}");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Structs()
    {
        delegate*<List<Item>, View> mk = &MakeView;
        delegate*<List<Item>, out Span<Item>, bool> tryItems = &TryItems;
        delegate*<List<Item>, ref ReadOnlySpan<Item>, void> refill = &Refill;
        var l = Make(5, 300);
        var v = mk(l);
        for (int i = 0; i < 40; i++) l.Add(new Item(310 + i));
        Console.WriteLine($"view {v.N} {Sum(v.Items)} {Sum(v.Tail)} {l.Count}");
        var o = Make(4, 400);
        bool ok = tryItems(o, out var os);
        for (int i = 0; i < 40; i++) o.Add(new Item(410 + i));
        Console.WriteLine($"out {ok} {Sum(os)} {o.Count}");
        var q = Make(4, 500);
        ReadOnlySpan<Item> qs = default;
        refill(q, ref qs);
        for (int i = 0; i < 40; i++) q.Add(new Item(510 + i));
        Console.WriteLine($"ref arg {Sum(qs)} {q.Count}");
        q.Clear(); q.TrimExcess();
        Console.WriteLine($"ref arg cleared {qs.Length} {qs[0] == null}");
    }

    [UnmanagedCallersOnly(EntryPoint = "main")] static void Main()
    {
        Spans();
        Console.WriteLine(Item.Left());
        Refs();
        Console.WriteLine(Item.Left());
        Structs();
        Console.WriteLine(Item.Left());
    }
}
