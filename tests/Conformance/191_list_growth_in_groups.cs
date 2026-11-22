// A list of objects that can be on a cycle grows (Rt.GrowInto: the new array takes the items and the old array's place
// in one step) in the states its items can be in: owned by the list alone, with a parent field above them, held by
// code from outside meanwhile, in the list twice, in two lists, null, beside strings and numbers in a list of
// anything, the list its own item's field, a list that is no member of a group yet, a capacity set by hand, trimmed, and a growth while a
// finalizer of the group runs. What .NET frees must be freed, and nothing before .NET frees it (finalizers count what
// lives; a use after free shows in the checked build).
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
        for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        Console.WriteLine(when + ": " + string.Join(", ", Kinds.Select((k, i) => (k, i)).OrderBy(x => x.k, StringComparer.Ordinal).Select(x => x.k + " " + Live[x.i])));
    }
}

class Node : Tracked
{
    public Node Parent; public List<Node> Children = new(); public List<Node> Others; public List<object> Bag; public int Id;
    public Node(string kind, int id = 0) : base(kind) { Id = id; }
    public long Sum() { long s = Id; foreach (var c in Children) if (c != null) s += c.Sum(); return s; }
}

// (not of the group: its finalizer runs while the group is still there, and the list grows in it)
class Grower : Tracked
{
    public List<Node> Into; public Node Root;
    public Grower() : base("grower") { }
    protected override void Last() { if (Into != null) for (int i = 0; i < 9; i++) Into.Add(new Node("late", i) { Parent = Root }); }
}

// (of the group: its finalizer runs as the group goes, and a list of the group that is still empty gets its first array)
class LastWord : Node
{
    public LastWord() : base("lastword") { }
    protected override void Last() { for (int i = 0; i < 5; i++) Children.Add(new Node("late", i)); }
}

static class P
{
    static Node s_keep;
    static List<Node> s_list;

    static Node Tree(string kind, Node parent, int depth, int fan)
    {
        var e = new Node(kind, depth) { Parent = parent };
        if (depth > 0) for (int i = 0; i < fan; i++) e.Children.Add(Tree(kind, e, depth - 1, fan));
        return e;
    }

    // items owned by the list alone, with their parent above them: 4, 8, 16, 32
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Plain()
    {
        var root = Tree("plain", null, 2, 33);
        Console.WriteLine("plain sum " + root.Sum() + " count " + root.Children.Count + " capacity " + root.Children.Capacity);
        s_keep = root.Children[7].Children[20];
    }

    // items held by code while their list grows, and after the list is gone
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Held()
    {
        var root = new Node("held");
        var held = new Node[20];
        for (int i = 0; i < 20; i++) { held[i] = new Node("held", i) { Parent = root }; root.Children.Add(held[i]); if (i % 3 == 0) held[i].Children.Add(new Node("held", 100 + i) { Parent = held[i] }); }
        var first = root.Children[0];
        for (int i = 0; i < 30; i++) root.Children.Add(new Node("held", 200 + i) { Parent = root });
        Console.WriteLine("held sum " + root.Sum() + " first " + first.Id + " parent " + (first.Parent == root));
        root = null;
        Tracked.Report("held, root let go");
        Console.WriteLine("held parents " + held.Count(h => h.Parent != null && h.Parent.Children.Count == 50));
    }

    // the same item twice, an item of two lists, nulls, leaves that cannot be on a cycle
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Mixed()
    {
        var root = new Node("mixed");
        var twice = new Node("mixed", 1) { Parent = root };
        var other = new Node("mixed", 2);
        other.Others = new List<Node>();
        root.Bag = new List<object>();
        for (int i = 0; i < 40; i++)
        {
            Node n = (i % 4) switch { 0 => twice, 1 => null, 2 => new Node("mixed", i) { Parent = root }, _ => new Node("mixed", i) { Parent = other } };
            root.Children.Add(n);
            if (i % 2 == 0 && n != null) other.Others.Add(n);
            // (a list of anything: members of the group, strings, numbers, itself)
            root.Bag.Add(i < 9 ? new Node("bag", i) { Parent = root } : (i % 3) switch { 0 => "text " + i, 1 => (object)i, _ => i == 20 ? root.Bag : new Node("bag", i) });
        }
        root.Others = other.Others;
        Console.WriteLine("mixed sum " + root.Sum() + " others " + other.Others.Count + " nulls " + root.Children.Count(c => c == null)
            + " bag " + root.Bag.Count + " nodes " + root.Bag.OfType<Node>().Sum(n => n.Id) + " texts " + root.Bag.OfType<string>().Count());
        s_list = other.Others;
    }

    // the list is its own items' field; a list that belongs to nothing; a capacity set by hand; trimmed
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Shapes()
    {
        var root = new Node("shapes");
        for (int i = 0; i < 20; i++) { var n = new Node("shapes", i); n.Others = root.Children; root.Children.Add(n); }
        var loose = new List<Node>();
        for (int i = 0; i < 20; i++) { var n = new Node("shapes", i) { Parent = root }; n.Others = loose; loose.Add(n); }
        root.Children.Capacity = 64;
        root.Children.EnsureCapacity(200);
        for (int i = 0; i < 5; i++) root.Children.Add(Tree("shapes", root, 1, 5));
        root.Children.TrimExcess();
        loose.Capacity = 21;
        loose.TrimExcess();
        root.Children.InsertRange(3, loose);
        root.Children.RemoveRange(10, 12);
        root.Children.TrimExcess();
        Console.WriteLine("shapes sum " + root.Sum() + " count " + root.Children.Count + " capacity " + root.Children.Capacity + " loose " + loose.Capacity);
        Tracked.Report("shapes, inside");
    }

    // growths in finalizers: of an object outside the group, and of a member as the group goes
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Late()
    {
        var root = new Node("fin");
        for (int i = 0; i < 4; i++) root.Children.Add(new Node("fin", i) { Parent = root });
        var g = new Grower { Into = root.Children, Root = root };
        root.Others = new List<Node>();
        root.Others.Add(root);
        root.Others.Add(new LastWord { Parent = root });
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Kept()
    {
        Console.WriteLine("kept " + s_keep.Id + " root children " + s_keep.Parent.Parent.Children.Count);
        s_keep = null;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Listed()
    {
        Console.WriteLine("list " + s_list.Count + " sum " + s_list.Sum(n => n.Id));
        s_list = null;
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        Plain();
        Tracked.Report("plain");
        Kept();
        Tracked.Report("plain, let go");
        Held();
        Tracked.Report("held");
        Mixed();
        Tracked.Report("mixed");
        Listed();
        Tracked.Report("mixed, let go");
        Shapes();
        Tracked.Report("shapes");
        Late();
        Tracked.Report("late");
    }
}
