// A generic type's small methods over different type arguments, each calling its own instantiation's larger one: a
// dictionary's TryGetValue, ContainsKey and indexer over its FindValue, with keys of int, string, long, a struct and
// a class with its own Equals, values of several classes, comparers given and not; the value that comes out, the
// one left when the key is not there, the exceptions (a null key, a key not found, a comparer that throws); the
// same for a sorted dictionary, a set's TryGetValue, a lookup through an interface and through a delegate. Each
// instantiation must look its key up with its own comparison: one that used another's would find the wrong entries.
using System;
using System.Runtime.InteropServices;
using System.Collections.Generic;

sealed class Node { public int V; public string Name; public Node(int v, string name) { V = v; Name = name; } public override string ToString() => Name + ":" + V; }
sealed class Other { public string S; public Other(string s) { S = s; } public override string ToString() => "other " + S; }
readonly record struct Cell(int X, int Y);
sealed class Key
{
    public readonly int A; public readonly string B;
    public Key(int a, string b) { A = a; B = b; }
    public override bool Equals(object? o) => o is Key k && k.A == A && k.B == B;
    public override int GetHashCode() => A * 31 + B.Length;
    public override string ToString() => A + "/" + B;
}
sealed class Mod10 : IEqualityComparer<int>
{
    public int Calls;
    public bool Equals(int a, int b) { Calls++; return a % 10 == b % 10; }
    public int GetHashCode(int a) => a % 10;
}
sealed class Throwing : IEqualityComparer<string>
{
    public bool Equals(string? a, string? b) => a == b;
    public int GetHashCode(string a) => a == "boom" ? throw new InvalidOperationException("no hash for " + a) : a.Length;
}

static class Program
{
    static string Show<T>(bool found, T? value) => (found ? "found " : "missing ") + (value == null ? "null" : value.ToString());

    static void Ints()
    {
        var d = new Dictionary<int, Node>();
        for (int i = 0; i < 40; i++) d[i * 3] = new Node(i, "n" + i);
        long sum = 0; int hits = 0;
        for (int k = -5; k < 130; k++) if (d.TryGetValue(k, out var n)) { sum += n.V; hits++; }
        Console.WriteLine($"int keys: {hits} hits, sum {sum}, count {d.Count}");
        Node? left = new Node(-1, "before");
        bool f = d.TryGetValue(1, out left);
        Console.WriteLine(Show(f, left));
        f = d.TryGetValue(9, out left);
        Console.WriteLine(Show(f, left));
        Console.WriteLine(d.ContainsKey(117) + " " + d.ContainsKey(118) + " " + d[117] + " " + d.GetValueOrDefault(118));
        try { Console.WriteLine(d[118]); } catch (KeyNotFoundException e) { Console.WriteLine(e.GetType().Name + ": " + e.Message); }
    }

    static void Strings()
    {
        var d = new Dictionary<string, Node>();
        for (int i = 0; i < 40; i++) d["k" + i * 3] = new Node(i, "s" + i);
        long sum = 0; int hits = 0;
        for (int k = -5; k < 130; k++) if (d.TryGetValue("k" + k, out var n)) { sum += n.V; hits++; }
        Console.WriteLine($"string keys: {hits} hits, sum {sum}, count {d.Count}");
        var o = new Dictionary<string, Other>(StringComparer.OrdinalIgnoreCase) { ["Wood"] = new Other("w"), ["IRON"] = new Other("i") };
        Console.WriteLine(Show(o.TryGetValue("wood", out var a), a) + ", " + Show(o.TryGetValue("iron", out var b), b) + ", " + Show(o.TryGetValue("gold", out var c), c));
        var s = new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" };
        Console.WriteLine(Show(s.TryGetValue("b", out var sv), sv) + ", " + Show(s.TryGetValue("B", out sv), sv) + ", " + s.ContainsKey("a") + s.ContainsKey("A"));
        try { d.TryGetValue(null!, out _); } catch (ArgumentNullException e) { Console.WriteLine(e.GetType().Name + ": " + e.ParamName); }
        try { Console.WriteLine(d.ContainsKey(null!)); } catch (ArgumentNullException e) { Console.WriteLine(e.GetType().Name + ": " + e.ParamName); }
        try { Console.WriteLine(s["zz"]); } catch (KeyNotFoundException e) { Console.WriteLine(e.GetType().Name + ": " + e.Message); }
        var t = new Dictionary<string, Node>(new Throwing()) { ["ab"] = new Node(2, "ab") };
        Console.WriteLine(Show(t.TryGetValue("ab", out var tn), tn));
        try { t.TryGetValue("boom", out tn); } catch (InvalidOperationException e) { Console.WriteLine(e.Message + ", left " + (tn == null ? "null" : tn.ToString())); }
    }

    static void Others()
    {
        var m = new Mod10();
        var d = new Dictionary<int, Node>(m) { [3] = new Node(3, "three"), [14] = new Node(14, "fourteen") };
        Console.WriteLine(Show(d.TryGetValue(23, out var a), a) + ", " + Show(d.TryGetValue(104, out var b), b) + ", " + Show(d.TryGetValue(5, out var c), c) + ", comparer calls " + m.Calls);
        var l = new Dictionary<long, Other> { [1L << 40] = new Other("big"), [7] = new Other("seven") };
        Console.WriteLine(Show(l.TryGetValue(1L << 40, out var lo), lo) + ", " + Show(l.TryGetValue(7, out lo), lo) + ", " + Show(l.TryGetValue(8, out lo), lo));
        var cells = new Dictionary<Cell, Node> { [new Cell(1, 2)] = new Node(12, "c") };
        Console.WriteLine(Show(cells.TryGetValue(new Cell(1, 2), out var cn), cn) + ", " + Show(cells.TryGetValue(new Cell(2, 1), out cn), cn));
        var keys = new Dictionary<Key, Other> { [new Key(1, "x")] = new Other("kx") };
        Console.WriteLine(Show(keys.TryGetValue(new Key(1, "x"), out var ko), ko) + ", " + Show(keys.TryGetValue(new Key(1, "y"), out ko), ko));
        var ints = new Dictionary<int, int> { [1] = 10, [2] = 20 };
        Console.WriteLine(Show(ints.TryGetValue(2, out int iv), iv) + ", " + Show(ints.TryGetValue(3, out iv), iv));
        var sorted = new SortedDictionary<string, Node> { ["b"] = new Node(2, "b"), ["a"] = new Node(1, "a") };
        Console.WriteLine(Show(sorted.TryGetValue("a", out var sn), sn) + ", " + Show(sorted.TryGetValue("c", out sn), sn));
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Stone" };
        Console.WriteLine(Show(set.TryGetValue("STONE", out var actual), actual) + ", " + Show(set.TryGetValue("sand", out actual), actual));
        IReadOnlyDictionary<int, Node> ro = d;
        IDictionary<string, Node> id = new Dictionary<string, Node> { ["q"] = new Node(9, "q") };
        Console.WriteLine(Show(ro.TryGetValue(33, out var rn), rn) + ", " + Show(id.TryGetValue("q", out var qn), qn) + ", " + Show(id.TryGetValue("r", out qn), qn));
        TryGet<int, Node> viaDelegate = d.TryGetValue;
        Console.WriteLine(Show(viaDelegate(4, out var dn), dn) + ", " + Show(viaDelegate(6, out dn), dn));
    }
    delegate bool TryGet<K, V>(K key, out V value);

    [UnmanagedCallersOnly(EntryPoint = "main")] static void Main()
    {
        Ints();
        Strings();
        Others();
    }
}
