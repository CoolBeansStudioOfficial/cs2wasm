// Small objects made and let go in the ways that decide whether a page of them is left alone until it is empty
// (Runtime.cs, PageAlloc, HoldPages): a frame's objects made first and then some let go, all over the pages, as a
// particle system does (every look at the pages is between two bursts of frees: none is used again while it loses
// blocks); pages a third of which is let go once and never again, while as many objects are made as it takes for the
// size to need a page several times over (no longer being emptied: used again); one object in every hundred kept
// for good from the start, when the heap is little else (the pages with freed blocks past their bound: used again
// as before); whole runs let go at once between pages that were passed over; three sizes, one of them the smallest
// there is (the most blocks a page has, which the mark in the page's header must hold). Every object is read back:
// a block handed out twice, or one still in use handed out, shows as a wrong sum or as the exception.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

sealed class Tiny { public int A; }
sealed class Mid { public int A, B; public long C; public string Tag = ""; }
sealed class Fat { public long A, B, C, D, E; public Mid? Of; }

static class Program
{
    static uint s_seed = 88172645;
    static uint Next() { s_seed ^= s_seed << 13; s_seed ^= s_seed >> 17; s_seed ^= s_seed << 5; return s_seed; }
    static int s_made;
    static readonly string[] s_tags = { "x", "yy", "zzz", "wwww" };

    static Mid MakeMid(int life) { int n = ++s_made; return new Mid { A = n, B = life, C = (long)n * 7919, Tag = s_tags[n & 3] }; }
    static Tiny MakeTiny() => new Tiny { A = ++s_made * 3 };
    static Fat MakeFat(Mid? of) { int n = ++s_made; return new Fat { A = n, B = n + 1, C = n + 2, D = n + 3, E = (long)n * 5, Of = of }; }

    static long Check(List<Mid> l)
    {
        long sum = 0;
        for (int i = 0; i < l.Count; i++)
        {
            var c = l[i];
            if (c.C != (long)c.A * 7919) throw new InvalidOperationException("a Mid changed: " + c.A);
            sum += c.A + c.Tag.Length;
        }
        return sum;
    }
    static long Check(List<Tiny> l)
    {
        long sum = 0;
        for (int i = 0; i < l.Count; i++) { if (l[i].A % 3 != 0) throw new InvalidOperationException("a Tiny changed"); sum += l[i].A; }
        return sum;
    }
    static long Check(List<Fat> l)
    {
        long sum = 0;
        for (int i = 0; i < l.Count; i++)
        {
            var c = l[i];
            if (c.E != c.A * 5 || c.D != c.A + 3) throw new InvalidOperationException("a Fat changed: " + c.A);
            sum += c.A + (c.Of != null ? c.Of.A & 15 : 0);
        }
        return sum;
    }

