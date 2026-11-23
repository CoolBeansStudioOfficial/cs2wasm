// A finalizer of a member of a group that is going grows a list of that same group (and makes new members of it):
// everything goes, as under .NET (what the finalizer added stayed for good). Each object counts itself; a log
// outside the group counts the finalizers.
// known-gap: what a finalizer of a dying group adds to that group refers back to its members, which reads as a member stored where the program can reach it: the group and the additions are never freed (docs/AUDIT.md, known differences 2026-11-22)
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

sealed class Item
{
    public static int Live;
    public Owner? Owner; public int Id;
    public Item(int id) { Id = id; Live++; }
    ~Item() { Live--; }
}

sealed class Owner
{
    public static int Live, Finalized;
    public List<Item> Items = new();
    public List<Owner> Others = new();
    public Owner? Peer;
    public int Mode;
    public Owner() { Live++; }
    ~Owner()
    {
        Finalized++; Live--;
        // 1: its own list grows past its capacity, with new members that refer back; 2: the peer's list grows;
        // 3: both, and a new owner joins the group; 4: the list is replaced by a larger one
        if (Mode is 1 or 3) for (int i = 0; i < 40; i++) Items.Add(new Item(1000 + i) { Owner = this });
        if (Mode is 2 or 3 && Peer != null) for (int i = 0; i < 40; i++) Peer.Items.Add(new Item(2000 + i) { Owner = Peer });
        if (Mode == 3) { var o = new Owner { Peer = this }; Others.Add(o); o.Others.Add(this); }
        if (Mode == 4) { var l = new List<Item>(100); for (int i = 0; i < 50; i++) l.Add(new Item(3000 + i) { Owner = this }); Items = l; }
    }
}

static class Program
{
    static void Collect() { for (int i = 0; i < 6; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); } }
    static string Counts() { Collect(); return $"owners {Owner.Live} items {Item.Live} finalized {Owner.Finalized}"; }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Make(int mode, int peerMode)
    {
        var a = new Owner { Mode = mode }; var b = new Owner { Mode = peerMode };
        a.Peer = b; b.Peer = a;
        for (int i = 0; i < 3; i++) { a.Items.Add(new Item(i) { Owner = a }); b.Items.Add(new Item(10 + i) { Owner = b }); }
    }

    static Owner? s_kept;
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void MakeUnderLiving(int mode)
    {
        // (the dying owners hang under one that lives on)
        var top = s_kept ??= new Owner();
        var a = new Owner { Mode = mode, Peer = top };
        for (int i = 0; i < 3; i++) a.Items.Add(new Item(i) { Owner = a });
        top.Others.Add(a);
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void CutUnderLiving() { s_kept!.Others.Clear(); }

    [UnmanagedCallersOnly(EntryPoint = "main")] static void Main()
    {
        for (int mode = 0; mode <= 4; mode++)
        {
            Make(mode, 0);
            Console.WriteLine($"mode {mode}: " + Counts());
            Make(mode, mode);
            Console.WriteLine($"mode {mode} twice: " + Counts());
        }
        for (int mode = 1; mode <= 4; mode++)
        {
            MakeUnderLiving(mode);
            CutUnderLiving();
            Console.WriteLine($"under a living one, mode {mode}: " + Counts());
        }
        s_kept = null;
        Console.WriteLine("end: " + Counts());
    }
}
