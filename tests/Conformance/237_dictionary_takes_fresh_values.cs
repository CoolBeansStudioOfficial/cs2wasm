// `map[key] = new Item()`, `map.Add(key, Make())`, `map.TryAdd(key, new Item())`: the new value's count goes to
// the dictionary (Dictionary.TryInsert consumes its value: OwnedParams.cs, IsMapInsert) instead of being counted by
// the store and let go by the caller. What must stay as .NET has it: the value stored once and kept; the value
// that was there before let go; a value that is not stored after all (Add of a key that is there: the exception,
// TryAdd of one: false; a key whose GetHashCode or Equals throws) dropped and finalized; a value the caller still
// uses afterwards (not fresh) counted as before; the result of the assignment used. Every item has a finalizer:
// what is left is counted after each part. MiniMap's TryInsert stores its value in two places in a loop, which
// is why the rule asks for a return after a store in a loop: with IL2WASM_OWNED_INSERT_BREAK=1 (the rule not asked
// for, a program's own TryInsert taken as Dictionary's) the "mini" lines differ.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

sealed class Item
{
    public static int Live, Made;
    public int Id;
    public Item(int id) { Id = id; Live++; Made++; }
    ~Item() { Id = -1000000; System.Threading.Interlocked.Decrement(ref Live); }
    public static string Left() { for (int i = 0; i < 4; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); } return "live " + Live + " of " + Made; }
    public override string ToString() => "#" + Id;
}

struct Pair { public Item? A; public int N; public string? S; }

sealed class Touchy
{
    public static bool ThrowOnHash, ThrowOnEquals;
    public readonly int V;
    public Touchy(int v) { V = v; }
    public override int GetHashCode() { if (ThrowOnHash) throw new InvalidOperationException("hash " + V); return V & 7; }
    public override bool Equals(object? o) { if (ThrowOnEquals) throw new InvalidOperationException("equals " + V); return o is Touchy t && t.V == V; }
}

sealed class MiniMap
{
    readonly Item?[] _a = new Item?[8], _b = new Item?[8];
    public bool TryInsert(int key, Item value, int behavior)
    {
        for (int i = 0; i < _a.Length; i++)
        {
            if (i != (key & 7)) continue;
            _a[i] = value;
            _b[i] = value;
        }
        return behavior == 0;
    }
    public void DropFirst(int key) => _a[key & 7] = null;
    public string Second(int key) => _b[key & 7] == null ? "null" : _b[key & 7]!.ToString();
}

