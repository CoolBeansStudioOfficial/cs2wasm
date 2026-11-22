// Two things a store into an object can be told at compile time, each counted against what .NET frees.
// A constructor's own stores are plain counted stores when that constructor, the one it hands over to and the bases'
// keep `this` in (Driver.CtorKeepsThis: asked of the constructor that runs, not of every constructor of the class):
// classes with one constructor that keeps `this` in and one that lets it out (into a static list, to a method, to
// the object it is given, into a lambda or a local function that names a field: the last was taken as keeping it in,
// and the object, a cycle with its own delegate, was never freed), `: this(...)` to either kind, a base that lets
// it out, a constructor that stores `this` into its own field, a list class of the program's built as List<T> is
// (an empty array from a static in one constructor, Add on `this` in the other), and a cycle closed after the
// constructors by a store from outside.
// And a store `c.F = v;` that is the last use of the local c (Rt.ArenaLastBegin, LastGiveBack: the local's count
// given back at the link, where it would make c hold its owners until its scope ends): the parent set after the
// child is in its list, in a tree that lives on and is counted while it does; the same with the child used before
// in calls; a value that is the top of the child's own chain and has no other reference (the count stays: the hold
// is what keeps it); the child stored into its own field; a child that is in no list; a child with a parent
// already (moved); the local declared outside the loop; the parent in a field past the sixteenth word; nodes with
// finalizers that see their parent; what was read through the local kept after the store (a field's value in
// another local, an enumerator: the count stays, by the rule of the text).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

