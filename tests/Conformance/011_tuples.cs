using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

class Point
{
    public int X, Y;
    public Point(int x, int y) { X = x; Y = y; }
    public void Deconstruct(out int x, out int y) { x = X; y = Y; }
}

static class Program
{
    static (int min, int max) MinMax(int[] a)
    {
        int mn = int.MaxValue, mx = int.MinValue;
        foreach (var v in a) { if (v < mn) mn = v; if (v > mx) mx = v; }
        return (mn, mx);
    }

    static (string, int, double) Triple() => ("t", 2, 3.5);

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        var r = MinMax(new[] { 4, -2, 9, 1 });
        Console.WriteLine(r.min + " " + r.max + " " + r.Item1);
        Console.WriteLine(r);
        var (lo, hi) = MinMax(new[] { 7, 3 });
        Console.WriteLine(lo * 10 + hi);
        (int a, int b) = (1, 2);
        (a, b) = (b, a);
        Console.WriteLine(a + "," + b);
        var t = Triple();
        Console.WriteLine(t.Item1 + t.Item2 + t.Item3);
        var (s, _, d) = Triple();
        Console.WriteLine(s + d);
        var p = new Point(3, 4);
        var (px, py) = p;
        Console.WriteLine(px + py);
        var pairs = new List<(string name, int score)> { ("ann", 3), ("bob", 5), ("cid", 1) };
        foreach (var (name, score) in pairs) Console.Write(name + "=" + score + " ");
        Console.WriteLine();
        Console.WriteLine(pairs.OrderBy(x => x.score).First().name);
        var dict = new Dictionary<string, int> { ["x"] = 1, ["y"] = 2 };
        foreach (var (k, v) in dict) Console.Write(k + v);
        Console.WriteLine();
        Console.WriteLine((1, "a") == (1, "a"));
        Console.WriteLine((1, 2) != (1, 3));
        (long, double) widened = (5, 6);
        Console.WriteLine(widened.Item1 + widened.Item2);
        var nested = (1, (2, 3));
        Console.WriteLine(nested.Item2.Item2);
        var tupleKey = new Dictionary<(int, int), string>();
        tupleKey[(1, 2)] = "one-two";
        Console.WriteLine(tupleKey[(1, 2)] + " " + tupleKey.ContainsKey((2, 1)));
        (string first, string last) person = ("Ada", "Lovelace");
        Console.WriteLine($"{person.last}, {person.first}");
        Console.WriteLine(((1, 2), "x").ToString());
    }
}
