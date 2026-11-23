// A node of the scene tree of tools/frames.mjs by its steps (docs/AUDIT.md A3.5, the build, round 10): N nodes made
// under one living node whose list has room, each with K leaves, in the scene's order (the parent set as the object
// is made, the node put into its parent's list after its children), then all let go at one store. Nanoseconds a
// node, the lowest of 30 rounds, for K = 0 (a leaf by another name), 1, 2, 4, 5 (the list grows from four to eight),
// and for 5 with the capacity set first; `drop` is the nanoseconds a node to let them go.
//   il2wasm tools/tree-node.cs -o tn.wasm   then   node host/run.mjs tn.wasm
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

static class Tn
{
    const int N = 4000;
    static Entity? s_root;
    static int s_ids;
    static readonly string[] s_words = "ore wood stone iron gold gem fish meat herb cloth rope nail plank brick glass coal".Split(' ');

    static Entity Node(Entity parent, int k, bool cap)
    {
        var e = new Entity { Parent = parent, Id = s_ids++, Name = s_words[s_ids & 15], X = 1, Y = 2 };
        if (cap) e.Children.Capacity = 8;
        for (int i = 0; i < k; i++) e.Children.Add(new Entity { Parent = e, Id = s_ids++, Name = s_words[s_ids & 15], X = 1, Y = 2 });
        return e;
    }

    static void Round(int k, bool cap, out long build, out long drop)
    {
        Entity? holder = s_root!.Children[0];
        long t0 = Stopwatch.GetTimestamp();
        for (int i = 0; i < N; i++) holder.Children.Add(Node(holder, k, cap));
        long t1 = Stopwatch.GetTimestamp();
        holder = null;
        var fresh = new Entity { Parent = s_root };
        fresh.Children.Capacity = N;
        long t1b = Stopwatch.GetTimestamp();
        s_root.Children[0] = fresh;
        long t2 = Stopwatch.GetTimestamp();
        build = t1 - t0; drop = t2 - t1b;
    }

    static void Measure(string name, int k, bool cap)
    {
        long bb = long.MaxValue, dd = long.MaxValue;
        for (int r = 0; r < 30; r++)
        {
            Round(k, cap, out long b, out long d);
            if (b < bb) bb = b;
            if (d < dd) dd = d;
        }
        Console.WriteLine(name + ": " + (bb * 1000000000.0 / Stopwatch.Frequency / N).ToString("F0") + " ns a node, drop " + (dd * 1000000000.0 / Stopwatch.Frequency / N).ToString("F0") + " (" + (dd * 1000000000.0 / Stopwatch.Frequency / N / (k + 1)).ToString("F1") + " an entity)");
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        s_root = new Entity();
        var h = new Entity { Parent = s_root };
        h.Children.Capacity = N;
        s_root.Children.Add(h);
        Measure("k=0", 0, false);
        Measure("k=1", 1, false);
        Measure("k=2", 2, false);
        Measure("k=4", 4, false);
        Measure("k=5", 5, false);
        Measure("k=5 capacity first", 5, true);
        Measure("k=0 capacity first", 0, true);
    }
}
