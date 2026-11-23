// A garbage structure whose members have two tops (test 127's tasks, made certain): tasks' results are kept in the
// array that Task.WhenAll returns, a node refers to that array, and each result refers back to the node. A result's
// owner (its task) goes while the one container noted to own it next is gone too, so the array that still holds it
// does not own it: the result is a top of its own. The node's tree and the result's each refer into the other, and
// each tried alone fails by the other's reference; a pending TaskCompletionSource keeps the group alive meanwhile,
// so that it is not freed whole. What .NET frees must be freed (finalizers count what lives).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

class Tracked
{
    public static readonly List<string> Kinds = new();
    public static readonly int[] Live = new int[32];
    readonly int _kind;
    public Tracked(string kind)
    {
        // (made on pool threads in .NET: the list of kinds under a lock)
        int k;
        lock (Kinds) { k = Kinds.IndexOf(kind); if (k < 0) { k = Kinds.Count; Kinds.Add(kind); } }
        _kind = k;
        System.Threading.Interlocked.Increment(ref Live[k]);
    }
    ~Tracked() { System.Threading.Interlocked.Decrement(ref Live[_kind]); }
    public static void Report(string when)
    {
        for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        // (the pool thread that completed the last async method may not be out of it yet, and its state machine holds
        // `parent` until it is, in .NET too: test 127's note. Until nothing is left, for a bounded time)
        for (int waited = 0; waited < 5000 && Live.Sum() > 0; waited++) { System.Threading.Thread.Sleep(1); GC.Collect(); GC.WaitForPendingFinalizers(); }
        Console.WriteLine(when + ": " + string.Join(", ", Kinds.Select((k, i) => (k, i)).OrderBy(x => x.k, StringComparer.Ordinal).Select(x => x.k + " " + Live[x.i])));
    }
}

class Node : Tracked
{
    public Node Parent; public List<Node> Children = new(); public Node[] Slots = new Node[2]; public object Tag;
    public Node(string kind) : base(kind) { }
}

static class P
{
    static async Task<Node> MakeAsync(Node parent) { await Task.Yield(); var n = new Node("result") { Parent = parent }; await Task.Delay(1); return n; }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Results()
    {
        var root = new Node("root");
        var all = Task.WhenAll(Enumerable.Range(0, 4).Select(_ => MakeAsync(root))).GetAwaiter().GetResult();
        root.Tag = all;
        var waiter = new Node("pending"); waiter.Tag = new TaskCompletionSource<int>();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Rounds()
    {
        for (int round = 0; round < 3; round++)
        {
            var root = new Node("rounds");
            var all = Task.WhenAll(Enumerable.Range(0, 3).Select(_ => MakeAsync(root))).GetAwaiter().GetResult();
            root.Tag = all;
            root.Children.Add(all[0]);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        Results();
        Tracked.Report("results");
        Rounds();
        Tracked.Report("rounds");
    }
}
