// A returned empty span over an array points at the array's end, the next heap block's start: the keeper must
// count the array, not the next object (a planned checked build trapped in rt.retain inside Encoding.get_Preamble).
using System;
using System.Globalization;
using System.Numerics;

internal static class BigIntTrap
{
    private static readonly BigInteger[] Values =
    [
        BigInteger.Zero, BigInteger.One, BigInteger.MinusOne, int.MaxValue, int.MinValue, (BigInteger)int.MinValue - 1,
        uint.MaxValue, (BigInteger)uint.MaxValue + 1, long.MaxValue, long.MinValue, ulong.MaxValue,
        (BigInteger)ulong.MaxValue + 1, -(BigInteger)ulong.MaxValue, BigInteger.Pow(2, 64) - 1, BigInteger.Pow(2, 127),
        -BigInteger.Pow(2, 128), BigInteger.Pow(3, 90), -BigInteger.Pow(3, 90), BigInteger.Pow(7, 61),
        BigInteger.Pow(10, 40) + 7,
        BigInteger.Parse("123456789012345678901234567890123456789012345678901234567890"),
    ];

    private static int Formats()
    {
        var hash = 17;
        string[] formats = { "", "D", "D50", "X", "X40", "x", "N0", "N3", "E", "E10", "G", "G5", "R", "B", "C2", "P1", "F2" };
        foreach (var value in Values)
            foreach (var format in formats)
            {
                var text = value.ToString(format, CultureInfo.InvariantCulture);
                foreach (var c in text) hash = unchecked(hash * 31 + c);
                hash = unchecked(hash * 31 + text.Length);
            }
        return hash;
    }

    private static string Show<T>(T value) => value is IFormattable f ? f.ToString(null, CultureInfo.InvariantCulture) : "null";

    private static int Main()
    {
        try { Console.Write("value " + Show(Formats()) + "\n"); }
        catch (Exception e) { Console.Write("throw " + e.GetType().FullName + "\n"); }
        return 0;
    }
}
