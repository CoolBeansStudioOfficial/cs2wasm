// A member of a living group that refers back to itself, or to a member above it, through an array's element or
// through a field past the sixteenth word, goes when it is let go, as under .NET (it stayed until its whole group
// went). A tree that lives on in a static while such nodes are cut from it.
// known-gap: a member cut from a living group that an array element or a field past the sixteenth word under it refers back to stays until the group goes (docs/AUDIT.md, known differences 2026-11-22)
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

struct Wide { public long A, B, C, D, E, F, G, H; public Node? Back; }

sealed class Node
{
    public static int Live;
    public int Id;
    public Node? Parent;
    public List<Node> Kids = new();
    public Node?[]? Raw;
    public long P0, P1, P2, P3, P4, P5, P6, P7, P8, P9;
    public Node? Far;
    public Wide W;
    public Node(int id) { Id = id; Live++; }
    ~Node() { Live--; }
}

static class Program
{
    static Node s_root = new Node(0);
    static int s_next = 1;
    static void Collect() { for (int i = 0; i < 4; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); } }
    static string Counts() { Collect(); return $"nodes {Node.Live}"; }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static Node Add(Node parent)
    {
        var n = new Node(s_next++) { Parent = parent };
        parent.Kids.Add(n);
        return n;
    }

    // kind 0: n.Raw[6] = n; 1: n.W.Back = n; 2: n.Far = n; 3: a child's Raw refers to n; 4: a child's W.Back to n;
    // 5: a child's Far to n; 6: Raw holds n twice and a child; 7: all of them
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Grow(int kind)
    {
        var n = Add(s_root);
        var kid = Add(n);
        if (kind is 0 or 7) { n.Raw = new Node?[8]; n.Raw[6] = n; }
        if (kind is 1 or 7) n.W.Back = n;
        if (kind is 2 or 7) n.Far = n;
        if (kind is 3 or 7) { kid.Raw = new Node?[3]; kid.Raw[1] = n; }
        if (kind is 4 or 7) kid.W.Back = n;
        if (kind is 5 or 7) kid.Far = n;
        if (kind is 6) { n.Raw = new Node?[4]; n.Raw[0] = n; n.Raw[3] = n; n.Raw[2] = kid; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Cut() { s_root.Kids.RemoveAt(s_root.Kids.Count - 1); }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void CutAll() { s_root.Kids.Clear(); }

    [UnmanagedCallersOnly(EntryPoint = "main")] static void Main()
    {
        Add(s_root);
        Console.WriteLine("start: " + Counts());
        for (int kind = 0; kind <= 7; kind++)
        {
            Grow(kind);
            Console.WriteLine($"kind {kind} grown: " + Counts());
            Cut();
            Console.WriteLine($"kind {kind} cut: " + Counts());
        }
        for (int kind = 0; kind <= 7; kind++) Grow(kind);
        Console.WriteLine("all grown: " + Counts());
        CutAll();
        Console.WriteLine("all cut: " + Counts());
        Console.WriteLine("root " + s_root.Id + " " + s_root.Kids.Count);
    }
}
