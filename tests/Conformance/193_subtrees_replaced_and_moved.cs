// A tree that lives on while parts of it are replaced and moved: a subtree stored over another is destroyed at that
// store, member by member through its owners (Rt.ArenaDestroyPlain: the dying members' references read by their slot
// bits, their counts zero as their turn comes, their blocks back onto their pages; Rt.VisitDying where a member has a
// finalizer). What .NET frees must be freed while the tree is still there, and nothing before .NET frees it (a use
// after free shows in the checked build). The states those paths can meet: the parent set before the child is in its
// list and after (Rt.LinkWaiting for a store, `e.Kids.Add(c); c.Up = e;`); a child moved to another list, to the
// parent of the node it was taken from too (Rt.OnChainOwned), in both orders, then the node it came from replaced;
// a subtree replaced while a static still holds a node of it, and while another list holds one of its nodes, with and
// without a reference up; members with more than sixteen words of references (no slot bits for the last), arrays of
// references with holes and of structs with two references, with references back up through them (told when
// their group has gone: such a part may stay until then); a plain object with content that joins by
// a store while a local holds it; nodes with finalizers of their own, which see their parent and their children as
// they go; plain objects with finalizers held by the dying members, whose finalizers allocate, drop a subtree of the
// same tree and ask a weak reference to a member that is dying with them.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

class Tracked
{
    public static readonly List<string> Kinds = new();
    public static readonly int[] Live = new int[32];
    readonly int _kind;
    public Tracked(string kind)
    {
        int k = Kinds.IndexOf(kind); if (k < 0) { k = Kinds.Count; Kinds.Add(kind); }
        _kind = k;
        Live[k]++;
    }
    // (what a class does as it goes: called from the one finalizer)
    protected virtual void Last() { }
    ~Tracked() { Last(); System.Threading.Interlocked.Decrement(ref Live[_kind]); }
    public static void Report(string when)
    {
        for (int i = 0; i < 4; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        Console.WriteLine(when + ": " + string.Join(", ", Kinds.Select((k, i) => (k, i)).OrderBy(x => x.k, StringComparer.Ordinal).Select(x => x.k + " " + Live[x.i])));
    }
}

// (a node's tag: not of the group, with the finalizer)
class Tag : Tracked
{
    public Tag(string kind) : base(kind) { }
}

// a tag whose finalizer runs program code while the subtree that held it goes
class Busy : Tag
{
    public static Node Victim;
    public static WeakReference<Node> Weak;
    public static int Ran, Asked;
    public List<string> Scratch;
    public Busy(string kind) : base(kind) { }
    protected override void Last()
    {
        Ran++;
        // (allocations while the members of the subtree wait to be freed: none of them may get a member's memory)
        Scratch = new List<string>();
        for (int i = 0; i < 40; i++) Scratch.Add("scratch " + i);
        // (a subtree of the same tree replaced from inside the finalizer, once)
        var v = Victim;
        Victim = null;
        if (v != null && v.Kids.Count > 1) v.Kids[1] = new Node("late", 9) { Up = v };
        // (a weak reference to a member that is dying with this one's holder: whether it still answers is not .NET's
        // to say the same way, so nothing is printed of it; it must not break anything)
        if (Weak != null) { if (Weak.TryGetTarget(out var n)) Asked += n.Id >= 0 ? 1 : 0; else Asked++; }
    }
}

struct Pair { public Node A; public int N; public Node B; }

// more than sixteen words of references: the last fields have no slot bits
sealed class Wide
{
    public Node A0, A1, A2, A3, A4, A5, A6, A7, A8, A9, A10, A11, A12, A13, A14, A15, A16, A17, A18, Back;
    public Wide More;
    public Tag Tag;
    public Wide(string kind) { Tag = new Tag(kind); }
}

sealed class Node
{
    public Node Up, Next; public List<Node> Kids = new(); public Node[] Raw; public Pair[] Pairs; public Wide W;
    public object Any; public Tag Tag; public int Id;
    public Node(string kind, int id = 0, bool busy = false) { Tag = busy ? new Busy(kind) : new Tag(kind); Id = id; }
    public long Sum() { long s = Id; foreach (var c in Kids) if (c != null) s += c.Sum(); return s; }
    public int Count() { int n = 1; foreach (var c in Kids) if (c != null) n += c.Count(); return n; }
    public bool Sound() { foreach (var c in Kids) if (c == null || c.Up != this || !c.Sound()) return false; return true; }
}

// nodes with a finalizer of their own (another group of classes): each sees its parent and its children as it goes
sealed class Fin
{
    public static long Seen; public static int Ran;
    public Fin Up; public List<Fin> Kids = new(); public Fin[] Raw; public int Id;
    public Fin(int id) { Id = id; }
    ~Fin()
    {
        Ran++;
        if (Up != null) Seen += Up.Id;
        foreach (var k in Kids) if (k != null) Seen += 3 * k.Id + (k.Up == this ? 1000 : 0);
        if (Raw != null) foreach (var k in Raw) if (k != null) Seen += 7 * k.Id;
    }
    public long Sum() { long s = Id; foreach (var c in Kids) if (c != null) s += c.Sum(); return s; }
}

static class P
{
    static Node s_tree, s_after, s_keep, s_side, s_cyc;
    static Fin s_fin;
    static WeakReference<Node> s_weakGone, s_weakKept;
    static uint s_seed = 12345;
    static uint Next() { s_seed = s_seed * 1664525u + 1013904223u; return s_seed >> 8; }

