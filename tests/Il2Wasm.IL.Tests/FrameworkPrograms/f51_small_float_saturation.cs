// unchecked float (double and float) to sbyte, byte, short, ushort and char: the value saturates to the target's range
// and NaN becomes 0, rather than being converted to int32 and then truncated (+Infinity is -1 as an sbyte that way).
using System;

static class Program
{
    static readonly double[] Values =
    [
        double.NaN, double.PositiveInfinity, double.NegativeInfinity, double.MaxValue, double.MinValue,
        1e30, -1e30, 4294967296.0, 2147483648.0, -2147483649.0, 300.7, -200.5, 255.9, 127.9, -128.9, -129.0,
        65535.9, 65536.0, -1.0, -0.5, 0.99, 32767.9, -32768.9, double.Epsilon,
    ];

    static void Main()
    {
        for (int i = 0; i < Values.Length; i++)
        {
            double d = Values[i];
            float f = (float)d;
            Console.WriteLine(i + " d: " + unchecked((sbyte)d) + " " + unchecked((byte)d) + " " + unchecked((short)d) + " " + unchecked((ushort)d) + " " + (int)unchecked((char)d));
            Console.WriteLine(i + " f: " + unchecked((sbyte)f) + " " + unchecked((byte)f) + " " + unchecked((short)f) + " " + unchecked((ushort)f) + " " + (int)unchecked((char)f));
        }
    }
}
