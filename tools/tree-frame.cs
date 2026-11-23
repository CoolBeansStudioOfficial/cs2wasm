// The tree scene of tools/frames.mjs, its frames that are not a rebuild, by phase, for both sides (il2wasm, and
// NativeAOT-LLVM with a Main that calls TreeFrame.Run): the new subtree's build, its store over the old one (which lets
// the old one go), the move, the walk, and the walk again indexed and without the floats; median and mean, microseconds
// x1000 a frame.
//   il2wasm tools/tree-frame.cs -o tf.wasm   then   node host/run.mjs tf.wasm
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

sealed class Entity
{
    public Entity? Parent;
    public List<Entity> Children = new();
    public string Name = "";
    public float X, Y;
    public int Id;
}

static class TreeFrame
{
    static uint s_seed;
    static uint Next() { s_seed = s_seed * 1664525u + 1013904223u; return s_seed >> 8; }
    static readonly string[] s_words = "ore wood stone iron gold gem fish meat herb cloth rope nail plank brick glass coal".Split(' ');
    static Entity? s_root;
    static int s_ids;
    static Entity Build(Entity? parent, int depth, int fan)
    {
        var e = new Entity { Parent = parent, Id = s_ids++, Name = s_words[s_ids & 15], X = Next() % 100, Y = Next() % 100 };
        if (depth > 0) for (int i = 0; i < fan; i++) e.Children.Add(Build(e, depth - 1, fan));
        return e;
    }
    static long Walk(Entity e, float px, float py)
    {
        long s = e.Id + e.Name.Length + (long)(px + py);
        foreach (var c in e.Children) s += Walk(c, px + e.X, py + e.Y);
        return s;
    }
    static long WalkIdx(Entity e, float px, float py)
    {
        long s = e.Id + e.Name.Length + (long)(px + py);
        var l = e.Children;
        for (int i = 0; i < l.Count; i++) s += WalkIdx(l[i], px + e.X, py + e.Y);
        return s;
    }
    static long WalkInt(Entity e)
    {
        long s = e.Id + e.Name.Length;
        var l = e.Children;
        for (int i = 0; i < l.Count; i++) s += WalkInt(l[i]);
        return s;
    }

    [UnmanagedCallersOnly(EntryPoint = "main")] static void Main() => Run();
    public static void Run()
    {
        const int warm = 1000, frames = 6000;
        s_seed = 12345;
        long tb = 0, ts = 0, tm = 0, tw = 0, twi = 0, twn = 0, check = 0, n = 0;
        var sb = new long[frames]; var ss = new long[frames]; var sm = new long[frames]; var sw = new long[frames]; var swi = new long[frames]; int q = 0;
        for (int f = 0; f < warm + frames; f++)
        {
            if (s_root == null || f % 500 == 0) s_root = Build(null, 6, 5);
            var a = s_root.Children[(int)(Next() % 5)];
            var b = a.Children[(int)(Next() % 5)];
            int k = (int)(Next() % 5);
            long t0 = Stopwatch.GetTimestamp();
            var sub = Build(b, 3, 5);
            long t1 = Stopwatch.GetTimestamp();
            b.Children[k] = sub;
            sub = null;
            long t2 = Stopwatch.GetTimestamp();
            var c = s_root.Children[(int)(Next() % 5)].Children[(int)(Next() % 5)].Children[(int)(Next() % 5)];
            if (c.Children.Count > 0)
            {
                var moved = c.Children[c.Children.Count - 1];
                c.Children.RemoveAt(c.Children.Count - 1);
                var to = s_root.Children[(int)(Next() % 5)].Children[(int)(Next() % 5)];
                moved.Parent = to;
                to.Children.Add(moved);
            }
            long t3 = Stopwatch.GetTimestamp();
            check += Walk(a, 0, 0);
            long t4 = Stopwatch.GetTimestamp();
            check += WalkIdx(a, 0, 0);
            long t5 = Stopwatch.GetTimestamp();
            check += WalkInt(a);
            long t6 = Stopwatch.GetTimestamp();
            if (f >= warm && f % 500 != 0) { tb += t1 - t0; ts += t2 - t1; tm += t3 - t2; tw += t4 - t3; twi += t5 - t4; twn += t6 - t5; n++; sb[q] = t1 - t0; ss[q] = t2 - t1; sm[q] = t3 - t2; sw[q] = t4 - t3; swi[q] = t5 - t4; q++; }
        }
        long per = Stopwatch.Frequency / 1000000;
        Array.Sort(sb, 0, q); Array.Sort(ss, 0, q); Array.Sort(sm, 0, q); Array.Sort(sw, 0, q); Array.Sort(swi, 0, q);
        Console.WriteLine("median us x1000: build " + sb[q / 2] * 1000 / per + " store " + ss[q / 2] * 1000 / per + " move " + sm[q / 2] * 1000 / per + " walk " + sw[q / 2] * 1000 / per + " walk-indexed " + swi[q / 2] * 1000 / per + "; store p90 " + ss[q * 9 / 10] * 1000 / per + " p99 " + ss[q * 99 / 100] * 1000 / per);
        Console.WriteLine("mean us a frame x1000: build " + tb * 1000 / per / n + " store " + ts * 1000 / per / n + " move " + tm * 1000 / per / n
            + " walk " + tw * 1000 / per / n + " walk-indexed " + twi * 1000 / per / n + " walk-no-floats " + twn * 1000 / per / n + " check " + check);
    }
}
