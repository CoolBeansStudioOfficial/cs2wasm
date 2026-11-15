using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

static class Ext
{
    extension(string s)
    {
        public int Vowels => s.Count(c => "aeiou".IndexOf(c) >= 0);
        public string Twice() => s + s;
        public static string Join3(string a, string b, string c) => a + "-" + b + "-" + c;
    }

    extension<T>(List<T> list)
    {
        public T? SecondOrDefault => list.Count > 1 ? list[1] : default;
    }
}

abstract class Visitor
{
    public abstract string Visit<T>(T value);
    public virtual string Pair<A, B>(A a, B b) => Visit(a) + "," + Visit(b);
}

sealed class Printer : Visitor
{
    public override string Visit<T>(T value) => typeof(T).Name + ":" + value;
    public override string Pair<A, B>(A a, B b) => "[" + base.Pair(a, b) + "]";
}

interface IShape { double Area(); string Describe<TFmt>() where TFmt : IFormatter, new() => new TFmt().Format(this); }
interface IFormatter { string Format(IShape s); }
struct Plain : IFormatter { public string Format(IShape s) => "area " + s.Area().ToString("F2"); }
sealed record Circle(double R) : IShape { public double Area() => Math.PI * R * R; }
sealed record Rect(double W, double H) : IShape { public double Area() => W * H; }

ref struct Cursor
{
    readonly Span<int> _data;
    int _pos;
    public Cursor(Span<int> data) { _data = data; _pos = 0; }
    public bool Next(out int v) { if (_pos < _data.Length) { v = _data[_pos++]; return true; } v = 0; return false; }
    public ref int Current => ref _data[_pos - 1];
}

static class Program
{
    static int Sum(params ReadOnlySpan<int> xs) { int s = 0; foreach (var x in xs) s += x; return s; }

    static ref int Largest(int[] a)
    {
        int best = 0;
        for (int i = 1; i < a.Length; i++) if (a[i] > a[best]) best = i;
        return ref a[best];
    }

    static string Classify(int[] xs) => xs switch
    {
        [] => "empty",
        [var one] => "one " + one,
        [var first, .., var last] when first == last => "same ends " + first,
        [_, _, .. var rest] => "rest " + rest.Length,
    };

    static int Scoped(scoped Span<int> s) { s[0] = 7; return s[0] + s.Length; }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        // stackalloc, spans, ref struct
        Span<int> buf = stackalloc int[5];
        for (int i = 0; i < buf.Length; i++) buf[i] = i * i;
        var cur = new Cursor(buf);
        int total = 0;
        while (cur.Next(out var v)) { total += v; cur.Current += 100; }
        Console.WriteLine(total + " " + string.Join(",", buf.ToArray()));
        Console.WriteLine(Sum(1, 2, 3, 4) + " " + Sum() + " " + Scoped(stackalloc int[3]));
        Span<char> chars = stackalloc char[] { 'h', 'e', 'y' };
        chars.Reverse();
        Console.WriteLine(new string(chars) + " " + chars.IndexOf('e') + " " + chars.Contains('z'));

        // ref returns and ref locals
        var arr = new[] { 3, 9, 4 };
        ref int big = ref Largest(arr);
        big = -1;
        Console.WriteLine(string.Join(",", arr));

        // list patterns
        Console.WriteLine(Classify([]) + "; " + Classify([5]) + "; " + Classify([2, 8, 2]) + "; " + Classify([1, 2, 3, 4]));

        // extension members (C# 14)
        Console.WriteLine("education".Vowels + " " + "ab".Twice() + " " + string.Join3("x", "y", "z"));
        Console.WriteLine(new List<string> { "a", "b" }.SecondOrDefault + " " + (new List<int> { 1 }.SecondOrDefault));

        // generic virtual methods, base calls, default interface method with a generic constraint
        Visitor p = new Printer();
        Console.WriteLine(p.Visit(42) + " " + p.Visit("s") + " " + p.Pair(1.5, 'c'));
        IShape[] shapes = [new Circle(1), new Rect(2, 3)];
        foreach (var s in shapes) Console.WriteLine(s + " " + s.Describe<Plain>());

        // query syntax
        var words = new[] { "apple", "bob", "cherry", "avocado", "banana" };
        var q = from w in words
                where w.Length > 3
                group w by w[0] into g
                orderby g.Key descending
                select new { g.Key, Count = g.Count(), Longest = (from x in g orderby x.Length descending select x).First() };
        foreach (var r in q) Console.WriteLine(r);
        var join = from a in new[] { 1, 2, 3 } join b in new[] { 2, 3, 4 } on a equals b let sq = a * a select $"{a}^2={sq}";
        Console.WriteLine(string.Join(" ", join));

        // static local functions, tuples in switch, nested generics
        static (int, int) DivMod(int a, int b) => (a / b, a % b);
        var (dq, dr) = DivMod(17, 5);
        Console.WriteLine((dq, dr) switch { (3, 2) => "ok", _ => "bad" });
        var nested = new Dictionary<string, List<(int, string)>> { ["k"] = [(1, "one"), (2, "two")] };
        Console.WriteLine(nested["k"].Select(t => t.Item2).Aggregate((x, y) => x + "+" + y));

        // checked arithmetic in expressions, nameof, default interface constants
        try { int m = int.MaxValue; Console.WriteLine(checked(m + 1)); }
        catch (OverflowException e) { Console.WriteLine(nameof(OverflowException) + ": " + e.Message); }
        Console.WriteLine($"{Math.Round(2.5)} {Math.Round(3.5)} {(int)-3.7} {-7 / 2} {-7 % 2} {7 >> 1} {-7 >>> 28}");
    }
}
