// Stores whose old value is known null (docs/MEMORY-IR.md 5.3's Init, Memory/InitStores.cs): a store into an object
// only this code knows, whose field nothing has written yet, needs no release of the old value. In every case below the
// old value is NOT null at the last store: a rule that took it for null would keep the old object (its weak reference
// stays alive). The fresh objects' first stores are Init, and their values are freed with their holders.
// live: 71
using System;
using System.Runtime.CompilerServices;

sealed class T { }
class Holder { public object? F; public Holder() { } public Holder(object f) { F = f; } }
class Chained { public object? F; public Chained(object a) { F = a; } public Chained(object a, object b) : this(a) { F = b; } }
class WithInit { public object? F = Program.Track(); }
class Base { public object? F; public Base() { F = Program.Track(); } }
class Derived : Base { public Derived(object b) { F = b; } }
class Escapes { public object? F; public Escapes() { Program.Set(this); F = new T(); } }
class Pair { public object? A, B; public Pair(object a) { A = a; B = a; } }

static class Program
{
    static readonly WeakReference?[] s_w = new WeakReference?[32];
    static int s_n;

    [MethodImpl(MethodImplOptions.NoInlining)] public static object Track() { var t = new T(); s_w[s_n++] = new WeakReference(t); return t; }
    [MethodImpl(MethodImplOptions.NoInlining)] public static void Set(object o) { if (o is Holder h) h.F = Track(); else if (o is Escapes e) e.F = Track(); }

    [MethodImpl(MethodImplOptions.NoInlining)] static bool Loop() { var h = new Holder(); for (int i = 0; i < 3; i++) h.F = Track(); return h.F != null; }
    [MethodImpl(MethodImplOptions.NoInlining)] static bool Twice() { var h = new Holder(); h.F = Track(); h.F = new T(); return h.F != null; }
    [MethodImpl(MethodImplOptions.NoInlining)] static bool CtorWrote() { var h = new Holder(Track()); h.F = new T(); return h.F != null; }
    [MethodImpl(MethodImplOptions.NoInlining)] static bool Initializer() { var i = new WithInit(); i.F = new T(); return i.F != null; }
    [MethodImpl(MethodImplOptions.NoInlining)] static bool Chain() { var c = new Chained(Track(), new T()); return c.F != null; }
    [MethodImpl(MethodImplOptions.NoInlining)] static bool FromBase() { var d = new Derived(new T()); return d.F != null; }
    [MethodImpl(MethodImplOptions.NoInlining)] static bool Escaped() { var h = new Holder(); Set(h); h.F = new T(); return h.F != null; }
    [MethodImpl(MethodImplOptions.NoInlining)] static bool EscapedInCtor() { var e = new Escapes(); return e.F != null; }
    [MethodImpl(MethodImplOptions.NoInlining)] static bool Branch(bool b) { var h = new Holder(); if (b) h.F = Track(); h.F = new T(); return h.F != null; }
    [MethodImpl(MethodImplOptions.NoInlining)] static bool Caught() { var h = new Holder(); try { h.F = Track(); Throw(); } catch (InvalidOperationException) { h.F = Track(); } h.F = new T(); return h.F != null; }
    [MethodImpl(MethodImplOptions.NoInlining)] static bool Finally() { var h = new Holder(); try { h.F = Track(); } finally { h.F = Track(); } h.F = new T(); return h.F != null; }
    [MethodImpl(MethodImplOptions.NoInlining)] static bool Aliased() { var h = new Holder(); object o = h; ((Holder)o).F = Track(); h.F = new T(); return h.F != null; }
    [MethodImpl(MethodImplOptions.NoInlining)] static bool Fresh() { var p = new Pair(Track()); var h = new Holder { F = Track() }; return p.A != null && h.F != null; }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Throw() => throw new InvalidOperationException();

    static void Main()
    {
        bool all = Loop() & Twice() & CtorWrote() & Initializer() & Chain() & FromBase() & Escaped() & EscapedInCtor()
            & Branch(true) & Branch(false) & Caught() & Finally() & Aliased() & Fresh();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Console.WriteLine(all);
        Console.WriteLine(s_n);
        for (int i = 0; i < s_n; i++) Console.WriteLine(i + " " + s_w[i]!.IsAlive);
    }
}
