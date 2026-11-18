// Test 132's stores on four threads at once, each among its own objects and with one object all of them hold: the
// runtime's own state (groups, owners, what waits to be destroyed) is shared by them. Each thread's sums are what
// .NET's are, and nothing is left.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

class Wide
{
    public static int Live;
    public int Id;
    public Wide F0, F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12, F13, F14, F15, F16, F17, F18, F19, F20, F21;
    public Wide[] Arr = new Wide[6];
    public List<Wide> List = new();
    public (Wide a, int n) Pair;
    public Dictionary<int, Wide> Map = new();
    public object Any;
    public Wide(int id) { Id = id; System.Threading.Interlocked.Increment(ref Live); }
    // (a finalizer that stores into its own object what the object holds: stores while its group is being destroyed)
    ~Wide() { System.Threading.Interlocked.Decrement(ref Live); F19 = F2; Arr[1] = F3; F2 = null; Pair = (F5, 0); Any = F7; }

    public Wide Get(int i) => i switch
    {
        0 => F0, 1 => F1, 2 => F2, 3 => F3, 4 => F4, 5 => F5, 6 => F6, 7 => F7, 8 => F8, 9 => F9, 10 => F10, 11 => F11,
        12 => F12, 13 => F13, 14 => F14, 15 => F15, 16 => F16, 17 => F17, 18 => F18, 19 => F19, 20 => F20, _ => F21,
    };
    public void Set(int i, Wide v)
    {
        switch (i)
        {
            case 0: F0 = v; break; case 1: F1 = v; break; case 2: F2 = v; break; case 3: F3 = v; break;
            case 4: F4 = v; break; case 5: F5 = v; break; case 6: F6 = v; break; case 7: F7 = v; break;
            case 8: F8 = v; break; case 9: F9 = v; break; case 10: F10 = v; break; case 11: F11 = v; break;
            case 12: F12 = v; break; case 13: F13 = v; break; case 14: F14 = v; break; case 15: F15 = v; break;
            case 16: F16 = v; break; case 17: F17 = v; break; case 18: F18 = v; break; case 19: F19 = v; break;
            case 20: F20 = v; break; default: F21 = v; break;
        }
    }
}

static class P
{
    [ThreadStatic] static uint s_seed;
    [ThreadStatic] static List<string> t_out;
    static int Next(int n) { s_seed = s_seed * 1664525 + 1013904223; return (int)((s_seed >> 8) % (uint)n); }

    // every object reachable from the pool, once: the sum of their ids and how many
    static (long sum, int count) Reach(Wide[] pool)
    {
        var seen = new HashSet<Wide>(); var todo = new Stack<Wide>();
        foreach (var w in pool) if (w != null && seen.Add(w)) todo.Push(w);
        long sum = 0;
        while (todo.Count > 0)
        {
            var w = todo.Pop(); sum += w.Id;
            void See(Wide x) { if (x != null && seen.Add(x)) todo.Push(x); }
            for (int i = 0; i < 22; i++) See(w.Get(i));
            foreach (var x in w.Arr) See(x);
            foreach (var x in w.List) See(x);
            See(w.Pair.a);
            foreach (var x in w.Map.Values) See(x);
            if (w.Any is Wide aw) See(aw); else if (w.Any is Wide[] wa) foreach (var x in wa) See(x);
        }
        return (sum, seen.Count);
    }

    static Wide s_shared;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Run(int rounds, bool hub = false)
    {
        var pool = new Wide[24];
        int id = 0;
        // (a group that statics keep alive throughout: what is let go inside it is let go while it lives)
        // (one object every thread refers to and none stores into)
        if (hub) pool[0] = s_shared;
        for (int step = 0; step < rounds; step++)
        {
            int a = Next(pool.Length), b = Next(pool.Length);
            if (pool[a] == null) { pool[a] = new Wide(++id); continue; }
            if (pool[a] == s_shared) continue;
            var x = pool[a]; var y = Next(5) == 0 ? null : pool[b];
            switch (Next(14))
            {
                case 0: case 1: case 2: x.Set(Next(22), y); break;
                case 3: x.Arr[Next(x.Arr.Length)] = y; break;
                case 4: if (y != null) x.List.Add(y); break;
                case 5: if (x.List.Count > 0) x.List.RemoveAt(Next(x.List.Count)); break;
                case 6: x.Pair = (y, step); break;
                case 7: x.Map[Next(4)] = y; break;
                case 8: x.Map.Remove(Next(4)); break;
                case 9: x.Any = Next(2) == 0 ? y : new[] { y, x }; break;
                case 10: pool[a] = null; break;                       // let go of one (what it holds may live on)
                case 11: pool[a] = new Wide(++id) { F17 = x, F3 = y }; break; // replaced by one that holds it
                case 12: x.List.Clear(); Array.Clear(x.Arr); break;
                case 13: { var t = x.Get(Next(22)); if (t != null && t != s_shared) { t.Set(Next(22), x); x.Arr[Next(x.Arr.Length)] = t; } break; }
            }
            if (step % 997 == 0) { var (sum, count) = Reach(pool); t_out.Add(step + ": " + count + " " + sum); }
        }
        var (s2, c2) = Reach(pool);
        t_out.Add("end: " + c2 + " " + s2);
    }

    static int Left() { for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); } return System.Threading.Volatile.Read(ref Wide.Live); }

    static List<string> Thread(uint seed, int rounds) { s_seed = seed; t_out = new List<string>(); Run(rounds, hub: true); return t_out; }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void All()
    {
        s_shared = new Wide(1000000);
        var tasks = new Task<List<string>>[4];
        for (int i = 0; i < tasks.Length; i++) { uint seed = (uint)(1000 + i * 77); tasks[i] = Task.Run(() => Thread(seed, 12000)); }
        Task.WaitAll(tasks);
        for (int i = 0; i < tasks.Length; i++) { Console.WriteLine("thread " + i); foreach (var line in tasks[i].Result) Console.WriteLine(line); }
        s_shared = null;
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        All();
        Console.WriteLine("left " + Left());
    }
}
