// A span over a list's items (CollectionsMarshal.AsSpan) or a `ref` to a dictionary's value (GetValueRefOrNullRef,
// GetValueRefOrAddDefault) held while the collection grows, is cleared or is dropped: .NET keeps the old array
// through the reference, so the span reads what was there, writes go to the old array (the collection does not see
// them after the growth), and nothing is freed under it. `list.AddRange(CollectionsMarshal.AsSpan(list))` and
// InsertRange of the list's own span; a span taken in a loop; a span from a method and a property of the program's
// that wrap the call; a `ref` held across many insertions and a removal; the collection dropped while the span
// lives. Every item has a finalizer that marks it: an item read after it was freed shows in the sums, and what is
// left at the end is counted.
// Deeper (2026-11-22): the reference comes out through any number of frames and by every way a call has of
// giving one out: a wrapper of a wrapper, a `ref` returned up two frames, a ref struct with spans in it (returned,
// filled by its constructor, filled by a method called on it, inside another ref struct), an `out` and a `ref`
// argument, an interface's and an abstract class's property, a delegate, recursion, and the library's own
// TryGetSpan under LINQ's First/Last while the predicate grows the list.
using System;
using System.Collections.Generic;
using System.Linq;
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

sealed class Bag
{
    readonly List<Item> _items = new();
    public void Add(Item it) => _items.Add(it);
    public int Count => _items.Count;
    public Span<Item> Items => CollectionsMarshal.AsSpan(_items);
    public Span<Item> Slice(int from) => CollectionsMarshal.AsSpan(_items).Slice(from);
    public Item this[int i] => _items[i];
}

sealed class Bag2
{
    readonly Bag _bag = new();
    public void Add(Item it) => _bag.Add(it);
    public int Count => _bag.Count;
    public Span<Item> Items => _bag.Items;
    public Span<Item> Tail(int from) => Deeper.Slice3(_bag, from);
    public Item this[int i] => _bag[i];
}

static class Deeper
{
    public static Span<Item> Slice2(Bag b, int from) => b.Slice(from);
    public static Span<Item> Slice3(Bag b, int from) => Slice2(b, from);
    public static ref Item Slot(Dictionary<int, Item> d, int key) => ref CollectionsMarshal.GetValueRefOrNullRef(d, key);
    public static ref Item Slot2(Dictionary<int, Item> d, int key) => ref Slot(d, key);
    public static ref Item Slot3(Dictionary<int, Item> d, int key) { ref Item r = ref Slot2(d, key); return ref r; }
    // (recursion: the answer for Down leans on itself)
    public static Span<Item> Down(List<Item> l, int depth) => depth == 0 ? CollectionsMarshal.AsSpan(l) : Down(l, depth - 1);
    public static bool TryItems(List<Item> l, out Span<Item> items) { items = CollectionsMarshal.AsSpan(l); return items.Length > 0; }
    public static bool TryItems2(List<Item> l, out Span<Item> items) => TryItems(l, out items);
    public static void Refill(List<Item> l, ref ReadOnlySpan<Item> items) { if (l.Count > 0) items = CollectionsMarshal.AsSpan(l); }
    public static View MakeView(List<Item> l) => new View { Items = CollectionsMarshal.AsSpan(l), N = l.Count, Tail = CollectionsMarshal.AsSpan(l).Slice(1) };
    public static View MakeView2(List<Item> l) => MakeView(l);
    public static Outer MakeOuter(List<Item> a, List<Item> b) => new Outer { X = 7, V = MakeView2(a), W = new View(b) };
}

ref struct View
{
    public Span<Item> Items;
    public int N;
    public ReadOnlySpan<Item> Tail;
    public View(List<Item> l) { Items = CollectionsMarshal.AsSpan(l); N = l.Count; Tail = Items.Slice(l.Count / 2); }
    public void Load(List<Item> l) { Items = CollectionsMarshal.AsSpan(l); N = l.Count; Tail = default; }
    public long Total() { long t = N; foreach (var it in Items) t += it.Id; foreach (var it in Tail) t += it.Id; return t; }
}

ref struct Outer
{
    public int X;
    public View V;
    public View W;
}

interface ISource { Span<Item> Items { get; } ref Item At(int i); }
abstract class Source { public abstract ReadOnlySpan<Item> Items { get; } }

sealed class ListSource : Source, ISource
{
    public readonly List<Item> List = new();
    public override ReadOnlySpan<Item> Items => CollectionsMarshal.AsSpan(List);
    Span<Item> ISource.Items => CollectionsMarshal.AsSpan(List);
    public ref Item At(int i) => ref CollectionsMarshal.AsSpan(List)[i];
}

delegate Span<Item> Getter(List<Item> l);

