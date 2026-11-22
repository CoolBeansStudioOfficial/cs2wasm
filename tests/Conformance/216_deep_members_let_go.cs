// A chain of members, each the owner of the next and referred to by it, in a group that lives on (a static holds one
// of its members): the chain is let go while a member deep in it is still held, then that member too. All of it must
// be freed then, however deep the member is (it was not, from 64 owners down: the member's last reference did not
// find the top of what it was under, and the chain stayed for as long as the group lived). And a held member that is
// taken out of the chain first: what it was under goes when the references the member still has into it are gone.
// What .NET frees must be freed (finalizers count what lives).
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

sealed class N
{
    public static int Live;
    public N? Next, Prev; public List<N> Kids = new();
    public N() { Live++; }
    ~N() { Live--; }
}

static class P
{
    // (the group lives: one member a static holds, which every chain refers to)
    static N? s_keep;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static N Chain(int n, int pick)
    {
        var head = new N(); var cur = head; N? got = null; head.Kids.Add(s_keep!);
        for (int i = 1; i < n; i++) { var x = new N(); cur.Next = x; x.Prev = cur; cur.Kids.Add(x); if (i == pick) got = x; cur = x; }
        return got ?? head;
    }

    // a member below the top held while the top is let go, then let go itself
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Deep(int n, int pick)
    {
        var held = Chain(n, pick);
        GC.KeepAlive(held);
    }

    // a held member taken out of the chain it was under, then its reference up into the chain, then the member
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Leaves(int n, int pick)
    {
        var held = Chain(n, pick);
        var above = held.Prev!;
        above.Next = null; above.Kids.Clear();
        above = null;
        held.Prev = null;
        held = null;
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
        foreach (int n in new[] { 4, 40, 100, 300, 3000 })
        {
            Deep(n, n - 1); Say("last of " + n);
            Deep(n, n / 2); Say("middle of " + n);
            Leaves(n, n / 2); Say("taken out of " + n);
        }
    }
}
