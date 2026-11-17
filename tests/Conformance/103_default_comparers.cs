// EqualityComparer<T>.Default for integer types, written out by the compiler: the same hash codes and results as .NET.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
static class P
{
    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        var c = EqualityComparer<long>.Default; var u = EqualityComparer<uint>.Default; var ul = EqualityComparer<ulong>.Default;
        Console.WriteLine($"{EqualityComparer<long>.Default.GetHashCode(-5)} {EqualityComparer<long>.Default.GetHashCode(1L << 40)} {EqualityComparer<long>.Default.GetHashCode(long.MinValue)} {EqualityComparer<uint>.Default.GetHashCode(uint.MaxValue)} {EqualityComparer<ulong>.Default.GetHashCode(ulong.MaxValue - 7)} {EqualityComparer<int>.Default.GetHashCode(-9)} {EqualityComparer<long>.Default.Equals(3, 3)} {EqualityComparer<char>.Default.Equals('a', 'b')} {c.GetHashCode(-5)} {u.GetHashCode(7)} {ul.GetHashCode(1UL << 63)} {(-5L).GetHashCode()} {EqualityComparer<DayOfWeek>.Default.Equals(DayOfWeek.Friday, DayOfWeek.Friday)} {EqualityComparer<bool>.Default.Equals(true, false)}");
        var d = new Dictionary<long, int>(); for (long i = -50; i < 50; i++) d[i * 4000000007L] = (int)i; int s = 0; foreach (var kv in d) s += kv.Value; Console.WriteLine(d.Count + " " + s + " " + d[4000000007L * 7] + " " + d.ContainsKey(5));
        var h = new HashSet<ulong> { 1, 2, 1UL << 40, 1 }; Console.WriteLine(h.Count + " " + h.Contains(1UL << 40));
    }
}
