// Test 220 in a program with threads (a thread is started and joined first; the objects stay the main thread's):
// a getter that returns a field borrowed in a program with threads (Driver.ThreadLocalReturn) read the value of
// `x ??= v` back with a read that counts (the library's lazy getters too), and the count was never given back:
// what .NET frees stayed (claude/thread-leaks-1; CS2WASM_NO_COALESCE_PLACE=1 puts it back).
// A collection that grows hands its items to the new array without counting them (Rt.MoveRange: Dictionary, HashSet,
// List) when nothing else still reads the old array. What the collections hold after growing many times, with
// items that are counted (each has a finalizer: none is lost, none is freed early, all are freed when the collection
// is dropped), keys and values that are references, structs with references, strings; a queue that has wrapped; a
// list grown by Add, AddRange, Insert, EnsureCapacity; and the cases where something still reads the old array, in
// which .NET's old array keeps its items: a key whose Equals adds to the dictionary it is being looked up in (the
// insert further up the stack goes on reading the entries it had), a comparer that adds to the set, an enumerator
// that sees the collection changed. (Not here: a span over a list's items, or a `ref` to a dictionary's value, held
// across a growth. The old array is freed at the growth, moved or copied, and a checked build stops at the read.)
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

sealed class Item
{
    public static int Live, Made;
    public readonly int Id;
    public Item(int id) { Id = id; Live++; Made++; }
    ~Item() { System.Threading.Interlocked.Decrement(ref Live); }
    public static string Left() { for (int i = 0; i < 4; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); } return "live " + Live + " of " + Made; }
    public override string ToString() => "#" + Id;
}
struct Slot { public Item? A; public int N; public string? S; }

// a key whose Equals, once armed, adds to the dictionary it is in: the dictionary resizes under the insert that called it
sealed class Reentrant
{
    public static Dictionary<Reentrant, Item>? Target;
    public static int Armed, Calls;
    public readonly int V;
    public Reentrant(int v) { V = v; }
    public override int GetHashCode() => V & 3;
    public override bool Equals(object? o)
    {
        Calls++;
        if (Armed > 0 && Target != null)
        {
            Armed--;
            var t = Target;
            for (int i = 0; i < 40; i++) t.TryAdd(new Reentrant(1000 + Calls * 100 + i), new Item(5000 + i));
        }
        return o is Reentrant r && r.V == V;
    }
}
sealed class AddingComparer : IEqualityComparer<string>
{
    public HashSet<string>? Target; public int Armed;
    public bool Equals(string? a, string? b) { if (Armed > 0 && Target != null) { Armed--; for (int i = 0; i < 30; i++) Target.Add("extra" + i); } return a == b; }
    public int GetHashCode(string s) => s.Length & 1;
}

