// Forms the compiler once answered with "not supported yet" (docs/AUDIT.md A5.1), each used the ordinary way.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

struct Money : IEquatable<Money>
{
    public decimal Amount;
    public Money(decimal a) { Amount = a; }
    public static Money operator +(Money a, Money b) => new Money(a.Amount + b.Amount);
    public static Money operator *(Money a, int n) => new Money(a.Amount * n);
    public static Money operator ++(Money a) => new Money(a.Amount + 1);
    public static bool operator ==(Money a, Money b) => a.Amount == b.Amount;
    public static bool operator !=(Money a, Money b) => a.Amount != b.Amount;
    public static bool operator <(Money a, Money b) => a.Amount < b.Amount;
    public static bool operator >(Money a, Money b) => a.Amount > b.Amount;
    public static Money operator -(Money a) => new Money(-a.Amount);
    public static bool operator true(Money a) => a.Amount != 0;
    public static bool operator false(Money a) => a.Amount == 0;
    public static Money operator &(Money a, Money b) => new Money(Math.Min(a.Amount, b.Amount));
    public static Money operator |(Money a, Money b) => new Money(Math.Max(a.Amount, b.Amount));
    public bool Equals(Money o) => Amount == o.Amount;
    public override bool Equals(object o) => o is Money m && Equals(m);
    public override int GetHashCode() => Amount.GetHashCode();
    public override string ToString() => Amount.ToString("0.##");
}

class Triple : ITuple
{
    public int A, B, C;
    public int Length => 3;
    public object this[int i] => i switch { 0 => A, 1 => B, _ => C };
}

class Point { public int X, Y; public void Deconstruct(out int x, out int y) { x = X; y = Y; } }

class Counted : IEnumerable<int>
{
    public IEnumerator<int> GetEnumerator() { yield return 1; yield return 2; }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

class Shaped { public ShapedEnum GetEnumerator() => new ShapedEnum(); }
class ShapedEnum { int _i; public int Current => _i * 10; public bool MoveNext() => ++_i <= 3; }

static class Ext
{
    public static IEnumerator<char> GetEnumerator(this (char, char) pair) { yield return pair.Item1; yield return pair.Item2; }
}

static class P
{
    static T Sum<T>(IEnumerable<T> xs) where T : INumber<T> { T s = T.Zero; foreach (var x in xs) s += x; return s; }
    static T Clamp01<T>(T v) where T : INumber<T> => T.Min(T.Max(v, T.Zero), T.One);
    static TTo Convert<TFrom, TTo>(TFrom v) where TFrom : INumber<TFrom> where TTo : INumber<TTo> => TTo.CreateChecked(v);
    static string Describe<T>(T v) where T : IParsable<T>, ISpanFormattable => v.ToString("", null) + "/" + T.Parse(v.ToString("", null), null);
    static T OrDefault<T>(T? v, T d) where T : struct { v ??= d; return v.Value; }
    static T First<T>(ref T? slot, T make) where T : class { slot ??= make; return slot; }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        // lifted and compound operators on a struct with its own operators, and on nullable library structs
        Money? m = new Money(2.5m), none = null;
        m += new Money(1); m *= 3; m++;
        none += new Money(1);
        Console.WriteLine(m + " " + (none == null) + " " + (m > new Money(1)) + " " + (none < m) + " " + (-m) + " " + (m == new Money(11.5m)));
        var mm = new Money(3); mm += mm; mm++;
        Console.WriteLine(mm + " " + (mm && new Money(2)) + " " + (new Money(0) || mm));
        decimal? d = 1.5m; d += 2; d *= d; decimal? dn = null; dn -= 1;
        DateTime? when = new DateTime(2026, 1, 31); when += TimeSpan.FromDays(1); TimeSpan? span = null; span += TimeSpan.FromSeconds(1);
        BigInteger? big = 10; big *= big; big++;
        Console.WriteLine(d + " " + (dn == null) + " " + when.Value.ToString("yyyy-MM-dd") + " " + (span == null) + " " + big);
        int? ni = 5; ni <<= 2; ni %= 7; ni ^= 3; long? nl = null; nl ??= 9; nl >>= 1;
        Console.WriteLine(ni + " " + nl);

        // ??= and ?? on value types and generics
        Console.WriteLine(OrDefault<int>(null, 4) + " " + OrDefault<Money>(new Money(1), new Money(2)));
        string slot = null; Console.WriteLine(First(ref slot, "made") + First(ref slot, "again"));
        (int, string)? pair = null; pair ??= (1, "one"); var got = pair ?? (2, "two");
        Console.WriteLine(got);

