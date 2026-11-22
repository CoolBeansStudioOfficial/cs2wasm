// A new object put into a slot of a structure that can hold a cycle joins it with its own slots linked in one step
// where what they hold says what the links come to (Rt.JoinFresh, CS2WASM_NO_FUSED_JOIN=1 for the general way: the
// join, then a visit of the object's slots, each as the store it stands for). The step rests on: the type's word
// naming every slot a cycle can close through (Rt.ArenaSlots: a slot it left out would stay a counted reference
// no link knows of, and its group would never go); a new object of the object's own being taken as its own only when
// its slots hold nothing to link; the reference up being an up-reference only to a member on the container's chain
// of owned links that code still refers to; and, for a node whose list takes its first item (the scene's order:
// the node made with its parent, then its children one by one), the node being the plain object that holds the top
// of that chain. Each case here is built both ways the step can be met or refused, counted against .NET while the
// structure lives (what .NET frees must be freed then, nothing sooner: a use after free shows in the checked build
// and in the sums), and again when it has gone. The cases whose structure is one that may stay until its group goes
// (a node that refers to a member beside it or of another tree, or to itself) are built in a tree of their own,
// `odd`, which is counted when it has gone, and summed while it lives. Nothing here assigns a node's list of
// children: a program with such a store anywhere keeps every replaced subtree until its tree goes, with the step
// and without it (tools/tree-assign.cs).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

class Tracked
{
    public static readonly List<string> Kinds = new();
    public static readonly int[] Live = new int[64];
    readonly int _kind;
    public Tracked(string kind)
    {
        int k = Kinds.IndexOf(kind); if (k < 0) { k = Kinds.Count; Kinds.Add(kind); }
        _kind = k;
        Live[k]++;
    }
    ~Tracked() { System.Threading.Interlocked.Decrement(ref Live[_kind]); }
    public static void Report(string when)
    {
        for (int i = 0; i < 4; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        // (the trees whose parts may stay until their group goes are counted when they have gone: the odd tree
        // and the one it refers into; the classes whose reference up is a delegate's or inside a struct)
        bool all = when == "all gone", kinds = all || when == "kinds gone";
        Console.WriteLine(when + ": " + string.Join(", ", Kinds.Select((k, i) => (k, i)).Where(x => all || (x.k != "odd" && x.k != "other" && (kinds || (x.k != "clo" && x.k != "inl")))).OrderBy(x => x.k, StringComparer.Ordinal).Select(x => x.k + " " + Live[x.i])));
    }
}

// (a node's tag: not of the group, with the finalizer)
sealed class Tag : Tracked
{
    public Tag(string kind) : base(kind) { }
}

// five slots a cycle can close through, all among the first words
sealed class Node
{
    public Node Up, Next; public List<Node> Kids = new(); public Node[] Raw; public object Any; public Tag Tag; public int Id;
    public Node(string kind, int id = 0) { Tag = new Tag(kind); Id = id; }
    public long Sum() { long s = Id; foreach (var c in Kids) if (c != null) s += c.Sum(); if (Raw != null) foreach (var c in Raw) if (c != null) s += 5 * c.Id; if (Next != null) s += 3 * Next.Id; if (Any is Node a) s += 7 * a.Id; return s; }
    public int Count() { int n = 1; foreach (var c in Kids) if (c != null) n += c.Count(); return n; }
    public bool Sound() { foreach (var c in Kids) if (c == null || c.Up != this || !c.Sound()) return false; return true; }
}

// slots of a base class and of the class itself
class Mid
{
    public Mid Up; public Tag Tag; public int Id;
    public Mid(string kind, int id) { Tag = new Tag(kind); Id = id; }
}
sealed class Der : Mid
{
    public List<Mid> Kids = new(); public Mid Other;
    public Der(string kind, int id) : base(kind, id) { }
    public long Sum() { long s = Id + (Other != null ? 2 * Other.Id : 0); foreach (var c in Kids) s += c is Der d ? d.Sum() : c.Id; return s; }
}

// nine slots: more than the word says
sealed class Nine
{
    public Nine Up, A, B, C, D, E, F, G; public List<Nine> Kids = new(); public Tag Tag; public int Id;
    public Nine(string kind, int id) { Tag = new Tag(kind); Id = id; }
    public long Sum() { long s = Id + (A != null ? A.Id : 0) + (G != null ? 2 * G.Id : 0); foreach (var c in Kids) s += c.Sum(); return s; }
}

// slots past the first fifteen words
sealed class Late
{
    public long P0, P1, P2, P3, P4, P5, P6, P7;
    public Late Up; public List<Late> Kids = new(); public Tag Tag; public int Id;
    public Late(string kind, int id) { Tag = new Tag(kind); Id = id; P7 = id; }
    public long Sum() { long s = Id + P7; foreach (var c in Kids) s += c.Sum(); return s; }
}

// references inside a struct that is a field
struct Link { public Inl Up; public int N; public Inl Peer; }
sealed class Inl
{
    public Link L; public List<Inl> Kids = new(); public Tag Tag; public int Id;
    public Inl(string kind, int id) { Tag = new Tag(kind); Id = id; }
    public long Sum() { long s = Id + L.N + (L.Peer != null ? 2 * L.Peer.Id : 0); foreach (var c in Kids) s += c.Sum(); return s; }
}

// members with a finalizer: each sees its parent and its children as it goes
sealed class Fin
{
    public static long Seen; public static int Ran;
    public Fin Up; public List<Fin> Kids = new(); public int Id;
    public Fin(int id) { Id = id; }
    ~Fin()
    {
        Ran++;
        if (Up != null) Seen += Up.Id;
        foreach (var k in Kids) if (k != null) Seen += 3 * k.Id + (k.Up == this ? 1000 : 0);
    }
    public long Sum() { long s = Id; foreach (var c in Kids) s += c.Sum(); return s; }
}

// a delegate that refers to the object that holds it
sealed class Clo
{
    public Clo Up; public List<Clo> Kids = new(); public Func<int> Get; public Tag Tag; public int Id;
    public Clo(string kind, int id) { Tag = new Tag(kind); Id = id; }
    public long Sum() { long s = Id + (Get != null ? Get() : 0); foreach (var c in Kids) s += c.Sum(); return s; }
}

static class P
{
    static Node s_tree, s_other, s_odd, s_keep, s_lone, s_plainHeld, s_hold, s_side;
    static List<Node> s_list;
    static Der s_der; static Nine s_nine; static Late s_late; static Inl s_inl; static Fin s_fin; static Clo s_clo;
    static uint s_seed = 4711;
    static uint Next() { s_seed = s_seed * 1664525u + 1013904223u; return s_seed >> 8; }
    static int s_ids;

