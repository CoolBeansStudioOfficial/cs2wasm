// A finalizer of a member of a group that is going stores the member (or another of the group) where the program
// can reach it: it lives on as any object, with what it refers to; the others go; let go later it is freed, its
// finalizer not run again unless GC.ReRegisterForFinalize asked (until 2026-11-23 the whole group was never freed).
// Short weak references read dead from the finalizers on, a weak reference that tracks resurrection reads the
// object until it is freed.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

sealed class Node
{
    public static int Live, Finalized;
    public static Node? Saved;
    public static List<Node> SavedList = new();
    public static Dictionary<int, Node> SavedMap = new();
    public Node? Next, Hub;
    public List<Node> Kids = new();
    public int Id, Mode, Runs;
    public Node(int id, int mode = 0) { Id = id; Mode = mode; Live++; }
    ~Node()
    {
        Finalized++; Runs++;
        // 1: into a static; 2: into a static list; 3: its next into the static; 4: itself, cut from the others;
        // 5: into a static and asks to be finalized again; 6: into a dictionary; 7: a new node that refers to it
        // into the static list; 8: into the static, and takes itself out again
        if (Mode == 1 && Runs == 1) Saved = this;
        else if (Mode == 2 && Runs == 1) SavedList.Add(this);
        else if (Mode == 3 && Runs == 1) Saved = Next;
        else if (Mode == 4 && Runs == 1) { Hub = null; Next = null; Kids.Clear(); Saved = this; }
        else if (Mode == 5 && Runs <= 2) { Saved = this; GC.ReRegisterForFinalize(this); }
        else if (Mode == 6 && Runs == 1) SavedMap[Id] = this;
        else if (Mode == 7 && Runs == 1) SavedList.Add(new Node(900 + Id) { Hub = this });
        else if (Mode == 8 && Runs == 1) { Saved = this; SavedList.Add(this); Saved = null; SavedList.Clear(); }
        if (Runs == Mode + 100) Live += 1000;
        Live--;
    }
}

static class Program
{
    static WeakReference<Node>? s_short;
    static WeakReference? s_long;
    static void Collect() { for (int i = 0; i < 2; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); } }
    static string Counts() { Collect(); return $"live {Node.Live} finalized {Node.Finalized}"; }
    static string Weak() => $"short {(s_short!.TryGetTarget(out _) ? "alive" : "dead")} long {(s_long!.IsAlive ? "alive" : "dead")}";

    // a ring of n, the first with the mode
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Ring(int n, int mode)
    {
        var first = new Node(0, mode); var prev = first;
        for (int i = 1; i < n; i++) { var x = new Node(i); prev.Next = x; x.Hub = first; prev = x; }
        prev.Next = first;
        s_short = new WeakReference<Node>(first); s_long = new WeakReference(first, trackResurrection: true);
    }

    // a hub with n kids that refer back, kid k with the mode
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Star(int n, int k, int mode)
    {
        var hub = new Node(100);
        for (int i = 0; i < n; i++) { var x = new Node(101 + i, i == k ? mode : 0) { Hub = hub }; hub.Kids.Add(x); if (i == k) { s_short = new WeakReference<Node>(x); s_long = new WeakReference(x, trackResurrection: true); } }
    }

    static Node s_root = new Node(-1);
    // the same star under a tree that lives on, then cut from it
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void StarUnder(int n, int k, int mode)
    {
        var hub = new Node(200) { Hub = s_root };
        s_root.Kids.Add(hub);
        for (int i = 0; i < n; i++) { var x = new Node(201 + i, i == k ? mode : 0) { Hub = hub }; hub.Kids.Add(x); if (i == k) { s_short = new WeakReference<Node>(x); s_long = new WeakReference(x, trackResurrection: true); } }
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Cut() { s_root.Kids.Clear(); }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static string Reach()
    {
        // what the saved nodes still reach
        int n = 0;
        var seen = new HashSet<Node>();
        void Walk(Node? x) { while (x != null && seen.Add(x)) { n++; foreach (var k in x.Kids) Walk(k); Walk(x.Hub); x = x.Next; } }
        Walk(Node.Saved); foreach (var x in Node.SavedList) Walk(x); foreach (var x in Node.SavedMap.Values) Walk(x);
        return $"reach {n}";
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Drop() { Node.Saved = null; Node.SavedList.Clear(); Node.SavedMap.Clear(); }

    static void Round(string what)
    {
        Console.WriteLine($"{what}: {Counts()} {Reach()} {Weak()}");
        Drop();
        Console.WriteLine($"{what}, let go: {Counts()} {Weak()}");
        Drop();
        Console.WriteLine($"{what}, let go again: {Counts()} {Reach()}");
        Drop();
        Console.WriteLine($"{what}, at the end: {Counts()}");
    }

    [UnmanagedCallersOnly(EntryPoint = "main")] static void Main()
    {
        Console.WriteLine("start: " + Counts());
        for (int mode = 0; mode <= 8; mode++)
        {
            Ring(5, mode); Round($"ring, mode {mode}");
            Star(6, 2, mode); Round($"star, mode {mode}");
            StarUnder(6, 3, mode); Cut(); Round($"star under a living tree, mode {mode}");
            Ring(1, mode); Round($"one that refers to itself, mode {mode}");
        }
        Console.WriteLine("root " + s_root.Id + " " + s_root.Kids.Count);
    }
}
