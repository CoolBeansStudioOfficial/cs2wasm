using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

static class Program
{
    sealed class Node { public string Name; public Node(string n) { Name = n; } }
    sealed class Noisy { public string Tag; ~Noisy() { Console.WriteLine("finalized " + Tag); } }
    sealed class Tag { public string Value; public Tag(string v) { Value = v; } }

    static Node s_keep;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static WeakReference<Node> MakeWeak(string name, bool keep)
    {
        var n = new Node(name);
        if (keep) s_keep = n;
        return new WeakReference<Node>(n);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static WeakReference MakeOld() => new WeakReference(new Node("old"));

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void MakeNoisy(string tag, bool suppress)
    {
        var x = new Noisy { Tag = tag };
        if (suppress) GC.SuppressFinalize(x);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Attach(ConditionalWeakTable<Node, Tag> table, Node key, string v) => table.Add(key, new Tag(v));

    static void Collect() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        var gone = MakeWeak("gone", false);
        var kept = MakeWeak("kept", true);
        Collect();
        Console.WriteLine(gone.TryGetTarget(out var g) + " " + (g == null));
        Console.WriteLine(kept.TryGetTarget(out var k) + " " + k.Name);
        var old = MakeOld();
        Collect();
        Console.WriteLine(old.IsAlive + " " + (old.Target == null));
        old.Target = s_keep;
        Console.WriteLine(old.IsAlive + " " + ((Node)old.Target).Name);
        kept.SetTarget(null);
        Console.WriteLine(kept.TryGetTarget(out _));

        MakeNoisy("a", false);
        Collect();
        MakeNoisy("b", true);
        Collect();
        Console.WriteLine("after b");

        var table = new ConditionalWeakTable<Node, Tag>();
        var key = new Node("key");
        Attach(table, key, "attached");
        Console.WriteLine(table.TryGetValue(key, out var t) + " " + t.Value);
        Console.WriteLine(table.TryGetValue(new Node("other"), out _));
        try { table.Add(key, new Tag("dup")); } catch (ArgumentException) { Console.WriteLine("duplicate"); }
        Console.WriteLine(table.GetValue(s_keep, n => new Tag("made for " + n.Name)).Value);
        Console.WriteLine(table.Remove(key) + " " + table.TryGetValue(key, out _));
        table.AddOrUpdate(key, new Tag("v2"));
        Console.WriteLine(table.TryGetValue(key, out var t2) + " " + t2.Value);
        GC.KeepAlive(key);
    }
}
