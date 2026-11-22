// `if (map.TryGetValue(key, out var v)) ...` where the branch only reads: the value is not counted for v
// (OutBorrows.cs). What the lookups give in the shapes that qualify (a sum over a field, a store of a number through
// v, `&&` conditions, nested ifs, a return of v or of a field of v, a null value and the exception its use throws, a
// loop, a set's TryGetValue, a class of the program's whose TryGet reads a field or an element) and in the shapes
// that must count as before, each with a use of v after the place it was read from has let it go: the branch removes
// the key, clears the map, replaces the value, calls a method that does, drops the map; v used after the `if`; v
// captured; a TryGet that makes a new object, one that returns what a local held, a virtual one, one on a struct.
// Every item has a finalizer that marks it: an item used after its finalizer ran shows in the sums, and what is
// left at the end is counted.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

sealed class Item
{
    public static int Live;
    public int Id, Hits;
    public Item? Next;
    public Item(int id) { Id = id; Live++; }
    ~Item() { Id = -1000000; System.Threading.Interlocked.Decrement(ref Live); }
    public static string Left() { for (int i = 0; i < 4; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); } return "live " + Live; }
}

class Shelf
{
    readonly Item?[] _slots = new Item?[8];
    Item? _one;
    public void Put(int i, Item? it) { _slots[i & 7] = it; _one = it; }
    // reads an element / a field of the object, then returns
    public bool TryGet(int i, out Item? found) { int k = i & 7; if (_slots[k] != null) { found = _slots[k]; return true; } found = null; return false; }
    public bool TryOne(out Item? found) { if (_one != null) { found = _one; return true; } found = default; return false; }
    // makes the object it gives out: nothing else keeps it
    public bool TryMake(int i, out Item? made) { if (i > 0) { made = new Item(i); return true; } made = null; return false; }
    // gives out what a local held after the place was cleared
    public bool TryTake(int i, out Item? taken) { Item? t = _slots[i & 7]; _slots[i & 7] = null; taken = t; return t != null; }
    // reads, then runs other code before it returns
    public bool TryGetAndClear(int i, out Item? found) { found = _slots[i & 7]; Clear(i); return found != null; }
    void Clear(int i) { _slots[i & 7] = null; }
    public virtual bool TryVirtual(int i, out Item? found) { found = _slots[i & 7]; return found != null; }
    public void Drop(int i) { _slots[i & 7] = null; if (_one != null && _one.Id == i) _one = null; }
}
sealed class DeepShelf : Shelf { public override bool TryVirtual(int i, out Item? found) { found = new Item(i + 500); return true; } }
struct Box { public Item? It; public bool TryGet(out Item? found) { found = It; return It != null; } }

static class Program
{
    static Dictionary<int, Item> s_map = new();
    static Item? s_kept;

    static Dictionary<int, Item> Fill(int n) { var d = new Dictionary<int, Item>(); for (int i = 0; i < n; i++) d[i * 3] = new Item(i); return d; }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static Item? Find(Dictionary<int, Item> d, int key) { if (d.TryGetValue(key, out var v)) return v; return null; }
    [MethodImpl(MethodImplOptions.NoInlining)]
    static int IdOf(Dictionary<int, Item> d, int key) { if (d.TryGetValue(key, out var v)) return v.Id; return -1; }
    [MethodImpl(MethodImplOptions.NoInlining)]
    static Item? NextOf(Dictionary<int, Item> d, int key) { if (d.TryGetValue(key, out var v) && v.Next != null) return v.Next; return null; }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Reads()
    {
        var d = Fill(200);
        s_map = d;
        long sum = 0; int hits = 0;
        for (int k = -5; k < 700; k++) if (d.TryGetValue(k, out var v)) { sum += v.Id; hits++; }
        for (int k = 0; k < 700; k++) if (s_map.TryGetValue(k, out var v)) sum += v.Id;
        for (int k = 0; k < 700; k += 2) if (d.TryGetValue(k, out var v) && v.Id % 2 == 0 && k > 10) { v.Hits++; v.Hits += 2; sum += v.Hits; }
        for (int k = 0; k < 700; k++) if (d.TryGetValue(k, out var v)) { if (v.Id > 100) { if (v.Hits > 0) sum += 7; else sum -= 1; } else sum += v.Id * 2; }
        Console.WriteLine($"reads {hits} {sum} {d.Values.Sum(x => x.Hits)}");
        var a = Find(d, 30); var b = Find(d, 31);
        d[30].Next = new Item(9000);
        var n = NextOf(d, 30);
        Console.WriteLine($"found {a?.Id} {b == null} {IdOf(d, 33)} {IdOf(d, 34)} {n?.Id} {NextOf(d, 33) == null}");
        d.Clear();
        Console.WriteLine($"kept after clear {a?.Id} {n?.Id}");
        var withNull = new Dictionary<string, Item?> { ["a"] = new Item(1), ["n"] = null };
        int got = 0;
        foreach (var key in new[] { "a", "n", "x", "a" })
        {
            try { if (withNull.TryGetValue(key, out var v)) got += v!.Id + 10; else got += 1000; }
            catch (NullReferenceException) { got += 100000; }
        }
        Console.WriteLine($"null value {got}");
        var set = new HashSet<Item>(); var probe = new Item(77); set.Add(probe);
        int same = 0; for (int i = 0; i < 5; i++) if (set.TryGetValue(probe, out var actual)) same += actual.Id;
        Console.WriteLine($"set {same}");
        var shelf = new Shelf(); for (int i = 0; i < 8; i++) shelf.Put(i, new Item(300 + i));
        long ssum = 0;
        for (int i = 0; i < 20; i++) { if (shelf.TryGet(i, out var f)) ssum += f!.Id; if (shelf.TryOne(out var o)) ssum += o!.Id; }
        Console.WriteLine($"shelf {ssum}");
    }

