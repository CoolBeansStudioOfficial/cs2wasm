// Portable double-precision math, ported from FreeBSD msun / fdlibm (Sun Microsystems, permissive licence).
// Computed in WASM so that every machine gets bit-identical results (section 7.3).
#pragma warning disable CS0436
using Il2Wasm.Runtime;

namespace System
{
    internal static class CMath
    {
        static int Hi(double x) => (int)(Rt.F64Bits(x) >> 32);
        static uint Lo(double x) => (uint)Rt.F64Bits(x);
        static double Make(int hi, uint lo) => Rt.F64FromBits(((long)hi << 32) | lo);
        static double WithHi(double x, int hi) => Make(hi, Lo(x));
        static double WithLo(double x, uint lo) => Make(Hi(x), lo);

        // ------------------------------------------------------------------ exp
        const double Ln2Hi = 6.93147180369123816490e-01, Ln2Lo = 1.90821492927058770002e-10, InvLn2 = 1.44269504088896338700e+00;
        
        // double-double helpers (no FMA in core WASM: Dekker products)
        static void TwoSum(double a, double b, out double s, out double e) { s = a + b; double bb = s - a; e = (a - (s - bb)) + (b - bb); }
        static void QuickTwoSum(double a, double b, out double s, out double e) { s = a + b; e = b - (s - a); }
        static void TwoProd(double a, double b, out double p, out double e)
        {
            p = a * b;
            const double split = 134217729.0;
            double ca = split * a, ah = ca - (ca - a), al = a - ah;
            double cb = split * b, bh = cb - (cb - b), bl = b - bh;
            e = ((ah * bh - p) + ah * bl + al * bh) + al * bl;
        }
        static void DDMul(double ah, double al, double bh, double bl, out double h, out double l)
        {
            TwoProd(ah, bh, out double p, out double e);
            e += ah * bl + al * bh;
            QuickTwoSum(p, e, out h, out l);
        }

        static readonly double[] InvFact = [1.0, 1.0, 0.5, 1.0 / 6, 1.0 / 24, 1.0 / 120, 1.0 / 720, 1.0 / 5040, 1.0 / 40320, 1.0 / 362880, 1.0 / 3628800, 1.0 / 39916800];

        /// <summary>e^x, evaluated in double-double and rounded once (matches correctly rounded results).</summary>
        public static double Exp(double x)
        {
            if (x != x) return x;
            if (x > 709.782712893384) return double.PositiveInfinity;
            if (x < -745.1332191019412) return 0;
            if (x == 0) return 1;
            double kd = Rt.RoundEven(x * InvLn2);
            int k = (int)kd;
            double hi = x - kd * Ln2Hi;
            TwoProd(kd, Ln2Lo, out double p, out double pe);
            TwoSum(hi, -p, out double rh, out double rl);
            rl -= pe;
            QuickTwoSum(rh, rl, out rh, out rl);
            rh *= 1.0 / 256; rl *= 1.0 / 256;
            // Taylor series in double-double (Horner)
            double sh = InvFact[11], sl = 0;
            for (int n = 10; n >= 0; n--)
            {
                DDMul(sh, sl, rh, rl, out sh, out sl);
                TwoSum(sh, InvFact[n], out double t, out double te);
                QuickTwoSum(t, te + sl, out sh, out sl);
            }
            for (int i = 0; i < 8; i++) DDMul(sh, sl, sh, sl, out sh, out sl);
            double r = sh + sl;
            if (k > -1022 && k < 1024) return r * Rt.F64FromBits((long)(0x3ff + k) << 52);
            return ScaleB(r, k);
        }

        // ------------------------------------------------------------------ log
        const double Lg1 = 6.666666666666735130e-01, Lg2 = 3.999999999940941908e-01, Lg3 = 2.857142874366239149e-01,
                     Lg4 = 2.222219843214978396e-01, Lg5 = 1.818357216161805012e-01, Lg6 = 1.531383769920937332e-01, Lg7 = 1.479819860511658591e-01;

        public static double Log(double x)
        {
            const double two54 = 1.80143985094819840000e+16;
            double hfsq, f, s, z, R, w, t1, t2, dk;
            int k, hx, i, j;
            uint lx;
            hx = Hi(x); lx = Lo(x);
            k = 0;
            if (hx < 0x00100000)
            {
                if (((hx & 0x7fffffff) | (int)lx) == 0) return double.NegativeInfinity;
                if (hx < 0) return double.NaN;
                k -= 54; x *= two54;
                hx = Hi(x);
            }
            if (hx >= 0x7ff00000) return x + x;
            k += (hx >> 20) - 1023;
            hx &= 0x000fffff;
            i = (hx + 0x95f64) & 0x100000;
            x = WithHi(x, hx | (i ^ 0x3ff00000));
            k += i >> 20;
            f = x - 1.0;
            if ((0x000fffff & (2 + hx)) < 3)
            {
                if (f == 0) { if (k == 0) return 0; dk = k; return dk * Ln2Hi + dk * Ln2Lo; }
                R = f * f * (0.5 - 0.33333333333333333 * f);
                if (k == 0) return f - R;
                dk = k; return dk * Ln2Hi - ((R - dk * Ln2Lo) - f);
            }
            s = f / (2.0 + f);
            dk = k;
            z = s * s;
            i = hx - 0x6147a;
            w = z * z;
            j = 0x6b851 - hx;
            t1 = w * (Lg2 + w * (Lg4 + w * Lg6));
            t2 = z * (Lg1 + w * (Lg3 + w * (Lg5 + w * Lg7)));
            i |= j;
            R = t2 + t1;
            if (i > 0)
            {
                hfsq = 0.5 * f * f;
                if (k == 0) return f - (hfsq - s * (hfsq + R));
                return dk * Ln2Hi - ((hfsq - (s * (hfsq + R) + dk * Ln2Lo)) - f);
            }
            if (k == 0) return f - s * (f - R);
            return dk * Ln2Hi - ((s * (f - R) - dk * Ln2Lo) - f);
        }

