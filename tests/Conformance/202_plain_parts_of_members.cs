// A member's plain part (Rt.PartHolder, docs/AUDIT.md A3.5, the build, round 10): a new object that joins a group in
// one step (Rt.JoinFresh) leaves its own new, empty object (an entity's list of children) a plain object, held by a
// plain counted reference, with the member and the slot noted in it; whenever that object joins later, its holder's
// slot is linked first (IL2WASM_NO_PLAIN_PARTS=1: it joins with its holder, as before). Counted against .NET while
// the structures live and when they have gone: a subtree cut from a living tree goes at the store that cuts it.
// The cases: a tree in the scene's order, a subtree replaced; a leaf that gets children later, and is then cut (the
// part joins as a container); a leaf's list a second node holds, filled through either, the leaf cut, the second
// reference let go (the part joins as a value); a part of another class than a list, with a reference back to its
// holder stored into it; a list taken out of its leaf (the slot null, another list, the same list again) and filled
// through a local (the note is stale); a leaf that left its group and is filled as a plain object; weak references
// to parts, made before and after the leaf joined, read by the finalizers of the group they die with; a part of a
// class with a finalizer; a part held by an array and by a static.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

sealed class Tag { readonly int _k; public Tag(int k) { _k = k; Node.Live[k]++; } ~Tag() { System.Threading.Interlocked.Decrement(ref Node.Live[_k]); } }

sealed class Bag { public Node? Back; public Bag? Next; public int N; public Tag Tag = new Tag(5); }

sealed class Node
{
    public static readonly int[] Live = new int[8];
    public Node? Up; public List<Node> Kids = new(); public List<Node>? Lent; public Bag? Bag; public Tag Tag; public int Id;
    public Node(int kind, int id) { Id = id; Tag = new Tag(kind); }
    public int Count() { int n = 1; foreach (var c in Kids) n += c.Count(); return n; }
    public bool Sound() { foreach (var c in Kids) if (c.Up != this || !c.Sound()) return false; return true; }
}

// (a group whose members have finalizers: what a finalizer sees of the weak references to the lists of its group)
sealed class Watch
{
    public static int Ran, SawAlive, SawDead;
    public static readonly List<WeakReference<List<Watch>>>[] Weak = { new(), new(), new() };
    public Watch? Up; public List<Watch> Kids = new(); public int Id, Group;
    // (the lists of its own group: all of them go with it, or went before)
    ~Watch() { Ran++; foreach (var w in Weak[Group]) { if (w.TryGetTarget(out _)) SawAlive++; else SawDead++; } }
}

// (a part with a finalizer of its own)
sealed class Bell { public static int Rang; public Holder? Back; ~Bell() { Rang++; } }
sealed class Holder { public Holder? Up; public List<Holder> Kids = new(); public Bell Bell = new(); public Tag Tag = new Tag(6); }

static class P
{
    static Node? s_tree, s_keep;
    static List<Node>? s_list;
    static Holder? s_holders;
    static Watch? s_watch;
    static int s_ids;

    static Node Tree(Node? up, int depth, int fan, int kind = 0)
    {
        var e = new Node(kind, ++s_ids) { Up = up };
        if (depth > 0) for (int i = 0; i < fan; i++) e.Kids.Add(Tree(e, depth - 1, fan, kind));
        return e;
    }

