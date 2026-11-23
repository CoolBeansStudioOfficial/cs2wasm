// ConditionalWeakTable: its entries enumerated, cleared, replaced, made by a callback; values that hold other keys;
// keys in cycles and keys with finalizers; a table that is itself let go. What is alive after each step is what
// .NET says.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

sealed class Key
{
    public static int Live;
    public int Id; public Key? Other; public List<Key>? Kids;
    public Key(int id) { Id = id; Live++; }
    ~Key() { Live--; }
}

sealed class Val
{
    public static int Live;
    public int N; public Key? Holds; public Val? Next;
    public Val(int n) { N = n; Live++; }
    ~Val() { Live--; }
}

static class Program
{
    static ConditionalWeakTable<Key, Val> s_table = new();
    static List<Key> s_keep = new();
    static void Collect() { for (int i = 0; i < 6; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); } }
    static string Counts() { Collect(); return $"keys {Key.Live} values {Val.Live}"; }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static string Entries()
    {
        int n = 0; long sum = 0;
        foreach (var kv in s_table) { n++; sum += kv.Key.Id * 1000 + kv.Value.N; }
        return $"entries {n} sum {sum}";
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Fill(int from, int n, int keepEvery)
    {
        for (int i = from; i < from + n; i++)
        {
            var k = new Key(i);
            s_table.Add(k, new Val(i));
            if (i % keepEvery == 0) s_keep.Add(k);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Ways()
    {
        var a = new Key(1000); var b = new Key(1001); var c = new Key(1002); var d = new Key(1003);
        s_keep.Add(a); s_keep.Add(b); s_keep.Add(c);
        Console.WriteLine("try add " + s_table.TryAdd(a, new Val(1)) + " " + s_table.TryAdd(a, new Val(2)));
        s_table.AddOrUpdate(b, new Val(3)); s_table.AddOrUpdate(b, new Val(4));
        var made = s_table.GetValue(c, k => new Val(k.Id));
        var again = s_table.GetValue(c, k => new Val(-1));
        Console.WriteLine("get value " + made.N + " " + ReferenceEquals(made, again));
        try { s_table.Add(a, new Val(5)); Console.WriteLine("added twice"); } catch (ArgumentException) { Console.WriteLine("ArgumentException"); }
        Console.WriteLine("remove " + s_table.Remove(b) + " " + s_table.Remove(b) + " " + s_table.Remove(d));
        Console.WriteLine("try get " + s_table.TryGetValue(a, out var va) + " " + va!.N + " " + s_table.TryGetValue(b, out _) + " " + s_table.TryGetValue(d, out _));
        s_table.Add(d, new Val(6));
    }

    // values that hold the next key: a chain kept by its first key alone
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Chain(int n)
    {
        Key? next = null;
        for (int i = 0; i < n; i++)
        {
            var k = new Key(2000 + i);
            s_table.Add(k, new Val(2000 + i) { Holds = next });
            next = k;
        }
        s_keep.Add(next!);
    }

    // keys in a cycle with each other, and a key in a tree that lives on
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Cycles(int n)
    {
        for (int i = 0; i < n; i++)
        {
            var x = new Key(3000 + 2 * i); var y = new Key(3001 + 2 * i);
            x.Other = y; y.Other = x;
            s_table.Add(x, new Val(1)); s_table.Add(y, new Val(2) { Next = new Val(3) });
            if (i == 0) s_keep.Add(x);
        }
        var root = new Key(4000) { Kids = new List<Key>() };
        s_keep.Add(root);
        for (int i = 0; i < n; i++) { var kid = new Key(4001 + i) { Other = root }; root.Kids.Add(kid); s_table.Add(kid, new Val(4001 + i)); }
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void CutKids() { var root = s_keep.First(k => k.Id == 4000); root.Kids!.RemoveRange(1, root.Kids.Count - 1); }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Local()
    {
        // a table of the method's own, let go with keys that live on
        var t = new ConditionalWeakTable<Key, Val>();
        foreach (var k in s_keep) t.Add(k, new Val(-k.Id));
        int n = 0; foreach (var kv in t) n++;
        Console.WriteLine("local table " + n);
    }

    [UnmanagedCallersOnly(EntryPoint = "main")] static void Main()
    {
        Fill(0, 40, 4);
        Console.WriteLine("filled: " + Counts() + " " + Entries());
        Ways();
        Console.WriteLine("ways: " + Counts() + " " + Entries());
        Chain(10);
        Console.WriteLine("chain: " + Counts() + " " + Entries());
        Cycles(6);
        Console.WriteLine("cycles: " + Counts() + " " + Entries());
        CutKids();
        Console.WriteLine("kids cut: " + Counts() + " " + Entries());
        Local();
        Console.WriteLine("local: " + Counts() + " " + Entries());
        s_keep.RemoveAll(k => k.Id >= 2000 && k.Id < 3000);
        Console.WriteLine("chain let go: " + Counts() + " " + Entries());
        s_table.Clear();
        Console.WriteLine("cleared: " + Counts() + " " + Entries());
        Fill(5000, 20, 5);
        Console.WriteLine("filled again: " + Counts() + " " + Entries());
        s_table = new ConditionalWeakTable<Key, Val>();
        Console.WriteLine("table let go: " + Counts() + " " + Entries());
        s_keep.Clear();
        Console.WriteLine("keys let go: " + Counts());
    }
}
