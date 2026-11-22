// The collections and strings scenes of tools/frames.mjs by phase, for both sides (cs2wasm, and NativeAOT-LLVM through
// tools/parts-naot-wasm, whose Main calls Parts.Run): each frame does what the scene's frame does, in the scene's
// order and with the scene's generator, with a timestamp between the phases; per phase the mean of the middle half of the 6,000 measured frames
// ("mid": the clock's step is 100 ns, a median is a multiple of it), the median, the mean and p99, in nanoseconds. "clock" is two timestamps with nothing between them (what one costs).
//   cs2wasmc tools/colstr-parts.cs -o cp.wasm   then   node host/run.mjs cp.wasm parts
//   dotnet publish tools/parts-naot-wasm -c Release -o <dir> -p:Part=colstr-parts   then   node <dir>/parts-naot.js
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

sealed class Particle { public float X, Y, Vx, Vy; public int Life; public string Tag = ""; }

static class Parts
{
    static uint s_seed;
    static uint Next() { s_seed = s_seed * 1664525u + 1013904223u; return s_seed >> 8; }
    static readonly string[] s_words = "ore wood stone iron gold gem fish meat herb cloth rope nail plank brick glass coal".Split(' ');

    const int Warm = 1000, FrameCount = 6000, MaxPhases = 16;
    static long[][] s_t = new long[MaxPhases][];
    static string[] s_name = new string[MaxPhases];
    static int s_phases;
    static void Reset()
    {
        s_phases = 0;
        for (int i = 0; i < MaxPhases; i++) s_t[i] = new long[FrameCount];
        s_seed = 12345;
    }
    static void Report(string scene, long check)
    {
        long freq = Stopwatch.Frequency;
        long totalMid = 0;
        for (int p = 0; p < s_phases; p++)
        {
            var t = s_t[p];
            long sum = 0;
            for (int i = 0; i < FrameCount; i++) sum += t[i];
            Array.Sort(t);
            long mid = 0;
            for (int i = FrameCount / 4; i < FrameCount * 3 / 4; i++) mid += t[i];
            mid = mid * 1000000000 / freq / (FrameCount / 2);
            long med = t[FrameCount / 2] * 1000000000 / freq, mean = sum * 1000000000 / freq / FrameCount, p99 = t[FrameCount * 99 / 100] * 1000000000 / freq;
            if (s_name[p][0] != '(' && s_name[p] != "clock") totalMid += mid;
            Console.WriteLine(scene + " " + s_name[p].PadRight(22) + " mid " + mid.ToString().PadLeft(6) + "  median " + med.ToString().PadLeft(6) + "  mean " + mean.ToString().PadLeft(6) + "  p99 " + p99.ToString().PadLeft(6));
        }
        Console.WriteLine(scene + (s_phases == 1 ? " whole: check " : " sum of mids " + totalMid + "  check ") + check);
    }

    // collections
    static List<int> s_list = new();
    static Dictionary<int, Particle> s_map = new();
    static Queue<string> s_queue = new();
    static void ColClear(int f) { if (f % 300 == 0) { s_list.Clear(); s_map.Clear(); s_list.TrimExcess(); s_map.TrimExcess(); } }
    static void ColListAdd() { for (int i = 0; i < 200; i++) s_list.Add((int)Next()); }
    static void ColMapSet() { for (int i = 0; i < 100; i++) s_map[(int)(Next() % 50000)] = new Particle { Life = i }; }
    static void ColMapRemove() { for (int i = 0; i < 30; i++) s_map.Remove((int)(Next() % 50000)); }
    static long ColQueue(int f)
    {
        long sum = 0;
        int phase = f % 120;
        if (phase < 60) for (int i = 0; i < 50; i++) s_queue.Enqueue(s_words[Next() & 15]);
        else for (int i = 0; i < 50 && s_queue.Count > 0; i++) sum += s_queue.Dequeue().Length;
        return sum;
    }
    static long ColMapGet()
    {
        long sum = 0;
        for (int i = 0; i < 300; i++) if (s_map.TryGetValue((int)(Next() % 50000), out var p)) sum += p.Life;
        return sum;
    }
    static long ColListIndex() { return s_list[(int)(Next() % (uint)s_list.Count)]; }
    // (the generator alone, as many draws as a frame makes: 200 + 100 + 30 + 50 + 300 + 1; on a seed of its own)
    static uint s_seed2 = 99;
    static long ColNextOnly()
    {
        uint keep = s_seed; s_seed = s_seed2;
        long sum = 0;
        for (int i = 0; i < 681; i++) sum += Next() % 50000;
        s_seed2 = s_seed; s_seed = keep;
        return sum;
    }

