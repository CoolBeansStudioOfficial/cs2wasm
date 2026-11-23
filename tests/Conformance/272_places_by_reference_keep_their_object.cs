// A place in an object passed by reference (`ref`, `out`, `in`, the struct a method or property is called on) keeps
// that object while the callee runs, as under .NET: the callee lets go of the array or the object the place is in
// and goes on reading and writing the place. Each callee allocates and frees in between, so memory let go too early
// is used again: until 2026-11-23 the first of these read freed memory and trapped.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

sealed class Node { public Node? Next; public int Id; public Node(int id) { Id = id; } }
sealed class Inner { public int Count = 5; public long Pad; }
struct P
{
    public int X, Y;
    [MethodImpl(MethodImplOptions.NoInlining)] public int Get(Holder h) { h.Pts = Program.Points(3, 100); Program.Churn(); return X; }
    public int Prop { [MethodImpl(MethodImplOptions.NoInlining)] get { Program.s_h.Pts = Program.Points(3, 200); Program.Churn(); return Y; } }
}
sealed class Holder { public Node[] Arr = Program.Make(3); public P[] Pts = Program.Points(3, 70); public Inner Inner = new Inner(); public P[,] Grid = new P[2, 2]; }

static class Program
{
    public static Holder s_h = new Holder();
    [MethodImpl(MethodImplOptions.NoInlining)] public static Node[] Make(int n) { var a = new Node[n]; for (int i = 0; i < n; i++) a[i] = new Node(i + 1); return a; }
    [MethodImpl(MethodImplOptions.NoInlining)] public static P[] Points(int n, int x) { var a = new P[n]; for (int i = 0; i < n; i++) { a[i].X = x + i; a[i].Y = -x - i; } return a; }
    [MethodImpl(MethodImplOptions.NoInlining)] public static void Churn() { for (int i = 0; i < 50; i++) { var t = Make(3); var p = Points(3, 900 + i); var q = new Inner { Count = 1000 + i }; var g = new P[2, 2]; g[1, 1].X = 4000 + i; var s = new string('x', 4 + i % 5); } }

    [MethodImpl(MethodImplOptions.NoInlining)] static int Read(ref Node slot, Holder h) { h.Arr = Make(3); Churn(); return slot.Id; }
    [MethodImpl(MethodImplOptions.NoInlining)] static int ReadInt(ref int x, Holder h) { h.Pts = Points(3, 300); h.Grid = new P[2, 2]; Churn(); return x; }
    [MethodImpl(MethodImplOptions.NoInlining)] static int ReadIn(in P p, Holder h) { h.Pts = Points(3, 400); Churn(); return p.X; }
    [MethodImpl(MethodImplOptions.NoInlining)] static int ReadCount(ref int x, Holder h) { h.Inner = new Inner { Count = 6 }; Churn(); return x; }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Out(out int x, Holder h) { h.Pts = Points(3, 500); Churn(); x = 1; }

    [UnmanagedCallersOnly(EntryPoint = "main")] static void Main()
    {
        for (int round = 0; round < 3; round++)
        {
            var h = new Holder();
            Console.WriteLine("ref to an element of references: " + Read(ref h.Arr[1], h));
            h = new Holder();
            Console.WriteLine("struct element's method: " + h.Pts[1].Get(h));
            h = new Holder();
            Console.WriteLine("ref to a struct element's field: " + ReadInt(ref h.Pts[2].X, h));
            h = new Holder();
            Console.WriteLine("in of a struct element: " + ReadIn(in h.Pts[0], h));
            h = new Holder();
            Console.WriteLine("ref to an object's field: " + ReadCount(ref h.Inner.Count, h));
            h = new Holder(); h.Grid[1, 1].X = 42;
            Console.WriteLine("ref into a grid: " + ReadInt(ref h.Grid[1, 1].X, h));
            s_h = new Holder();
            Console.WriteLine("struct element's property: " + s_h.Pts[1].Prop);
            h = new Holder();
            Out(out h.Pts[1].X, h);
            Console.WriteLine("out: " + h.Pts[1].X);
        }
    }
}
