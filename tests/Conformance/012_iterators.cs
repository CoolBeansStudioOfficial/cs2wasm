using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

class Tree
{
    public int Value; public Tree Left, Right;
    public Tree(int v, Tree l = null, Tree r = null) { Value = v; Left = l; Right = r; }
    public IEnumerable<int> InOrder()
    {
        if (Left != null) foreach (var x in Left.InOrder()) yield return x;
        yield return Value;
        if (Right != null) foreach (var x in Right.InOrder()) yield return x;
    }
}

static class Program
{
    static IEnumerable<int> Range(int from, int count)
    {
        for (int i = 0; i < count; i++) yield return from + i;
    }

    static IEnumerable<string> Words()
    {
        Console.WriteLine("  (start)");
        yield return "alpha";
        Console.WriteLine("  (middle)");
        yield return "beta";
        Console.WriteLine("  (end)");
    }

    static IEnumerable<int> Fib()
    {
        int a = 0, b = 1;
        while (true) { yield return a; (a, b) = (b, a + b); }
    }

    static IEnumerable<int> UntilNegative(int[] xs)
    {
        foreach (var x in xs)
        {
            if (x < 0) yield break;
            yield return x * 10;
        }
    }

    static IEnumerable<int> WithFinally()
    {
        try
        {
            yield return 1;
            yield return 2;
        }
        finally
        {
            Console.WriteLine("  finally ran");
        }
        yield return 3;
    }

    static IEnumerator<char> Letters(string s)
    {
        foreach (var c in s) yield return char.ToUpper(c);
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        Console.WriteLine(string.Join(",", Range(5, 4)));
        foreach (var w in Words()) Console.WriteLine(w);
        Console.WriteLine(string.Join(" ", Fib().Take(10)));
        Console.WriteLine(string.Join(" ", UntilNegative(new[] { 1, 2, -1, 3 })));
        foreach (var x in WithFinally()) Console.WriteLine(x);
        var e = Letters("abc");
        while (e.MoveNext()) Console.Write(e.Current);
        Console.WriteLine();
        var tree = new Tree(4, new Tree(2, new Tree(1), new Tree(3)), new Tree(6, null, new Tree(7)));
        Console.WriteLine(string.Join(" ", tree.InOrder()));
        var seq = Range(1, 3);
        Console.WriteLine(seq.Sum() + " " + seq.Count() + " " + seq.Max());
        Console.WriteLine(Range(0, 100).Where(x => x % 7 == 0).Select(x => x * x).Skip(2).First());
        IEnumerable<int> lazy = Range(1, 5).Select(x => { Console.Write("[" + x + "]"); return x; });
        Console.WriteLine("before");
        foreach (var x in lazy.Take(2)) Console.Write(x);
        Console.WriteLine();
    }
}