    [MethodImpl(MethodImplOptions.NoInlining)] static void RemoveKey(Dictionary<int, Item> d, int k) => d.Remove(k);

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void LetGo()
    {
        long sum = 0;
        var d = Fill(40);
        // the branch lets the value go from where it was read, then uses it
        if (d.TryGetValue(3, out var v1)) { d.Remove(3); sum += v1.Id; }
        if (d.TryGetValue(6, out var v2)) { d.Clear(); sum += v2.Id; }
        d = Fill(40);
        if (d.TryGetValue(9, out var v3)) { d[9] = new Item(5000); sum += v3.Id; }
        if (d.TryGetValue(12, out var v4)) { RemoveKey(d, 12); sum += v4.Id; }
        if (d.TryGetValue(15, out var v5)) { d = new Dictionary<int, Item>(); sum += v5.Id; }
        d = Fill(40);
        // used after the if
        d.TryGetValue(18, out var v6); d.Remove(18); sum += v6!.Id;
        if (d.TryGetValue(21, out var v7)) sum += 1;
        d.Remove(21); sum += v7!.Id;
        // captured
        if (d.TryGetValue(24, out var v8)) { Func<int> f = () => v8.Id; d.Remove(24); sum += f(); }
        // kept in a static, in a field, in a list
        if (d.TryGetValue(27, out var v9)) { s_kept = v9; }
        d.Remove(27); sum += s_kept!.Id; s_kept = null;
        var list = new List<Item>();
        for (int k = 30; k < 60; k += 3) if (d.TryGetValue(k, out var v)) list.Add(v);
        d.Clear(); sum += list.Sum(x => x.Id);
        Console.WriteLine($"let go {sum}");
        // a TryGet that is not a read of the object's own place
        var shelf = new Shelf(); for (int i = 0; i < 8; i++) shelf.Put(i, new Item(700 + i));
        long s2 = 0;
        if (shelf.TryMake(5, out var made)) s2 += made!.Id;
        if (shelf.TryTake(1, out var taken)) s2 += taken!.Id;
        if (shelf.TryGetAndClear(2, out var cleared)) s2 += cleared!.Id;
        Shelf deep = new DeepShelf();
        if (deep.TryVirtual(3, out var virt)) s2 += virt!.Id;
        if (shelf.TryVirtual(4, out var virt2)) s2 += virt2!.Id;
        if (shelf.TryGet(6, out var dropped)) { shelf.Drop(6); s2 += dropped!.Id; }
        var box = new Box { It = new Item(900) };
        if (box.TryGet(out var boxed)) { box.It = null; s2 += boxed!.Id; }
        Console.WriteLine($"not reads {s2}");
    }

    [UnmanagedCallersOnly(EntryPoint = "main")] static void Main()
    {
        Reads(); s_map = new(); Console.WriteLine("after reads: " + Item.Left());
        LetGo(); Console.WriteLine("after let go: " + Item.Left());
    }
}
