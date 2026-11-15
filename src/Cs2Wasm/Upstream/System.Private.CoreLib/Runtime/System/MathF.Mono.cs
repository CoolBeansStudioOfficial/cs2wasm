// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// cs2wasm: computed in WASM (SPEC 7.3): what WASM defines exactly is an instruction, the rest is portable code (CMath).

using System.Runtime.CompilerServices;

namespace System
{
    public partial class MathF
    {
                public static float Acos(float x) => (float)CMath.Acos(x);

                public static float Acosh(float x) => (float)CMath.Acosh(x);

                public static float Asin(float x) => (float)CMath.Asin(x);

                public static float Asinh(float x) => (float)CMath.Asinh(x);

                public static float Atan(float x) => (float)CMath.Atan(x);

                public static float Atan2(float y, float x) => (float)CMath.Atan2(y, x);

                public static float Atanh(float x) => (float)CMath.Atanh(x);

                public static float Cbrt(float x) => (float)CMath.Cbrt(x);

                public static float Ceiling(float x) => Cs2Wasm.Runtime.Rt.CeilingF(x);

                public static float Cos(float x) => (float)CMath.Cos(x);

                public static float Cosh(float x) => (float)CMath.Cosh(x);

                public static float Exp(float x) => (float)CMath.Exp(x);

                public static float Floor(float x) => Cs2Wasm.Runtime.Rt.FloorF(x);

                public static float Log(float x) => (float)CMath.Log(x);

                public static float Log10(float x) => (float)CMath.Log10(x);

                public static float Pow(float x, float y) => (float)CMath.Pow(x, y);

                public static float Sin(float x) => (float)CMath.Sin(x);

        public static (float Sin, float Cos) SinCos(float x) => (Sin(x), Cos(x));

                public static float Sinh(float x) => (float)CMath.Sinh(x);

                public static float Sqrt(float x) => Cs2Wasm.Runtime.Rt.SqrtF(x);

                public static float Tan(float x) => (float)CMath.Tan(x);

                public static float Tanh(float x) => (float)CMath.Tanh(x);

        [Intrinsic]
                public static float FusedMultiplyAdd(float x, float y, float z) => (float)CMath.Fma(x, y, z);

                public static float Log2(float x) => (float)CMath.Log2(x);

                private static unsafe float ModF(float x, float* intptr) { float t = Cs2Wasm.Runtime.Rt.TruncateF(x); *intptr = t; return float.IsInfinity(x) ? CopySign(0.0f, x) : x - t; }
    }
}
