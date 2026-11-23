// Threads (section 4.5): test 235's flat destructions in a program with threads, run on a worker, and the copies and
// clears of arrays. A class whose destruction is flat (it lets go of leaves and frees the object, no program code,
// RuntimeGen.IsFlatDestroy) is destroyed in place as without threads, its fields' counts the typed ones
// (CS2WASM_FLAT_DESTROY_BREAK=1 takes a class that is not sealed for exact: the "derived" lines differ, here too).
// An array of a type no other thread can reach (rt.slots_local) is copied and cleared as without threads, its
// elements counted by their type (RuntimeGen.Range); every other array under its places' locks.
// The race: every thread copies its Gems into one array a static holds and clears it, again and again; with
// CS2WASM_EXP_ALL_SLOTS_LOCAL=1 (every array taken for one thread's) two threads take the same element out at once
// and its Gem's count goes wrong: a Gem goes while referenced, and the sums (or the program) are no longer what
// they were. Whatever the interleaving, the output is the same (tools/thread-escape-breaks.sh).
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

sealed class Plain
{
    public int A; public float F;
    public string Name = "";
    public int[]? Data;
    public Plain(int a) { A = a; Name = "plain" + a; Data = new int[a % 5 + 1]; }
}

sealed class NoRefs { public int A, B; public long C; }

sealed class Two
{
    public string First, Second;
    public NoRefs Inner = new NoRefs();
    public Two(int i) { First = "f" + i; Second = First; Inner.A = i; }
}

sealed class Tracker
{
    public static int Live, Finalized;
    public int Id;
    public Tracker(int id) { Id = id; Live++; }
    ~Tracker() { Id = -1; System.Threading.Interlocked.Decrement(ref Live); System.Threading.Interlocked.Increment(ref Finalized); }
}

sealed class WithFinalizer
{
    public static int Finalized;
    public string Name;
    public WithFinalizer(int i) { Name = "w" + i; }
    ~WithFinalizer() { if (Name.Length > 0) System.Threading.Interlocked.Increment(ref Finalized); }
}

class UnsealedShape
{
    public string Name;
    public int Sides;
    public UnsealedShape(string name) { Name = name; }
}

sealed class Circle : UnsealedShape
{
    public Tracker T;
    public string Label;
    public Circle(int i) : base("circle" + i) { T = new Tracker(i); Label = "label" + i; }
}

sealed class Gem { public int V; public string Name = ""; }

sealed class Holder
{
    public UnsealedShape? Shape;
    public Plain? P;
    public Two? T;
    public WithFinalizer? W;
}

sealed class Link
{
    public object? Next;
    public Plain? P;
    public Two? T;
}

static class Program
{
    static UnsealedShape? s_shape;
    static Plain? s_plain;

