// Where the churn scene's particles lie (tools/frames/Frames.cs, tools/churn-move.cs): the scene run for 3,000 frames,
// then, over the list in its order, the particles' addresses: the block's size (the smallest distance between two),
// how many 16 KB pages and how many 64-byte lines they are in, how many steps of the walk go to another line that is
// not the next one or the one before (a line the engine's and the processor's read-ahead did not have), how many
// lines the walk comes back to after it has been elsewhere, and then the loop's time over that layout.
//   cs2wasmc tools/churn-layout.cs -o cl.wasm   then   node host/run.mjs cl.wasm
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;

sealed class Particle { public float X, Y, Vx, Vy; public int Life; public string Tag = ""; }

static class ChurnLayout
{
    static uint s_seed;
    static uint Next() { s_seed = s_seed * 1664525u + 1013904223u; return s_seed >> 8; }
    static readonly string[] s_words = "ore wood stone iron gold gem fish meat herb cloth rope nail plank brick glass coal".Split(' ');
    static List<Particle> s_particles = new();
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
    // (the loop without the decrement: nothing dies, every store into the element's own slot)
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
    // (the scene's loop with the dead put into an array: no particle is freed inside the loop)
    static Particle?[] s_grave = new Particle?[1024];
    static int s_g;
    static long LoopGrave()
    {
        long sum = 0;
        int w = 0, g = 0;
        for (int i = 0; i < s_particles.Count; i++)
        {
            var p = s_particles[i];
            p.X += p.Vx; p.Y += p.Vy; p.Vy += 0.1f;
            if (--p.Life > 0) { s_particles[w++] = p; sum += p.Tag.Length; }
            else s_grave[g++] = p;
        }
        s_w = w; s_g = g;
        return sum;
    }
    static long[] s_t = new long[8000];
    static int s_n;
    static void T(long ticks) { s_t[s_n++] = ticks; }
    // (the median frame, ns, and back to no samples)
    static long Med() { Array.Sort(s_t, 0, s_n); long m = s_t[s_n / 2] * 1000000000 / Stopwatch.Frequency; s_n = 0; return m; }
    static void Remove() { s_particles.RemoveRange(s_w, s_particles.Count - s_w); }
    static uint Addr(Particle p) => (uint)Unsafe.As<Particle, nint>(ref p);

    static void Layout()
    {
        int n = s_particles.Count;
        var a = new uint[n];
        for (int i = 0; i < n; i++) a[i] = Addr(s_particles[i]);
        var sorted = (uint[])a.Clone();
        Array.Sort(sorted);
        uint size = uint.MaxValue;
        for (int i = 1; i < n; i++) if (sorted[i] - sorted[i - 1] < size) size = sorted[i] - sorted[i - 1];
        var pages = new HashSet<uint>();
        var lines = new HashSet<uint>();
        var left = new HashSet<uint>();
        int jumps = 0, back = 0;
        uint last = 0;
        for (int i = 0; i < n; i++)
        {
            pages.Add(a[i] >> 14);
            uint l0 = a[i] >> 6, l1 = (a[i] + size - 1) >> 6;
            for (uint l = l0; l <= l1; l++)
            {
                if (l != last)
                {
                    if (l != last + 1 && l != last - 1) jumps++;
                    if (!lines.Add(l)) back++;
                    last = l;
                }
            }
        }
        Console.WriteLine("live " + n + " block " + size + " pages " + pages.Count + " lines " + lines.Count + " jumps " + jumps + " lines come back to " + back
            + " first page offset " + (sorted[0] & 16383) + " span KB " + (sorted[n - 1] - sorted[0]) / 1024);
    }

