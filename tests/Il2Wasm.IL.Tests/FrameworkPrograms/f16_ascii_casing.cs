// System.Text.Ascii's case changes over lengths around the vector widths, with a non-ASCII char in the middle.
using System;
using System.Text;
static class Program
{
    static void Main()
    {
        foreach (var length in new[] { 0, 1, 3, 7, 8, 9, 15, 16, 17, 31, 33, 64, 65 })
        foreach (var position in new[] { -1, length / 2 })
        {
            var chars = new char[length];
            for (int i = 0; i < length; i++) chars[i] = (char)('a' + (i * 11) % 26);
            if (position >= 0 && length > 0) chars[position] = 'é';
            var upper = new char[length]; var lower = new byte[length];
            var r1 = Ascii.ToUpper(chars, upper, out var w1);
            var r2 = Ascii.ToLower(chars, lower, out var w2);
            var ip = (char[])chars.Clone();
            var r3 = Ascii.ToUpperInPlace(ip, out var w3);
            var sh = new char[length / 2];
            var r4 = Ascii.ToLower(chars, sh, out var w4);
            Console.WriteLine($"{length} {position}: {r1} {w1} {Convert.ToHexString(System.Runtime.InteropServices.MemoryMarshal.AsBytes(upper.AsSpan()))} | {r2} {w2} {Convert.ToHexString(lower)} | {r3} {w3} {Convert.ToHexString(System.Runtime.InteropServices.MemoryMarshal.AsBytes(ip.AsSpan()))} | {r4} {w4}");
        }
    }
}
