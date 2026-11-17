// Forms the compiler lowers itself: an extension GetEnumerator and Deconstruct, FormattableString, a custom
// interpolated string handler, lambdas with default and params parameters, delegate equality.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
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
}
static class Program
{
    static readonly StringBuilder s_log = new();
    public static void Log(string s) => s_log.Append(s).Append(';');
    static string Flush() { var s = s_log.ToString(); s_log.Clear(); return s; }
    static string Use(LogHandler h) => h.Result();
    static void T(string name, Action a)
    {
        Console.Write(name + ": ");
        try { a(); } catch (Exception e) { Console.WriteLine(name + " threw " + e.GetType().Name + ": " + e.Message); }
    }
    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        T("decon", () => { var (y, m, day) = new DateTime(2024, 5, 6); Console.WriteLine($"{y}{m}{day}"); });
        T("range", () => { int total = 0; foreach (var i in 3..7) total += i; Console.WriteLine(total); });
        T("dict", () => { var dict = new Dictionary<string, int> { ("a", 1), ("b", 2) }; dict["c"] = 3; Console.WriteLine(dict.Count + "" + dict["b"]); });
        T("anon", () => { var nested = new { P = new { Q = new List<int> { 1, 2 } }, R = new[] { new { Z = 1 }, new { Z = 2 } } }; Console.WriteLine(nested.P.Q.Count + "" + nested.R[1].Z); });
        T("target", () => { List<int> target = new() { Capacity = 10 }; target.AddRange([1, 2]); Dictionary<int, string> d2 = new() { [1] = "one", [2] = "two" }; int[][] jag = [[1], [2, 3], []]; Console.WriteLine(target.Count + "" + target.Capacity + d2[2] + jag.Sum(r => r.Length)); });
        T("scope", () => { using (new Scope("outer")) { using var inner = new Scope("inner"); Log("body"); } Console.WriteLine(Flush()); });
        T("handler", () => { var msg = Use($"value {42} and {3.14159:F2} end"); Console.WriteLine(msg); });
        T("fmt", () => { var fmt = FormattableString.Invariant($"{1.5} {new DateTime(2020, 1, 2):yyyy}"); Console.WriteLine(fmt); });
        T("local", () => { static int Local(int x = 5, params int[] more) => x + more.Length; var lam = (int x = 3, params int[] more) => x * 2 + more.Length; Console.WriteLine(Local() + "" + Local(1, 2, 3) + lam() + lam(1, 7, 8)); });
        T("tuple", () => { var tuple = (a: 1, b: (c: 2, d: "x")); var (ta, (tc, td)) = tuple; tuple.b.c = 9; var sw = "x" switch { var v when v.Length > 3 => 1, "x" => 2, _ => 3 }; Console.WriteLine($"{ta}{tc}{td}{tuple.b.c} {sw} {nameof(tuple.b.c)} {default(int)}{default(string) is null} {sizeof(long)}"); });
        T("deleg", () => { Action a = () => Log("a"); var c = (Action)a.Clone(); Action b = a; Func<int> f1 = Flush2, f2 = Flush2; Console.WriteLine($"{a == c} {a.Equals(c)} {a == b} {f1 == f2} {ReferenceEquals(a, c)} {a.GetHashCode() == c.GetHashCode()} {a.Target == c.Target}"); });
    }
    static int Flush2() => 1;
}