    // the parent set before the child is in its list
    static Node Tree(string kind, Node up, int depth, int fan, bool busy = false)
    {
        var e = new Node(kind, depth, busy && depth == 1) { Up = up };
        if (depth > 0) for (int i = 0; i < fan; i++) e.Kids.Add(Tree(kind, e, depth - 1, fan, busy));
        return e;
    }

    // and after
    static Node After(string kind, int depth, int fan)
    {
        var e = new Node(kind, depth);
        if (depth > 0) for (int i = 0; i < fan; i++) { var c = After(kind, depth - 1, fan); e.Kids.Add(c); c.Up = e; }
        return e;
    }

    static Fin FinTree(Fin up, int depth, int fan)
    {
        var e = new Fin(depth) { Up = up };
        if (depth > 0) for (int i = 0; i < fan; i++) e.Kids.Add(FinTree(e, depth - 1, fan));
        return e;
    }

    static void Say(string what, Node tree) => Console.WriteLine(what + ": nodes " + tree.Count() + " sum " + tree.Sum() + " sound " + tree.Sound());

    // subtrees stored over subtrees, in both orders of the parent's store; lists that grow in between
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Replace()
    {
        s_tree = Tree("tree", null, 5, 3);
        s_after = After("after", 5, 3);
        for (int r = 0; r < 40; r++)
        {
            var b = s_tree.Kids[(int)(Next() % 3)].Kids[(int)(Next() % 3)];
            b.Kids[(int)(Next() % 3)] = Tree("tree", b, 3, 3);
            var a = s_after.Kids[(int)(Next() % 3)].Kids[(int)(Next() % 3)];
            int k = (int)(Next() % 3);
            if (r % 2 == 0) { var sub = After("after", 3, 3); a.Kids[k] = sub; sub.Up = a; }
            else { var sub = After("after", 3, 3); sub.Up = a; a.Kids[k] = sub; }
            if (r % 5 == 0) { b.Kids.Add(Tree("tree", b, 1, 5)); var c = After("after", 1, 5); a.Kids.Add(c); c.Up = a; }
            if (r % 7 == 0) { if (b.Kids.Count > 3) b.Kids.RemoveAt(b.Kids.Count - 1); if (a.Kids.Count > 3) a.Kids.RemoveAt(0); }
        }
        Say("replaced", s_tree);
        Say("replaced, parent after", s_after);
    }

    // a child taken out of its parent's list and put into another, the parent of the node it was taken from every
    // third time, in both orders; then the node it came from replaced
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Moves()
    {
        for (int r = 0; r < 90; r++)
        {
            var tree = r % 2 == 0 ? s_tree : s_after;
            int i = (int)(Next() % 3), j = (int)(Next() % 3);
            var from = tree.Kids[i].Kids[j];
            var c = from.Kids[(int)(Next() % 3)];
            if (c.Kids.Count == 0) continue;
            var moved = c.Kids[c.Kids.Count - 1];
            c.Kids.RemoveAt(c.Kids.Count - 1);
            var to = r % 3 == 0 ? from : r % 3 == 1 ? tree.Kids[(int)(Next() % 3)].Kids[(int)(Next() % 3)] : tree.Kids[(int)(Next() % 3)];
            if (r % 4 < 2) { moved.Up = to; to.Kids.Add(moved); }
            else { to.Kids.Add(moved); moved.Up = to; }
            if (r % 3 != 2) { int k = from.Kids.IndexOf(c); from.Kids[k] = Tree(r % 2 == 0 ? "tree" : "after", from, 2, 3); }
        }
        Say("moved", s_tree);
        Say("moved, parent after", s_after);
    }

