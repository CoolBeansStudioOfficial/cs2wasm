using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.CSharp.RuntimeBinder;
public class Vec { public int X, Y; public Vec(int x, int y) { X = x; Y = y; }
  public static Vec operator +(Vec a, Vec b) => new Vec(a.X + b.X, a.Y + b.Y);
  public static implicit operator string(Vec v) => "(" + v.X + "," + v.Y + ")";
  public override string ToString() => "Vec" + (string)this; }
public class Bag { readonly Dictionary<string, int> _d = new(); public int this[string k] { get => _d.TryGetValue(k, out var v) ? v : -1; set => _d[k] = value; }
  public int Count => _d.Count; public string Describe(int n, string sep = "-") => n + sep + Count; public string Describe(string s) => "s:" + s; }
static class Program {
  static int Twice(int x) => 2 * x;
  static string Twice(string s) => s + s;
  [UnmanagedCallersOnly(EntryPoint = "main")]
  static void Main() {
    dynamic a = new Vec(1, 2), b = new Vec(10, 20);
    dynamic c = a + b;
    Console.WriteLine(c);
    Console.WriteLine(c.X + c.Y);
    string s = c;
    Console.WriteLine(s);
    c.X = 100;
    c.Y += 5;
    Console.WriteLine(c.X + " " + c.Y);
    dynamic bag = new Bag();
    bag["k"] = 3;
    bag["k"] += 4;
    Console.WriteLine(bag["k"] + " " + bag["missing"] + " " + bag.Count);
    Console.WriteLine(bag.Describe(5) + " " + bag.Describe(6, sep: "+") + " " + bag.Describe("x"));
    dynamic n = 7;
    n++;
    n *= 3;
    int i = n;
    Console.WriteLine(i + " " + (n > 20) + " " + (n == 24) + " " + (-n) + " " + (n % 5));
    if (n > 3) Console.WriteLine("big");
    dynamic v = "text";
    Console.WriteLine(v.Length + " " + v.ToUpper() + " " + Twice(v) + " " + Twice(n));
    dynamic d = 1.5;
    Console.WriteLine(d * 2 + " " + (d + n));
    dynamic list = new List<int> { 1, 2 };
    list.Add(3);
    Console.WriteLine(list.Count + " " + list[2]);
    dynamic f = new Func<int, int>(x => x + 1);
    Console.WriteLine(f(41));
    dynamic made = new Vec(n, 1);
    Console.WriteLine(made.X);
    Logic.Run();
    try { dynamic o = new object(); o.Missing(); }
    catch (RuntimeBinderException e) { Console.WriteLine(e.GetType().Name + ": " + e.Message); }
    try { dynamic z = "a"; int bad = z; }
    catch (RuntimeBinderException e) { Console.WriteLine(e.Message); }
  }
}
static class Logic {
  public static void Run() {
    dynamic t = true, f = false, n = 0;
    Console.WriteLine((t && f) + " " + (f || t) + " " + (f && Boom()) + " " + (t || Boom()));
    if (t && !f) Console.WriteLine("and-not");
  }
  static bool Boom() { Console.WriteLine("evaluated"); return true; }
}
