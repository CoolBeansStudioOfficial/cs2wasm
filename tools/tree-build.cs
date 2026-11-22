// The tree of the scene tree of tools/frames.mjs built and let go 60 times, nothing else (tools/tree-parts.cs has the
// scene's frames between its rebuilds, so its old tree has lived a while): the build and the drop timed apart, the
// lowest and the median of the last 40. `Variant` picks what is built, to tell the parts of a build apart:
// 0 the tree as the scene builds it (fan 5, depth 6: 19,531 entities, the parent set before the child is in its list);
// 1 no parent field set; 2 the parent set after the child is in its parent's list; 3 each list's capacity set to 8
// first (no growth from 4 to 8); 4 the objects alone, made and let go, no list of them; 5 fan 4, depth 7 (21,845
// entities, 5,461 with children, a list never grows).
//   sed "s/const int Variant = 0;/const int Variant = 3;/" tools/tree-build.cs > tb3.cs
//   cs2wasmc tb3.cs -o tb3.wasm   then   node host/run.mjs tb3.wasm
// With CS2WASM_DROP_STATS=1 at compile time the runtime prints, at each drop, how often each path of the arena
// operations ran so far: the difference of two such lines is one build and one drop.
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

static class Bo
{
    const int Variant = 0;
    static uint s_seed;
    static uint Next() { s_seed = s_seed * 1664525u + 1013904223u; return s_seed >> 8; }
    static readonly string[] s_words = "ore wood stone iron gold gem fish meat herb cloth rope nail plank brick glass coal".Split(' ');
    static int s_ids;
    static Entity? s_root;
    // 0: the tree as in the scene (fan 5, depth 6)
    static Entity Build(Entity? parent, int depth, int fan)
    {
        var e = new Entity { Parent = parent, Id = s_ids++, Name = s_words[s_ids & 15], X = Next() % 100, Y = Next() % 100 };
        if (depth > 0) for (int i = 0; i < fan; i++) e.Children.Add(Build(e, depth - 1, fan));
        return e;
    }
    // 1: no parent field set
    static Entity BuildNoParent(int depth, int fan)
    {
        var e = new Entity { Id = s_ids++, Name = s_words[s_ids & 15], X = Next() % 100, Y = Next() % 100 };
        if (depth > 0) for (int i = 0; i < fan; i++) e.Children.Add(BuildNoParent(depth - 1, fan));
        return e;
    }
    // 2: the parent field set after the Add
    static Entity BuildParentAfter(int depth, int fan)
    {
        var e = new Entity { Id = s_ids++, Name = s_words[s_ids & 15], X = Next() % 100, Y = Next() % 100 };
        if (depth > 0) for (int i = 0; i < fan; i++) { var c = BuildParentAfter(depth - 1, fan); e.Children.Add(c); c.Parent = e; }
        return e;
    }
    // 3: capacity set first (no growth 4 to 8)
    static Entity BuildCap(Entity? parent, int depth, int fan)
    {
        var e = new Entity { Parent = parent, Id = s_ids++, Name = s_words[s_ids & 15], X = Next() % 100, Y = Next() % 100 };
        if (depth > 0) { e.Children.Capacity = 8; for (int i = 0; i < fan; i++) e.Children.Add(BuildCap(e, depth - 1, fan)); }
        return e;
    }
    // 4: objects alone: entities made and let go (no links)
    static int Alone(int depth, int fan)
    {
        var e = new Entity { Id = s_ids++, Name = s_words[s_ids & 15], X = Next() % 100, Y = Next() % 100 };
        int n = e.Id & 1;
        if (depth > 0) for (int i = 0; i < fan; i++) n += Alone(depth - 1, fan);
        return n;
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        var builds = new List<double>(); var drops = new List<double>();
        int fan = Variant == 5 ? 4 : 5, depth = Variant == 5 ? 7 : 6;
        for (int f = 0; f < 60; f++)
        {
            long t0 = Stopwatch.GetTimestamp();
            Entity? n = null;
            int sink = 0;
            switch (Variant)
            {
                case 0: case 5: n = Build(null, depth, fan); break;
                case 1: n = BuildNoParent(depth, fan); break;
                case 2: n = BuildParentAfter(depth, fan); break;
                case 3: n = BuildCap(null, depth, fan); break;
                case 4: sink = Alone(depth, fan); break;
            }
            long t1 = Stopwatch.GetTimestamp();
            s_root = n;
            n = null;
            long t2 = Stopwatch.GetTimestamp();
            if (f >= 20) { builds.Add((t1 - t0) * 1000.0 / Stopwatch.Frequency); drops.Add((t2 - t1) * 1000.0 / Stopwatch.Frequency); }
        }
        builds.Sort(); drops.Sort();
        Console.WriteLine("variant " + Variant + " build min " + builds[0].ToString("F2") + " med " + builds[builds.Count / 2].ToString("F2") + " ms; drop min " + drops[0].ToString("F2") + " med " + drops[drops.Count / 2].ToString("F2") + " ms");
    }
}
