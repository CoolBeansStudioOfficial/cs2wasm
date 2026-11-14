#pragma warning disable CS0436
using Cs2Wasm.Runtime;

namespace System
{
    /// <summary>Math is computed in WASM (section 7.3): exact operations are instructions, the rest is portable code (CMath).</summary>
    public static class Math
    {
        public const double PI = 3.14159265358979323846;
        public const double E = 2.7182818284590452354;
        public const double Tau = 6.283185307179586476925;

        public static int Abs(int value) { if (value < 0) { if (value == int.MinValue) throw new OverflowException("Negating the minimum value of a twos complement number is invalid."); return -value; } return value; }
        public static long Abs(long value) { if (value < 0) { if (value == long.MinValue) throw new OverflowException("Negating the minimum value of a twos complement number is invalid."); return -value; } return value; }
        public static short Abs(short value) { if (value < 0) { if (value == short.MinValue) throw new OverflowException("Negating the minimum value of a twos complement number is invalid."); return (short)-value; } return value; }
        public static sbyte Abs(sbyte value) { if (value < 0) { if (value == sbyte.MinValue) throw new OverflowException("Negating the minimum value of a twos complement number is invalid."); return (sbyte)-value; } return value; }
        public static double Abs(double value) => Rt.Abs(value);
        public static float Abs(float value) => Rt.AbsF(value);

        public static int Max(int a, int b) => a >= b ? a : b;
        public static uint Max(uint a, uint b) => a >= b ? a : b;
        public static long Max(long a, long b) => a >= b ? a : b;
        public static ulong Max(ulong a, ulong b) => a >= b ? a : b;
        public static short Max(short a, short b) => a >= b ? a : b;
        public static ushort Max(ushort a, ushort b) => a >= b ? a : b;
        public static byte Max(byte a, byte b) => a >= b ? a : b;
        public static sbyte Max(sbyte a, sbyte b) => a >= b ? a : b;
        public static double Max(double a, double b) => Rt.Max(a, b);
        public static float Max(float a, float b) => Rt.MaxF(a, b);
        public static int Min(int a, int b) => a <= b ? a : b;
        public static uint Min(uint a, uint b) => a <= b ? a : b;
        public static long Min(long a, long b) => a <= b ? a : b;
        public static ulong Min(ulong a, ulong b) => a <= b ? a : b;
        public static short Min(short a, short b) => a <= b ? a : b;
        public static ushort Min(ushort a, ushort b) => a <= b ? a : b;
        public static byte Min(byte a, byte b) => a <= b ? a : b;
        public static sbyte Min(sbyte a, sbyte b) => a <= b ? a : b;
        public static double Min(double a, double b) => Rt.Min(a, b);
        public static float Min(float a, float b) => Rt.MinF(a, b);

        public static int Clamp(int value, int min, int max) { if (min > max) ThrowMinMax(min, max); return value < min ? min : value > max ? max : value; }
        public static long Clamp(long value, long min, long max) { if (min > max) ThrowMinMax(min, max); return value < min ? min : value > max ? max : value; }
        public static uint Clamp(uint value, uint min, uint max) { if (min > max) ThrowMinMax(min, max); return value < min ? min : value > max ? max : value; }
        public static byte Clamp(byte value, byte min, byte max) { if (min > max) ThrowMinMax(min, max); return value < min ? min : value > max ? max : value; }
        public static double Clamp(double value, double min, double max) { if (min > max) ThrowMinMax(min, max); return value < min ? min : value > max ? max : value; }
        public static float Clamp(float value, float min, float max) { if (min > max) ThrowMinMax(min, max); return value < min ? min : value > max ? max : value; }
        static void ThrowMinMax(object min, object max) => throw new ArgumentException("'" + min.ToString() + "' cannot be greater than " + max.ToString() + ".");

        public static int Sign(int value) => value < 0 ? -1 : value > 0 ? 1 : 0;
        public static int Sign(long value) => value < 0 ? -1 : value > 0 ? 1 : 0;
        public static int Sign(double value)
        {
            if (value < 0) return -1;
            if (value > 0) return 1;
            if (value == 0) return 0;
            throw new ArithmeticException("Function does not accept floating point Not-a-Number values.");
        }
        public static int Sign(float value) => Sign((double)value);

