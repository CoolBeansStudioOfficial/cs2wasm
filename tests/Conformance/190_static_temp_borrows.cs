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
       
        Console.WriteLine("end");
    }
}
