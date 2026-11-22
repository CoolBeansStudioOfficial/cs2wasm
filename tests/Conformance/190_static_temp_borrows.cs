// Statics held for a call without a count (a receiver, an argument) while the callee or a later argument
// rewrites the static; writes nested in an expression through a reference (--p.Life) against real nested
// writes of the variable; the compaction loop of a list a static holds.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

sealed class Node
{
    public int V; public string Name = "";
    public Node(int v) { V = v; Name = "n" + v; }
    [MethodImpl(MethodImplOptions.NoInlining)]
    public int Replace(int v) { Holder.S = new Node(v); return V + Name.Length; }
    [MethodImpl(MethodImplOptions.NoInlining)]
    public int Clear() { Holder.S = null; return V + Name.Length; }
    [MethodImpl(MethodImplOptions.NoInlining)]
    public int Keep() { return V + Name.Length; }
}
static class Holder
{
    public static Node? S;
    public static List<Node> L = new();
    public static Node[] A = new Node[2];
    [MethodImpl(MethodImplOptions.NoInlining)] public static int Use(Node n, int x) => n.V + x;
    [MethodImpl(MethodImplOptions.NoInlining)] public static int Swap(int v) { S = new Node(v); return v; }
    [MethodImpl(MethodImplOptions.NoInlining)] public static int NewList() { L = new List<Node> { new Node(70) }; return 1; }
}
sealed class Box { public int Life = 3; public string Tag = "t"; }
static class Program
{
    static List<Box> s_boxes = new();
    static int Compact()
    {
        int w = 0, sum = 0;
        for (int i = 0; i < s_boxes.Count; i++)
        {
            var p = s_boxes[i];
            if (--p.Life > 0) { s_boxes[w++] = p; sum += p.Tag.Length; }
        }
        s_boxes.RemoveRange(w, s_boxes.Count - w);
        return sum + s_boxes.Count;
    }
    static int Nested(Box b, Box c)
    {
        // a nested write through a reference, and a real nested write of the variable
        int r = Holder.Use(new Node(40), --b.Life);
        Box d = b;
        r += Take(d, d = c);
        return r + d.Life;
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static int Take(Box x, Box y) => x.Life * 10 + y.Life;

    [System.Runtime.InteropServices.UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        Holder.S = new Node(1);
        Console.WriteLine(Holder.S.Replace(2));          // the receiver's static is rewritten by the callee
        Console.WriteLine(Holder.S!.Clear());            // and cleared
        Holder.S = new Node(3);
        Console.WriteLine(Holder.S.Keep());
        Console.WriteLine(Holder.Use(Holder.S, Holder.Swap(4))); // an argument's static rewritten by a later argument
        Console.WriteLine(Holder.S.V);
        Holder.L.Add(new Node(60));
        Console.WriteLine(Holder.L[0].V + Holder.NewList() + Holder.L.Count);
        Console.WriteLine(Holder.Use(Holder.L[0], Holder.NewList()));
        for (int i = 0; i < 6; i++) s_boxes.Add(new Box { Life = 1 + i % 3 });
        Console.WriteLine(Compact()); Console.WriteLine(Compact()); Console.WriteLine(Compact());
        Console.WriteLine(Nested(new Box(), new Box { Life = 9 }));
        Holder.S = null; Holder.L.Clear();
       
        Elements.Run();
        Console.WriteLine("end");
    }
}

// Borrowed elements (a local from an array's element or a list's indexer) against the stores that run meanwhile:
// the local's own value stored back (kept borrowed), another value stored over its slot (the object must stay
// alive for the local), a setter that stores more than its value, a list replaced or cleared while the local is used.
sealed class Cell { public int V; public string S; public Cell(int v) { V = v; S = "c" + v; } }
sealed class Bag
{
    public Cell[] Items = new Cell[4];
    public Cell? Last;
    public Cell this[int i] { get => Items[i]; set { Last = Items[i]; Items[i] = value; } }
}
sealed class Link { public Link? Next; public List<Link> Kids = new(); public int V; }
static class Elements
{
    static Cell[] s_cells = { new Cell(1), new Cell(2), new Cell(3) };
    static List<Cell> s_list = new() { new Cell(10), new Cell(20), new Cell(30) };
    static Bag s_bag = new();
    static List<Link> s_links = new();
    [MethodImpl(MethodImplOptions.NoInlining)] static int SelfArray(int i, int w) { var p = s_cells[i]; p.V++; s_cells[w] = p; return p.V + p.S.Length; }
    [MethodImpl(MethodImplOptions.NoInlining)] static int OtherArray(int i) { var p = s_cells[i]; s_cells[i] = new Cell(100 + i); return p.V + p.S.Length; }
    [MethodImpl(MethodImplOptions.NoInlining)] static int SelfThenOther(int i, int w) { var p = s_cells[i]; s_cells[w] = p; s_cells[i] = new Cell(200); s_cells[w] = new Cell(201); return p.V + p.S.Length; }
    [MethodImpl(MethodImplOptions.NoInlining)] static int SelfList(int i, int w) { var p = s_list[i]; p.V++; s_list[w] = p; return p.V + p.S.Length; }
    [MethodImpl(MethodImplOptions.NoInlining)] static int OtherList(int i) { var p = s_list[i]; s_list[i] = new Cell(300 + i); return p.V + p.S.Length; }
    [MethodImpl(MethodImplOptions.NoInlining)] static int TwoLocals(int i, int j) { var p = s_list[i]; var q = s_list[j]; s_list[i] = q; s_list[j] = q; return p.V + p.S.Length + q.V; }
    [MethodImpl(MethodImplOptions.NoInlining)] static int ClearedList(int i) { var p = s_list[i]; s_list.Clear(); return p.V + p.S.Length; }
    [MethodImpl(MethodImplOptions.NoInlining)] static int ReplacedList(int i) { var p = s_list[i]; s_list = new List<Cell> { new Cell(40), new Cell(50) }; return p.V + p.S.Length; }
    [MethodImpl(MethodImplOptions.NoInlining)] static int RemovedAt(int i) { var p = s_list[i]; s_list.RemoveAt(i); return p.V + p.S.Length; }
    [MethodImpl(MethodImplOptions.NoInlining)] static int BagSetter(int i) { s_bag[i] = new Cell(60 + i); var p = s_bag.Items[i]; s_bag[i] = p; s_bag[i] = new Cell(70 + i); s_bag.Last = null; return p.V + p.S.Length; }
    [MethodImpl(MethodImplOptions.NoInlining)] static int CyclicSelf(int i, int w) { var p = s_links[i]; p.V++; s_links[w] = p; return p.V + s_links.Count; }
    [MethodImpl(MethodImplOptions.NoInlining)] static int CyclicOther(int i) { var p = s_links[i]; s_links[i] = new Link { V = 90 }; return p.V + p.Kids.Count; }
    static int Compact(List<Cell> list)
    {
        int w = 0, sum = 0;
        for (int i = 0; i < list.Count; i++) { var p = list[i]; if ((p.V & 1) == 0) { list[w++] = p; sum += p.S.Length; } }
        list.RemoveRange(w, list.Count - w);
        return sum * 100 + list.Count;
    }
    public static void Run()
    {
        Console.WriteLine(SelfArray(0, 0) + " " + SelfArray(1, 0) + " " + SelfArray(2, 1));
        Console.WriteLine(OtherArray(0) + " " + OtherArray(1) + " " + s_cells[0].V + " " + s_cells[1].V);
        Console.WriteLine(SelfThenOther(2, 0) + " " + s_cells[0].V + " " + s_cells[2].V);
        Console.WriteLine(SelfList(0, 0) + " " + SelfList(1, 0) + " " + SelfList(2, 1));
        Console.WriteLine(OtherList(0) + " " + OtherList(2) + " " + s_list[0].V + " " + s_list[2].V);
        Console.WriteLine(TwoLocals(0, 1) + " " + s_list[0].V + " " + s_list[1].V);
        Console.WriteLine(RemovedAt(0) + " " + s_list.Count);
        Console.WriteLine(ReplacedList(0) + " " + s_list.Count);
        Console.WriteLine(ClearedList(1) + " " + s_list.Count);
        Console.WriteLine(BagSetter(0) + " " + BagSetter(3) + " " + s_bag.Items[0].V);
        var a = new Link { V = 1 }; var b = new Link { V = 2, Next = a }; a.Next = b; a.Kids.Add(b); b.Kids.Add(a);
        s_links.Add(a); s_links.Add(b); s_links.Add(new Link { V = 3 });
        Console.WriteLine(CyclicSelf(0, 2) + " " + CyclicSelf(1, 0) + " " + CyclicOther(0) + " " + CyclicOther(1) + " " + s_links[2].V);
        s_links.Clear();
        var l = new List<Cell>(); for (int i = 0; i < 9; i++) l.Add(new Cell(i));
        Console.WriteLine(Compact(l) + " " + Compact(l) + " " + l[0].V);
    }
}
