using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

class NoDefault { public NoDefault(int x) { } }

static class Program
{
    static IEnumerable<int> Gen() { yield return 7; yield return 8; }
    static T Make<T>() => Activator.CreateInstance<T>();

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        int[] a = [1, 2, 3];
        List<int> l = [0, .. a, 4, .. Gen()];
        int[] b = [.. l, .. a];
        ReadOnlySpan<char> chars = "hello";
        HashSet<char> unique = [.. chars];
        IEnumerable<string> words = ["x", .. new[] { "y", "z" }];
        IReadOnlyList<int> rl = [.. a.Select(x => x * 10)];
        Span<int> sp = [.. a, 9];
        ICollection<int> col = [.. b];
        Console.WriteLine(string.Join(",", l) + " | " + string.Join(",", b) + " | " + unique.Count + " | " + string.Join("", words) + " | " + string.Join(",", rl) + " | " + sp.Length + ":" + sp[3] + " | " + col.Count);
        try { Make<NoDefault>(); } catch (MissingMethodException e) { Console.WriteLine(e.GetType().Name + ": " + e.Message); }
    }
}