    [System.Runtime.InteropServices.UnmanagedCallersOnly(EntryPoint = "main")] static void Main() => Run();
    public static void Run()
    {
        long per = Stopwatch.Frequency / 1000000;
        s_seed = 12345;
        const int warm = 1000, frames = 6000;
        long check = 0, tl = 0;
        for (int f = 0; f < warm + frames; f++)
        {
            Birth();
            long t1 = Stopwatch.GetTimestamp();
            check += Loop();
            long t2 = Stopwatch.GetTimestamp();
            Remove();
            if (f >= warm) T(t2 - t1);
            if (f == 6999) Layout();
        }
        Console.WriteLine("loop, median ns a frame: " + Med() + " check " + check);
        s_particles = new(); s_seed = 12345; tl = 0; check = 0;
        long tg = 0;
        for (int f = 0; f < warm + frames; f++)
        {
            Birth();
            long t1 = Stopwatch.GetTimestamp();
            check += LoopGrave();
            long t2 = Stopwatch.GetTimestamp();
            Remove();
            long t3 = Stopwatch.GetTimestamp();
            for (int i = 0; i < s_g; i++) s_grave[i] = null;
            long t4 = Stopwatch.GetTimestamp();
            if (f >= warm) { T(t2 - t1); tg += t4 - t3; }
        }
        Console.WriteLine("loop, the dead into an array: " + Med() + " the array cleared " + tg * 1000 / per / frames + " check " + check);
        // the same particles where the scene left them, none dying; then as many made in order
        long t = 0, sum = 0;
        for (int r = 0; r < 3000; r++) { long a0 = Stopwatch.GetTimestamp(); sum += LoopKeep(); if (r >= 1000) T(Stopwatch.GetTimestamp() - a0); }
        Console.WriteLine("where the scene left them, none dying : " + Med() + " " + sum);
        // the same again with one in 48 dead, all over the list (at random places), and held by an array: the
        // branch and the moves of the scene, no free
        {
            var master = s_particles.ToArray();
            int dead = 0;
            for (int i = 0; i < master.Length; i++) if (Next() % 48 == 0) { master[i].Life = 0; dead++; }
            t = 0;
            for (int r = 0; r < 3000; r++)
            {
                long a0 = Stopwatch.GetTimestamp(); sum += LoopKeep(); if (r >= 1000) T(Stopwatch.GetTimestamp() - a0);
                s_particles.Clear(); s_particles.AddRange(master);
            }
            Console.WriteLine("where the scene left them, " + dead + " dead all over, held : " + Med() + " " + sum);
            // and not held: each is freed inside the loop, and made again (in the block it had) outside it
            var at = new int[dead];
            for (int i = 0, k = 0; i < master.Length; i++) if (master[i].Life == 0) at[k++] = i;
            for (int r = 0; r < 3000; r++)
            {
                long a0 = Stopwatch.GetTimestamp(); sum += LoopKeep(); if (r >= 1000) T(Stopwatch.GetTimestamp() - a0);
                for (int k = 0; k < at.Length; k++) master[at[k]] = null!;
                for (int k = at.Length - 1; k >= 0; k--) { var p = Make(); p.Life = 0; master[at[k]] = p; }
                s_particles.Clear(); s_particles.AddRange(master);
            }
            Console.WriteLine("where the scene left them, " + dead + " dead all over, freed in the loop: " + Med() + " " + sum);
            for (int i = 0; i < master.Length; i++) master[i].Life = 50;
        }
        // the scene's own loop (with its decrement) over them, none dying
        for (int i = 0; i < s_particles.Count; i++) s_particles[i].Life = 1 << 30;
        for (int r = 0; r < 3000; r++) { long a0 = Stopwatch.GetTimestamp(); sum += Loop(); if (r >= 1000) T(Stopwatch.GetTimestamp() - a0); }
        Console.WriteLine("where the scene left them, the scene's loop, none dying: " + Med() + " " + sum);
        int n = s_particles.Count;
        var fresh = new List<Particle>(n);
        for (int i = 0; i < n; i++) fresh.Add(Make());
        s_particles = fresh;
        Layout();
        t = 0;
        for (int r = 0; r < 3000; r++) { long a0 = Stopwatch.GetTimestamp(); sum += LoopKeep(); if (r >= 1000) T(Stopwatch.GetTimestamp() - a0); }
        Console.WriteLine("as many made in order, none dying : " + Med() + " " + sum);
    }
}
