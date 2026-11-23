// Structures that refer into each other (test 215's two structures) while code still holds a member of one of them,
// and small structures checked at their drop that refer into the one that lives: the check takes in no top above a
// member that code refers to (Rt.TopToTake), and frees each small one; once the code lets go, both structures go.
// (The code holds a member still in the list: one taken out of it and held by code leaks on main as well, a gap of
// its own, docs/AUDIT.md.) CS2WASM_TAKE_IN_STOP_BREAK=1 (no top taken in from above) shows in test 215 (`two
// structures: 7`, .NET's 1), not here.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

sealed class N
{
    public static int Live;
    public N? Next, Prev, Other; public List<N> Kids = new(); public object? Tag;
    public N() { Live++; }
    ~N() { Live--; }
}

static class P
{
    static N? s_keep;

    static N Tree(int children)
    {
        var root = new N(); root.Kids.Add(s_keep!);
        for (int i = 0; i < children; i++) { var c = new N(); c.Prev = root; root.Kids.Add(c); }
        return root;
    }

    static int Count()
    {
        for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        return N.Live;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static N Build()
    {
        var root = Tree(2);
        var all = new N[] { root.Kids[1], root.Kids[2] }; root.Tag = all;
        var x = new N(); x.Other = x; x.Next = root.Kids[2];
        root.Kids[1] = root.Kids[2];
        // (a small structure that refers into both, let go first)
        var y = new N(); y.Other = y; y.Next = all[0]; y.Prev = root.Kids[2];
        y = null;
        x = null;
        return root.Kids[2];
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Small(N into, int n)
    {
        for (int i = 0; i < n; i++)
        {
            var z = new N(); z.Other = z; z.Next = into; z.Prev = into.Prev;
            var w = new N(); w.Next = z; z.Tag = w;
            z = null; w = null;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Run()
    {
        var keep = Build();
        Console.WriteLine("held: " + Count());
        Small(keep, 200);
        Console.WriteLine("small ones: " + Count());
        Small(keep.Prev!.Kids[1], 50);
        Console.WriteLine("small ones, the other: " + Count());
        GC.KeepAlive(keep);
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        s_keep = new N(); s_keep.Kids.Add(s_keep);
        Console.WriteLine("start: " + Count());
        Run();
        Console.WriteLine("after: " + Count());
    }
}
