// Where an object's allocation and free go, for both sides (cs2wasm, and NativeAOT-LLVM through
// tools/parts-naot-wasm): 100 objects of the collections scene's kind (40 bytes: four floats, an int, a string) a
// frame, 6,000 frames, in parts: made and kept in an array of 30,000 that starts empty (the allocation, the
// constructor's stores and the store into the array, nothing freed: the array is cleared outside the clock every 300
// frames); the same with the array full, each new object taking the place of one made 300 frames before (the
// allocation and a free); the frees alone (100 places set to null); and the array's stores alone (one object made
// before, stored 100 times: the counts on our side, the write barrier on theirs).
//   node tools/parts.mjs alloc-parts
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

sealed class Particle { public float X, Y, Vx, Vy; public int Life; public string Tag = ""; }

static class Parts
{
    const int Warm = 1000, FrameCount = 6000;
    static Particle[] s_kept = new Particle[30000];
    static Particle s_one = new Particle();

    static long Make(int at) { var kept = s_kept; for (int i = 0; i < 100; i++) kept[at + i] = new Particle { Life = i }; return at; }
    static long Free(int at) { var kept = s_kept; for (int i = 0; i < 100; i++) kept[at + i] = null!; return at; }
    static long Store(int at) { var kept = s_kept; var one = s_one; for (int i = 0; i < 100; i++) kept[at + i] = one; return at; }

    static void Row(string name, long[] t)
    {
        long freq = Stopwatch.Frequency, sum = 0;
        for (int i = 0; i < FrameCount; i++) sum += t[i];
        Array.Sort(t);
        long mid = 0;
        for (int i = FrameCount / 4; i < FrameCount * 3 / 4; i++) mid += t[i];
        mid = mid * 1000000000 / freq / (FrameCount / 2);
        Console.WriteLine("alloc " + name.PadRight(30) + " mid " + mid.ToString().PadLeft(6) + "  median " + (t[FrameCount / 2] * 1000000000 / freq).ToString().PadLeft(6)
            + "  mean " + (sum * 1000000000 / freq / FrameCount).ToString().PadLeft(6) + "  p99 " + (t[FrameCount * 99 / 100] * 1000000000 / freq).ToString().PadLeft(6));
    }

    [UnmanagedCallersOnly(EntryPoint = "parts")] static void Entry() => Run();
    public static void Run()
    {
        var t = new long[FrameCount];
        long x = 0;
        // made and kept: nothing freed inside the clock
        for (int f = 0; f < Warm + FrameCount; f++)
        {
            int at = f % 300 * 100;
            if (at == 0) Array.Clear(s_kept);
            long t0 = Stopwatch.GetTimestamp();
            x += Make(at);
            long t1 = Stopwatch.GetTimestamp();
            if (f >= Warm) t[f - Warm] = t1 - t0;
        }
        Row("100 made, kept", t);
        // made, each in the place of an old one: an allocation and a free
        for (int f = 0; f < Warm + FrameCount; f++)
        {
            int at = f % 300 * 100;
            long t0 = Stopwatch.GetTimestamp();
            x += Make(at);
            long t1 = Stopwatch.GetTimestamp();
            if (f >= Warm) t[f - Warm] = t1 - t0;
        }
        Row("100 made over old ones", t);
        // the frees alone
        for (int f = 0; f < Warm + FrameCount; f++)
        {
            int at = f % 300 * 100;
            Make(at);
            long t0 = Stopwatch.GetTimestamp();
            x += Free(at);
            long t1 = Stopwatch.GetTimestamp();
            if (f >= Warm) t[f - Warm] = t1 - t0;
        }
        Row("100 let go", t);
        Array.Clear(s_kept);
        for (int f = 0; f < Warm + FrameCount; f++)
        {
            int at = f % 300 * 100;
            long t0 = Stopwatch.GetTimestamp();
            x += Store(at);
            long t1 = Stopwatch.GetTimestamp();
            if (f >= Warm) t[f - Warm] = t1 - t0;
        }
        Row("100 stores of one object", t);
        Console.WriteLine("alloc sum of mids 0  check " + (x & 0));
    }
}
