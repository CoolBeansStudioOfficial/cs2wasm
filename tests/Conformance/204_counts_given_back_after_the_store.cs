// A store into a slot a cycle can close through, where the slot held nothing (docs/AUDIT.md A3.5, the build, round
// 10): the unlink of the old value has nothing to release, and is not called, unless the store's link kept a count
// to give back once the slot is written (Rt.GiveBackLater: the value's last reference from outside, where giving it
// back before the store could let the value go). That count must go back at this store, not at the next one that
// happens to link something: with it, the last reference to a whole group goes, and the group with it, at the
// statement that lets it go (IL2WASM_NO_UNLINK_SKIP=1: the call, always). Counted against .NET after each step.
// The cases: a member that another member owns, stored by a temporary into an empty field of a temporary member,
// into an empty element, into a new list, into an empty field of a plain object and of a new object; the same
// with the field holding something before; a group whose members are held by nothing else than such a store's
// value; many such statements in a row with nothing between them.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

sealed class Tag { readonly int _k; public Tag(int k) { _k = k; Link.Live[k]++; } ~Tag() { System.Threading.Interlocked.Decrement(ref Link.Live[_k]); } }

sealed class Link
{
    public static readonly int[] Live = new int[6];
    public Link? Next, Side, Up;
    public Link?[]? Many;
    public List<Link>? Kids;
    public Tag Tag;
    public int Id;
    public Link(int kind, int id) { Id = id; Tag = new Tag(kind); }
}

static class P
{
    static Link? s_keep;
    static int s_ids;

    // two links that refer to each other; the one the other owns is returned (its last reference the caller's)
    [MethodImpl(MethodImplOptions.NoInlining)]
    static Link Pair(int kind)
    {
        var a = new Link(kind, ++s_ids);
        var b = new Link(kind, ++s_ids);
        a.Next = b;
        b.Up = a;
        return b;
    }

    // a ring of n, any one of them returned
    [MethodImpl(MethodImplOptions.NoInlining)]
    static Link Ring(int kind, int n)
    {
        var first = new Link(kind, ++s_ids);
        var p = first;
        for (int i = 1; i < n; i++) { var q = new Link(kind, ++s_ids); p.Next = q; q.Up = p; p = q; }
        p.Next = first;
        return p;
    }

    static void Say(string when)
    {
        for (int i = 0; i < 4; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        Console.WriteLine(when + ": " + string.Join(" ", Link.Live));
    }
    static void Do(string when, Action step) { step(); Say(when); }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        // into an empty field of a member that only this statement refers to: both groups go with the statement
        Do("field of a temporary", static () => Pair(0).Side = Pair(0));
        Do("field of a ring", static () => Ring(1, 4).Side = Ring(1, 3));
        Do("field, the value a ring's", static () => Pair(0).Side = Ring(1, 5));
        // into an empty element, and into a list
        Do("element", static () => { var holder = Pair(2); holder.Many = new Link?[3]; holder.Many[1] = Pair(2); });
        Do("list", static () => { var holder = Pair(2); holder.Kids = new List<Link>(); holder.Kids.Add(Pair(2)); holder.Kids.Add(Ring(2, 3)); });
        // the field held something before: the unlink has an old value too
        Do("field over a value", static () => { var holder = Pair(3); holder.Side = Pair(3); holder.Side = Pair(3); });
        // a plain object and a new one as the container
        Do("plain container", static () => { var plain = new Link(4, ++s_ids); plain.Side = Pair(4); });
        Do("new container", static () => { var made = new Link(4, ++s_ids) { Side = Pair(4), Next = Ring(4, 2) }; made.Up = made; });
        // kept: the same stores into a structure that lives on, then let go
        Do("kept", static () =>
        {
            s_keep = Pair(5);
            s_keep.Side = Pair(5);
            s_keep.Many = new Link?[4];
            for (int i = 0; i < 4; i++) s_keep.Many[i] = Pair(5);
            s_keep.Kids = new List<Link> { Pair(5), Ring(5, 3) };
        });
        Do("kept, some replaced", static () => { s_keep!.Many![2] = Pair(5); s_keep.Side = null; s_keep.Kids![0] = Ring(5, 2); });
        Do("kept, let go", static () => s_keep = null);
        // many in a row, nothing between them that links
        Do("a thousand", static () => { for (int i = 0; i < 1000; i++) Pair(0).Side = Pair(1); });
        Do("a thousand rings", static () => { for (int i = 0; i < 1000; i++) Ring(2, 3).Side = Ring(3, 4); });
    }
}
