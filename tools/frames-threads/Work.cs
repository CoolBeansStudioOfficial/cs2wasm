// The work of tools/frames-threads.mjs's scenes, with no thread in it (the same file is compiled into the program
// with threads and into the one without):
//   Worker   one thread's private game state: a particle system (pchurn) or a scene tree with parent links (ptree),
//            as tools/frames/Frames.cs's churn and tree, in fields of an object made by the thread that runs it.
//            Its types (WParticle, WEntity) are stored nowhere another thread can reach.
//   Jobs     work items and results handed between threads through two queues under a lock (shared).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;

sealed class WParticle { public float X, Y, Vx, Vy; public int Life; public string Tag = ""; }
sealed class WEntity
{
    public WEntity? Parent;
    public List<WEntity> Children = new();
    public string Name = "";
    public float X, Y;
    public int Id;
}

sealed class Worker
{
    uint _seed;
    readonly string[] _words = "ore wood stone iron gold gem fish meat herb cloth rope nail plank brick glass coal".Split(' ');
    readonly List<WParticle> _particles = new();
    WEntity? _root;
    int _ids;
    public long Check;
    public long[] Times = Array.Empty<long>();

    public Worker(int id) { _seed = 12345 + (uint)id * 7919; }
    uint Next() { _seed = _seed * 1664525u + 1013904223u; return _seed >> 8; }

    public void Run(bool tree, int warm, int frames)
    {
        var times = new long[frames];
        for (int f = 0; f < warm + frames; f++)
        {
            long t0 = Stopwatch.GetTimestamp();
            Check += tree ? Tree(f) : Churn(f);
            long t = Stopwatch.GetTimestamp() - t0;
            if (f >= warm) times[f - warm] = t * 1000000000 / Stopwatch.Frequency;
        }
        Times = times;
    }

    long Churn(int f)
    {
        int born = 300 + (int)(Next() % 200);
        for (int i = 0; i < born; i++)
            _particles.Add(new WParticle { X = Next() % 640, Y = Next() % 480, Vx = (int)(Next() % 7) - 3, Vy = (int)(Next() % 7) - 3, Life = 20 + (int)(Next() % 60), Tag = _words[Next() & 15] });
        long sum = 0;
        int w = 0;
        for (int i = 0; i < _particles.Count; i++)
        {
            var p = _particles[i];
            p.X += p.Vx; p.Y += p.Vy; p.Vy += 0.1f;
            if (--p.Life > 0) { _particles[w++] = p; sum += p.Tag.Length; }
        }
        _particles.RemoveRange(w, _particles.Count - w);
        return sum + _particles.Count;
    }

    WEntity Build(WEntity? parent, int depth, int fan)
    {
        var e = new WEntity { Parent = parent, Id = _ids++, Name = _words[_ids & 15], X = Next() % 100, Y = Next() % 100 };
        if (depth > 0) for (int i = 0; i < fan; i++) e.Children.Add(Build(e, depth - 1, fan));
        return e;
    }
    static long Walk(WEntity e, float px, float py)
    {
        long s = e.Id + e.Name.Length + (long)(px + py);
        foreach (var c in e.Children) s += Walk(c, px + e.X, py + e.Y);
        return s;
    }
    long Tree(int f)
    {
        if (_root == null || f % 250 == 0) _root = Build(null, 5, 5); // 3,906 entities
        var a = _root.Children[(int)(Next() % 5)];
        var b = a.Children[(int)(Next() % 5)];
        int k = (int)(Next() % 5);
        b.Children[k] = Build(b, 2, 5); // a subtree of 31 replaced
        var c = _root.Children[(int)(Next() % 5)].Children[(int)(Next() % 5)].Children[(int)(Next() % 5)];
        if (c.Children.Count > 0)
        {
            var moved = c.Children[c.Children.Count - 1];
            c.Children.RemoveAt(c.Children.Count - 1);
            var to = _root.Children[(int)(Next() % 5)].Children[(int)(Next() % 5)];
            moved.Parent = to;
            to.Children.Add(moved);
        }
        return Walk(a, 0, 0);
    }
}

