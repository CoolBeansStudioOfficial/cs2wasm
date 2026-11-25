// Bit-exact double and single math against .NET: FusedMultiplyAdd at its overflow, underflow and rounding edges (and a
// random sweep over the whole exponent range), Sin, Cos and Tan at large arguments (Payne-Hanek reduction), RootN, and
// generic math edge operations. Every result is printed as its bit pattern (NaN as "NaN": only its NaN-ness is
// compared, since NaN payloads and signs are canonical).
using System;
using System.Numerics;

static class Program
{
    static void Bits(string label, double v) => Console.WriteLine(label + " " + (double.IsNaN(v) ? "NaN" : BitConverter.DoubleToInt64Bits(v).ToString("x16")));
    static void Bits(string label, float v) => Console.WriteLine(label + " " + (float.IsNaN(v) ? "NaN" : BitConverter.SingleToInt32Bits(v).ToString("x8")));

    static double Pow2(int e) => BitConverter.Int64BitsToDouble((long)(e + 1023) << 52);

    static ulong s_state = 0x2545F4914F6CDD1DUL;
    static ulong Next() { s_state ^= s_state << 13; s_state ^= s_state >> 7; s_state ^= s_state << 17; return s_state; }

    // a finite double whose exponent field is drawn from [lo, hi] (0 is subnormal), with a random sign and mantissa
    static double Pick(int lo, int hi)
    {
        ulong field = (ulong)(lo + (int)(Next() % (ulong)(hi - lo + 1)));
        ulong frac = Next() & 0xFFFFFFFFFFFFFUL;
        if (Next() % 16 == 0) frac = 0;
        return BitConverter.Int64BitsToDouble((long)((Next() & 0x8000000000000000UL) | (field << 52) | frac));
    }

    static void Fma()
    {
        // the overflow, underflow and cancellation edges
        double max = double.MaxValue, min = double.Epsilon;
        Bits("fma max*2-max", Math.FusedMultiplyAdd(max, 2, -max));
        Bits("fma max*1-max", Math.FusedMultiplyAdd(max, 1, -max));
        Bits("fma max*2+max", Math.FusedMultiplyAdd(max, 2, max));
        Bits("fma -max*2+max", Math.FusedMultiplyAdd(-max, 2, max));
        Bits("fma minvalue*2+max", Math.FusedMultiplyAdd(double.MinValue, 2, max));
        Bits("fma 1*minvalue+1", Math.FusedMultiplyAdd(1, double.MinValue, 1));
        Bits("fma 2^-600*2^-500-2^-1000", Math.FusedMultiplyAdd(Pow2(-600), Pow2(-500), -Pow2(-1000)));
        Bits("fma 134217729^2-2^54", Math.FusedMultiplyAdd(134217729, 134217729, -18014398777917440));
        Bits("fma eps*0.5+eps", Math.FusedMultiplyAdd(min, 0.5, min));
        Bits("fma eps*-0.5+eps", Math.FusedMultiplyAdd(min, -0.5, min));
        Bits("fma 2^-1022*2^-1022+0", Math.FusedMultiplyAdd(Pow2(-1022), Pow2(-1022), 0));
        Bits("fma -0*2+-0", Math.FusedMultiplyAdd(-0.0, 2, -0.0));
        Bits("fma 0*-1+0", Math.FusedMultiplyAdd(0.0, -1, 0.0));
        Bits("fma 3*3+(-9)", Math.FusedMultiplyAdd(3, 3, -9));
        Bits("fma (1+2^-52)^2-1", Math.FusedMultiplyAdd(1 + Pow2(-52), 1 + Pow2(-52), -1));
        Bits("fma 1e300*1e300-1e300^2", Math.FusedMultiplyAdd(1e300, 1e300, -(1e300 * 1e300)));
        Bits("fma 1e-300*1e-300+1e-320", Math.FusedMultiplyAdd(1e-300, 1e-300, 1e-320));
        Console.WriteLine("fma inf*0+1 " + double.IsNaN(Math.FusedMultiplyAdd(double.PositiveInfinity, 0, 1)));
        Console.WriteLine("fma max*2+max inf " + double.IsPositiveInfinity(Math.FusedMultiplyAdd(max, 2, max)));
        Bits("ffma max*2-max", MathF.FusedMultiplyAdd(float.MaxValue, 2, -float.MaxValue));
        Bits("ffma 1+2^-23 squared -1", MathF.FusedMultiplyAdd(1 + BitConverter.Int32BitsToSingle(0x34000000), 1 + BitConverter.Int32BitsToSingle(0x34000000), -1));
        Bits("ffma eps*0.5+eps", MathF.FusedMultiplyAdd(float.Epsilon, 0.5f, float.Epsilon));

        // a random sweep over the exponent range: products that overflow or underflow, sums that cancel
        for (int i = 0; i < 300; i++)
        {
            int mode = (int)(Next() % 4);
            double x, y, z;
            if (mode == 0) { x = Pick(1023 - 300, 1023 + 300); y = Pick(1023 - 300, 1023 + 300); z = Pick(1023 - 300, 1023 + 300); }
            else if (mode == 1) { x = Pick(0, 2046); y = Pick(0, 2046); z = Pick(0, 2046); }
            else if (mode == 2) { x = Pick(1023 - 500, 1023 + 500); y = Pick(1023 - 500, 1023 + 500); z = -(x * y) * (1 + ((long)(Next() % 7) - 3) * Pow2(-52)); }
            else { x = Pick(1, 2046); y = Pick(1, 2046); z = Pick(0, 1500); }
            Bits("sweep " + i, Math.FusedMultiplyAdd(x, y, z));
        }
    }

