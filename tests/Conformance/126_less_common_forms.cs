// Less common forms of ordinary C#, each used once: operators and conversions on unusual types, ref locals and ref
// returns in every position, delegates made every way, records and structs with every member kind, patterns on
// everything, labels and gotos, checked contexts, enums, indices and ranges on the program's own types.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

[Flags] enum Perm : byte { None = 0, Read = 1, Write = 2, All = Read | Write }
enum Big : ulong { Low = 1, High = 1UL << 63 }

readonly record struct Cell(int Row, int Col) { public int Sum => Row + Col; public static Cell operator +(Cell a, Cell b) => new(a.Row + b.Row, a.Col + b.Col); }
record Shape(string Name) { public virtual double Area => 0; public string Label { get; init; } = "none"; }
record Circle(double R) : Shape("circle") { public override double Area => Math.Round(Math.PI * R * R, 2); }
record Rect(double W, double H) : Shape("rect") { public override double Area => W * H; public void Deconstruct(out double w, out double h, out double area) { w = W; h = H; area = Area; } }

struct Grid
{
    readonly int[] _cells; public readonly int Width;
    public Grid(int w, int h) { Width = w; _cells = new int[w * h]; }
    public ref int this[int x, int y] => ref _cells[y * Width + x];
    public int this[Index i] => _cells[i];
    public int[] this[Range r] => _cells[r];
    public int Length => _cells.Length;
    public ref int First => ref _cells[0];
}

class Temperature : IComparable<Temperature>, IFormattable
{
    public double Degrees;
    public static implicit operator Temperature(double d) => new Temperature { Degrees = d };
    public static explicit operator int(Temperature t) => (int)t.Degrees;
    public static explicit operator checked byte(Temperature t) => checked((byte)t.Degrees);
    public static explicit operator byte(Temperature t) => unchecked((byte)t.Degrees);
    public static Temperature operator -(Temperature t) => -t.Degrees;
    public static Temperature operator !(Temperature t) => t.Degrees == 0 ? 1.0 : 0.0;
    public static Temperature operator ~(Temperature t) => 100 - t.Degrees;
    public static Temperature operator %(Temperature a, Temperature b) => a.Degrees % b.Degrees;
    public static Temperature operator >>(Temperature a, int n) => a.Degrees / (1 << n);
    public static Temperature operator >>>(Temperature a, int n) => a.Degrees - n;
    public static bool operator ==(Temperature a, Temperature b) => a.Degrees == b.Degrees;
    public static bool operator !=(Temperature a, Temperature b) => !(a == b);
    public int CompareTo(Temperature o) => Degrees.CompareTo(o.Degrees);
    public string ToString(string f, IFormatProvider p) => Degrees.ToString(f ?? "0.#", p) + "°";
    public override string ToString() => ToString(null, null);
    public override bool Equals(object o) => o is Temperature t && t == this;
    public override int GetHashCode() => Degrees.GetHashCode();
}

delegate ref int Pick(int[] a, int i);
delegate T Maker<out T>();
delegate void Sink<in T>(T value);

static class P
{
    static int s_counter;
    static ref int Counter() => ref s_counter;
    static ref int Larger(ref int a, ref int b) => ref a >= b ? ref a : ref b;
    static ref readonly Cell Origin(in Cell c) => ref c;
    static int Twice(int x) => x * 2;
    static string Join<T>(params ReadOnlySpan<T> xs) { var sb = new StringBuilder(); foreach (var x in xs) sb.Append(x).Append(';'); return sb.ToString(); }
    static IEnumerable<int> Guarded(int n) { try { for (int i = 0; i < n; i++) { if (i == 2) yield break; yield return i; } } finally { s_counter += 100; } }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        // enums: flags, big values, conversions, formatting, Enum's methods
        Perm p = Perm.Read; p |= Perm.Write; p &= ~Perm.Read; p ^= Perm.All;
        Big big = Big.High | Big.Low;
        Console.WriteLine(p + " " + (int)p + " " + p.HasFlag(Perm.Read) + " " + (Perm)3 + " " + (Perm)8 + " " + big.ToString("X") + " " + ((ulong)big > long.MaxValue) + " " + Enum.Parse<Perm>("Write, Read") + " " + Enum.GetValues<Perm>().Length + " " + default(Perm) + (p == Perm.Read ? "!" : "?"));

        // user-defined operators and conversions of every kind
        Temperature t = 21.5, zero = 0.0;
        Console.WriteLine((-t) + " " + (!zero) + " " + (~t) + " " + (t % 5.0) + " " + (t >> 1) + " " + (t >>> 3) + " " + (int)t + " " + (byte)(Temperature)300.0 + " " + (t == 21.5) + " " + $"{t:0.00}" + " " + t.CompareTo(22.0));
        try { Console.WriteLine(checked((byte)(Temperature)300.0)); } catch (OverflowException) { Console.WriteLine("overflow"); }

        // ref locals, ref returns, ref conditionals, ref reassignment, ref readonly, scoped
        int a = 3, b = 7;
        ref int big2 = ref Larger(ref a, ref b); big2 += 10;
        ref int r = ref a; r = 4; r = ref b; r++;
        Counter() += 5; Counter()++;
        ref int pick = ref (a > b ? ref a : ref b); pick = -1;
        var g = new Grid(3, 2); g[1, 1] = 9; g[2, 0]++; g.First -= 2; ref int cell = ref g[0, 1]; cell = 6;
        var c0 = new Cell(1, 2); ref readonly Cell ro = ref Origin(in c0);
        int[] nums = { 5, 6, 7 }; ref int last = ref nums[^1]; last *= 2;
        Pick pk = (arr, i) => ref arr[i]; pk(nums, 0) = 50;
        Console.WriteLine(a + " " + b + " " + s_counter + " " + g[4] + g[2] + g[0] + g[^3] + " " + string.Join(",", g[1..4]) + " " + ro.Sum + " " + string.Join(",", nums));