sealed class Job { public int Id; public int[] Data = null!; public string Label = ""; }
sealed class JobResult { public int Id; public long Sum; public string Text = ""; }

/// <summary>A queue two threads meet at: Put wakes a taker, Take waits for an item.</summary>
sealed class Channel<T> where T : class
{
    readonly Queue<T> _items = new();
    public void Put(T item) { lock (_items) { _items.Enqueue(item); Monitor.Pulse(_items); } }
    public T Take() { lock (_items) { while (_items.Count == 0) Monitor.Wait(_items); return _items.Dequeue(); } }
    public T? TryTake() { lock (_items) return _items.Count == 0 ? null : _items.Dequeue(); }
}

static class Jobs
{
    public const int PerFrame = 64;
    static readonly string[] s_labels = "ore wood stone iron gold gem fish meat herb cloth rope nail plank brick glass coal".Split(' ');
    public static readonly Channel<Job> In = new();
    public static readonly Channel<JobResult> Out = new();
    static uint s_seed = 777;
    static uint Next() { s_seed = s_seed * 1664525u + 1013904223u; return s_seed >> 8; }

    /// <summary>The frame's work items, made by the thread that hands them out.</summary>
    public static void Post(int f)
    {
        for (int i = 0; i < PerFrame; i++)
        {
            var d = new int[48 + (int)(Next() & 15)];
            for (int k = 0; k < d.Length; k++) d[k] = (int)(Next() & 1023);
            In.Put(new Job { Id = f * PerFrame + i, Data = d, Label = s_labels[Next() & 15] });
        }
    }

    /// <summary>One item's work: sums, a sort of a copy, a string.</summary>
    public static JobResult Process(Job j)
    {
        long sum = 0;
        var copy = new List<int>(j.Data.Length);
        foreach (var v in j.Data) { sum += v; if ((v & 3) != 0) copy.Add(v); }
        copy.Sort();
        var sb = new StringBuilder(j.Label);
        for (int i = 0; i < copy.Count; i += 8) sb.Append(':').Append(copy[i]);
        return new JobResult { Id = j.Id, Sum = sum + copy.Count, Text = sb.ToString() };
    }

    /// <summary>The frame's results taken back: what they sum to does not depend on their order.</summary>
    public static long Collect(bool wait)
    {
        long sum = 0;
        for (int i = 0; i < PerFrame; i++)
        {
            var r = wait ? Out.Take() : Out.TryTake()!;
            sum += r.Sum + r.Text.Length + (r.Id & 7);
        }
        return sum;
    }
}

static class Report
{
    public static void Print(string scene, long[][] times, long check, long wallNs)
    {
        var sb = new StringBuilder("frames ").Append(scene).Append(' ');
        bool first = true;
        foreach (var ts in times) foreach (var t in ts) { if (!first) sb.Append(','); first = false; sb.Append(t); }
        Console.WriteLine(sb.ToString());
        Console.WriteLine("check " + scene + " " + check);
        Console.WriteLine("wall " + scene + " " + wallNs);
    }
    // (not ticks * 1e9 / frequency: that product leaves 64 bits after nine seconds of a nanosecond clock)
    public static long Now() => (long)(Stopwatch.GetTimestamp() * (1000000000.0 / Stopwatch.Frequency));
}