    static void Collections()
    {
        Reset();
        s_name[0] = "clear+trim (1 in 300)"; s_name[1] = "list add 200"; s_name[2] = "map set 100 (new)"; s_name[3] = "map remove 30";
        s_name[4] = "queue 50"; s_name[5] = "map get 300"; s_name[6] = "list index"; s_name[7] = "clock"; s_name[8] = "(681 draws alone)"; s_phases = 9;
        long check = 0;
        for (int f = 0; f < Warm + FrameCount; f++)
        {
            long t0 = Stopwatch.GetTimestamp();
            ColClear(f);
            long t1 = Stopwatch.GetTimestamp();
            ColListAdd();
            long t2 = Stopwatch.GetTimestamp();
            ColMapSet();
            long t3 = Stopwatch.GetTimestamp();
            ColMapRemove();
            long t4 = Stopwatch.GetTimestamp();
            long sum = ColQueue(f);
            long t5 = Stopwatch.GetTimestamp();
            sum += ColMapGet();
            long t6 = Stopwatch.GetTimestamp();
            sum += ColListIndex();
            long t7 = Stopwatch.GetTimestamp();
            long t8 = Stopwatch.GetTimestamp();
            long x = ColNextOnly();
            long t9 = Stopwatch.GetTimestamp();
            check += sum + s_map.Count + (x & 0);
            if (f >= Warm)
            {
                int i = f - Warm;
                s_t[0][i] = t1 - t0; s_t[1][i] = t2 - t1; s_t[2][i] = t3 - t2; s_t[3][i] = t4 - t3; s_t[4][i] = t5 - t4; s_t[5][i] = t6 - t5; s_t[6][i] = t7 - t6; s_t[7][i] = t8 - t7; s_t[8][i] = t9 - t8;
            }
        }
        Report("collections", check);
    }

