// A span local over an array nothing else names, read in a loop after the array's own last use, while new arrays
// are made (they must not reuse its memory); and a span local assigned in a loop.
using System;

static class Program
{
    static int Sum(ReadOnlySpan<char> s) { int h = s.Length; foreach (var c in s) h = unchecked(h * 31 + c); return h; }
    static void Main()
    {
        int hash = 7;
        for (int n = 1; n < 40; n++)
        {
            ReadOnlySpan<char> source = new string('a', n).ToCharArray();
            foreach (var size in new[] { n, n + 3, n / 2 })
            {
                var destination = new char[size];
                int written = source.ToUpperInvariant(destination);
                hash = unchecked(hash * 31 + written + Sum(destination) + Sum(source));
            }
        }
        Console.WriteLine(hash);
        Span<int> s = default;
        long t = 0;
        for (int i = 0; i < 100; i++)
        {
            t += s.Length > 0 ? s[0] : 0;
            s = new int[] { i, i };
            var junk = new int[2];
            t += s[1] + junk.Length;
        }
        Console.WriteLine(t);
    }
}