        public static double Sqrt(double d) => Rt.Sqrt(d);
        public static double Floor(double d) => Rt.Floor(d);
        public static double Ceiling(double a) => Rt.Ceiling(a);
        public static double Truncate(double d) => Rt.Truncate(d);
        public static double Round(double a) => Rt.RoundEven(a);
        public static double Round(double value, MidpointRounding mode)
        {
            switch (mode)
            {
                case MidpointRounding.ToEven: return Rt.RoundEven(value);
                case MidpointRounding.AwayFromZero:
                    {
                        double f = Rt.Truncate(value);
                        double diff = value - f;
                        if (diff >= 0.5) return f + 1;
                        if (diff <= -0.5) return f - 1;
                        return f;
                    }
                case MidpointRounding.ToZero: return Rt.Truncate(value);
                case MidpointRounding.ToNegativeInfinity: return Rt.Floor(value);
                case MidpointRounding.ToPositiveInfinity: return Rt.Ceiling(value);
            }
            throw new ArgumentException("The value '" + ((int)mode).ToString() + "' is not valid for this usage of the type MidpointRounding.", "mode");
        }
        public static double Round(double value, int digits) => Round(value, digits, MidpointRounding.ToEven);
        public static double Round(double value, int digits, MidpointRounding mode)
        {
            if ((uint)digits > 15) throw new ArgumentOutOfRangeException("digits", "Rounding digits must be between 0 and 15, inclusive.");
            if (Rt.Abs(value) < 1e16)
            {
                double power10 = s_pow10[digits];
                value *= power10;
                value = Round(value, mode);
                value /= power10;
            }
            return value;
        }
        static readonly double[] s_pow10 = [1e0, 1e1, 1e2, 1e3, 1e4, 1e5, 1e6, 1e7, 1e8, 1e9, 1e10, 1e11, 1e12, 1e13, 1e14, 1e15];

        public static double CopySign(double x, double y) => Rt.CopySign(x, y);
        public static double FusedMultiplyAdd(double x, double y, double z) => CMath.Fma(x, y, z);
        public static double IEEERemainder(double x, double y)
        {
            if (double.IsNaN(x) || double.IsNaN(y) || double.IsInfinity(x) || y == 0) return double.NaN;
            double r = Rt.FMod(x, y);
            double ay = Rt.Abs(y), ar = Rt.Abs(r);
            if (ar > ay / 2 || (ar == ay / 2 && Rt.FMod(Rt.Truncate(x / y), 2) != 0)) r -= Rt.CopySign(ay, r);
            return r;
        }
        public static double BitIncrement(double x)
        {
            long bits = Rt.F64Bits(x);
            if (((bits >> 32) & 0x7FF00000) >= 0x7FF00000) return bits == unchecked((long)0xFFF0000000000000UL) ? double.MinValue : x;
            if (bits == unchecked((long)0x8000000000000000UL)) return double.Epsilon;
            bits += bits < 0 ? -1 : 1;
            return Rt.F64FromBits(bits);
        }
        public static double BitDecrement(double x)
        {
            long bits = Rt.F64Bits(x);
            if (((bits >> 32) & 0x7FF00000) >= 0x7FF00000) return bits == 0x7FF0000000000000L ? double.MaxValue : x;
            if (bits == 0) return -double.Epsilon;
            bits += bits < 0 ? 1 : -1;
            return Rt.F64FromBits(bits);
        }

        public static long BigMul(int a, int b) => (long)a * b;
        public static int DivRem(int a, int b, out int result) { result = a % b; return a / b; }
        public static long DivRem(long a, long b, out long result) { result = a % b; return a / b; }

