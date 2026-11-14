using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

interface IShape { double Area { get; } string Kind => GetType().Name.ToLowerInvariant(); }
record Circle(double R) : IShape { public double Area => Math.PI * R * R; }
record Rect(double W, double H) : IShape { public double Area => W * H; }

interface IMonoid<T> where T : IMonoid<T> { static abstract T Zero { get; } static abstract T operator +(T a, T b); }
readonly struct Money : IMonoid<Money>
{
    public readonly long Cents;
    public Money(long c) { Cents = c; }
    public static Money Zero => new(0);
    public static Money operator +(Money a, Money b) => new(a.Cents + b.Cents);
    public override string ToString() => (Cents / 100) + "." + (Cents % 100).ToString("00");
}

static class Algo
{
    public static T Sum<T>(IEnumerable<T> xs) where T : IMonoid<T> { var acc = T.Zero; foreach (var x in xs) acc += x; return acc; }
    public static T Max<T>(T a, T b) where T : IComparable<T> => a.CompareTo(b) >= 0 ? a : b;
    public static TNum Total<TNum>(IEnumerable<TNum> xs) where TNum : INumber<TNum> { var t = TNum.Zero; foreach (var x in xs) t += x; return t; }
    public static IEnumerable<(T a, T b)> Pairs<T>(IReadOnlyList<T> xs) { for (int i = 0; i + 1 < xs.Count; i += 2) yield return (xs[i], xs[i + 1]); }
}

static class Program
{
    static Dictionary<string, int> WordCounts(string text) =>
        text.Split(new[] { ' ', ',', '.', '!', '?', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.ToLowerInvariant())
            .GroupBy(w => w)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key)
            .ToDictionary(g => g.Key, g => g.Count());

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        var text = "The quick brown fox. The lazy dog! A quick dog?\nthe end";
        foreach (var (w, c) in WordCounts(text).Take(4)) Console.WriteLine($"{w,-6}|{c,3}");
        IShape[] shapes = { new Circle(1), new Rect(2, 3), new Circle(0.5) };
        foreach (var s in shapes.OrderBy(s => s.Area)) Console.WriteLine($"{s.Kind} {s.Area:F2} {s}");
        Console.WriteLine(Algo.Sum(new[] { new Money(150), new Money(275), new Money(5) }));
        Console.WriteLine(Algo.Max("pear", "apple") + " " + Algo.Max(3.5, -1.0) + " " + Algo.Total(new[] { 1, 2, 3, 4 }) + " " + Algo.Total(new[] { 0.5, 0.25 }));
        Console.WriteLine(string.Join(" ", Algo.Pairs(new[] { 1, 2, 3, 4, 5 }).Select(p => p.a * p.b)));
        var sb = new StringBuilder();
        sb.Append("x=").Append(42).Append(", y=").AppendFormat("{0:X4}", 255).Insert(0, "[").Append(']');
        Console.WriteLine(sb.ToString() + " " + sb.Length + " " + sb.ToString().Replace("x", "X").IndexOf('y'));
        Console.WriteLine(int.Parse("-123") + int.Parse("7f", NumberStyles.HexNumber) + " " + double.Parse("1.5e3", CultureInfo.InvariantCulture) + " " + (int.TryParse("12a", out var bad) ? bad : -1));
        Console.WriteLine(string.Format("{0,8:N2}|{1:P1}|{2:E2}|{3:D5}", 12345.678, 0.256, 98765.4321, 42));
        Console.WriteLine("  trim me ".Trim() + "|" + "abc".PadLeft(5, '*') + "|" + string.Concat(Enumerable.Repeat("ab", 3)) + "|" + "Hello".Substring(1, 3).ToUpper());
        var parts = "k1=v1;k2=v2;;k3=".Split(';').Where(p => p.Length > 0).Select(p => p.Split('=')).ToDictionary(a => a[0], a => a[1]);
        Console.WriteLine(string.Join(",", parts.Select(kv => kv.Key + "->'" + kv.Value + "'")));
        var nums = Enumerable.Range(1, 20).ToList();
        Console.WriteLine($"{nums.Where(n => n % 3 == 0).Sum()} {nums.Average():F1} {nums.Aggregate((a, b) => a * b % 1000)} {nums.Skip(5).Take(3).Last()} {nums.Chunk(6).Count()} {string.Join("", nums.Select(n => n % 10).Distinct())}");
        var sorted = new SortedDictionary<string, List<int>>();
        foreach (var n in nums) { var key = n % 2 == 0 ? "even" : "odd"; if (!sorted.TryGetValue(key, out var l)) sorted[key] = l = new(); l.Add(n); }
        foreach (var kv in sorted) Console.WriteLine(kv.Key + ": " + string.Join(",", kv.Value.Take(5)));
        var big = BigInteger.Pow(2, 100) + 1;
        Console.WriteLine(big + " " + (big % 97));
    }
}