    static void Trig()
    {
        double[] xs = [100000.0, -100000.0, 1234567.891, 1e10, -1e10, 1e300, -1e300];
        for (int i = 0; i < xs.Length; i++)
        {
            Bits("sin " + i, Math.Sin(xs[i]));
            Bits("cos " + i, Math.Cos(xs[i]));
            Bits("tan " + i, Math.Tan(xs[i]));
        }
    }

    static void Roots()
    {
        double[] radicands = [0, -0.0, 1, -1, 4, -8, 16, -32, 2.5, double.Epsilon, double.PositiveInfinity, double.NegativeInfinity, double.NaN];
        int[] degrees = [int.MinValue, -3, -2, -1, 0, 1, 2, 4, 5];
        // (degree 3 is Cbrt, whose result is the C runtime's own, not a correctly rounded value, so it is left out)
        for (int i = 0; i < radicands.Length; i++)
            for (int j = 0; j < degrees.Length; j++)
            {
                Bits("rootN d " + i + " " + j, double.RootN(radicands[i], degrees[j]));
                Bits("rootN f " + i + " " + j, (double)float.RootN((float)radicands[i], degrees[j]));
            }
    }

    // generic math edges, as the NetWasm suite's GenericMathEdgeCorpus does for doubles
    static void GenericEdges<T>(T[] values, Func<T, double> toDouble) where T : IFloatingPointIeee754<T>
    {
        foreach (var a in values)
        {
            Bits("g ceil", toDouble(T.Ceiling(a)));
            Bits("g floor", toDouble(T.Floor(a)));
            Bits("g round", toDouble(T.Round(a)));
            Bits("g round away", toDouble(T.Round(a, MidpointRounding.AwayFromZero)));
            Bits("g round 1", toDouble(T.Round(a, 1)));
            Bits("g round 2", toDouble(T.Round(a, 2)));
            Bits("g truncate", toDouble(T.Truncate(a)));
            Bits("g binc", toDouble(T.BitIncrement(a)));
            Bits("g bdec", toDouble(T.BitDecrement(a)));
            Bits("g scaleb3", toDouble(T.ScaleB(a, 3)));
            Bits("g scaleb-1000", toDouble(T.ScaleB(a, -1000)));
            Console.WriteLine("g ilogb " + T.ILogB(a));
            foreach (var b in values)
            {
                Bits("g ieee", toDouble(T.Ieee754Remainder(a, b)));
                Bits("g fma", toDouble(T.FusedMultiplyAdd(a, b, T.One)));
                Bits("g mod", toDouble(a % b));
                Bits("g maxmag", toDouble(T.MaxMagnitude(a, b)));
                Bits("g minmag", toDouble(T.MinMagnitude(a, b)));
                Bits("g maxmagN", toDouble(T.MaxMagnitudeNumber(a, b)));
                Bits("g minmagN", toDouble(T.MinMagnitudeNumber(a, b)));
                Bits("g copysign", toDouble(T.CopySign(a, b)));
            }
        }
    }

    static void Main()
    {
        Fma();
        Trig();
        Roots();
        double[] values = [0d, -0d, 1d, -1d, 0.5, 1.5, 2.5, -2.5, double.Epsilon, -double.Epsilon, double.Epsilon * 3, 1e-308,
            double.MinValue, double.MaxValue, double.NaN, double.PositiveInfinity, double.NegativeInfinity, 123.456, 1e300];
        GenericEdges(values, v => v);
        Console.WriteLine("done");
    }
}