static class Program
{
    static List<Item> Make(int n, int from) { var l = new List<Item>(); for (int i = 0; i < n; i++) l.Add(new Item(from + i)); return l; }
    static long Sum(ReadOnlySpan<Item> s) { long t = 0; foreach (var it in s) t += it.Id; return t; }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Lists()
    {
        var l = Make(4, 1);
        Span<Item> span = CollectionsMarshal.AsSpan(l);
        for (int i = 0; i < 40; i++) l.Add(new Item(100 + i));
        span[1] = new Item(999);
        Console.WriteLine($"span {span.Length} {span[0]} {span[1]} {span[3]} {Sum(span)} list {l.Count} {l[0]} {l[1]} {l[43]}");
        var self = Make(4, 10);
        self.AddRange(CollectionsMarshal.AsSpan(self));
        self.InsertRange(2, CollectionsMarshal.AsSpan(self));
        self.AddRange(CollectionsMarshal.AsSpan(self).Slice(1, 5));
        Console.WriteLine($"self {self.Count} {string.Join(",", self.Select(x => x.Id))}");
        long total = 0;
        var grow = Make(3, 20);
        for (int r = 0; r < 12; r++)
        {
            var s = CollectionsMarshal.AsSpan(grow);
            for (int i = 0; i < s.Length; i++) { grow.Add(new Item(1000 + r * 100 + i)); total += s[i].Id; }
            total += Sum(s) * 3 + grow.Count;
        }
        Console.WriteLine($"loop {grow.Count} {total}");
        var cleared = Make(5, 30);
        var cs = CollectionsMarshal.AsSpan(cleared);
        cleared.Clear(); cleared.TrimExcess(); cleared.Add(new Item(77));
        Console.WriteLine($"cleared {cs.Length} {cs[0] == null} {cs[4] == null} {cleared.Count}");
        var dropped = Make(6, 40);
        var ds = CollectionsMarshal.AsSpan(dropped);
        dropped.Add(new Item(46)); dropped.Add(new Item(47)); dropped.Add(new Item(48));
        dropped = null!;
        Console.WriteLine($"dropped {Sum(ds)} {ds[5]}");
        var bag = new Bag(); for (int i = 0; i < 4; i++) bag.Add(new Item(50 + i));
        var items = bag.Items; var tail = bag.Slice(2);
        for (int i = 0; i < 30; i++) bag.Add(new Item(60 + i));
        items[0] = new Item(555);
        Console.WriteLine($"wrapped {Sum(items)} {Sum(tail)} {bag.Count} {bag[0]} {bag[33]}");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Maps()
    {
        var d = new Dictionary<int, Item>();
        for (int i = 0; i < 3; i++) d[i] = new Item(200 + i);
        ref Item slot = ref CollectionsMarshal.GetValueRefOrNullRef(d, 1);
        for (int i = 10; i < 80; i++) d[i] = new Item(300 + i);
        var seen = slot;
        slot = new Item(998);
        Console.WriteLine($"ref {seen} {slot} dictionary {d.Count} {d[1]} {d[79]}");
        ref Item? added = ref CollectionsMarshal.GetValueRefOrAddDefault(d, 500, out bool existed);
        added = new Item(501);
        for (int i = 600; i < 700; i++) d[i] = new Item(i);
        d.Remove(500);
        Console.WriteLine($"added {existed} {added} {d.ContainsKey(500)} {d.Count}");
        ref Item missing = ref CollectionsMarshal.GetValueRefOrNullRef(d, 12345);
        Console.WriteLine($"missing {Unsafe.IsNullRef(ref missing)}");
        var names = new Dictionary<string, Item> { ["a"] = new Item(1), ["b"] = new Item(2) };
        long sum = 0;
        for (int r = 0; r < 20; r++)
        {
            ref Item v = ref CollectionsMarshal.GetValueRefOrNullRef(names, r % 2 == 0 ? "a" : "b");
            names["k" + r] = new Item(700 + r);
            sum += v.Id;
            v = new Item(800 + r);
        }
        Console.WriteLine($"names {names.Count} {sum} {names["a"]} {names["b"]}");
        var gone = new Dictionary<int, Item> { [1] = new Item(900) };
        ref Item g = ref CollectionsMarshal.GetValueRefOrNullRef(gone, 1);
        gone.Clear(); gone = null!;
        Console.WriteLine($"gone {(g == null ? "null" : g.ToString())}");
    }

    static void Grow(List<Item> l, int n, int from) { for (int i = 0; i < n; i++) l.Add(new Item(from + i)); }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Frames()
    {
        var bag = new Bag2();
        for (int i = 0; i < 4; i++) bag.Add(new Item(2000 + i));
        var items = bag.Items; var tail = bag.Tail(1);
        for (int i = 0; i < 50; i++) bag.Add(new Item(2100 + i));
        items[0] = new Item(2999);
        Console.WriteLine($"two frames {Sum(items)} {Sum(tail)} {bag.Count} {bag[0]} {bag[53]}");
        var l = Make(5, 2200);
        var down = Deeper.Down(l, 6);
        Grow(l, 60, 2300);
        Console.WriteLine($"recursion {Sum(down)} {l.Count}");
        var d = new Dictionary<int, Item>();
        for (int i = 0; i < 3; i++) d[i] = new Item(2400 + i);
        ref Item slot = ref Deeper.Slot3(d, 2);
        for (int i = 10; i < 90; i++) d[i] = new Item(2500 + i);
        var seen = slot;
        slot = new Item(2998);
        Console.WriteLine($"ref frames {seen} {slot} {d.Count} {d[2]}");
        long sum = 0;
        for (int r = 0; r < 10; r++)
        {
            ref Item v = ref Deeper.Slot2(d, r % 3);
            d[1000 + r] = new Item(2600 + r);
            sum += v.Id;
        }
        Console.WriteLine($"ref loop {sum} {d.Count}");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Structs()
    {
        var a = Make(6, 3000); var b = Make(4, 3100);
        var view = Deeper.MakeView2(a);
        Grow(a, 70, 3200);
        Console.WriteLine($"view {view.Total()} {view.Items.Length} {view.Tail.Length} {a.Count}");
        var made = new View(b);
        Grow(b, 70, 3300);
        Console.WriteLine($"constructed {made.Total()} {b.Count}");
        var c = Make(5, 3400);
        View loaded = default;
        loaded.Load(c);
        Grow(c, 70, 3500);
        Console.WriteLine($"loaded {loaded.Total()} {c.Count}");
        var e = Make(3, 3600); var f = Make(3, 3700);
        var outer = Deeper.MakeOuter(e, f);
        Grow(e, 40, 3800); Grow(f, 40, 3900);
        e.Clear(); e.TrimExcess(); f = null!;
        Console.WriteLine($"outer {outer.X} {outer.V.Total()} {outer.W.Total()}");
        long sum = 0;
        var g = Make(2, 4000);
        for (int r = 0; r < 8; r++)
        {
            var v = new View(g);
            Grow(g, 9, 4100 + 10 * r);
            sum += v.Total();
        }
        Console.WriteLine($"view loop {sum} {g.Count}");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Arguments()
    {
        var a = Make(5, 5000);
        Deeper.TryItems2(a, out var items);
        Grow(a, 70, 5100);
        Console.WriteLine($"out {Sum(items)} {a.Count}");
        var b = Make(5, 5200);
        ReadOnlySpan<Item> ro = default;
        Deeper.Refill(b, ref ro);
        Grow(b, 70, 5300);
        Console.WriteLine($"ref argument {Sum(ro)} {b.Count}");
        long sum = 0;
        var c = Make(2, 5400);
        for (int r = 0; r < 8; r++)
        {
            if (!Deeper.TryItems(c, out Span<Item> s)) break;
            Grow(c, 9, 5500 + 10 * r);
            sum += Sum(s);
        }
        Console.WriteLine($"out loop {sum} {c.Count}");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Slots()
    {
        var src = new ListSource();
        Grow(src.List, 5, 6000);
        ISource i = src; Source s = src;
        var x = i.Items; var y = s.Items;
        ref Item at = ref i.At(2);
        Grow(src.List, 70, 6100);
        Console.WriteLine($"interface {Sum(x)} abstract {Sum(y)} at {at} {src.List.Count}");
        Getter get = l => CollectionsMarshal.AsSpan(l);
        var z = get(src.List);
        Grow(src.List, 200, 6200);
        Console.WriteLine($"delegate {Sum(z)} {src.List.Count}");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Linq()
    {
        var l = Make(4, 7000);
        // (the predicate grows the list under the library's span of it)
        var first = l.First(x => { if (l.Count < 200) Grow(l, 30, 7100); return x.Id == 7003; });
        var m = Make(4, 7300);
        var last = m.Last(x => { if (m.Count < 200) Grow(m, 30, 7400); return x.Id == 7300; });
        var n = Make(6, 7500);
        bool any = n.Any(x => { if (n.Count < 200) Grow(n, 50, 7600); return x.Id == 7505; });
        Console.WriteLine($"linq {first} {l.Count} {last} {m.Count} {any} {n.Count}");
    }

    [UnmanagedCallersOnly(EntryPoint = "main")] static void Main()
    {
        Lists(); Console.WriteLine("after lists: " + Item.Left());
        Maps(); Console.WriteLine("after maps: " + Item.Left());
        Frames(); Console.WriteLine("after frames: " + Item.Left());
        Structs(); Console.WriteLine("after structs: " + Item.Left());
        Arguments(); Console.WriteLine("after arguments: " + Item.Left());
        Slots(); Console.WriteLine("after slots: " + Item.Left());
        Linq(); Console.WriteLine("after linq: " + Item.Left());
    }
}
