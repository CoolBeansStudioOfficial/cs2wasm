// What a compaction loop's move would cost without its two counts (docs/AUDIT.md A3.5, "The counted move"): the churn
// scene's loop over an array of 19,392 particles made in order, the first 400 dead and held elsewhere, so that every
// survivor moves 400 slots down (no free). Three ways: no store; the counted store `a[w++] = p` (a count for the new
// slot, one taken from what the slot held); and the store a proof of "moved, not copied" would leave: the reference
// written without a count, the slot it came from set to null, and what the destination held let go only when it is
// not null (a dead particle; here counted, not freed). The third is written with the array seen as nint[]: it is a
// measurement of the instructions, not something a program should do (the counts are put right outside the timing).
// Median ns a frame.   cs2wasmc tools/churn-raw.cs -o cr.wasm   then   node host/run.mjs cr.wasm
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

sealed class Particle { public float X, Y, Vx, Vy; public int Life; public string Tag = ""; }

static class ChurnRaw
{
    static uint s_seed;
    static uint Next() { s_seed = s_seed * 1664525u + 1013904223u; return s_seed >> 8; }
    static readonly string[] s_words = "ore wood stone iron gold gem fish meat herb cloth rope nail plank brick glass coal".Split(' ');
    static Particle Make() => new Particle { X = Next() % 640, Y = Next() % 480, Vx = (int)(Next() % 7) - 3, Vy = (int)(Next() % 7) - 3, Life = 20 + (int)(Next() % 60), Tag = s_words[Next() & 15] };
    const int N = 19392 + 400;
    static Particle[] s_master = new Particle[N];
    static Particle[] s_a = new Particle[N];
    static int s_w, s_dead;

    static long LoopNoStore(Particle[] a)
    {
        long sum = 0;
        int w = 0;
        for (int i = 0; i < a.Length; i++)
        {
            var p = a[i];
            p.X += p.Vx; p.Y += p.Vy; p.Vy += 0.1f;
            if (p.Life > 0) { w++; sum += p.Tag.Length; }
        }
        s_w = w;
        return sum;
    }
    static long LoopCounted(Particle[] a)
    {
        long sum = 0;
        int w = 0;
        for (int i = 0; i < a.Length; i++)
        {
            var p = a[i];
            p.X += p.Vx; p.Y += p.Vy; p.Vy += 0.1f;
            if (p.Life > 0) { a[w++] = p; sum += p.Tag.Length; }
        }
        s_w = w;
        return sum;
    }
    static long LoopMoved(Particle[] a)
    {
        long sum = 0;
        int w = 0, dead = 0;
        nint[] raw = Unsafe.As<nint[]>(a);
        for (int i = 0; i < a.Length; i++)
        {
            var p = a[i];
            p.X += p.Vx; p.Y += p.Vy; p.Vy += 0.1f;
            if (p.Life > 0)
            {
                if (w != i)
                {
                    nint old = raw[w];
                    raw[w] = raw[i];
                    raw[i] = 0;
                    if (old != 0) dead++;
                }
                w++;
                sum += p.Tag.Length;
            }
        }
        s_w = w; s_dead = dead;
        return sum;
    }

