// A subtree of the scene tree of tools/frames.mjs (156 entities: 343 members with their lists and arrays) built and
// stored over the one before it, 20,000 times, nothing else: the build and the store (which lets the old subtree go:
// its members destroyed there through their owners) timed apart, median and mean, nanoseconds a store and a member.
// `Variant` picks what is replaced: 0 the subtree in a tree's list, as the scene does (`b.Children[k] = Build(b, 3, 5)`);
// 1 the same without parent fields (no up-reference in it); 2 the parent set after the child is in its list;
// 3 a subtree that is a group of its own, held by a static (let go whole: the group's drop, for comparison);
// 4 as 0, and a node of the old subtree held by a local over the store (the old subtree stays until the local goes).
//   sed "s/const int Variant = 0;/const int Variant = 3;/" tools/tree-store.cs > ts3.cs
//   cs2wasmc ts3.cs -o ts3.wasm   then   node host/run.mjs ts3.wasm
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

static class TreeStore
{
    const int Variant = 0;
    static uint s_seed;
    static uint Next() { s_seed = s_seed * 1664525u + 1013904223u; return s_seed >> 8; }
    static readonly string[] s_words = "ore wood stone iron gold gem fish meat herb cloth rope nail plank brick glass coal".Split(' ');
    static int s_ids;
    static Entity? s_root, s_alone;
    static Entity Build(Entity? parent, int depth, int fan)
    {
        var e = new Entity { Parent = parent, Id = s_ids++, Name = s_words[s_ids & 15], X = Next() % 100, Y = Next() % 100 };
        if (depth > 0) for (int i = 0; i < fan; i++) e.Children.Add(Build(e, depth - 1, fan));
        return e;
    }
    static Entity BuildNoParent(int depth, int fan)
    {
        var e = new Entity { Id = s_ids++, Name = s_words[s_ids & 15], X = Next() % 100, Y = Next() % 100 };
        if (depth > 0) for (int i = 0; i < fan; i++) e.Children.Add(BuildNoParent(depth - 1, fan));
        return e;
    }
    static Entity BuildParentAfter(int depth, int fan)
    {
        var e = new Entity { Id = s_ids++, Name = s_words[s_ids & 15], X = Next() % 100, Y = Next() % 100 };
        if (depth > 0) for (int i = 0; i < fan; i++) { var c = BuildParentAfter(depth - 1, fan); e.Children.Add(c); c.Parent = e; }
        return e;
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        const int warm = 2000, n = 20000;
        s_seed = 12345;
        s_root = Variant == 1 ? BuildNoParent(5, 5) : Variant == 2 ? BuildParentAfter(5, 5) : Build(null, 5, 5);
        var tb = new long[n]; var ts = new long[n];
        long sb = 0, ss = 0, check = 0;
        for (int f = 0; f < warm + n; f++)
        {
            var b = s_root.Children[(int)(Next() % 5)].Children[(int)(Next() % 5)];
            int k = (int)(Next() % 5);
            Entity? keep = Variant == 4 ? b.Children[k].Children[2] : null;
            long t0 = Stopwatch.GetTimestamp();
            Entity sub;
            switch (Variant)
            {
                case 1: sub = BuildNoParent(3, 5); break;
                case 2: sub = BuildParentAfter(3, 5); break;
                case 3: sub = Build(null, 3, 5); break;
                default: sub = Build(b, 3, 5); break;
            }
            long t1 = Stopwatch.GetTimestamp();
            if (Variant == 3) s_alone = sub;
            else
            {
                b.Children[k] = sub;
                if (Variant == 2) sub.Parent = b;
            }
            sub = null!;
            long t2 = Stopwatch.GetTimestamp();
            if (keep != null) { check += keep.Parent == null ? 0 : keep.Parent.Children.Count; keep = null; }
            if (f >= warm) { tb[f - warm] = t1 - t0; ts[f - warm] = t2 - t1; sb += t1 - t0; ss += t2 - t1; }
        }
        Array.Sort(tb); Array.Sort(ts);
        double per = Stopwatch.Frequency / 1e9;
        Console.WriteLine("variant " + Variant + " build med " + (tb[n / 2] / per).ToString("F0") + " mean " + (sb / per / n).ToString("F0") + " ns (" + (tb[n / 2] / per / 156).ToString("F1")
            + " an entity); store med " + (ts[n / 2] / per).ToString("F0") + " mean " + (ss / per / n).ToString("F0") + " ns (" + (ts[n / 2] / per / 343).ToString("F1") + " a member) check " + check);
    }
}
