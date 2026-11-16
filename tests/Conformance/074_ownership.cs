// Ownership (section 4.1): moves of a local's last use into an owner (a field, an element, another local, a return),
// with the cases a move must not take: a `for` loop's variable (its increment runs after the body's last use), a
// local declared outside a loop and read in it, a use inside a lambda, `goto`. Getters that return a field return it
// borrowed. Everything prints what .NET prints; destruction order is observed through finalizers' side effects on a
// log kept in a static list.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

sealed class Node
{
    public int V; public Node? Next;
    public Node(int v) { V = v; }
}

struct Pair
{
    public Node A, B;
    public Pair(Node a, Node b) { A = a; B = b; }
    public int Sum => A.V + B.V;
    public Node First => A;
}

struct Elem
{
    public Node Value;
    public static implicit operator Node(Elem e) => e.Value;
}

struct Cursor<T> where T : class
{
    T[] _items; int _i;
    public Cursor(T[] items) { _items = items; _i = 0; }
    public T Current => _items[_i];
    public bool Next() => ++_i < _items.Length;
}

readonly struct Frozen
{
    public readonly Node A, B;
    public Frozen(Node a, Node b) { A = a; B = b; }
    public int Read() { var r = new FrozenReader(this); return r.Sum(); }
}

struct FrozenReader
{
    Node _a, _b;
    public FrozenReader(Frozen f) { _a = f.A; _b = f.B; }
    public int Sum() => _a.V + _b.V;
}

struct Wrap
{
    public Pair P; public int K;
    public Wrap(Pair p, int k) { K = k; P = p; }
}

sealed class Holder { public Pair P; public Pair[] Ps = new Pair[2]; }

sealed class Box
{
    Node? _held;
    public Node? Held => _held;
    Node? _lazy;
    public Node Lazy => _lazy ??= new Node(42);
    public void Reset() { _lazy = null; }
    public Node? Take() { var h = _held; _held = null; return h; }
    public void Put(Node n) { var local = n; _held = local; }
}

static class Program
{
    static Node Make(int v) { var n = new Node(v); n.Next = new Node(v + 1); return n; }

    static int Walk(Node? head)
    {
        Node? prev = null; int n = 0;
        for (Node? node = head; node is not null; node = node.Next) { n += node.V; prev = node; }
        return n + (prev?.V ?? 0);
    }

    static int OutsideLoop(Node seed)
    {
        Node cur = seed; int sum = 0;
        var keep = new List<Node>();
        for (int i = 0; i < 3; i++) { keep.Add(cur); sum += cur.V; cur = new Node(cur.V * 2); }
        return sum + keep.Count + cur.V;
    }

    static Node[] IntoArray()
    {
        var a = new Node(1); var b = new Node(2);
        return [a, b];
    }

    static Func<int> Captured()
    {
        var n = new Node(7);
        return () => n.V;
    }

    static int WithGoto(int k)
    {
        var n = new Node(k);
        int r = 0;
    again:
        r += n.V;
        if (r < 10) goto again;
        return r;
    }

    // struct moves: a call's struct result or a constructed struct handed to a variable, a field, an element, a return
    static Pair MakePair(int v) => new Pair(new Node(v), new Node(v + 1));
    static Pair Forward(int v) { return MakePair(v); }
    static Pair Swap(Pair p) => new Pair(p.B, p.A);

    static int Throws() => throw new InvalidOperationException("thrown between the arguments");

    static void StructMoves()
    {
        var p = Forward(1);
        var h = new Holder();
        h.P = MakePair(10);
        h.Ps[0] = Swap(h.P);
        h.Ps[1] = new Pair(h.P.A, p.B);
        p = Swap(p);
        h.P = Swap(h.P);
        Console.WriteLine(p.Sum + " " + p.A.V + " " + h.P.A.V + " " + h.Ps[0].A.V + " " + h.Ps[1].Sum + " " + (h.Ps[1].A == h.P.B));
        switch (MakePair(4)) { case { A.V: 4 } q: Console.WriteLine(q.Sum); break; }
        int n = 0;
        for (int i = 0; i < 3; i++) { var q = i % 2 == 0 ? MakePair(i) : Swap(MakePair(i)); n += q.A.V; }
        Console.WriteLine(n);
        Node fromElem = new Elem { Value = new Node(50) };
        var es = new Elem[] { new Elem { Value = new Node(51) } };
        Node e0 = es[0]; es[0] = default;
        var cur = new Cursor<Node>([new Node(60), new Node(61)]);
        int cs = 0; do cs += cur.Current.V; while (cur.Next());
        Console.WriteLine(fromElem.V + " " + e0.V + " " + cs);
        var fz = new Frozen(new Node(70), new Node(71));
        var fzs = new[] { fz };
        Console.WriteLine(fz.Read() + " " + fzs[0].Read() + " " + new Frozen(new Node(1), new Node(2)).Read());
        var w1 = new Wrap(MakePair(80), 1);
        var pl = MakePair(90);
        var w2 = new Wrap(pl, 2);
        var w3 = new Wrap(h.P, 3);
        Console.WriteLine(w1.P.Sum + " " + w2.P.Sum + " " + pl.Sum + " " + (w2.P.A == pl.A) + " " + w3.P.Sum + " " + (w3.P.B == h.P.B));
        try { var w4 = new Wrap(MakePair(1), Throws()); Console.WriteLine(w4.K); } catch (InvalidOperationException e) { Console.WriteLine(e.Message); }
        var f = MakePair(20).First; var g = h.P.First;
        h.P = MakePair(30);
        Console.WriteLine(f.V + " " + g.V + " " + h.P.First.V + " " + Swap(h.P).First.V);
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        var m = Make(3);
        Console.WriteLine(m.V + " " + m.Next!.V + " " + Walk(m));
        Console.WriteLine(OutsideLoop(new Node(1)));
        var arr = IntoArray();
        Console.WriteLine(arr[0].V + arr[1].V);
        Console.WriteLine(Captured()());
        Console.WriteLine(WithGoto(3));
        var box = new Box();
        var x = new Node(5);
        box.Put(x);
        Console.WriteLine(box.Held!.V + " " + (box.Held == x));
        var taken = box.Take();
        Console.WriteLine((taken?.V ?? -1) + " " + (box.Held == null));
        Node? y = new Node(9);
        var z = y;
        y = null;
        Console.WriteLine(z.V + " " + (y == null));
        StructMoves();
        var lz = box.Lazy; var lz2 = box.Lazy;
        box.Reset();
        Console.WriteLine(lz.V + " " + (lz == lz2) + " " + (box.Lazy == lz) + " " + box.Lazy.V);
    }
}