    // (the reference written and nothing else; and written into another array)
    static long LoopRawOnly(Particle[] a)
    {
        long sum = 0;
        int w = 0;
        nint[] raw = Unsafe.As<nint[]>(a);
        for (int i = 0; i < a.Length; i++)
        {
            var p = a[i];
            p.X += p.Vx; p.Y += p.Vy; p.Vy += 0.1f;
            if (p.Life > 0) { raw[w] = raw[i]; w++; sum += p.Tag.Length; }
        }
        s_w = w;
        return sum;
    }
    static nint[] s_other = new nint[N];
    static long LoopRawOther(Particle[] a)
    {
        long sum = 0;
        int w = 0;
        nint[] raw = Unsafe.As<nint[]>(a), other = s_other;
        for (int i = 0; i < a.Length; i++)
        {
            var p = a[i];
            p.X += p.Vx; p.Y += p.Vy; p.Vy += 0.1f;
            if (p.Life > 0) { other[w] = raw[i]; w++; sum += p.Tag.Length; }
        }
        s_w = w;
        return sum;
    }
    static long LoopIntOther(Particle[] a)
    {
        long sum = 0;
        int w = 0;
        nint[] other = s_other;
        for (int i = 0; i < a.Length; i++)
        {
            var p = a[i];
            p.X += p.Vx; p.Y += p.Vy; p.Vy += 0.1f;
            if (p.Life > 0) { other[w] = i; w++; sum += p.Tag.Length; }
        }
        s_w = w;
        return sum;
    }
    static long LoopRawNull(Particle[] a)
    {
        long sum = 0;
        int w = 0;
        nint[] raw = Unsafe.As<nint[]>(a);
        for (int i = 0; i < a.Length; i++)
        {
            var p = a[i];
            p.X += p.Vx; p.Y += p.Vy; p.Vy += 0.1f;
            if (p.Life > 0) { raw[w] = raw[i]; raw[i] = 0; w++; sum += p.Tag.Length; }
        }
        s_w = w;
        return sum;
    }
    static long LoopRawOld(Particle[] a)
    {
        long sum = 0;
        int w = 0, dead = 0;
        nint[] raw = Unsafe.As<nint[]>(a);
        for (int i = 0; i < a.Length; i++)
        {
            var p = a[i];
            p.X += p.Vx; p.Y += p.Vy; p.Vy += 0.1f;
            if (p.Life > 0) { nint old = raw[w]; raw[w] = raw[i]; if (old == 0) dead++; w++; sum += p.Tag.Length; }
        }
        s_w = w; s_dead = dead;
        return sum;
    }
    static long[] s_t = new long[4000];
    static int s_n;
    static long Med() { Array.Sort(s_t, 0, s_n); long m = s_t[s_n / 2] * 1000000000 / Stopwatch.Frequency; s_n = 0; return m; }

    [UnmanagedCallersOnly(EntryPoint = "main")] static void Main() => Run();
    public static void Run()
    {
        s_seed = 12345;
        for (int i = 0; i < N; i++) { s_master[i] = Make(); s_master[i].Life = i < 400 ? 0 : 50; }
        long sum = 0;
        for (int kind = 0; kind < 8; kind++)
        {
            for (int r = 0; r < 3000; r++)
            {
                Array.Copy(s_master, s_a, N);
                long t0 = Stopwatch.GetTimestamp();
                sum += kind == 0 ? LoopNoStore(s_a) : kind == 1 ? LoopCounted(s_a) : kind == 2 ? LoopMoved(s_a) : kind == 3 ? LoopRawOnly(s_a) : kind == 4 ? LoopRawOther(s_a) : kind == 5 ? LoopIntOther(s_a) : kind == 6 ? LoopRawNull(s_a) : LoopRawOld(s_a);
                long t1 = Stopwatch.GetTimestamp();
                if (r >= 1000) s_t[s_n++] = t1 - t0;
                if (kind == 2)
                {
                    // the counts put right: a slot left null had its count taken along; the dead that were written
                    // over still have theirs (the array holds them no more)
                    // (so the slots left null are given the dead, without a count, and the array cleared)
                    nint[] raw = Unsafe.As<nint[]>(s_a), from = Unsafe.As<nint[]>(s_master);
                    for (int i = 0; i < 400; i++) raw[N - 400 + i] = from[i];
                    Array.Clear(s_a);
                }
                if (kind == 3 || kind >= 6)
                {
                    // (every slot holds a survivor without a count of its own, or its own particle: all as they were)
                    nint[] raw = Unsafe.As<nint[]>(s_a), from = Unsafe.As<nint[]>(s_master);
                    for (int i = 0; i < N; i++) raw[i] = from[i];
                }
            }
            Console.WriteLine((kind == 0 ? "no store: " : kind == 1 ? "counted store: " : kind == 2 ? "moved, no counts: " : kind == 3 ? "the reference written, nothing else: " : kind == 4 ? "written into another array: " : kind == 5 ? "an int written into another array: " : kind == 6 ? "the reference written, its old slot set to null: " : "the reference written, what the slot held read and tested: ") + Med() + " " + sum + " " + s_w + " " + s_dead);
        }
    }
}
