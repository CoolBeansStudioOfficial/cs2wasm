// known-gap: cycles through closures, events, tasks, and references inside structs (tuples, dictionary entries) are never freed
// Objects held every ordinary way, then let go: each kind must be gone afterwards (counted by finalizers, as .NET's
// collector leaves none). Cycles of every shape among them: parent links, events, closures, collections that hold
// their owner, structs with references, tasks and their continuations.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

class Tracked
{
    // (counted with Interlocked: .NET runs finalizers on a thread of their own)
    public static readonly List<string> Kinds = new();
    public static readonly int[] Live = new int[64];
    readonly int _kind;
    public Tracked(string kind)
    {
        int k = Kinds.IndexOf(kind);
        if (k < 0) { k = Kinds.Count; Kinds.Add(kind); }
        _kind = k;
        System.Threading.Interlocked.Increment(ref Live[k]);
    }
    ~Tracked() { System.Threading.Interlocked.Decrement(ref Live[_kind]); }
}

class Node : Tracked
{
    public Node Parent, Next; public List<Node> Children = new(); public Node[] Slots = new Node[2];
    public Dictionary<string, Node> ByName = new(); public (Node other, int n) Pair; public Node? Maybe;
    public Action Callback; public event Action<Node> Changed; public Func<Node> Lazy; public object Tag;
    public WeakReference<Node> Weak;
    public Node(string kind) : base(kind) { }
    public void Raise() => Changed?.Invoke(this);
    public void Listen(Node to) { to.Changed += OnChanged; }
    void OnChanged(Node n) { }
}

struct Holder { public Node N; public List<Holder> More; }

static class P
{
    static readonly List<Node> s_keep = new();
    static Node s_static;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Shapes()
    {
        // a tree with parent links, through a list
        var root = new Node("tree");
        for (int i = 0; i < 3; i++) { var c = new Node("tree") { Parent = root }; root.Children.Add(c); for (int j = 0; j < 2; j++) c.Children.Add(new Node("tree") { Parent = c }); }
        // a ring, and a doubly linked list
        Node first = new Node("ring"), prev = first;
        for (int i = 0; i < 4; i++) { var n = new Node("ring") { Parent = prev }; prev.Next = n; prev = n; }
        prev.Next = first; first.Parent = prev;
        // itself
        var self = new Node("self"); self.Next = self; self.Slots[0] = self; self.Children.Add(self); self.ByName["me"] = self; self.Tag = self;
        // through arrays and dictionaries
        var a = new Node("array"); var b = new Node("array"); a.Slots[1] = b; b.Slots[0] = a; a.ByName["b"] = b; b.ByName["a"] = a;
        // through a tuple field, a nullable-annotated field, an object field holding an array of them
        var t1 = new Node("tuple"); var t2 = new Node("tuple"); t1.Pair = (t2, 1); t2.Pair = (t1, 2); t1.Maybe = t2; t2.Tag = new object[] { t1, new[] { t2 } };
        // closures that capture their owner, and each other
        var c1 = new Node("closure"); var c2 = new Node("closure");
        c1.Callback = () => c2.Raise(); c2.Callback = () => c1.Raise(); c1.Lazy = () => c1; c2.Lazy = () => c2.Lazy();
        // events: a subscriber that the publisher holds, and the other way round
        var pub = new Node("event"); var sub = new Node("event"); sub.Listen(pub); pub.Listen(sub); pub.Changed += n => sub.Raise(); sub.Parent = pub;
        pub.Raise();
        // a struct that holds its owner's kind
        var h = new Node("struct"); var hs = new Holder { N = h, More = new List<Holder>() }; hs.More.Add(new Holder { N = new Node("struct") { Parent = h } }); h.Tag = hs;
        // weak references do not keep anything
        var w = new Node("weak"); w.Weak = new WeakReference<Node>(w); var w2 = new Node("weak"); w2.Weak = new WeakReference<Node>(w);
        // a part cut off a structure that lives on
        var kept = new Node("kept"); s_keep.Add(kept);
        for (int i = 0; i < 3; i++) { var c = new Node("cut") { Parent = kept }; kept.Children.Add(c); c.Children.Add(new Node("cut") { Parent = c }); }
        kept.Children.Clear();
        var chain = new Node("cut"); kept.Next = chain; chain.Parent = kept; chain.Next = new Node("cut") { Parent = chain }; kept.Next = null;
        // replaced values in a static and in a kept collection
        s_static = new Node("static"); s_static.Next = new Node("static") { Parent = s_static }; s_static = new Node("static-kept");
        kept.ByName["x"] = new Node("replaced") { Parent = kept }; kept.ByName["x"] = null; kept.Slots[0] = new Node("replaced") { Parent = kept }; kept.Slots[0] = null;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Temporaries()
    {
        // LINQ over objects, sorted, grouped, joined, with captured owners
        var items = Enumerable.Range(0, 20).Select(i => new Node("linq") { Tag = i }).ToList();
        var owner = new Node("linq");
        var picked = items.Where(n => (int)n.Tag % 2 == 0).OrderByDescending(n => (int)n.Tag).GroupBy(n => (int)n.Tag % 3).SelectMany(g => g.Select(n => (n, owner))).ToDictionary(p => p.n, p => p.owner);
        owner.Tag = picked;
        // iterators left half way
        IEnumerable<Node> Gen() { var mine = new Node("iterator"); for (int i = 0; i < 5; i++) yield return new Node("iterator") { Parent = mine }; }
        foreach (var n in Gen()) { if ((object)n.Parent == null) break; break; }
        using (var e = Gen().GetEnumerator()) { e.MoveNext(); e.MoveNext(); }
        var lazy = Gen(); var en = lazy.GetEnumerator(); en.MoveNext();
        // exceptions that carry objects, caught, rethrown, wrapped
        try { try { throw new InvalidOperationException("x") { Data = { ["n"] = new Node("exception") } }; } catch (Exception e) { throw new ApplicationException("outer", e) { Data = { ["m"] = new Node("exception") } }; } }
        catch (Exception) { }
        // strings, boxes, arrays of arrays, dictionaries of lists
        var big = new Dictionary<int, List<Node[]>>();
        for (int i = 0; i < 5; i++) big[i] = new List<Node[]> { new[] { new Node("nested"), new Node("nested") } };
        big[0][0][0].Tag = big;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Tasks()
    {
        static async Task<Node> MakeAsync(Node parent) { await Task.Yield(); var n = new Node("async") { Parent = parent }; await Task.Delay(1); parent.Children.Add(n); return n; }
        var root = new Node("async");
        var all = Task.WhenAll(Enumerable.Range(0, 4).Select(_ => MakeAsync(root))).GetAwaiter().GetResult();
        root.Tag = all;
        var tcs = new TaskCompletionSource<Node>(); var waiter = new Node("pending");
        tcs.Task.ContinueWith(t => waiter.Raise());   // never completed: dropped with its continuation
        waiter.Tag = tcs;
        var done = Task.FromResult(new Node("async")); done.ContinueWith(t => t.Result.Raise()).Wait();
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        Shapes(); Temporaries(); Tasks();
        for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        foreach (var kind in Tracked.Kinds.OrderBy(k => k, StringComparer.Ordinal)) Console.WriteLine(kind + " " + Tracked.Live[Tracked.Kinds.IndexOf(kind)]);
    }
}
