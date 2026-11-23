// Thread scaling on one shared group (docs/FINISH.md check 5): the shape of the self-hosted compiler's parse, where
// Roslyn's green nodes are one cycle group, shared through static caches. A cache of nodes that are all members of one
// arena; each work item reads nodes from the cache (a member's first and last outside reference), makes new nodes
// that refer to them (each joins the arena), and puts some into the cache (a store into a pinned array, its old value
// a member). Run with IL2WASM_PROCESSORS=n; prints the checksum and the milliseconds.
using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

sealed class Node
{
    public Node? Parent;
    public Node? A;
    public Node? B;
    public int V;
}

static class Program
{
    const int CacheSize = 4096;
    static readonly Node[] s_cache = new Node[CacheSize];

    static long Work(int seed)
    {
        long sum = 0;
        uint x = (uint)seed * 2654435761u + 1;
        Node? prev = null;
        for (int i = 0; i < 4000; i++)
        {
            x = x * 1664525u + 1013904223u;
            int idx = (int)(x >> 20) & (CacheSize - 1);
            var c = s_cache[idx];
            sum += c.V;
            // a new node over a cached one (it joins the cache's arena) and the one made before
            var n = new Node { A = c, B = prev, V = c.V };
            if ((i & 7) == 0) s_cache[idx] = n;
            // (a store into a member: the new node, and the chain behind it, join the arena; the old value leaves)
            else if ((i & 7) == 4) c.B = n;
            prev = (i & 15) == 0 ? null : n;
            if (n.A!.V != idx) sum--;
        }
        return sum;
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        // the cache's nodes: one arena (each refers to the root, which refers to one of them)
        var root = new Node { V = -1 };
        for (int i = 0; i < CacheSize; i++) s_cache[i] = new Node { Parent = root, V = i };
        root.A = s_cache[0];
        var sw = Stopwatch.StartNew();
        long total = Enumerable.Range(0, 2000).AsParallel().Select(Work).Sum();
        Console.WriteLine(total + " " + sw.ElapsedMilliseconds + " ms");
    }
}
