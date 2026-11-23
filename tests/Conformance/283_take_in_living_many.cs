// Test 282 past any size: the structures' roots refer into a chain of five living structures (a static's member
// refers into the first, each into the next: the trial without the first fails by the first's reference into the
// second, and so on, five rounds), and into one living structure with forty members a static's list refers to
// (forty members with references left from outside the whole set). Rt.LivingTops leaves out each round's living
// tops, as many rounds as it takes; the two structures go, as .NET frees them.
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
    // (the group lives: one member a static holds, which every structure below refers to)
    static N? s_keep, s_live;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static N Tree(int children)
    {
        var root = new N(); root.Kids.Add(s_keep!);
        for (int i = 0; i < children; i++) { var c = new N(); c.Prev = root; root.Kids.Add(c); }
        return root;
    }

    // a holder outside the tree, a member of the group itself, refers to a child; the tree is let go, then the holder
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void HolderDies(bool viaArray)
    {
        var root = Tree(3);
        var x = new N(); x.Other = x;
        if (viaArray) x.Tag = new N[] { root.Kids[1], root.Kids[2] }; else x.Next = root.Kids[1];
        root = null;
        x = null;
    }

    // two holders, let go one after the other
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void TwoHolders()
    {
        var root = Tree(3);
        var x = new N(); x.Other = x; x.Next = root.Kids[1];
        var y = new N(); y.Other = y; y.Next = root.Kids[2]; y.Tag = root;
        root = null;
        x = null;
        y = null;
    }

    // a task's result: made referring to its parent, owned by another member (the task), then in the parent's list;
    // the tree is let go while the owner lives, then the owner goes and the list takes the result over
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void HolderComesUnder(int results)
    {
        var root = Tree(1);
        var owners = new N[results];
        for (int i = 0; i < results; i++)
        {
            var owner = new N(); owner.Other = owner;
            var r = new N(); r.Prev = root; r.Other = root.Kids[1];
            owner.Next = r;
            root.Kids.Add(r);
            owners[i] = owner;
        }
        root = null;
        for (int i = 0; i < results; i++) owners[i] = null!;
    }

    // a member taken out of the structure that still refers up into it, then lets that go too
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void HeldPlace()
    {
        var root = Tree(2);
        var a = root.Kids[1]; var b = new N(); a.Next = b; b.Prev = a; a.Kids.Add(b);
        root = null;
        a.Next = null; a.Kids.Clear();
        a = null;
        b.Prev = null;
        b = null;
    }

    // two structures that refer into each other while a holder that lives refers into one: a child dropped from its
    // parent's list (as two threads' List.Add at once drop one), still in an array the parent holds, and still
    // referring up to the parent
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void TwoStructures(bool holderFirst)
    {
        var root = Tree(2);
        var h = new N(); root.Other = h;
        foreach (var m in s_into) h.Kids.Add(m);
        var all = new N[] { root.Kids[1], root.Kids[2] }; root.Tag = all;
        var x = new N(); x.Other = x; x.Next = root.Kids[2];
        root.Kids[1] = root.Kids[2];
        if (holderFirst) x = null;
        root = null; all = null;
        x = null;
    }

    static List<N> s_into = new();

    // five living structures, each referred into by the one before, the first by a static's member
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void MakeChain()
    {
        N? prev = null;
        for (int k = 0; k < 5; k++)
        {
            var l = Tree(3);
            if (prev == null) s_keep!.Other = l.Kids[1]; else prev.Kids[2].Other = l.Kids[1];
            s_into.Add(l.Kids[3]);
            prev = l;
        }
    }

    // one living structure, forty of whose members a static's list refers to
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void MakeWide()
    {
        var l = Tree(40);
        for (int i = 1; i <= 40; i++) s_keep!.Kids.Add(l.Kids[i]);
        s_into.Add(l.Kids[40]);
    }

    static void Say(string what)
    {
        for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        Console.WriteLine(what + ": " + N.Live);
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        s_keep = new N(); s_keep.Kids.Add(s_keep);
        MakeChain();
        Say("start");
        HolderDies(false); Say("holder dies");
        HolderDies(true); Say("holder dies, an array");
        TwoHolders(); Say("two holders");
        HolderComesUnder(1); Say("holder comes under");
        HolderComesUnder(4); Say("four come under");
        HeldPlace(); Say("held place");
        TwoStructures(true); Say("two structures");
        TwoStructures(false); Say("two structures, the holder last");
        s_into.Clear(); MakeWide(); Say("wide");
        TwoStructures(true); Say("two structures, wide");
        TwoStructures(false); Say("two structures, wide, the holder last");
    }
}