/// <summary>The churn's parts, each timed by itself (scene parts0 in the program without threads, parts1 on a worker of
/// the program with threads): making 20,000 particles into a list, ten passes over the list (each element read,
/// changed and stored back), letting them all go. Prints the lowest time of each part over the rounds, in ns.</summary>
static class Parts
{
    public static void Run(string scene)
    {
        var words = "ore wood stone iron".Split(' ');
        var list = new List<WParticle>();
        long make = long.MaxValue, pass = long.MaxValue, drop = long.MaxValue, sum = 0;
        for (int round = 0; round < 60; round++)
        {
            long t0 = Report.Now();
            for (int i = 0; i < 20000; i++) list.Add(new WParticle { X = i, Life = 100, Tag = words[i & 3] });
            long t1 = Report.Now();
            for (int k = 0; k < 10; k++)
            {
                int w = 0;
                for (int i = 0; i < list.Count; i++)
                {
                    var p = list[i];
                    p.X += p.Vx; p.Y += 0.1f;
                    if (--p.Life > 0) { list[w++] = p; sum += p.Tag.Length; }
                }
            }
            long t2 = Report.Now();
            list.RemoveRange(0, list.Count);
            long t3 = Report.Now();
            make = Math.Min(make, t1 - t0); pass = Math.Min(pass, (t2 - t1) / 10); drop = Math.Min(drop, t3 - t2);
        }
        Console.WriteLine("parts " + scene + " make " + make + " pass " + pass + " drop " + drop + " check " + sum);
    }
}

/// <summary>The tree's parts, each timed by itself (tparts0 without threads, tparts1 on a worker): building a tree of
/// 3,906 entities with parent links and child lists, a walk over it, 100 subtrees of 31 replaced, the whole tree let
/// go. Prints the lowest time of each part over the rounds, in ns.</summary>
static class TreeParts
{
    static int s_ids;
    static WEntity Build(WEntity? parent, int depth, int fan)
    {
        var e = new WEntity { Parent = parent, Id = s_ids++, X = depth, Y = fan };
        if (depth > 0) for (int i = 0; i < fan; i++) e.Children.Add(Build(e, depth - 1, fan));
        return e;
    }
    static long Walk(WEntity e, float px, float py)
    {
        long s = e.Id + (long)(px + py);
        foreach (var c in e.Children) s += Walk(c, px + e.X, py + e.Y);
        return s;
    }
    public static void Run(string scene)
    {
        long build = long.MaxValue, walk = long.MaxValue, replace = long.MaxValue, drop = long.MaxValue, sum = 0;
        for (int round = 0; round < 60; round++)
        {
            s_ids = 0;
            long t0 = Report.Now();
            WEntity? root = Build(null, 5, 5);
            long t1 = Report.Now();
            for (int k = 0; k < 4; k++) sum += Walk(root, 0, 0) & 1023;
            long t2 = Report.Now();
            for (int i = 0; i < 100; i++)
            {
                var b = root.Children[i % 5].Children[(i / 5) % 5];
                b.Children[i % 5] = Build(b, 2, 5);
            }
            long t3 = Report.Now();
            root = null;
            long t4 = Report.Now();
            build = Math.Min(build, t1 - t0); walk = Math.Min(walk, (t2 - t1) / 4); replace = Math.Min(replace, t3 - t2); drop = Math.Min(drop, t4 - t3);
        }
        Console.WriteLine("tparts " + scene + " build " + build + " walk " + walk + " replace " + replace + " drop " + drop + " check " + sum);
    }
}

/// <summary>The scenes on one thread, in a program with no thread in it.</summary>
static class Single
{
    public const int Warm = 300, Frames = 3000;
    public static void Private(string scene, bool tree)
    {
        long t0 = Report.Now();
        var w = new Worker(0);
        w.Run(tree, Warm, Frames);
        Report.Print(scene, new[] { w.Times }, w.Check, Report.Now() - t0);
    }
    public static void Shared(string scene)
    {
        long t0 = Report.Now(), check = 0;
        var times = new long[Frames];
        for (int f = 0; f < Warm + Frames; f++)
        {
            long a = Report.Now();
            Jobs.Post(f);
            for (int i = 0; i < Jobs.PerFrame; i++) Jobs.Out.Put(Jobs.Process(Jobs.In.Take()));
            check += Jobs.Collect(false);
            if (f >= Warm) times[f - Warm] = Report.Now() - a;
        }
        Report.Print(scene, new[] { times }, check, Report.Now() - t0);
    }
}
