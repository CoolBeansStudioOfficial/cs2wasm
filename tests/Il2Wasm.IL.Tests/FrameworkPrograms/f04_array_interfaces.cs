// Arrays through the generic collection interfaces: IList, ICollection, IEnumerable, IReadOnlyList and
// IReadOnlyCollection, of values and of references (covariant), and the casts to them.
using System;
using System.Collections;
using System.Collections.Generic;

static class Program
{
    static int Sum(IReadOnlyList<int> l) { int s = 0; for (int i = 0; i < l.Count; i++) s += l[i]; return s; }
    static int Count<T>(IReadOnlyCollection<T> c) => c.Count;
    static string Join(IEnumerable<object> e) => string.Join("+", e);

    static void Main()
    {
        int[] a = [3, 1, 2];
        Console.WriteLine(Sum(a) + " " + Count(a) + " " + Count(new string[4]));
        IList<int> il = a;
        il[1] = 10;
        Console.WriteLine(il.Count + " " + il.IndexOf(10) + " " + il.Contains(2) + " " + il.IsReadOnly + " " + a[1]);
        ICollection<int> ic = a;
        var copy = new int[5];
        ic.CopyTo(copy, 1);
        Console.WriteLine(string.Join(",", copy));
        string[] s = ["x", "y"];
        Console.WriteLine(Join(s) + " " + ((IReadOnlyList<object>)s)[1] + " " + ((IReadOnlyCollection<object>)s).Count);
        object o = a;
        Console.WriteLine((o is IReadOnlyList<int>) + " " + (o is IReadOnlyCollection<int>) + " " + (o is IList<long>) + " " + (o is ICollection) + " " + ((IList)a).Count);
        try { il.Add(4); } catch (NotSupportedException e) { Console.WriteLine("NotSupported " + e.Message); }
        IEnumerable<int> en = a;
        foreach (var v in en) Console.Write(v + ";");
        Console.WriteLine();
    }
}