        public static double Log10(double x)
        {
            const double two54 = 1.80143985094819840000e+16, ivln10 = 4.34294481903251816668e-01,
                log10_2hi = 3.01029995663611771306e-01, log10_2lo = 3.69423907715893078616e-13;
            int hx = Hi(x); uint lx = Lo(x);
            int k = 0;
            if (hx < 0x00100000)
            {
                if (((hx & 0x7fffffff) | (int)lx) == 0) return double.NegativeInfinity;
                if (hx < 0) return double.NaN;
                k -= 54; x *= two54; hx = Hi(x);
            }
            if (hx >= 0x7ff00000) return x + x;
            k += (hx >> 20) - 1023;
            int i = (int)(((uint)k & 0x80000000) >> 31);
            hx = (hx & 0x000fffff) | ((0x3ff - i) << 20);
            double y = k + i;
            x = WithHi(x, hx);
            double z = y * log10_2lo + ivln10 * Log(x);
            return z + y * log10_2hi;
        }

        // (msun's e_log2.c: the logarithm's high and low parts times 1/ln 2's, so the last bit is right)
        public static double Log2(double x)
        {
            const double two54 = 1.80143985094819840000e+16, ivln2hi = 1.44269504072144627571e+00, ivln2lo = 1.67517131648865118353e-10;
            int hx = Hi(x); uint lx = Lo(x);
            int k = 0;
            if (hx < 0x00100000)
            {
                if (((hx & 0x7fffffff) | (int)lx) == 0) return double.NegativeInfinity;
                if (hx < 0) return double.NaN;
                k -= 54; x *= two54; hx = Hi(x);
            }
            if (hx >= 0x7ff00000) return x + x;
            if (hx == 0x3ff00000 && lx == 0) return 0;
            k += (hx >> 20) - 1023;
            hx &= 0x000fffff;
            int i = (hx + 0x95f64) & 0x100000;
            x = WithHi(x, hx | (i ^ 0x3ff00000));
            k += i >> 20;
            double y = k;
            double f = x - 1.0;
            double hfsq = 0.5 * f * f;
            double s = f / (2.0 + f), z = s * s, w = z * z;
            double t1 = w * (Lg2 + w * (Lg4 + w * Lg6));
            double t2 = z * (Lg1 + w * (Lg3 + w * (Lg5 + w * Lg7)));
            double r = s * (hfsq + (t2 + t1));
            double hi = WithLo(f - hfsq, 0);
            double lo = (f - hi) - hfsq + r;
            double valHi = hi * ivln2hi;
            double valLo = (lo + hi) * ivln2lo + lo * ivln2hi;
            w = y + valHi;
            valLo += (y - w) + valHi;
            return valLo + w;
        }

        // ------------------------------------------------------------------ trig kernels
        const double S1 = -1.66666666666666324348e-01, S2 = 8.33333333332248946124e-03, S3 = -1.98412698298579493134e-04,
                     S4 = 2.75573137070700676789e-06, S5 = -2.50507602534068634195e-08, S6 = 1.58969099521155010221e-10;

        static double KSin(double x, double y, int iy)
        {
            double z = x * x, w = z * z;
            double r = S2 + z * (S3 + z * S4) + z * w * (S5 + z * S6);
            double v = z * x;
            if (iy == 0) return x + v * (S1 + z * r);
            return x - ((z * (0.5 * y - v * r) - y) - v * S1);
        }

        const double C1 = 4.16666666666666019037e-02, C2 = -1.38888888888741095749e-03, C3 = 2.48015872894767294178e-05,
                     C4 = -2.75573143513906633035e-07, C5 = 2.08757232129817482790e-09, C6 = -1.13596475577881948265e-11;

        static double KCos(double x, double y)
        {
            double z = x * x, w = z * z;
            double r = z * (C1 + z * (C2 + z * C3)) + w * w * (C4 + z * (C5 + z * C6));
            double hz = 0.5 * z;
            w = 1.0 - hz;
            return w + (((1.0 - w) - hz) + (z * r - x * y));
        }

        static readonly double[] T = [3.33333333333334091986e-01, 1.33333333333201242699e-01, 5.39682539762260521377e-02,
            2.18694882948595424599e-02, 8.86323982359930005737e-03, 3.59207910759131235356e-03, 1.45620945432529025516e-03,
            5.88041240820264096874e-04, 2.46463134818469906812e-04, 7.81794442939557092300e-05, 7.14072491382608190305e-05,
            -1.85586374855275456654e-05, 2.59073051863633712884e-05];

        static double KTan(double x, double y, int iy)
        {
            const double pio4 = 7.85398163397448278999e-01, pio4lo = 3.06161699786838301793e-17;
            double z, r, v, w, s;
            int hx = Hi(x);
            int ix = hx & 0x7fffffff;
            if (ix >= 0x3FE59428)
            {
                if (hx < 0) { x = -x; y = -y; }
                z = pio4 - x; w = pio4lo - y; x = z + w; y = 0.0;
            }
            z = x * x; w = z * z;
            r = T[1] + w * (T[3] + w * (T[5] + w * (T[7] + w * (T[9] + w * T[11]))));
            v = z * (T[2] + w * (T[4] + w * (T[6] + w * (T[8] + w * (T[10] + w * T[12])))));
            s = z * x;
            r = y + z * (s * (r + v) + y);
            r += T[0] * s;
            w = x + r;
            if (ix >= 0x3FE59428)
            {
                v = iy;
                return (1 - ((hx >> 30) & 2)) * (v - 2.0 * (x - (w * w / (w + v) - r)));
            }
            if (iy == 1) return w;
            z = WithLo(w, 0);
            v = r - (z - x);
            double t, a = -1.0 / w;
            t = WithLo(a, 0);
            s = 1.0 + t * z;
            return t + a * (s + t * v);
        }

