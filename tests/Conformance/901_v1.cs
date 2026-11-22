using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

sealed class Node
{
    public static int Live;
    public static int[] LiveBy = new int[64];
    public static int[] MineBy = new int[64];
    public Node? Next, Other;
    public int Id;
    public int T; public bool Mine;
    public Node(int id, int t, bool mine) { Id = id; T = t; Mine = mine; Interlocked.Increment(ref Live); if (t >= 0) { Interlocked.Increment(ref LiveBy[t]); if (mine) Interlocked.Increment(ref MineBy[t]); } }
    ~Node() { Interlocked.Decrement(ref Live); if (T >= 0) { Interlocked.Decrement(ref LiveBy[T]); if (Mine) Interlocked.Decrement(ref MineBy[T]); } }
}

struct Entry { public int Hash; public Node? Node; }

static class Program
{
    static Node?[] s_slots = new Node?[8];
    static Entry[] s_cache = new Entry[8];
    static Node s_hub = new Node(-1, -1, false);
    const int Rounds = 1, Inner = 20000;

    static long Work(int t)
    {
        long sum = 0;
        for (int r = 0; r < Rounds; r++)
        {
        var mine = new Node(t * 1000, t, true);
        for (int i = 0; i < Inner; i++)
        {
            var n = new Node(t * 1000 + i % 997, t, false) { Next = mine };
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
        string s = ok + " " + Node.Live;
        for (int t = 0; t < 16; t++) if (Node.LiveBy[t] != 0) s += " t" + t + ":" + Node.LiveBy[t] + "/" + Node.MineBy[t];
        Console.WriteLine(s);
    }
}
