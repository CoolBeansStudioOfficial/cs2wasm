// What the particles that end inside the churn scene's loop cost it (docs/AUDIT.md A3.5, "The churn loop, round 5"):
// the scene as tools/frames/Frames.cs has it, and the same scene with every particle also held by a second list
// that is cleared outside the timed loop (after it, every frame): the same births, the same moves, the same branch
// taken for the same particles, but no particle's last reference is the slot written over. Then the scene with
// particles whose string is made (it ends with its particle) instead of a literal; and with every particle given the
// same life, so that a frame's dead are the frame's oldest, side by side at the list's start (the processor predicts
// the branch, and the path that lets an object go runs 400 times in a row instead of once in 48 turns). Median ns a
// frame of the loop alone, and the mean of the clearing.
//   cs2wasmc tools/churn-deaths.cs -o cd.wasm   then   node host/run.mjs cd.wasm
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

sealed class Particle { public float X, Y, Vx, Vy; public int Life; public string Tag = ""; }

static class ChurnDeaths
{
    static uint s_seed;
    static uint Next() { s_seed = s_seed * 1664525u + 1013904223u; return s_seed >> 8; }
    static readonly string[] s_words = "ore wood stone iron gold gem fish meat herb cloth rope nail plank brick glass coal".Split(' ');
    static List<Particle> s_particles = new();
    static List<Particle> s_hold = new();
    static bool s_held, s_made, s_sameLife;
    static Particle Make() => new Particle { X = Next() % 640, Y = Next() % 480, Vx = (int)(Next() % 7) - 3, Vy = (int)(Next() % 7) - 3, Life = s_sameLife ? 50 + (int)(Next() % 60) * 0 : 20 + (int)(Next() % 60), Tag = s_made ? string.Concat(s_words[Next() & 15], "!") : s_words[Next() & 15] };
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
    // (the dead of this frame: found before the loop, held until after it)
    static void Hold()
    {
        for (int i = 0; i < s_particles.Count; i++) if (s_particles[i].Life <= 1) s_hold.Add(s_particles[i]);
    }
    static long[] s_t = new long[8000];
    static int s_n;
    static long Med() { Array.Sort(s_t, 0, s_n); long m = s_t[s_n / 2] * 1000000000 / Stopwatch.Frequency; s_n = 0; return m; }

    static void Scene(string name)
    {
        const int warm = 1000, frames = 6000;
        s_particles = new(); s_hold = new(); s_seed = 12345;
        long check = 0, clear = 0, dead = 0;
        for (int f = 0; f < warm + frames; f++)
        {
            Birth();
            if (s_held) Hold();
            long t1 = Stopwatch.GetTimestamp();
            check += Loop();
            long t2 = Stopwatch.GetTimestamp();
            s_particles.RemoveRange(s_w, s_particles.Count - s_w);
            long t3 = Stopwatch.GetTimestamp();
            dead += s_hold.Count;
            s_hold.Clear();
            long t4 = Stopwatch.GetTimestamp();
            if (f >= warm) { s_t[s_n++] = t2 - t1; clear += t4 - t3; }
        }
        Console.WriteLine(name + ": " + Med() + " the dead let go after the loop, mean ns a frame: " + clear * 1000000000 / Stopwatch.Frequency / frames + " dead a frame " + dead / (warm + frames) + " check " + check);
    }

    [UnmanagedCallersOnly(EntryPoint = "main")] static void Main() => Run();
    public static void Run()
    {
        Scene("the scene");
        s_held = true; Scene("the scene, its dead held until after the loop");
        s_held = false; s_made = true; Scene("the scene, each particle's string its own");
        s_held = true; Scene("the same, its dead held until after the loop");
        s_made = false; s_sameLife = true;
        s_held = false; Scene("every life the same: the dead side by side");
        s_held = true; Scene("the same, its dead held until after the loop");
    }
}