static class Program
{
    static Item Make(int id) => new Item(id);

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Ints()
    {
        var d = new Dictionary<int, Item>();
        for (int i = 0; i < 200; i++) d[i] = new Item(i);
        for (int i = 0; i < 200; i += 2) d[i] = Make(1000 + i);
        for (int i = 200; i < 300; i++) d.Add(i, new Item(i) { Id = 2000 + i });
        int refused = 0, threw = 0;
        for (int i = 0; i < 50; i++) if (!d.TryAdd(i, new Item(3000 + i))) refused++;
        for (int i = 300; i < 320; i++) if (d.TryAdd(i, Make(i))) refused--;
        for (int i = 0; i < 30; i++) { try { d.Add(i, new Item(4000 + i)); } catch (ArgumentException) { threw++; } }
        long sum = 0;
        foreach (var kv in d) sum += kv.Value.Id;
        Console.WriteLine($"ints {d.Count} {sum} refused {refused} threw {threw} {d[0]} {d[1]} {d[250]} {d[319]}");
        // not fresh: the caller goes on using the value
        var kept = new Item(5000);
        d[5000] = kept; d.Add(5001, kept); d.TryAdd(5002, kept);
        kept.Id++;
        // the assignment's own value
        var chained = d[5003] = new Item(5003);
        Item? last;
        d[5004] = last = Make(5004);
        Console.WriteLine($"kept {kept} {d[5000]} {d[5001]} {d[5002]} {ReferenceEquals(kept, d[5002])} chained {chained} {ReferenceEquals(chained, d[5003])} {last} {d.Count}");
        d.Remove(5000); d.Remove(5001);
        Console.WriteLine($"still {kept} {d[5002]}");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Keys()
    {
        var d = new Dictionary<string, Item>();
        for (int i = 0; i < 150; i++) d["k" + i] = new Item(6000 + i);
        for (int i = 0; i < 150; i += 3) d["k" + i] = new Item(7000 + i);
        int threw = 0;
        for (int i = 0; i < 20; i++) { try { d.Add("k" + i, new Item(8000 + i)); } catch (ArgumentException) { threw++; } }
        long sum = 0;
        foreach (var kv in d) sum += kv.Value.Id;
        Console.WriteLine($"strings {d.Count} {sum} threw {threw} {d["k0"]} {d["k1"]}");
        var t = new Dictionary<Touchy, Item>();
        for (int i = 0; i < 20; i++) t[new Touchy(i)] = new Item(9000 + i);
        int hash = 0, equals = 0;
        Touchy.ThrowOnHash = true;
        for (int i = 0; i < 10; i++) { try { t[new Touchy(i)] = new Item(9100 + i); } catch (InvalidOperationException) { hash++; } }
        Touchy.ThrowOnHash = false; Touchy.ThrowOnEquals = true;
        for (int i = 0; i < 10; i++) { try { t.Add(new Touchy(i), new Item(9200 + i)); } catch (InvalidOperationException) { equals++; } }
        for (int i = 0; i < 10; i++) { try { t.TryAdd(new Touchy(i), Make(9300 + i)); } catch (InvalidOperationException) { equals++; } }
        Touchy.ThrowOnEquals = false;
        sum = 0;
        foreach (var kv in t) sum += kv.Value.Id;
        Console.WriteLine($"touchy {t.Count} {sum} hash {hash} equals {equals}");
        // a value that is a struct with references, and one that is a number: nothing to hand over, counted as before
        var p = new Dictionary<string, Pair>();
        for (int i = 0; i < 60; i++) p["p" + i] = new Pair { A = new Item(9500 + i), N = i, S = "s" + i };
        for (int i = 0; i < 60; i += 4) p["p" + i] = new Pair { A = Make(9600 + i), N = -i };
        int dup = 0;
        for (int i = 0; i < 10; i++) { try { p.Add("p" + i, new Pair { A = new Item(9700 + i) }); } catch (ArgumentException) { dup++; } }
        p.TryAdd("p0", new Pair { A = new Item(9800) }); p.TryAdd("new", new Pair { A = new Item(9801), S = "n" });
        var n = new Dictionary<int, int>();
        for (int i = 0; i < 50; i++) { n[i] = i * 3; n.TryAdd(i, 7); }
        sum = 0;
        foreach (var kv in p) sum += kv.Value.A!.Id + kv.Value.N + (kv.Value.S?.Length ?? 0);
        foreach (var kv in n) sum += kv.Value;
        Console.WriteLine($"structs {p.Count} {n.Count} {sum} threw {dup}");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Mini()
    {
        var m = new MiniMap();
        bool ok = true;
        for (int i = 0; i < 8; i++) ok &= m.TryInsert(i, new Item(10000 + i), 0);
        Console.WriteLine($"mini {ok} {m.Second(0)} {m.Second(3)} {m.Second(7)}");
        for (int i = 0; i < 8; i++) m.DropFirst(i);
        Console.WriteLine($"mini after the first are dropped {m.Second(0)} {m.Second(3)} {m.Second(7)} {Item.Left()}");
        GC.KeepAlive(m);
    }

    [UnmanagedCallersOnly(EntryPoint = "main")] static void Main()
    {
        Ints(); Console.WriteLine("after ints: " + Item.Left());
        Keys(); Console.WriteLine("after keys: " + Item.Left());
        Mini(); Console.WriteLine("after mini: " + Item.Left());
    }
}