        // delegates: method groups, static and instance, variance, combining, removing, lambdas with every shape
        Func<int, int> twice = Twice; Func<int, int> local = static x => x + 1; Func<int, int> both = twice + local;
        Maker<string> ms = () => "made"; Maker<object> mo = ms;
        Sink<object> so = o => s_counter += o.ToString().Length; Sink<string> ss = so; ss("four");
        Action act = null; act += () => s_counter++; act += () => s_counter += 2; act -= act.GetInvocationList()[0] as Action; act?.Invoke();
        Func<int, int, int> add = (x, y) => x + y; var curried = (int x) => (int y) => add(x, y);
        var def = (int x = 5, params int[] rest) => x + rest.Sum();
        Func<string> bound = 42.ToString; Func<object, string> open = Convert.ToString; Comparison<int> cmp = (x, y) => y.CompareTo(x);
        var sorted = new[] { 2, 9, 4 }; Array.Sort(sorted, cmp);
        Console.WriteLine(both(5) + " " + both.GetInvocationList().Length + " " + mo() + " " + s_counter + " " + curried(3)(4) + " " + def() + def(1, 2, 3) + " " + bound() + open(7) + " " + string.Join("", sorted) + " " + (twice == Twice) + " " + twice.Method.Name);

        // records: with, equality, positional deconstruction, inheritance, printing
        Shape s1 = new Circle(1), s2 = new Rect(2, 3) { Label = "box" }, s3 = new Rect(2, 3) { Label = "box" };
        var s4 = (Rect)s2 with { W = 5 }; var (w, h, area) = s4;
        var cell1 = new Cell(1, 2); var cell2 = cell1 with { Col = 5 };
        Console.WriteLine(s1 + " " + s2.Equals(s3) + (s2 == s3) + (s1 != s2) + " " + s4 + " " + w + h + area + " " + (cell1 + cell2) + " " + cell1.GetHashCode().Equals(new Cell(1, 2).GetHashCode()) + " " + s1.Area);

        // patterns on everything
        string Kind(object o) => o switch
        {
            null => "null",
            Circle { R: > 10 } => "big circle",
            Circle or Rect { W: 2 } => "small shape",
            Shape { Label: var l } when l.Length > 3 => "labelled",
            (int x, int y) and var tup when x == y => "pair " + tup,
            int n and (< 0 or > 100) => "far",
            int n and not 0 => "int " + n,
            string { Length: 0 } or "" => "empty",
            string str and ['a', .., 'z'] => "a-z",
            IEnumerable<int> seq and not int[] => "seq " + seq.Count(),
            int[] { Length: var len } arr2 when len > 0 && arr2[0] is 1 => "array from 1",
            Perm.Read or Perm.Write => "perm",
            ValueTuple<int, string>(_, "x") => "vt",
            Type ty when ty == typeof(int) => "typeof",
            char ch and >= 'a' and <= 'f' => "hex",
            double.NaN => "nan",
            1.5m or decimal.MinusOne => "decimal constant",
            float.NaN or 2.5f => "float constant",
            decimal and > 100m => "big decimal",
            _ => o.GetType().Name,
        };
        Console.WriteLine(string.Join("|", new object[] { null, new Circle(11), new Rect(2, 9), new Rect(3, 3) { Label = "long" }, (2, 2), -5, 7, "", "abz", new List<int> { 1, 2 }, new[] { 1, 2 }, Perm.Write, (1, "x"), typeof(int), 'c', double.NaN, 0, 1.5, 1.5m, -1m, 7m, 1000m, float.NaN, 2.5f, (decimal?)1.5m }.Select(Kind)));

        // goto, labels, switch fall-ins, nested loops with labelled exits, checked blocks
        int state = 0, steps = 0;
    again:
        switch (state)
        {
            case 0: state = 2; goto case 2;
            case 1: state = 3; goto default;
            case 2: steps++; state = 1; goto again;
            default: steps += 10; break;
        }
        for (int i = 0; i < 5; i++) for (int j = 0; j < 5; j++) { if (i * j == 6) goto found; steps++; }
    found:
        int over = int.MaxValue; long wide;
        unchecked { over++; wide = checked((long)over * 2); }
        try { checked { over = int.MaxValue; over += Twice(1); } } catch (OverflowException) { steps += 1000; }
        uint u = unchecked((uint)-1); short sh = unchecked((short)70000); double dz = 1.0 / Twice(0); int trunc = (int)3.99, neg = (int)-3.99;
        Console.WriteLine(steps + " " + over + " " + wide + " " + u + " " + sh + " " + dz + " " + trunc + neg + " " + (5 / 2) + (-5 / 2) + (5 % -3) + (-5 % 3) + " " + (1 << 33) + (1L << 33) + (-8 >> 1) + (-8 >>> 28));

        // iterators with finally, params spans, interpolation with alignment and formats, string building
        Console.WriteLine(string.Join(",", Guarded(5)) + " " + s_counter + " " + Join(1, 2, 3) + Join<string>() + $"[{t,8:0.0}|{-3,-4}|{Perm.All,5}|{12345.678:N1}|{255:X4}|{0.5:P0}|{new DateTime(2026, 2, 3):yyyy-MM-dd}]" + $"{{{a}}}" + $"{(a > 0 ? "p" : "n")}");
    }
}
