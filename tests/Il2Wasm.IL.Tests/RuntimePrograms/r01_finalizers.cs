// Finalizers and weak references (SPEC 4.4): a finalizer runs once when its object becomes unreachable, the base
// class's after the derived one's; a short weak reference reads empty before the finalizer runs, one that tracks
// resurrection only once the object is gone; GC.SuppressFinalize and GC.ReRegisterForFinalize; an object its
// finalizer brings back; WeakReference<T>, GCHandle, ConditionalWeakTable. Each object dies alone (between
// collections in .NET), so the order of what is printed is .NET's.
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

class Base
{
    public readonly int Id;
    public Base(int id) { Id = id; }
    ~Base() { Console.WriteLine("Base finalizer " + Id); }
}

class Derived : Base
{
    public Derived(int id) : base(id) { }
    ~Derived() { Console.WriteLine("Derived finalizer " + Id); }
}

class Watched
{
    public static WeakReference Short, Long;
    public static Watched Saved;
    public static bool Resurrect;
    public readonly string Name;
    public Watched(string name) { Name = name; }
    ~Watched()
    {
        Console.WriteLine($"finalizing {Name}: short {(Short?.Target == null ? "empty" : "set")}, long {(Long?.Target == null ? "empty" : "set")}");
        if (Resurrect) { Saved = this; Resurrect = false; }
    }
}

class Plain
{
    public int V = 5;
}

class Value
{
    public readonly string Text;
    public Value(string t) { Text = t; }
    ~Value() { Console.WriteLine("value finalizer " + Text); }
}

class Generic<T>
{
    public T Item;
    public Generic(T item) { Item = item; }
    ~Generic() { Console.WriteLine("Generic finalizer " + Item); }
}

static class Program
{
    static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void MakeDerived(int id) { Console.WriteLine("made " + id); var d = new Derived(id); GC.KeepAlive(d); }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static WeakReference MakeWeak() => new WeakReference(new Plain());

    [MethodImpl(MethodImplOptions.NoInlining)]
    static WeakReference<Plain> MakeWeakT() => new WeakReference<Plain>(new Plain());

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void MakeWatched(string name, bool resurrect)
    {
        var w = new Watched(name);
        Watched.Short = new WeakReference(w);
        Watched.Long = new WeakReference(w, trackResurrection: true);
        Watched.Resurrect = resurrect;
        GC.KeepAlive(w);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void MakeSuppressed() { var b = new Base(99); GC.SuppressFinalize(b); }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void MakeReRegistered() { var b = new Base(77); GC.SuppressFinalize(b); GC.ReRegisterForFinalize(b); }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static GCHandle MakeHandle(GCHandleType t) => GCHandle.Alloc(new Plain(), t);

    static readonly ConditionalWeakTable<object, Value> s_table = new();

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void AddToTable(string key) { var k = new object(); s_table.Add(k, new Value(key)); Console.WriteLine("table has it: " + s_table.TryGetValue(k, out _)); }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void MakeGeneric() { Console.WriteLine("generic"); GC.KeepAlive(new Generic<int>(12)); }

    static void Main()
    {
        MakeDerived(1);
        Collect();
        Console.WriteLine("after derived");

        var weak = MakeWeak();
        Collect();
        Console.WriteLine("weak alive: " + weak.IsAlive + ", target null: " + (weak.Target == null));

        var weakT = MakeWeakT();
        Collect();
        Console.WriteLine("weak<T> has target: " + weakT.TryGetTarget(out _));

        var kept = new Plain();
        var weakKept = new WeakReference(kept);
        Collect();
        Console.WriteLine("kept alive: " + weakKept.IsAlive + " " + ((Plain)weakKept.Target).V);
        GC.KeepAlive(kept);

        MakeWatched("first", resurrect: false);
        Collect();
        Console.WriteLine($"after first: short {(Watched.Short.Target == null ? "empty" : "set")}, long {(Watched.Long.Target == null ? "empty" : "set")}");

        MakeWatched("second", resurrect: true);
        Collect();
        Console.WriteLine($"after second: saved {Watched.Saved?.Name}, short {(Watched.Short.Target == null ? "empty" : "set")}, long {(Watched.Long.Target == null ? "empty" : "set")}");
        // (brought back by its finalizer: not finalized again when it goes)
        Watched.Saved = null;
        Collect();
        Console.WriteLine($"after second let go: long {(Watched.Long.Target == null ? "empty" : "set")}");

        MakeSuppressed();
        Collect();
        Console.WriteLine("after suppressed");
        MakeReRegistered();
        Collect();
        Console.WriteLine("after re-registered");

        var hw = MakeHandle(GCHandleType.Weak);
        var hn = MakeHandle(GCHandleType.Normal);
        Collect();
        Console.WriteLine("weak handle target null: " + (hw.Target == null) + ", normal handle target null: " + (hn.Target == null));
        hw.Free();
        hn.Free();

        AddToTable("cwt");
        Collect();
        Console.WriteLine("after table");

        MakeGeneric();
        Collect();
        Console.WriteLine("done");
    }
}
