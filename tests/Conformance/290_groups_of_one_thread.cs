// Threads (section 4.5): groups of cycle-capable objects that are each one thread's. Each worker builds trees of
// Nodes of its own (parent links, child lists, a spare array), replaces subtrees, moves leaves, walks and drops
// them: no other thread can reach a Node, so their groups take no lock (Arenas.LocalScc, GroupLocks.cs LocalStripe)
// and go the single-thread paths. Every node's spare array starts as one array with no element, which every
// worker's nodes refer to, handed over by the threads' closure: a program with threads pins such an array as it
// is made (Rt.AllocArray), so it joins no group.
// The Hubs are every thread's: workers link and unlink them, under a lock, into one the main thread made; their
// groups are locked. Whatever the interleaving, the sums are the same.
// With CS2WASM_SHARE_CHECK=1, a group's lock left out for an object another thread made stops the program:
// CS2WASM_EXP_NO_EMPTY_PIN=1 (the empty array joins a worker's group, and another worker's store finds it there)
// and CS2WASM_EXP_ALL_GROUPS_LOCAL=1 (the Hubs too taken for one thread's) do (tools/thread-escape-breaks.sh).
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

sealed class Node
{
    public int V;
    public Node? Parent;
    public List<Node> Kids = new();
    public Node[] Spare = null!;
}

sealed class Hub
{
    public int V;
    public Hub? Parent;
    public List<Hub> Links = new();
}

static class Program
{
    static Hub s_hub = new Hub { V = -1 };
    static readonly object s_gate = new();

    static Node Build(Node? parent, int depth, ref int ids, Node[] none)
    {
        var n = new Node { V = ids++, Parent = parent, Spare = none };
        if (depth > 0) for (int i = 0; i < 4; i++) n.Kids.Add(Build(n, depth - 1, ref ids, none));
        if ((n.V & 7) == 3 && n.Kids.Count > 0) n.Spare = new Node[] { n.Kids[0] };
        return n;
    }

    static long Walk(Node n)
    {
        long s = n.V + n.Spare.Length * 7 + (n.Parent != null ? 1 : 0);
        foreach (var k in n.Kids) s += Walk(k);
        return s;
    }

    static long Work(int t, Node[] none)
    {
        long sum = 0;
        int ids = t * 1000000;
        Node? root = null;
        for (int i = 0; i < 400; i++)
        {
            if (i % 100 == 0) root = Build(null, 4, ref ids, none);
            // (a subtree replaced, a leaf moved to another parent, the spare array changed and given back)
            var a = root!.Kids[i % 4];
            a.Kids[(i / 4) % 4] = Build(a, 2, ref ids, none);
            var from = root.Kids[(i + 1) % 4].Kids[i % 4];
            if (from.Kids.Count > 0)
            {
                var leaf = from.Kids[from.Kids.Count - 1];
                from.Kids.RemoveAt(from.Kids.Count - 1);
                var to = root.Kids[(i + 2) % 4];
                leaf.Parent = to;
                to.Kids.Add(leaf);
            }
            a.Spare = (i & 1) == 0 ? new Node[] { a, root } : none;
            sum += Walk(a) & 0xFFFF;
            if (i % 10 == 0)
            {
                // (the Hubs, every thread's: linked and unlinked under the lock)
                lock (s_gate)
                {
                    var h = new Hub { V = t * 1000 + i, Parent = s_hub };
                    s_hub.Links.Add(h);
                    h.Links.Add(new Hub { V = 1, Parent = h });
                    if (s_hub.Links.Count > 6)
                    {
                        var gone = s_hub.Links[0];
                        s_hub.Links.RemoveAt(0);
                        sum += gone.Links.Count;
                    }
                }
            }
        }
        root = null;
        return sum;
    }

    static void Run()
    {
        var sums = new long[4];
        // (one array with no element, which every worker's nodes refer to: handed over by the lambdas' closure)
        var none = new Node[0];
        var threads = new Thread[4];
        for (int t = 0; t < threads.Length; t++)
        {
            int k = t;
            threads[t] = new Thread(() => sums[k] = Work(k + 1, none));
            threads[t].Start();
        }
        long own = Work(0, none);
        foreach (var th in threads) th.Join();
        long total = own;
        for (int i = 0; i < sums.Length; i++) total += sums[i];
        int links = s_hub.Links.Count, parents = 0;
        foreach (var h in s_hub.Links) if (h.Parent == s_hub && h.Links[0].Parent == h) parents++;
        Console.WriteLine(total + " " + links + " " + parents + " " + none.Length);
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        Run();
        Console.WriteLine("done");
    }
}
