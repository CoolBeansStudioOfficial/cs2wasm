using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public class Vec { public double X, Y; public Vec(double x, double y) { X = x; Y = y; } public double Len() => Math.Sqrt(X * X + Y * Y); public Vec Scale(double k) { X *= k; Y *= k; return this; } }
public class Named { public string Name; public List<int> Items = new(); public Named(string n) { Name = n; } public virtual string Show() => Name + ":" + string.Join(",", Items); }
public class Tagged : Named { public string Tag = "t"; public Tagged(string n) : base(n) { } public override string Show() => Tag + "/" + base.Show(); }
public class Holder { public static Named? Last; public Named? Kept; }
public class Acc { int total; public void Add(int v) { total += v; } public int Total => total; public void Keep(Holder h) { h.Kept = null; } }
public class Thrower { public int N; public Thrower(int n) { if (n < 0) throw new ArgumentException("negative"); N = n; } }

static class Program {
  static int Sum(Acc a) => a.Total;
  static Named Pass(Named n) => n;

  [UnmanagedCallersOnly(EntryPoint = "main")]
  static void Main() {
    double total = 0;
    for (int i = 0; i < 100000; i++)
    {
      var v = new Vec(i, i + 1);
      v.Scale(0.5);
      total += v.Len();
    }
    Console.WriteLine(total.ToString("F3"));

    var sb = new StringBuilder();
    for (int i = 0; i < 5; i++) sb.Append(i).Append(';');
    Console.WriteLine(sb.ToString());

    for (int i = 0; i < 3; i++)
    {
      var n = new Tagged("n" + i) { Tag = "tag" + i };
      n.Items.Add(i); n.Items.Add(i * i);
      Console.WriteLine(n.Show() + " " + n.GetHashCode().GetType().Name);
    }

    var acc = new Acc();
    for (int i = 1; i <= 10; i++) acc.Add(i);
    Console.WriteLine(Sum(acc));

    // escaping objects stay on the heap
    var kept = new Named("kept");
    Holder.Last = kept;
    var passed = Pass(new Named("passed"));
    var h = new Holder { Kept = new Named("held") };
    Console.WriteLine(Holder.Last.Show() + " " + passed.Show() + " " + h.Kept!.Show());

    for (int i = -1; i < 2; i++)
    {
      try
      {
        var t = new Thrower(i);
        var w = new Vec(t.N, 2);
        if (i == 1) throw new InvalidOperationException("after " + w.Len());
        Console.WriteLine("made " + t.N);
      }
      catch (Exception e) { Console.WriteLine(e.GetType().Name + " " + e.Message); }
    }

    var list = new List<string>();
    for (int i = 0; i < 4; i++) { var b = new StringBuilder("x"); b.Append(i); list.Add(b.ToString()); }
    Console.WriteLine(string.Join(" ", list));
    var a1 = new Vec(1, 2); var a2 = new Vec(1, 2);
    Console.WriteLine((a1 == a2) + " " + ReferenceEquals(a1, a1) + " " + (a1 is Vec) + " " + a1.Equals(a2));
  }
}