        /// <summary>Argument reduction: x = n·π/2 + (y0 + y1).</summary>
        static int RemPio2(double x, out double y0, out double y1)
        {
            const double invpio2 = 6.36619772367581382433e-01, pio2_1 = 1.57079632673412561417e+00, pio2_1t = 6.07710050650619224932e-11,
                pio2_2 = 6.07710050630396597660e-11, pio2_2t = 2.02226624879595063154e-21, pio2_3 = 2.02226624871116645580e-21, pio2_3t = 8.47842766036889956997e-32;
            int hx = Hi(x);
            int ix = hx & 0x7fffffff;
            if (ix <= 0x3fe921fb) { y0 = x; y1 = 0; return 0; }
            if (ix < 0x413921fb)
            {
                // |x| < 2^20 π/2: Cody-Waite with up to three steps
                double fn = Rt.RoundEven(x * invpio2);
                int n = (int)fn;
                double r = x - fn * pio2_1;
                double w = fn * pio2_1t;
                int j = ix >> 20;
                y0 = r - w;
                int i = j - ((Hi(y0) >> 20) & 0x7ff);
                if (i > 16)
                {
                    double t = r;
                    w = fn * pio2_2;
                    r = t - w;
                    w = fn * pio2_2t - ((t - r) - w);
                    y0 = r - w;
                    i = j - ((Hi(y0) >> 20) & 0x7ff);
                    if (i > 49)
                    {
                        t = r;
                        w = fn * pio2_3;
                        r = t - w;
                        w = fn * pio2_3t - ((t - r) - w);
                        y0 = r - w;
                    }
                }
                y1 = (r - y0) - w;
                return n;
            }
            if (ix >= 0x7ff00000) { y0 = y1 = x - x; return 0; }
            return RemPio2Large(x, out y0, out y1);
        }

        // 2/π in 24-bit chunks (Payne–Hanek), from k_rem_pio2.c
        static readonly int[] IPio2 = [
            0xA2F983, 0x6E4E44, 0x1529FC, 0x2757D1, 0xF534DD, 0xC0DB62, 0x95993C, 0x439041, 0xFE5163, 0xABDEBB, 0xC561B7, 0x246E3A,
            0x424DD2, 0xE00649, 0x2EEA09, 0xD1921C, 0xFE1DEB, 0x1CB129, 0xA73EE8, 0x8235F5, 0x2EBB44, 0x84E99C, 0x7026B4, 0x5F7E41,
            0x3991D6, 0x398353, 0x39F49C, 0x845F8B, 0xBDF928, 0x3B1FF8, 0x97FFDE, 0x05980F, 0xEF2F11, 0x8B5A0A, 0x6D1F6D, 0x367ECF,
            0x27CB09, 0xB74F46, 0x3F669E, 0x5FEA2D, 0x7527BA, 0xC7EBE5, 0xF17B3D, 0x0739F7, 0x8A5292, 0xEA6BFB, 0x5FB11F, 0x8D5D08,
            0x560330, 0x46FC7B, 0x6BABF0, 0xCFBC20, 0x9AF436, 0x1DA9E3, 0x91615E, 0xE61B08, 0x659985, 0x5F14A0, 0x68408D, 0xFFD880,
            0x4D7327, 0x310606, 0x1556CA, 0x73A8C9, 0x60E27B, 0xC08C6B];
        static readonly double[] PIo2 = [1.57079625129699707031e+00, 7.54978941586159635335e-08, 5.39030252995776476554e-15,
            3.28200341580791294123e-22, 1.27065575308067607349e-29, 1.22933308981111328932e-36, 2.73370053816464559624e-44, 2.16741683877804819444e-51];

        static int RemPio2Large(double x, out double y0, out double y1)
        {
            // split |x| into 24-bit pieces: x = sum tx[i] * 2^(e0 - 24 i)
            int hx = Hi(x);
            int e0 = ((hx & 0x7fffffff) >> 20) - 1046;
            double z = Make((hx & 0x7fffffff) - (e0 << 20), Lo(x));
            var tx = new double[3];
            for (int i = 0; i < 2; i++) { tx[i] = (int)z; z = (z - tx[i]) * 16777216.0; }
            tx[2] = z;
            int nx = 3;
            while (nx > 1 && tx[nx - 1] == 0) nx--;
            var y = new double[2];
            int n = KRemPio2(tx, y, e0, nx, 1);
            if (hx < 0) { y0 = -y[0]; y1 = -y[1]; return -n; }
            y0 = y[0]; y1 = y[1];
            return n;
        }

