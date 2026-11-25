// A span local made by its constructor on the local's address (`var s = new ReadOnlySpan<byte>(a, off, n)` is
// `Call ..ctor(LocA, a, off, n)`): the local keeps the array for all of its reads (Ownership.ByRefLikeLocalWrites,
// in naive counting and the plan). Without it the array was let go after the constructor call, and the arrays made
// while the span was read took its memory (naive and planned, shipped and checked). The array made here and not used
// again by name; one handed over at its last use; BitConverter.ToString over a span of a fresh array.
using System;
using System.Runtime.CompilerServices;

static class Program
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    static int SumLocal(int offset, int count)
    {
        var a = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var s = new ReadOnlySpan<byte>(a, offset, count);
        int total = 0;
        for (int round = 0; round < 3; round++)
        {
            var other = new byte[8];
            for (int i = 0; i < other.Length; i++) other[i] = 0xEE;
            for (int i = 0; i < s.Length; i++) total += s[i] + other[i] * 0;
        }
        return total;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int SumGiven(byte[] a, int offset, int count)
    {
        var s = new ReadOnlySpan<byte>(a, offset, count);
        int total = 0;
        for (int round = 0; round < 3; round++)
        {
            var other = new byte[8];
            for (int i = 0; i < other.Length; i++) other[i] = 0xEE;
            for (int i = 0; i < s.Length; i++) total += s[i] + other[i] * 0;
        }
        return total;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static byte[] Make() => new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };

    static void Main()
    {
        for (int k = 0; k < 3; k++) Console.WriteLine(SumLocal(2, 2) + " " + SumGiven(Make(), 2, 2));
        Console.WriteLine(BitConverter.ToString(new byte[] { 0x20, 0xFF, 0x07 }, 1, 2));
    }
}
