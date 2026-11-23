// What a loop pays when its counts' other path is the usual one (docs/AUDIT.md A3.5, "The churn loop, round 5":
// Wasm/ColdExits.cs writes that path after the loop): an array of 20,000 objects cleared, every one of them ending
// there (the range's release: each turn takes the other path), one in 2, 4, 10 and 50 of them at random places, none
// (each has a second reference); and a loop that stores into an array whose old values end at those rates. Median ns a run.
//   il2wasm tools/drop-range.cs -o dr.wasm   then   node host/run.mjs dr.wasm
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

sealed class Thing { public int A, B; public string Tag = ""; }

static class DropRange
{
    const int N = 20000;
    static Thing[] s_a = new Thing[N], s_keep = new Thing[N], s_src = new Thing[N];
    static long[] s_t = new long[400];
    static int s_n;
    static long Med() { Array.Sort(s_t, 0, s_n); long m = s_t[s_n / 2] * 1000000000 / Stopwatch.Frequency; s_n = 0; return m; }

    static uint s_seed = 12345;
    static uint Next() { s_seed = s_seed * 1664525u + 1013904223u; return s_seed >> 8; }
    // (one in endOneIn ends where it is let go, at random places; 0: none; 1: all)
    static void Fill(int endOneIn)
    {
        for (int i = 0; i < N; i++)
        {
            var t = new Thing { A = i, Tag = "t" };
            s_a[i] = t;
            s_keep[i] = endOneIn == 0 || endOneIn > 1 && Next() % (uint)endOneIn != 0 ? t : null!;
        }
    }
    static int Store(Thing[] a, Thing[] src)
    {
        int n = 0;
        for (int i = 0; i < a.Length; i++) { a[i] = src[i]; n += i; }
        return n;
    }

    [UnmanagedCallersOnly(EntryPoint = "main")] static void Main() => Run();
    public static void Run()
    {
        long sum = 0;
        for (int i = 0; i < N; i++) s_src[i] = new Thing { A = -i };
        string[] names = { "cleared, all end", "cleared, 1 in 2 ends", "cleared, 1 in 4", "cleared, 1 in 10", "cleared, 1 in 50", "cleared, none ends", "stored over, all end", "stored over, 1 in 4", "stored over, 1 in 10", "stored over, 1 in 50", "stored over, none ends" };
        int[] rate = { 1, 2, 4, 10, 50, 0, 1, 4, 10, 50, 0 };
        for (int kind = 0; kind < names.Length; kind++)
        {
            for (int r = 0; r < 500; r++)
            {
                Fill(rate[kind]);
                long t0 = Stopwatch.GetTimestamp();
                if (kind < 6) Array.Clear(s_a); else sum += Store(s_a, s_src);
                long t1 = Stopwatch.GetTimestamp();
                if (r >= 100) s_t[s_n++] = t1 - t0;
                Array.Clear(s_a);
            }
            Console.WriteLine(names[kind] + ": " + Med() + " " + sum);
        }
    }
}