static class Program
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Grow()
    {
        var d = new Dictionary<int, Item>();
        for (int i = 0; i < 3000; i++) { d[i * 7] = new Item(i); if (i % 5 == 0) d.Remove(i * 3); }
        long sum = 0; foreach (var kv in d) sum += kv.Key ^ kv.Value.Id;
        Console.WriteLine($"dictionary {d.Count} {sum} {d[7 * 2999]} {d.ContainsKey(21)}");
        var byName = new Dictionary<string, Slot>();
        for (int i = 0; i < 500; i++) byName["k" + i] = new Slot { A = new Item(10000 + i), N = i, S = "s" + i };
        Console.WriteLine($"structs {byName.Count} {byName["k499"].A} {byName["k0"].S} {byName.Values.Sum(v => v.N)}");
        var objKeys = new Dictionary<Item, string>();
        for (int i = 0; i < 300; i++) objKeys[new Item(20000 + i)] = "v" + i;
        Console.WriteLine($"object keys {objKeys.Count} {objKeys.Keys.Max(k => k.Id)} {string.Concat(objKeys.Values.Take(3))}");
        var set = new HashSet<Item>();
        var keep = new List<Item>();
        for (int i = 0; i < 700; i++) { var it = new Item(30000 + i); set.Add(it); if (i % 100 == 0) keep.Add(it); if (i % 9 == 0) set.Remove(it); }
        Console.WriteLine($"set {set.Count} {keep.Count(set.Contains)} {set.Max(x => x.Id)}");
        var names = new HashSet<string>(); for (int i = 0; i < 400; i++) names.Add("n" + (i % 250));
        Console.WriteLine($"names {names.Count} {names.Contains("n249")} {names.Contains("n250")}");
        var q = new Queue<Item>();
        for (int i = 0; i < 20; i++) q.Enqueue(new Item(40000 + i));
        for (int i = 0; i < 15; i++) q.Dequeue();
        for (int i = 0; i < 300; i++) { q.Enqueue(new Item(41000 + i)); if (i % 4 == 0) q.Dequeue(); }
        Console.WriteLine($"queue {q.Count} {q.Peek()} {q.Last()} {q.Sum(x => (long)x.Id)}");
        var l = new List<Item>();
        for (int i = 0; i < 200; i++) l.Add(new Item(50000 + i));
        l.AddRange(Enumerable.Range(0, 300).Select(i => new Item(51000 + i)));
        l.Insert(3, new Item(52000)); l.InsertRange(100, new[] { new Item(52001), new Item(52002) });
        l.EnsureCapacity(5000); l.RemoveRange(10, 50); l.TrimExcess(); l.Add(new Item(52003));
        Console.WriteLine($"list {l.Count} {l[3]} {l[^1]} {l.Capacity >= l.Count} {l.Sum(x => (long)x.Id)}");
        var lists = new List<List<Item>>();
        for (int i = 0; i < 40; i++) { var inner = new List<Item>(); for (int k = 0; k <= i; k++) inner.Add(new Item(60000 + i * 100 + k)); lists.Add(inner); }
        Console.WriteLine($"lists {lists.Count} {lists.Sum(x => x.Count)} {lists[39][39]}");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Held()
    {
        // a struct enumerator over a queue's array, the queue grown: the enumerator says so
        var q = new Queue<Item>(); for (int i = 0; i < 4; i++) q.Enqueue(new Item(400 + i));
        var e = q.GetEnumerator(); e.MoveNext();
        for (int i = 0; i < 10; i++) q.Enqueue(new Item(410 + i));
        try { e.MoveNext(); Console.WriteLine("moved"); } catch (InvalidOperationException) { Console.WriteLine("queue changed " + e.Current + " " + q.Count); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Reentered()
    {
        var d = new Dictionary<Reentrant, Item>();
        Reentrant.Target = d;
        for (int i = 0; i < 6; i++) d[new Reentrant(i)] = new Item(600 + i);
        Reentrant.Armed = 1;
        string result;
        try { d[new Reentrant(2)] = new Item(700); result = "set"; }
        catch (InvalidOperationException ex) { result = "threw " + ex.GetType().Name; }
        Console.WriteLine($"reentrant set: {result}, {d.Count} entries, calls {Reentrant.Calls}, value {(d.TryGetValue(new Reentrant(2), out var v) ? v.ToString() : "none")}, {d.Values.Sum(x => (long)x.Id)}");
        Reentrant.Armed = 1;
        try { result = d.TryAdd(new Reentrant(5), new Item(701)) ? "added" : "there"; }
        catch (InvalidOperationException ex) { result = "threw " + ex.GetType().Name; }
        Console.WriteLine($"reentrant add: {result}, {d.Count} entries, {d.Values.Sum(x => (long)x.Id)}");
        Reentrant.Armed = 1;
        try { result = d.Remove(new Reentrant(4)) ? "removed" : "absent"; }
        catch (InvalidOperationException ex) { result = "threw " + ex.GetType().Name; }
        Console.WriteLine($"reentrant remove: {result}, {d.Count} entries, {d.Values.Sum(x => (long)x.Id)}");
        Reentrant.Target = null;
        var c = new AddingComparer();
        var s = new HashSet<string>(c) { "a", "bb", "ccc", "dddd", "e" };
        c.Target = s; c.Armed = 1;
        try { result = s.Add("ccc") ? "added" : "there"; }
        catch (InvalidOperationException ex) { result = "threw " + ex.GetType().Name; }
        c.Target = null;
        Console.WriteLine($"reentrant set add: {result}, {s.Count}, {string.Join(",", s.OrderBy(x => x, StringComparer.Ordinal).Take(6))}");
    }

    [UnmanagedCallersOnly(EntryPoint = "main")] static void Main()
    {
        { var warm = new System.Threading.Thread(() => { }); warm.Start(); warm.Join(); }
        Grow(); Console.WriteLine("after grow: " + Item.Left());
        Held(); Console.WriteLine("after held: " + Item.Left());
        Reentered(); Console.WriteLine("after reentered: " + Item.Left());
    }
}
