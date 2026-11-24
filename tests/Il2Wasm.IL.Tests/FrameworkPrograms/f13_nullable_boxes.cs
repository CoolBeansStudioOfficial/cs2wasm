// Nullable<T> boxes as its value or as null; unboxes from a T's box or from null; casts to T? are casts to T.
using System;
using System.Collections.Generic;

struct R { public string S; public int N; public override string ToString() => S + N; }

static class Program
{
    static object Box<T>(T? v) where T : struct => v;
    static T? Unbox<T>(object o) where T : struct => (T?)o;
    static void Main()
    {
        int? a = 5, none = null;
        object oa = a, on = none;
        Console.WriteLine((oa == null) + " " + (on == null) + " " + oa.GetType().Name + " " + (oa is int) + " " + (oa is int?) + " " + (oa is long?));
        Console.WriteLine(Unbox<int>(oa) + " " + Unbox<int>(null).HasValue + " " + ((int?)(object)7).Value);
        try { var bad = (int?)(object)"x"; Console.WriteLine(bad); } catch (InvalidCastException) { Console.WriteLine("cast"); }
        var list = new List<object>();
        for (int i = 0; i < 500; i++) { R? r = i % 2 == 0 ? new R { S = "r" + i, N = i } : null; list.Add(Box(r)); }
        int nulls = 0; long sum = 0;
        foreach (var o in list) { if (o == null) nulls++; else { var r = Unbox<R>(o)!.Value; sum += r.N + r.S.Length; } }
        Console.WriteLine(nulls + " " + sum + " " + list[2]);
        R? q = new R { S = "q", N = 1 };
        Console.WriteLine(q.ToString() + " " + q.GetHashCode().GetType().Name + " " + none.ToString().Length + " " + q.Equals(new R { S = "q", N = 1 }));
    }
}
