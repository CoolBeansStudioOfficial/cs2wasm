// What a loop pays when its counts' other path is the usual one (docs/AUDIT.md A3.5, "The churn loop, round 5":
// Wasm/ColdExits.cs writes that path after the loop): an array of 20,000 objects cleared, every one of them ending
// there (the range's release: each turn takes the other path), half of them, none (each has a second reference);
// and a loop that stores into an array whose every old value ends. Median ns a run.
//   cs2wasmc tools/drop-range.cs -o dr.wasm   then   node host/run.mjs dr.wasm
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

    static void Fill(int keepEvery)
    {
        for (int i = 0; i < N; i++)
        {
            var t = new Thing { A = i, Tag = "t" };
            s_a[i] = t;
            s_keep[i] = keepEvery != 0 && i % keepEvery == 0 ? t : null!;
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
        string[] names = { "cleared, all end", "cleared, half end", "cleared, none ends", "stored over, all end", "stored over, none ends" };
        for (int kind = 0; kind < names.Length; kind++)
        {
            for (int r = 0; r < 500; r++)
            {
                Fill(kind == 0 || kind == 3 ? 0 : kind == 1 ? 2 : 1);
                long t0 = Stopwatch.GetTimestamp();
                if (kind < 3) Array.Clear(s_a); else sum += Store(s_a, s_src);
                long t1 = Stopwatch.GetTimestamp();
                if (r >= 100) s_t[s_n++] = t1 - t0;
                Array.Clear(s_a);
            }
            Console.WriteLine(names[kind] + ": " + Med() + " " + sum);
        }
    }
}
