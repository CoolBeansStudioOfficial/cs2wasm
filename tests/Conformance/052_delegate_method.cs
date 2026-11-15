using System;
using System.Reflection;
using System.Runtime.InteropServices;
class Calc { public int Base = 10; public int Add(int x) => Base + x; public static int Twice(int x) => 2 * x; public override string ToString() => "Calc"; public static string Show<T>(T v) => "" + v; }
struct S { public int V; public int Get() => V; }
static class Program {
  [UnmanagedCallersOnly(EntryPoint = "main")]
  static void Main() {
    var c = new Calc();
    Func<int, int> a = c.Add;
    Func<int, int> t = Calc.Twice;
    Func<string> ts = c.ToString;
    Func<int, string> g = Calc.Show<int>;
    Func<int> sg = new S { V = 3 }.Get;
    Console.WriteLine(a.Method + " " + a.Method.DeclaringType + " " + (a.Target == c));
    Console.WriteLine(t.Method + " " + t.Method.IsStatic + " " + (t.Target == null));
    Console.WriteLine(ts.Method.Name + " " + ts.Method.DeclaringType + " " + g.Method + " " + sg.Method);
    Console.WriteLine(a.GetMethodInfo() == typeof(Calc).GetMethod("Add"));
    var m = (Func<int, int>)(a + t);
    Console.WriteLine(m.Method.Name);
    var cd = (Func<int, int>)Delegate.CreateDelegate(typeof(Func<int, int>), c, typeof(Calc).GetMethod("Add")!);
    Console.WriteLine(cd.Method.Name + " " + (cd.Target == c) + " " + cd(1));
    var st = (Func<int, int>)Delegate.CreateDelegate(typeof(Func<int, int>), typeof(Calc).GetMethod("Twice")!);
    Console.WriteLine(st.Method.Name + " " + (st.Target == null) + " " + st(4));
  }
}
