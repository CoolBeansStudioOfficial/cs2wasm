// A release at a place whose type names the object's class exactly (a sealed class in a local, a field, an array's
// element, a field of an object that dies) goes straight to that class's destruction when the class holds only
// leaves (strings, arrays of numbers, sealed classes without references) and has no finalizer. What must stay as
// .NET has it: the object's strings and arrays go with it (weak references see them go); a weak reference to the
// object itself is cleared; a class with a finalizer still runs
// it; a place of a class that is NOT sealed may hold an object of a derived class, whose own fields and finalizer
// must not be passed over (with CS2WASM_FLAT_DESTROY_BREAK=1, a class named Unsealed* taken as exact, the "derived"
// lines differ); a long chain of objects that each hold such an object is let go without running out of stack.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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

    [UnmanagedCallersOnly(EntryPoint = "main")] static void Main()
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
}
