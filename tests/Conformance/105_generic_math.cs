// Generic math: the numeric interfaces' static abstract members over the built-in types and a user type.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;

readonly record struct Frac(long N, long D) :
    IAdditionOperators<Frac, Frac, Frac>, IMultiplyOperators<Frac, Frac, Frac>, IAdditiveIdentity<Frac, Frac>,
    IMultiplicativeIdentity<Frac, Frac>, IComparisonOperators<Frac, Frac, bool>, IParsable<Frac>, IMinMaxValue<Frac>
{
    static long Gcd(long a, long b) { while (b != 0) (a, b) = (b, a % b); return Math.Abs(a); }
    public static Frac Make(long n, long d) { long g = Gcd(n, d); if (d < 0) g = -g; return new(n / g, d / g); }
    public static Frac operator +(Frac a, Frac b) => Make(a.N * b.D + b.N * a.D, a.D * b.D);
    public static Frac operator checked +(Frac a, Frac b) => Make(checked(a.N * b.D + b.N * a.D), checked(a.D * b.D));
    public static Frac operator *(Frac a, Frac b) => Make(a.N * b.N, a.D * b.D);
    public static Frac AdditiveIdentity => new(0, 1);
    public static Frac MultiplicativeIdentity => new(1, 1);
    public static bool operator <(Frac a, Frac b) => a.N * b.D < b.N * a.D;
    public static bool operator >(Frac a, Frac b) => a.N * b.D > b.N * a.D;
    public static bool operator <=(Frac a, Frac b) => !(a > b);
    public static bool operator >=(Frac a, Frac b) => !(a < b);
    public static Frac Parse(string s, IFormatProvider? p) { var parts = s.Split('/'); return Make(long.Parse(parts[0], p), long.Parse(parts[1], p)); }
    public static bool TryParse(string? s, IFormatProvider? p, out Frac r) { r = default; if (s == null || !s.Contains('/')) return false; r = Parse(s, p); return true; }
    public static Frac MaxValue => new(long.MaxValue, 1);
    public static Frac MinValue => new(long.MinValue, 1);
    public override string ToString() => N + "/" + D;
}
static class Program
{
    static T Sum<T>(IEnumerable<T> xs) where T : IAdditionOperators<T, T, T>, IAdditiveIdentity<T, T>
    { T s = T.AdditiveIdentity; foreach (var x in xs) s += x; return s; }
    static T CheckedSum<T>(IEnumerable<T> xs) where T : IAdditionOperators<T, T, T>, IAdditiveIdentity<T, T>
    { T s = T.AdditiveIdentity; foreach (var x in xs) s = checked(s + x); return s; }
    static T Product<T>(ReadOnlySpan<T> xs) where T : IMultiplyOperators<T, T, T>, IMultiplicativeIdentity<T, T>
    { T s = T.MultiplicativeIdentity; foreach (var x in xs) s *= x; return s; }
    static T Max<T>(params T[] xs) where T : IComparisonOperators<T, T, bool>, IMinMaxValue<T>
    { T m = T.MinValue; foreach (var x in xs) if (x > m) m = x; return m; }
    static T Mean<T>(T[] xs) where T : INumber<T> => Sum(xs) / T.CreateChecked(xs.Length);
    static T Poly<T>(T x) where T : INumber<T> => x * x * T.CreateChecked(3) + x * T.CreateChecked(2) + T.One;
    static T ParseAll<T>(string s) where T : IParsable<T>, IAdditionOperators<T, T, T>, IAdditiveIdentity<T, T>
    { T r = T.AdditiveIdentity; foreach (var p in s.Split(' ')) r += T.Parse(p, CultureInfo.InvariantCulture); return r; }
    static string Bits<T>(T v) where T : IBinaryInteger<T> =>
        T.PopCount(v) + " " + T.LeadingZeroCount(v) + " " + T.TrailingZeroCount(v) + " " + (v << 3) + " " + (v >> 1) + " " + (v >>> 1) + " " + (v & T.CreateTruncating(0xF0)) + " " + T.RotateLeft(v, 5) + " " + v.GetByteCount();
    static string Floats<T>(T v) where T : IFloatingPointIeee754<T> =>
        string.Create(CultureInfo.InvariantCulture, $"{T.Sqrt(v)} {T.Floor(v)} {T.Round(v, 2)} {T.Sin(v):F6} {T.Exp(v):F4} {T.IsNaN(v / T.Zero - v / T.Zero)} {T.Pi:F5} {T.Epsilon > T.Zero} {T.Atan2(v, T.One):F6} {T.Hypot(v, v):F5}");
    static TTo Convert<TFrom, TTo>(TFrom v, int mode) where TFrom : INumber<TFrom> where TTo : INumber<TTo> =>
        mode == 0 ? TTo.CreateChecked(v) : mode == 1 ? TTo.CreateSaturating(v) : TTo.CreateTruncating(v);
    static string Info<T>(T v) where T : INumber<T> =>
        T.IsNegative(v) + " " + T.Abs(v) + " " + T.Sign(v) + " " + T.Clamp(v, T.Zero, T.CreateChecked(10)) + " " + T.Max(v, T.One) + " " + T.IsZero(v) + " " + T.IsInteger(v) + " " + v.CompareTo(T.One);
    static void T(string name, Func<string> f)
    {
        try { Console.WriteLine(name + ": " + f()); } catch (Exception e) { Console.WriteLine(name + " threw " + e.GetType().Name); }
    }
    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        Console.WriteLine(Sum(new[] { 1, 2, 3, 4 }) + " " + Sum(new[] { 1.5, 2.25 }).ToString(CultureInfo.InvariantCulture) + " " + Sum(new List<long> { long.MaxValue, 1 }) + " " + Sum(new[] { 1.1m, 2.2m }).ToString(CultureInfo.InvariantCulture));
        Console.WriteLine(Sum(new[] { Frac.Make(1, 2), Frac.Make(1, 3), Frac.Make(1, 6) }) + " " + Product<Frac>([Frac.Make(2, 3), Frac.Make(3, 4)]) + " " + Max(Frac.Make(1, 2), Frac.Make(2, 3), Frac.Make(3, 5)));
        T("checked int", () => CheckedSum(new[] { int.MaxValue, 1 }).ToString());
        T("checked byte", () => CheckedSum(new byte[] { 200, 100 }).ToString());
        T("unchecked byte", () => Sum(new byte[] { 200, 100 }).ToString());
        T("checked frac", () => CheckedSum(new[] { Frac.Make(long.MaxValue, 1), Frac.Make(1, 1) }).ToString());
        Console.WriteLine(Product<int>([1, 2, 3, 4, 5]) + " " + Product<double>([1.5, 2]).ToString(CultureInfo.InvariantCulture) + " " + Product<BigInteger>([BigInteger.Pow(10, 20), 12345]));
        Console.WriteLine(Max(3, 9, 2) + " " + Max(1.5f, -2f).ToString(CultureInfo.InvariantCulture) + " " + Max('a', 'z', 'q') + " " + Max<ulong>(5, ulong.MaxValue));
        Console.WriteLine(Mean(new[] { 1, 2, 3, 5 }) + " " + Mean(new[] { 1.0, 2, 3, 5 }).ToString(CultureInfo.InvariantCulture) + " " + Mean(new[] { 1m, 2m }).ToString(CultureInfo.InvariantCulture) + " " + Mean(new[] { (Half)1, (Half)2 }).ToString(CultureInfo.InvariantCulture));
        Console.WriteLine(Poly(2) + " " + Poly(2.5).ToString(CultureInfo.InvariantCulture) + " " + Poly((short)7) + " " + Poly(new BigInteger(1000000007)) + " " + Poly((Int128)5) + " " + Poly(1.5m).ToString(CultureInfo.InvariantCulture));
        Console.WriteLine(ParseAll<int>("1 2 3") + " " + ParseAll<double>("1.5 2.5").ToString(CultureInfo.InvariantCulture) + " " + ParseAll<Frac>("1/2 1/4") + " " + ParseAll<decimal>("0.1 0.2").ToString(CultureInfo.InvariantCulture));
        Console.WriteLine(Bits(0x1234) + " | " + Bits((byte)0x96) + " | " + Bits(-2L) + " | " + Bits((ushort)1) + " | " + Bits((UInt128)255));
        Console.WriteLine(Floats(2.0) + " | " + Floats(0.5f));
        T("conv checked", () => Convert<int, byte>(300, 0).ToString());
        T("conv sat", () => Convert<int, byte>(300, 1) + " " + Convert<double, int>(1e20, 1) + " " + Convert<int, uint>(-5, 1) + " " + Convert<double, long>(double.NaN, 1));
        T("conv trunc", () => Convert<int, byte>(300, 2) + " " + Convert<long, short>(0x12345678, 2) + " " + Convert<int, char>(65, 2) + " " + Convert<float, int>(-2.9f, 2));
        T("conv float", () => Convert<int, double>(7, 0).ToString(CultureInfo.InvariantCulture) + " " + Convert<decimal, float>(1.25m, 0).ToString(CultureInfo.InvariantCulture) + " " + Convert<long, decimal>(-9, 0).ToString(CultureInfo.InvariantCulture));
        Console.WriteLine(Info(-5) + " | " + Info(2.5).ToString(CultureInfo.InvariantCulture) + " | " + Info(0u) + " | " + Info(-0.0f) + " | " + Info(12m));
        Console.WriteLine(int.CreateChecked(3.99) + " " + double.CreateChecked(1L << 40).ToString(CultureInfo.InvariantCulture) + " " + byte.CreateSaturating(-1) + " " + sbyte.CreateTruncating(200));
        Console.WriteLine(Frac.TryParse("3/9", null, out var fr) + " " + fr + " " + Frac.TryParse("x", null, out _) + " " + (Frac.Make(1, 2) < Frac.Make(2, 3)) + " " + (Frac.Make(1, 2) == Frac.Make(2, 4)));
    }
}
