// Stream K's changes to the code made, each against what .NET does: a struct returned straight into the caller's
// place (a constructor or a call that throws half way, a return inside try, a chain of returns); an element of an array
// a static holds, kept while code runs that replaces it (the element must stay alive) or touches the type's static
// initializer; a struct field set to default (its old references released); Array.Clear of a few and of many elements.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

sealed class Node
{
    public readonly string Name;
    public Node(string n) { Name = n; }
}

struct Pair
{
    public Node A; public Node B; public int N;
    public Pair(Node a, Node b, int n) { A = a; B = b; N = n; }
    public Pair(Node a, int fail)
    {
        A = a; N = fail;
        if (fail > 0) throw new InvalidOperationException("half made " + fail);
        B = a;
    }
    public override string ToString() => (A?.Name ?? "-") + "/" + (B?.Name ?? "-") + "/" + N;
}

struct Wide { public Node A, B, C; public long X; }

static class Cache
{
    static readonly Node[] s_items = Make();
    static Node[] Make() { var a = new Node[4]; for (int i = 0; i < 4; i++) a[i] = new Node("item" + i); return a; }
    public static void Replace(int i) { s_items[i] = new Node("new" + i); Churn(); }
    public static string ReadAcross(int i)
    {
        var e = s_items[i];          // borrowed from the static's array?
        Replace(i);                  // the element replaced meanwhile: e must stay alive
        return e.Name + "," + s_items[i].Name;
    }
    public static string ReadAcrossInit(int i)
    {
        var e = s_items[i];
        int n = Late.Value;          // another type's initializer runs in between
        return e.Name + "," + n;
    }
    public static int Count() => s_items.Length;
    public static string ReadAcrossCheck(int i)
    {
        var e = s_items[i];          // borrowed: what runs meanwhile only checks the type's initializer
        int n = Count() + Count();
        return e.Name + "," + n;
    }
    public static void Churn() { var l = new List<Node>(); for (int i = 0; i < 50; i++) l.Add(new Node("c" + i)); }
}

static class Late
{
    public static readonly int Value = Compute();
    static int Compute() { Cache.Replace(1); return 7; }
}

sealed class Holder
{
    public Pair P;
    public Wide W;
}

static class P
{
    static Pair Make(string a, string b, int n) => new Pair(new Node(a), new Node(b), n);
    static Pair Chain(string a, string b, int n) => Make(a + "+", b + "+", n + 1);
    static Pair Half(string a, int fail) => new Pair(new Node(a), fail);
    static Pair InTry(string a, int fail)
    {
        try { return new Pair(new Node(a), fail); }
        catch (InvalidOperationException e) { Console.WriteLine("caught " + e.Message); return default; }
    }
    static Pair InFinally(string a, int fail, List<string> log)
    {
        try { return Half(a, fail); }
        finally { log.Add("finally " + a); }
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        Console.WriteLine(Make("a", "b", 1));
        Console.WriteLine(Chain("c", "d", 2));
        var list = new List<Pair>();
        for (int i = 0; i < 5; i++) list.Add(Chain("x" + i, "y" + i, i));
        Console.WriteLine(list[4]);
        try { var p = Half("h", 3); Console.WriteLine(p); }
        catch (InvalidOperationException e) { Console.WriteLine("half: " + e.Message); }
        for (int i = 0; i < 3; i++) { try { Console.WriteLine(Half("loop" + i, i)); } catch (InvalidOperationException e) { Console.WriteLine(e.Message); } }
        Console.WriteLine(InTry("t", 0));
        Console.WriteLine(InTry("t", 2));
        var log = new List<string>();
        try { InFinally("f", 1, log); } catch (InvalidOperationException e) { Console.WriteLine("fin: " + e.Message); }
        Console.WriteLine(InFinally("g", 0, log) + " " + string.Join(";", log));

        Console.WriteLine(Cache.ReadAcross(0));
        Console.WriteLine(Cache.ReadAcrossInit(1));
        Console.WriteLine(Cache.ReadAcross(1));
        Console.WriteLine(Cache.ReadAcrossCheck(2));

        var h = new Holder { P = Make("p", "q", 9), W = new Wide { A = new Node("wa"), B = new Node("wb"), C = new Node("wc"), X = 5 } };
        Console.WriteLine(h.P + " " + h.W.A.Name + h.W.X);
        h.P = default;
        h.W = new Wide();
        Console.WriteLine(h.P + " " + (h.W.A == null) + h.W.X);
        h.P = Make("r", "s", 3);
        Console.WriteLine(h.P);

        var small = new Node[8];
        for (int i = 0; i < 8; i++) small[i] = new Node("s" + i);
        Array.Clear(small, 2, 3);
        Console.WriteLine(string.Join(",", Array.ConvertAll(small, n => n?.Name ?? "-")));
        var big = new Node[300];
        for (int i = 0; i < 300; i++) big[i] = new Node("b" + i);
        Array.Clear(big, 1, 298);
        Console.WriteLine(big[0].Name + " " + (big[150] == null) + " " + big[299].Name);
        var bytes = new byte[7];
        for (int i = 0; i < 7; i++) bytes[i] = (byte)(i + 1);
        Array.Clear(bytes, 1, 5);
        Console.WriteLine(string.Join(",", bytes));
        var pairs = new Pair[5];
        for (int i = 0; i < 5; i++) pairs[i] = Make("k" + i, "l" + i, i);
        Array.Clear(pairs, 0, 4);
        Console.WriteLine(pairs[0] + " " + pairs[4]);
    }
}
