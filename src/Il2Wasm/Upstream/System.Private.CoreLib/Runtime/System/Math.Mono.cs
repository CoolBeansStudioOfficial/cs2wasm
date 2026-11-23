// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// il2wasm: computed in WASM (SPEC 7.3): what WASM defines exactly is an instruction, the rest is portable code (CMath).

using System.Runtime.CompilerServices;

namespace System
{
    public partial class Math
    {
                public static double Acos(double d) => CMath.Acos(d);

                public static double Acosh(double d) => CMath.Acosh(d);

                public static double Asin(double d) => CMath.Asin(d);

                public static double Asinh(double d) => CMath.Asinh(d);

                public static double Atan(double d) => CMath.Atan(d);

                public static double Atan2(double y, double x) => CMath.Atan2(y, x);

                public static double Atanh(double d) => CMath.Atanh(d);

                public static double Cbrt(double d) => CMath.Cbrt(d);

                public static double Ceiling(double a) => Il2Wasm.Runtime.Rt.Ceiling(a);

                public static double Cos(double d) => CMath.Cos(d);

                public static double Cosh(double value) => CMath.Cosh(value);

                public static double Exp(double d) => CMath.Exp(d);

                public static double Floor(double d) => Il2Wasm.Runtime.Rt.Floor(d);

                public static double Log(double d) => CMath.Log(d);

                public static double Log10(double d) => CMath.Log10(d);

                public static double Pow(double x, double y) => CMath.Pow(x, y);

                public static double Sin(double a) => CMath.Sin(a);

        public static (double Sin, double Cos) SinCos(double x) => (Sin(x), Cos(x));

                public static double Sinh(double value) => CMath.Sinh(value);

                public static double Sqrt(double d) => Il2Wasm.Runtime.Rt.Sqrt(d);

                public static double Tan(double a) => CMath.Tan(a);

                public static double Tanh(double value) => CMath.Tanh(value);

        [Intrinsic]
                public static double FusedMultiplyAdd(double x, double y, double z) => CMath.Fma(x, y, z);

                public static double Log2(double x) => CMath.Log2(x);

                private static unsafe double ModF(double x, double* intptr) { double t = Il2Wasm.Runtime.Rt.Truncate(x); *intptr = t; return double.IsInfinity(x) ? CopySign(0.0, x) : x - t; }
    }
}
