// What the order in memory costs the churn scene's read loop (tools/churn-parts.cs, "loop without move"): 19,392
// particles allocated in order, then the same loop over a list of them in other orders: all of memory at random; at
// random within windows of 454 particles (a 16 KB page of them) and of 113 (4 KB); and in passes, as a page's freed
// blocks are used again: each window visited P times, a P-th of its particles each time, in address order or at
// random. Mean microseconds x1000 a run of the loop.
//   il2wasm tools/churn-order.cs -o co.wasm   then   node host/run.mjs co.wasm
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

sealed class Particle { public float X, Y, Vx, Vy; public int Life; public string Tag = ""; }

static class ChurnOrder
{
    static uint s_seed;
    static uint Next() { s_seed = s_seed * 1664525u + 1013904223u; return s_seed >> 8; }
    static readonly string[] s_words = "ore wood stone iron gold gem fish meat herb cloth rope nail plank brick glass coal".Split(' ');
    static List<Particle> s_particles = new();
    static Particle[] s_all = new Particle[19392];

    static long LoopNoMove()
    {
        long sum = 0;
        for (int i = 0; i < s_particles.Count; i++)
        {
            var p = s_particles[i];
            p.X += p.Vx; p.Y += p.Vy; p.Vy += 0.1f;
            if (p.Life > 0) { sum += p.Tag.Length; }
        }
        return sum;
    }
    static void Time(string name)
    {
        long per = Stopwatch.Frequency / 1000000;
        long t = 0, sum = 0;
        for (int r = 0; r < 3000; r++) { long a0 = Stopwatch.GetTimestamp(); sum += LoopNoMove(); if (r >= 1000) t += Stopwatch.GetTimestamp() - a0; }
        Console.WriteLine(name + ": " + t * 1000 / per / 2000 + " " + sum);
    }
    static void Shuffle(int[] a, int from, int to)
    {
        for (int i = to - 1; i > from; i--) { int j = from + (int)(Next() % (uint)(i - from + 1)); (a[i], a[j]) = (a[j], a[i]); }
    }
    static void Fill(int[] order) { s_particles.Clear(); for (int i = 0; i < order.Length; i++) s_particles.Add(s_all[order[i]]); }
    static void Windows(int w)
    {
        var o = new int[s_all.Length];
        for (int i = 0; i < o.Length; i++) o[i] = i;
        for (int a = 0; a < o.Length; a += w) Shuffle(o, a, Math.Min(o.Length, a + w));
        Fill(o);
        Time("at random within windows of " + w);
    }
    // each window of w visited `passes` times, a part of its particles each time (which part: at random)
    static void Passes(int w, int passes, bool sorted)
    {
        var part = new int[s_all.Length];
        for (int i = 0; i < part.Length; i++) part[i] = (int)(Next() % (uint)passes);
        var o = new int[s_all.Length];
        int n = 0;
        for (int p = 0; p < passes; p++)
            for (int a = 0; a < o.Length; a += w)
            {
                int from = n;
                for (int i = a; i < Math.Min(o.Length, a + w); i++) if (part[i] == p) o[n++] = i;
                if (!sorted) Shuffle(o, from, n);
            }
        Fill(o);
        Time("windows of " + w + " in " + passes + " passes, " + (sorted ? "in address order" : "at random"));
    }

    [UnmanagedCallersOnly(EntryPoint = "main")] static void Main() => Run();
    public static void Run()
    {
        s_seed = 12345;
        for (int i = 0; i < s_all.Length; i++)
            s_all[i] = new Particle { X = Next() % 640, Y = Next() % 480, Vx = (int)(Next() % 7) - 3, Vy = (int)(Next() % 7) - 3, Life = 20 + (int)(Next() % 60), Tag = s_words[Next() & 15] };
        var o = new int[s_all.Length];
        for (int i = 0; i < o.Length; i++) o[i] = i;
        Fill(o); Time("in order");
        Shuffle(o, 0, o.Length); Fill(o); Time("at random");
        Windows(454); Windows(113);
        Passes(454, 48, false); Passes(454, 48, true);
        Passes(454, 8, false); Passes(454, 8, true);
        Passes(454, 4, false); Passes(454, 4, true);
        Passes(454, 2, false); Passes(454, 2, true);
        for (int i = 0; i < o.Length; i++) o[i] = i;
        Fill(o); Time("in order");
    }
}
