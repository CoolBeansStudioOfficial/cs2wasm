// [UnsafeAccessor] for fields: a reference to another type's private field (an instance's, a struct's, a static),
// read and written through, the way generated code reaches what a class keeps to itself.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

class Timer
{
    float _left = 3.5f;
    string _name = "spawn";
    List<int> _ticks = new() { 1, 2 };
    static int s_made = 7;
    static string s_label = "timers";
    public string Show() => _name + " " + _left + " " + string.Join(",", _ticks) + " " + s_made + " " + s_label;
}

class Derived : Timer { int _extra = 11; public int Extra => _extra; }

struct Pair { int _a; string _b; public Pair(int a, string b) { _a = a; _b = b; } public override string ToString() => _a + ":" + _b; }

class Box<T> { T _value; public Box(T v) { _value = v; } public T Value => _value; }

static class Reach
{
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_left")] public static extern ref float Left(Timer t);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_name")] public static extern ref string Name(Timer t);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_ticks")] public static extern ref List<int> Ticks(Timer t);
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "s_made")] public static extern ref int Made(Timer? t);
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "s_label")] public static extern ref string Label(Timer? t);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_extra")] public static extern ref int Extra(Derived d);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_a")] public static extern ref int A(ref Pair p);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_b")] public static extern ref string B(ref Pair p);
}

static class BoxReach<T>
{
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_value")] public static extern ref T Value(Box<T> b);
}

static class P
{
    static string Fresh(char c, int n) => new string(c, n + int.Parse("3"));

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        var t = new Timer();
        Console.WriteLine(Reach.Left(t) + " " + Reach.Name(t) + " " + Reach.Ticks(t).Count + " " + Reach.Made(null) + " " + Reach.Label(null));
        Reach.Left(t) -= 1.25f;
        Reach.Name(t) = Fresh('n', 2);
        Reach.Ticks(t).Add(3);
        Reach.Ticks(t) = new List<int> { 9, 8, 7 };
        Reach.Made(null)++;
        Reach.Label(null) = Fresh('l', 4);
        Console.WriteLine(t.Show());
        // the old values were let go, the new ones are held by the fields
        for (int i = 0; i < 50; i++) { Reach.Name(t) = Fresh('x', i % 5); Reach.Label(null) = Fresh('y', i % 7); }
        Console.WriteLine(t.Show());

        var d = new Derived();
        Reach.Extra(d) *= 3; Reach.Left(d) = 0.5f;
        Console.WriteLine(d.Extra + " " + d.Show());

        var p = new Pair(1, "one");
        Reach.A(ref p) = 2; Reach.B(ref p) = Fresh('t', 0);
        Console.WriteLine(p);

        var bs = new Box<string>("s"); var bi = new Box<int>(4);
        BoxReach<string>.Value(bs) += Fresh('!', 0); BoxReach<int>.Value(bi) += 38;
        Console.WriteLine(bs.Value + " " + bi.Value);

        ref float left = ref Reach.Left(t);
        left = 10; left /= 4;
        Console.WriteLine(Reach.Left(t));
        try { Reach.Left(null!) = 1; Console.WriteLine("no exception"); }
        catch (NullReferenceException) { Console.WriteLine("NullReferenceException"); }
    }
}
