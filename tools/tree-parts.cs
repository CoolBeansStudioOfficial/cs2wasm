// The tree scene of tools/frames.mjs in parts: its frames as there (a subtree of 156 entities replaced every frame, a
// leaf group moved, the walk), the whole tree rebuilt every 500th, and the rebuild's two halves timed apart: the build
// of 19,531 new entities, and the store into the static that lets the old tree go (its drop, of the old tree and of
// whatever let-go part stayed with its group). Prints the lowest and the median of the 22 rebuilds after the first,
// the median and p99 of the other frames, and how many entities the last old tree had.
//   cs2wasmc tools/tree-parts.cs -o tp.wasm   then   node host/run.mjs tp.wasm
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

sealed class Entity
{
    public Entity? Parent;
    public List<Entity> Children = new();
    public string Name = "";
    public float X, Y;
    public int Id;
}

static class TreeParts
{
    static uint s_seed;
    static uint Next() { s_seed = s_seed * 1664525u + 1013904223u; return s_seed >> 8; }
    static readonly string[] s_words = "ore wood stone iron gold gem fish meat herb cloth rope nail plank brick glass coal".Split(' ');
    static int s_ids;
    static Entity? s_root;
    static Entity Build(Entity? parent, int depth, int fan)
    {
        var e = new Entity { Parent = parent, Id = s_ids++, Name = s_words[s_ids & 15], X = Next() % 100, Y = Next() % 100 };
        if (depth > 0) for (int i = 0; i < fan; i++) e.Children.Add(Build(e, depth - 1, fan));
        return e;
    }
    static long Walk(Entity e, float px, float py)
    {
        long s = e.Id + e.Name.Length + (long)(px + py);
        foreach (var c in e.Children) s += Walk(c, px + e.X, py + e.Y);
        return s;
    }
    static int Count(Entity e)
    {
        int n = 1;
        foreach (var c in e.Children) n += Count(c);
        return n;
    }
    static double s_build, s_drop, s_rest;
    static int s_reachable;
    static long Frame(int f)
    {
        if (s_root == null || f % 500 == 0)
        {
            s_reachable = s_root == null ? 0 : Count(s_root);
            long t0 = Stopwatch.GetTimestamp();
            var n = Build(null, 6, 5);
            long t1 = Stopwatch.GetTimestamp();
            s_root = n;
            n = null;
            long t2 = Stopwatch.GetTimestamp();
            s_build = (t1 - t0) * 1000.0 / Stopwatch.Frequency; s_drop = (t2 - t1) * 1000.0 / Stopwatch.Frequency;
        }
        long t3 = Stopwatch.GetTimestamp();
        var a = s_root.Children[(int)(Next() % 5)];
        var b = a.Children[(int)(Next() % 5)];
        int k = (int)(Next() % 5);
        b.Children[k] = Build(b, 3, 5);
        var c = s_root.Children[(int)(Next() % 5)].Children[(int)(Next() % 5)].Children[(int)(Next() % 5)];
        if (c.Children.Count > 0)
        {
            var moved = c.Children[c.Children.Count - 1];
            c.Children.RemoveAt(c.Children.Count - 1);
            var to = s_root.Children[(int)(Next() % 5)].Children[(int)(Next() % 5)];
            moved.Parent = to;
            to.Children.Add(moved);
        }
        long r = Walk(a, 0, 0);
        s_rest = (Stopwatch.GetTimestamp() - t3) * 1000.0 / Stopwatch.Frequency;
        return r;
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        long sum = 0;
        var builds = new List<double>(); var drops = new List<double>(); var rest = new List<double>();
        int reach = 0;
        for (int f = 0; f < 12001; f++)
        {
            sum += Frame(f);
            if (f >= 1000) rest.Add(s_rest);
            if (f % 500 == 0 && f > 500) { builds.Add(s_build); drops.Add(s_drop); reach = s_reachable; }
        }
        builds.Sort(); drops.Sort(); rest.Sort();
        string F(double v) => v.ToString("F2");
        Console.WriteLine("build min " + F(builds[0]) + " med " + F(builds[builds.Count / 2]) + " ms; drop min " + F(drops[0]) + " med " + F(drops[drops.Count / 2])
            + " ms; other frames med " + rest[rest.Count / 2].ToString("F3") + " p99 " + rest[rest.Count * 99 / 100].ToString("F3") + " ms; last old tree " + reach + " entities");
        Console.WriteLine(sum);
    }
}
