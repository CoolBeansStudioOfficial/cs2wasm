// Threads that read references while others overwrite them, with no synchronization (section 4.5: racy code stays
// memory-safe and gives some outcome .NET allows). An object is destroyed the moment its last reference goes, so a
// reader must never be left holding one it has not counted: every value read here must still be the object it was.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

sealed class Node
{
    public readonly int V;
    public readonly string S;
    public Node? Next;
    public Node(int v) { V = v; S = "n" + v; }
}

struct Pair
{
    public Node? A;
    public string? B;
}

sealed class Holder
{
    public Node Current = new Node(0);
    public Pair P;
    public Node?[] Slots = new Node?[8];
    public object? Boxed;
}

static class Program
{
    static Node s_node = new Node(0);
    static readonly Holder s_holder = new Holder();
    static Node? s_swapped;
    static readonly string[] s_tags = { "p0", "p1", "p2", "p3" };
    static int s_bad, s_stop;

    static bool Good(Node? n) => n == null || n.S.Length >= 2 && n.S[0] == 'n' && n.V >= 0 && n.S == "n" + n.V;

    static void Writer(object? arg)
    {
        int seed = (int)arg!;
        for (int i = 1; i <= 30000; i++)
        {
            int v = seed * 100000 + i;
            s_node = new Node(v);
            s_holder.Current = new Node(v) { Next = new Node(v + 1) };
            s_holder.Slots[i & 7] = (i % 5 == 0) ? null : new Node(v);
            s_holder.P = new Pair { A = new Node(v), B = s_tags[i & 3] + i };
            s_holder.Boxed = (i & 1) == 0 ? new Node(v) : "s" + i;
            Interlocked.Exchange(ref s_swapped, new Node(v));
            if (i % 1000 == 0) Array.Clear(s_holder.Slots, 0, 4);
        }
    }

    static void Reader()
    {
        var copy = new Node?[8];
        var list = new List<Node?>();
        int bad = 0;
        while (Volatile.Read(ref s_stop) == 0)
        {
            if (!Good(s_node)) bad++;
            var c = s_holder.Current;
            if (!Good(c) || !Good(c.Next)) bad++;
            for (int k = 0; k < 8; k++) if (!Good(s_holder.Slots[k])) bad++;
            var p = s_holder.P;
            if (!Good(p.A)) bad++;
            if (p.B != null && p.B[0] != 'p') bad++;
            object? b = s_holder.Boxed;
            if (b is Node bn) { if (!Good(bn)) bad++; }
            else if (b is string bs && bs[0] != 's') bad++;
            Array.Copy(s_holder.Slots, copy, 8);
            foreach (var n in copy) if (!Good(n)) bad++;
            var clone = (Node?[])s_holder.Slots.Clone();
            foreach (var n in clone) if (!Good(n)) bad++;
            list.Clear();
            list.AddRange(s_holder.Slots);
            foreach (var n in list) if (!Good(n)) bad++;
            var mine = new Node(-1 + 1);
            var was = Interlocked.CompareExchange(ref s_swapped, mine, null);
            if (!Good(was)) bad++;
            if (!Good(Volatile.Read(ref s_swapped))) bad++;
        }
        Interlocked.Add(ref s_bad, bad);
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        var readers = new Thread[3];
        for (int i = 0; i < readers.Length; i++) { readers[i] = new Thread(Reader); readers[i].Start(); }
        var writers = new Thread[2];
        for (int i = 0; i < writers.Length; i++) { writers[i] = new Thread(Writer); writers[i].Start(i + 1); }
        foreach (var w in writers) w.Join();
        Volatile.Write(ref s_stop, 1);
        foreach (var r in readers) r.Join();
        Console.WriteLine("bad " + s_bad);
        Console.WriteLine(Good(s_node) && Good(s_holder.Current) ? "final good" : "final bad");
    }
}
