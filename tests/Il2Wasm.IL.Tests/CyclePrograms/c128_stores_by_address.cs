// Ported from tests/Conformance/128_stores_by_address.cs (the old suite): the same program, for the cycle groups (docs/MEMORY-IR.md S2).
// live: 0
// References stored through a ref, a span, the library's own element moves and Interlocked into objects that refer
// to each other: every such store counts as the store through the object would, and nothing is left afterwards.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

class Node
{
    public static int Live;
    public string Name; public Node[] Kids = new Node[4]; public Node Parent; public List<Node> More = new();
    public Node(string n) { Name = n; Live++; }
    ~Node() { Live--; }
}

static class P
{
    static string Churn() { var l = new List<Node>(); for (int i = 0; i < 30; i++) l.Add(new Node("churn" + i)); return l[29].Name; }
    static void Set(ref Node slot, Node v) { slot = v; }
    static void Swap(ref Node a, ref Node b) { var t = a; a = b; b = t; }

    static void Run()
    {
        var root = new Node(new string('r', 12));
        for (int i = 0; i < 4; i++) { var k = new Node(new string('k', 13 + i)) { Parent = root }; root.Kids[i] = k; root.More.Add(k); }
        // stores through references, spans and the library's own element moves
        Array.Reverse(root.Kids);
        root.Kids.AsSpan().Reverse();
        Swap(ref root.Kids[0], ref root.Kids[3]);
        var span = root.Kids.AsSpan();
        var keep = span[1];
        span[1] = null; Set(ref root.Kids[2], null);
        Churn();
        Console.WriteLine(keep.Name.Length + " " + keep.Parent.Name.Length + " " + (root.Kids[0]?.Name.Length ?? -1) + " " + (root.Kids[3]?.Name.Length ?? -1));
        span[1] = keep; span.Fill(keep); span.Clear();
        Array.Sort(root.More.ToArray(), (a, b) => b.Name.Length - a.Name.Length);
        root.More.Sort((a, b) => b.Name.Length - a.Name.Length);
        root.More.Reverse(); root.More.RemoveAt(1); root.More.Insert(0, keep);
        CollectionsMarshal.AsSpan(root.More)[1] = keep;
        Churn();
        int sum = 0; foreach (var n in root.More) sum += n.Name.Length + n.Parent.Name.Length;
        Console.WriteLine(sum + " " + keep.Name.Length);
    }

    static void Main()
    {
        Run();
        for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        Console.WriteLine(Node.Live);
    }
}
