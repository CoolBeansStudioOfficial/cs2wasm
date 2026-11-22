// A subtree stored over another, in a program that assigns a node's list of children somewhere (the line in Main,
// never run): .NET frees the old subtree at each store; cs2wasm keeps every one of them until the whole tree goes
// (`built 121 replaced 161 again 201 gone 0`; without the line `121 121 121 0`). Found by test 198's first version
// (AUDIT A3.5, "The build, round 6"), on main 1f7a807 too. With a second place that stores the field, the list is
// no longer borrowed across `e.Kids.Add(Tree(e, ..))`: P.Tree counts it for the length of the call (one more
// rt.retain and its release, by CS2WASM_DUMP=P.Tree), and a list with up-references below it that is referred to
// from elsewhere than its owner holds its owner (UpdateHold); what those holds leave behind is not found when the
// subtree is let go. Not followed further here.
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
