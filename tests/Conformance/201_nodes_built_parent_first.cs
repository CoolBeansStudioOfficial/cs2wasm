// A tree built parent first: a node is made with its parent in a field, its children are made and put into its
// list one by one, and only then is the node put into its parent's list. Each node takes three direct steps of the
// runtime where the general link would come to the same words (Rt.JoinPair: the list's first array into the list,
// while both are plain objects; Rt.NodeUp: the node's parent field linked as the node joins at its first child;
// Rt.AddNode: the node into its parent's list; CS2WASM_NO_NODE_PATH=1 for the general way). The steps rest on:
// the new array and the list's other slots holding nothing a visit would link (JoinPair); the parent being
// referred to by code besides the node's field, the node having no second slot to link in the same step, and the
// parent's owner being asked (NodeUp); the store's count being the node's one reference from outside, and the
// member the node owns above the list being free to be let go (AddNode). Everything is counted against .NET while
// the trees live (a subtree cut from a living tree goes at the store that cuts it, a node never added goes with
// its children when its local does, nothing sooner: the checked build reads no freed memory) and when they have
// gone. The cases: the scene's order; a node dropped before the add, under a tree being built and under a living
// one; a node added to another parent than its field names; the parent field set after the add; a node whose list
// a local or a static holds; nodes with finalizers; a node moved after it was built, and one in two lists; a
// build that throws half way; a parent only the node's field refers to; a node with two slots that hold members;
// a child that refers to the node above its parent; new objects with content stored into plain ones; a node
// whose own parent is a plain object still (Rt.PlainParents, CS2WASM_NO_PLAIN_PARENTS=1), with a parent that
// holds a new node with content. tools/node-path-breaks.sh leaves each rule out in turn.
// The trees whose parts may stay until their group goes (a node that refers to a node beside it) are counted when
// they have gone.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

sealed class Tag { readonly int _k; public Tag(int k) { _k = k; Node.Live[k]++; } ~Tag() { System.Threading.Interlocked.Decrement(ref Node.Live[_k]); } }
sealed class Node
{
    public static readonly int[] Live = new int[8];
    public Node Up; public List<Node> Kids = new(); public Node Side; public Tag Tag; public int Id;
    public Node(int kind, int id) { Id = id; Tag = new Tag(kind); }
    public int Count() { int n = 1; foreach (var c in Kids) n += c.Count(); return n; }
    public bool Sound() { foreach (var c in Kids) if (c.Up != this || !c.Sound()) return false; return true; }
    public long Sum() { long s = Id + (Up != null ? Up.Id : 0) + (Side != null ? 7 * Side.Id : 0); foreach (var c in Kids) s += 3 * c.Sum(); return s; }
}

// (members with a finalizer of their own: another group of classes)
sealed class Fin
{
    public static int Ran; public static long Seen;
    public Fin Up; public List<Fin> Kids = new(); public int Id;
    public Fin(int id) { Id = id; }
    ~Fin() { Ran++; if (Up != null) Seen += Up.Id; foreach (var k in Kids) Seen += 3 * k.Id + (k.Up == this ? 1000 : 0); }
}

// (no list: a plain object of this class can take a new one as a pair)
sealed class Link { public Link Next, Up; public Tag Tag; public Link() { Tag = new Tag(6); } }

sealed class Boom : Exception { }

static class P
{
    static Node s_tree, s_odd, s_keep, s_two, s_hold, s_pin, s_cross;
    static List<Node> s_list;
    static Fin s_fin;
    static int s_ids, s_fuse;