    static void Collect() { for (int i = 0; i < 4; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); } }
    static int Alive<T>(List<WeakReference<T>> l) where T : class { int n = 0; foreach (var w in l) if (w.TryGetTarget(out _)) n++; return n; }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Plains(List<WeakReference<Plain>> objects, List<WeakReference<string>> names, List<WeakReference<int[]>> datas)
    {
        // a local assigned again and again, an array's elements stored over and cleared, a field, a static
        Plain p = new Plain(0);
        long sum = 0;
        for (int i = 1; i < 50; i++)
        {
            objects.Add(new WeakReference<Plain>(p)); names.Add(new WeakReference<string>(p.Name)); datas.Add(new WeakReference<int[]>(p.Data!));
            sum += p.A + p.Name.Length + p.Data!.Length;
            p = new Plain(i);
        }
        var arr = new Plain[40];
        for (int r = 0; r < 3; r++)
            for (int i = 0; i < arr.Length; i++)
            {
                arr[i] = new Plain(100 * r + i);
                objects.Add(new WeakReference<Plain>(arr[i])); names.Add(new WeakReference<string>(arr[i].Name));
            }
        for (int i = 0; i < arr.Length; i += 2) arr[i] = null!;
        foreach (var x in arr) if (x != null) sum += x.A;
        var h = new Holder();
        for (int i = 0; i < 30; i++)
        {
            h.P = new Plain(500 + i); h.T = new Two(i);
            objects.Add(new WeakReference<Plain>(h.P)); names.Add(new WeakReference<string>(h.T.First));
            s_plain = h.P;
            sum += h.T.Second.Length + h.T.Inner.A;
        }
        // (one object in two places: the first release is not the last)
        var shared = new Plain(900);
        objects.Add(new WeakReference<Plain>(shared));
        arr[0] = shared; arr[1] = shared; h.P = shared;
        arr[0] = null!; sum += arr[1].A; arr[1] = null!; sum += h.P.A;
        Console.WriteLine($"plains sum {sum} static {s_plain!.Name}");
        s_plain = null;
        // the same through generic collections, whose code is compiled for the sealed class
        var list = new List<Plain>();
        for (int i = 0; i < 40; i++) { list.Add(new Plain(2000 + i)); objects.Add(new WeakReference<Plain>(list[i])); names.Add(new WeakReference<string>(list[i].Name)); }
        for (int r = 0; r < 3; r++) for (int i = 0; i < list.Count; i += 2) { list[i] = new Plain(2100 + 50 * r + i); objects.Add(new WeakReference<Plain>(list[i])); }
        list.RemoveAt(3); list.RemoveRange(10, 5); list[0] = list[1];
        var map = new Dictionary<int, Plain>();
        for (int r = 0; r < 4; r++) for (int i = 0; i < 30; i++) { map[i] = new Plain(3000 + 100 * r + i); objects.Add(new WeakReference<Plain>(map[i])); names.Add(new WeakReference<string>(map[i].Name)); }
        for (int i = 0; i < 30; i += 3) map.Remove(i);
        var queue = new Queue<Plain>(); var stack = new Stack<Plain>();
        for (int i = 0; i < 20; i++) { queue.Enqueue(new Plain(4000 + i)); stack.Push(new Plain(4100 + i)); objects.Add(new WeakReference<Plain>(queue.Peek())); objects.Add(new WeakReference<Plain>(stack.Peek())); }
        for (int i = 0; i < 12; i++) { sum += queue.Dequeue().A; sum += stack.Pop().A; }
        long rest = 0;
        foreach (var x in list) rest += x.A; foreach (var kv in map) rest += kv.Value.A; foreach (var x in queue) rest += x.A; foreach (var x in stack) rest += x.A;
        Console.WriteLine($"collections {list.Count} {map.Count} {queue.Count} {stack.Count} {rest} {sum}");
        map.Clear(); list.Clear();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Derived()
    {
        // places of the class that is not sealed, holding objects of the sealed class derived from it
        UnsealedShape local = new Circle(0);
        long sum = 0;
        for (int i = 1; i < 20; i++) { sum += local.Name.Length; local = new Circle(i); }
        var arr = new UnsealedShape[20];
        for (int r = 0; r < 2; r++) for (int i = 0; i < arr.Length; i++) arr[i] = new Circle(100 + i);
        for (int i = 0; i < arr.Length; i++) arr[i] = i % 2 == 0 ? new UnsealedShape("plain shape") : null!;
        var h = new Holder();
        for (int i = 0; i < 20; i++) { h.Shape = new Circle(200 + i); s_shape = new Circle(300 + i); sum += h.Shape.Name.Length; }
        var holders = new Holder[10];
        for (int i = 0; i < holders.Length; i++) holders[i] = new Holder { Shape = new Circle(400 + i), P = new Plain(i) };
        for (int i = 0; i < holders.Length; i++) holders[i] = null!;
        s_shape = null; h.Shape = null; local = null!;
        Console.WriteLine($"derived sum {sum}");
        // (and in generic collections of the class that is not sealed)
        var list = new List<UnsealedShape>();
        for (int i = 0; i < 20; i++) list.Add(new Circle(500 + i));
        for (int i = 0; i < 20; i += 2) list[i] = new Circle(600 + i);
        var map = new Dictionary<int, UnsealedShape>();
        for (int r = 0; r < 3; r++) for (int i = 0; i < 15; i++) map[i] = new Circle(700 + 20 * r + i);
        for (int i = 0; i < 15; i += 2) map.Remove(i);
        Console.WriteLine($"derived collections {list.Count} {map.Count}");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Finalizers()
    {
        WithFinalizer w = new WithFinalizer(0);
        var arr = new WithFinalizer[10];
        var h = new Holder();
        for (int i = 1; i < 30; i++) { w = new WithFinalizer(i); arr[i % 10] = w; h.W = new WithFinalizer(1000 + i); }
        Console.WriteLine($"finalizers made {30 + 29}");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Chain(List<WeakReference<Plain>> objects)
    {
        Link? head = null;
        for (int i = 0; i < 200000; i++)
        {
            head = new Link { Next = head, P = new Plain(i), T = (i & 7) == 0 ? new Two(i) : null };
            if (i % 20000 == 0) objects.Add(new WeakReference<Plain>(head.P!));
        }
        int n = 0;
        for (object? l = head; l is Link k; l = k.Next) n += k.P!.Data!.Length;
        Console.WriteLine($"chain {n}");
    }

    static Gem[] s_shared = new Gem[64];
    static Gem[] s_gems = new Gem[8];

    static long Race()
    {
        var mine = new Gem[64];
        for (int i = 0; i < mine.Length; i++) mine[i] = s_gems[i & 7];
        long sum = 0;
        for (int r = 0; r < 3000; r++)
        {
            Array.Copy(mine, 0, s_shared, 0, 64);
            sum += s_shared[r & 63]?.V ?? 0;
            Array.Clear(s_shared, 0, 64);
        }
        Array.Clear(mine, 0, mine.Length);
        return sum >= 0 ? 1 : 0;
    }

    static void Run()
    {
        var objects = new List<WeakReference<Plain>>(); var names = new List<WeakReference<string>>(); var datas = new List<WeakReference<int[]>>();
        Plains(objects, names, datas); Collect();
        Console.WriteLine($"after plains: objects {Alive(objects)} of {objects.Count} names {Alive(names)} of {names.Count} arrays {Alive(datas)} of {datas.Count}");
        Derived(); Collect();
        Console.WriteLine($"derived: trackers live {Tracker.Live} finalized {Tracker.Finalized}");
        Finalizers(); Collect();
        Console.WriteLine($"finalizers run {WithFinalizer.Finalized}");
        objects.Clear();
        Chain(objects); Collect();
        Console.WriteLine($"after chain: objects {Alive(objects)} of {objects.Count}");
    }

    [UnmanagedCallersOnly(EntryPoint = "main")] static void Main()
    {
        // (test 235 on a worker, with the main thread waiting)
        var t = new Thread(Run);
        t.Start(); t.Join();
        for (int i = 0; i < s_gems.Length; i++) s_gems[i] = new Gem { V = 1000 + i, Name = "gem" + i };
        var racers = new Thread[3];
        var got = new long[3];
        for (int i = 0; i < racers.Length; i++) { int k = i; racers[i] = new Thread(() => got[k] = Race()); racers[i].Start(); }
        long own = Race();
        foreach (var r in racers) r.Join();
        Array.Clear(s_shared, 0, s_shared.Length);
        var junk = new List<Gem>();
        for (int i = 0; i < 1000; i++) junk.Add(new Gem { V = -i, Name = "j" + i });
        long gems = 0; string names = "";
        foreach (var g in s_gems) { gems += g.V; names += g.Name[0]; }
        Console.WriteLine($"race {own + got[0] + got[1] + got[2]} {gems} {names} {junk.Count}");
    }
}
