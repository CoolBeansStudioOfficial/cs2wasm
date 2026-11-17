// `foreach` over a List<T> (section 3: the enumerator's semantics; section 4.1: the element borrows): elements of
// every kind, the list changed in the loop, the variable captured, the loop left early.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

struct Item { public string Name; public int N; public object? Tag; }
sealed class Node { public string Id = ""; public List<Node> Kids = new(); public int V; }
class MyList : List<string> { public MyList(IEnumerable<string> s) : base(s) { } }

static class Program
{
    static List<Node> s_nodes = new();

    static string Try(Action a)
    {
        try { a(); return "ok"; }
        catch (Exception e) { return e.GetType().Name + ": " + e.Message; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static long Sum(List<int> xs) { long s = 0; foreach (int x in xs) s += x; return s; }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static string Names(List<Item> items)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var it in items) { sb.Append(it.Name).Append(it.N); if (it.Tag is string t) sb.Append(t); sb.Append(','); }
        return sb.ToString();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int Walk(Node n, int depth)
    {
        int s = n.V * depth;
        foreach (var k in n.Kids) s += Walk(k, depth + 1);
        return s;
    }

    static void ChangeDuring()
    {
        var xs = new List<string> { "a", "b", "c" };
        Console.WriteLine(Try(() => { foreach (var x in xs) if (x == "b") xs.Add("d"); }));
        Console.WriteLine(xs.Count);
        Console.WriteLine(Try(() => { foreach (var x in xs) if (x == "d") xs.Remove("a"); }));   // changed in the last round
        Console.WriteLine(Try(() => { foreach (var x in xs) { xs.Clear(); } }));
        Console.WriteLine(xs.Count);
        var ys = new List<int> { 1, 2, 3 };
        Console.WriteLine(Try(() => { foreach (var y in ys) ys[0] = 9; }));   // a set through the indexer changes no version
        Console.WriteLine(Try(() => { foreach (var y in ys) if (y == 3) ys.Sort(); }));
        List<int>? none = null;
        Console.WriteLine(Try(() => { foreach (var y in none!) Console.WriteLine(y); }));
    }

    static void Replaced()
    {
        // the element outlives the list's hold on it
        var holder = new List<Node> { new Node { Id = "p" }, new Node { Id = "q" }, new Node { Id = "r" } };
        s_nodes = holder;
        string seen = "";
        try
        {
            foreach (var n in s_nodes)
            {
                if (n.Id == "p") { s_nodes = new List<Node>(); holder = null!; }   // the old list lives on in the loop
                seen += n.Id;
            }
        }
        catch (Exception e) { seen += e.GetType().Name; }
        Console.WriteLine(seen + s_nodes.Count);
        var items = new List<Item> { new Item { Name = "x", N = 1, Tag = "t" }, new Item { Name = "y", N = 2 } };
        string got = "";
        try
        {
            foreach (var it in items)
            {
                items[1] = new Item { Name = "z", N = 3 };   // the element read later is the new one
                got += it.Name + it.N;
            }
        }
        catch (Exception e) { got += e.GetType().Name; }
        Console.WriteLine(got);
        var refs = new List<string> { "one", "two", "three" };
        string acc = "";
        try
        {
            foreach (var r in refs)
            {
                if (r == "one") refs[1] = "TWO";    // an element stored while one is held
                acc += r;
            }
        }
        catch (Exception e) { acc += e.GetType().Name; }
        Console.WriteLine(acc + refs[1]);
        var arr = new[] { new Node { Id = "a1" }, new Node { Id = "a2" } };
        var inArray = new List<Node>(arr);
        string ids = "";
        foreach (var n in inArray) { arr[0] = null!; arr[1] = null!; ids += n.Id; }
        Console.WriteLine(ids);
    }

    static void Captured()
    {
        var acts = new List<Func<string>>();
        var words = new List<string> { "u", "v", "w" };
        foreach (var w in words) acts.Add(() => w + w);
        foreach (var a in acts) Console.Write(a() + " ");
        Console.WriteLine();
        var pairs = new List<(int a, string b)> { (1, "i"), (2, "ii"), (3, "iii") };
        int total = 0; string s = "";
        foreach (var (a, b) in pairs) { if (a == 2) continue; total += a; s += b; }
        Console.WriteLine(total + s);
        foreach (object o in words) { if (o is string str && str == "v") break; Console.Write(o); }
        Console.WriteLine();
        var my = new MyList(new[] { "m1", "m2" });
        foreach (var m in my) Console.Write(m);
        foreach (var m in (List<string>)my) Console.Write(m.Length);
        Console.WriteLine();
        var nested = new List<List<int>> { new() { 1, 2 }, new(), new() { 3 } };
        int n = 0;
        foreach (var inner in nested) foreach (var v in inner) n = n * 10 + v;
        Console.WriteLine(n);
        IEnumerable<int> asInterface = new List<int> { 4, 5 };
        foreach (var v in asInterface) Console.Write(v);
        foreach (var v in new List<int> { 6, 7 }.Where(x => x > 6)) Console.Write(v);
        Console.WriteLine();
    }

    static async System.Threading.Tasks.Task<int> InAsync(List<int> xs)
    {
        int s = 0;
        foreach (var x in xs) { await System.Threading.Tasks.Task.Yield(); s += x; }
        return s;
    }

    static IEnumerable<string> InIterator(List<string> xs) { foreach (var x in xs) yield return x + "!"; }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        var ints = new List<int>();
        for (int i = 0; i < 1000; i++) ints.Add(i * 3);
        Console.WriteLine(Sum(ints) + " " + Sum(new List<int>()));
        var items = new List<Item>();
        for (int i = 0; i < 5; i++) items.Add(new Item { Name = "n" + i, N = i, Tag = i % 2 == 0 ? "e" : null });
        Console.WriteLine(Names(items));
        var root = new Node { V = 1 };
        for (int i = 0; i < 3; i++) { var k = new Node { V = i + 2 }; k.Kids.Add(new Node { V = 10 }); root.Kids.Add(k); }
        Console.WriteLine(Walk(root, 1));
        ChangeDuring();
        Replaced();
        Captured();
        Console.WriteLine(InAsync(new List<int> { 1, 2, 3 }).GetAwaiter().GetResult());
        Console.WriteLine(string.Join("", InIterator(new List<string> { "a", "b" })));
        long big = 0;
        var many = new List<Item>();
        for (int i = 0; i < 20000; i++) many.Add(new Item { Name = (i % 10).ToString(), N = i });
        for (int r = 0; r < 20; r++) foreach (var it in many) big += it.N + it.Name.Length;
        Console.WriteLine(big);
    }
}
