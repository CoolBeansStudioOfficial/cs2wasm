using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

struct Pt { public int X, Y; public Pt(int x, int y) { X = x; Y = y; } public override string ToString() => X + ":" + Y; }

class Item { public string Name; public int? Count; public Item Next; }

static class Program
{
    static int? Find(int[] a, int v) { for (int i = 0; i < a.Length; i++) if (a[i] == v) return i; return null; }
    static double? Avg(List<int> l) => l.Count == 0 ? null : (double)Sum(l) / l.Count;
    static int Sum(List<int> l) { int s = 0; foreach (var x in l) s += x; return s; }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        int? a = 5, b = null;
        Console.WriteLine(a.HasValue + " " + b.HasValue + " " + a.Value + " " + b.GetValueOrDefault() + " " + b.GetValueOrDefault(9));
        Console.WriteLine(a + 1); Console.WriteLine(a + b); Console.WriteLine((a + b).HasValue);
        Console.WriteLine(a == 5); Console.WriteLine(b == null); Console.WriteLine(a != null); Console.WriteLine(a > 3); Console.WriteLine(b < 3); Console.WriteLine(a == b);
        Console.WriteLine(b ?? -1); Console.WriteLine(a ?? -1);
        int? c = b ?? a;
        Console.WriteLine(c);
        Console.WriteLine(Find(new[] { 4, 5, 6 }, 6)); Console.WriteLine(Find(new[] { 1 }, 2) == null);
        Console.WriteLine(Avg(new List<int> { 1, 2 })); Console.WriteLine(Avg(new List<int>()).HasValue);
        object boxed = a; object boxedNull = b;
        Console.WriteLine(boxed + " " + (boxedNull == null) + " " + (boxed is int));
        int? back = (int?)boxed;
        Console.WriteLine(back);
        int? none = (int?)boxedNull;
        Console.WriteLine(none.HasValue);
        if (a is int v) Console.WriteLine("has " + v);
        if (b is null) Console.WriteLine("b is null");
        if (a is 5) Console.WriteLine("a is 5");
        Console.WriteLine(-a); Console.WriteLine(-b == null);
        long? wide = a;
        Console.WriteLine(wide * 1000000000000);
        Pt? p = new Pt(1, 2);
        Pt? q = null;
        Console.WriteLine(p.Value.X + " " + q.HasValue + " " + p);
        var item = new Item { Name = "a", Count = 3 };
        item.Count++;
        Console.WriteLine(item.Count);
        item.Count = null;
        Console.WriteLine(item.Count ?? 0);
        Item n = null;
        Console.WriteLine(n?.Name ?? "none");
        Console.WriteLine(n?.Count ?? -2);
        Console.WriteLine(item?.Name?.Length);
        var list = new List<int?> { 1, null, 3 };
        int total = 0;
        foreach (var x in list) total += x ?? 100;
        Console.WriteLine(total);
        try { int bad = b.Value; Console.WriteLine(bad); } catch (InvalidOperationException e) { Console.WriteLine(e.Message); }
        object o = 42;
        int? asNullable = o as int?;
        Console.WriteLine(asNullable + " " + ("x" as object as int?).HasValue);
        double? d = 2.5;
        Console.WriteLine($"{d} {d * 2} {(int?)d}");
        bool? flag = true;
        if (flag == true) Console.WriteLine("flag true");
        Console.WriteLine(!flag);
    }
}
