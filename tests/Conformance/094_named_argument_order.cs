// Named arguments are evaluated in the order they are written, whatever the order of the parameters.
using System;
using System.Runtime.InteropServices;
struct Pt { public int X; public Pt(int x) { X = x; } }
class C { public C(int a, string b, Pt c = default) { Program.Log += $"ctor({a},{b},{c.X});"; } public int this[int i, int j] => i * 10 + j; }
static class Program
{
    public static string Log = "";
    static int F(string tag, int v) { Log += tag + ";"; return v; }
    static string S(string tag) { Log += tag + ";"; return tag.ToUpper(); }
    static Pt P(string tag, int v) { Log += tag + ";"; return new Pt(v); }
    static int Named(int a, int b = 10, params int[] rest) { int s = 0; foreach (var r in rest) s += r; return a * 100 + b * 10 + s; }
    static string Mix(int a, string s, Pt p, long l, double d, ref int r, out int o) { o = a + r; r = 99; return $"{a}{s}{p.X}{l}{d}"; }
    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        int r = 5;
        Console.WriteLine(Named(b: F("b", 2), a: F("a", 1)) + " " + Log); Log = "";
        Console.WriteLine(Mix(d: F("d", 1) + 0.5, l: F("l", 2), p: P("p", 3), s: S("s"), a: F("a", 4), r: ref r, o: out int o) + " " + r + o + " " + Log); Log = "";
        new C(b: S("x"), a: F("y", 1)); new C(c: P("p", 7), b: S("b"), a: F("a", 2));
        Console.WriteLine(new C(1, "q")[j: F("j", 1), i: F("i", 2)] + " " + Log); Log = "";
        Console.WriteLine(Named(rest: new[] { F("r", 1) }, a: F("a", 2)) + " " + Named(F("p", 1), rest: F("q", 3), b: F("b", 4)) + " " + Log);
    }
}