    static void Say(string when)
    {
        for (int i = 0; i < 4; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        Console.WriteLine(when + ": " + string.Join(" ", Node.Live) + (s_tree != null ? " tree " + s_tree.Count() + (s_tree.Sound() ? " sound" : " NOT SOUND") : ""));
    }

    [MethodImpl(MethodImplOptions.NoInlining)] static Node Leaf(Node root, int a, int b) => root.Kids[a].Kids[b];

    // a leaf gets children later: its list joins as a container
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Grow(Node leaf, int n, int kind)
    {
        for (int i = 0; i < n; i++) leaf.Kids.Add(new Node(kind, ++s_ids) { Up = leaf });
    }

    // (each step a method of its own, so that nothing of it is on the stack when the objects are counted)
    static void Do(string when, Action step) { step(); Say(when); }

    static void LaterChildren()
    {
        Do("built", static () => s_tree = Tree(null, 2, 3));
        Do("leaves grown", static () => { Grow(Leaf(s_tree!, 0, 1), 3, 1); Grow(Leaf(s_tree!, 2, 2), 6, 1); Grow(s_tree!.Kids[0].Kids[1].Kids[2], 2, 1); });
        Do("a grown leaf replaced", static () => s_tree!.Kids[0].Kids[1] = new Node(0, ++s_ids) { Up = s_tree.Kids[0] });
        Do("a grown leaf removed", static () => s_tree!.Kids[2].Kids.RemoveAt(2));
        Do("a subtree replaced", static () => s_tree!.Kids[1] = Tree(s_tree, 2, 2));
    }

    // a leaf's list held by a second node, filled through either; the leaf cut; the second reference let go
    static void Lent()
    {
        Do("lent", static () => Leaf(s_tree!, 2, 0).Lent = Leaf(s_tree!, 0, 0).Kids);
        Do("lent and filled", static () =>
        {
            var leaf = Leaf(s_tree!, 0, 0);
            var other = Leaf(s_tree!, 2, 0);
            other.Lent!.Add(new Node(2, ++s_ids) { Up = leaf });
            leaf.Kids.Add(new Node(2, ++s_ids) { Up = leaf });
        });
        Do("the lender cut", static () => s_tree!.Kids[0].Kids.RemoveAt(0));
        Do("the loan read", static () => { var other = Leaf(s_tree!, 2, 0); Console.WriteLine(other.Lent!.Count + " " + other.Lent[0].Up!.Kids.Count + " " + (other.Lent[1].Up!.Up == null ? "no" : "up")); });
        Do("the loan gone", static () => Leaf(s_tree!, 2, 0).Lent = null);
        // the list lent before anything is in it, then the leaf cut: the empty list lives by the second node
        Do("an empty loan, the lender cut", static () => { Leaf(s_tree!, 2, 0).Lent = Leaf(s_tree!, 0, 0).Kids; s_tree!.Kids[0].Kids.RemoveAt(0); });
        Do("the loan filled", static () => { var other = Leaf(s_tree!, 2, 0); other.Lent!.Add(new Node(2, ++s_ids) { Up = other }); });
        Do("the borrower cut", static () => s_tree!.Kids[2].Kids.RemoveAt(0));
    }

    // a part of another class: a reference back to the holder stored into it
    [MethodImpl(MethodImplOptions.NoInlining)]
    static Node WithBag(Node up, int kind)
    {
        return new Node(kind, ++s_ids) { Up = up, Bag = new Bag() };
    }
    static void Bags()
    {
        Do("bags", static () => { var top = s_tree!.Kids[1]; for (int i = 0; i < 4; i++) top.Kids.Add(WithBag(top, 3)); });
        Do("bags that refer back", static () =>
        {
            var top = s_tree!.Kids[1];
            var n = top.Kids[top.Kids.Count - 1];
            n.Bag!.Back = n;                         // the part refers to its holder
            var m = top.Kids[top.Kids.Count - 2];
            m.Bag!.Next = new Bag { Back = m };      // a new object with a reference up, into the part
            var k = top.Kids[top.Kids.Count - 3];
            k.Bag!.Back = top;                       // the part refers above its holder
        });
        Do("the bags' nodes cut", static () => { var top = s_tree!.Kids[1]; top.Kids.RemoveRange(top.Kids.Count - 4, 4); });
    }

    // the note is stale: the list taken out of its leaf, filled through a local
    static void Stale()
    {
        Do("a list taken out, filled, and back", static () =>
        {
            var a = Leaf(s_tree!, 1, 0).Kids[0];
            var l = a.Kids;
            a.Kids = null!;                          // the slot empty
            l.Add(new Node(4, ++s_ids) { Up = a });
            Console.WriteLine("a list taken out and filled: " + l.Count + " " + Node.Live[4]);
            a.Kids = l;                              // and back
        });
        Do("another list in the slot", static () =>
        {
            var b = Leaf(s_tree!, 1, 0).Kids[1];
            var lb = b.Kids;
            b.Kids = new List<Node>();               // another list in the slot
            lb.Add(new Node(4, ++s_ids) { Up = b });
            b.Kids.Add(new Node(4, ++s_ids) { Up = b });
            s_list = lb;
        });
        Do("the old list let go", static () => s_list = null);
        Do("the same list stored again", static () =>
        {
            var c = Leaf(s_tree!, 1, 1).Kids[0];
            var lc = c.Kids;
            c.Kids = lc;                             // the same list stored again
            lc.Add(new Node(4, ++s_ids) { Up = c });
        });
        Do("a list taken out for good and filled", static () =>
        {
            var d = Leaf(s_tree!, 1, 1).Kids[1];
            var ld = d.Kids;
            d.Kids = null!;
            ld.Add(new Node(4, ++s_ids) { Up = d });
            s_list = ld;                             // the leaf lives by the list from here on
            d.Kids = new List<Node>();
        });
        Do("its leaf cut", static () => Leaf(s_tree!, 1, 1).Kids.RemoveAt(1));
        Do("the list let go", static () => s_list = null);
        Do("their subtree replaced", static () => s_tree!.Kids[1] = new Node(0, ++s_ids) { Up = s_tree });
    }

    // a leaf that left its group (nothing refers to it but code, it refers to no member) is filled as a plain object
    static void Left()
    {
        Do("a leaf out of its group, grown", static () =>
        {
            s_list = new List<Node>();
            var hub = new Node(7, ++s_ids);
            s_keep = hub;
            for (int i = 0; i < 5; i++) hub.Kids.Add(new Node(7, ++s_ids));     // no parent: nothing of theirs is linked
            var x = hub.Kids[4];
            hub.Kids.RemoveAt(4);                                               // held by the local alone
            Grow(x, 3, 7);
            s_list.Add(x);
        });
        Do("and let go", static () => s_list!.Clear());
        Do("a leaf in two lists, grown, out of one", static () =>
        {
            var y = s_keep!.Kids[0];
            s_list!.Add(y);                                                     // in two lists
            Grow(y, 2, 7);
            s_keep.Kids.RemoveAt(0);
        });
        Do("out of both", static () => s_list = null);
        Do("the hub gone", static () => s_keep = null);
    }

    // weak references to parts: every one dead when a finalizer of the group that goes runs
    static Watch WatchTree(Watch? up, int depth, int group, bool weakBefore)
    {
        var e = new Watch { Up = up, Id = ++s_ids, Group = group };
        if (weakBefore && depth == 0) Watch.Weak[group].Add(new WeakReference<List<Watch>>(e.Kids));
        if (depth > 0) for (int i = 0; i < 3; i++) e.Kids.Add(WatchTree(e, depth - 1, group, weakBefore));
        return e;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void WeakAfter(Watch w) { if (w.Kids.Count == 0) Watch.Weak[w.Group].Add(new WeakReference<List<Watch>>(w.Kids)); else foreach (var k in w.Kids) WeakAfter(k); }
    [MethodImpl(MethodImplOptions.NoInlining)]
    static int Alive(int group) { int n = 0; foreach (var w in Watch.Weak[group]) if (w.TryGetTarget(out _)) n++; return n; }
    static void SayWatch(string when)
    {
        for (int i = 0; i < 4; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        Console.WriteLine(when + ": weak " + Watch.Weak[0].Count + " " + Watch.Weak[1].Count + " alive " + Alive(0) + " " + Alive(1) + " ran " + Watch.Ran + " saw alive " + Watch.SawAlive + " bells " + Bell.Rang + " holders " + Node.Live[6]);
    }
    static void DoW(string when, Action step) { step(); SayWatch(when); }
    static void Weaks()
    {
        DoW("watched", static () =>
        {
            s_watch = new Watch { Group = 2 };
            s_watch.Kids.Add(WatchTree(s_watch, 2, 0, true));      // weak handles before the lists' leaves join
            s_watch.Kids.Add(WatchTree(s_watch, 2, 1, false));
        });
        DoW("weak handles for parts", static () => WeakAfter(s_watch!.Kids[1]));
        DoW("a watched leaf grown", static () => { var l = s_watch!.Kids[1].Kids[2].Kids[0]; l.Kids.Add(new Watch { Up = l, Group = 1 }); });
        DoW("the first watched subtree replaced", static () => s_watch!.Kids[0] = new Watch { Up = s_watch, Group = 2 });
        DoW("the second", static () => s_watch!.Kids[1] = new Watch { Up = s_watch, Group = 2 });
        DoW("the watched tree gone", static () => s_watch = null);
    }

    // a part of a class with a finalizer, and lists an array and a static hold as well
    static Holder HolderTree(Holder? up, int depth)
    {
        var e = new Holder { Up = up };
        if (depth > 0) for (int i = 0; i < 3; i++) e.Kids.Add(HolderTree(e, depth - 1));
        return e;
    }
    static List<Holder>[]? s_arr;
    static void Bells()
    {
        DoW("holders", static () =>
        {
            s_holders = HolderTree(null, 2);
            s_holders.Kids[1].Kids[1].Bell.Back = s_holders.Kids[1].Kids[1];
        });
        DoW("a subtree of holders replaced", static () => s_holders!.Kids[0] = HolderTree(s_holders, 1));
        DoW("lists in an array", static () =>
        {
            s_arr = new List<Holder>[2];
            s_arr[0] = s_holders!.Kids[1].Kids[0].Kids;
            s_arr[1] = s_holders.Kids[2].Kids[0].Kids;
            s_arr[0].Add(new Holder { Up = s_holders.Kids[1].Kids[0] });
            s_holders.Kids.RemoveAt(1);
        });
        DoW("one list left", static () => { Console.WriteLine(s_arr![0].Count + " " + s_arr[1].Count); s_arr[0] = null!; s_holders = null; });
        DoW("the last list filled", static () => s_arr![1].Add(new Holder()));
        DoW("gone", static () => s_arr = null);
    }

    // frames of the scene's kind: a subtree replaced, a leaf group moved, now and then a leaf grown
    static uint s_seed = 12345;
    static uint Next() { s_seed = s_seed * 1664525u + 1013904223u; return s_seed >> 8; }
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Frame(int f)
    {
        var a = s_tree!.Kids[(int)(Next() % 3)];
        var b = a.Kids[(int)(Next() % 3)];
        int bi = (int)(Next() % 3);
        if (bi < b.Kids.Count) b.Kids[bi] = Tree(b, 1, 3); else b.Kids.Add(Tree(b, 1, 3));
        var c = s_tree.Kids[(int)(Next() % 3)].Kids[(int)(Next() % 3)];
        if (c.Kids.Count > 0)
        {
            var moved = c.Kids[c.Kids.Count - 1];
            c.Kids.RemoveAt(c.Kids.Count - 1);
            var to = s_tree.Kids[(int)(Next() % 3)];
            moved.Up = to;
            to.Kids.Add(moved);
            if (f % 7 == 0 && moved.Kids.Count > 0) Grow(moved.Kids[0], 2, 1);
            if (f % 11 == 0 && moved.Kids.Count > 1) { var l = moved.Kids[1].Kids; moved.Kids[0].Lent = l; l.Add(new Node(2, ++s_ids) { Up = moved.Kids[1] }); }
        }
    }
    static void Frames()
    {
        Do("frames", static () => s_tree = Tree(null, 4, 3));
        for (int f = 0; f < 300; f++)
        {
            Frame(f);
            if (f % 50 == 49) Say("frame " + f);
        }
        Do("frames gone", static () => s_tree = null);
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        LaterChildren();
        Lent();
        Bags();
        Stale();
        Do("tree gone", static () => s_tree = null);
        Left();
        Weaks();
        Bells();
        Frames();
    }
}
