// A method that may change its struct runs on a copy when the struct is not a variable: a property value, an
// unboxed value, a readonly field outside its constructor, an `in` parameter.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
struct Pt { public int X, Y; public Pt(int x, int y) { X = x; Y = y; } public void Move(int d) { X += d; } public readonly int Sum() => X + Y; public override string ToString() => $"({X},{Y})"; }
readonly struct RO { public readonly int V; public RO(int v) { V = v; } public int Twice() => V * 2; }
class Holder
{
    public Pt P; public Pt Prop { get; set; } public readonly Pt RoF = new(1, 1); public static readonly Pt SRo = new(2, 2); public RO R = new(4);
    public Holder() { RoF.Move(10); }
    public void InMethod() { RoF.Move(100); }
}
static class Program
{
    static int In(in Pt p) { p.Move(5); return p.X; }
    static Pt Make() => new Pt(9, 9);
    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        var h = new Holder(); h.InMethod();
        h.P.Move(3); h.Prop.Move(5); h.RoF.Move(6); Holder.SRo.Move(7);
        object boxed = h.P; ((Pt)boxed).Move(1);
        var l = new List<Pt> { new Pt(1, 2) }; l[0].Sum();
        var local = new Pt(1, 1); int r = In(local);
        Make().Move(1);
        ref readonly Pt rr = ref h.P; rr.Move(50);
        Pt? n = new Pt(3, 3); n.Value.Move(2);
        (h.P, 1).Item1.Move(8);
        Console.WriteLine($"{h.P} {h.Prop} {h.RoF} {Holder.SRo} {boxed} {l[0]} {local} {r} {n} {h.R.Twice()} {h.RoF.Sum()}");
    }
}
