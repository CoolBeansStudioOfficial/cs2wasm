// Newer language forms: params collections, System.Threading.Lock, the field keyword, partial properties,
// null-conditional assignment, user-defined compound assignment, ref struct interfaces, inline arrays, ref fields,
// function pointers, UTF-8 literals, required members, file-local types, list patterns, raw strings.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

[InlineArray(4)]
struct Four<T> { T _e; }
ref struct Window(ref int first, int length)
{
    ref int _first = ref first;
    public int Length = length;
    public ref int this[int i] => ref Unsafe.Add(ref _first, i);
    public void Bump() { for (int i = 0; i < Length; i++) this[i]++; }
}
interface IShape { double Area { get; } string Name => GetType().Name; }
ref struct RefSquare(double s) : IShape { public double Area => s * s; public string Name => "RefSquare"; }
partial class Counter
{
    public partial int Value { get; set; }
    public partial string Label { get; }
}
partial class Counter
{
    int _sets;
    public partial int Value { get => field; set { field = value < 0 ? 0 : value; _sets++; } }
    public partial string Label => "counter#" + _sets;
    public string Trimmed { get; set => field = value.Trim(); } = "";
    public void operator +=(int n) { Value += n; }
    public void operator ++() { Value++; }
}
class Person { public required string Name { get; init; } public int Age { get; init; } public Person? Friend; public List<string>? Tags; public int Visits; }
file class Hidden { public static string Who() => nameof(Hidden); }
unsafe static class Program
{
    static int Total(params ReadOnlySpan<int> xs) { int s = 0; foreach (var x in xs) s += x; return s; }
    static string Join(params IEnumerable<string> xs) => string.Join("+", xs);
    static int Count(params List<int> xs) => xs.Count;
    static double TotalArea<T>(T shape) where T : IShape, allows ref struct => shape.Area * 2;
    static int Twice(int x) => x * 2;
    static int Apply(delegate*<int, int> f, int v) => f(f(v));
    static string Classify(int[] a) => a switch
    {
        [] => "empty",
        [var x] => "one " + x,
        [1, .. var rest] => "starts with 1, " + rest.Length + " more",
        [.., > 100] => "ends big",
        [_, _, ..] => "two or more",
    };
    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        Console.WriteLine(Total() + " " + Total(1, 2, 3) + " " + Total([4, 5]) + " " + Join("a", "b") + " " + Join(new[] { "x" }.Concat(["y"])) + " " + Count(1, 2, 3, 4));

        var gate = new Lock(); int n = 0;
        lock (gate) { n++; lock (gate) n++; }
        using (gate.EnterScope()) n++;
        Console.WriteLine(n + " " + gate.IsHeldByCurrentThread + " " + gate.TryEnter() + " " + gate.IsHeldByCurrentThread);
        gate.Exit();

        var c = new Counter { Value = -3, Trimmed = "  padded " };
        c.Value = 5; c += 10; c++;
        Console.WriteLine(c.Value + " " + c.Label + " [" + c.Trimmed + "]");

        Person? nobody = null; var ann = new Person { Name = "Ann", Age = 30 };
        nobody?.Visits = 7; ann?.Visits = 7; ann?.Visits += 2;
        nobody?.Friend = new Person { Name = Fail() };
        ann?.Friend = new Person { Name = "Bob" };
        ann?.Friend?.Tags ??= ["new"];
        ann?.Tags?[0] = "never";
        Console.WriteLine(ann!.Visits + " " + ann.Friend!.Name + " " + ann.Friend.Tags![0] + " " + (ann.Tags == null));

        var four = new Four<int>(); four[0] = 3; four[3] = 9;
        Span<int> fs = four; fs[1] = 4;
        int sum = 0; foreach (var v in four) sum += v;
        Console.WriteLine(sum + " " + fs.Length + " " + four[^1] + " " + string.Join(",", fs[1..3].ToArray()));
        var strs = new Four<string>(); strs[2] = "two";
        Console.WriteLine(string.Join("|", ((ReadOnlySpan<string>)strs).ToArray().Select(s => s ?? "-")));

        int[] data = [1, 2, 3, 4, 5];
        var w = new Window(ref data[1], 3); w.Bump(); w[0] += 100;
        Console.WriteLine(string.Join(",", data));

        Console.WriteLine(TotalArea(new RefSquare(3)) + " " + new RefSquare(2).Name);

        delegate*<int, int> fp = &Twice;
        Console.WriteLine(Apply(fp, 5) + " " + Apply(&Twice, 1));
        delegate*<string, int, char> at = &At;
        Console.WriteLine(at("hello", 1));

        ReadOnlySpan<byte> utf8 = "héllo €"u8;
        Console.WriteLine(utf8.Length + " " + Encoding.UTF8.GetString(utf8) + " " + utf8[1].ToString("x2"));

        Console.WriteLine(Hidden.Who() + " " + nameof(List<>) + " " + nameof(Dictionary<,>.Keys));
        Console.WriteLine(Classify([]) + "; " + Classify([7]) + "; " + Classify([1, 2, 3]) + "; " + Classify([5, 500]) + "; " + Classify([5, 6, 7]));

        int count = 3; string who = "world";
        Console.WriteLine($$"""
            {"greeting": "hello {{who}}", "count": {{count * 2}},
             "raw": "\n is not a newline", "braces": "{}"}
            """);
        var parse = (string s, out int r) => int.TryParse(s, out r);
        Console.WriteLine(parse("42", out var parsed) + " " + parsed);
        var add = (int a, int b = 10, params int[] more) => a + b + more.Sum();
        Console.WriteLine(add(1) + " " + add(1, 2) + " " + add(1, 2, 3, 4));
        Span<int> onStack = [1, 2, 3];
        ReadOnlySpan<char> chars = "abc";
        Console.WriteLine(onStack.Contains(2) + " " + chars.StartsWith("ab") + " " + "x,y".Split(',').Contains("y"));
        scoped ref int first = ref onStack[0]; first = 50;
        Console.WriteLine(onStack[0]);
    }
    static string Fail() => throw new InvalidOperationException("evaluated");
    static char At(string s, int i) => s[i];
}
