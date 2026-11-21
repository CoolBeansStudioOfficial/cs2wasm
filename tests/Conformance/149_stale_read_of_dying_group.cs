// Threads reading shared slots while others overwrite them, again and again, with nodes that refer to an anchor of
// their writer (which refers back to its latest node: one group). Each node let go dies with its finalizer run after
// the runtime lock is given back, while a reader may still hold the node's address from a read the slot has since
// changed (a read that counts takes no lock, and gives the count back once the place says it is out of date). Such a
// count once made the check after the finalizers keep the node for good, as if its finalizer had stored it, and with
// it the anchor it refers to (test 139's leak under load). Once all is let go, nothing is left.
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

sealed class Node
{
    public static int Live, Anchors;
    public Node? Next, Other;
    public int Id;
    public Node(int id) { Id = id; Interlocked.Increment(ref Live); if (id < 0) Interlocked.Increment(ref Anchors); }
    ~Node() { Interlocked.Decrement(ref Live); if (Id < 0) Interlocked.Decrement(ref Anchors); }
}

static class Program
{
    static Node?[] s_slots = new Node?[4];
    static volatile int s_stop;

    static void Write(int w)
    {
        var anchor = new Node(-1 - w);
        for (int i = 0; i < 20000; i++)
        {
            var n = new Node(i) { Next = anchor };
            anchor.Other = n;
            s_slots[(i + w) & 3] = n;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)] static int Use(Node p) => p.Id & 1;

    static long Read(int r)
    {
        long sum = 0;
        while (s_stop == 0)
            for (int k = 0; k < 4; k++)
            {
                var p = s_slots[(k + r) & 3];
                if (p != null) sum += Use(p);
            }
        return sum >= 0 ? 1 : 0;
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        var readers = new Task<long>[8];
        for (int r = 0; r < readers.Length; r++) { int k = r; readers[r] = Task.Run(() => Read(k)); }
        var writers = new Task[4];
        for (int w = 0; w < writers.Length; w++) { int k = w; writers[w] = Task.Run(() => Write(k)); }
        Task.WaitAll(writers);
        s_stop = 1;
        long ok = 0;
        foreach (var t in readers) ok += t.Result;
        for (int i = 0; i < s_slots.Length; i++) s_slots[i] = null;
        for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        Console.WriteLine(ok + " " + Node.Live + " " + Node.Anchors);
    }
}
