// The churn scene's move (tools/frames/Frames.cs: `s_particles[w++] = p`) apart from the order in memory and from the
// frees, for both sides (cs2wasm, and NativeAOT-LLVM with a Main that calls ChurnMove.Run): a particle's allocation
// and its free alone; the scene's loop over 19,392 particles allocated in order, none dead (every store into the
// element's own slot), then with the first 400 dead and held by an array (every survivor moves 400 slots down, no
// free); then the scene itself, its loop as it is and with the list in a local on alternate frames. Mean
// microseconds x1000 a frame, picoseconds a particle for the first.
//   cs2wasmc tools/churn-move.cs -o cm.wasm   then   node host/run.mjs cm.wasm
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

sealed class Particle { public float X, Y, Vx, Vy; public int Life; public string Tag = ""; }

static class ChurnMove
{
    static uint s_seed;
    static uint Next() { s_seed = s_seed * 1664525u + 1013904223u; return s_seed >> 8; }
    static readonly string[] s_words = "ore wood stone iron gold gem fish meat herb cloth rope nail plank brick glass coal".Split(' ');
    static List<Particle> s_particles = new();
    static Particle[] s_dead = new Particle[400];
    static Particle?[] s_some = new Particle?[400];

    static Particle Make() => new Particle { X = Next() % 640, Y = Next() % 480, Vx = (int)(Next() % 7) - 3, Vy = (int)(Next() % 7) - 3, Life = 20 + (int)(Next() % 60), Tag = s_words[Next() & 15] };
    static void Birth()
    {
        int born = 300 + (int)(Next() % 200);
        for (int i = 0; i < born; i++) s_particles.Add(Make());
    }
    static int s_w;
    static long Loop()
    {
        long sum = 0;
        int w = 0;
        for (int i = 0; i < s_particles.Count; i++)
        {
            var p = s_particles[i];
            p.X += p.Vx; p.Y += p.Vy; p.Vy += 0.1f;
            if (--p.Life > 0) { s_particles[w++] = p; sum += p.Tag.Length; }
        }
        s_w = w;
        return sum;
    }
    static long LoopLocal()
    {
        long sum = 0;
        int w = 0;
        var l = s_particles;
        for (int i = 0; i < l.Count; i++)
        {
            var p = l[i];
            p.X += p.Vx; p.Y += p.Vy; p.Vy += 0.1f;
            if (--p.Life > 0) { l[w++] = p; sum += p.Tag.Length; }
        }
        s_w = w;
        return sum;
    }
    // (the scene's loop without the decrement: the same particles live on every run)
    static long LoopKeep()
    {
        long sum = 0;
        int w = 0;
        for (int i = 0; i < s_particles.Count; i++)
        {
            var p = s_particles[i];
            p.X += p.Vx; p.Y += p.Vy; p.Vy += 0.1f;
            if (p.Life > 0) { s_particles[w++] = p; sum += p.Tag.Length; }
        }
        s_w = w;
        return sum;
    }
    static long LoopKeepNoStore()
    {
        long sum = 0;
        int w = 0;
        for (int i = 0; i < s_particles.Count; i++)
        {
            var p = s_particles[i];
            p.X += p.Vx; p.Y += p.Vy; p.Vy += 0.1f;
            if (p.Life > 0) { w++; sum += p.Tag.Length; }
        }
        s_w = w;
        return sum;
    }
    static void Remove() { s_particles.RemoveRange(s_w, s_particles.Count - s_w); }

    [UnmanagedCallersOnly(EntryPoint = "main")] static void Main() => Run();
    public static void Run()
    {
        long per = Stopwatch.Frequency / 1000000;
        s_seed = 12345;
        // a particle's allocation and its free, 400 at a time
        long ta = 0, tf = 0;
        for (int r = 0; r < 30000; r++)
        {
            long a0 = Stopwatch.GetTimestamp();
            for (int i = 0; i < s_some.Length; i++) s_some[i] = Make();
            long a1 = Stopwatch.GetTimestamp();
            for (int i = 0; i < s_some.Length; i++) s_some[i] = null;
            long a2 = Stopwatch.GetTimestamp();
            if (r >= 10000) { ta += a1 - a0; tf += a2 - a1; }
        }
        Console.WriteLine("ps a particle: made " + ta * 1000 / per / 20000 * 1000 / 400 + " let go " + tf * 1000 / per / 20000 * 1000 / 400);

        for (int i = 0; i < s_dead.Length; i++) { s_dead[i] = Make(); s_dead[i].Life = 0; }
        for (int i = 0; i < 19392; i++) s_particles.Add(Make());
        long t = 0, sum = 0;
        for (int r = 0; r < 3000; r++) { long a0 = Stopwatch.GetTimestamp(); sum += LoopKeepNoStore(); if (r >= 1000) t += Stopwatch.GetTimestamp() - a0; }
        Console.WriteLine("in order, no store x1000: " + t * 1000 / per / 2000 + " " + sum);
        t = 0;
        for (int r = 0; r < 3000; r++) { long a0 = Stopwatch.GetTimestamp(); sum += LoopKeep(); if (r >= 1000) t += Stopwatch.GetTimestamp() - a0; }
        Console.WriteLine("in order, own slot x1000: " + t * 1000 / per / 2000 + " " + sum);
        t = 0;
        for (int r = 0; r < 3000; r++)
        {
            s_particles.InsertRange(0, s_dead);
            long a0 = Stopwatch.GetTimestamp(); sum += LoopKeep(); if (r >= 1000) t += Stopwatch.GetTimestamp() - a0;
            Remove();
        }
        Console.WriteLine("in order, 400 slots down x1000: " + t * 1000 / per / 2000 + " " + sum + " " + s_particles.Count);

        s_particles.Clear();
        s_seed = 12345;
        const int warm = 1000, frames = 6000;
        long check = 0, tl = 0, tloc = 0, tb = 0, tr = 0;
        for (int f = 0; f < warm + frames; f++)
        {
            long t0 = Stopwatch.GetTimestamp();
            Birth();
            long t1 = Stopwatch.GetTimestamp();
            check += (f & 1) == 0 ? Loop() : LoopLocal();
            long t2 = Stopwatch.GetTimestamp();
            Remove();
            long t3 = Stopwatch.GetTimestamp();
            if (f >= warm) { tb += t1 - t0; if ((f & 1) == 0) tl += t2 - t1; else tloc += t2 - t1; tr += t3 - t2; }
        }
        Console.WriteLine("scene, mean us a frame x1000: birth " + tb * 1000 / per / frames + " loop " + tl * 1000 / per / (frames / 2) + " loop, list in a local " + tloc * 1000 / per / (frames / 2)
            + " remove " + tr * 1000 / per / frames + " live " + s_particles.Count + " check " + check);
    }
}
