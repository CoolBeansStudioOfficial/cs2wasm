// Test 300 in a program with threads: a tree let go while code still holds members its list let go (by a store over
// them or by Remove), each referring back to the root and kept as well by an array the root holds, in a group a
// static keeps alive through an object in the root's list. Built and let go on the main thread, on another thread,
// and built on another thread and let go on the main one. What is alive while the code holds them and after is what
// .NET says.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

class Node
{
    public Node? Prev, Next; public List<Node> Kids = new(); public object? Tag; public int Id;
}

sealed class Fin : Node
{
    public static int Live;
    public Fin() { Interlocked.Increment(ref Live); }
    ~Fin() { Interlocked.Decrement(ref Live); }
}

sealed class Plain : Node { }

static class Program
{
    static Node? s_keep;
    static readonly List<WeakReference> s_weak = new();
    static void Collect() { for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); } }

    static string Alive()
    {
        Collect();
        int n = 0;
        lock (s_weak) foreach (var w in s_weak) if (w.IsAlive) n++;
        return $"weak {n} fin {Fin.Live}";
    }

    static Node Make(bool fin, int id)
    {
        Node x = fin ? new Fin() : new Plain();
        x.Id = id;
        lock (s_weak) s_weak.Add(new WeakReference(x));
        return x;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Build(bool fin, int how, int keepAt, int held, out Node? k1, out Node? k2)
    {
        var root = Make(fin, 0);
        var a1 = Make(fin, 1); a1.Prev = root;
        var a2 = held > 1 ? Make(fin, 2) : null; if (a2 != null) a2.Prev = root;
        var b = Make(fin, 3); b.Prev = root;
        root.Kids.Add(a1);
        if (a2 != null) root.Kids.Add(a2);
        root.Kids.Add(b);
        if (keepAt >= 0) root.Kids.Insert(Math.Min(keepAt, root.Kids.Count), s_keep!);
        root.Tag = a2 != null ? new Node[] { a1, a2 } : new Node[] { a1 };
        TakeOut(root, a1, b, how);
        if (a2 != null) TakeOut(root, a2, b, how);
        k1 = a1; k2 = a2;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void TakeOut(Node root, Node x, Node b, int how)
    {
        if (how == 0) root.Kids[root.Kids.IndexOf(x)] = b;
        else root.Kids.Remove(x);
    }

    sealed class Held { public Node? K1, K2; }

    // where: 0 all on the main thread, 1 all on another thread, 2 built on another thread and let go on this one
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Run(bool fin, int how, int keepAt, int held, int where)
    {
        var h = new Held();
        string during;
        if (where == 0) { Build(fin, how, keepAt, held, out h.K1, out h.K2); during = Alive(); }
        else
        {
            var t = new Thread(() => Build(fin, how, keepAt, held, out h.K1, out h.K2));
            t.Start(); t.Join();
            during = Alive();
            if (where == 1)
            {
                var u = new Thread(() => { h.K1 = null; h.K2 = null; });
                u.Start(); u.Join();
            }
        }
        h.K1 = null; h.K2 = null;
        Console.WriteLine($"{(fin ? "fin" : "plain")} how {how} keep {keepAt} held {held} where {where}: held {during}, after {Alive()}");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Setup()
    {
        var k = new Fin { Id = -1 };
        k.Kids.Add(k);
        s_keep = k;
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        Setup();
        foreach (bool fin in new[] { true, false })
            for (int how = 0; how < 2; how++)
                for (int keepAt = -1; keepAt <= 2; keepAt++)
                    for (int held = 1; held <= 2; held++)
                        for (int where = 0; where < 3; where++)
                        {
                            lock (s_weak) s_weak.Clear();
                            Run(fin, how, keepAt, held, where);
                        }
        s_keep = null;
        Console.WriteLine("end " + Alive());
    }
}
