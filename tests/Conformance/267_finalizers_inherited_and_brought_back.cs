// Finalizers of plain objects (no cycle): the derived class's then the base's; an object a finalizer brings back,
// from the derived or the base finalizer, with what it holds; finalized again only when asked; suppressed and asked
// again; generic classes; objects made by finalizers; a finalizer that lets go of what its object holds.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

class Base
{
    public static int Live;
    public static List<string> Log = new();
    public static List<object> Saved = new();
    public string Name; public int Mode, Runs;
    public Leaf? Holds;
    public Base(string name, int mode) { Name = name; Mode = mode; Live++; }
    ~Base()
    {
        Runs++;
        Log.Add($"~Base {Name} {Runs}");
        if (Mode == 2 && Runs == 1) Saved.Add(this);
        if (Mode == 4) { Saved.Add(this); if (Runs < 3) GC.ReRegisterForFinalize(this); }
        if (Mode == 6 && Runs == 1) Saved.Add(new Base(Name + "'", 0) { Holds = Holds });
        if (Mode == 7) Holds = null;
        Live--;
    }
}

class Mid : Base
{
    public Mid(string name, int mode) : base(name, mode) { }
    ~Mid()
    {
        Log.Add($"~Mid {Name}");
        if (Mode == 1 && Runs == 0) Saved.Add(this);
        if (Mode == 5 && Runs == 0) Saved.Add(Holds!);
    }
}

sealed class Last : Mid
{
    public Last(string name, int mode) : base(name, mode) { }
    ~Last() { Log.Add($"~Last {Name}"); }
}

class NoOwn : Mid { public NoOwn(string name, int mode) : base(name, mode) { } }

sealed class Leaf
{
    public static int Live;
    public int N;
    public Leaf(int n) { N = n; Live++; }
    ~Leaf() { Live--; }
}

class Gen<T> : Base
{
    public T Value;
    public Gen(string name, T value) : base(name, 0) { Value = value; }
    ~Gen() { Log.Add($"~Gen<{typeof(T).Name}> {Name} {Value}"); }
}

sealed class Disposable : Base, IDisposable
{
    public Disposable(string name) : base(name, 0) { }
    public void Dispose() { Log.Add("Dispose " + Name); GC.SuppressFinalize(this); }
}

static class Program
{
    static void Collect() { for (int i = 0; i < 2; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); } }
    static void Show(string what)
    {
        Collect();
        // (the leaves are not counted while something is saved: under .NET the leaf of an object that was finalized and
        // came back has been finalized too, having been unreachable with it; here it is finalized when it is freed)
        Console.WriteLine($"{what}: bases {Base.Live} leaves {(Base.Saved.Count == 0 ? Leaf.Live.ToString() : "-")} saved {Base.Saved.Count}");
        foreach (var l in Base.Log) Console.WriteLine("  " + l);
        Base.Log.Clear();
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Make(int mode, int kind)
    {
        Base b = kind == 0 ? new Base("base" + mode, mode) : kind == 1 ? new Mid("mid" + mode, mode) : kind == 2 ? new Last("last" + mode, mode) : new NoOwn("noown" + mode, mode);
        b.Holds = new Leaf(mode);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    static string SavedText()
    {
        var parts = new List<string>();
        foreach (var o in Base.Saved) parts.Add(o is Base b ? b.Name + ":" + (b.Holds == null ? "-" : b.Holds.N.ToString()) : o is Leaf l ? "leaf " + l.N : "?");
        return string.Join(" ", parts);
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Drop() { Base.Saved.Clear(); }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Others()
    {
        new Gen<int>("gi", 42); new Gen<string>("gs", "text"); new Gen<Leaf?>("gl", null);
        using (var d = new Disposable("d1")) { }
        var d2 = new Disposable("d2"); d2.Dispose(); GC.ReRegisterForFinalize(d2);
        var d3 = new Disposable("d3"); GC.SuppressFinalize(d3); GC.SuppressFinalize(d3);
        var d4 = new Disposable("d4"); GC.SuppressFinalize(d4); GC.ReRegisterForFinalize(d4);
        var arr = new Base[3]; for (int i = 0; i < 3; i++) arr[i] = new Base("a", 0);
    }

    [UnmanagedCallersOnly(EntryPoint = "main")] static void Main()
    {
        for (int kind = 0; kind <= 3; kind++)
            for (int mode = 0; mode <= 7; mode++)
            {
                Make(mode, kind);
                Show($"kind {kind} mode {mode}");
                Console.WriteLine("  saved: " + SavedText());
                Drop(); Show("  let go");
                Drop(); Show("  again");
                Drop(); Show("  and again");
                Drop(); Show("  at the end");
            }
        Others();
        Collect();
        Base.Log.Sort(StringComparer.Ordinal);
        Show("others");
    }
}