    // one in a hundred kept for good, the rest let go at once, over and over: every page has freed blocks and never
    // empties (run first, when the heap holds little else)
    static long Pinned(List<Mid> keep, List<Tiny> keepTiny, int rounds)
    {
        long sum = 0;
        var tmp = new List<Mid>(); var tmpTiny = new List<Tiny>();
        for (int r = 0; r < rounds; r++)
        {
            for (int i = 0; i < 3000; i++)
            {
                var c = MakeMid(1); if (i % 100 == 0) keep.Add(c); else tmp.Add(c);
                var t = MakeTiny(); if (i % 100 == 50) keepTiny.Add(t); else tmpTiny.Add(t);
            }
            sum += Check(tmp) + Check(tmpTiny);
            tmp.Clear(); tmpTiny.Clear();
        }
        return sum + Check(keep) + Check(keepTiny) + keep.Count;
    }
    // a frame: some born, then every one looked at and the dead taken out (20 to 79 frames each)
    static long Frames(List<Mid> l, List<Tiny> tiny, List<Fat> fat, int frames, int born)
    {
        long sum = 0;
        for (int f = 0; f < frames; f++)
        {
            int n = born + (int)(Next() % (uint)(born / 2 + 1));
            for (int i = 0; i < n; i++)
            {
                var m = MakeMid(20 + (int)(Next() % 60));
                l.Add(m);
                if ((i & 1) == 0) tiny.Add(MakeTiny());
                if ((i & 3) == 0) fat.Add(MakeFat(m));
            }
            int w = 0;
            for (int i = 0; i < l.Count; i++) { var c = l[i]; if (--c.B > 0) l[w++] = c; }
            l.RemoveRange(w, l.Count - w);
            w = 0;
            for (int i = 0; i < tiny.Count; i++) { var t = tiny[i]; if (Next() % 40 != 0) tiny[w++] = t; }
            tiny.RemoveRange(w, tiny.Count - w);
            w = 0;
            for (int i = 0; i < fat.Count; i++) { var x = fat[i]; if (Next() % 50 != 0) fat[w++] = x; }
            fat.RemoveRange(w, fat.Count - w);
            if (f % 20 == 0) sum += Check(l) + Check(tiny) + Check(fat) + l.Count;
        }
        return sum + Check(l) + Check(tiny) + Check(fat);
    }
    // a third of every page let go once (every third object), then nothing more of them while many are made: the
    // pages stop losing blocks
    static long Still(List<Mid> old, List<Mid> more, int count)
    {
        for (int i = 0; i < count; i++) old.Add(MakeMid(1));
        int w = 0;
        for (int i = 0; i < old.Count; i++) if (i % 3 != 0) old[w++] = old[i];
        old.RemoveRange(w, old.Count - w);
        long sum = Check(old);
        for (int r = 0; r < 12; r++)
        {
            for (int i = 0; i < count / 6; i++) more.Add(MakeMid(1));
            sum += Check(old) + Check(more);
        }
        return sum;
    }
    // whole runs let go at once, from the middle, between frames of the first kind
    static long Runs(List<Mid> l, List<Tiny> tiny, List<Fat> fat, int rounds)
    {
        long sum = 0;
        for (int r = 0; r < rounds; r++)
        {
            if (l.Count > 3000) l.RemoveRange((int)(Next() % (uint)(l.Count - 2500)), 2500);
            if (tiny.Count > 4000) tiny.RemoveRange((int)(Next() % (uint)(tiny.Count - 3000)), 3000);
            if (fat.Count > 900) fat.RemoveRange((int)(Next() % (uint)(fat.Count - 600)), 600);
            sum += Frames(l, tiny, fat, 6, 500);
        }
        return sum;
    }

    [UnmanagedCallersOnly(EntryPoint = "main")] static void Main()
    {
        var keep = new List<Mid>(); var keepTiny = new List<Tiny>();
        Console.WriteLine("pinned " + Pinned(keep, keepTiny, 30));
        var l = new List<Mid>(); var tiny = new List<Tiny>(); var fat = new List<Fat>();
        Console.WriteLine("frames " + Frames(l, tiny, fat, 300, 300) + " " + l.Count + " " + tiny.Count + " " + fat.Count);
        var old = new List<Mid>(); var more = new List<Mid>();
        Console.WriteLine("still " + Still(old, more, 12000) + " " + old.Count + " " + more.Count);
        Console.WriteLine("frames again " + Frames(l, tiny, fat, 200, 500) + " " + Check(old) + " " + Check(keep));
        Console.WriteLine("runs " + Runs(l, tiny, fat, 30) + " " + l.Count + " " + tiny.Count + " " + fat.Count);
        more.Clear();
        Console.WriteLine("pinned again " + Pinned(keep, keepTiny, 10) + " " + Check(old));
        old.Clear(); l.Clear(); tiny.Clear(); fat.Clear();
        Console.WriteLine("after clear " + Frames(l, tiny, fat, 150, 400) + " " + Check(keep) + " " + Check(keepTiny) + " " + s_made);
    }
}
