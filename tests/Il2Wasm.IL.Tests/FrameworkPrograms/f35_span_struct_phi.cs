// A span made by a call (the array-to-span conversion) and stored inside a byref-like struct, chosen by a phi:
// HashSet<int>.IntersectWith's `c ? new BitHelper(stackSpan, true) : new BitHelper(new int[n], false)`. The phi of
// byref-like values keeps its inputs' bases (Ownership.FindBases), so the array lives as long as the BitHelper local
// is read. Without it naive counting let the array go after the conversion (the NetWasm case HashSetCompatibility
// [12]: 25500001 where .NET prints 17000001). The same shape in the program's own code, a ref struct over a span.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

ref struct Bits
{
    readonly Span<int> _span;
    public Bits(Span<int> span, bool clear) { _span = span; if (clear) span.Clear(); }
    public void Mark(int i) => _span[i >> 5] |= 1 << (i & 31);
    public bool Has(int i) => (_span[i >> 5] & (1 << (i & 31))) != 0;
}

static class Program
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    static int Count(int n, bool small)
    {
        Span<int> stack = stackalloc int[4];
        Bits bits = small ? new Bits(stack, true) : new Bits(new int[(n >> 5) + 1], false);
        for (int round = 0; round < 3; round++)
        {
            var other = new int[(n >> 5) + 1];
            for (int i = 0; i < other.Length; i++) other[i] = -1;
            for (int i = 0; i < n; i += 3) bits.Mark(i);
        }
        int c = 0;
        for (int i = 0; i < n; i++) if (bits.Has(i)) c++;
        return c;
    }

    static void Main()
    {
        var values = new HashSet<int>();
        var other = new List<int>();
        for (int value = 0; value < 3400; value++)
        {
            values.Add(value);
            if ((value & 1) == 0) other.Add(value);
        }
        values.IntersectWith(other);
        Console.WriteLine(values.Count * 10000 + (values.Contains(3398) ? 1 : 0));
        Console.WriteLine(Count(100, true) + " " + Count(1000, false));
    }
}
