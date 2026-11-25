// (float) of an unsigned 64-bit value is one rounding to single, not a rounding to double and then one to single: the
// values here sit just past a single's midpoint, which a double (with its low bit dropped) makes a tie that rounds down.
using System;

static class Program
{
    static readonly ulong[] Sources =
    [
        9223372549610589697UL, // 2^63 + 2^39 + 1: just above the midpoint, so up
        9223372036854775808UL + 549755813888UL, // 2^63 + 2^39: an exact tie, to even (down)
        9223372036854775808UL + 549755813888UL + 2048UL, // above the midpoint, by more than the double keeps
        16777217UL, 16777219UL, 9007199254740993UL, 18446744073709551615UL, 0UL,
    ];

    static void Main()
    {
        for (int i = 0; i < Sources.Length; i++)
        {
            ulong source = Sources[i];
            float single = (float)source;
            double twice = (double)source;
            Console.WriteLine(i + ": " + BitConverter.SingleToInt32Bits(single) + " " + BitConverter.DoubleToInt64Bits(twice) + " " + BitConverter.SingleToInt32Bits((float)twice));
        }
    }
}
