// A fourth sweep: inheritance and hiding, static abstract members, custom events, delegates, generic math over
// user types, pattern forms, newer syntax, iterators and awaits, spans, formatting.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

class Animal { public virtual string Name => "animal"; public string Hidden() => "A.Hidden"; public virtual Animal Clone() => new Animal(); public virtual string Speak(int times = 1) => string.Concat(Enumerable.Repeat("...", times)); public override string ToString() => Name; }
class Cat : Animal { public override string Name => "cat"; public new string Hidden() => "C.Hidden"; public override Cat Clone() => new Cat(); public override string Speak(int times = 2) => string.Concat(Enumerable.Repeat("meow", times)); }
sealed class Lion : Cat { public override string Name => "lion:" + base.Name; public sealed override string Speak(int times = 3) => base.Speak(times).ToUpper(); }
interface IStack<T> : IEnumerable<T> { void Push(T x); T Pop(); int Count { get; } T this[Index i] { get; } static abstract IStack<T> Create(); }
class ArrStack<T> : IStack<T>
{
    T[] _a = new T[2]; int _n;
    public int Count => _n;
    public void Push(T x) { if (_n == _a.Length) Array.Resize(ref _a, _n * 2); _a[_n++] = x; }
    public T Pop() => _n == 0 ? throw new InvalidOperationException("empty") : _a[--_n];
    public T this[Index i] => _a[i.GetOffset(_n)];
    public ArrStack<T> Slice(int start, int length) { var r = new ArrStack<T>(); for (int i = 0; i < length; i++) r.Push(_a[start + i]); return r; }
    public int Length => _n;
    public static IStack<T> Create() => new ArrStack<T>();
    public IEnumerator<T> GetEnumerator() { for (int i = _n - 1; i >= 0; i--) yield return _a[i]; }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public void Add(T x) => Push(x);
}
class Publisher
{
    EventHandler<int>? _h; public int Subs;
    public event EventHandler<int> Changed { add { _h += value; Subs++; } remove { _h -= value; Subs--; } }
    public event Action? Simple;
    public void Fire(int v) { _h?.Invoke(this, v); Simple?.Invoke(); }
}
readonly struct Frac : IComparable<Frac>, IEquatable<Frac>, IAdditionOperators<Frac, Frac, Frac>, IMultiplyOperators<Frac, Frac, Frac>, IParsable<Frac>
{
    public readonly long N, D;
    public Frac(long n, long d) { if (d == 0) throw new DivideByZeroException(); long g = (long)BigInteger.GreatestCommonDivisor(n, d); if (d < 0) g = -g; N = n / g; D = d / g; }
    public static Frac operator +(Frac a, Frac b) => new(a.N * b.D + b.N * a.D, a.D * b.D);
    public static Frac operator *(Frac a, Frac b) => new(a.N * b.N, a.D * b.D);
    public static bool operator <(Frac a, Frac b) => a.CompareTo(b) < 0;
    public static bool operator >(Frac a, Frac b) => a.CompareTo(b) > 0;
    public static implicit operator Frac(long n) => new(n, 1);
    public static explicit operator double(Frac f) => (double)f.N / f.D;
    public int CompareTo(Frac o) => (N * o.D).CompareTo(o.N * D);
    public bool Equals(Frac o) => N == o.N && D == o.D;
    public override bool Equals(object? o) => o is Frac f && Equals(f);
    public override int GetHashCode() => HashCode.Combine(N, D);
    public override string ToString() => D == 1 ? N.ToString() : $"{N}/{D}";
    public void Deconstruct(out long n, out long d) { n = N; d = D; }
    public static Frac Parse(string s, IFormatProvider? p) { var parts = s.Split('/'); return new(long.Parse(parts[0]), parts.Length > 1 ? long.Parse(parts[1]) : 1); }
    public static bool TryParse(string? s, IFormatProvider? p, out Frac r) { try { r = Parse(s!, p); return true; } catch { r = default; return false; } }
}
[InterpolatedStringHandler]
ref struct LogHandler
{
    StringBuilder _sb; public int Holes;
    public LogHandler(int literalLength, int formattedCount) { _sb = new StringBuilder(literalLength); Holes = formattedCount; }
    public void AppendLiteral(string s) => _sb.Append(s.ToUpperInvariant());
    public void AppendFormatted<T>(T v) => _sb.Append('<').Append(v).Append('>');
    public void AppendFormatted<T>(T v, string format) where T : IFormattable => _sb.Append('<').Append(v.ToString(format, CultureInfo.InvariantCulture)).Append('>');
    public string Result() => _sb.ToString() + "#" + Holes;
}
ref struct Scope { string _n; public Scope(string n) { _n = n; Program.Log("enter " + n); } public void Dispose() => Program.Log("exit " + _n); }
static class Ext
{
    public static IEnumerator<int> GetEnumerator(this Range r) { for (int i = r.Start.Value; i < r.End.Value; i++) yield return i; }
    public static void Deconstruct(this DateTime d, out int y, out int m, out int day) { y = d.Year; m = d.Month; day = d.Day; }
    public static void Add(this Dictionary<string, int> d, (string, int) kv) => d[kv.Item1] = kv.Item2;
    public static T Sum<T>(this IEnumerable<T> xs) where T : IAdditionOperators<T, T, T>, new() { var s = new T(); bool first = true; foreach (var x in xs) { s = first ? x : s + x; first = false; } return s; }
    public static TaskAwaiter<int> GetAwaiter(this int ms) => Task.Delay(ms).ContinueWith(_ => ms).GetAwaiter();
}

