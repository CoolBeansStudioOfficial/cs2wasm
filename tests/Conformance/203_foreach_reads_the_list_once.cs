// `foreach` over a List<T> written with the size's test first, the list's fields read before the loop where nothing
// the loop runs stores them, and the changed list's throw on an enumerator made on the spot (docs/AUDIT.md A3.5, the
// build, round 10): a list changed by the body, by what the body calls, through another reference and through another
// instantiation; changed in the last round and after it; a loop left early after a change; a change in a callee far
// below, seen when the walk comes back; the exception's frames; the stack after a catch in the function that loops.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

sealed class Node
{
    public string Id = "";
    public List<Node> Kids = new();
    public float X, Y;
    public int V;
}

struct Wide { public long A, B, C; public string S; }

static class Program
{
    static List<Node>? s_alias;
    static List<object>? s_objects;
    static Node? s_root;
    static int s_calls;

    static string Try(Action a)
    {
        try { a(); return "ok"; }
        catch (Exception e) { return e.GetType().Name + ": " + e.Message; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)] static void AddTo(List<Node> l) => l.Add(new Node { Id = "added" });
    [MethodImpl(MethodImplOptions.NoInlining)] static void AddAlias() => s_alias!.Add(new Node { Id = "alias" });
    [MethodImpl(MethodImplOptions.NoInlining)] static void AddObject() => s_objects!.Add("o");
    [MethodImpl(MethodImplOptions.NoInlining)] static void Nothing(Node n) { s_calls += n.V; }
    [MethodImpl(MethodImplOptions.NoInlining)] static void TrimAlias() => s_alias!.RemoveAt(s_alias.Count - 1);
    [MethodImpl(MethodImplOptions.NoInlining)] static void SetCapacity(List<Node> l) => l.Capacity = l.Count + 9;

    // the walk of a tree: each node's list read once before its loop (nothing below stores a list's fields)
    [MethodImpl(MethodImplOptions.NoInlining)]
    static long Walk(Node n, float px, float py)
    {
        long s = n.V + n.Id.Length + (long)(px + py);
        foreach (var k in n.Kids) s += Walk(k, px + n.X, py + n.Y);
        return s;
    }

    // the same walk where a node far below changes the root's list: the root's loop throws when the walk is back
    [MethodImpl(MethodImplOptions.NoInlining)]
    static long WalkChanging(Node n, int depth)
    {
        long s = n.V;
        if (n.Id == "trigger") s_root!.Kids.Add(new Node { Id = "late" });
        foreach (var k in n.Kids) s += WalkChanging(k, depth + 1);
        return s;
    }

