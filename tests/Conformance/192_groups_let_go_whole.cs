// A group of objects that can be on a cycle, none with a finalizer, is let go whole (Rt.ArenaFree: its members'
// references released as its ring is walked, then the members freed from a list), and the stores that build it give
// the store's count back as they link (Rt.EarlyGiveBack). The states those paths can meet: plain objects with
// finalizers held by the members, whose finalizers run program code while the group goes (they allocate, make and
// drop a group of their own, let another group go, ask a weak reference to a member that has not been cleared yet);
// a member that holds the last reference to a member of another group; a dictionary and arrays with holes among the
// members; an array too large for a page of small blocks; an object in a static readonly field (in no group) in the
// members' lists; two groups made apart and joined; trees built from the leaves up, from the root down, with the
// parent set after the child is in its list, a child in two lists, a root stored into its own descendant.
// The nodes have no finalizer: each holds a tag that has one and counts. What .NET frees must be freed, and nothing
// before .NET frees it (a use after free shows in the checked build).
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

// a tag whose finalizer runs program code while the group that held it goes
class Busy : Tag
{
    public static Node Other;
    public static WeakReference<Node> Weak;
    public static int Ran, Asked;
    public List<string> Scratch;
    public Busy(string kind) : base(kind) { }
    protected override void Last()
    {
        Ran++;
        // (allocations while the members of the group wait to be freed: none of them may get a member's memory)
        Scratch = new List<string>();
        for (int i = 0; i < 60; i++) Scratch.Add("scratch " + i);
        // (a group made and let go inside the finalizer)
        var t = new Node("late");
        for (int i = 0; i < 6; i++) t.Kids.Add(new Node("late", i) { Up = t });
        t = null;
        // (another group let go from here)
        Other = null;
        // (a weak reference to a member of the group that is going: whether it still answers is not .NET's to say
        // the same way, so nothing is printed of it; it must not break anything)
        if (Weak != null) { if (Weak.TryGetTarget(out var n)) Asked += n.Id >= 0 ? 1 : 0; else Asked++; }
    }
}

// (another kind of group: a Loop refers to no Node)
sealed class Loop
{
    public Loop Self; public List<Loop> More = new(); public Tag Tag;
    public Loop(string kind) { Tag = new Tag(kind); Self = this; }
}

sealed class Node
{
    public Node Up, Next; public List<Node> Kids = new(); public List<Node> Others; public Dictionary<int, Node> Map; public Node[] Raw;
    public object Any; public Loop Friend; public Tag Tag; public int Id;
    public Node(string kind, int id = 0, bool busy = false) { Tag = busy ? new Busy(kind) : new Tag(kind); Id = id; }
    public long Sum() { long s = Id; foreach (var c in Kids) if (c != null && c.Up == this) s += c.Sum(); return s; }
}

static class P
{
    // (in no group: the object of a static readonly field; its class has a static constructor, so both make it at its first use)
    static class Pins { public static readonly Node Pinned; static Pins() { Pinned = new Node("pinned", 7); } }
    static Node s_pinned => Pins.Pinned;
    static Node s_keep, s_keep2;
    static Loop s_loop;

    static Node Tree(string kind, Node up, int depth, int fan)
    {
        var e = new Node(kind, depth) { Up = up };
        if (depth > 0) for (int i = 0; i < fan; i++) e.Kids.Add(Tree(kind, e, depth - 1, fan));
        return e;
    }

    static Node TopDown(string kind, int depth, int fan)
    {
        var e = new Node(kind, depth);
        if (depth > 0) for (int i = 0; i < fan; i++) { var c = TopDown(kind, depth - 1, fan); e.Kids.Add(c); c.Up = e; }
        return e;
    }

    // a ring with finalizers that run code as it goes
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Ring()
    {
        Busy.Other = Tree("other", null, 2, 4);
        var first = new Node("ring");
        var cur = first;
        Node late = null;
        for (int i = 1; i < 50; i++)
        {
            var x = new Node("ring", i, busy: i == 3 || i == 20 || i == 49) { Up = cur };
            cur.Next = x; first.Kids.Add(x);
            if (i == 40) late = x;
            cur = x;
        }
        cur.Next = first; first.Up = cur;
        Busy.Weak = new WeakReference<Node>(late);
        long s = 0; var w = first; do { s += w.Id; w = w.Next; } while (w != first);
        Console.WriteLine("ring sum " + s + " kids " + first.Kids.Count);
    }

