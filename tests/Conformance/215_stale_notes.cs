// A structure let go while members outside it still refer into it, in a group that lives on (a static holds one of
// its members), and those references then go in ways that are no store: the holder dies, the holder comes under the
// structure (its owner dies and a list of the structure, which held it too, takes it over), or the holder had been
// taken out of the structure and only still held its place in it. Each time the structure is garbage from then on and
// must be freed, as .NET frees it (finalizers count what lives). Test 127's tasks under load, made certain: there a
// pool thread destroyed a task (which held its result, a child of the tree) and a delegate after the main thread had
// let the tree go.
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
    static N? s_keep;

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
        var all = new N[] { root.Kids[1], root.Kids[2] }; root.Tag = all;
        var x = new N(); x.Other = x; x.Next = root.Kids[2];
        root.Kids[1] = root.Kids[2];
        if (holderFirst) x = null;
        root = null; all = null;
        x = null;
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
        Say("start");
        HolderDies(false); Say("holder dies");
        HolderDies(true); Say("holder dies, an array");
        TwoHolders(); Say("two holders");
        HolderComesUnder(4); Say("four come under");
        HeldPlace(); Say("held place");
        TwoStructures(true); Say("two structures");
        TwoStructures(false); Say("two structures, the holder last");
    }
}
