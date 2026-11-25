// Math.FusedMultiplyAdd and MathF.FusedMultiplyAdd round once. The single version's product is exact in double, but
// rounding the double sum to single rounds twice when the sum lies just beside a single midpoint; a tiny addend next to a
// midpoint decides it. The double version rounds once too, at a cancelling addend and a midpoint's neighbour.
using System;

static class Program
{
    static void Main()
    {
        // (1 + k 2^-12)^2 for odd k lies on a single midpoint; a tiny addend moves the exact result off it
        float[] squares = { 1 + 3 / 4096f, 1 + 101 / 4096f, 1 + 2001 / 4096f };
        foreach (var sq in squares)
        {
            float nudge = 1.0f / (1L << 60);
            Console.WriteLine(BitConverter.SingleToInt32Bits(MathF.FusedMultiplyAdd(sq, sq, nudge)) + " " +
                BitConverter.SingleToInt32Bits(MathF.FusedMultiplyAdd(sq, sq, -nudge)));
        }

        double a = 1.0 + 1.0 / (1L << 30), b = 1.0 - 1.0 / (1L << 30);
        Console.WriteLine(BitConverter.DoubleToInt64Bits(Math.FusedMultiplyAdd(a, b, -1.0)));
        Console.WriteLine(BitConverter.DoubleToInt64Bits(Math.FusedMultiplyAdd(3.0000000000000004, 1.0 / 3, -1.0)));
        Console.WriteLine(BitConverter.DoubleToInt64Bits(Math.FusedMultiplyAdd(1e300, 1e-300, 1e-17)));
        Console.WriteLine(BitConverter.DoubleToInt64Bits(Math.FusedMultiplyAdd(-2.5, 4.0, 10.0)));
        Console.WriteLine(BitConverter.SingleToInt32Bits(MathF.FusedMultiplyAdd(-2.5f, 4f, 10f)));
        Console.WriteLine(BitConverter.SingleToInt32Bits(MathF.FusedMultiplyAdd(0f, -1f, 0f)));
    }
}
