// Concatenations and interpolated strings of five parts and more (one new string for the lot): the text for five to
// twenty parts, null and empty parts anywhere, parts of every kind (numbers, chars, bools, enums, structs, objects
// with their own ToString, null objects), the parts evaluated once and in order, a part that throws (nothing after
// it evaluated), `+=` in a loop; and what the result is: never one of the parts itself, even when all the others
// are empty, and the empty string when every part is. Four parts and fewer are string.Concat's own rules (a lone
// non-empty part comes back itself).
using System;
using System.Runtime.InteropServices;
using System.Globalization;
using System.Text;

enum Kind { Ore, Wood, Stone }
readonly struct Pair { public readonly int A, B; public Pair(int a, int b) { A = a; B = b; } public override string ToString() => "<" + A + "," + B + ">"; }
sealed class Named { readonly string _n; public Named(string n) { _n = n; } public override string ToString() => _n; }
sealed class NullText { public override string? ToString() => null; }

static class Program
{
    static int s_calls;
    static readonly StringBuilder s_order = new();
    static string P(string s) { s_calls++; s_order.Append(s).Append('.'); return s; }
    static int N(int n) { s_calls++; s_order.Append(n).Append('.'); return n; }
    static string Boom(string s) => throw new InvalidOperationException("part " + s);
    static string Same(string result, string part) => ReferenceEquals(result, part) ? "same" : "new";

    static void Texts()
    {
        string a = "ore", b = "wood", c = "stone", d = "iron", e = "gold", f = "gem", g = "fish", h = "meat", i = "herb";
        Console.WriteLine(a + b + c + d + e);
        Console.WriteLine(a + b + c + d + e + f);
        Console.WriteLine(a + b + c + d + e + f + g);
        Console.WriteLine(a + b + c + d + e + f + g + h);
        Console.WriteLine(a + b + c + d + e + f + g + h + i);
        Console.WriteLine(a + "-" + b + "-" + c + "-" + d + "-" + e + "-" + f + "-" + g + "-" + h + "-" + i + "-" + a + "!");
        Console.WriteLine(a + b + c + d + e + f + g + h + i + a + b + c + d + e + f + g);
        Console.WriteLine(a + b + c + d + e + f + g + h + i + a + b + c + d + e + f + g + h + i + a + b + c + d + e);
        string? n = null;
        Console.WriteLine("[" + n + a + n + "" + b + n + n + c + "" + n + "]");
        Console.WriteLine((n + n + n + n + n + n).Length + " " + (n + "" + n + "" + n).Length + " " + ("" + n + "" + n + "" + n + "" + n + "x" + n).Length);
    }

    static void Kinds()
    {
        int f = 3001; uint u = 4000000000; long l = -9000000000; double x = 1.5; char ch = '|'; bool yes = true; decimal m = 12.50m;
        object? none = null; object boxed = 42; var p = new Pair(1, 2); var named = new Named("named"); Kind k = Kind.Stone; int? maybe = null; int? has = 7;
        Console.WriteLine($"{"stone"} x{12}: {f * 7 + 12} hp {(f + 12) % 100}/100");
        Console.WriteLine($"{u} {l} {x.ToString(CultureInfo.InvariantCulture)} {ch} {yes} {m.ToString(CultureInfo.InvariantCulture)} {none} {boxed} {p} {named} {k} {maybe} {has}");
        Console.WriteLine("a" + f + ch + yes + none + boxed + p + named + k + maybe + has + new NullText() + "z");
        Console.WriteLine($"{f,6}|{f,-6}|{f:X}|{f:D6}|{k,7}|{named,-7}|{ch}{ch}");
        Console.WriteLine($"{{{f}}} {{}} {f}{f}{f}{f}{f}{f}{f}{f}{f}");
        string s = "";
        for (int i = 0; i < 12; i++) s += i + "," + (i * i) + ";" + (i % 2 == 0 ? "e" : null) + ' ';
        Console.WriteLine(s);
    }

    static void Order()
    {
        s_calls = 0; s_order.Clear();
        string r = P("a") + N(1) + P("b") + N(2) + P("c") + N(3) + P("d") + N(4) + P("e") + N(5);
        Console.WriteLine(r + " calls " + s_calls + " order " + s_order);
        s_calls = 0; s_order.Clear();
        r = $"{P("a")}{N(1)}:{P("b")}{N(2)}:{P("c")}{N(3)}:{P("d")}{N(4)}";
        Console.WriteLine(r + " calls " + s_calls + " order " + s_order);
        s_calls = 0; s_order.Clear();
        try { r = P("a") + P("b") + P("c") + P("d") + P("e") + Boom("f") + P("g") + P("h") + P("i"); }
        catch (InvalidOperationException e) { Console.WriteLine(e.Message + " after " + s_calls + " order " + s_order); }
        s_calls = 0; s_order.Clear();
        try { r = $"{P("a")} {N(1)} {new Thrower()} {P("b")} {N(2)}"; }
        catch (InvalidOperationException e) { Console.WriteLine(e.Message + " after " + s_calls + " order " + s_order); }
    }
    sealed class Thrower { public override string ToString() => throw new InvalidOperationException("ToString threw"); }

    static void Identity()
    {
        string a = new string('a', 3), e = "", b = new string('b', 2);
        string? n = null;
        // five parts and more: a new string whenever there is text
        Console.WriteLine(Same(a + e + e + e + e, a) + " " + Same(e + e + a + e + e + e, a) + " " + Same(n + n + n + n + a, a) + " " + Same(a + n + e + n + e + n + e + n + e + n, a));
        Console.WriteLine(Same(e + e + e + e + e, "") + " " + Same(n + n + n + n + n + n + n + n + n, "") + " " + Same(e + n + e + n + e, string.Empty));
        Console.WriteLine(Same(a + e + e + e + b, a) + " " + (a + e + e + e + b));
        // four and fewer: Concat's own rules
        Console.WriteLine(Same(a + e, a) + " " + Same(e + a, a) + " " + Same(a + e + e, a) + " " + Same(a + e + e + e, a) + " " + Same(e + e + e + e, ""));
        // the parts themselves are untouched
        string all = a + b + a + b + a + b + a + b + a;
        Console.WriteLine(all + " " + a + " " + b + " " + all.Length);
    }

    [UnmanagedCallersOnly(EntryPoint = "main")] static void Main()
    {
        Texts();
        Kinds();
        Order();
        Identity();
    }
}
