// A subtree stored over another, in a program that assigns a node's list of children somewhere (the line in Main,
// never run): .NET frees the old subtree at each store. Until round 7 cs2wasm kept every one of them until the whole
// tree went (`built 121 replaced 161 again 201 gone 0`; `121 121 121 0` now, and with CS2WASM_NO_WAITING_TOP=1 the
// old figures). With a second place that stores the field, the list is not borrowed across
// `e.Kids.Add(Tree(e, ..))`: P.Tree counts it for the length of the call, so the list waits for its owner as the
// node does when the first child joins, and Rt.LinkWaiting took (child, node) before (node, list): the child became
// the node's owner and the list the top of the structure (Rt.WaitingTop; test 199).
//   cs2wasmc tools/tree-assign.cs -o ta.wasm   then   node host/run.mjs ta.wasm
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

sealed class Tag { ~Tag() { Node.Live--; } }
sealed class Node
{
    public static int Live;
    public Node Up; public List<Node> Kids = new(); public Tag Tag; public int Id;
    public Node(int id) { Id = id; Live++; Tag = new Tag(); }
}

static class P
{
    static Node s_tree;
    static int s_ids;
    static Node Tree(Node up, int depth, int fan)
    {
        var e = new Node(++s_ids) { Up = up };
        if (depth > 0) for (int i = 0; i < fan; i++) e.Kids.Add(Tree(e, depth - 1, fan));
        return e;
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Build() { s_tree = Tree(null, 4, 3); }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Replace() { s_tree.Kids[0] = Tree(s_tree, 3, 3); }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Drop() { s_tree = null; }
    static int Left() { for (int i = 0; i < 4; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); } return Node.Live; }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        // (the line: take it out and every replaced subtree goes at its store)
        if (s_ids < 0) s_tree.Kids = new List<Node>();
        Build(); int a = Left();
        Replace(); int b = Left();
        Replace(); int c = Left();
        Drop(); int d = Left();
        Console.WriteLine("built " + a + " replaced " + b + " again " + c + " gone " + d);
    }
}
