// A ConditionalWeakTable does not keep its keys, and a value goes when its key does while the table lives (.NET:
// the entry is an ephemeron). Keys and values count themselves in finalizers. Keys dropped one by one and all at
// once; a value shared by two keys; a key in two tables; entries removed, replaced and cleared; the table dropped
// before its keys; the table grown past its first size with dead and living keys mixed; a DependentHandle by
// itself. Everything is made in frames that have returned before the counts are read (.NET's own temporaries keep
// objects otherwise). Not here, a difference that is left (docs/AUDIT.md): a value that holds its own key, which
// .NET collects and cs2wasm keeps while the table has the entry.
using System;
using System.Collections.Generic;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

sealed class Key
{
    public static int Live;
    public int Id;
    public Key(int id) { Id = id; Live++; }
    ~Key() { Live--; }
}

sealed class Value
{
    public static int Live;
    public int Id; public object? Ref;
    public Value(int id) { Id = id; Live++; }
    ~Value() { Live--; }
}

static class Program
{
    static void Collect() { for (int i = 0; i < 4; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); } }
    static string Counts() { Collect(); return $"keys {Key.Live} values {Value.Live}"; }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static List<Key> Fill(ConditionalWeakTable<Key, Value> table, int n, int from)
    {
        var keys = new List<Key>();
        for (int i = 0; i < n; i++) { var k = new Key(from + i); keys.Add(k); table.Add(k, new Value(from + i)); }
        return keys;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int Sum(ConditionalWeakTable<Key, Value> table, List<Key> keys)
    {
        int t = 0;
        foreach (var k in keys) if (table.TryGetValue(k, out var v)) t += v.Id;
        return t;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void DropHalf(List<Key> keys) { for (int i = keys.Count - 1; i >= 0; i -= 2) keys.RemoveAt(i); }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Basics(ConditionalWeakTable<Key, Value> table)
    {
        var keys = Fill(table, 25, 0);
        Console.WriteLine($"filled {Sum(table, keys)} {Counts()}");
        DropHalf(keys);
        Console.WriteLine($"half {Sum(table, keys)} {Counts()}");
        keys.Clear();
        Console.WriteLine($"none {Counts()}");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Shapes(ConditionalWeakTable<Key, Value> table)
    {
        var other = new ConditionalWeakTable<Key, Value>();
        var keys = MakeShapes(table, other);
        Console.WriteLine($"shapes {Counts()}");
        keys[0] = null!;
        Console.WriteLine($"one of two keys {Counts()} {IdOf(table, keys[1])}");
        keys[1] = null!;
        Console.WriteLine($"both {Counts()}");
        Console.WriteLine($"two tables {IdOf(table, keys[2])} {IdOf(other, keys[2])}");
        keys[2] = null!;
        Console.WriteLine($"two tables, key gone {Counts()}");
        GC.KeepAlive(other);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static Key[] MakeShapes(ConditionalWeakTable<Key, Value> table, ConditionalWeakTable<Key, Value> other)
    {
        // (one value under two keys)
        var k2 = new Key(101); var k3 = new Key(102); var shared = new Value(101); table.Add(k2, shared); table.Add(k3, shared);
        // (a key in two tables)
        var k4 = new Key(103); table.Add(k4, new Value(103)); other.Add(k4, new Value(104));
        return new[] { k2, k3, k4 };
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int IdOf(ConditionalWeakTable<Key, Value> table, Key key) => table.TryGetValue(key, out var v) ? v.Id : -1;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Replace(ConditionalWeakTable<Key, Value> table, Key remove, Key update)
    {
        table.Remove(remove);
        table.AddOrUpdate(update, new Value(999));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int Enumerate(ConditionalWeakTable<Key, Value> table)
    {
        int seen = 0; foreach (var kv in table) seen += kv.Value.Id == kv.Key.Id ? 1 : 1000;
        return seen;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Handles()
    {
        var h = Handle(out var key);
        Console.WriteLine($"handle {h.IsAllocated} {TargetId(h)} {DependentId(h)} {Counts()}");
        Console.WriteLine($"pair {PairIds(h)}");
        SetDependent(ref h, 3001);
        Console.WriteLine($"dependent set {DependentId(h)} {Counts()}");
        key = null!;
        Console.WriteLine($"handle's key gone {Counts()} {h.IsAllocated} {TargetId(h)} {DependentId(h)}");
        h.Dispose();
        Console.WriteLine($"disposed {h.IsAllocated} {Counts()}");
        var h2 = Handle(out key);
        h2.Dispose();
        Console.WriteLine($"disposed before its key {h2.IsAllocated} {Counts()}");
        key = null!;
        Console.WriteLine($"and the key {Counts()}");
    }

    [MethodImpl(MethodImplOptions.NoInlining)] static int TargetId(DependentHandle h) => h.Target is Key k ? k.Id : -1;
    [MethodImpl(MethodImplOptions.NoInlining)] static int DependentId(DependentHandle h) => h.Dependent is Value v ? v.Id : -1;
    [MethodImpl(MethodImplOptions.NoInlining)] static string PairIds(DependentHandle h) { var (t, d) = h.TargetAndDependent; return ((Key)t!).Id + " " + ((Value)d!).Id; }
    [MethodImpl(MethodImplOptions.NoInlining)] static void SetDependent(ref DependentHandle h, int id) => h.Dependent = new Value(id);

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Changes(ConditionalWeakTable<Key, Value> table)
    {
        s_keys = Fill(table, 10, 200);
        ReplaceFirstTwo(table);
        Console.WriteLine($"removed and replaced {Counts()} {Sum(table, s_keys)}");
        table.Clear();
        Console.WriteLine($"cleared {Counts()} {Sum(table, s_keys)}");
        s_again = Fill(table, 300, 300);
        DropHalf(s_again);
        More(table);
        Console.WriteLine($"grown {Counts()} {Sum(table, s_again)}");
        Console.WriteLine($"enumerated {Enumerate(table)}");
    }

    static List<Key> s_keys = null!, s_again = null!;
    [MethodImpl(MethodImplOptions.NoInlining)] static void ReplaceFirstTwo(ConditionalWeakTable<Key, Value> table) => Replace(table, s_keys[0], s_keys[1]);
    [MethodImpl(MethodImplOptions.NoInlining)] static void More(ConditionalWeakTable<Key, Value> table) => s_again.AddRange(Fill(table, 300, 1000));

    [MethodImpl(MethodImplOptions.NoInlining)]
    static List<Key> TableFirst()
    {
        var table = new ConditionalWeakTable<Key, Value>();
        var keys = Fill(table, 20, 2000);
        Console.WriteLine($"own table {Sum(table, keys)}");
        return keys;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static DependentHandle Handle(out Key key)
    {
        key = new Key(3000);
        return new DependentHandle(key, new Value(3000));
    }

    [UnmanagedCallersOnly(EntryPoint = "main")] static void Main()
    {
        var table = new ConditionalWeakTable<Key, Value>();
        Basics(table);
        Shapes(table);
        Changes(table);
        s_again = null!; s_keys = null!;
        Console.WriteLine($"all gone {Counts()}");
        var left = TableFirst();
        Console.WriteLine($"table gone, keys kept {left.Count} {Counts()}");
        left.Clear();
        Console.WriteLine($"keys gone {Counts()}");
        Handles();
        GC.KeepAlive(table);
    }
}
