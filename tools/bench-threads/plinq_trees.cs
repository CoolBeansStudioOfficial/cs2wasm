// Thread scaling of cycle-capable objects (docs/FINISH.md, stream C): a PLINQ query that makes 3,000 trees of 200
// nodes, each with a parent reference (a cycle-capable type), and lets each go. Every node's stores are arena
// operations. Run with IL2WASM_PROCESSORS=n; prints the checksum and the milliseconds.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

sealed class TreeNode
{
    public TreeNode? Parent;
    public List<TreeNode> Children = new();
    public int Value;
}

static class Program
{
    static int Build(int seed)
    {
        var root = new TreeNode { Value = seed };
        var all = new List<TreeNode> { root };
        uint x = (uint)seed * 2654435761u + 1;
        for (int i = 1; i < 200; i++)
        {
            x = x * 1664525u + 1013904223u;
            var parent = all[(int)(x % (uint)all.Count)];
            var n = new TreeNode { Value = i, Parent = parent };
            parent.Children.Add(n);
            all.Add(n);
        }
        int sum = 0;
        foreach (var n in all) sum += n.Children.Count * n.Value;
        return sum;
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        var sw = Stopwatch.StartNew();
        long total = Enumerable.Range(0, 3000).AsParallel().Select(Build).Sum(v => (long)v);
        Console.WriteLine(total + " " + sw.ElapsedMilliseconds + " ms");
    }
}