static class Program
{
    static readonly StringBuilder s_log = new();
    public static void Log(string s) => s_log.Append(s).Append(';');
    static string Flush() { var s = s_log.ToString(); s_log.Clear(); return s; }
    static void T(string name, Action a)
    {
        try { a(); } catch (Exception e) { Console.WriteLine(name + " threw " + e.GetType().Name + ": " + e.Message); }
    }
    static string Use(LogHandler h) => h.Result();
    static TStack Fill<TStack, T>(params T[] xs) where TStack : IStack<T> { var s = TStack.Create(); foreach (var x in xs) s.Push(x); return (TStack)s; }
    static string Grade(int score) => score switch { < 0 or > 100 => "invalid", >= 90 => "A", >= 80 and < 90 => "B", not (>= 0 and < 60) => "C", _ => "F" };
    static string Shape(object o) => o switch { (int x, int y) when x == y => "diag", (0, _) or (_, 0) => "axis", ValueTuple<int, int>(var a, var b) => $"pt{a + b}", Frac(var n, 1) => "whole" + n, Frac { D: > 1 } f => "frac" + f, string { Length: var l } s and not "" => s[0] + l.ToString(), int[] and [var first, .. var rest] => $"arr{first}+{rest.Length}", null => "null", _ => "?" };
    static int Fallthrough(int n) { int r = 0; switch (n) { case 1: r += 1; goto case 2; case 2: r += 10; goto default; case 3: case 4: r += 100; break; default: r += 1000; break; } return r; }
    static int Labels() { int found = 0; outer: for (int i = 0; i < 5; i++) { for (int j = 0; j < 5; j++) { if (j == 3) continue; if (i * j == 6) { found = i * 10 + j; goto done; } if (i == 4) break; } } done: return found; }
    static IEnumerable<string> Words(string s) { if (s.Length == 0) yield break; foreach (var w in s.Split(' ')) { if (w == "stop") yield break; yield return w; } }
    static async Task<string> Aw() { int ms = await 5; await using var d = new AsyncRes(); var vt = await new ValueTask<int>(3); var r = await Task.FromResult("x").ConfigureAwait(false); return $"{ms}{vt}{r}{await Later()}"; static async ValueTask<int> Later() { await Task.Yield(); return 9; } }
    sealed class AsyncRes : IAsyncDisposable { public async ValueTask DisposeAsync() { await Task.Yield(); Log("adisposed"); } }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        T("inherit", () => { Animal a = new Lion(); Cat c = new Lion(); Animal plain = new Cat(); Console.WriteLine($"{a.Name} {a.Hidden()} {c.Hidden()} {((Cat)a).Hidden()} {a.Clone().GetType().Name} {plain.Clone().Name} {a.Speak()} {c.Speak()} {((Lion)a).Speak()} {plain.Speak()} {a} {(a is Cat { Name: var n } ? n : "")} {typeof(Lion).BaseType!.BaseType!.Name} {a.GetType().IsSealed} {plain is Lion}"); });
        T("generic iface", () => { var s = Fill<ArrStack<string>, string>("a", "b", "c", "d"); var top = s[^1]; var sl = s[1..3]; var init = new ArrStack<int> { 1, 2, 3 }; var popped = init.Pop(); try { new ArrStack<int>().Pop(); } catch (InvalidOperationException e) { Log(e.Message); } Console.WriteLine($"{top} {string.Join("", s)} {string.Join("", sl)} {sl.Count} {popped}{init.Count} {s.Count()} {s.First()} {string.Join(",", init.Select(x => x * 2))} {Flush()}"); });
        T("events", () => { var p = new Publisher(); int total = 0; EventHandler<int> h = (s, v) => total += v; p.Changed += h; p.Changed += h; p.Changed += (s, v) => total += v * 100; p.Simple += () => Log("simple"); p.Fire(1); p.Changed -= h; p.Fire(2); Action a = () => Log("a"); Action b = () => Log("b"); var ab = a + b + a; var minus = ab - a; var none = a - a; Delegate d = ab; Func<object, string> covar = o => "obj"; Func<string, object> narrowed = covar; Console.WriteLine($"{total} {p.Subs} {ab.GetInvocationList().Length}{minus!.GetInvocationList().Length}{none == null} {a == (Action)a.Clone()} {narrowed("s")} {Flush()}"); ab(); minus(); Console.WriteLine(Flush()); });
        T("operators", () => { Frac a = new(1, 2), b = new(1, 3), c = 2; var sum = new[] { a, b, c }.Sum(); var (n, d) = a * b; var list = new List<Frac> { c, a, b }; list.Sort(); var parsed = Frac.Parse("6/8", null); static TNum Twice<TNum>(TNum x) where TNum : IAdditionOperators<TNum, TNum, TNum> => x + x; static TNum Parse<TNum>(string s) where TNum : IParsable<TNum> => TNum.Parse(s, CultureInfo.InvariantCulture); Console.WriteLine($"{a + b} {sum} {n}/{d} {string.Join("<", list)} {a < b} {a > b} {(double)a} {parsed} {a.Equals(new Frac(2, 4))} {new HashSet<Frac> { a, new(2, 4), new(3, 6) }.Count} {Twice(a)} {Twice(21)} {Twice(1.5)} {Parse<Frac>("9/3")} {Parse<int>("42")} {Parse<double>("1.5")} {Frac.TryParse("x", null, out _)} {list.Max()} {list.Min()} {Comparer<Frac>.Default.Compare(a, b)}"); try { _ = new Frac(1, 0); } catch (DivideByZeroException) { Console.WriteLine("div0"); } });
        T("patterns", () => { Console.WriteLine(string.Join(" ", new[] { -5, 95, 85, 70, 30, 100, 101 }.Select(Grade)) + " | " + string.Join(" ", new object?[] { (3, 3), (0, 5), (5, 0), (2, 3), new Frac(4, 1), new Frac(1, 2), "hello", "", new[] { 7, 8, 9 }, new int[0], null, 1.5 }.Select(Shape)) + " | " + Fallthrough(1) + " " + Fallthrough(2) + " " + Fallthrough(3) + " " + Fallthrough(9) + " " + Labels()); object o = 5; if (o is not string and not null and int i and (> 3 or < -3)) Console.Write("matched" + i); int? ni = null; ni ??= 4; ni ??= 5; string? s = null; s ??= "set"; var arr = new[] { 1, 2, 3, 4, 5 }; Console.WriteLine($" {ni} {s} {(arr is [1, .. { Length: 3 } mid, 5] ? mid.Sum() : -1)} {(arr is [.., > 4])} {arr[^2..] is [4, 5]} {(o, s) is (5, "set")} {o is IComparable and IFormattable}"); });
        T("syntax", () => { var (y, m, day) = new DateTime(2024, 5, 6); int total = 0; foreach (var i in 3..7) total += i; var dict = new Dictionary<string, int> { ("a", 1), ("b", 2) }; dict["c"] = 3; var nested = new { P = new { Q = new List<int> { 1, 2 } }, R = new[] { new { Z = 1 }, new { Z = 2 } } }; List<int> target = new() { Capacity = 10 }; target.AddRange([1, 2]); Dictionary<int, string> d2 = new() { [1] = "one", [2] = "two" }; int[][] jag = [[1], [2, 3], []]; using (new Scope("outer")) { using var inner = new Scope("inner"); Log("body"); } var msg = Use($"value {42} and {3.14159:F2} end"); var fmt = FormattableString.Invariant($"{1.5} {new DateTime(2020, 1, 2):yyyy}"); static int Local(int x = 5, params int[] more) => x + more.Length; var lam = (int x = 3, params int[] more) => x * 2 + more.Length; var tuple = (a: 1, b: (c: 2, d: "x")); var (ta, (tc, td)) = tuple; tuple.b.c = 9; var sw = "x" switch { var v when v.Length > 3 => 1, "x" => 2, _ => 3 }; Console.WriteLine($"{y}{m}{day} {total} {dict.Count}{dict["b"]} {nested.P.Q.Count}{nested.R[1].Z} {target.Count}{target.Capacity} {d2[2]} {jag.Sum(r => r.Length)} {Flush()} {msg} {fmt} {Local()}{Local(1, 2, 3)} {lam()}{lam(1, 7, 8)} {ta}{tc}{td}{tuple.b.c} {sw} {nameof(tuple.b.c)} {default(int)}{default(string) is null} {sizeof(long)}"); });
        T("iter/async", () => { Console.WriteLine(string.Join(",", Words("a b stop c")) + "|" + Words("").Count() + "|" + Aw().GetAwaiter().GetResult() + "|" + Flush()); });
        T("spans", () => { Span<int> s = stackalloc int[] { 5, 3, 9, 1 }; s.Sort(); var ro = (ReadOnlySpan<int>)s; int total = 0; foreach (ref int x in s) x *= 2; foreach (ref readonly var x in ro) total += x; var chars = "hello world".AsSpan(); var ranges = new List<string>(); foreach (var r in chars.Split(' ')) ranges.Add(chars[r].ToString()); Span<byte> bytes = stackalloc byte[8]; BitConverter.TryWriteBytes(bytes, 0x0102030405060708L); var mem = new Memory<char>("abcdef".ToCharArray()); mem.Span[1..3].Reverse(); var seq = new ArraySegment<int>(new[] { 1, 2, 3, 4 }, 1, 2); Span<char> buf = stackalloc char[16]; bool ok = 12345.TryFormat(buf, out int w) && buf[..w].SequenceEqual("12345"); for (int i = 0; i < 3; i++) { Span<int> tmp = stackalloc int[4]; tmp.Fill(i); total += tmp[3]; } Console.WriteLine($"{total} {s[0]}{s[^1]} {string.Join("+", ranges)} {bytes[0]}{bytes[7]} {mem} {seq.Sum()}{seq[0]} {ok} {chars.IndexOfAny('w', 'z')} {chars.LastIndexOf('o')} {chars.Trim('h').Length} {MemoryExtensions.Equals(chars[..5], "HELLO", StringComparison.OrdinalIgnoreCase)} {s.Contains(18)} {ro.ToArray().Length} {chars.Count('l')} {"a-b_c".AsSpan().ContainsAny("_-")}"); });
        T("text", () => { var sb = new StringBuilder(); sb.AppendJoin(", ", new[] { 1, 2, 3 }).Append($" {DateTime.MinValue:yyyy} ").Append('|').Append(CultureInfo.InvariantCulture, $"{1234.5:N1}"); Console.WriteLine(sb + " " + 0.5.ToString("P0", CultureInfo.InvariantCulture) + " " + (-1234.5).ToString("#,##0.00;(#,##0.00)", CultureInfo.InvariantCulture) + " " + 255.ToString("X8") + " " + 3.0.ToString("0.###") + " " + 1e10.ToString("E2", CultureInfo.InvariantCulture) + " " + TimeSpan.FromSeconds(3725.5).ToString(@"hh\:mm\:ss\.f") + " " + new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.FromHours(2)).ToUniversalTime().Hour + " " + DayOfWeek.Friday.ToString("G") + " " + new StringInfo("éa").LengthInTextElements + " " + string.Join("", "héllo wörld".Normalize(NormalizationForm.FormD).Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)) + " " + new DateTime(2024, 3, 5, 14, 30, 0).ToString("f", CultureInfo.InvariantCulture) + " " + CultureInfo.InvariantCulture.TextInfo.ToTitleCase("hallo welt")); });
    }
}