        // ?. that gives a value type, an element, a method on a struct result
        string s = "hello"; string nul = null; int[] arr = { 4, 5 }; int[] narr = null;
        Console.WriteLine(s?.Length + " " + (nul?.Length == null) + " " + arr?[1] + " " + (narr?[0] ?? -1) + " " + s?[1] + " " + s?.AsSpan(1, 2).ToString() + " " + nul?.Trim().Length);
        Point pt = new Point { X = 3, Y = 4 }, npt = null;
        Console.WriteLine(pt?.X.ToString("00") + " " + (npt?.X.CompareTo(1) ?? 99));

        // positional patterns: Deconstruct, ITuple, tuples; list patterns; property patterns on structs
        object o = new Triple { A = 1, B = 2, C = 3 };
        Console.WriteLine(o is (1, var b, 3) ? "triple " + b : "no");
        Console.WriteLine(pt is (3, var y) ? "point " + y : "no");
        Console.WriteLine((1, "x") is (1, { Length: 1 }) ? "tuple" : "no");
        Console.WriteLine(new[] { 1, 2, 3, 4 } is [1, .. var mid, 4] ? "list " + mid.Length : "no");
        Console.WriteLine(new Money(5) is { Amount: > 4 and < 6 } ? "money" : "no");

        // deconstruction into fields, elements, properties, discards, nested, with conversions
        var hold = new Point(); int[] into = new int[2]; long wide; string text;
        (hold.X, (into[1], _), wide, text) = (7, (8, 9), 10, "t");
        (into[0], hold.Y) = (hold.X, into[1]);
        var (px, py) = pt; (long lx, double ly) = pt;
        foreach (var (k, v) in new Dictionary<string, int> { ["a"] = 1 }) Console.Write(k + v + " ");
        Console.WriteLine(hold.X + " " + hold.Y + " " + into[0] + into[1] + " " + wide + text + " " + px + py + lx + ly);

        // foreach over everything that can be enumerated
        int total = 0;
        foreach (var x in new Counted()) total += x;
        foreach (var x in new Shaped()) total += x;
        foreach (var c in ('a', 'b')) total += c;
        foreach (var x in (IEnumerable)new ArrayList { 1, 2 }) total += (int)x;
        foreach (int x in new object[] { 3, 4 }) total += x;
        foreach (var x in "hi".AsSpan()) total += x;
        foreach (ref var x in arr.AsSpan()) x *= 2;
        foreach (var x in new int[,] { { 1, 2 }, { 3, 4 } }) total += x;
        foreach (var x in Enumerable.Range(0, 3).ToLookup(i => i % 2)[0]) total += x;
        foreach (var r in s.AsSpan().Split('l')) total += r.Start.Value;
        Console.WriteLine(total + " " + arr[0] + arr[1]);

        // stackalloc in its forms
        Span<int> sa = stackalloc int[] { 1, 2, 3 };
        ReadOnlySpan<byte> sb = stackalloc byte[4];
        Span<Money> sm = stackalloc Money[2]; sm[1] = new Money(7);
        unsafe { int* p = stackalloc int[2]; p[1] = 5; total = p[1] + sa[2] + sb.Length; }
        Console.WriteLine(total + " " + sm[1]);

        // collection expressions with conversions and spreads, jagged and nested initializers
        long[] longs = [1, 2, .. arr, .. new List<int> { 9 }];
        List<object> objs = [1, "two", .. arr, null];
        int[][] jag = [[1], [2, 3], []]; int[][] jag2 = { new[] { 1 }, new int[2] }; int[,] grid = { { 1, 2 }, { 3, 4 } };
        IEnumerable<double> ds = [1, 2.5f, .. longs.Take(1)];
        Span<char> chars = ['a', .. "bc"]; HashSet<string> set = ["x", "x", "y"];
        Dictionary<string, List<int>> nested = new() { ["k"] = [1, 2], ["l"] = { } };
        Console.WriteLine(longs.Sum() + " " + objs.Count + " " + jag.Sum(j => j.Length) + jag2[1].Length + grid[1, 0] + " " + ds.Sum() + " " + chars.Length + set.Count + nested["k"].Count);

        // generic math through static abstract members
        Console.WriteLine(Sum(new[] { 1, 2, 3 }) + " " + Sum(new[] { 1.5, 2.5 }) + " " + Sum(new[] { 1m, 2m }) + " " + Clamp01(3.5f) + Clamp01(-2) + " " + Convert<int, double>(7) + Convert<double, long>(8.0) + " " + Describe(42) + " " + Describe(1.5));

        // anonymous types and records with every member kind
        var anon = new { pt.X, Name = s, Inner = new { arr.Length }, List = new[] { 1 } };
        var anon2 = anon with { Name = "other" };
        Console.WriteLine(anon + " " + anon2.Name + " " + anon.Equals(new { pt.X, Name = s, anon.Inner, anon.List }));
    }
}
