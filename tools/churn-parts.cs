// The churn scene of tools/frames.mjs in parts, for both sides (cs2wasm, and NativeAOT-LLVM with a Main that calls
// ChurnParts.Run): the scene's read loop over 19,392 particles allocated in order, then over 800 (in the cache) in
// its forms (the static list, the list in a local, an array, without the float fields, rotated, with the element
// store), in picoseconds a particle; then the scene itself, its frame by phase (birth, the loop, the removal, and the
// loop again without the move), mean microseconds x1000 a frame.
//   cs2wasmc tools/churn-parts.cs -o cp.wasm   then   node host/run.mjs cp.wasm
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

sealed class Particle { public float X, Y, Vx, Vy; public int Life; public string Tag = ""; }

static class ChurnParts
{
    static uint s_seed;
    static uint Next() { s_seed = s_seed * 1664525u + 1013904223u; return s_seed >> 8; }
    static readonly string[] s_words = "ore wood stone iron gold gem fish meat herb cloth rope nail plank brick glass coal".Split(' ');
    static List<Particle> s_particles = new();
    static long s_birth, s_loop, s_remove, s_live;

    static void Birth()
    {
        int born = 300 + (int)(Next() % 200);
        for (int i = 0; i < born; i++)
            s_particles.Add(new Particle { X = Next() % 640, Y = Next() % 480, Vx = (int)(Next() % 7) - 3, Vy = (int)(Next() % 7) - 3, Life = 20 + (int)(Next() % 60), Tag = s_words[Next() & 15] });
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
    // (the loop without the move: the update and the test alone; run on a frame of its own kind)
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
    static Particle[] s_arr = new Particle[0];
    static long LoopArr()
    {
        long sum = 0;
        var arr = s_arr;
        for (int i = 0; i < arr.Length; i++)
        {
            var p = arr[i];
            p.X += p.Vx; p.Y += p.Vy; p.Vy += 0.1f;
            if (p.Life > 0) { sum += p.Tag.Length; }
        }
        return sum;
    }
    static long LoopArrInt()
    {
        long sum = 0;
        var arr = s_arr;
        for (int i = 0; i < arr.Length; i++)
        {
            var p = arr[i];
            if (p.Life > 0) { sum += p.Tag.Length; }
        }
        return sum;
    }
    static long LoopListLocal()
    {
        long sum = 0;
        var l = s_particles;
        for (int i = 0; i < l.Count; i++)
        {
            var p = l[i];
            p.X += p.Vx; p.Y += p.Vy; p.Vy += 0.1f;
            if (p.Life > 0) { sum += p.Tag.Length; }
        }
        return sum;
    }
    static void Time(string name, Func<long> f)
    {
        long t = 0, sum = 0;
        for (int r = 0; r < 60000; r++) { long a0 = Stopwatch.GetTimestamp(); sum += f(); if (r >= 20000) t += Stopwatch.GetTimestamp() - a0; }
        Console.WriteLine(name + ", ps a particle: " + t * 1000 / (Stopwatch.Frequency / 1000000) / 40000 * 1000 / 800 + " " + sum);
    }
    static long LoopArrStore()
    {
        long sum = 0;
        var arr = s_arr;
        int w = 0;
        for (int i = 0; i < arr.Length; i++)
        {
            var p = arr[i];
            if (p.Life > 0) { arr[w++] = p; sum += p.Tag.Length; }
        }
        return sum;
    }
    static Particle[] s_arr2 = new Particle[0];
    static long LoopArrStoreOther()
    {
        long sum = 0;
        var arr = s_arr; var to = s_arr2;
        int w = 0;
        for (int i = 0; i < arr.Length; i++)
        {
            var p = arr[i];
            if (p.Life > 0) { to[w++] = p; sum += p.Tag.Length; }
        }
        return sum;
    }
    static long LoopListStore()
    {
        long sum = 0;
        var l = s_particles;
        int w = 0;
        for (int i = 0; i < l.Count; i++)
        {
            var p = l[i];
            if (p.Life > 0) { l[w++] = p; sum += p.Tag.Length; }
        }
        return sum;
    }
    static long LoopArrIntRot()
    {
        long sum = 0;
        var arr = s_arr;
        int i = 0;
        if (i < arr.Length)
            do
            {
                var p = arr[i];
                if (p.Life > 0) { sum += p.Tag.Length; }
                i++;
            } while (i < arr.Length);
        return sum;
    }
    static long LoopArrIntRotN()
    {
        long sum = 0;
        var arr = s_arr;
        int i = 0, n = arr.Length;
        if (i < n)
            do
            {
                var p = arr[i];
                if (p.Life > 0) { sum += p.Tag.Length; }
                i++;
            } while (i < n);
        return sum;
    }
    static long LoopArrIntN()
    {
        long sum = 0;
        var arr = s_arr;
        int n = arr.Length;
        for (int i = 0; i < n; i++)
        {
            var p = arr[i];
            if (p.Life > 0) { sum += p.Tag.Length; }
        }
        return sum;
    }
    static void Remove() { s_particles.RemoveRange(s_w, s_particles.Count - s_w); }

    [UnmanagedCallersOnly(EntryPoint = "main")] static void Main() => Run();
    public static void Run()
    {
        const int warm = 1000, frames = 6000;
        s_seed = 12345;
        for (int i = 0; i < 19392; i++)
            s_particles.Add(new Particle { X = Next() % 640, Y = Next() % 480, Vx = (int)(Next() % 7) - 3, Vy = (int)(Next() % 7) - 3, Life = 20 + (int)(Next() % 60), Tag = s_words[Next() & 15] });
        long fresh = 0, fsum = 0;
        for (int r = 0; r < 3000; r++) { long a0 = Stopwatch.GetTimestamp(); fsum += LoopNoMove(); if (r >= 1000) fresh += Stopwatch.GetTimestamp() - a0; }
        Console.WriteLine("fresh in order, loop-without-move x1000: " + fresh * 1000 / (Stopwatch.Frequency / 1000000) / 2000 + " " + fsum);
        s_particles.RemoveRange(800, s_particles.Count - 800);
        fresh = 0;
        for (int r = 0; r < 60000; r++) { long a0 = Stopwatch.GetTimestamp(); fsum += LoopNoMove(); if (r >= 20000) fresh += Stopwatch.GetTimestamp() - a0; }
        Console.WriteLine("800 in order, ps a particle: " + fresh * 1000 / (Stopwatch.Frequency / 1000000) / 40000 * 1000 / 800 + " " + fsum);
        s_arr = s_particles.ToArray();
        Time("list static", LoopNoMove); Time("list local", LoopListLocal); Time("array", LoopArr); Time("array, no floats", LoopArrInt);
        s_arr2 = new Particle[s_arr.Length];
        Time("array, no floats, store same slot", LoopArrStore); Time("array, no floats, store other array", LoopArrStoreOther); Time("list local, no floats, store", LoopListStore);
        Time("array, no floats, rotated", LoopArrIntRot); Time("array, no floats, rotated, n local", LoopArrIntRotN); Time("array, no floats, n local", LoopArrIntN); Time("array, no floats", LoopArrInt);
        s_arr = new Particle[0]; s_arr2 = s_arr;
        s_particles.Clear();
        s_seed = 12345;
        long check = 0, nomove = 0;
        for (int f = 0; f < warm + frames; f++)
        {
            long t0 = Stopwatch.GetTimestamp();
            Birth();
            long t1 = Stopwatch.GetTimestamp();
            check += Loop();
            long t2 = Stopwatch.GetTimestamp();
            Remove();
            long t3 = Stopwatch.GetTimestamp();
            check += LoopNoMove();
            long t4 = Stopwatch.GetTimestamp();
            if (f >= warm) { s_birth += t1 - t0; s_loop += t2 - t1; s_remove += t3 - t2; nomove += t4 - t3; s_live += s_particles.Count; }
        }
        long per = Stopwatch.Frequency / 1000000;
        Console.WriteLine("mean us a frame x1000: birth " + s_birth * 1000 / per / frames + " loop " + s_loop * 1000 / per / frames + " remove " + s_remove * 1000 / per / frames
            + " loop-without-move " + nomove * 1000 / per / frames + " live " + s_live / frames + " check " + check);
    }
}
