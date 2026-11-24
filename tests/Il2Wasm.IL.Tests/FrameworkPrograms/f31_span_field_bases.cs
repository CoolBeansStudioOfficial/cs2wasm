// A span made from an array the method owns, then converted (Span<T> to ReadOnlySpan<T>, inlined: a load of the
// span's _reference field, a byref, and a new ReadOnlySpan over it): the address the field holds is into the array,
// so the array lives as long as the new span is used (Ownership.Derives: a byref field's load). Without that, the array
// was let go at the field's load, and the arrays made after it took its memory: WebUtility.UrlEncodeToBytes read the
// result it was writing instead of its input (the NetWasm case WebUtilityCompatibility, [9]).
using System;
using System.Net;
using System.Runtime.CompilerServices;

static class Program
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    static int Sum(byte[] a, int offset, int count)
    {
        ReadOnlySpan<byte> s = a.AsSpan(offset, count);
        int total = 0;
        for (int round = 0; round < 3; round++)
        {
            // (arrays of the input's size, made while the span is in use: freed memory is taken first)
            var other = new byte[a.Length];
            for (int i = 0; i < other.Length; i++) other[i] = 0xEE;
            for (int i = 0; i < s.Length; i++) total += s[i] + other[i] * 0;
        }
        return total;
    }

    static void Main()
    {
        Console.WriteLine(Sum(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, 2, 5));
        byte[] encoded = WebUtility.UrlEncodeToBytes([0, 32, 255], 1, 2)!;
        Console.WriteLine(BitConverter.ToString(encoded));
        byte[] decoded = WebUtility.UrlDecodeToBytes(encoded, 0, encoded.Length)!;
        Console.WriteLine(BitConverter.ToString(decoded));
    }
}
