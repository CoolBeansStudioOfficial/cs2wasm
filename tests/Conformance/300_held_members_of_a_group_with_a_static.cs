// A tree let go while code still holds members its list let go (taken out by a store over them, or by Remove), each
// member referring back to the root and kept as well by an array the root holds, in a group that a static keeps
// alive through an object in the root's list. When the code lets the members go too, nothing refers to the tree:
// .NET frees it, and so must the counts here, at that release (a member that held its old place in the list kept
// the root, the root's array kept the member, and the check made when the root was let go had failed by that hold).
// Variants: Remove or a store, the static's object at each place in the list or not there, one or two members the
// code holds, objects with a finalizer and without (counted by weak references), the code letting go of them in
// either order. What is alive while the code holds them and after is what .NET says.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

class Node
{
    public Node? Prev, Next; public List<Node> Kids = new(); public object? Tag; public int Id;
}

sealed class Fin : Node
{
    public static int Live;
    public Fin() { Live++; }
    ~Fin() { Live--; }
}

sealed class Plain : Node { }

static class Program
{
    static Node? s_keep;
    static readonly List<WeakReference> s_weak = new();
    static void Collect() { for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); } }

    static string Alive()
    {
        Collect();
        int n = 0;
        foreach (var w in s_weak) if (w.IsAlive) n++;
        return $"weak {n} fin {Fin.Live}";
    }

    static Node Make(bool fin, int id)
    {
        Node x = fin ? new Fin() : new Plain();
        x.Id = id;
        s_weak.Add(new WeakReference(x));
        return x;
    }

    // how: 0 a store of the last member over each held one, 1 Remove of each held one, 2 RemoveAt;
    // keepAt: where the static's object is in the list (-1 not there); held: 1 or 2 members the code keeps
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Build(bool fin, int how, int keepAt, int held, out Node? k1, out Node? k2)
    {
        var root = Make(fin, 0);
        var a1 = Make(fin, 1); a1.Prev = root;
        var a2 = held > 1 ? Make(fin, 2) : null; if (a2 != null) a2.Prev = root;
        var b = Make(fin, 3); b.Prev = root;
        // (no list or array of the code's own holds them: it would be one more container of the members)
        root.Kids.Add(a1);
        if (a2 != null) root.Kids.Add(a2);
        root.Kids.Add(b);
        if (keepAt >= 0) root.Kids.Insert(Math.Min(keepAt, root.Kids.Count), s_keep!);
        root.Tag = a2 != null ? new Node[] { a1, a2 } : new Node[] { a1 };
        TakeOut(root, a1, b, how);
        if (a2 != null) TakeOut(root, a2, b, how);
        k1 = a1; k2 = a2;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void TakeOut(Node root, Node x, Node b, int how)
    {
        if (how == 0) root.Kids[root.Kids.IndexOf(x)] = b;
        else if (how == 1) root.Kids.Remove(x);
        else root.Kids.RemoveAt(root.Kids.IndexOf(x));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void LetGoSecond(ref Node? k) { k = null; }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Run(bool fin, int how, int keepAt, int held, bool reverse)
    {
        Build(fin, how, keepAt, held, out var k1, out var k2);
        string during = Alive();
        string middle = "";
        if (held > 1)
        {
            if (reverse) LetGoSecond(ref k2); else LetGoSecond(ref k1);
            middle = " / " + Alive();
        }
        GC.KeepAlive(k1); GC.KeepAlive(k2);
        Drop(ref k1, ref k2);
        Console.WriteLine($"{(fin ? "fin" : "plain")} how {how} keep {keepAt} held {held}{(reverse ? " reversed" : "")}: held {during}{middle}, after {Alive()}");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Drop(ref Node? k1, ref Node? k2) { k1 = null; k2 = null; }

    // the root let go first, its list still held by the code when the member is taken out of it
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void BuildList(bool fin, int keepAt, out List<Node>? kids, out Node? a1, out Node? b)
    {
        var root = Make(fin, 0);
        a1 = Make(fin, 1); a1.Prev = root;
        b = Make(fin, 3); b.Prev = root;
        root.Kids.Add(a1); root.Kids.Add(b);
        if (keepAt >= 0) root.Kids.Insert(keepAt, s_keep!);
        root.Tag = new Node[] { a1 };
        kids = root.Kids;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void RunList(bool fin, int how, int keepAt)
    {
        BuildList(fin, keepAt, out var kids, out var a1, out var b);
        TakeOut2(kids!, a1!, b!, how);
        b = null;
        string s1 = Alive();
        kids = null;
        string s2 = Alive();
        GC.KeepAlive(a1);
        Drop(ref a1, ref b);
        Console.WriteLine($"{(fin ? "fin" : "plain")} list kept, how {how} keep {keepAt}: {s1} / {s2}, after {Alive()}");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void TakeOut2(List<Node> kids, Node x, Node b, int how)
    {
        if (how == 0) kids[kids.IndexOf(x)] = b; else kids.Remove(x);
    }

    // the array that holds the member too is another tree's, one a static keeps
    static Node? s_other;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void BuildOther(bool fin, int how, int keepAt, out Node? k1)
    {
        var root = Make(fin, 0);
        var a1 = Make(fin, 1); a1.Prev = root;
        var b = Make(fin, 3); b.Prev = root;
        root.Kids.Add(a1); root.Kids.Add(b);
        if (keepAt >= 0) root.Kids.Insert(keepAt, s_keep!);
        var other = Make(fin, 4);
        other.Kids.Add(s_keep!);
        other.Tag = new Node[] { a1 };
        s_other = other;
        TakeOut(root, a1, b, how);
        k1 = a1;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void RunOther(bool fin, int how, int keepAt)
    {
        BuildOther(fin, how, keepAt, out var k1);
        string during = Alive();
        Node? none = null;
        Drop(ref k1, ref none);
        string after = Alive();
        s_other = null;
        Console.WriteLine($"{(fin ? "fin" : "plain")} another tree's array, how {how} keep {keepAt}: held {during}, after {after}, without the other {Alive()}");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Setup()
    {
        var k = new Fin { Id = -1 };
        k.Kids.Add(k);
        s_keep = k;
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        Setup();
        foreach (bool fin in new[] { true, false })
            for (int how = 0; how < 3; how++)
                for (int keepAt = -1; keepAt <= 3; keepAt++)
                    for (int held = 1; held <= 2; held++)
                        foreach (bool reverse in held > 1 ? new[] { false, true } : new[] { false })
                        {
                            if (keepAt == 3 && held == 1) continue;
                            s_weak.Clear();
                            Run(fin, how, keepAt, held, reverse);
                        }
        foreach (bool fin in new[] { true, false })
            for (int how = 0; how < 2; how++)
                for (int keepAt = -1; keepAt <= 2; keepAt++)
                {
                    s_weak.Clear();
                    RunList(fin, how, keepAt);
                    s_weak.Clear();
                    RunOther(fin, how, keepAt);
                }
        s_keep = null;
        Console.WriteLine("end " + Alive());
    }
}
