using System;
using System.Runtime.InteropServices;

public class Counter { public int Count { get; set; } public int[] Items = { 1, 2, 3 }; public int this[int i] { get => Items[i]; set => Items[i] = value; }
  public bool TryGet(int i, out int v) { if (i < Items.Length) { v = Items[i]; return true; } v = -1; return false; }
  public void Bump(ref int x, int by) { x += by; Count += by; }
  public static void Twice(ref string s) { s += s; } }
public class Flag { public bool Value; public static Flag operator &(bool a, Flag b) => new Flag { Value = a & b.Value }; public static Flag operator |(bool a, Flag b) => new Flag { Value = a | b.Value }; public override string ToString() => "Flag(" + Value + ")"; }

static class Program {
  static int calls;
  static dynamic Side(dynamic v) { calls++; return v; }

  [UnmanagedCallersOnly(EntryPoint = "main")]
  static void Main() {
    dynamic c = new Counter();
    var before = c.Count++;
    var after = ++c.Count;
    Console.WriteLine(before + " " + after + " " + c.Count);
    var old = c[1]--;
    Console.WriteLine(old + " " + c[1]);

    int got;
    bool ok = c.TryGet(2, out got);
    Console.WriteLine(ok + " " + got);
    ok = c.TryGet(7, out got);
    Console.WriteLine(ok + " " + got);
    int n = 10;
    c.Bump(ref n, 5);
    dynamic by = 7;
    c.Bump(ref n, by);
    Console.WriteLine(n + " " + c.Count);
    string str = "ab";
    dynamic cs = typeof(Counter);
    Counter.Twice(ref str);
    dynamic dstr = "x";
    Console.WriteLine(str);

    bool t = true, f = false;
    dynamic one = 1, flag = new Flag { Value = true };
    calls = 0;
    var r1 = f && Side(true);
    var r2 = t || Side(false);
    Console.WriteLine(r1 + " " + r2 + " " + calls);
    var r3 = t && Side(false);
    var r4 = f || Side(true);
    Console.WriteLine(r3 + " " + r4 + " " + calls);
    try { var r5 = t && flag; Console.WriteLine(r5); }
    catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException e) { Console.WriteLine(e.Message); }
    var r6 = f && flag;
    Console.WriteLine(r6 + " " + calls);
    Console.WriteLine((t && one == 1) + " " + (f || one > 5));
    try { var bad = t && one; Console.WriteLine(bad); }
    catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException e) { Console.WriteLine(e.Message); }
  }
}