        public static double Pow(double x, double y) => CMath.Pow(x, y);
        public static double Exp(double d) => CMath.Exp(d);
        public static double Log(double d) => CMath.Log(d);
        public static double Log(double a, double newBase)
        {
            if (double.IsNaN(a)) return a;
            if (double.IsNaN(newBase)) return newBase;
            if (newBase == 1) return double.NaN;
            if (a != 1 && (newBase == 0 || double.IsPositiveInfinity(newBase))) return double.NaN;
            return CMath.Log(a) / CMath.Log(newBase);
        }
        public static double Log10(double d) => CMath.Log10(d);
        public static double Log2(double d) => CMath.Log2(d);
        public static double Sin(double a) => CMath.Sin(a);
        public static double Cos(double d) => CMath.Cos(d);
        public static double Tan(double a) => CMath.Tan(a);
        public static double Asin(double d) => CMath.Asin(d);
        public static double Acos(double d) => CMath.Acos(d);
        public static double Atan(double d) => CMath.Atan(d);
        public static double Atan2(double y, double x) => CMath.Atan2(y, x);
        public static double Sinh(double value) => CMath.Sinh(value);
        public static double Cosh(double value) => CMath.Cosh(value);
        public static double Tanh(double value) => CMath.Tanh(value);
        public static double Asinh(double x) => CMath.Asinh(x);
        public static double Acosh(double x) => CMath.Acosh(x);
        public static double Atanh(double x) => CMath.Atanh(x);
        public static double Cbrt(double d) => CMath.Cbrt(d);
        public static double ReciprocalEstimate(double d) => 1.0 / d;
        public static double ReciprocalSqrtEstimate(double d) => 1.0 / Rt.Sqrt(d);
        public static double MaxMagnitude(double x, double y) { double ax = Rt.Abs(x), ay = Rt.Abs(y); if (ax > ay || double.IsNaN(ax)) return x; if (ax == ay) return double.IsNegative(x) ? y : x; return y; }
        public static double MinMagnitude(double x, double y) { double ax = Rt.Abs(x), ay = Rt.Abs(y); if (ax < ay || double.IsNaN(ax)) return x; if (ax == ay) return double.IsNegative(x) ? x : y; return y; }
        public static int ILogB(double x) => CMath.ILogB(x);
        public static double ScaleB(double x, int n) => CMath.ScaleB(x, n);
    }

    public static class MathF
    {
        public const float PI = 3.14159265f;
        public const float E = 2.71828183f;
        public const float Tau = 6.28318531f;
        public static float Abs(float x) => Rt.AbsF(x);
        public static float Max(float x, float y) => Rt.MaxF(x, y);
        public static float Min(float x, float y) => Rt.MinF(x, y);
        public static float Sqrt(float x) => Rt.SqrtF(x);
        public static float Floor(float x) => Rt.FloorF(x);
        public static float Ceiling(float x) => Rt.CeilingF(x);
        public static float Truncate(float x) => Rt.TruncateF(x);
        public static float Round(float x) => Rt.RoundEvenF(x);
        public static float Round(float x, MidpointRounding mode) => (float)Math.Round((double)x, mode);
        public static float Round(float x, int digits) => (float)Math.Round((double)x, digits);
        public static int Sign(float x) => Math.Sign(x);
        public static float CopySign(float x, float y) => (float)Rt.CopySign(x, y);
        public static float Pow(float x, float y) => (float)CMath.Pow(x, y);
        public static float Exp(float x) => (float)CMath.Exp(x);
        public static float Log(float x) => (float)CMath.Log(x);
        public static float Log10(float x) => (float)CMath.Log10(x);
        public static float Log2(float x) => (float)CMath.Log2(x);
        public static float Sin(float x) => (float)CMath.Sin(x);
        public static float Cos(float x) => (float)CMath.Cos(x);
        public static float Tan(float x) => (float)CMath.Tan(x);
        public static float Asin(float x) => (float)CMath.Asin(x);
        public static float Acos(float x) => (float)CMath.Acos(x);
        public static float Atan(float x) => (float)CMath.Atan(x);
        public static float Atan2(float y, float x) => (float)CMath.Atan2(y, x);
        public static float Sinh(float x) => (float)CMath.Sinh(x);
        public static float Cosh(float x) => (float)CMath.Cosh(x);
        public static float Tanh(float x) => (float)CMath.Tanh(x);
        public static float Cbrt(float x) => (float)CMath.Cbrt(x);
        public static float FusedMultiplyAdd(float x, float y, float z) => (float)((double)x * y + z);
    }
}