    // trees built from the leaves up and from the root down; an array too large for a page; kept, then let go
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Trees()
    {
        var a = Tree("up", null, 3, 5);
        var b = TopDown("down", 3, 4);
        var wide = new Node("wide");
        for (int i = 0; i < 3000; i++) wide.Kids.Add(new Node("wide", i) { Up = wide });
        wide.Raw = new Node[5000];
        for (int i = 0; i < 5000; i += 7) wide.Raw[i] = wide.Kids[i % 3000];
        a.Kids[2].Kids[1].Others = wide.Kids;
        b.Kids[0].Any = a.Kids[4];
        Console.WriteLine("trees " + a.Sum() + " " + b.Sum() + " " + wide.Sum() + " capacity " + wide.Kids.Capacity);
        s_keep = a.Kids[2].Kids[1].Kids[3];
        s_keep2 = b.Kids[3].Kids[3];
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void TreesKept()
    {
        Console.WriteLine("kept " + s_keep.Up.Up.Up.Sum() + " others " + s_keep.Up.Others.Count + " any " + ((Node)s_keep2.Up.Up.Kids[0].Any).Sum());
        s_keep = null;
        Tracked.Report("trees, one kept");
        s_keep2 = null;
    }

    // a dictionary, arrays with holes, an object of a static readonly field, strings and numbers among the members
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Maps()
    {
        var root = new Node("maps");
        root.Map = new Dictionary<int, Node>();
        root.Raw = new Node[40];
        for (int i = 0; i < 40; i++)
        {
            var n = new Node("maps", i) { Up = root };
            root.Map[i] = n;
            if (i % 3 != 0) root.Raw[i] = n;
            if (i % 5 == 0) { root.Kids.Add(s_pinned); n.Any = s_pinned; n.Raw = new Node[] { s_pinned, null, n, root }; }
            else if (i % 5 == 1) n.Any = "text " + i;
            else if (i % 5 == 2) n.Any = i;
            else n.Any = root.Map;
            n.Map = i % 7 == 0 ? root.Map : null;
            root.Kids.Add(n);
        }
        for (int i = 0; i < 40; i += 4) root.Map.Remove(i);
        Console.WriteLine("maps " + root.Sum() + " map " + root.Map.Count + " kids " + root.Kids.Count + " pinned " + root.Kids.Count(k => k == s_pinned));
    }

    // members that hold members of another kind of group: the last reference to one, one of several, one a static keeps
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Friends()
    {
        var root = new Node("friends");
        var shared = new Loop("loop");
        s_loop = new Loop("loop");
        s_loop.More.Add(new Loop("loop"));
        for (int i = 0; i < 30; i++)
        {
            var n = new Node("friends", i) { Up = root };
            n.Friend = (i % 3) switch { 0 => new Loop("loop"), 1 => shared, _ => s_loop };
            if (i % 6 == 0) { n.Friend.More.Add(new Loop("loop")); n.Friend.More.Add(n.Friend); }
            root.Kids.Add(n);
        }
        shared = null;
        Console.WriteLine("friends " + root.Sum() + " loops " + root.Kids.Count(k => k.Friend.Self == k.Friend));
    }

    // two groups made apart, each large enough to keep its own record when they are joined; then both let go
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Joined()
    {
        var a = Tree("joined", null, 2, 6);
        var b = Tree("joined", null, 2, 5);
        var c = TopDown("joined", 2, 3);
        a.Kids[1].Others = b.Kids;
        c.Kids[0].Kids[0].Up = b;
        b.Kids[2].Any = c;
        Console.WriteLine("joined " + a.Sum() + " " + b.Sum() + " " + c.Sum());
    }

    static Node s_root;
    static Node Take() { var r = s_root; s_root = null; return r; }

    // the stores' states: a child in two lists, the parent set after, a root stored into its own descendant by its
    // last reference, a list's item replaced, a part let go that still refers to itself while the group lives on
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Stores()
    {
        var root = new Node("stores");
        var other = new Node("stores", 1);
        for (int i = 0; i < 12; i++)
        {
            var n = new Node("stores", i);
            root.Kids.Add(n);
            if (i % 2 == 0) other.Kids.Add(n);
            n.Up = i % 3 == 0 ? other : root;
        }
        other.Up = root; root.Kids.Add(other);
        // (two siblings that refer to each other, taken out of their list: a part that is let go with the group)
        var p = new Node("stores", 50) { Up = root }; var q = new Node("stores", 51) { Up = root };
        p.Next = q; q.Next = p;
        root.Kids.Add(p); root.Kids.Add(q);
        root.Kids.Remove(p); root.Kids.Remove(q);
        p = null; q = null;
        for (int i = 0; i < 9; i++) root.Kids.Add(Tree("stores", root, 1, 3));
        root.Kids[3] = Tree("stores", root, 2, 2);
        root.Kids.Insert(0, TopDown("stores", 1, 2));
        Console.WriteLine("stores " + root.Sum() + " " + other.Kids.Count);
        // (the root's last reference stored into a descendant's field: the tree refers to itself alone then)
        s_root = Tree("turned", null, 2, 3);
        var leaf = s_root.Kids[1].Kids[2];
        leaf.Next = Take();
        Console.WriteLine("turned " + leaf.Next.Sum());
        leaf = null;
        s_root = Tree("turned", null, 1, 4);
        s_root.Kids[0].Up = Take();
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        Ring();
        Tracked.Report("ring");
        Console.WriteLine("finalizers that ran code " + Busy.Ran + " asked " + Busy.Asked);
        Trees();
        Tracked.Report("trees");
        TreesKept();
        Tracked.Report("trees, let go");
        Maps();
        Tracked.Report("maps");
        Friends();
        Tracked.Report("friends");
        s_loop = null;
        Tracked.Report("friends, let go");
        Joined();
        Tracked.Report("joined");
        Stores();
        Tracked.Report("stores");
        Console.WriteLine("pinned " + s_pinned.Id);
    }
}