        static int KRemPio2(double[] x, double[] y, int e0, int nx, int prec)
        {
            const double two24 = 1.67772160000000000000e+07, twon24 = 5.96046447753906250000e-08;
            int jk = 4, jz, jx, jv, jp, jk0, carry, n, i, j, k, m, q0, ih;
            double z, fw;
            var iq = new int[20];
            var f = new double[20];
            var fq = new double[20];
            var q = new double[20];
            jp = jk;
            jx = nx - 1;
            jv = (e0 - 3) / 24; if (jv < 0) jv = 0;
            q0 = e0 - 24 * (jv + 1);
            j = jv - jx; m = jx + jk;
            for (i = 0; i <= m; i++, j++) f[i] = j < 0 ? 0.0 : IPio2[j];
            for (i = 0; i <= jk; i++) { fw = 0; for (j = 0; j <= jx; j++) fw += x[j] * f[jx + i - j]; q[i] = fw; }
            jz = jk;
            while (true)
            {
                for (i = 0, j = jz, z = q[jz]; j > 0; i++, j--)
                {
                    fw = (int)(twon24 * z);
                    iq[i] = (int)(z - two24 * fw);
                    z = q[j - 1] + fw;
                }
                z = ScaleB(z, q0);
                z -= 8.0 * Rt.Floor(z * 0.125);
                n = (int)z;
                z -= n;
                ih = 0;
                if (q0 > 0) { i = iq[jz - 1] >> (24 - q0); n += i; iq[jz - 1] -= i << (24 - q0); ih = iq[jz - 1] >> (23 - q0); }
                else if (q0 == 0) ih = iq[jz - 1] >> 23;
                else if (z >= 0.5) ih = 2;
                if (ih > 0)
                {
                    n += 1; carry = 0;
                    for (i = 0; i < jz; i++)
                    {
                        j = iq[i];
                        if (carry == 0) { if (j != 0) { carry = 1; iq[i] = 0x1000000 - j; } }
                        else iq[i] = 0xffffff - j;
                    }
                    if (q0 > 0)
                    {
                        if (q0 == 1) iq[jz - 1] &= 0x7fffff;
                        else if (q0 == 2) iq[jz - 1] &= 0x3fffff;
                    }
                    if (ih == 2) { z = 1.0 - z; if (carry != 0) z -= ScaleB(1.0, q0); }
                }
                if (z == 0)
                {
                    j = 0;
                    for (i = jz - 1; i >= jk; i--) j |= iq[i];
                    if (j == 0)
                    {
                        for (k = 1; iq[jk - k] == 0; k++) ;
                        for (i = jz + 1; i <= jz + k; i++)
                        {
                            f[jx + i] = IPio2[jv + i];
                            for (j = 0, fw = 0.0; j <= jx; j++) fw += x[j] * f[jx + i - j];
                            q[i] = fw;
                        }
                        jz += k;
                        continue;
                    }
                }
                break;
            }
            if (z == 0.0) { jz -= 1; q0 -= 24; while (iq[jz] == 0) { jz--; q0 -= 24; } }
            else
            {
                z = ScaleB(z, -q0);
                if (z >= two24) { fw = (int)(twon24 * z); iq[jz] = (int)(z - two24 * fw); jz += 1; q0 += 24; iq[jz] = (int)fw; }
                else iq[jz] = (int)z;
            }
            fw = ScaleB(1.0, q0);
            for (i = jz; i >= 0; i--) { q[i] = fw * iq[i]; fw *= twon24; }
            for (i = jz; i >= 0; i--)
            {
                for (fw = 0.0, k = 0; k <= jp && k <= jz - i; k++) fw += PIo2[k] * q[i + k];
                fq[jz - i] = fw;
            }
            fw = 0.0;
            for (i = jz; i >= 0; i--) fw += fq[i];
            y[0] = ih == 0 ? fw : -fw;
            fw = fq[0] - fw;
            for (i = 1; i <= jz; i++) fw += fq[i];
            y[1] = ih == 0 ? fw : -fw;
            return n & 7;
        }

        public static double Sin(double x)
        {
            int ix = Hi(x) & 0x7fffffff;
            if (ix <= 0x3fe921fb)
            {
                if (ix < 0x3e500000 && (int)x == 0) return x;
                return KSin(x, 0.0, 0);
            }
            if (ix >= 0x7ff00000) return x - x;
            int n = RemPio2(x, out double y0, out double y1);
            switch (n & 3)
            {
                case 0: return KSin(y0, y1, 1);
                case 1: return KCos(y0, y1);
                case 2: return -KSin(y0, y1, 1);
                default: return -KCos(y0, y1);
            }
        }

        public static double Cos(double x)
        {
            int ix = Hi(x) & 0x7fffffff;
            if (ix <= 0x3fe921fb)
            {
                if (ix < 0x3e46a09e && (int)x == 0) return 1.0;
                return KCos(x, 0.0);
            }
            if (ix >= 0x7ff00000) return x - x;
            int n = RemPio2(x, out double y0, out double y1);
            switch (n & 3)
            {
                case 0: return KCos(y0, y1);
                case 1: return -KSin(y0, y1, 1);
                case 2: return -KCos(y0, y1);
                default: return KSin(y0, y1, 1);
            }
        }

        public static double Tan(double x)
        {
            int ix = Hi(x) & 0x7fffffff;
            if (ix <= 0x3fe921fb)
            {
                if (ix < 0x3e400000 && (int)x == 0) return x;
                return KTan(x, 0.0, 1);
            }
            if (ix >= 0x7ff00000) return x - x;
            int n = RemPio2(x, out double y0, out double y1);
            return KTan(y0, y1, 1 - ((n & 1) << 1));
        }

        // ------------------------------------------------------------------ inverse trig
        static readonly double[] AtanHi = [4.63647609000806093515e-01, 7.85398163397448278999e-01, 9.82793723247329054082e-01, 1.57079632679489655800e+00];
        static readonly double[] AtanLo = [2.26987774529616870924e-17, 3.06161699786838301793e-17, 1.39033110312309984516e-17, 6.12323399573676603587e-17];
        static readonly double[] AT = [3.33333333333329318027e-01, -1.99999999998764832476e-01, 1.42857142725034663711e-01,
            -1.11111104054623557880e-01, 9.09088713343650656196e-02, -7.69187620504482999495e-02, 6.66107313738753120669e-02,
            -5.83357013379057348645e-02, 4.97687799461593236017e-02, -3.65315727442169155270e-02, 1.62858201153657823623e-02];