    // a node's own fields change while its loop runs: read again each turn
    [MethodImpl(MethodImplOptions.NoInlining)]
    static string OwnFields(Node n)
    {
        string r = "";
        foreach (var k in n.Kids) { r += (n.X + n.Y) + k.Id + ","; n.X += 1; Bump(n); }
        return r;
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Bump(Node n) => n.Y += 10;

    static Node Tree(int depth, int fan, ref int id)
    {
        var n = new Node { Id = "n" + id, V = id++, X = depth, Y = fan };
        if (depth > 0) for (int i = 0; i < fan; i++) n.Kids.Add(Tree(depth - 1, fan, ref id));
        return n;
    }

    static void Changes()
    {
        var xs = new List<Node> { new Node { Id = "a" }, new Node { Id = "b" }, new Node { Id = "c" } };
        // by a call
        Console.WriteLine(Try(() => { foreach (var x in xs) if (x.Id == "b") AddTo(xs); }) + " " + xs.Count);
        // through a static that refers to the same list
        s_alias = xs;
        Console.WriteLine(Try(() => { foreach (var x in xs) { Nothing(x); if (x.Id == "c") AddAlias(); } }) + " " + xs.Count);
        // in the last round, by a removal that leaves the index past the end
        Console.WriteLine(Try(() => { foreach (var x in xs) if (x.Id == "alias") TrimAlias(); }) + " " + xs.Count);
        // in the last round, by an addition
        Console.WriteLine(Try(() => { foreach (var x in xs) if (x.Id == "added") AddTo(xs); }) + " " + xs.Count);
        // a change and then out of the loop: nothing throws
        Console.WriteLine(Try(() => { foreach (var x in xs) { AddTo(xs); break; } }) + " " + xs.Count);
        string seen = "";
        Console.WriteLine(Try(() => { foreach (var x in xs) { if (x.Id == "c") { AddTo(xs); return; } seen += x.Id; } }) + " " + seen);
        // another list of the same class, and one of another instantiation: this one is not changed
        var other = new List<Node>();
        s_objects = new List<object>();
        Console.WriteLine(Try(() => { foreach (var x in xs) { AddTo(other); AddObject(); } }) + " " + other.Count + " " + s_objects.Count);
        // the capacity alone: the array is another one, the version too
        Console.WriteLine(Try(() => { foreach (var x in xs) if (x.Id == "b") SetCapacity(xs); }) + " " + xs.Capacity);
        // an empty list changed by nothing, and one whose loop never turns but is changed before
        var none = new List<Node>();
        Console.WriteLine(Try(() => { foreach (var x in none) AddTo(none); }) + " " + none.Count);
        // nested: the inner list changed, caught inside, the outer goes on; then the outer changed
        var outer = new List<List<Node>> { new() { new Node { Id = "i1" }, new Node { Id = "i2" } }, new() { new Node { Id = "j1" } } };
        string log = "";
        Console.WriteLine(Try(() =>
        {
            foreach (var inner in outer)
            {
                try { foreach (var x in inner) { log += x.Id; AddTo(inner); } }
                catch (InvalidOperationException) { log += "!"; }
            }
        }) + " " + log);
        Console.WriteLine(Try(() => { foreach (var inner in outer) foreach (var x in inner) if (x.Id == "j1") outer.Add(new()); }) + " " + outer.Count);
    }

    // the function that loops catches, and goes on with structs on its stack
    [MethodImpl(MethodImplOptions.NoInlining)]
    static string CatchAndGoOn(List<Node> xs)
    {
        var w = new Wide { A = 1, B = 2, C = 3, S = "w" };
        string r = "";
        for (int round = 0; round < 3; round++)
        {
            try
            {
                foreach (var x in xs) { r += x.Id; if (round == 1 && x.Id == "q") AddTo(xs); w.A += x.V; }
                r += "|";
            }
            catch (InvalidOperationException e) { r += "<" + e.Message.Length + ">"; }
            var v = new Wide { A = w.A + round, B = w.B, C = w.C, S = w.S + round };
            r += Describe(in v);
        }
        return r + w.A;
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static string Describe(in Wide v) => v.S + (v.A + v.B + v.C);

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Thrower(List<Node> xs)
    {
        foreach (var x in xs)
            AddTo(xs);
    }

    static void Frames()
    {
        var xs = new List<Node> { new Node { Id = "f" } };
        try { Thrower(xs); }
        catch (InvalidOperationException e)
        {
            var lines = (e.StackTrace ?? "").Split('\n');
            foreach (var l in lines)
            {
                string t = l.Trim();
                int at = t.IndexOf(" in ", StringComparison.Ordinal);
                Console.WriteLine(at >= 0 ? t.Substring(0, at) + " line " + t.Substring(t.LastIndexOf(' ') + 1) : t);
            }
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        int id = 0;
        var root = Tree(4, 3, ref id);
        long sum = 0;
        for (int r = 0; r < 300; r++) sum += Walk(root, r, 1);
        Console.WriteLine(sum + " " + id);
        Console.WriteLine(OwnFields(root));
        s_root = root;
        Console.WriteLine(Try(() => Console.WriteLine(WalkChanging(root, 0))) + " " + root.Kids.Count);
        root.Kids[1].Kids[2].Id = "trigger";
        Console.WriteLine(Try(() => Console.WriteLine(WalkChanging(root, 0))) + " " + root.Kids.Count);
        root.Kids[1].Kids[2].Id = "calm";
        Console.WriteLine(Try(() => Console.WriteLine(WalkChanging(root, 0))) + " " + root.Kids.Count);
        Changes();
        var qs = new List<Node> { new Node { Id = "p", V = 5 }, new Node { Id = "q", V = 7 } };
        Console.WriteLine(CatchAndGoOn(qs));
        Frames();
        // many changed lists one after the other: the stack is where it was after each
        int thrown = 0;
        var again = new List<Node> { new Node { Id = "z" } };
        for (int i = 0; i < 3000; i++)
        {
            try { foreach (var x in again) { if (again.Count < 3) AddTo(again); else again.RemoveAt(2); } }
            catch (InvalidOperationException) { thrown++; }
        }
        Console.WriteLine(thrown + " " + again.Count);
    }
}
