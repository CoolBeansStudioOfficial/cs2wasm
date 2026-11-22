// A span over a list's items (CollectionsMarshal.AsSpan) or a `ref` to a dictionary's value (GetValueRefOrNullRef,
// GetValueRefOrAddDefault) held while the collection grows, is cleared or is dropped: .NET keeps the old array
// through the reference, so the span reads what was there, writes go to the old array (the collection does not see
// them after the growth), and nothing is freed under it. `list.AddRange(CollectionsMarshal.AsSpan(list))` and
// InsertRange of the list's own span; a span taken in a loop; a span from a method and a property of the program's
// that wrap the call; a `ref` held across many insertions and a removal; the collection dropped while the span
// lives. Every item has a finalizer that marks it: an item read after it was freed shows in the sums, and what is
// left at the end is counted.
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

    [UnmanagedCallersOnly(EntryPoint = "main")] static void Main()
    {
        Lists(); Console.WriteLine("after lists: " + Item.Left());
        Maps(); Console.WriteLine("after maps: " + Item.Left());
    }
}
