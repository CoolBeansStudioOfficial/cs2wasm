// Game-like frame loops for tools/frames.mjs: each scene runs warm-up frames, then measured frames, timing every frame
// with Stopwatch and printing the times (ns) and a checksum. The same source is compiled by il2wasm and by NativeAOT-LLVM
// for WebAssembly; both run in Node (V8). What a frame does is fixed by a seeded generator, so both sides do the same work.
//   churn       a particle system: a few hundred objects (with a string each) born and dying every frame
//   collections a list and a dictionary that grow over 300 frames and are cleared, a queue that fills and drains
//   strings     a HUD rebuilt every frame: interpolation, StringBuilder, Split, Join, a dictionary of strings
//   tree        a scene tree of entities with parent links and child lists: a subtree replaced every frame, the whole
//               tree rebuilt every 500 frames (the drop of a graph with cycles)
//   bigdrop     a world of 150,000 acyclic objects grown over 400 frames, then dropped in one frame (the cascade free)
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

sealed class Particle { public float X, Y, Vx, Vy; public int Life; public string Tag = ""; }
sealed class Entity
{
    public Entity? Parent;
    public List<Entity> Children = new();
    public string Name = "";
    public float X, Y;
    public int Id;
}
sealed class Item { public Item? Next; public int[] Data = null!; public string? Label; public long V; }

static class Frames
{
    static uint s_seed;
    static uint Next() { s_seed = s_seed * 1664525u + 1013904223u; return s_seed >> 8; }
    static readonly string[] s_words = "ore wood stone iron gold gem fish meat herb cloth rope nail plank brick glass coal".Split(' ');
    static long s_check;

    public static void Run(string scene)
    {
        Func<int, long> frame = scene switch
        {
            "churn" => Churn,
            "collections" => Collections,
            "strings" => Strings,
            "tree" => Tree,
            "bigdrop" => BigDrop,
            _ => throw new ArgumentException("no scene " + scene),
        };
        const int warm = 1000, frames = 6000;
        s_seed = 12345;
        var times = new long[frames];
        for (int f = 0; f < warm + frames; f++)
        {
            long t0 = Stopwatch.GetTimestamp();
            s_check += frame(f);
            long t = Stopwatch.GetTimestamp() - t0;
            if (f >= warm) times[f - warm] = t * 1000000000 / Stopwatch.Frequency;
        }
        var sb = new StringBuilder("frames ").Append(scene).Append(' ');
        for (int i = 0; i < frames; i++) { if (i > 0) sb.Append(','); sb.Append(times[i]); }
        Console.WriteLine(sb.ToString());
        Console.WriteLine("check " + scene + " " + s_check);
    }

    // churn
    static List<Particle> s_particles = new();
    static long Churn(int f)
    {
        int born = 300 + (int)(Next() % 200);
        for (int i = 0; i < born; i++)
            s_particles.Add(new Particle { X = Next() % 640, Y = Next() % 480, Vx = (int)(Next() % 7) - 3, Vy = (int)(Next() % 7) - 3, Life = 20 + (int)(Next() % 60), Tag = s_words[Next() & 15] });
        long sum = 0;
        int w = 0;
        for (int i = 0; i < s_particles.Count; i++)
        {
            var p = s_particles[i];
            p.X += p.Vx; p.Y += p.Vy; p.Vy += 0.1f;
            if (--p.Life > 0) { s_particles[w++] = p; sum += p.Tag.Length; }
        }
        s_particles.RemoveRange(w, s_particles.Count - w);
        return sum + s_particles.Count;
    }

    // collections
    static List<int> s_list = new();
    static Dictionary<int, Particle> s_map = new();
    static Queue<string> s_queue = new();
    static long Collections(int f)
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

    // strings
    static Dictionary<string, int> s_names = new();
    static long Strings(int f)
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

    // tree
    static Entity? s_root;
    static int s_ids;
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
    static long Tree(int f)
    {
        if (s_root == null || f % 500 == 0) s_root = Build(null, 6, 5); // 19,531 entities
        // replace one subtree of 156 entities (depth 3 under the root's grandchildren)
        var a = s_root.Children[(int)(Next() % 5)];
        var b = a.Children[(int)(Next() % 5)];
        int k = (int)(Next() % 5);
        b.Children[k] = Build(b, 3, 5);
        // move a leaf group to another parent
        var c = s_root.Children[(int)(Next() % 5)].Children[(int)(Next() % 5)].Children[(int)(Next() % 5)];
        if (c.Children.Count > 0)
        {
            var moved = c.Children[c.Children.Count - 1];
            c.Children.RemoveAt(c.Children.Count - 1);
            var to = s_root.Children[(int)(Next() % 5)].Children[(int)(Next() % 5)];
            moved.Parent = to;
            to.Children.Add(moved);
        }
        return Walk(a, 0, 0);
    }

    // bigdrop
    static Item? s_world;
    static int s_worldCount;
    static long BigDrop(int f)
    {
        long sum = 0;
        if (f % 400 == 399) { sum = s_worldCount; s_world = null; s_worldCount = 0; return sum; } // the drop
        for (int i = 0; i < 375; i++)
        {
            var it = new Item { Next = s_world, Data = new int[4 + (int)(Next() & 7)], Label = (i & 3) == 0 ? s_words[i & 15] : null, V = Next() };
            it.Data[0] = i;
            s_world = it;
            s_worldCount++;
        }
        for (var it = s_world; it != null && sum < 2000; it = it.Next) sum += it.Data.Length;
        return sum;
    }
}
