using System;
using System.Reflection;
using System.Runtime.InteropServices;
class P { public string Tag = "";
  public P() { } public P(out int made) { made = 7; Tag = "made"; }
  public static bool TryHalf(int x, out int half) { half = x / 2; return x % 2 == 0; }
  public static void Swap(ref string a, ref string b) { (a, b) = (b, a); }
  public void Bump(ref int n, in int by) { n += by; }
  public static void Grow(ref Pt p) { p.X += 1; p.Y += 2; }
}
struct Pt { public int X, Y; public override string ToString() => X + "," + Y; }
static class Program {
  [UnmanagedCallersOnly(EntryPoint = "main")]
  static void Main() {
    var th = typeof(P).GetMethod("TryHalf")!;
    var args = new object?[] { 10, null };
    Console.WriteLine(th.Invoke(null, args) + " " + args[1]);
    args = new object?[] { 7, 0 };
    Console.WriteLine(th.Invoke(null, args) + " " + args[1]);
    var ps = th.GetParameters();
    Console.WriteLine(ps[1].ParameterType + " " + ps[1].ParameterType.Name + " " + ps[1].IsOut + " " + ps[1].ParameterType.IsByRef + " " + ps[1].ParameterType.GetElementType() + " " + ps[0].IsOut);
    Console.WriteLine(th);
    Console.WriteLine(ps[1].ParameterType == typeof(int).MakeByRefType());
    var sw = new object?[] { "x", "y" };
    typeof(P).GetMethod("Swap")!.Invoke(null, sw);
    Console.WriteLine(sw[0] + " " + sw[1]);
    var bump = new object?[] { 5, 3 };
    typeof(P).GetMethod("Bump")!.Invoke(new P(), bump);
    Console.WriteLine(bump[0] + " " + bump[1] + " " + typeof(P).GetMethod("Bump")!.GetParameters()[1].IsIn);
    var g = new object?[] { new Pt { X = 1, Y = 1 } };
    typeof(P).GetMethod("Grow")!.Invoke(null, g);
    Console.WriteLine(g[0]);
    var ca = new object?[] { 0 };
    var made = (P)typeof(P).GetConstructor(new[] { typeof(int).MakeByRefType() })!.Invoke(ca);
    Console.WriteLine(made.Tag + " " + ca[0]);
  }
}
