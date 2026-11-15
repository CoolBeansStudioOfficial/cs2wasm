// Arrays through the non-generic collection interfaces: System.Array's implementations (IList.Add throws
// NotSupportedException, the enumerator is ArrayEnumerator, which foreach does not dispose: it is no IDisposable).
using System;
using System.Collections;
using System.Linq;
using System.Runtime.InteropServices;

static class Program
{
    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        object[] a = [1, "x", 2];
        IEnumerable e = a;
        Console.WriteLine(e.OfType<int>().FirstOrDefault());
        foreach (var o in e) Console.Write(o + " ");
        Console.WriteLine();
        var en = e.GetEnumerator();
        Console.WriteLine(en.GetType().FullName + " " + (en is IDisposable) + " " + (en is ICloneable));
        IList l = a;
        try { l.Add(3); } catch (NotSupportedException ex) { Console.WriteLine("NotSupported: " + ex.Message); }
        try { l.Remove(1); } catch (NotSupportedException ex) { Console.WriteLine("NotSupported: " + ex.Message); }
        Console.WriteLine(((ICollection)a).Count + " " + l.Contains("x") + " " + l.IndexOf(2) + " " + l.IsFixedSize + " " + l.IsReadOnly);
        l[1] = "y";
        Console.WriteLine(string.Join(",", a));
        int[] ints = [3, 1, 2];
        var c = (ICollection)ints;
        var copy = new int[3];
        c.CopyTo(copy, 0);
        Console.WriteLine(string.Join(",", copy) + " " + ((IList)ints).Contains(2) + " " + ((IStructuralEquatable)ints).Equals(new[] { 3, 1, 2 }, StructuralComparisons.StructuralEqualityComparer));
    }
}