class Tracked
{
    public static readonly List<string> Kinds = new();
    public static readonly int[] Live = new int[64];
    readonly int _kind;
    public Tracked(string kind)
    {
        int k = Kinds.IndexOf(kind); if (k < 0) { k = Kinds.Count; Kinds.Add(kind); }
        _kind = k;
        Live[k]++;
    }
    ~Tracked() { System.Threading.Interlocked.Decrement(ref Live[_kind]); }
    public static void Report(string when)
    {
        for (int i = 0; i < 4; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        Console.WriteLine(when + ": " + string.Join(", ", Kinds.Select((k, i) => (k, i)).OrderBy(x => x.k, StringComparer.Ordinal).Select(x => x.k + " " + Live[x.i])));
    }
}

sealed class Tag : Tracked { public Tag(string kind) : base(kind) { } }

// ---- constructors ----

// a lambda that names a field of `this`, stored into the object: a cycle of the object and its delegate
sealed class Counter
{
    Func<int> _next; int _count; public Tag Tag = new Tag("counter");
    public Counter() { _next = () => _count++; }
    public int Next() => _next();
}

// the same by a local function, and by a lambda in the constructor the first hands over to
sealed class Ticker
{
    Func<int> _next; int _count; public List<Ticker> Others = new(); public Tag Tag = new Tag("ticker");
    public Ticker() { int Step() => _count += 2; _next = Step; }
    public Ticker(int start) : this() { _count = start; }
    public int Next() => _next();
}

// one constructor keeps `this` in, the others let it out: into a static list, to a method of its own, to its argument
sealed class Mixed
{
    public static readonly List<Mixed> All = new();
    public Mixed Peer; public List<Mixed> Kids = new(); public Mixed[] Arr; public Tag Tag;
    public Mixed(string kind) { Tag = new Tag(kind); Arr = Array.Empty<Mixed>(); }
    public Mixed(string kind, bool register) { Tag = new Tag(kind); Kids = new List<Mixed>(); if (register) All.Add(this); }
    public Mixed(string kind, int n) { Tag = new Tag(kind); Fill(n); }
    public Mixed(string kind, Mixed peer) { Tag = new Tag(kind); Peer = peer; peer.Peer = this; }
    // (hands over to the one that keeps it in, then stores itself)
    public Mixed(string kind, char self) : this(kind) { Peer = this; }
    // (hands over to the one that lets it out)
    public Mixed(string kind, long n) : this(kind, (int)n) { Arr = new Mixed[1]; }
    void Fill(int n) { for (int i = 0; i < n; i++) Kids.Add(new Mixed("mixed kid") { Peer = this }); }
}

// a base that lets `this` out, a derived class whose own constructor does not
class Base
{
    public static Base Last;
    public Base Other; public Tag Tag;
    public Base(string kind, bool keep) { Tag = new Tag(kind); if (keep) Last = this; }
}
sealed class Derived : Base
{
    public List<Base> Kids = new(); public Base Mine;
    public Derived(string kind, bool keep) : base(kind, keep) { Mine = new Base("derived's", false); Kids.Add(Mine); Mine.Other = this; }
}

// built as List<T> is: an empty array from a static in one constructor, Add on `this` in the other
sealed class Bag<T> : IEnumerable<T>
{
    static readonly T[] s_empty = new T[0];
    T[] _items; int _size;
    public Bag() { _items = s_empty; }
    public Bag(IEnumerable<T> all) { _items = s_empty; foreach (var x in all) Add(x); }
    public void Add(T x) { if (_size == _items.Length) { var n = new T[_size == 0 ? 2 : 2 * _size]; Array.Copy(_items, n, _size); _items = n; } _items[_size++] = x; }
    public int Count => _size;
    public T this[int i] => _items[i];
    public IEnumerator<T> GetEnumerator() { for (int i = 0; i < _size; i++) yield return _items[i]; }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
sealed class Leaf
{
    public Leaf Up; public Bag<Leaf> Kids = new(); public Tag Tag;
    public Leaf(string kind) { Tag = new Tag(kind); }
}

// a cycle closed after the constructors, by a store from outside
sealed class Left { public Right R; public Tag Tag = new Tag("left"); public Left(Right r) { R = r; } }
sealed class Right { public Left L; public Tag Tag = new Tag("right"); }

// ---- the last store ----

sealed class Node
{
    public Node Up, Other; public List<Node> Kids = new(); public Tag Tag; public int Id;
    public Node A0, A1, A2, A3, A4, A5, A6, A7, A8, A9, A10, A11, A12, FarUp;
    public Node UpProp { get; set; }
    public Node(string kind, int id = 0) { Tag = new Tag(kind); Id = id; }
    public int Count() { int n = 1; foreach (var c in Kids) if (c != null) n += c.Count(); return n; }
    public bool Sound() { foreach (var c in Kids) if (c == null || c.Up != this || !c.Sound()) return false; return true; }
}

// nodes with a finalizer of their own: each sees its parent and its children as it goes
sealed class Fin
{
    public static long Seen; public static int Ran;
    public Fin Up; public List<Fin> Kids = new(); public int Id;
    public Fin(int id) { Id = id; }
    ~Fin() { Ran++; if (Up != null) Seen += Up.Id; foreach (var k in Kids) if (k != null) Seen += 3 * k.Id + (k.Up == this ? 1000 : 0); }
}

static class P
{
    static Node s_tree, s_park, s_used, s_moved, s_far;
    static Fin s_fin;
    static long s_sum;

