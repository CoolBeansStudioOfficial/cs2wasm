using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

interface IShape { double Area { get; } }
sealed class Sq : IShape { public double S; public Sq(double s) { S = s; } public double Area => S * S; }
sealed class Ci : IShape { public double R; public Ci(double r) { R = r; } public double Area => 3 * R * R; }

class Box<T>
{
    public T Value;
    public Box(T v) { Value = v; }
    public Box<U> Map<U>(Func<T, U> f) => new Box<U>(f(Value));
    public override string ToString() => "Box(" + Value + ")";
}

struct Pair<A, B>
{
    public A First; public B Second;
    public Pair(A a, B b) { First = a; Second = b; }
}

interface IAddable<T> where T : IAddable<T> { static abstract T Zero { get; } static abstract T Add(T a, T b); }
struct Money : IAddable<Money>
{
    public long Cents;
    public Money(long c) { Cents = c; }
    public static Money Zero => new Money(0);
    public static Money Add(Money a, Money b) => new Money(a.Cents + b.Cents);
    public override string ToString() => (Cents / 100) + "." + (Cents % 100).ToString("D2");
}

static class Program
{
    static T Max<T>(T a, T b) where T : IComparable<T> => a.CompareTo(b) >= 0 ? a : b;
    static void Swap<T>(ref T a, ref T b) { T t = a; a = b; b = t; }
    static T SumAll<T>(IEnumerable<T> xs) where T : IAddable<T> { T acc = T.Zero; foreach (var x in xs) acc = T.Add(acc, x); return acc; }

    static string Classify(object o) => o switch
    {
        null => "null",
        int i when i < 0 => "negative int",
        int i => "int " + i,
        string { Length: 0 } => "empty string",
        string s => "string of " + s.Length,
        IShape { Area: > 10 } sh => "big shape " + sh.Area,
        IShape => "small shape",
        _ => "other",
    };

    static string Grade(int score) => score switch { >= 90 => "A", >= 80 and < 90 => "B", < 0 or > 100 => "invalid", _ => "C" };

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        Console.WriteLine(Max(3, 9) + " " + Max("pear", "apple") + " " + Max(2.5, -1.0));
        int x = 1, y = 2; Swap(ref x, ref y); Console.WriteLine(x + " " + y);
        string s1 = "a", s2 = "b"; Swap(ref s1, ref s2); Console.WriteLine(s1 + s2);
        var b = new Box<int>(21);
        var b2 = b.Map(v => v * 2).Map(v => "v" + v);
        Console.WriteLine(b + " " + b2);
        var p = new Pair<string, double>("pi", 3.14);
        Console.WriteLine(p.First + "=" + p.Second);
        var pairs = new List<Pair<int, string>> { new(1, "x"), new(2, "y") };
        foreach (var pr in pairs) Console.Write(pr.First + pr.Second);
        Console.WriteLine();
        Console.WriteLine(SumAll(new[] { new Money(150), new Money(275), new Money(5) }));
        object[] things = { 5, -3, "", "hey", new Sq(4), new Ci(1), 2.0, null };
        foreach (var t in things) Console.WriteLine(Classify(t));
        foreach (var sc in new[] { 95, 85, 50, 120 }) Console.Write(Grade(sc) + " ");
        Console.WriteLine();
        object o = 42;
        if (o is int n && n > 40) Console.WriteLine("matched " + n);
        if (o is not string) Console.WriteLine("not a string");
        IShape shape = new Sq(3);
        if (shape is Sq { S: 3 } sq) Console.WriteLine("square side " + sq.S);
        var boxes = new List<Box<string>> { new("a"), new("b") };
        Console.WriteLine(boxes[1].Value);
        var dd = new Dictionary<int, List<string>>();
        dd[1] = new List<string> { "one" };
        dd[1].Add("uno");
        Console.WriteLine(dd[1].Count);
        Console.WriteLine(EqualityComparer<string>.Default.Equals("q", "q") + " " + Comparer<int>.Default.Compare(5, 2));
        char c = 'm';
        string kind = c switch { >= 'a' and <= 'z' => "lower", >= 'A' and <= 'Z' => "upper", _ => "other" };
        Console.WriteLine(kind);
        switch (things[3])
        {
            case string str when str.StartsWith("h"): Console.WriteLine("starts with h"); break;
            case string: Console.WriteLine("string"); break;
        }
    }
}