        public static double Atan(double x)
        {
            double w, s1, s2, z;
            int ix, hx, id;
            hx = Hi(x);
            ix = hx & 0x7fffffff;
            if (ix >= 0x44100000)
            {
                if (ix > 0x7ff00000 || (ix == 0x7ff00000 && Lo(x) != 0)) return x + x;
                return hx > 0 ? AtanHi[3] + AtanLo[3] : -AtanHi[3] - AtanLo[3];
            }
            if (ix < 0x3fdc0000)
            {
                if (ix < 0x3e400000) return x;
                id = -1;
            }
            else
            {
                x = Rt.Abs(x);
                if (ix < 0x3ff30000)
                {
                    if (ix < 0x3fe60000) { id = 0; x = (2.0 * x - 1.0) / (2.0 + x); }
                    else { id = 1; x = (x - 1.0) / (x + 1.0); }
                }
                else
                {
                    if (ix < 0x40038000) { id = 2; x = (x - 1.5) / (1.0 + 1.5 * x); }
                    else { id = 3; x = -1.0 / x; }
                }
            }
            z = x * x;
            w = z * z;
            s1 = z * (AT[0] + w * (AT[2] + w * (AT[4] + w * (AT[6] + w * (AT[8] + w * AT[10])))));
            s2 = w * (AT[1] + w * (AT[3] + w * (AT[5] + w * (AT[7] + w * AT[9]))));
            if (id < 0) return x - x * (s1 + s2);
            z = AtanHi[id] - ((x * (s1 + s2) - AtanLo[id]) - x);
            return hx < 0 ? -z : z;
        }

        public static double Atan2(double y, double x)
        {
            const double pi_o_4 = 7.8539816339744827900E-01, pi_o_2 = 1.5707963267948965580E+00, pi = 3.1415926535897931160E+00, pi_lo = 1.2246467991473531772E-16;
            double z;
            int k, m, hx, hy, ix, iy;
            uint lx, ly;
            hx = Hi(x); ix = hx & 0x7fffffff; lx = Lo(x);
            hy = Hi(y); iy = hy & 0x7fffffff; ly = Lo(y);
            if (((uint)ix | ((lx | (uint)-(int)lx) >> 31)) > 0x7ff00000 || ((uint)iy | ((ly | (uint)-(int)ly) >> 31)) > 0x7ff00000) return x + y;
            if (hx == 0x3ff00000 && lx == 0) return Atan(y);
            m = ((hy >> 31) & 1) | ((hx >> 30) & 2);
            if ((iy | (int)ly) == 0)
            {
                switch (m) { case 0: case 1: return y; case 2: return pi; default: return -pi; }
            }
            if ((ix | (int)lx) == 0) return hy < 0 ? -pi_o_2 : pi_o_2;
            if (ix == 0x7ff00000)
            {
                if (iy == 0x7ff00000)
                {
                    switch (m) { case 0: return pi_o_4; case 1: return -pi_o_4; case 2: return 3.0 * pi_o_4; default: return -3.0 * pi_o_4; }
                }
                switch (m) { case 0: return 0.0; case 1: return -0.0; case 2: return pi; default: return -pi; }
            }
            if (iy == 0x7ff00000) return hy < 0 ? -pi_o_2 : pi_o_2;
            k = (iy - ix) >> 20;
            if (k > 60) { z = pi_o_2 + 0.5 * pi_lo; m &= 1; }
            else if (hx < 0 && k < -60) z = 0.0;
            else z = Atan(Rt.Abs(y / x));
            switch (m)
            {
                case 0: return z;
                case 1: return -z;
                case 2: return pi - (z - pi_lo);
                default: return (z - pi_lo) - pi;
            }
        }

        const double pS0 = 1.66666666666666657415e-01, pS1 = -3.25565818622400915405e-01, pS2 = 2.01212532134862925881e-01,
                     pS3 = -4.00555345006794114027e-02, pS4 = 7.91534994289814532176e-04, pS5 = 3.47933107596021167570e-05,
                     qS1 = -2.40339491173441421878e+00, qS2 = 2.02094576023350569471e+00, qS3 = -6.88283971605453293030e-01, qS4 = 7.70381505559019352791e-02;
        const double pio2_hi = 1.57079632679489655800e+00, pio2_lo = 6.12323399573676603587e-17, pio4_hi = 7.85398163397448278999e-01;

        static double R(double z)
        {
            double p = z * (pS0 + z * (pS1 + z * (pS2 + z * (pS3 + z * (pS4 + z * pS5)))));
            double q = 1.0 + z * (qS1 + z * (qS2 + z * (qS3 + z * qS4)));
            return p / q;
        }

        public static double Asin(double x)
        {
            double t, w, p, q, c, r, s;
            int hx = Hi(x), ix = hx & 0x7fffffff;
            if (ix >= 0x3ff00000)
            {
                if (((ix - 0x3ff00000) | (int)Lo(x)) == 0) return x * pio2_hi + x * 7.52316384526264005099e-37;
                return double.NaN;
            }
            if (ix < 0x3fe00000)
            {
                if (ix < 0x3e500000) return x;
                return x + x * R(x * x);
            }
            w = 1.0 - Rt.Abs(x);
            t = w * 0.5;
            s = Rt.Sqrt(t);
            if (ix >= 0x3FEF3333)
            {
                w = R(t);
                t = pio2_hi - (2.0 * (s + s * w) - pio2_lo);
            }
            else
            {
                w = WithLo(s, 0);
                c = (t - w * w) / (s + w);
                r = R(t);
                p = 2.0 * s * r - (pio2_lo - 2.0 * c);
                q = pio4_hi - 2.0 * w;
                t = pio4_hi - (p - q);
            }
            return hx > 0 ? t : -t;
        }

