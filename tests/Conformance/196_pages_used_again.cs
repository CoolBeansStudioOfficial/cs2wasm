// Small objects of one size made and let go in the ways that decide which page of them is used again (Runtime.cs,
// PageAlloc): dying one by one all over their pages while as many are made (pages left to empty, then cut again);
// let go in runs (pages a part of which is free and that lose no more: used again as they are); a few kept in every
// page for good (pages that never empty); everything let go and made again; the same with the memory nearly full, so
// that an empty page would need the memory to grow (large arrays held meanwhile), and past that. What is checked is
// what the objects hold: every object is read back each round, so a block handed out twice, or one still in use
// handed out, shows as a wrong sum.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

sealed class Cell { public int A, B; public long C; public Cell? Next; public string Tag = ""; }
sealed class Wide { public long A, B, C, D, E, F; public Cell? Of; }

static class Program
{
    static uint s_seed = 2463534242;
    static uint Next() { s_seed ^= s_seed << 13; s_seed ^= s_seed >> 17; s_seed ^= s_seed << 5; return s_seed; }
    static int s_made;
    static readonly string[] s_tags = { "a", "bb", "ccc", "dddd" };

    static Cell Make(int life)
    {
        int n = ++s_made;
        return new Cell { A = n, B = life, C = (long)n * 1000003, Tag = s_tags[n & 3] };
    }
    static long Check(List<Cell> l)
    {
        long sum = 0;
        for (int i = 0; i < l.Count; i++)
        {
            var c = l[i];
            if (c.C != (long)c.A * 1000003) throw new InvalidOperationException("a cell changed: " + c.A);
            sum += c.A + c.Tag.Length + (c.Next != null ? c.Next.A & 7 : 0);
        }
        return sum;
    }
    // every frame some are born, each lives 20 to 79 frames: the list's order is their age, the dead are all over it
    static long Churn(List<Cell> l, int frames, int born)
    {
        long sum = 0;
        for (int f = 0; f < frames; f++)
        {
            int n = born + (int)(Next() % (uint)(born / 2 + 1));
            for (int i = 0; i < n; i++) l.Add(Make(20 + (int)(Next() % 60)));
            int w = 0;
            for (int i = 0; i < l.Count; i++) { var c = l[i]; if (--c.B > 0) l[w++] = c; }
            l.RemoveRange(w, l.Count - w);
            if (f % 16 == 0) sum += Check(l) + l.Count;
        }
        return sum + Check(l);
    }
    // runs of them let go at once, in the middle of the list; as many made again, linked to an older one
    static long Runs(List<Cell> l, int rounds, int run)
    {
        long sum = 0;
        for (int r = 0; r < rounds; r++)
        {
            if (l.Count > run * 2)
            {
                int at = (int)(Next() % (uint)(l.Count - run));
                l.RemoveRange(at, run);
            }
            for (int i = 0; i < run; i++) { var c = Make(1 << 20); if (l.Count > 0) c.Next = l[(int)(Next() % (uint)l.Count)]; l.Add(c); }
            if (r % 8 == 0) sum += Check(l) + l.Count;
        }
        return sum + Check(l);
    }
    // of every 97 made one is kept for good: no page of them ever empties
    static long Pinned(List<Cell> keep, int rounds)
    {
        long sum = 0;
        var tmp = new List<Cell>();
        for (int r = 0; r < rounds; r++)
        {
            for (int i = 0; i < 2000; i++) { var c = Make(1); if (i % 97 == 0) keep.Add(c); else tmp.Add(c); }
            sum += Check(tmp);
            if ((r & 1) == 0) tmp.Clear(); else tmp.RemoveRange(0, tmp.Count / 2);
        }
        return sum + Check(keep) + keep.Count;
    }
    static long Wides(int rounds, List<Cell> cells)
    {
        long sum = 0;
        var w = new List<Wide>();
        for (int r = 0; r < rounds; r++)
        {
            for (int i = 0; i < 300; i++) w.Add(new Wide { A = r, B = i, F = r * 31 + i, Of = cells.Count > 0 ? cells[(int)(Next() % (uint)cells.Count)] : null });
            int k = 0;
            for (int i = 0; i < w.Count; i++) { var x = w[i]; if (x.F != x.A * 31 + x.B) throw new InvalidOperationException("a wide one changed"); if (Next() % 5 != 0) w[k++] = x; }
            w.RemoveRange(k, w.Count - k);
            sum += w.Count;
        }
        for (int i = 0; i < w.Count; i++) sum += w[i].F + (w[i].Of?.A & 3 ?? 0);
        return sum;
    }

    [UnmanagedCallersOnly(EntryPoint = "main")] static void Main()
    {
        var a = new List<Cell>(); var b = new List<Cell>(); var keep = new List<Cell>();
        Console.WriteLine("churn " + Churn(a, 400, 300) + " " + a.Count);
        Console.WriteLine("runs " + Runs(b, 300, 150) + " " + b.Count);
        Console.WriteLine("pinned " + Pinned(keep, 40));
        Console.WriteLine("churn again " + Churn(a, 200, 500) + " " + a.Count);
        Console.WriteLine("wides " + Wides(200, a));
        a.Clear();
        Console.WriteLine("after clear " + Churn(a, 150, 300) + " " + Check(b) + " " + Check(keep));
        // the memory nearly full, then past what it was: large arrays held while the same goes on
        var big = new List<byte[]>();
        long total = 0;
        for (int step = 0; step < 14; step++)
        {
            var arr = new byte[1 << 20];
            arr[step * 1000] = (byte)(step + 1);
            big.Add(arr);
            total += Churn(a, 30, 400) + Runs(b, 20, 150) + Pinned(keep, 2);
        }
        for (int step = 0; step < 6; step++)
        {
            for (int i = 0; i < 4; i++) big.Add(new byte[250000 + i * 4096]);
            total += Churn(a, 40, 600) + Runs(b, 30, 200) + Wides(20, a);
        }
        long bigSum = 0;
        for (int i = 0; i < 14; i++) bigSum += big[i][i * 1000];
        Console.WriteLine("full " + total + " " + bigSum + " " + a.Count + " " + b.Count + " " + keep.Count);
        big.Clear();
        Console.WriteLine("freed " + Churn(a, 100, 300) + " " + Check(b) + " " + Check(keep) + " " + s_made);
    }
}
