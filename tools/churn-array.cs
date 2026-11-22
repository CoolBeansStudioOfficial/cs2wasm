// The churn scene (tools/frames/Frames.cs) through List<Particle> and the same by hand over an array and a count
// (what the list's indexers come to once their checks and loads are shared: the array read once before the loop, no
// size check beside the array's own, no version), for both sides: median ns a frame of each, 6,000 frames after
// 1,000. The difference is the most a compiler can take out of the list's loop without touching the store itself.
//   cs2wasmc tools/churn-array.cs -o ca.wasm   then   node host/run.mjs ca.wasm
// NativeAOT-LLVM: tools/frames-naot-wasm's project with this file in place of Frames.cs, the attribute on WasmMain
// taken out (its export would be the module's main), and a Main that calls ChurnArray.Go.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

sealed class Particle { public float X, Y, Vx, Vy; public int Life; public string Tag = ""; }

static class ChurnArray
{
    static uint s_seed;
    static uint Next() { s_seed = s_seed * 1664525u + 1013904223u; return s_seed >> 8; }
    static readonly string[] s_words = "ore wood stone iron gold gem fish meat herb cloth rope nail plank brick glass coal".Split(' ');
    static Particle Make() => new Particle { X = Next() % 640, Y = Next() % 480, Vx = (int)(Next() % 7) - 3, Vy = (int)(Next() % 7) - 3, Life = 20 + (int)(Next() % 60), Tag = s_words[Next() & 15] };

    static List<Particle> s_particles = new();
    static long ListFrame()
    {
        int born = 300 + (int)(Next() % 200);
        for (int i = 0; i < born; i++) s_particles.Add(Make());
        long sum = 0;
        int w = 0;
        for (int i = 0; i < s_particles.Count; i++)
        {
            var p = s_particles[i];
            p.X += p.Vx; p.Y += p.Vy; p.Vy += 0.1f;
            if (--p.Life > 0) { s_particles[w++] = p; sum += p.Tag.Length; }
        }
        s_particles.RemoveRange(w, s_particles.Count - w);
        return sum + s_particles.Count;
    }

    static Particle[] s_arr = new Particle[32768];
    static int s_count;
    static long ArrayFrame()
    {
        int born = 300 + (int)(Next() % 200);
        for (int i = 0; i < born; i++) s_arr[s_count++] = Make();
        long sum = 0;
        int w = 0;
        var a = s_arr;
        int n = s_count;
        for (int i = 0; i < n; i++)
        {
            var p = a[i];
            p.X += p.Vx; p.Y += p.Vy; p.Vy += 0.1f;
            if (--p.Life > 0) { a[w++] = p; sum += p.Tag.Length; }
        }
        Array.Clear(a, w, n - w);
        s_count = w;
        return sum + w;
    }

    static long[] s_t = new long[6000];
    static long Run(Func<long> frame)
    {
        s_seed = 12345;
        long check = 0;
        for (int f = 0; f < 7000; f++)
        {
            long t0 = Stopwatch.GetTimestamp();
            check += frame();
            long t = Stopwatch.GetTimestamp() - t0;
            if (f >= 1000) s_t[f - 1000] = t;
        }
        Array.Sort(s_t);
        Console.WriteLine("median ns " + s_t[3000] * 1000000000 / Stopwatch.Frequency + " check " + check);
        return check;
    }

    [UnmanagedCallersOnly(EntryPoint = "main")] static void WasmMain() => Go();
    public static void Go()
    {
        Console.Write("list: "); Run(ListFrame);
        Console.Write("array: "); Run(ArrayFrame);
        Console.Write("list again: "); s_particles = new(); Run(ListFrame);
    }
}