        public static double Acos(double x)
        {
            double z, w, s, c, df;
            int hx = Hi(x), ix = hx & 0x7fffffff;
            if (ix >= 0x3ff00000)
            {
                if (((ix - 0x3ff00000) | (int)Lo(x)) == 0)
                {
                    if (hx > 0) return 0.0;
                    return 3.14159265358979311600e+00 + 2.0 * pio2_lo;
                }
                return double.NaN;
            }
            if (ix < 0x3fe00000)
            {
                if (ix <= 0x3c600000) return pio2_hi + pio2_lo;
                z = x * x;
                return pio2_hi - (x - (pio2_lo - x * R(z)));
            }
            if (hx < 0)
            {
                z = (1.0 + x) * 0.5;
                s = Rt.Sqrt(z);
                w = R(z) * s - pio2_lo;
                return 3.14159265358979311600e+00 - 2.0 * (s + w);
            }
            z = (1.0 - x) * 0.5;
            s = Rt.Sqrt(z);
            df = WithLo(s, 0);
            c = (z - df * df) / (s + df);
            w = R(z) * s + c;
            return 2.0 * (df + w);
        }

        // ------------------------------------------------------------------ pow
        static readonly double[] bp = [1.0, 1.5], dp_h = [0.0, 5.84962487220764160156e-01], dp_l = [0.0, 1.35003920212974897128e-08];

        public static double Pow(double x, double y)
        {
            const double two53 = 9007199254740992.0, huge = 1.0e300, tiny = 1.0e-300,
                L1 = 5.99999999999994648725e-01, L2 = 4.28571428578550184252e-01, L3 = 3.33333329818377432918e-01,
                L4 = 2.72728123808534006489e-01, L5 = 2.30660745775561754067e-01, L6 = 2.06975017800338417784e-01,
                PP1 = 1.66666666666666019037e-01, PP2 = -2.77777777770155933842e-03, PP3 = 6.61375632143793436117e-05,
                PP4 = -1.65339022054652515390e-06, PP5 = 4.13813679705723846039e-08,
                lg2 = 6.93147180559945286227e-01, lg2_h = 6.93147182464599609375e-01, lg2_l = -1.90465429995776804525e-09,
                ovt = 8.0085662595372944372e-17, cp = 9.61796693925975554329e-01, cp_h = 9.61796700954437255859e-01,
                cp_l = -7.02846165095275826516e-09, ivln2 = 1.44269504088896338700e+00, ivln2_h = 1.44269502162933349609e+00,
                ivln2_l = 1.92596299112661746887e-08;
            double z, ax, z_h, z_l, p_h, p_l, y1, t1, t2, r, s, t, u, v, w;
            int i, j, k, yisint, n, hx, hy, ix, iy;
            uint lx, ly;
            hx = Hi(x); lx = Lo(x); hy = Hi(y); ly = Lo(y);
            ix = hx & 0x7fffffff; iy = hy & 0x7fffffff;
            if ((iy | (int)ly) == 0) return 1.0;
            if (hx == 0x3ff00000 && lx == 0) return 1.0;
            if (ix > 0x7ff00000 || (ix == 0x7ff00000 && lx != 0) || iy > 0x7ff00000 || (iy == 0x7ff00000 && ly != 0)) return x + y;
            yisint = 0;
            if (hx < 0)
            {
                if (iy >= 0x43400000) yisint = 2;
                else if (iy >= 0x3ff00000)
                {
                    k = (iy >> 20) - 0x3ff;
                    if (k > 20) { j = (int)(ly >> (52 - k)); if ((uint)(j << (52 - k)) == ly) yisint = 2 - (j & 1); }
                    else if (ly == 0) { j = iy >> (20 - k); if ((j << (20 - k)) == iy) yisint = 2 - (j & 1); }
                }
            }
            if (ly == 0)
            {
                if (iy == 0x7ff00000)
                {
                    if (((ix - 0x3ff00000) | (int)lx) == 0) return 1.0;
                    if (ix >= 0x3ff00000) return hy >= 0 ? y : 0.0;
                    return hy < 0 ? -y : 0.0;
                }
                if (iy == 0x3ff00000) return hy < 0 ? 1.0 / x : x;
                if (hy == 0x40000000) return x * x;
                if (hy == 0x3fe00000 && hx >= 0) return Rt.Sqrt(x);
            }
            ax = Rt.Abs(x);
            if (lx == 0)
            {
                if (ix == 0x7ff00000 || ix == 0 || ix == 0x3ff00000)
                {
                    z = ax;
                    if (hy < 0) z = 1.0 / z;
                    if (hx < 0)
                    {
                        if (((ix - 0x3ff00000) | yisint) == 0) z = (z - z) / (z - z);
                        else if (yisint == 1) z = -z;
                    }
                    return z;
                }
            }
            int nflag = (int)(((uint)hx >> 31) - 1);
            if ((nflag | yisint) == 0) return (x - x) / (x - x);
            double sgn = 1.0;
            if ((nflag | (yisint - 1)) == 0) sgn = -1.0;
            if (iy > 0x41e00000)
            {
                if (iy > 0x43f00000)
                {
                    if (ix <= 0x3fefffff) return hy < 0 ? huge * huge : tiny * tiny;
                    if (ix >= 0x3ff00000) return hy > 0 ? huge * huge : tiny * tiny;
                }
                if (ix < 0x3fefffff) return hy < 0 ? sgn * huge * huge : sgn * tiny * tiny;
                if (ix > 0x3ff00000) return hy > 0 ? sgn * huge * huge : sgn * tiny * tiny;
                t = ax - 1.0;
                w = (t * t) * (0.5 - t * (0.3333333333333333333333 - t * 0.25));
                u = ivln2_h * t;
                v = t * ivln2_l - w * ivln2;
                t1 = WithLo(u + v, 0);
                t2 = v - (t1 - u);
            }
            else
            {
                double ss, s2, s_h, s_l, t_h, t_l;
                n = 0;
                if (ix < 0x00100000) { ax *= two53; n -= 53; ix = Hi(ax); }
                n += ((ix) >> 20) - 0x3ff;
                j = ix & 0x000fffff;
                ix = j | 0x3ff00000;
                if (j <= 0x3988E) k = 0;
                else if (j < 0xBB67A) k = 1;
                else { k = 0; n += 1; ix -= 0x00100000; }
                ax = WithHi(ax, ix);
                u = ax - bp[k];
                v = 1.0 / (ax + bp[k]);
                ss = u * v;
                s_h = WithLo(ss, 0);
                t_h = Make(((ix >> 1) | 0x20000000) + 0x00080000 + (k << 18), 0);
                t_l = ax - (t_h - bp[k]);
                s_l = v * ((u - s_h * t_h) - s_h * t_l);
                s2 = ss * ss;
                r = s2 * s2 * (L1 + s2 * (L2 + s2 * (L3 + s2 * (L4 + s2 * (L5 + s2 * L6)))));
                r += s_l * (s_h + ss);
                s2 = s_h * s_h;
                t_h = WithLo(3.0 + s2 + r, 0);
                t_l = r - ((t_h - 3.0) - s2);
                u = s_h * t_h;
                v = s_l * t_h + t_l * ss;
                p_h = WithLo(u + v, 0);
                p_l = v - (p_h - u);
                z_h = cp_h * p_h;
                z_l = cp_l * p_h + p_l * cp + dp_l[k];
                t = n;
                t1 = WithLo(((z_h + z_l) + dp_h[k]) + t, 0);
                t2 = z_l - (((t1 - t) - dp_h[k]) - z_h);
            }
            y1 = WithLo(y, 0);
            p_l = (y - y1) * t1 + y * t2;
            p_h = y1 * t1;
            z = p_l + p_h;
            j = Hi(z);
            i = (int)Lo(z);
            if (j >= 0x40900000)
            {
                if (((j - 0x40900000) | i) != 0) return sgn * huge * huge;
                if (p_l + ovt > z - p_h) return sgn * huge * huge;
            }
            else if ((j & 0x7fffffff) >= 0x4090cc00)
            {
                if (((j - unchecked((int)0xc090cc00)) | i) != 0) return sgn * tiny * tiny;
                if (p_l <= z - p_h) return sgn * tiny * tiny;
            }
            i = j & 0x7fffffff;
            k = (i >> 20) - 0x3ff;
            n = 0;
            if (i > 0x3fe00000)
            {
                n = j + (0x00100000 >> (k + 1));
                k = ((n & 0x7fffffff) >> 20) - 0x3ff;
                t = Make(n & ~(0x000fffff >> k), 0);
                n = ((n & 0x000fffff) | 0x00100000) >> (20 - k);
                if (j < 0) n = -n;
                p_h -= t;
            }
            t = WithLo(p_l + p_h, 0);
            u = t * lg2_h;
            v = (p_l - (t - p_h)) * lg2 + t * lg2_l;
            z = u + v;
            w = v - (z - u);
            t = z * z;
            t1 = z - t * (PP1 + t * (PP2 + t * (PP3 + t * (PP4 + t * PP5))));
            r = (z * t1) / (t1 - 2.0) - (w + z * w);
            z = 1.0 - (r - z);
            j = Hi(z);
            j += n << 20;
            if ((j >> 20) <= 0) z = ScaleB(z, n);
            else z = WithHi(z, j);
            return sgn * z;
        }