    // the scene's order
    static Node Tree(Node up, int depth, int fan, int kind = 0)
    {
        var e = new Node(kind, ++s_ids) { Up = up };
        if (depth > 0) for (int i = 0; i < fan; i++) e.Kids.Add(Tree(e, depth - 1, fan, kind));
        return e;
    }
    static Fin FinTree(Fin up, int depth, int fan)
    {
        var e = new Fin(++s_ids) { Up = up };
        if (depth > 0) for (int i = 0; i < fan; i++) e.Kids.Add(FinTree(e, depth - 1, fan));
        return e;
    }
    // the parent set after the add; the node used after it
    static Node After(int depth, int fan)
    {
        var e = new Node(1, ++s_ids);
        if (depth > 0) for (int i = 0; i < fan; i++) { var c = After(depth - 1, fan); e.Kids.Add(c); c.Up = e; c.Id += e.Kids.Count; }
        return e;
    }
    // a local holds the list, then a static
    static Node Held(Node up, int depth, int fan)
    {
        var e = new Node(2, ++s_ids) { Up = up };
        var kids = e.Kids;
        if (depth > 0) for (int i = 0; i < fan; i++) { if (i == 1) s_list = kids; kids.Add(Held(e, depth - 1, fan)); }
        s_list = null;
        return e;
    }
    // a build that throws when the fuse runs out: the nodes made so far and not yet in a list go with their locals
    static Node Fused(Node up, int depth, int fan)
    {
        var e = new Node(3, ++s_ids) { Up = up };
        if (depth > 0) for (int i = 0; i < fan; i++) { if (--s_fuse == 0) throw new Boom(); e.Kids.Add(Fused(e, depth - 1, fan)); }
        return e;
    }
    // a subtree whose every node is put into the list of the child made before it, not of the parent its field names
    static Node Elsewhere(Node up, int depth, int fan)
    {
        var e = new Node(4, ++s_ids) { Up = up };
        Node last = null;
        if (depth > 0)
            for (int i = 0; i < fan; i++)
            {
                var c = Elsewhere(e, depth - 1, fan);
                if (last != null && (i & 1) == 1) last.Kids.Add(c); else e.Kids.Add(c);
                last = c;
            }
        return e;
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static Node Pick(Node from, int k) => from.Kids[k];
    [MethodImpl(MethodImplOptions.NoInlining)] static Node Leaf(Node up, int kind) => new Node(kind, ++s_ids) { Up = up };
    [MethodImpl(MethodImplOptions.NoInlining)] static void SetSide(Node a, Node v) { a.Side = v; }
    [MethodImpl(MethodImplOptions.NoInlining)] static void SetUp(Node a, Node v) { a.Up = v; }
    [MethodImpl(MethodImplOptions.NoInlining)] static void SetNext(Link a, Link v) { a.Next = v; }
    [MethodImpl(MethodImplOptions.NoInlining)] static Link MakeLink(Link up) => new Link { Up = up };
    [MethodImpl(MethodImplOptions.NoInlining)] static Node Take(Node from) { var m = from.Kids[from.Kids.Count - 1]; from.Kids.RemoveAt(from.Kids.Count - 1); return m; }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Build() { s_tree = Tree(null, 4, 3); s_fin = FinTree(null, 3, 3); }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Replace(int r)
    {
        s_tree.Kids[r % 3] = Tree(s_tree, 3, 3);
        var b = s_tree.Kids[(r + 1) % 3];
        b.Kids[r % 3] = r % 2 == 0 ? Held(b, 2, 2 + r % 3) : Elsewhere(b, 3, 3);
        var a = After(2, 3); b.Kids.Add(a); a.Up = b;
        // (a node with leaves alone, under a node that has an owner)
        b.Kids.Add(Tree(b, 1, 3));
        s_tree.Kids[r % 3].Kids[r % 3].Kids.Add(Tree(s_tree.Kids[r % 3].Kids[r % 3], 1, 2));
        if (b.Kids.Count > 4) b.Kids.RemoveAt(1);
        s_fin.Kids[r % 3] = FinTree(s_fin, 2, 3);
    }

    // a node made under a member, with children and grandchildren, and never added: under a living tree's node
    // (which has an owner) and under a node of a tree being built (which has none, and is the new node's meanwhile)
    [MethodImpl(MethodImplOptions.NoInlining)]
    static int Orphans()
    {
        int n = 0;
        var b = s_tree.Kids[1];
        for (int i = 0; i < 3; i++) { var o = Tree(b, 2, 2 + i); n += o.Count(); var o1 = Tree(b.Kids[i % 2], 1, 2 + i); n += o1.Count(); }
        var top = new Node(0, ++s_ids);
        for (int i = 0; i < 4; i++)
        {
            var c = Tree(top, 2, 2);
            if (i % 2 == 0) top.Kids.Add(c); else n += c.Count();
        }
        s_tree.Kids[1].Kids.Add(top); top.Up = s_tree.Kids[1];
        var f = s_fin.Kids[0];
        for (int i = 0; i < 2; i++) { var o = FinTree(f, 2, 2); n += o.Kids.Count; }
        return n;
    }

    // a parent that only the new node's field refers to from outside (Pick's result goes into the field), with a
    // first child at once; added, and not added
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void OnlyField(Node tree, int kind)
    {
        for (int i = 0; i < 4; i++)
        {
            var e = new Node(kind, ++s_ids) { Up = Pick(tree.Kids[2], i % 2) };
            e.Kids.Add(Leaf(e, kind));
            e.Kids.Add(Tree(e, 1, 2, kind));
            if (i % 2 == 0) e.Up.Kids.Add(e);
        }
    }

    // (the trees are not handed over from Main: .NET keeps an argument of an unoptimized frame alive)
    [MethodImpl(MethodImplOptions.NoInlining)] static void OnlyTree() => OnlyField(s_tree, 0);
    [MethodImpl(MethodImplOptions.NoInlining)] static void OnlyOdd() => OnlyField(s_odd, 5);

    // a node with two slots that hold members when its first child joins: its parent and a node beside it, or its
    // parent twice; and a child that refers, from below the parent, to the node that owns the parent meanwhile
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void BuildOdd()
    {
        var top = new Node(5, ++s_ids);
        top.Kids.Add(Tree(top, 1, 2, 5));
        var first = top.Kids[0];
        for (int i = 0; i < 4; i++)
        {
            var e = new Node(5, ++s_ids) { Up = top };
            e.Side = i % 2 == 0 ? first : top;
            e.Kids.Add(Leaf(e, 5));
            if (i == 3) SetSide(first.Kids[0], e);
            e.Kids.Add(Tree(e, 1, 2, 5));
            if (i == 1) e.Side = null;
            top.Kids.Add(e);
            if (i == 2) e.Side = null;
            // (the node taken out again while the child below its parent refers to it)
            if (i == 3) { top.Kids.RemoveAt(top.Kids.Count - 1); e = null; Console.WriteLine("side " + first.Kids[0].Side.Kids.Count + " " + first.Kids[0].Side.Sum()); }
        }
        // a node in two lists, then taken out of the first
        var two = Tree(top, 1, 2, 5);
        top.Kids.Add(two);
        top.Kids[1].Kids.Add(Pick(top, top.Kids.Count - 1));
        s_two = top.Kids[1];
        s_odd = top;
    }

    // A node p that another node's list holds (and owns), a node built under p and put into p's list, then p's own
    // parent field set to its holder: a reference up only if the holder's list still owns p. `twice`: the new
    // node refers to p by two slots when its first child joins, one of them cleared before p goes into the
    // holder's list. The holder is then let go: all of it goes at that store. A small tree of the same group
    // stays, so that nothing goes because its whole group does.
    [MethodImpl(MethodImplOptions.NoInlining)]
    static Node Under(Node p, Node hold, bool twice)
    {
        var e = new Node(7, ++s_ids) { Up = p };
        if (twice) e.Side = p;
        e.Kids.Add(Leaf(e, 7));
        if (twice) { SetSide(e, null); hold.Kids.Add(p); }
        e.Kids.Add(Leaf(e, 7));
        return e;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Holder(bool twice)
    {
        var hold = new Node(7, ++s_ids);
        var pin = Tree(null, 1, 2, 7);
        var p = new Node(7, ++s_ids);
        p.Kids.Add(Leaf(p, 7));
        if (!twice) hold.Kids.Add(p);
        p.Kids.Add(Under(p, hold, twice));
        SetUp(p, hold);
        SetSide(hold, pin); SetSide(hold, null);
        s_pin = pin; s_hold = hold;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void HolderGone() { Console.WriteLine("holder " + s_hold.Count() + " " + s_hold.Sum()); s_hold = null; }

    // a node built under a top that nothing owns, which a child of that top comes to refer to before the node is
    // put into the top's list (the node is above the top meanwhile, the child's reference one up through the
    // top); the node is then taken out of the list again and read through the child
    [MethodImpl(MethodImplOptions.NoInlining)]
    static Node Cross(Node top, Node c)
    {
        var e = new Node(7, ++s_ids) { Up = top };
        e.Kids.Add(Leaf(e, 7));
        SetSide(c, e);
        return e;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Crossed()
    {
        var top = new Node(7, ++s_ids);
        top.Kids.Add(Leaf(top, 7));
        top.Kids.Add(Cross(top, top.Kids[0]));
        s_cross = top;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Uncross()
    {
        s_cross.Kids.RemoveAt(1);
        var c = s_cross.Kids[0];
        Console.WriteLine("crossed " + c.Side.Kids.Count + " " + c.Side.Sum() + " " + (c.Side.Up == s_cross));
        c.Side = null;
    }

    // A node whose own parent is a plain object still when its first child joins (every first descent of a tree
    // built parent first), where that parent holds, besides its list, a new node of its own with content (a list
    // of its own): not something the parent can take as its own in one step. Then a child under that new node.
    [MethodImpl(MethodImplOptions.NoInlining)]
    static Node First(Node up) { var e = new Node(6, ++s_ids) { Up = up }; e.Kids.Add(Leaf(e, 6)); return e; }
    [MethodImpl(MethodImplOptions.NoInlining)]
    static long Sided()
    {
        long sum = 0;
        for (int i = 0; i < 3; i++)
        {
            var top = new Node(6, ++s_ids) { Side = new Node(6, ++s_ids) };
            if (i == 2) top.Up = new Node(6, ++s_ids);
            top.Kids.Add(First(top));
            var side = top.Side;
            side.Kids.Add(Leaf(side, 6));
            if (i == 1) side.Kids.Add(First(side));
            sum += top.Sum() + side.Sum();
        }
        return sum;
    }

    // new objects with content stored into plain objects that then close a cycle
    [MethodImpl(MethodImplOptions.NoInlining)]
    static long Plain()
    {
        long s = 0;
        for (int i = 0; i < 3; i++)
        {
            var a = new Node(6, ++s_ids); var b = new Node(6, ++s_ids);
            SetSide(a, Leaf(b, 6));     // the new object refers to a plain one
            SetSide(b, a);              // b -> a -> leaf -> b
            var c = new Node(6, ++s_ids) { Up = new Node(6, ++s_ids) };
            var d = c.Up;
            SetSide(c, new Node(6, ++s_ids));   // the container's other slot holds a plain one
            SetSide(d, c);                      // d -> c -> d
            s += a.Sum() + c.Sum() + d.Id;
            var la = new Link(); var lb = new Link();
            SetNext(la, MakeLink(lb));          // the new object refers to a plain one
            SetNext(lb, la);                    // lb -> la -> new -> lb
            var lc = new Link { Up = new Link() };
            var ld = lc.Up;
            SetNext(lc, new Link());            // the container's other slot holds a plain one
            SetNext(ld, lc);                    // ld -> lc -> ld
            var le = new Link();
            SetNext(le, new Link());            // a pair, then a third
            SetNext(le.Next, new Link { Up = le });
            s += le.Next.Next.Up == le ? 1 : 0;
        }
        return s;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Move()
    {
        var from = s_tree.Kids[0].Kids[0]; var to = s_tree.Kids[2];
        var moved = from.Kids[from.Kids.Count - 1];
        from.Kids.RemoveAt(from.Kids.Count - 1);
        moved.Up = to; to.Kids.Add(moved);
        // (taken out and put in, the store's count the only one)
        var from2 = s_tree.Kids[0].Kids[1];
        to.Kids.Add(Take(from2));
        SetUp(to.Kids[to.Kids.Count - 1], to);
        s_tree.Kids[0].Kids[0] = Tree(s_tree.Kids[0], 2, 3);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Throw(int fuse)
    {
        s_fuse = fuse;
        try { var b = s_tree.Kids[fuse % 3]; b.Kids[0] = Fused(b, 3, 3); }
        catch (Boom) { Console.WriteLine("thrown at " + fuse); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Keep() { var b = s_tree.Kids[1]; s_keep = b.Kids[0].Kids[0]; s_tree.Kids[1] = Tree(s_tree, 2, 3); }
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Unkeep() { Console.WriteLine("kept " + s_keep.Up.Up.Count()); s_keep = null; }
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Drop() { s_tree = null; s_fin = null; s_pin = null; s_cross = null; }
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void OddOut() { s_two.Kids.RemoveAt(s_two.Kids.Count - 1); Console.WriteLine("odd " + s_odd.Count() + " " + s_odd.Sum()); s_two = null; s_odd = null; }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Say(string when)
    {
        for (int i = 0; i < 4; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        string live = Node.Live[0] + " " + Node.Live[1] + " " + Node.Live[2] + " " + Node.Live[3] + " " + Node.Live[4] + " " + Node.Live[7];
        // (kinds 5 and 6 may stay until their groups go: counted when all is gone)
        if (s_tree == null) { Console.WriteLine(when + ": " + live + " " + Node.Live[5] + " " + Node.Live[6] + " finalizers " + Fin.Ran + " saw " + Fin.Seen); return; }
        Console.WriteLine(when + ": " + live + " nodes " + s_tree.Count() + " sound " + s_tree.Sound() + " sum " + s_tree.Sum() + " finalizers " + Fin.Ran + " saw " + Fin.Seen);
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        Build(); Say("built");
        for (int r = 0; r < 6; r++) { Replace(r); Say("replaced"); }
        Console.WriteLine("orphans " + Orphans()); Say("orphans");
        OnlyTree(); Say("only field");
        BuildOdd(); OnlyOdd(); Console.WriteLine("plain " + Plain() + " " + Sided()); Say("odd");
        Holder(false); Say("held"); HolderGone(); Say("holder gone");
        Holder(true); Say("held twice"); HolderGone(); Say("holder gone");
        Crossed(); Say("crossed"); Uncross(); Say("uncrossed");
        Move(); Say("moved");
        for (int f = 3; f < 40; f += 7) { Throw(f); Say("thrown"); }
        Keep(); Say("kept");
        Unkeep(); Say("let go");
        for (int r = 0; r < 3; r++) { Replace(r); Say("replaced"); }
        OddOut(); Say("odd out");
        Drop(); Say("gone");
    }
}
