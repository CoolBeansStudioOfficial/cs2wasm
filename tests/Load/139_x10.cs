// Threads storing into the same slots of shared objects of a cycle-capable type, unsynchronized (as Roslyn's caches
// do: racy stores, which .NET keeps memory-safe): a shared array of nodes and a shared node's fields, each thread with
// nodes of its own that refer to each other. Whatever the interleaving, no object is freed while in use and none is
// left once all is let go (counted by finalizers).
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

sealed class Node
{
    public static int Live;
    public Node? Next, Other;
    public int Id;
    public Node(int id) { Id = id; Interlocked.Increment(ref Live); }
    ~Node() { Interlocked.Decrement(ref Live); }
}

struct Entry { public int Hash; public Node? Node; }

static class Program
{
    static Node?[] s_slots = new Node?[8];
    // (a cache as Roslyn's: entries of a hash and a node, written by any thread)
    static Entry[] s_cache = new Entry[8];
    static Node s_hub = new Node(-1);

    static long Work(int t)
    {
        long sum = 0;
        var mine = new Node(t * 1000);
        for (int i = 0; i < 200000; i++)
        {
            var n = new Node(t * 1000 + i % 997) { Next = mine };
            mine.Other = n;
            s_slots[(i + t) & 7] = n;
            s_hub.Next = n;
            s_hub.Other = mine;
            s_cache[(i + 3 * t) & 7] = new Entry { Hash = i, Node = n };
            var e = s_cache[(i * 5 + t) & 7];
            if (e.Node != null) sum += e.Node.Id & 1;
            var seen = s_slots[(i * 3 + t) & 7];
            if (seen != null) sum += seen.Id & 1;
            var h = s_hub.Next;
            if (h != null) sum += h.Next != null ? 1 : 0;
        }
        return sum >= 0 ? 1 : 0;
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        var tasks = new Task<long>[16];
        for (int t = 0; t < tasks.Length; t++) { int k = t; tasks[t] = Task.Run(() => Work(k)); }
        long ok = 0;
        foreach (var task in tasks) ok += task.Result;
        for (int i = 0; i < s_slots.Length; i++) { s_slots[i] = null; s_cache[i] = default; }
        s_hub.Next = null; s_hub.Other = null;
        for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        // (the hub itself stays: a static holds it)
        Console.WriteLine(ok + " " + Node.Live);
    }
}
