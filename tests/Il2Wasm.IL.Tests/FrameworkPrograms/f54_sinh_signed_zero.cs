// Math.Sinh and MathF.Sinh keep the sign of a zero (sinh(-0.0) is -0.0, as the C library and IEEE give), and of the
// small arguments that round to it.
using System;

static class Program
{
    static void Main()
    {
        Console.WriteLine("double: " + BitConverter.DoubleToInt64Bits(Math.Sinh(-0.0)) + " " + BitConverter.DoubleToInt64Bits(Math.Sinh(0.0)));
        Console.WriteLine("single: " + BitConverter.SingleToInt32Bits(MathF.Sinh(-0.0f)) + " " + BitConverter.SingleToInt32Bits(MathF.Sinh(0.0f)));
        Console.WriteLine("tiny: " + BitConverter.DoubleToInt64Bits(Math.Sinh(-1e-300)) + " " + Math.Sinh(-0.5) + " " + Math.Sinh(0.5));
        Console.WriteLine("infinite: " + Math.Sinh(double.NegativeInfinity) + " " + MathF.Sinh(float.PositiveInfinity));
    }
}
