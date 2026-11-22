// A foreach over a struct enumerator whose Current returns a field (ImmutableArray's, a struct enumerator's own
// `_current`, an element of an array a class holds): the loop variable borrows the value when nothing that runs while
// it lives stores what the value came through (Driver.EnumeratorCurrentKeys). Here the sources are overwritten while
// the variable is still in use, by the body and by calls; the value must stay alive. Also returns of the variable
// from inside the loop, casts in the foreach, objects on cycles (arena members), and a nested loop.
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

class Node
{
    public string Name; public Node Parent; public List<Node> Kids = new(); public int[] Data;
    public Node(string n) { Name = n; Data = new int[40]; for (int i = 0; i < 40; i++) Data[i] = n.Length; }
    public int Sum() => Data[0] + Data[39] + Name.Length;
}
sealed class Root : Node { public bool Marked; public Root(string n, bool m) : base(n) { Marked = m; } }

class Holder
{
    public Node[] Items;
    public Holder(int n) { Items = new Node[n]; for (int i = 0; i < n; i++) Items[i] = new Node("h" + new string('x', i + 3)); }
    public Enumerator GetEnumerator() => new Enumerator(this);
    public struct Enumerator
    {
        readonly Holder _h; int _i;
        public Enumerator(Holder h) { _h = h; _i = -1; }
        public bool MoveNext() => ++_i < _h.Items.Length;
        public Node Current => _h.Items[_i];
    }
}

struct Seq
{
    readonly Node[] _a;
    public Seq(Node[] a) { _a = a; }
    public E GetEnumerator() => new E(_a);
    public struct E
    {
        readonly Node[] _a; int _i; Node _current;
        public E(Node[] a) { _a = a; _i = 0; _current = null; }
        public bool MoveNext() { if (_i >= _a.Length) { _current = null; return false; } _current = _a[_i++]; return true; }
        public Node Current => _current;
    }
}

static class P
{
    static string Churn(int k) { var l = new List<Node>(); for (int i = 0; i < 30; i++) l.Add(new Node("churn" + (i + k))); return l[29].Name; }
    static string Mk(char c, int n) => new string(c, n + int.Parse("20"));
    [MethodImpl(MethodImplOptions.NoInlining)] static int Use(Node a, int n) { Churn(n); return a.Sum() + n; }
    static ImmutableArray<Node> s_arr;
    static Holder s_holder;

    static ImmutableArray<Node> Make(char c, int n, bool cycles)
    {
        var b = ImmutableArray.CreateBuilder<Node>();
        for (int i = 0; i < n; i++)
        {
            var r = new Root(Mk(c, i), i % 2 == 0);
            b.Add(r);
            if (cycles) { var k = new Node("kid"); k.Parent = r; r.Kids.Add(k); }
        }
        return b.ToImmutable();
    }

    static void Immutable(bool cycles)
    {
        s_arr = Make('a', 4, cycles);
        int sum = 0;
        // nothing stores: the variable borrows
        foreach (var n in s_arr) sum += n.Sum();
        Console.WriteLine(sum);
        // the array's own elements overwritten by the body, through the array behind the ImmutableArray
        var arr = Make('b', 4, cycles);
        int k = 0;
        foreach (var n in arr) { ImmutableCollectionsMarshal.AsArray(arr)![k++] = null; sum += Use(n, 1); }
        Console.WriteLine(sum);
        // the static that held the ImmutableArray cleared by a call (the enumerator holds the array)
        foreach (var n in s_arr) { Clear(); sum += Use(n, 2); }
        Console.WriteLine(sum + " " + s_arr.IsDefault);
        // a cast in the foreach, and a list filled in the body after the variable's last use
        s_arr = Make('c', 6, cycles);
        var names = new List<string>();
        foreach (Root r in s_arr) if (r.Marked) names.Add(r.Name.Substring(0, 3) + r.Sum());
        Console.WriteLine(string.Join(",", names));
        // the variable returned from inside the loop, and kept after the array is gone
        var found = Find(s_arr, 3);
        s_arr = default; Churn(3);
        Console.WriteLine(found.Sum() + " " + found.Name.Length);
        // nested loops over one array
        var two = Make('d', 3, cycles);
        int pairs = 0;
        foreach (var x in two) foreach (var y in two) pairs += x.Sum() - y.Sum() + 1;
        Console.WriteLine(pairs);
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Clear() { s_arr = default; Churn(4); }
    static Node Find(ImmutableArray<Node> a, int i)
    {
        foreach (var n in a) if (n.Name.Length == 20 + i) return n;
        return null;
    }

    static void ClassHeld()
    {
        s_holder = new Holder(3);
        int sum = 0;
        foreach (var n in s_holder) sum += n.Sum();
        Console.WriteLine(sum);
        // the class's array replaced, and its elements cleared, while the variable is in use
        foreach (var n in s_holder) { var old = s_holder.Items; s_holder.Items = new Node[old.Length]; for (int i = 0; i < old.Length; i++) { s_holder.Items[i] = old[i]; old[i] = null; } sum += Use(n, 5); }
        Console.WriteLine(sum);
        foreach (var n in s_holder) { Array.Clear(s_holder.Items); sum += Use(n, 6); break; }
        Console.WriteLine(sum);
    }

    static void OwnCurrent()
    {
        var a = new Node[3];
        for (int i = 0; i < 3; i++) a[i] = new Node(Mk('e', i));
        int sum = 0;
        // Current is the enumerator's own field: the element cleared from the array, the enumerator still holds it
        int k = 0;
        foreach (var n in new Seq(a)) { a[k++] = null; Churn(7); sum += Use(n, 8); }
        Console.WriteLine(sum + " " + (a[0] == null));
        a[0] = new Node(Mk('f', 1));
        Node keep = null;
        foreach (var n in new Seq(a)) { if (n != null) keep = n; }
        a[0] = null; Churn(9);
        Console.WriteLine(keep.Sum());
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        Immutable(false);
        Immutable(true);
        ClassHeld();
        OwnCurrent();
        Console.WriteLine("done");
    }
}