    static void Say(string what, Node tree) => Console.WriteLine(what + ": nodes " + tree.Count() + " sum " + tree.Sum() + " sound " + tree.Sound());

    // the scene's order: the parent set as the node is made, the node put into its parent's list when its own
    // children are in its list
    static Node Tree(string kind, Node up, int depth, int fan)
    {
        var e = new Node(kind, ++s_ids) { Up = up };
        if (depth > 0) for (int i = 0; i < fan; i++) e.Kids.Add(Tree(kind, e, depth - 1, fan));
        return e;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Build()
    {
        s_tree = Tree("tree", null, 4, 3);
        s_other = Tree("other", null, 2, 4);
        s_odd = Tree("odd", null, 3, 3);
        Say("built", s_tree);
        Say("built other", s_other);
        Say("built odd", s_odd);
    }

    // a leaf, by what its slots hold when it is put into the list of a node that has items
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Leaves()
    {
        // (a static refers to the parent meanwhile, as the local of the method that builds a node does: the leaf's
        // reference to it is then not its last from outside)
        var p = s_hold = s_tree.Kids[1].Kids[2];
        var sib = p.Kids[0];
        // its parent
        p.Kids.Add(new Node("leaf", 1000) { Up = p });
        // its parent's parent, and the root
        p.Kids.Add(new Node("leaf", 1002) { Up = p.Up });
        p.Kids.Add(new Node("leaf", 1003) { Up = s_tree });
        // with things that are not of the group
        p.Kids.Add(new Node("leaf", 1007) { Up = p, Any = "text" });
        p.Kids.Add(new Node("leaf", 1008) { Up = p, Any = 42 });
        p.Kids.Add(new Node("leaf", 1009) { Up = p, Any = new Tag("any tag") });
        // with a new node of its own, empty, and one with content
        p.Kids.Add(new Node("leaf", 1010) { Up = p, Any = new Node("own", 3) });
        p.Kids.Add(new Node("leaf", 1011) { Up = p, Next = new Node("own", 4) { Next = new Node("own", 5) } });
        p.Kids.Add(new Node("leaf", 1012) { Up = p, Next = Tree("own", null, 1, 2) });
        // with a new node of its own whose own node refers up
        p.Kids.Add(new Node("leaf", 1022) { Up = p, Next = new Node("own", 10) { Next = new Node("own", 11) { Up = p } } });
        // with a new array, empty and not
        p.Kids.Add(new Node("leaf", 1013) { Up = p, Raw = new Node[3] });
        p.Kids.Add(new Node("leaf", 1014) { Up = p, Raw = new[] { new Node("own", 6), null, new Node("own", 7) } });
        // the same new node in two slots, and a plain node something else holds too
        var twice = new Node("own", 8);
        p.Kids.Add(new Node("leaf", 1015) { Up = p, Next = twice, Any = twice });
        twice = null;
        s_plainHeld = new Node("held plain", 9);
        p.Kids.Add(new Node("leaf", 1016) { Up = p, Next = s_plainHeld });
        // no parent at all
        p.Kids.Add(new Node("leaf", 1017));
        // into a slot that is a field, not a list's
        sib.Next = new Node("leaf", 1018) { Up = sib };
        // into an array that is a member's
        p.Raw = new Node[4];
        p.Raw[1] = new Node("leaf", 1020) { Up = p };
        p.Raw[3] = new Node("leaf", 1021) { Up = p };
        s_hold = null;
        Say("leaves", s_tree);
    }

    // leaves and nodes that refer beside themselves: in the tree that is counted when it has gone
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Odd()
    {
        var p = s_hold = s_odd.Kids[1].Kids[2];
        var sib = p.Kids[0];
        var far = s_other.Kids[3].Kids[1];
        // a member beside it that code holds: when that one is taken out of the list, the leaf's reference keeps it
        s_side = p.Kids[1];
        p.Kids.Add(new Node("odd", 1030) { Up = s_side });
        var beside = p.Kids[p.Kids.Count - 1];
        p.Kids.RemoveAt(1);
        s_side = null;
        s_other.Kids[2].Kids.Add(Tree("other", s_other.Kids[2], 1, 4));
        Console.WriteLine("beside: " + beside.Up.Id + " " + beside.Up.Kids.Count + " " + (beside.Up.Up == p));
        // the same with the root of its tree, too far above for an up-reference
        s_side = s_odd;
        p.Kids.Add(new Node("odd", 1031) { Up = s_side });
        s_side = null;
        // its parent twice
        p.Kids.Add(new Node("odd", 1001) { Up = p, Next = p });
        // a member that is not above it, of its own tree and of another
        p.Kids.Add(new Node("odd", 1004) { Up = sib });
        p.Kids.Add(new Node("odd", 1005) { Up = far });
        p.Kids.Add(new Node("odd", 1006) { Up = p, Next = far });
        sib.Any = new Node("odd", 1019) { Up = p };
        p.Raw = new Node[4];
        p.Raw[3] = new Node("odd", 1021) { Up = p.Up, Next = sib };
        var q = s_odd.Kids[2].Kids[0];
        foreach (int how in new[] { 1, 5, 9, 1, 5, 9 }) q.Kids.Add(First("odd", q, how, how % 2 == 0 ? q.Kids[1] : far));
        q.Kids.Add(First("odd", q, 1, q.Kids[1]));
        First("odd", q, 1, q.Kids[1]);
        // two nodes beside each other that refer to each other: they go with the group, not before
        q.Kids[0].Next = q.Kids[2]; q.Kids[2].Next = q.Kids[0];
        // a leaf whose parent nothing but the tree refers to: the leaf's reference is the parent's last from outside
        s_odd.Kids[1].Kids[0].Kids.Add(new Node("odd", 1032) { Up = s_odd.Kids[1].Kids[0] });
        for (int i = p.Kids.Count - 1; i >= 3; i -= 2) p.Kids.RemoveAt(i);
        q.Kids.RemoveAt(4);
        s_odd.Kids[0] = Tree("odd", s_odd, 2, 3);
        s_hold = null;
        Console.WriteLine("beside: " + beside.Up.Id + " " + beside.Up.Kids.Count + " " + (beside.Up.Up == p));
        Say("odd", s_odd);
        Say("odd other", s_other);
    }

    // the same where nothing but the tree refers to the parent
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Lone()
    {
        s_tree.Kids[0].Kids[0].Kids.Add(new Node("lone", 1100) { Up = s_tree.Kids[0].Kids[0] });
        s_tree.Kids[0].Kids[0].Kids[0].Kids.Add(new Node("lone", 1101) { Up = s_tree.Kids[0].Kids[0].Kids[0] });
        s_tree.Kids[0].Kids[1].Kids.Add(new Node("lone", 1102) { Up = s_tree.Kids[0] });
        Say("lone", s_tree);
    }

    // a node whose list takes its first item, by what the node's other slots hold
    static Node First(string kind, Node up, int how, Node side)
    {
        var e = new Node(kind, ++s_ids) { Up = up };
        switch (how)
        {
            case 1: e.Next = side; break;                           // a member of the same tree, or of another
            case 2: e.Any = "text"; break;
            case 3: e.Any = new Node("own", 20); break;             // a plain node of its own
            case 4: e.Raw = new Node[2]; break;
            case 5: e.Next = e; break;                              // itself
            case 6: e.Kids.Capacity = 8; break;

        }
        if (how == 8)
        {
            // two children made before either is in the list
            var c1 = new Node(kind, ++s_ids) { Up = e }; var c2 = new Node(kind, ++s_ids) { Up = e };
            e.Kids.Add(c1); e.Kids.Add(c2);
        }
        else if (how == 9)
        {
            // the first child refers to the node twice
            e.Kids.Add(new Node(kind, ++s_ids) { Up = e, Next = e });
        }
        else if (how == 10)
        {
            // the first child has content of its own
            var c = new Node(kind, ++s_ids) { Up = e };
            c.Kids.Add(new Node(kind, ++s_ids) { Up = c });
            e.Kids.Add(c);
        }
        else if (how == 11)
        {
            // the first child goes into the node's array
            e.Raw = new Node[2];
            e.Raw[0] = new Node(kind, ++s_ids) { Up = e };
            e.Kids.Add(new Node(kind, ++s_ids) { Up = e });
        }
        else e.Kids.Add(new Node(kind, ++s_ids) { Up = e });
        e.Kids.Add(new Node(kind, ++s_ids) { Up = e });
        return e;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Firsts()
    {
        var p = s_tree.Kids[2].Kids[0];
        foreach (int how in new[] { 0, 2, 3, 4, 6, 8, 10, 11, 0 }) p.Kids.Add(First("first", p, how, null));
        // a node no list takes: the root of its own tree, kept by a static; and one let go at once
        s_lone = First("first lone", null, 0, null);
        First("first gone", null, 0, null);
        First("first gone", p, 3, null);
        Say("firsts", s_tree);
        Console.WriteLine("lone: " + s_lone.Count() + " " + s_lone.Sound());
    }

    // a node whose list something else refers to as it takes its first item: the list is not the node's alone, and
    // when the node is taken out of the tree the list keeps its items, and they the node
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Shared()
    {
        var p = s_tree.Kids[2].Kids[1];
        var e = new Node("shared", ++s_ids) { Up = p };
        s_list = e.Kids;
        e.Kids.Add(new Node("shared", ++s_ids) { Up = e });
        e.Kids.Add(new Node("shared", ++s_ids) { Up = e });
        p.Kids.Add(e);
        e = null;
        Say("shared", s_tree);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void SharedCut()
    {
        var p = s_tree.Kids[2].Kids[1];
        p.Kids.RemoveAt(p.Kids.Count - 1);
        Console.WriteLine("shared: " + s_list.Count + " " + (s_list[0].Up == s_list[1].Up) + " " + s_list[1].Up.Kids.Count);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void SharedGone() { s_list = null; }

    // parts let go while the tree lives: each must go then
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Cut()
    {
        var p = s_tree.Kids[1].Kids[2];
        // (every second leaf of Leaves, from the end)
        for (int i = p.Kids.Count - 1; i >= 3; i -= 2) p.Kids.RemoveAt(i);
        p.Kids[0].Next = null;
        p.Raw[3] = null;
        // (one more that stays: a new node of its own whose own node refers up)
        s_lone.Kids.Add(new Node("leaf", 1024) { Up = s_lone, Next = new Node("own", 12) { Next = new Node("own", 13) { Up = s_lone } } });
        var q = s_tree.Kids[2].Kids[0];
        for (int i = q.Kids.Count - 1; i >= 3; i -= 3) q.Kids.RemoveAt(i);
        // (a subtree stored over: nodes made by the step go member by member)
        s_tree.Kids[0] = Tree("tree", s_tree, 3, 3);
        s_plainHeld = null;
        Say("cut", s_tree);
    }

    // a node of a part that is replaced, held by a static: the part stays, and goes when the static lets go
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Keep()
    {
        var b = s_tree.Kids[1];
        b.Kids[0] = Tree("held", b, 3, 3);
        s_keep = b.Kids[0].Kids[2].Kids[1].Kids[0];
        b.Kids[0] = Tree("tree", b, 2, 3);
        Say("kept", s_tree);
        Console.WriteLine("kept node: " + s_keep.Id + " up " + s_keep.Up.Up.Up.Count());
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Unkeep() { s_keep = null; }

    static Der DerTree(string kind, Der up, int depth, int fan)
    {
        var e = new Der(kind, ++s_ids) { Up = up };
        if (depth > 0) for (int i = 0; i < fan; i++) e.Kids.Add(DerTree(kind, e, depth - 1, fan));
        else { e.Kids.Add(new Mid(kind, ++s_ids) { Up = e }); e.Other = up; }
        return e;
    }
    static Nine NineTree(string kind, Nine up, int depth, int fan)
    {
        var e = new Nine(kind, ++s_ids) { Up = up, A = up, G = up?.Up };
        if (depth > 0) for (int i = 0; i < fan; i++) e.Kids.Add(NineTree(kind, e, depth - 1, fan));
        return e;
    }
    static Late LateTree(string kind, Late up, int depth, int fan)
    {
        var e = new Late(kind, ++s_ids) { Up = up };
        if (depth > 0) for (int i = 0; i < fan; i++) e.Kids.Add(LateTree(kind, e, depth - 1, fan));
        return e;
    }
    static Inl InlTree(string kind, Inl up, int depth, int fan)
    {
        var e = new Inl(kind, ++s_ids) { L = new Link { Up = up, N = depth, Peer = up?.L.Up } };
        if (depth > 0) for (int i = 0; i < fan; i++) e.Kids.Add(InlTree(kind, e, depth - 1, fan));
        return e;
    }
    static Fin FinTree(Fin up, int depth, int fan)
    {
        var e = new Fin(++s_ids) { Up = up };
        if (depth > 0) for (int i = 0; i < fan; i++) e.Kids.Add(FinTree(e, depth - 1, fan));
        return e;
    }
    static Clo CloTree(string kind, Clo up, int depth, int fan)
    {
        var e = new Clo(kind, ++s_ids) { Up = up };
        e.Get = () => e.Id + (e.Up != null ? e.Up.Id : 0) + e.Kids.Count;
        if (depth > 0) for (int i = 0; i < fan; i++) e.Kids.Add(CloTree(kind, e, depth - 1, fan));
        return e;
    }

    // the same order for classes whose slots the word says in other ways, or not at all
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Kinds()
    {
        s_der = DerTree("der", null, 3, 3);
        s_nine = NineTree("nine", null, 3, 3);
        s_late = LateTree("late", null, 3, 3);
        s_inl = InlTree("inl", null, 3, 3);
        s_fin = FinTree(null, 3, 3);
        s_clo = CloTree("clo", null, 3, 3);
        Console.WriteLine("kinds: " + s_der.Sum() + " " + s_nine.Sum() + " " + s_late.Sum() + " " + s_inl.Sum() + " " + s_fin.Sum() + " " + s_clo.Sum());
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void KindsCut()
    {
        ((Der)s_der.Kids[1]).Kids[2] = DerTree("der", (Der)s_der.Kids[1], 1, 2);
        s_der.Kids.RemoveAt(0);
        s_nine.Kids[2] = NineTree("nine", s_nine, 2, 2);
        s_nine.Kids[0].Kids.RemoveAt(1);
        s_late.Kids[1] = LateTree("late", s_late, 1, 4);
        s_inl.Kids[0] = InlTree("inl", s_inl, 2, 2);
        s_inl.Kids[1].Kids.RemoveAt(2);
        s_fin.Kids[1] = FinTree(s_fin, 2, 2);
        s_fin.Kids[0].Kids.RemoveAt(0);
        s_clo.Kids[2] = CloTree("clo", s_clo, 1, 3);
        s_clo.Kids[0].Kids.RemoveAt(2);
        Console.WriteLine("kinds cut: " + s_der.Sum() + " " + s_nine.Sum() + " " + s_late.Sum() + " " + s_inl.Sum() + " " + s_fin.Sum() + " " + s_clo.Sum());
    }

    // many rounds of the scene's own frame: a subtree built and stored over another, a child moved
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Frames()
    {
        for (int f = 0; f < 300; f++)
        {
            var a = s_tree.Kids[(int)(Next() % 3)];
            var b = a.Kids[(int)(Next() % (uint)Math.Min(3, a.Kids.Count))];
            if (b.Kids.Count == 0) continue;
            int k = (int)(Next() % (uint)b.Kids.Count);
            b.Kids[k] = Tree("tree", b, 2, 3 + f % 3);
            var c = b.Kids[k];
            if (f % 4 == 0 && c.Kids.Count > 1)
            {
                var moved = c.Kids[c.Kids.Count - 1];
                c.Kids.RemoveAt(c.Kids.Count - 1);
                var to = f % 8 == 0 ? b : a;
                moved.Up = to;
                to.Kids.Add(moved);
            }
            if (f % 50 == 49) s_tree.Kids[(int)(Next() % 3)] = Tree("tree", s_tree, 3, 3);
        }
        Say("frames", s_tree);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Drop(int what)
    {
        if (what == 0) { s_tree = null; s_lone = null; }
        else if (what == 2) { s_odd = null; s_other = null; }
        else { s_der = null; s_nine = null; s_late = null; s_inl = null; s_fin = null; s_clo = null; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void FinReport(string when)
    {
        for (int i = 0; i < 4; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        Console.WriteLine(when + ": finalizers " + Fin.Ran + " saw " + Fin.Seen);
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        Build(); Tracked.Report("after build");
        Leaves(); Tracked.Report("after leaves");
        Lone(); Tracked.Report("after lone");
        Firsts(); Tracked.Report("after firsts");
        Shared(); Tracked.Report("after shared");
        SharedCut(); Tracked.Report("after shared cut");
        SharedGone(); Tracked.Report("after shared gone");
        Cut(); Tracked.Report("after cut");
        Keep(); Tracked.Report("after keep");
        Unkeep(); Tracked.Report("after unkeep");
        Kinds(); Tracked.Report("after kinds"); FinReport("after kinds");
        KindsCut(); Tracked.Report("after kinds cut"); FinReport("after kinds cut");
        Frames(); Tracked.Report("after frames");
        Drop(1); Tracked.Report("kinds gone"); FinReport("kinds gone");
        Drop(0); Tracked.Report("tree gone");
        Odd();
        Drop(2); Tracked.Report("all gone");
    }
}
