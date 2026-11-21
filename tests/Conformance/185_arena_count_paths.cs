// A member's first and last outside reference, and the stores into members' slots, in each case their usual path
// leaves to the full one (docs/FINISH.md stream N: the usual cases are written without calls): a member that is not
// owned (the check at the drop), the arena's last member referred to from outside, a member nothing inside refers to
// any more, an arena merged into another, a member with a failed check's note, members with up-references below them,
// stores by address into fields and array elements. After each part is let go, a collection: what .NET frees must be
// freed by then (finalizers count what lives).
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
        int k = Kinds.IndexOf(kind);
        if (k < 0) { k = Kinds.Count; Kinds.Add(kind); }
        _kind = k;
        System.Threading.Interlocked.Increment(ref Live[k]);
    }
    ~Tracked() { System.Threading.Interlocked.Decrement(ref Live[_kind]); }
    public static void Report(string when)
    {
        for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        Console.WriteLine(when + ": " + string.Join(", ", Kinds.Select((k, i) => (k, i)).OrderBy(x => x.k, StringComparer.Ordinal).Select(x => x.k + " " + Live[x.i])));
    }
}

// a red node's shape: a readonly parent, children made into the parent's slots
sealed class Red : Tracked
{
    public readonly Red? Parent;
    readonly Red?[] _kids;
    public Red(Red? parent, int width, string kind) : base(kind) { Parent = parent; _kids = new Red?[width]; }
    public Red Child(int i, int width) => _kids[i] ??= new Red(this, width, Kind);
    public string Kind => Parent?.Kind ?? "red";
    public void Drop(int i) => _kids[i] = null;
    public int Width => _kids.Length;
}

// a tree whose parents are set later (up-references below the children)
sealed class Up : Tracked
{
    public Up? Parent;
    public readonly List<Up> Children = new();
    public Up() : base("up") { }
    public Up Add() { var c = new Up(); c.Parent = this; Children.Add(c); return c; }
}

// a subtree held twice inside its tree (a list and a dictionary): kept by holds alone when let go (the check at the drop)
sealed class Doubly : Tracked
{
    public readonly List<Doubly> Kids = new();
    public readonly Dictionary<int, Doubly> ById = new();
    public Doubly? Owner;
    public Doubly() : base("doubly") { }
    public Doubly Add(int id) { var c = new Doubly { Owner = this }; Kids.Add(c); ById[id] = c; return c; }
}

struct Pair { public Red? Node; public int N; }

sealed class Holder : Tracked
{
    public Pair Current;
    public Pair[] Stack = new Pair[8];
    public Red? Last;
    public Holder() : base("holder") { }
}

static class P
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    static long Walk(Red r, int depth, Holder h)
    {
        long s = 1;
        for (int i = 0; i < r.Width; i++)
        {
            var c = r.Child(i, depth > 0 ? r.Width : 0);
            // stores by address: a struct field of a member, an array of structs' element
            h.Current = new Pair { Node = c, N = i };
            h.Stack[i % h.Stack.Length] = h.Current;
            h.Last = c;
            if (depth > 0) s += Walk(c, depth - 1, h);
        }
        return s;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static long RedTrees()
    {
        // first and last outside references of owned members, again and again (the walk's locals)
        var a = new Red(null, 4, "red a");
        var h = new Holder();
        long s = Walk(a, 3, h) + Walk(a, 3, h);
        // a part let go: its members lose their last references from inside
        a.Drop(1);
        s += Walk(a, 2, h);
        // two trees' arenas merged by a store, then let go together
        var b = new Red(null, 3, "red b");
        s += Walk(b, 2, h);
        h.Last = b;
        h.Stack[0] = new Pair { Node = a.Child(0, 4), N = 0 };
        h.Stack[1] = new Pair { Node = b.Child(0, 3), N = 1 };
        h.Current = default; h.Stack = new Pair[2]; h.Last = null;
        return s;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static long UpTrees()
    {
        var root = new Up();
        for (int i = 0; i < 20; i++) { var c = root.Add(); for (int j = 0; j < 5; j++) c.Add(); }
        long s = 0;
        // members with up-references below them, held from locals and let go
        foreach (var c in root.Children) { var keep = c.Children[2]; s += keep.Children.Count + (keep.Parent == c ? 1 : 0); }
        // a child let go while its parent lives, then the parent
        var gone = root.Children[3];
        root.Children.RemoveAt(3);
        gone.Parent = null;
        return s;
    }

    // (in statics: a local of Main may stay alive to its end in .NET's build)
    static Doubly? s_root, s_held;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void DoublyTree()
    {
        var root = new Doubly();
        for (int i = 0; i < 6; i++) { var c = root.Add(i); for (int j = 0; j < 4; j++) c.Add(j); }
        // a member of a subtree held from outside while the subtree is let go: its check fails, and is made again
        // when that last outside reference goes
        s_held = root.Kids[2].Kids[1];
        var sub = root.Kids[2];
        root.Kids.RemoveAt(2);
        root.ById.Remove(2);
        sub.Owner = null;
        s_root = root;
    }

    // (a member held from outside when its subtree was let go, so the check failed; then taken out of that subtree
    // and put elsewhere, then let go: the subtree it left must go then)
    static Doubly? s_moved, s_elsewhere;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void MovedAway()
    {
        var root = new Doubly();
        for (int i = 0; i < 4; i++) { var c = root.Add(i); for (int j = 0; j < 3; j++) c.Add(j).Add(0); }
        var sub = root.Kids[1];
        s_moved = sub.Kids[2];
        root.Kids.RemoveAt(1);
        root.ById.Remove(1);
        sub.Owner = null;
        s_elsewhere = new Doubly();
        s_elsewhere.Kids.Add(s_moved);
        sub.Kids.RemoveAt(2);
        sub.ById.Remove(2);
        s_moved.Owner = s_elsewhere;
        GC.KeepAlive(root);
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        Console.WriteLine(RedTrees());
        Tracked.Report("red trees let go");
        Console.WriteLine(UpTrees());
        Tracked.Report("up trees let go");
        DoublyTree();
        Tracked.Report("a subtree let go, one of it held");
        Console.WriteLine(s_held!.Kids.Count);
        s_held = null;
        Tracked.Report("that one let go too");
        Console.WriteLine(s_root!.Kids.Count);
        s_root = null;
        Tracked.Report("all let go");
        MovedAway();
        Tracked.Report("a held member moved away from a let-go subtree");
        Console.WriteLine(s_moved!.Kids.Count);
        s_moved = null;
        Tracked.Report("and let go");
        s_elsewhere = null;
        Tracked.Report("its new place let go");
    }
}