        // ------------------------------------------------------------------ hyperbolic & misc
        public static double Expm1(double x)
        {
            if (Rt.Abs(x) < 1e-5) return x + 0.5 * x * x + x * x * x / 6.0;
            return Exp(x) - 1.0;
        }

        public static double Log1p(double x)
        {
            double u = 1.0 + x;
            if (u == 1.0) return x;
            return Log(u) * x / (u - 1.0);
        }

        public static double Sinh(double x)
        {
            double a = Rt.Abs(x);
            // (the sign bit, not x < 0: sinh(-0.0) is -0.0)
            double h = Hi(x) < 0 ? -0.5 : 0.5;
            if (a < 22)
            {
                double t = Expm1(a);
                if (a < 1) return h * (2.0 * t - t * t / (t + 1.0));
                return h * (t + t / (t + 1.0));
            }
            if (a < 709.7822265625) return h * Exp(a);
            double w = Exp(0.5 * a);
            return (h * w) * w;
        }

        public static double Cosh(double x)
        {
            double a = Rt.Abs(x);
            if (a < 0.34657359027997264)
            {
                double t = Expm1(a);
                double w = 1.0 + t;
                return 1.0 + (t * t) / (w + w);
            }
            if (a < 22) { double t = Exp(a); return 0.5 * t + 0.5 / t; }
            if (a < 709.7822265625) return 0.5 * Exp(a);
            double w2 = Exp(0.5 * a);
            return (0.5 * w2) * w2;
        }

        public static double Tanh(double x)
        {
            double a = Rt.Abs(x), z;
            if (double.IsNaN(x)) return x;
            if (a < 22)
            {
                if (a < 2.77555756156289135105e-17) return x;
                if (a >= 1) { double t = Expm1(2 * a); z = 1.0 - 2.0 / (t + 2.0); }
                else { double t = Expm1(-2 * a); z = -t / (t + 2.0); }
            }
            else z = 1.0;
            return x < 0 ? -z : z;
        }

        public static double Asinh(double x)
        {
            double a = Rt.Abs(x);
            if (double.IsNaN(x) || double.IsInfinity(x)) return x;
            double r;
            if (a > 1e8) r = Log(a) + 6.93147180559945286227e-01;
            else if (a > 2) r = Log(2 * a + 1 / (Rt.Sqrt(x * x + 1) + a));
            else if (a < 3.725290298461914e-09) return x;
            else { double t = x * x; r = Log1p(a + t / (1 + Rt.Sqrt(1 + t))); }
            return x < 0 ? -r : r;
        }

