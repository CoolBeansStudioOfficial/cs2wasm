// Test 139's racing stores in many short rounds: what goes wrong where the threads start and end (a thread's last
// nodes let go while others still read and replace them in the shared slots) shows once in some thousand runs of
// test 139, which has one such moment a thread; here there are thousands. After each round all is let go and nothing
// but the hub may be left (counted by finalizers).
using System;
using System.Runtime.CompilerServices;
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
    static Entry[] s_cache = new Entry[8];
    static Node s_hub = new Node(-1);

    static long Work(int t, int n0)
    {
        long sum = 0;
        var mine = new Node(t * 1000);
        for (int i = 0; i < n0; i++)
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

    [MethodImpl(MethodImplOptions.NoInlining)]
    static long Round(int round)
    {
        var tasks = new Task<long>[8];
        for (int t = 0; t < tasks.Length; t++) { int k = t; tasks[t] = Task.Run(() => Work(k, 40 + (round + k) % 50)); }
        long ok = 0;
        foreach (var task in tasks) ok += task.Result;
        for (int i = 0; i < s_slots.Length; i++) { s_slots[i] = null; s_cache[i] = default; }
        s_hub.Next = null; s_hub.Other = null;
        return ok;
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        long ok = 0; int left = 0;
        for (int round = 0; round < 400; round++)
        {
            ok += Round(round);
            GC.Collect(); GC.WaitForPendingFinalizers();
            // (the hub itself stays: a static holds it)
            if (Volatile.Read(ref Node.Live) != 1) left++;
        }
        for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        Console.WriteLine(ok + " " + left + " " + Node.Live);
    }
}
