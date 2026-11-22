// A node's list of children that code refers to while the node's first child is put into it. In a program that
// stores the field in a second place (the line in Main, never run) the list is not borrowed across
// `e.Kids.Add(Tree(e, ..))` but counted for the length of the call; the same when a local or a static holds the
// list. The list and the node then both wait for their owner as the first child joins (Rt.LinkWaiting), and the
// node must end up above its list and its child, not below the child with the list on top (Rt.WaitingTop,
// CS2WASM_NO_WAITING_TOP=1 for the pairs strictly in their order: every subtree stored over then stays until its
// tree goes, `replaced` below 161 and more for 121). Counted against .NET while the trees live: subtrees stored
// over, children taken out and moved, a node of a replaced part held meanwhile, nodes with finalizers that see
// their parent and children as they go.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

sealed class Tag { readonly int _k; public Tag(int k) { _k = k; Node.Live[k]++; } ~Tag() { System.Threading.Interlocked.Decrement(ref Node.Live[_k]); } }
sealed class Node
{
    public static readonly int[] Live = new int[4];
    public Node Up; public List<Node> Kids = new(); public Tag Tag; public int Id;
    public Node(int kind, int id) { Id = id; Tag = new Tag(kind); }
    public int Count() { int n = 1; foreach (var c in Kids) n += c.Count(); return n; }
    public bool Sound() { foreach (var c in Kids) if (c.Up != this || !c.Sound()) return false; return true; }
}

// (members with a finalizer of their own: another group of classes)
sealed class Fin
{
    public static int Ran; public static long Seen;
    public Fin Up; public List<Fin> Kids = new(); public int Id;
    public Fin(int id) { Id = id; }
    ~Fin() { Ran++; if (Up != null) Seen += Up.Id; foreach (var k in Kids) Seen += 3 * k.Id + (k.Up == this ? 1000 : 0); }
}

static class P
{
    static Node s_tree, s_local, s_static, s_keep;
    static List<Node> s_list;
    static Fin s_fin;
    static int s_ids;

    // the receiver counted across the call (the field has a second store in the program)
    static Node Tree(Node up, int depth, int fan)
    {
        var e = new Node(0, ++s_ids) { Up = up };
        if (depth > 0) for (int i = 0; i < fan; i++) e.Kids.Add(Tree(e, depth - 1, fan));
        return e;
    }
    // a local holds the list
    static Node ByLocal(Node up, int depth, int fan)
    {
        var e = new Node(1, ++s_ids) { Up = up };
        var kids = e.Kids;
        if (depth > 0) for (int i = 0; i < fan; i++) kids.Add(ByLocal(e, depth - 1, fan));
        return e;
    }
    // a static holds the list of the node being filled
    static Node ByStatic(Node up, int depth, int fan)
    {
        var e = new Node(2, ++s_ids) { Up = up };
        for (int i = 0; i < (depth > 0 ? fan : 0); i++) { s_list = e.Kids; var c = ByStatic(e, depth - 1, fan); s_list = e.Kids; s_list.Add(c); }
        s_list = null;
        return e;
    }
    static Fin FinTree(Fin up, int depth, int fan)
    {
        var e = new Fin(++s_ids) { Up = up };
        if (depth > 0) for (int i = 0; i < fan; i++) e.Kids.Add(FinTree(e, depth - 1, fan));
        return e;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Build() { s_tree = Tree(null, 4, 3); s_local = ByLocal(null, 4, 3); s_static = ByStatic(null, 4, 3); s_fin = FinTree(null, 3, 3); }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Replace(int round)
    {
        s_tree.Kids[round % 3] = Tree(s_tree, 3, 3);
        s_local.Kids[round % 3] = ByLocal(s_local, 3, 3);
        s_static.Kids[round % 3] = ByStatic(s_static, 3, 3);
        s_fin.Kids[round % 3] = FinTree(s_fin, 2, 3);
        var b = s_tree.Kids[(round + 1) % 3];
        b.Kids[round % 3] = ByLocal(b, 2, 2 + round % 3);
        b.Kids.Add(Tree(b, 1, 5));
        if (b.Kids.Count > 4) b.Kids.RemoveAt(1);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Move()
    {
        var from = s_local.Kids[1].Kids[0]; var to = s_local.Kids[2];
        var moved = from.Kids[from.Kids.Count - 1];
        from.Kids.RemoveAt(from.Kids.Count - 1);
        moved.Up = to; to.Kids.Add(moved);
        s_local.Kids[1].Kids[0] = ByLocal(s_local.Kids[1], 2, 3);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Keep() { var b = s_static.Kids[1]; s_keep = b.Kids[2].Kids[0]; s_static.Kids[1] = ByStatic(s_static, 2, 3); }
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Unkeep() { Console.WriteLine("kept " + s_keep.Up.Up.Count()); s_keep = null; }
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Drop() { s_tree = null; s_local = null; s_static = null; s_fin = null; }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Say(string when)
    {
        for (int i = 0; i < 4; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        string live = Node.Live[0] + " " + Node.Live[1] + " " + Node.Live[2];
        if (s_tree == null) { Console.WriteLine(when + ": " + live + " finalizers " + Fin.Ran + " saw " + Fin.Seen); return; }
        Console.WriteLine(when + ": " + live + " nodes " + s_tree.Count() + " " + s_local.Count() + " " + s_static.Count()
            + " sound " + (s_tree.Sound() && s_local.Sound() && s_static.Sound()) + " finalizers " + Fin.Ran + " saw " + Fin.Seen);
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        // (the second store of the field: with it in the program, never run, no list is borrowed across an Add)
        if (s_ids < 0) s_tree.Kids = new List<Node>();
        Build(); Say("built");
        for (int r = 0; r < 6; r++) { Replace(r); Say("replaced"); }
        Move(); Say("moved");
        Keep(); Say("kept");
        Unkeep(); Say("let go");
        Drop(); Say("gone");
    }
}