        public static double Acosh(double x)
        {
            if (x < 1) return double.NaN;
            if (x >= 2.68435456e8) return Log(x) + 6.93147180559945286227e-01;
            if (x == 1) return 0;
            if (x > 2) { double t = x * x; return Log(2.0 * x - 1.0 / (x + Rt.Sqrt(t - 1.0))); }
            double t2 = x - 1.0;
            return Log1p(t2 + Rt.Sqrt(2.0 * t2 + t2 * t2));
        }

        public static double Atanh(double x)
        {
            double a = Rt.Abs(x);
            if (a > 1) return double.NaN;
            if (a == 1) return x / 0.0;
            if (a < 3.725290298461914e-09) return x;
            double t;
            if (a < 0.5) { t = a + a; t = 0.5 * Log1p(t + t * a / (1 - a)); }
            else t = 0.5 * Log1p((a + a) / (1 - a));
            return x < 0 ? -t : t;
        }

        public static double Cbrt(double x)
        {
            const uint B1 = 715094163, B2 = 696219795;
            const double P0 = 1.87595182427177009643, P1c = -1.88497979543377169875, P2c = 1.621429720105354466140,
                P3c = -0.758397934778766047437, P4c = 0.145996192886612446982;
            int hx = Hi(x);
            uint sign = (uint)hx & 0x80000000;
            hx &= 0x7fffffff;
            if (hx >= 0x7ff00000) return x + x;
            double t;
            if (hx < 0x00100000)
            {
                if ((hx | (int)Lo(x)) == 0) return x;
                t = x * 18014398509481984.0; // 2^54
                int high = Hi(t) & 0x7fffffff;
                t = Make((int)(sign | ((uint)high / 3 + B2)), 0);
            }
            else t = Make((int)(sign | ((uint)hx / 3 + B1)), 0);
            double r = (t * t) * (t / x);
            t = t * ((P0 + r * (P1c + r * P2c)) + ((r * r) * r) * (P3c + r * P4c));
            long bits = Rt.F64Bits(t);
            bits = (long)(((ulong)bits + 0x80000000UL) & 0xffffffffc0000000UL);
            t = Rt.F64FromBits(bits);
            double s = t * t;
            r = x / s;
            double w = t + t;
            r = (r - t) / (w + r);
            return t + t * r;
        }

        public static double ScaleB(double x, int n)
        {
            if (n > 1023)
            {
                x *= 8.98846567431157953865e307; n -= 1023;
                if (n > 1023) { x *= 8.98846567431157953865e307; n -= 1023; if (n > 1023) n = 1023; }
            }
            else if (n < -1022)
            {
                x *= 2.2250738585072014e-308 * 9007199254740992.0; n += 1022 - 53;
                if (n < -1022) { x *= 2.2250738585072014e-308 * 9007199254740992.0; n += 1022 - 53; if (n < -1022) n = -1022; }
            }
            return x * Rt.F64FromBits((long)(0x3ff + n) << 52);
        }

        public static int ILogB(double x)
        {
            long b = Rt.F64Bits(x);
            int e = (int)((b >> 52) & 0x7ff);
            if (e == 0)
            {
                if ((b << 1) == 0) return int.MinValue;
                long m = b << 12; int k = -1023;
                while (m > 0) { k--; m <<= 1; }
                return k;
            }
            if (e == 0x7ff) return (b << 12) != 0 ? int.MinValue : int.MaxValue;
            return e - 1023;
        }

        /// <summary>x·y+z with a single rounding: Boldo and Melquiond's emulated fma. The product is exact (TwoProd), the
        /// addend and the product's high part are summed exactly (TwoSum), and the two low parts are added rounding to
        /// odd, so the final rounding is the only one that loses bits (the sum rounded once more would round twice).</summary>
        public static double Fma(double x, double y, double z)
        {
            if (double.IsNaN(x) || double.IsNaN(y) || double.IsNaN(z) || double.IsInfinity(x) || double.IsInfinity(y) || x == 0 || y == 0) return x * y + z;
            double p = x * y;
            if (double.IsInfinity(p) || double.IsInfinity(z) || z == 0) return p + z;
            TwoProd(x, y, out double uh, out double ul);
            TwoSum(z, uh, out double th, out double tl);
            return th + AddRoundToOdd(tl, ul);
        }

        /// <summary>x·y+z rounded once to single precision: the product is exact in double, and the sum is rounded to odd
        /// in double (two bits more than single), so rounding that to single is the single rounding.</summary>
        public static float FmaSingle(float x, float y, float z)
        {
            double p = (double)x * y, dz = z;
            if (double.IsNaN(p) || double.IsNaN(dz) || double.IsInfinity(dz) || p == 0 || dz == 0) return (float)(p + dz);
            return (float)AddRoundToOdd(p, dz);
        }

        /// <summary>a + b rounded to odd: the sum rounded to nearest, unless it is inexact and its last bit is even, then the
        /// neighbour toward the exact sum (a + b lies between the two).</summary>
        static double AddRoundToOdd(double a, double b)
        {
            TwoSum(a, b, out double s, out double e);
            if (e == 0 || !double.IsFinite(s)) return s;
            long bits = BitConverter.DoubleToInt64Bits(s);
            if ((bits & 1) != 0) return s;
            // (the exact sum is beyond s in magnitude when e has s's sign: the next magnitude up)
            return BitConverter.Int64BitsToDouble((e > 0) == (s > 0) ? bits + 1 : bits - 1);
        }
    }
}