    // a subtree replaced while a static holds a node of it: it stays, all of it (the node's parents, their children)
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Keep()
    {
        var b = s_tree.Kids[0].Kids[1];
        b.Kids[0] = Tree("held", b, 3, 3);
        s_keep = b.Kids[0].Kids[2].Kids[1];
        s_weakKept = new WeakReference<Node>(b.Kids[0].Kids[0]);
        b.Kids[0] = Tree("tree", b, 3, 3);
        // (and one that goes at its store)
        b.Kids[1] = Tree("gone", b, 2, 3);
        s_weakGone = new WeakReference<Node>(b.Kids[1].Kids[2]);
        b.Kids[1] = Tree("tree", b, 3, 3);
        Say("kept", s_tree);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void KeptStill()
    {
        Console.WriteLine("kept node's subtree " + s_keep.Up.Up.Count() + " parent in the tree " + (s_keep.Up.Up.Up == s_tree.Kids[0].Kids[1])
            + " weak " + s_weakKept.TryGetTarget(out _) + " " + s_weakGone.TryGetTarget(out _));
        s_keep = null;
    }

    // a subtree replaced while another list holds two of its nodes: one that refers up (its parents stay), one that
    // does not (it stays alone, with what is below it)
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Shared()
    {
        s_side = new Node("side");
        var b = s_tree.Kids[1].Kids[1];
        b.Kids[0] = Tree("up", b, 3, 3);
        b.Kids[1] = Tree("alone", b, 3, 3);
        s_side.Kids.Add(b.Kids[0].Kids[1].Kids[2]);
        var y = b.Kids[1].Kids[2];
        y.Up = null;
        s_side.Kids.Add(y);
        b.Kids[0] = Tree("tree", b, 3, 3);
        b.Kids[1] = Tree("tree", b, 3, 3);
        Console.WriteLine("shared: " + s_side.Kids[0].Up.Up.Count() + " " + s_side.Kids[1].Count() + " " + s_tree.Count());
    }

    // (not in Main: .NET may keep what a statement of Main read for as long as Main runs)
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void SideLess() => s_side.Kids.RemoveAt(0);

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void SideGone() => s_side = null;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void AfterGone() => s_after = null;

    // (members that refer back to a member above them through an array's element or a field without slot bits: such a
    // part, let go, may stay until its group goes; told only then)
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Cycles() { s_cyc = Tree("cyc", null, 3, 3); Shapes(s_cyc, "cyc", true); Say("cycles", s_cyc); }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void CyclesGone() => s_cyc = null;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void TreeShapes() => Shapes(s_tree, "shape", false);

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void TreeGone() => s_tree = null;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void FinGone() => s_fin = null;

    // members with references no slot bits tell of, arrays of every shape, references back up through them
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Shapes(Node tree, string kind, bool cycles)
    {
        var b = tree.Kids[2].Kids[0];
        for (int r = 0; r < 6; r++)
        {
            var n = Tree(kind, b, 2, 3);
            n.W = new Wide("wide") { A0 = n.Kids[0], A7 = n.Kids[1], A15 = n.Kids[2], A16 = n.Kids[0].Kids[1], A18 = n.Kids[2].Kids[2] };
            if (cycles && r % 2 == 0) n.W.Back = n;
            if (r % 3 == 0) { n.W.More = new Wide("wide") { A17 = n.Kids[1].Kids[1], Back = cycles && r == 3 ? n : null }; n.Kids[1].W = n.W.More; }
            n.Raw = new Node[7];
            n.Raw[1] = n.Kids[1]; n.Raw[4] = n.Kids[2].Kids[0]; if (cycles && r == 1) n.Raw[6] = n;
            n.Pairs = new Pair[4];
            n.Pairs[1].A = n.Kids[2]; n.Pairs[1].B = n.Kids[0].Kids[2]; n.Pairs[3].B = n.Kids[1]; if (cycles && r == 4) n.Pairs[2].A = n;
            n.Kids[2].Raw = new Node[] { null, n.Kids[2].Kids[1], null };
            n.Kids[0].Any = n.Kids[1];
            b.Kids[r % 3] = n;
        }
        if (cycles) return;
        // (a plain object with content of its own that joins by a store while a local holds it)
        var m = tree.Kids[2].Kids[1];
        var p1 = new Node("joined", 3) { Raw = new Node[3] };
        m.Next = p1;
        p1.Raw[1] = new Node("joined", 4) { Up = p1 };
        var p2 = new Node("joined", 5) { Raw = new Node[] { m.Kids[0], null } };
        m.Kids[1].Up = p2;
        p2.Up = m.Kids[1];
        m.Kids[1].Up = m;
        p2 = null; p1 = null;
        Say("shapes", s_tree);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void ShapesGo()
    {
        var b = s_tree.Kids[2].Kids[0];
        for (int k = 0; k < 3; k++) b.Kids[k] = Tree("tree", b, 3, 3);
        var m = s_tree.Kids[2].Kids[1];
        m.Next = null;
        Say("shapes replaced", s_tree);
    }

    // nodes with finalizers: subtrees replaced, a child moved, the tree let go
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Fins()
    {
        s_fin = FinTree(null, 4, 3);
        for (int r = 0; r < 12; r++)
        {
            var b = s_fin.Kids[r % 3].Kids[(r / 3) % 3];
            var sub = FinTree(b, 2, 3);
            if (r % 4 == 0) sub.Raw = new Fin[] { sub.Kids[1], null, sub.Kids[2].Kids[0] };
            if (b.Kids.Count > r % 3) b.Kids[r % 3] = sub; else b.Kids.Add(sub);
            if (r % 3 == 1)
            {
                var c = s_fin.Kids[(r + 1) % 3].Kids[(r / 3) % 3];
                if (c.Kids.Count == 0) continue;
                var moved = c.Kids[c.Kids.Count - 1];
                c.Kids.RemoveAt(c.Kids.Count - 1);
                var to = s_fin.Kids[(r + 1) % 3];
                moved.Up = to; to.Kids.Add(moved);
            }
        }
        Console.WriteLine("fins " + s_fin.Sum());
    }

    // plain objects with finalizers that run code while the subtree that holds them is destroyed
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void BusyTags()
    {
        var b = s_tree.Kids[1].Kids[2];
        b.Kids[0] = Tree("busy", b, 3, 3, busy: true);
        Busy.Weak = new WeakReference<Node>(b.Kids[0].Kids[2].Kids[2]);
        Busy.Victim = s_tree.Kids[0].Kids[2];
        b.Kids[0] = Tree("tree", b, 3, 3);
    }

    static void Totals(string when)
    {
        Tracked.Report(when);
        // (every tag left is a node's that the tree, the list aside or the kept node still reaches)
        int reach = s_tree == null ? 0 : s_tree.Count();
        Console.WriteLine(when + ": tree reaches " + reach + ", parent after " + (s_after == null ? 0 : s_after.Count()));
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        Replace();
        Totals("replaced");
        Moves();
        Totals("moved");
        Keep();
        Totals("one kept");
        KeptStill();
        Totals("kept let go");
        Shared();
        Totals("shared");
        SideLess();
        Totals("shared, one left");
        SideGone();
        Totals("shared let go");
        TreeShapes();
        Totals("shapes");
        ShapesGo();
        Totals("shapes replaced");
        Cycles();
        CyclesGone();
        Totals("cycles let go");
        Fins();
        for (int i = 0; i < 4; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        Console.WriteLine("fins ran " + Fin.Ran + " saw " + Fin.Seen);
        FinGone();
        for (int i = 0; i < 4; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        Console.WriteLine("fins ran " + Fin.Ran + " saw " + Fin.Seen);
        BusyTags();
        Totals("busy");
        Console.WriteLine("finalizers that ran code " + Busy.Ran);
        AfterGone();
        Totals("parent after let go");
        TreeGone();
        Totals("all let go");
    }
}
