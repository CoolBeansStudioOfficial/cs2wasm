// Instance fields read in loops where the compiler makes the objects or the code itself: an iterator (its locals
// and parameters are fields of the machine; its own code stores a field between two reads of a loop outside), a
// loop inside an iterator (an async method is in test 234: a program with tasks has threads), a lambda and a local
// function (captured variables are fields of a closure), a primary constructor's parameter kept in the object, an
// auto-property and an init-only one, a record and its `with`, a generic class in two instantiations and through
// its base class, an iterator's handler that goes on with its loop, an anonymous type and a tuple, a struct held in a field, an interface's default method, a field
// hidden by a derived class's field of the same name. Each loop reads a field that something else stores meanwhile.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

sealed class Box
{
    public int Iter, Inner, Closure, Local, Prop0, Quiet;
    public int Auto { get; set; }
    public int Init { get; init; }
    public Box? Next;
}

sealed class Kept(int n, Box box)
{
    public int Next() => n++;
    public int Peek() => n;
    public int Read() => box.Quiet;
    public void Swap(Box other) { box = other; }
}

record Rec(int A, string S) { public int B { get; set; } public int C; }

class Gen<T> { public T? Val; public int N; public void Bump() { N++; } public virtual void Put(T v) { Val = v; } }
sealed class GenInt : Gen<int> { public new int N; public void Both() { N += 10; base.N += 100; } }

struct Pt { public int X, Y; }
sealed class Holder { public Pt P; public (int a, string b) T; public Box? B; }

interface ICount { int Count { get; set; } void Up() { Count++; } }
sealed class Counted : ICount { public int Count { get; set; } }

static class P
{
    // an iterator that stores the field between its values
    static IEnumerable<int> Steps(Box b)
    {
        for (int i = 0; i < 4; i++) { b.Iter += 10; yield return i; b.Iter++; }
    }
    static int Iterators(Box b)
    {
        int s = 0;
        foreach (var x in Steps(b)) s += b.Iter + x;
        var e = Steps(b).GetEnumerator();
        for (int i = 0; i < 3; i++) { s += b.Iter; e.MoveNext(); s += b.Iter; }
        foreach (var v in Inner(b, new Box { Inner = 7 })) s += v;
        return s;
    }
    // a loop inside an iterator: the machine's fields are its locals
    static IEnumerable<int> Inner(Box b, Box other)
    {
        int n = 0;
        for (int round = 0; round < 3; round++)
        {
            for (int i = 0; i < 3; i++) { n += b.Inner; b.Inner += i; }
            yield return n;
            b = other;
            other.Inner++;
        }
    }
    // an iterator whose handler goes on with its loop after a throw inside its try statement
    static IEnumerable<int> Catching(Box b, int[]? none)
    {
        var list = new List<Box> { b, b, b, b };
        for (int i = 0; i < list.Count; i++)
        {
            var x = list[i];
            int before = x.Iter;
            try { if (i > 0) before += none!.Length; } catch (NullReferenceException) { before += 1000; x.Iter += 3; }
            yield return before + x.Iter;
        }
    }
    static int Machines(Box b)
    {
        int s = 0;
        foreach (var v in Catching(b, null)) s += v;
        return s + b.Iter;
    }
    static int Closures(Box b)
    {
        int s = 0, captured = 0;
        Action inc = () => captured++;
        for (int i = 0; i < 4; i++) { s += captured; inc(); s += captured; }
        Func<int> get = () => b.Closure;
        for (int i = 0; i < 4; i++) { s += get(); b.Closure++; s += get() + b.Closure; }
        Action store = () => b.Closure += 10;
        for (int i = 0; i < 4; i++) { s += b.Closure; store(); s += b.Closure; }
        void Local(int v) { b.Local += v; captured += v; }
        for (int i = 0; i < 4; i++) { s += b.Local + captured; Local(i + 1); s += b.Local + captured; }
        var other = new Box { Closure = 1000 };
        Action swap = () => b = other;
        for (int i = 0; i < 4; i++) { s += b.Closure; if (i == 1) swap(); }
        return s;
    }
    static int Primary(Kept k, Box other)
    {
        int s = 0;
        for (int i = 0; i < 4; i++) { s += k.Peek(); k.Next(); s += k.Peek(); }
        for (int i = 0; i < 4; i++) { s += k.Read(); if (i == 1) k.Swap(other); }
        return s;
    }
    static int Properties(Box b)
    {
        int s = 0;
        for (int i = 0; i < 4; i++) { s += b.Auto; b.Auto += i + 1; s += b.Auto; }
        for (int i = 0; i < 4; i++) { s += b.Auto + b.Init; b = new Box { Init = i + 1, Auto = b.Auto + 1 }; }
        var r = new Rec(1, "s") { B = 2, C = 3 };
        for (int i = 0; i < 4; i++) { s += r.B + r.C; r.B += i; r.C++; var r2 = r with { B = 100 + i, C = 50 }; s += r2.B + r.A + r.B + r2.C + r.C; }
        for (int i = 0; i < 4; i++) { s += r.A + r.S.Length; r = r with { A = r.A + 1, S = r.S + "x" }; }
        ICount c = new Counted();
        for (int i = 0; i < 4; i++) { s += c.Count; c.Up(); s += c.Count; }
        return s;
    }
    static int Generics(Gen<int> a, Gen<string> t, GenInt d)
    {
        int s = 0;
        for (int i = 0; i < 4; i++) { s += a.N; a.Bump(); s += a.N + t.N; t.Bump(); }
        for (int i = 0; i < 4; i++) { s += a.Val; a.Put(i + 1); s += a.Val + t.Val!.Length; t.Put(new string('g', i + 2)); }
        // the field of the base class and the one of the same name that hides it
        Gen<int> viaBase = d;
        for (int i = 0; i < 4; i++) { s += d.N + viaBase.N; d.Both(); s += d.N + viaBase.N; viaBase.Bump(); }
        return s;
    }
    static int Values(Holder h)
    {
        int s = 0;
        for (int i = 0; i < 4; i++) { s += h.P.X + h.P.Y; h.P = new Pt { X = i, Y = i + 1 }; s += h.P.X; h.P.Y += 5; s += h.P.Y; }
        for (int i = 0; i < 4; i++) { s += h.T.a + h.T.b.Length; h.T = (i, new string('t', i + 1)); h.T.a += 2; s += h.T.a; }
        var anon = new { K = 1, B = h };
        for (int i = 0; i < 4; i++) { s += anon.K + anon.B.P.X; anon.B.P.X++; }
        for (int i = 0; i < 4; i++) { s += h.B!.Prop0; h.B = new Box { Prop0 = i + 2 }; s += h.B.Prop0; h.B.Prop0++; s += h.B.Prop0; }
        return s;
    }

    [UnmanagedCallersOnly(EntryPoint = "main")] static void Main()
    {
        var b = new Box { Quiet = 4 };
        Console.WriteLine("iterators " + Iterators(b));
        Console.WriteLine("machines " + Machines(b));
        Console.WriteLine("closures " + Closures(b));
        Console.WriteLine("primary " + Primary(new Kept(5, b), new Box { Quiet = 40 }));
        Console.WriteLine("properties " + Properties(b));
        Console.WriteLine("generics " + Generics(new Gen<int>(), new Gen<string> { Val = "v" }, new GenInt()));
        Console.WriteLine("values " + Values(new Holder { B = new Box(), T = (1, "t") }));
    }
}
