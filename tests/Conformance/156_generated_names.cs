// The names the C# compiler gives lambdas and local functions in the places lowering gives scopes of their own
// (section 6.3): a foreach's collection and its variable, initializers, using, catch, switch sections, loops, nested
// and curried lambdas, expression trees, queries, generic methods, an instance method; as Delegate.Method and as a
// trace's frames name them.
#nullable disable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Runtime.InteropServices;

sealed class Initialized
{
    // (a static initializer's lambda is the static constructor's; an instance one's, each constructor's)
    public static readonly Func<int> S = () => 1;
    public readonly Func<int> I = () => 2;
    public Initialized() { }
    public Initialized(int x) { Program.P(() => x); }
}

sealed class Widget
{
    public Func<int> F;
    public List<Func<int>> L = new();
    int _v = 4;

    public void Methods(int p)
    {
        Program.P(() => _v);
        Program.P(() => p);
        Program.P(() => p + _v);
        Program.P(() => 1);
        int Local() => _v + p;
        Program.P((Func<int>)Local);
    }
}

static class Program
{
    public static void P(Delegate d) => Console.WriteLine(d.Method.Name + " on " + d.Method.DeclaringType.Name + (d.Method.IsStatic ? " static" : ""));

    static void Placements(int[] xs)
    {
        P(() => 1);
        // the collection's lambda is in the loop's scope of temporaries, the body's in the variable's
        foreach (var x in xs.Where(v => v > 0)) P(() => x);
        P(() => 2);
        var w = new Widget { F = () => 3 };
        P(w.F);
        var list = new List<Func<int>> { () => 4, () => 5 };
        foreach (var f in list) P(f);
        using (var d = new System.IO.MemoryStream()) { P(() => (int)d.Length); }
        try { throw new InvalidOperationException(); } catch (Exception e) { P(() => e.HResult); }
        switch (xs.Length) { case 3: P(() => 6); break; default: P(() => 7); break; }
        for (int i = 0; i < 1; i++) P(() => i);
        int k = 0;
        while (k < 1) { int kk = k; P(() => kk); k++; }
        lock (list) { P(() => 8); }
        Func<int, Func<int>> curry = a => () => a;
        P(curry);
        P(curry(1));
        int y = 9;
        int Local() => y;
        P((Func<int>)Local);
        static int S() => 10;
        P((Func<int>)S);
        Expression<Func<int>> tree = () => 11;
        Console.WriteLine(tree.Compile()());
        P(() => 12);
        foreach (var q in from v in xs where v > 1 select v * 2) Console.WriteLine(q);
        P(() => 13);
    }

    static void Generic<T>(T t)
    {
        P(() => 1);
        P(() => t);
        T Id(T v) => v;
        P((Func<T, T>)Id);
    }

    static int Deep(Func<int> f) => f();

    static void Traces()
    {
        int z = 0;
        try { Deep(() => Deep(() => 10 / z)); }
        catch (DivideByZeroException e)
        {
            var st = new StackTrace(e, false);
            for (int i = 0; i < st.FrameCount; i++) Console.WriteLine(st.GetFrame(i).GetMethod()?.Name);
        }
        try { foreach (var v in new[] { 1 }.Select(v => 1 / (v - 1))) Console.WriteLine(v); }
        catch (DivideByZeroException e) { Console.WriteLine(e.TargetSite?.Name + " " + e.TargetSite?.DeclaringType?.Name); }
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        Placements([1, 2, 3]);
        Generic(5);
        Generic("s");
        new Widget().Methods(2);
        P(Initialized.S);
        P(new Initialized().I);
        P(new Initialized(3).I);
        Traces();
    }
}
