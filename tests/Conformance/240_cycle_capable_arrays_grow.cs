// A collection of a struct that can be on a cycle (an item's `object` can be its owner) grows while its array is
// still a plain object: a new list, dictionary or set with a capacity, filled past it with items that refer to
// no object of a cycle, then given to its owner. Such an array has the arena's words before its header and is
// freed with them; Rt.MoveItems (collections that grow by moving) left it an array of bytes, which is freed at the
// header: 32 bytes into its block. Here: among large blocks of other kinds that are kept and checked; with the
// old move (IL2WASM_ARRAY_MOVE_CYCLE=1) the program traps in the allocator. The self-hosted compiler's
// Inliner (a List<Instr> made with a capacity for each function it rewrites) is where this was found.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
struct Item { public long Imm; public object? Ref; public int V; }
sealed class Owner { public List<Item> Items = new(); public Dictionary<int, Item> Map = new(); public HashSet<(int, object?)> Set = new(); public Queue<Item> Q = new(); }
static class P
{
    // a new list with a capacity, filled past it with items that refer to no object of a cycle, then given to
    // its owner: the list's first array is still a plain object when the list grows
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Rebuild(Owner f, int n, int round)
    {
        var body = new List<Item>(n);
        for (int i = 0; i < n + 1 + (round & 3); i++) body.Add(new Item { Imm = i, Ref = (i & 1) == 0 ? null : "s", V = round });
        var map = new Dictionary<int, Item>(n / 4);
        var set = new HashSet<(int, object?)>(n / 4);
        for (int i = 0; i < n / 2; i++) { map[i] = new Item { Imm = i, V = round }; set.Add((i, null)); }
        var q = new Queue<Item>(n / 4 + 4);
        for (int i = 0; i < n / 4 + 4; i++) q.Enqueue(new Item { Imm = i, V = round });
        q.Dequeue(); q.Dequeue(); q.Enqueue(new Item { Imm = -1, V = round }); q.Enqueue(new Item { Imm = -2, V = round });
        for (int i = 0; i < n / 2; i++) q.Enqueue(new Item { Imm = i, Ref = (i & 3) == 0 ? "q" : null, V = round });
        f.Items = body; f.Map = map; f.Set = set; f.Q = q;
    }
    [UnmanagedCallersOnly(EntryPoint = "main")] static void Main()
    {
        var a = new Owner();
        a.Items.Add(new Item { Ref = a }); a.Items.Clear();
        long total = 0, bad = 0;
        var keep = new List<long[]>();
        var items = new List<Item[]>();
        for (int round = 0; round < 400; round++)
        {
            int n = 30 + (round * 131) % 9000;
            Rebuild(a, n, round);
            var x = new long[(n * 2 + round) % 5000 + 10]; for (int i = 0; i < x.Length; i++) x[i] = round * 100000L + i;
            if ((round & 1) == 0) keep.Add(x);
            var y = new Item[(n + round * 7) % 3000 + 5]; for (int i = 0; i < y.Length; i++) y[i] = new Item { Imm = round, V = i };
            if ((round % 3) == 0) items.Add(y);
            if (keep.Count > 40) keep.RemoveAt(round % 17);
            if (items.Count > 30) items.RemoveAt(round % 13);
            foreach (var it in a.Items) { if (it.V != round) bad++; total += it.Imm; }
            foreach (var kv in a.Map) { if (kv.Value.Imm != kv.Key) bad++; }
            total += a.Set.Count;
            foreach (var it in a.Q) { if (it.V != round) bad++; total += it.Imm; }
            foreach (var k in keep) { long b0 = k[0]; for (int i = 1; i < k.Length; i++) if (k[i] != b0 + i) { bad++; break; } }
            foreach (var k in items) { for (int i = 0; i < k.Length; i++) if (k[i].V != i || k[i].Imm != k[0].Imm) { bad++; break; } }
        }
        Console.WriteLine(total + " bad " + bad);
    }
}
