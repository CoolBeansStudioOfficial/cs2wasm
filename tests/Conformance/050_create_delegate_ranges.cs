using System;
using System.Reflection;
using System.Runtime.InteropServices;
class Calc { public int Base = 10; public int Add(int x) => Base + x; public static int Twice(int x) => 2 * x; public static string Join(string a, int b) => a + b; }
static class Program {
  [UnmanagedCallersOnly(EntryPoint = "main")]
  static void Main() {
    var c = new Calc();
    var add = (Func<int, int>)Delegate.CreateDelegate(typeof(Func<int, int>), c, typeof(Calc).GetMethod("Add")!);
    Console.WriteLine(add(5));
    var tw = typeof(Calc).GetMethod("Twice")!.CreateDelegate<Func<int, int>>();
    Console.WriteLine(tw(21));
    var closed = (Func<int, string>)Delegate.CreateDelegate(typeof(Func<int, string>), "n=", typeof(Calc).GetMethod("Join")!);
    Console.WriteLine(closed(7));
    var open = (Func<Calc, int, int>)Delegate.CreateDelegate(typeof(Func<Calc, int, int>), null, typeof(Calc).GetMethod("Add")!);
    Console.WriteLine(open(new Calc { Base = 100 }, 1));
    Console.WriteLine(Delegate.CreateDelegate(typeof(Action), typeof(Calc).GetMethod("Twice")!, false) == null);
    var byName = (Func<int, int>)Delegate.CreateDelegate(typeof(Func<int, int>), new Calc { Base = 1 }, "Add");
    Console.WriteLine(byName(1));
    var staticByName = (Func<int, int>)Delegate.CreateDelegate(typeof(Func<int, int>), typeof(Calc), "twice", true);
    Console.WriteLine(staticByName(8));
    Console.WriteLine(Delegate.CreateDelegate(typeof(Func<int, int>), typeof(Calc), "Missing", false, false) == null);
    int[] a = { 1, 2, 3, 4, 5 };
    Console.WriteLine(string.Join(",", a[1..]) + " " + string.Join(",", a[..2]) + " " + string.Join(",", a[^2..]) + " " + a[^1] + " " + a[1..^1].Length);
    string[] s = { "x", "y", "z" };
    Console.WriteLine(string.Join("", s[1..]));
  }
}