    // ---- constructors ----
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Counters()
    {
        for (int i = 0; i < 20; i++) { var c = new Counter(); s_sum += c.Next() + c.Next(); }
        for (int i = 0; i < 20; i++)
        {
            var t = i % 2 == 0 ? new Ticker() : new Ticker(i);
            t.Others.Add(new Ticker(1));
            t.Others[0].Others.Add(t);
            s_sum += t.Next() + t.Others[0].Next();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Mixeds()
    {
        for (int i = 0; i < 10; i++)
        {
            var plain = new Mixed("mixed plain");
            var reg = new Mixed("mixed registered", i % 2 == 0);
            var filled = new Mixed("mixed filled", 3);
            var pair = new Mixed("mixed pair", plain);
            var self = new Mixed("mixed self", 's');
            var chained = new Mixed("mixed chained", 2L);
            // (cycles through the lists and arrays the constructors stored)
            plain.Kids.Add(filled); filled.Kids.Add(plain);
            self.Kids.Add(chained); chained.Arr[0] = self;
            reg.Kids.Add(pair);
            s_sum += plain.Kids.Count + filled.Kids.Count + chained.Kids.Count + (pair.Peer == plain ? 1 : 0) + (self.Peer == self ? 1 : 0);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void MixedsGo() { Mixed.All.Clear(); }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Bases()
    {
        for (int i = 0; i < 10; i++)
        {
            var d = new Derived("derived", i == 9);
            var e = new Derived("derived", false);
            d.Other = e; e.Other = d;
            s_sum += d.Kids.Count + (d.Mine.Other == d ? 1 : 0);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void BasesGo() { Base.Last = null; }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static Leaf Leaves(Leaf up, int depth)
    {
        var e = new Leaf("leaf") { Up = up };
        if (depth > 0) for (int i = 0; i < 3; i++) e.Kids.Add(Leaves(e, depth - 1));
        return e;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Bags()
    {
        for (int i = 0; i < 5; i++)
        {
            var t = Leaves(null, 3);
            var copy = new Bag<Leaf>(t.Kids);
            var u = new Leaf("leaf copy");
            foreach (var k in copy) u.Kids.Add(k);
            s_sum += copy.Count + u.Kids.Count + t.Kids[0].Kids.Count;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Outside()
    {
        for (int i = 0; i < 10; i++)
        {
            var r = new Right();
            var l = new Left(r);
            r.L = l;
            s_sum += l.R == r ? 1 : 0;
        }
    }

    // ---- the last store ----

    // the parent set after the child is in its list
    static Node After(string kind, int depth, int fan)
    {
        var e = new Node(kind, depth);
        if (depth > 0) for (int i = 0; i < fan; i++) { var c = After(kind, depth - 1, fan); e.Kids.Add(c); c.Up = e; }
        return e;
    }

    static void Note(Node n) { s_sum += n.Id; }

    // the child used before in calls and stores; and what the store's value is
    static Node Used(string kind, int depth, int fan)
    {
        var e = new Node(kind, depth);
        if (depth > 0)
            for (int i = 0; i < fan; i++)
            {
                var c = Used(kind, depth - 1, fan);
                Note(c);
                c.Id += i;
                c.Other = i == 0 ? null : e.Kids[i - 1];
                e.Kids.Add(c);
                c.Up = e;
            }
        return e;
    }

    static Fin FinAfter(int depth, int fan)
    {
        var e = new Fin(depth);
        if (depth > 0) for (int i = 0; i < fan; i++) { var c = FinAfter(depth - 1, fan); e.Kids.Add(c); c.Up = e; }
        return e;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Trees()
    {
        s_tree = After("after", 4, 4);
        s_used = Used("used", 4, 3);
        s_fin = FinAfter(3, 3);
        Console.WriteLine("after: nodes " + s_tree.Count() + " sound " + s_tree.Sound() + "; used: nodes " + s_used.Count() + " sound " + s_used.Sound());
    }

    // subtrees of such trees replaced while they live: what is replaced must go at once
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Replace()
    {
        for (int round = 0; round < 6; round++)
        {
            var at = s_tree.Kids[round % 4];
            var n = After("after new", 2, 3);
            at.Kids[round % at.Kids.Count] = n;
            n.Up = at;
        }
        for (int round = 0; round < 4; round++)
        {
            var at = s_used.Kids[round % 3];
            var n = Used("used new", 2, 2);
            at.Kids[0] = n;
            n.Up = at;
        }
        var f = FinAfter(2, 2);
        s_fin.Kids[1] = f;
        f.Up = s_fin;
        Console.WriteLine("replaced: nodes " + s_tree.Count() + " sound " + s_tree.Sound() + "; used: nodes " + s_used.Count() + " sound " + s_used.Sound());
    }

    static void Park(Node c) { var t = new Node("park top"); t.Kids.Add(c); s_park = t; }
    static Node TakePark() { var t = s_park; s_park = null; return t; }

    // a value that is the top of the child's own chain, with no reference but the store's
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Tops()
    {
        for (int i = 0; i < 10; i++)
        {
            var c = After("park", 1, 2);
            Park(c);
            c.Up = TakePark();
        }
        // (the same, the top kept)
        for (int i = 0; i < 3; i++)
        {
            var c = After("park kept", 1, 2);
            Park(c);
            s_far = s_park;
            c.Up = TakePark();
        }
    }

    // the child stored into its own field; a child in no list; the parent in a field past the sixteenth word, and by
    // a property; the local declared outside the loop
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Shapes()
    {
        var root = new Node("shape root");
        for (int i = 0; i < 6; i++)
        {
            var c = new Node("shape self", i);
            root.Kids.Add(c);
            c.Up = c;
        }
        for (int i = 0; i < 6; i++)
        {
            var c = new Node("shape alone", i);
            c.Up = root;
        }
        for (int i = 0; i < 6; i++)
        {
            var c = new Node("shape far", i);
            root.Kids.Add(c);
            c.FarUp = root;
        }
        for (int i = 0; i < 6; i++)
        {
            var c = new Node("shape prop", i);
            root.Kids.Add(c);
            c.UpProp = root;
        }
        Node o = null;
        for (int i = 0; i < 6; i++)
        {
            o = new Node("shape outer", i);
            root.Kids.Add(o);
            o.Up = root;
        }
        // (what was read through the local is still used after the store)
        for (int i = 0; i < 6; i++)
        {
            var c = After("shape read", 1, 2);
            var kids = c.Kids;
            root.Kids.Add(c);
            c.Up = root;
            root.Kids.RemoveAt(root.Kids.Count - 1);
            s_sum += kids.Count + kids[0].Id;
        }
        for (int i = 0; i < 6; i++)
        {
            var c = After("shape walked", 1, 3);
            root.Kids.Add(c);
            foreach (var k in c.Kids) { c.Up = root; root.Kids.Remove(c); s_sum += k.Id + 1; }
        }
        s_sum += root.Kids.Count;
        s_moved = root;
    }

    // a child with a parent already, moved by a last store; to the parent of the node it was taken from too
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Moves()
    {
        for (int round = 0; round < 12; round++)
        {
            var from = s_tree.Kids[round % 4];
            var to = round % 3 == 0 ? s_tree : s_tree.Kids[(round + 1) % 4];
            if (from.Kids.Count == 0) continue;
            var c = from.Kids[from.Kids.Count - 1];
            from.Kids.RemoveAt(from.Kids.Count - 1);
            to.Kids.Add(c);
            c.Up = to;
        }
        Console.WriteLine("moved: nodes " + s_tree.Count() + " sound " + s_tree.Sound());
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Go() { s_tree = null; s_used = null; s_fin = null; s_moved = null; s_far = null; }

    static void Fins(string when)
    {
        for (int i = 0; i < 4; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        Console.WriteLine(when + ": fins ran " + Fin.Ran + " saw " + Fin.Seen);
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        Counters();
        Tracked.Report("counters");
        Mixeds();
        Tracked.Report("mixed");
        MixedsGo();
        Tracked.Report("mixed let go");
        Bases();
        Tracked.Report("bases");
        BasesGo();
        Tracked.Report("bases let go");
        Bags();
        Tracked.Report("bags");
        Outside();
        Tracked.Report("outside");
        Trees();
        Tracked.Report("trees");
        Replace();
        Tracked.Report("replaced");
        Fins("replaced");
        Tops();
        Tracked.Report("tops");
        Shapes();
        Tracked.Report("shapes");
        Moves();
        Tracked.Report("moved");
        Go();
        Tracked.Report("all let go");
        Fins("all let go");
        Console.WriteLine("sum " + s_sum);
    }
}
