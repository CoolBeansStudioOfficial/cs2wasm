// Function pointers: to generic methods at several instantiations, with `ref`, `out` and `in` parameters and
// `ref` returns, in fields, arrays, generic classes and structs, as arguments and results, compared and cast, to
// lambdas made static, and returning references into a collection's storage that then grows.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

sealed class Node { public Node? Next; public int Id; public Node(int id) { Id = id; } }

unsafe struct Ops<T>
{
    public delegate*<T, T, T> Combine;
    public delegate*<ref T, void> Touch;
    public delegate*<List<T>, int, ref T> At;
}

unsafe class Table<T>
{
    public delegate*<T, string>[] Show = new delegate*<T, string>[2];
    public delegate*<in T, out T, bool> Copy;
    public string Both(T v) => Show[0](v) + "|" + Show[1](v);
}

static unsafe class Program
{
    static T Id<T>(T x) => x;
    static T First<T>(T a, T b) => a;
    static T Second<T>(T a, T b) => b;
    static int Add(int a, int b) => a + b;
    static string Cat(string a, string b) => a + b;
    static void Bump(ref int x) { x += 100; }
    static void Null<T>(ref T x) where T : class? { x = null!; }
    static ref T ElementAt<T>(List<T> l, int i) => ref CollectionsMarshal.AsSpan(l)[i];
    static Span<T> SpanOf<T>(List<T> l) => CollectionsMarshal.AsSpan(l);
    static bool CopyTo<T>(in T from, out T to) { to = from; return from != null; }
    static string Plain<T>(T v) => v?.ToString() ?? "null";
    static string Typed<T>(T v) => typeof(T).Name + ":" + v;
    static void Swap<T>(ref T a, ref T b) { var t = a; a = b; b = t; }
    static ref int Larger(ref int a, ref int b) => ref a >= b ? ref a : ref b;
    static int Apply(delegate*<int, int, int> f, int a, int b) => f(a, b);
    static delegate*<T, T> Identity<T>() => &Id;
    static R Twice<T, R>(delegate*<T, R> f, delegate*<R, T> back, T x) => f(back(f(x)));
    static int Len(string s) => s.Length;
    static string Stars(int n) => new string('*', n);
    static void Fill<T>(Span<T> s, delegate*<int, T> make) { for (int i = 0; i < s.Length; i++) s[i] = make(i); }
    static Node MakeNode(int i) => new Node(i);
    static long Wide(long a, double b, byte c, ref short d, out decimal e) { e = a + (decimal)b + c + d; d++; return a * 2; }

    [UnmanagedCallersOnly(EntryPoint = "main")] static void Main()
    {
        delegate*<int, int> idInt = &Id; delegate*<string, string> idStr = &Id; delegate*<Node?, Node?> idNode = &Id;
        Console.WriteLine($"generic: {idInt(5)} {idStr("s")} {idNode(null) == null} {idNode(new Node(3))!.Id}");
        Console.WriteLine($"same instantiation equal: {idInt == (delegate*<int, int>)&Id<int>} others differ: {(nint)idInt != (nint)idStr}");
        Console.WriteLine($"through a result: {Identity<int>()(9)} {Identity<string>()("t")} {Identity<double>()(1.5)}");

        var ints = new Ops<int> { Combine = &Add, Touch = &Bump, At = &ElementAt };
        var strs = new Ops<string> { Combine = &Cat, Touch = &Null, At = &ElementAt };
        var nodes = new Ops<Node> { Combine = &Second, Touch = &Null, At = &ElementAt };
        int x = 1; ints.Touch(ref x); string? s = "gone"; strs.Touch(ref s!);
        Console.WriteLine($"struct of pointers: {ints.Combine(2, 3)} {strs.Combine("a", "b")} {nodes.Combine(new Node(1), new Node(2)).Id} {x} {s == null}");

        var li = new List<int> { 1, 2, 3 }; var ls = new List<string> { "a", "b", "c" }; var ln = new List<Node> { new Node(1), new Node(2), new Node(3) };
        ln[0].Next = ln[1]; ln[1].Next = ln[0];
        ref int ri = ref ints.At(li, 1); ref string rs = ref strs.At(ls, 2); ref Node rn = ref nodes.At(ln, 0);
        for (int i = 0; i < 50; i++) { li.Add(i); ls.Add("x" + i); ln.Add(new Node(100 + i) { Next = ln[0] }); }
        ri += 40; rs += "!"; rn = new Node(77);
        Console.WriteLine($"refs into storage that grew: {ri} {rs} {rn.Id} lists {li[1]} {ls[2]} {ln[0].Id}");
        delegate*<List<Node>, Span<Node>> sp = &SpanOf;
        var span = sp(ln); ln.Capacity = ln.Count + 100; ln[1] = new Node(5);
        Console.WriteLine($"span through a pointer, list grown: {span.Length} {span[1].Id} {span[1].Next!.Id}");

        var t = new Table<int>(); t.Show[0] = &Plain; t.Show[1] = &Typed; t.Copy = &CopyTo;
        var ts = new Table<string>(); ts.Show[0] = &Typed; ts.Show[1] = &Plain; ts.Copy = &CopyTo;
        bool c1 = t.Copy(7, out int got), c2 = ts.Copy(null!, out string gs);
        Console.WriteLine($"generic class: {t.Both(4)} {ts.Both("q")} {c1} {got} {c2} {gs == null}");

        delegate*<ref int, ref int, void> swap = &Swap; int a = 1, b = 2; swap(ref a, ref b);
        delegate*<ref Node, ref Node, void> swapN = &Swap; Node na = new Node(1), nb = new Node(2); swapN(ref na, ref nb);
        delegate*<ref int, ref int, ref int> larger = &Larger; larger(ref a, ref b) = 50;
        Console.WriteLine($"by ref: {a} {b} {na.Id} {nb.Id}");

        Console.WriteLine($"as arguments: {Apply(&Add, 3, 4)} {Apply(&First, 3, 4)} {Apply(&Second<int>, 3, 4)} {Twice<string, int>(&Len, &Stars, "abcd")}");
        delegate*<int, int, int>[] table = { &Add, &First, &Second, &Add };
        int sum = 0; foreach (var f in table) sum = f(sum, 5);
        Console.WriteLine($"array of pointers: {sum} {table[0] == table[3]} {table[0] == table[1]}");

        nint raw = (nint)(delegate*<int, int, int>)&Add; var back = (delegate*<int, int, int>)raw; void* v = back;
        Console.WriteLine($"cast: {back(20, 22)} {((delegate*<int, int, int>)v)(1, 1)} {raw != 0}");

        delegate*<int, int> sq = &Square; static int Square(int n) => n * n;
        Console.WriteLine($"local function: {sq(12)}");

        Span<Node> made = new Node[4]; Fill(made, &MakeNode); Span<int> mi = stackalloc int[3]; Fill(mi, &Id);
        Console.WriteLine($"filled: {made[3].Id} {mi[2]}");

        delegate*<long, double, byte, ref short, out decimal, long> wide = &Wide; short sh = 4;
        long w = wide(10, 2.5, 3, ref sh, out decimal dec);
        Console.WriteLine($"many kinds: {w} {dec} {sh}");
        Console.WriteLine($"sizes: {sizeof(delegate*<int, int>) == sizeof(nint)}");
    }
}