    // strings
    static Dictionary<string, int> s_names = new();
    static long StrLines(StringBuilder sb, int f)
    {
        long sum = 0;
        for (int i = 0; i < 40; i++)
        {
            string line = $"{s_words[i & 15]} x{i}: {f * 7 + i} hp {(f + i) % 100}/100";
            sb.Append(line).Append('|');
            sum += line.Length;
        }
        return sum;
    }
    static void StrNames()
    {
        for (int i = 0; i < 60; i++)
        {
            string key = s_words[Next() & 15] + (Next() % 64);
            s_names.TryGetValue(key, out int c);
            s_names[key] = c + 1;
        }
    }
    // (the lines' loop taken apart, each on its own after the frame: the interpolation alone, the appends alone, the
    // names' concatenation alone and the dictionary's two calls alone with keys made before)
    static long StrInterpOnly(int f)
    {
        long sum = 0;
        for (int i = 0; i < 40; i++)
        {
            string line = $"{s_words[i & 15]} x{i}: {f * 7 + i} hp {(f + i) % 100}/100";
            sum += line.Length;
        }
        return sum;
    }
    static string s_line = "stone x12: 21012 hp 14/100";
    static long StrAppendOnly()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 40; i++) sb.Append(s_line).Append('|');
        return sb.Length;
    }
    static string[] s_keys = new string[60];
    static long StrConcatOnly()
    {
        uint keep = s_seed; s_seed = s_seed2;
        long sum = 0;
        for (int i = 0; i < 60; i++) { string key = s_words[Next() & 15] + (Next() % 64); s_keys[i] = key; sum += key.Length; }
        s_seed2 = s_seed; s_seed = keep;
        return sum;
    }
    static Dictionary<string, int> s_names2 = new();
    static void StrDictOnly()
    {
        var keys = s_keys;
        for (int i = 0; i < keys.Length; i++)
        {
            string key = keys[i];
            s_names2.TryGetValue(key, out int c);
            s_names2[key] = c + 1;
        }
    }
    static long StrIntToString(int f)
    {
        long sum = 0;
        for (int i = 0; i < 40; i++) sum += (f * 7 + i).ToString().Length;
        return sum;
    }

    static void Strings()
    {
        Reset();
        s_name[0] = "40 lines + append"; s_name[1] = "sb.ToString"; s_name[2] = "Split"; s_name[3] = "Join 20"; s_name[4] = "60 names (concat+dict)";
        s_name[5] = "ToUpperInvariant"; s_name[6] = "IndexOf"; s_name[7] = "clock";
        s_name[8] = "(interpolation alone)"; s_name[9] = "(appends alone)"; s_name[10] = "(concat alone)"; s_name[11] = "(dict alone)"; s_name[12] = "(40 int.ToString)"; s_phases = 13;
        long check = 0;
        for (int f = 0; f < Warm + FrameCount; f++)
        {
            long t0 = Stopwatch.GetTimestamp();
            var sb = new StringBuilder();
            long sum = StrLines(sb, f);
            long t1 = Stopwatch.GetTimestamp();
            string hud = sb.ToString();
            long t2 = Stopwatch.GetTimestamp();
            var parts = hud.Split('|');
            long t3 = Stopwatch.GetTimestamp();
            string joined = string.Join(";", parts, 0, 20);
            sum += joined.Length + parts.Length;
            long t4 = Stopwatch.GetTimestamp();
            StrNames();
            if (f % 1000 == 999) s_names.Clear();
            long t5 = Stopwatch.GetTimestamp();
            string upper = hud.ToUpperInvariant();
            long t6 = Stopwatch.GetTimestamp();
            sum += upper.IndexOf("GOLD", StringComparison.Ordinal);
            long t7 = Stopwatch.GetTimestamp();
            long t8 = Stopwatch.GetTimestamp();
            long x = StrInterpOnly(f);
            long t9 = Stopwatch.GetTimestamp();
            x += StrAppendOnly();
            long t10 = Stopwatch.GetTimestamp();
            x += StrConcatOnly();
            long t11 = Stopwatch.GetTimestamp();
            StrDictOnly();
            if (f % 1000 == 999) s_names2.Clear();
            long t12 = Stopwatch.GetTimestamp();
            x += StrIntToString(f);
            long t13 = Stopwatch.GetTimestamp();
            check += sum + s_names.Count + (x & 0);
            if (f >= Warm)
            {
                int i = f - Warm;
                s_t[0][i] = t1 - t0; s_t[1][i] = t2 - t1; s_t[2][i] = t3 - t2; s_t[3][i] = t4 - t3; s_t[4][i] = t5 - t4; s_t[5][i] = t6 - t5; s_t[6][i] = t7 - t6; s_t[7][i] = t8 - t7;
                s_t[8][i] = t9 - t8; s_t[9][i] = t10 - t9; s_t[10][i] = t11 - t10; s_t[11][i] = t12 - t11; s_t[12][i] = t13 - t12;
            }
        }
        Report("strings", check);
    }

    // (the scenes whole, as tools/frames/Frames.cs has them: one timed call a frame)
    static long CollectionsWhole(int f)
    {
        if (f % 300 == 0) { s_list.Clear(); s_map.Clear(); s_list.TrimExcess(); s_map.TrimExcess(); }
        for (int i = 0; i < 200; i++) s_list.Add((int)Next());
        for (int i = 0; i < 100; i++) s_map[(int)(Next() % 50000)] = new Particle { Life = i };
        for (int i = 0; i < 30; i++) s_map.Remove((int)(Next() % 50000));
        long sum = 0;
        int phase = f % 120;
        if (phase < 60) for (int i = 0; i < 50; i++) s_queue.Enqueue(s_words[Next() & 15]);
        else for (int i = 0; i < 50 && s_queue.Count > 0; i++) sum += s_queue.Dequeue().Length;
        for (int i = 0; i < 300; i++) if (s_map.TryGetValue((int)(Next() % 50000), out var p)) sum += p.Life;
        sum += s_list[(int)(Next() % (uint)s_list.Count)];
        return sum + s_map.Count;
    }
    static long StringsWhole(int f)
    {
        long sum = 0;
        var sb = new StringBuilder();
        for (int i = 0; i < 40; i++)
        {
            string line = $"{s_words[i & 15]} x{i}: {f * 7 + i} hp {(f + i) % 100}/100";
            sb.Append(line).Append('|');
            sum += line.Length;
        }
        string hud = sb.ToString();
        var parts = hud.Split('|');
        string joined = string.Join(";", parts, 0, 20);
        sum += joined.Length + parts.Length;
        for (int i = 0; i < 60; i++)
        {
            string key = s_words[Next() & 15] + (Next() % 64);
            s_names.TryGetValue(key, out int c);
            s_names[key] = c + 1;
        }
        if (f % 1000 == 999) s_names.Clear();
        sum += hud.ToUpperInvariant().IndexOf("GOLD", StringComparison.Ordinal);
        return sum + s_names.Count;
    }

    // (the collections frame cut short after each phase, whole frames timed with no clock inside: what a phase adds
    // is the difference from the row above. The map and the list are as in the scene whatever comes after them.)
    static long ColUpTo1(int f)
    {
        if (f % 300 == 0) { s_list.Clear(); s_map.Clear(); s_list.TrimExcess(); s_map.TrimExcess(); }
        for (int i = 0; i < 200; i++) s_list.Add((int)Next());
        return s_list.Count;
    }
    static long ColUpTo2(int f)
    {
        if (f % 300 == 0) { s_list.Clear(); s_map.Clear(); s_list.TrimExcess(); s_map.TrimExcess(); }
        for (int i = 0; i < 200; i++) s_list.Add((int)Next());
        for (int i = 0; i < 100; i++) s_map[(int)(Next() % 50000)] = new Particle { Life = i };
        return s_map.Count;
    }
    // (the sets with one object made before: no allocation and no free, the map's work and the counts alone)
    static Particle s_one = new Particle();
    static long ColUpTo2One(int f)
    {
        if (f % 300 == 0) { s_list.Clear(); s_map.Clear(); s_list.TrimExcess(); s_map.TrimExcess(); }
        for (int i = 0; i < 200; i++) s_list.Add((int)Next());
        for (int i = 0; i < 100; i++) s_map[(int)(Next() % 50000)] = s_one;
        return s_map.Count;
    }
    // (the allocations alone: 100 objects a frame kept in an array, each taking the place of the one made 300 frames
    // before, which is then free)
    static Particle[] s_kept = new Particle[30000];
    static int s_keptAt;
    static long ColAllocOnly(int f)
    {
        var kept = s_kept;
        int at = s_keptAt;
        for (int i = 0; i < 100; i++) { kept[at] = new Particle { Life = i }; if (++at == kept.Length) at = 0; }
        s_keptAt = at;
        return at;
    }
    static long ColUpTo3(int f)
    {
        if (f % 300 == 0) { s_list.Clear(); s_map.Clear(); s_list.TrimExcess(); s_map.TrimExcess(); }
        for (int i = 0; i < 200; i++) s_list.Add((int)Next());
        for (int i = 0; i < 100; i++) s_map[(int)(Next() % 50000)] = new Particle { Life = i };
        for (int i = 0; i < 30; i++) s_map.Remove((int)(Next() % 50000));
        return s_map.Count;
    }
    static long ColUpTo4(int f)
    {
        if (f % 300 == 0) { s_list.Clear(); s_map.Clear(); s_list.TrimExcess(); s_map.TrimExcess(); }
        for (int i = 0; i < 200; i++) s_list.Add((int)Next());
        for (int i = 0; i < 100; i++) s_map[(int)(Next() % 50000)] = new Particle { Life = i };
        for (int i = 0; i < 30; i++) s_map.Remove((int)(Next() % 50000));
        long sum = 0;
        int phase = f % 120;
        if (phase < 60) for (int i = 0; i < 50; i++) s_queue.Enqueue(s_words[Next() & 15]);
        else for (int i = 0; i < 50 && s_queue.Count > 0; i++) sum += s_queue.Dequeue().Length;
        return sum + s_map.Count;
    }
    // (the whole frame with the lookups asking only whether the key is there: no value comes out)
    static long ColContains(int f)
    {
        if (f % 300 == 0) { s_list.Clear(); s_map.Clear(); s_list.TrimExcess(); s_map.TrimExcess(); }
        for (int i = 0; i < 200; i++) s_list.Add((int)Next());
        for (int i = 0; i < 100; i++) s_map[(int)(Next() % 50000)] = new Particle { Life = i };
        for (int i = 0; i < 30; i++) s_map.Remove((int)(Next() % 50000));
        long sum = 0;
        int phase = f % 120;
        if (phase < 60) for (int i = 0; i < 50; i++) s_queue.Enqueue(s_words[Next() & 15]);
        else for (int i = 0; i < 50 && s_queue.Count > 0; i++) sum += s_queue.Dequeue().Length;
        for (int i = 0; i < 300; i++) if (s_map.ContainsKey((int)(Next() % 50000))) sum += 1;
        sum += s_list[(int)(Next() % (uint)s_list.Count)];
        return sum + s_map.Count;
    }
    // (skip: the frames whose number is a multiple of it are timed as nothing: the collections scene's clear, one
    // frame in 300, which frees the map's objects here and nothing in NativeAOT's build, takes 20 of the 60 places
    // above p99)
    static void Whole(string scene, string what, Func<int, long> frame, int skip = 0)
    {
        Reset();
        s_queue.Clear(); s_names.Clear();
        s_name[0] = what; s_phases = 1;
        long check = 0;
        for (int f = 0; f < Warm + FrameCount; f++)
        {
            long t0 = Stopwatch.GetTimestamp();
            check += frame(f);
            long t1 = Stopwatch.GetTimestamp();
            if (f >= Warm) s_t[0][f - Warm] = skip != 0 && f % skip == 0 ? 0 : t1 - t0;
        }
        Report(scene, check);
    }

    [UnmanagedCallersOnly(EntryPoint = "parts")] static void Entry() => Run();
    public static void Run()
    {
        Collections();
        Whole("collections", "whole frame", CollectionsWhole);
        Whole("collections", "frame to list add", ColUpTo1);
        Whole("collections", "frame to map set", ColUpTo2);
        Whole("collections", "frame to map set, 1 obj", ColUpTo2One);
        Whole("collections", "(100 objects alone)", ColAllocOnly);
        Whole("collections", "frame to map remove", ColUpTo3);
        Whole("collections", "frame to queue", ColUpTo4);
        Whole("collections", "frame, ContainsKey", ColContains);
        Whole("collections", "whole frame again", CollectionsWhole);
        Whole("collections", "whole frame, no clears", CollectionsWhole, 300);
        Strings();
        Whole("strings", "whole frame", StringsWhole);
    }
}
