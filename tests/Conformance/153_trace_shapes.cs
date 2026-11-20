// The shapes of trace frames (section 6.3): generic methods and types, nested types, operators, conversions,
// indexers, properties, static constructors, iterators, async methods, local functions, and two methods with the
// same code (each its own frame).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Outer
{
    struct Money
    {
        public int Cents;
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static Money operator +(Money a, Money b) => a.Cents < 0 ? throw new ArgumentException("negative") : new Money { Cents = a.Cents + b.Cents };
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static explicit operator int(Money m) => m.Cents == 0 ? throw new InvalidCastException("zero") : m.Cents;
    }

    class Box<T>
    {
        public class Inner
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            public U Pick<U>(U u, T t) => throw new NotSupportedException(typeof(U).Name);
        }

        public T this[int i]
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            get => throw new IndexOutOfRangeException("at " + i);
        }

        public int Count
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            get => throw new InvalidOperationException("count");
        }
    }

    static class Lazy
    {
        public static readonly int V = Make();
        [MethodImpl(MethodImplOptions.NoInlining)]
        static int Make() => throw new FormatException("static");
    }
}

static class Program
{
    static void Trace(Exception e)
    {
        Console.WriteLine(e.GetType().Name + ": " + e.Message);
        foreach (var raw in (e.StackTrace ?? "<null>").Split('\n'))
        {
            var t = raw.TrimEnd('\r');
            // (the program's frames and the boundaries between traces: the frames of .NET's own runtime are its own)
            if (!t.StartsWith("   at Outer.") && !t.StartsWith("   at Program.") && !t.StartsWith("---")) continue;
            int i = t.IndexOf(" in "), l = t.LastIndexOf(":line ");
            Console.WriteLine(i > 0 && l > i ? t.Substring(0, i) + t.Substring(l) : t);
        }
    }

    static IEnumerable<int> Numbers(int fail)
    {
        for (int i = 0; ; i++)
        {
            if (i == fail) throw new InvalidOperationException("iterator");
            yield return i;
        }
    }

    static async Task<int> Later(int x)
    {
        await Task.Yield();
        if (x > 0) throw new TimeoutException("async");
        return x;
    }

    static async Task<int> Chain(int x) => await Later(x) + 1;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int First(int x) { if (x > 0) throw new Exception("same"); return x; }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int Second(int x) { if (x > 0) throw new Exception("same"); return x; }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Locals(int n)
    {
        Inner(n);
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Inner(int k) => throw new ArgumentException("local " + k);
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        try { _ = new Outer.Money { Cents = -1 } + new Outer.Money(); } catch (Exception e) { Trace(e); }
        try { _ = (int)new Outer.Money(); } catch (Exception e) { Trace(e); }
        try { new Outer.Box<string>.Inner().Pick(3, "s"); } catch (Exception e) { Trace(e); }
        try { _ = new Outer.Box<int>()[4]; } catch (Exception e) { Trace(e); }
        try { _ = new Outer.Box<int>().Count; } catch (Exception e) { Trace(e); }
        try { _ = Outer.Lazy.V; } catch (Exception e) { Trace(e); Trace(e.InnerException!); }
        try { foreach (var n in Numbers(2)) Console.WriteLine(n); } catch (Exception e) { Trace(e); }
        try { Later(1).GetAwaiter().GetResult(); } catch (Exception e) { Trace(e); }
        try { Later(1).Wait(); } catch (AggregateException e) { Trace(e.InnerException!); }
        try { Chain(2).GetAwaiter().GetResult(); } catch (Exception e) { Trace(e); }
        try { First(1); } catch (Exception e) { Trace(e); }
        try { Second(1); } catch (Exception e) { Trace(e); }
        try { Locals(5); } catch (Exception e) { Trace(e); }
        Console.WriteLine(First(0) + Second(0));
    }
}
