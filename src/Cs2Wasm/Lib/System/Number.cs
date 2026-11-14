// Number formatting and parsing with .NET's invariant-culture results (section 7.4).
// Floating point uses exact big-integer arithmetic: shortest round-trip digits (Burger & Dybvig) for the default
// format and exactly rounded digits (ties to even) for precision formats, as .NET Core 3.0+ does.
#pragma warning disable CS0436
using Cs2Wasm.Runtime;

namespace System
{
    internal sealed class BigNum
    {
        uint[] _d = new uint[4];
        int _n;

        public BigNum(ulong v) { Set(v); }
        public void Set(ulong v) { _n = 0; if (v != 0) { Ensure(2); _d[0] = (uint)v; _d[1] = (uint)(v >> 32); _n = _d[1] != 0 ? 2 : 1; } }
        public BigNum Clone() { var b = new BigNum(0); b.Ensure(_n); for (int i = 0; i < _n; i++) b._d[i] = _d[i]; b._n = _n; return b; }
        public bool IsZero => _n == 0;

        void Ensure(int n)
        {
            if (_d.Length >= n) return;
            int m = _d.Length * 2; if (m < n) m = n;
            var nd = new uint[m];
            for (int i = 0; i < _n; i++) nd[i] = _d[i];
            _d = nd;
        }

        public void MulSmall(uint m)
        {
            ulong carry = 0;
            for (int i = 0; i < _n; i++) { ulong p = (ulong)_d[i] * m + carry; _d[i] = (uint)p; carry = p >> 32; }
            if (carry != 0) { Ensure(_n + 1); _d[_n++] = (uint)carry; }
        }

        public void ShiftLeft(int k)
        {
            if (_n == 0 || k == 0) return;
            int words = k >> 5, bits = k & 31;
            Ensure(_n + words + 1);
            if (bits == 0)
            {
                for (int i = _n - 1; i >= 0; i--) _d[i + words] = _d[i];
            }
            else
            {
                _d[_n + words] = 0;
                for (int i = _n - 1; i >= 0; i--)
                {
                    _d[i + words + 1] |= _d[i] >> (32 - bits);
                    _d[i + words] = _d[i] << bits;
                }
            }
            for (int i = 0; i < words; i++) _d[i] = 0;
            _n += words + 1;
            Trim();
        }

        public void MulPow10(int k) { while (k >= 9) { MulSmall(1000000000); k -= 9; } while (k-- > 0) MulSmall(10); }
        public void MulPow5(int k) { while (k >= 13) { MulSmall(1220703125); k -= 13; } while (k-- > 0) MulSmall(5); }

        void Trim() { while (_n > 0 && _d[_n - 1] == 0) _n--; }

        public void Add(BigNum b)
        {
            int n = _n > b._n ? _n : b._n;
            Ensure(n + 1);
            ulong carry = 0;
            for (int i = 0; i < n; i++)
            {
                ulong s = (ulong)(i < _n ? _d[i] : 0) + (i < b._n ? b._d[i] : 0) + carry;
                _d[i] = (uint)s; carry = s >> 32;
            }
            _n = n;
            if (carry != 0) _d[_n++] = (uint)carry;
        }

        /// <summary>this -= b (requires this >= b).</summary>
        public void Sub(BigNum b)
        {
            long borrow = 0;
            for (int i = 0; i < _n; i++)
            {
                long s = (long)_d[i] - (i < b._n ? b._d[i] : 0) - borrow;
                if (s < 0) { s += 1L << 32; borrow = 1; } else borrow = 0;
                _d[i] = (uint)s;
            }
            Trim();
        }

        public static int Compare(BigNum a, BigNum b)
        {
            if (a._n != b._n) return a._n < b._n ? -1 : 1;
            for (int i = a._n - 1; i >= 0; i--)
                if (a._d[i] != b._d[i]) return a._d[i] < b._d[i] ? -1 : 1;
            return 0;
        }

        /// <summary>Compare a + b with c.</summary>
        public static int CompareSum(BigNum a, BigNum b, BigNum c)
        {
            var s = a.Clone(); s.Add(b);
            return Compare(s, c);
        }

        /// <summary>Quotient (small) of this / s; this becomes the remainder.</summary>
        public int DivRemSmallQuotient(BigNum s)
        {
            int q = 0;
            while (Compare(this, s) >= 0) { Sub(s); q++; }
            return q;
        }

        public uint DivRemSmall(uint m)
        {
            ulong r = 0;
            for (int i = _n - 1; i >= 0; i--)
            {
                ulong cur = (r << 32) | _d[i];
                _d[i] = (uint)(cur / m);
                r = cur % m;
            }
            Trim();
            return (uint)r;
        }

        public int BitLength()
        {
            if (_n == 0) return 0;
            return (_n - 1) * 32 + (32 - Rt.I32Clz((int)_d[_n - 1]));
        }

        /// <summary>The top 64 bits (shifted so the highest set bit is bit 63) and whether any lower bit is set.</summary>
        public ulong Top64(out bool sticky, out int shift)
        {
            int bl = BitLength();
            shift = bl - 64;
            ulong r = 0;
            sticky = false;
            for (int bit = bl - 1, k = 63; bit >= 0; bit--, k--)
            {
                bool set = ((_d[bit >> 5] >> (bit & 31)) & 1) != 0;
                if (k >= 0) { if (set) r |= 1UL << k; }
                else if (set) { sticky = true; break; }
            }
            return r;
        }

        /// <summary>Decimal digits of the value (most significant first).</summary>
        public string ToDecimal()
        {
            if (_n == 0) return "0";
            var t = Clone();
            var chunks = new uint[(_n * 32) / 29 + 2];
            int c = 0;
            while (!t.IsZero) chunks[c++] = t.DivRemSmall(1000000000);
            var sb = new Text.StringBuilder();
            sb.Append(Number.UInt32ToString(chunks[c - 1]));
            for (int i = c - 2; i >= 0; i--)
            {
                string s = Number.UInt32ToString(chunks[i]);
                for (int z = s.Length; z < 9; z++) sb.Append('0');
                sb.Append(s);
            }
            return sb.ToString();
        }
    }

    /// <summary>Decimal digits d1 d2 ... with value 0.d1d2... × 10^(Exp+1), i.e. d1 is at 10^Exp.</summary>
    internal struct Digits
    {
        public byte[] D;
        public int Count;
        public int Exp;
    }

    internal static class Number
    {
        // ------------------------------------------------------------------ integers
        public static string Int32ToString(int v) => Int64ToString(v);
        public static string UInt32ToString(uint v) => UInt64ToString(v);

        public static string Int64ToString(long v)
        {
            if (v >= 0) return UInt64ToString((ulong)v);
            ulong m = (ulong)(-(v + 1)) + 1;
            return Neg(UInt64ToString(m));
        }

        static string Neg(string s) => string.Concat2("-", s);

        public static string UInt64ToString(ulong v)
        {
            if (v == 0) return "0";
            int n = 0;
            for (ulong t = v; t != 0; t /= 10) n++;
            string s = string.Alloc(n);
            int d = s.Data;
            for (int i = n - 1; i >= 0; i--) { Mem.StoreU16(d + i * 2, (char)('0' + (int)(v % 10))); v /= 10; }
            return s;
        }

        static void ParseFormat(string format, out char kind, out int precision)
        {
            kind = 'G'; precision = -1;
            if (format == null || format.Length == 0) return;
            char c = format[0];
            bool std = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');
            if (!std) { kind = '\0'; return; }
            int p = 0;
            for (int i = 1; i < format.Length; i++)
            {
                char d = format[i];
                if (d < '0' || d > '9') { kind = '\0'; return; }
                p = p * 10 + (d - '0');
                if (p > 999999999) throw new FormatException("Format specifier was invalid.");
            }
            kind = c;
            precision = format.Length > 1 ? p : -1;
        }

        /// <summary>Formats an integer; bits is the width for hexadecimal of negative values.</summary>
        public static string FormatInteger(long value, bool isUnsigned, ulong unsignedValue, int bits, string format)
        {
            ParseFormat(format, out char kind, out int prec);
            bool neg = !isUnsigned && value < 0;
            ulong mag = isUnsigned ? unsignedValue : neg ? (ulong)(-(value + 1)) + 1 : (ulong)value;
            switch (kind)
            {
                case 'G': case 'g':
                    if (prec <= 0 || prec >= 20) return neg ? Neg(UInt64ToString(mag)) : UInt64ToString(mag);
                    return FormatDouble(neg ? -(double)mag : mag, format, false);
                case 'D': case 'd':
                    {
                        string s = UInt64ToString(mag);
                        if (prec > s.Length) s = s.PadLeft(prec, '0');
                        return neg ? Neg(s) : s;
                    }
                case 'X': case 'x':
                    {
                        ulong u = isUnsigned ? unsignedValue : (ulong)value;
                        if (!isUnsigned && bits < 64) u &= (1UL << bits) - 1;
                        string digits = kind == 'X' ? "0123456789ABCDEF" : "0123456789abcdef";
                        int n = 0;
                        for (ulong t = u; t != 0; t >>= 4) n++;
                        if (n == 0) n = 1;
                        if (prec > n) n = prec;
                        string s = string.Alloc(n);
                        for (int i = n - 1; i >= 0; i--) { Mem.StoreU16(s.Data + i * 2, digits[(int)(u & 15)]); u >>= 4; }
                        return s;
                    }
                case 'N': case 'n':
                    {
                        string s = Group(UInt64ToString(mag));
                        int p = prec < 0 ? 2 : prec;
                        if (p > 0) s = string.Concat2(string.Concat2(s, "."), new string('0', p));
                        return neg ? Neg(s) : s;
                    }
                case 'F': case 'f':
                    {
                        string s = UInt64ToString(mag);
                        int p = prec < 0 ? 2 : prec;
                        if (p > 0) s = string.Concat2(string.Concat2(s, "."), new string('0', p));
                        return neg ? Neg(s) : s;
                    }
                case 'E': case 'e': case 'P': case 'p': case 'C': case 'c': case 'R': case 'r':
                    return FormatDouble(neg ? -(double)mag : mag, format, false);
                case '\0':
                    return FormatCustom(neg ? -(double)mag : mag, format, false);
            }
            throw new FormatException("Format specifier was invalid.");
        }

        static string Group(string intDigits)
        {
            int n = intDigits.Length;
            if (n <= 3) return intDigits;
            var sb = new Text.StringBuilder();
            int first = n % 3; if (first == 0) first = 3;
            sb.Append(intDigits.Substring(0, first));
            for (int i = first; i < n; i += 3) { sb.Append(','); sb.Append(intDigits.Substring(i, 3)); }
            return sb.ToString();
        }

        // ------------------------------------------------------------------ floating point: digit generation
        static void Decompose(double v, bool isFloat, out ulong mant, out int e2, out bool lowerBoundaryCloser, out bool even)
        {
            if (isFloat)
            {
                int bits = Rt.F32Bits((float)v);
                int be = (bits >> 23) & 0xFF;
                uint f = (uint)bits & 0x7FFFFF;
                if (be == 0) { mant = f; e2 = -149; lowerBoundaryCloser = false; }
                else { mant = f | 0x800000; e2 = be - 150; lowerBoundaryCloser = f == 0 && be > 1; }
                even = (mant & 1) == 0;
                return;
            }
            long b = Rt.F64Bits(v);
            int bexp = (int)((b >> 52) & 0x7FF);
            ulong frac = (ulong)b & 0xFFFFFFFFFFFFFUL;
            if (bexp == 0) { mant = frac; e2 = -1074; lowerBoundaryCloser = false; }
            else { mant = frac | (1UL << 52); e2 = bexp - 1075; lowerBoundaryCloser = frac == 0 && bexp > 1; }
            even = (mant & 1) == 0;
        }

        /// <summary>Shortest digits that read back as the same value (v &gt; 0, finite).</summary>
        static Digits Shortest(double v, bool isFloat)
        {
            Decompose(v, isFloat, out ulong f, out int e, out bool unequal, out bool even);
            BigNum r, s, mPlus, mMinus;
            if (e >= 0)
            {
                if (!unequal)
                {
                    r = new BigNum(f); r.ShiftLeft(e + 1);
                    s = new BigNum(2);
                    mPlus = new BigNum(1); mPlus.ShiftLeft(e);
                    mMinus = mPlus.Clone();
                }
                else
                {
                    r = new BigNum(f); r.ShiftLeft(e + 2);
                    s = new BigNum(4);
                    mPlus = new BigNum(1); mPlus.ShiftLeft(e + 1);
                    mMinus = new BigNum(1); mMinus.ShiftLeft(e);
                }
            }
            else
            {
                if (!unequal)
                {
                    r = new BigNum(f); r.ShiftLeft(1);
                    s = new BigNum(1); s.ShiftLeft(1 - e);
                    mPlus = new BigNum(1); mMinus = new BigNum(1);
                }
                else
                {
                    r = new BigNum(f); r.ShiftLeft(2);
                    s = new BigNum(1); s.ShiftLeft(2 - e);
                    mPlus = new BigNum(2); mMinus = new BigNum(1);
                }
            }
            // estimate k = ceil(log10(v)), then fix up
            int k = (int)Rt.Ceiling(Log10Approx(v) - 1e-10);
            if (k >= 0) s.MulPow10(k);
            else { r.MulPow10(-k); mPlus.MulPow10(-k); mMinus.MulPow10(-k); }
            // high = r + m+ ; ensure high < s (or <= when not even)
            while (true)
            {
                int c = BigNum.CompareSum(r, mPlus, s);
                if (c > 0 || (even && c == 0)) { s.MulSmall(10); k++; }
                else break;
            }
            while (true)
            {
                var r10 = r.Clone(); r10.MulSmall(10);
                var m10 = mPlus.Clone(); m10.MulSmall(10);
                int c = BigNum.CompareSum(r10, m10, s);
                if (c < 0 || (!even && c == 0)) { r = r10; mPlus = m10; mMinus.MulSmall(10); k--; }
                else break;
            }
            var digits = new byte[32];
            int n = 0;
            while (true)
            {
                r.MulSmall(10); mPlus.MulSmall(10); mMinus.MulSmall(10);
                int d = r.DivRemSmallQuotient(s);
                int cl = BigNum.Compare(r, mMinus);
                bool tc1 = even ? cl <= 0 : cl < 0;
                int ch = BigNum.CompareSum(r, mPlus, s);
                bool tc2 = even ? ch >= 0 : ch > 0;
                if (!tc1 && !tc2) { digits[n++] = (byte)d; continue; }
                if (tc1 && !tc2) digits[n++] = (byte)d;
                else if (!tc1 && tc2) digits[n++] = (byte)(d + 1);
                else
                {
                    var r2 = r.Clone(); r2.ShiftLeft(1);
                    int cmp = BigNum.Compare(r2, s);
                    digits[n++] = (byte)(cmp < 0 || (cmp == 0 && (d & 1) == 0) ? d : d + 1);
                }
                break;
            }
            return new Digits { D = digits, Count = n, Exp = k - 1 };
        }

        static double Log10Approx(double v)
        {
            long b = Rt.F64Bits(v);
            int bexp = (int)((b >> 52) & 0x7FF);
            if (bexp == 0) return (-1074 + 52) * 0.30102999566398119521 - 16; // subnormal: rough
            double m = Rt.F64FromBits((b & 0xFFFFFFFFFFFFFL) | 0x3FF0000000000000L); // [1,2)
            return (bexp - 1023) * 0.30102999566398119521 + (m - 1) * 0.4342944819;
        }

        /// <summary>All exact decimal digits of v &gt; 0.</summary>
        static Digits Exact(double v, bool isFloat)
        {
            Decompose(v, isFloat, out ulong f, out int e, out _, out _);
            var n = new BigNum(f);
            int exp10;
            string s;
            if (e >= 0) { n.ShiftLeft(e); s = n.ToDecimal(); exp10 = s.Length - 1; }
            else { n.MulPow5(-e); s = n.ToDecimal(); exp10 = s.Length - 1 + e; }
            int len = s.Length;
            while (len > 1 && s[len - 1] == '0') len--;
            var d = new byte[len + 1];
            for (int i = 0; i < len; i++) d[i] = (byte)(s[i] - '0');
            return new Digits { D = d, Count = len, Exp = exp10 };
        }

        /// <summary>Rounds exact digits to keep significant digits (ties to even). keep may be ≤ 0.</summary>
        static Digits Round(Digits x, int keep)
        {
            if (keep >= x.Count) return x;
            bool up;
            if (keep < 0) up = false;
            else
            {
                int first = x.D[keep];
                bool restNonZero = false;
                for (int i = keep + 1; i < x.Count; i++) if (x.D[i] != 0) { restNonZero = true; break; }
                if (first > 5 || (first == 5 && restNonZero)) up = true;
                else if (first == 5) up = keep > 0 ? (x.D[keep - 1] & 1) == 1 : false;
                else up = false;
            }
            if (keep <= 0)
            {
                if (up) return new Digits { D = new byte[] { 1, 0 }, Count = 1, Exp = x.Exp + 1 - keep };
                return new Digits { D = new byte[] { 0, 0 }, Count = 0, Exp = x.Exp };
            }
            var d = new byte[keep + 1];
            for (int i = 0; i < keep; i++) d[i] = x.D[i];
            int exp = x.Exp;
            int count = keep;
            if (up)
            {
                int i = keep - 1;
                while (i >= 0 && d[i] == 9) { d[i] = 0; i--; }
                if (i >= 0) d[i]++;
                else
                {
                    // 999 -> 1000
                    for (int j = keep; j > 0; j--) d[j] = d[j - 1];
                    d[0] = 1; exp++;
                }
            }
            while (count > 0 && d[count - 1] == 0) count--;
            return new Digits { D = d, Count = count, Exp = exp };
        }

        // ------------------------------------------------------------------ floating point: formatting
        public static string FormatDouble(double v, string format, bool isFloat)
        {
            if (double.IsNaN(v)) return "NaN";
            bool neg = Rt.F64Bits(v) < 0;
            if (double.IsInfinity(v)) return neg ? "-Infinity" : "Infinity";
            ParseFormat(format, out char kind, out int prec);
            double a = neg ? -v : v;
            switch (kind)
            {
                case 'R': case 'r':
                    return Sign(neg, General(a, isFloat, -1, 'E'));
                case 'G': case 'g':
                    return Sign(neg, General(a, isFloat, prec, kind == 'g' ? 'e' : 'E'));
                case 'F': case 'f':
                    return Sign(neg, Fixed(a, isFloat, prec < 0 ? 2 : prec, false));
                case 'N': case 'n':
                    return Sign(neg, Fixed(a, isFloat, prec < 0 ? 2 : prec, true));
                case 'E': case 'e':
                    return Sign(neg, Scientific(a, isFloat, prec < 0 ? 6 : prec, kind, 3));
                case 'P': case 'p':
                    return Sign(neg, string.Concat2(Fixed(a, isFloat, prec < 0 ? 2 : prec, true, 2), " %"));
                case 'C': case 'c':
                    return neg ? string.Concat2("-¤", Fixed(a, isFloat, prec < 0 ? 2 : prec, true)) : string.Concat2("¤", Fixed(a, isFloat, prec < 0 ? 2 : prec, true));
                case '\0':
                    return FormatCustom(v, format, isFloat);
            }
            throw new FormatException("Format specifier was invalid.");
        }

        static string Sign(bool neg, string s) => neg ? Neg(s) : s;

        static Digits DigitsOf(double a, bool isFloat) => a == 0 ? new Digits { D = new byte[] { 0, 0 }, Count = 0, Exp = 0 } : Exact(a, isFloat);

        static string General(double a, bool isFloat, int prec, char e)
        {
            Digits d;
            int p;
            if (prec <= 0)
            {
                if (a == 0) return "0";
                d = Shortest(a, isFloat);
                p = isFloat ? 9 : 17;
            }
            else
            {
                if (a == 0) return "0";
                d = Round(Exact(a, isFloat), prec);
                p = prec;
                if (d.Count == 0) return "0";
            }
            if (d.Exp > -5 && d.Exp < p) return FixedDigits(d, 0, false, true);
            return SciDigits(d, -1, e, 2);
        }

        static string Fixed(double a, bool isFloat, int decimals, bool group, int shift = 0)
        {
            var d = DigitsOf(a, isFloat);
            d.Exp += shift;
            if (d.Count > 0) d = Round(d, d.Exp + 1 + decimals);
            return FixedDigits(d, decimals, group, false);
        }

        /// <summary>Writes digits in fixed notation with exactly `decimals` fraction digits (or as many as needed when trim).</summary>
        static string FixedDigits(Digits d, int decimals, bool group, bool trim)
        {
            var sb = new Text.StringBuilder();
            // integer part
            string ip;
            if (d.Count == 0 || d.Exp < 0) ip = "0";
            else
            {
                var ib = new Text.StringBuilder();
                for (int i = 0; i <= d.Exp; i++) ib.Append((char)('0' + (i < d.Count ? d.D[i] : 0)));
                ip = ib.ToString();
            }
            sb.Append(group ? Group(ip) : ip);
            int fracDigits = trim ? (d.Count - 1 - d.Exp > 0 ? d.Count - 1 - d.Exp : 0) : decimals;
            if (fracDigits > 0)
            {
                sb.Append('.');
                for (int j = 1; j <= fracDigits; j++)
                {
                    int idx = d.Exp + j;
                    sb.Append((char)('0' + (idx >= 0 && idx < d.Count ? d.D[idx] : 0)));
                }
            }
            return sb.ToString();
        }

        static string Scientific(double a, bool isFloat, int decimals, char e, int minExpDigits)
        {
            var d = DigitsOf(a, isFloat);
            if (d.Count > 0) d = Round(d, decimals + 1);
            return SciDigits(d, decimals, e, minExpDigits);
        }

        /// <summary>d.ddd E+xx ; decimals &lt; 0 means as many digits as there are.</summary>
        static string SciDigits(Digits d, int decimals, char e, int minExpDigits)
        {
            var sb = new Text.StringBuilder();
            sb.Append((char)('0' + (d.Count > 0 ? d.D[0] : 0)));
            int frac = decimals < 0 ? d.Count - 1 : decimals;
            if (frac > 0)
            {
                sb.Append('.');
                for (int i = 1; i <= frac; i++) sb.Append((char)('0' + (i < d.Count ? d.D[i] : 0)));
            }
            sb.Append(e);
            int x = d.Count == 0 ? 0 : d.Exp;
            sb.Append(x < 0 ? '-' : '+');
            string xs = Int32ToString(x < 0 ? -x : x);
            for (int i = xs.Length; i < minExpDigits; i++) sb.Append('0');
            sb.Append(xs);
            return sb.ToString();
        }

        /// <summary>Custom numeric formats: 0 # . , % and literals (one section).</summary>
        public static string FormatCustom(double v, string format, bool isFloat)
        {
            int semi = format.IndexOf(';');
            if (semi >= 0)
            {
                string pos = format.Substring(0, semi);
                string rest = format.Substring(semi + 1);
                int semi2 = rest.IndexOf(';');
                string negf = semi2 >= 0 ? rest.Substring(0, semi2) : rest;
                if (v < 0 && negf.Length > 0) return FormatCustom(-v, negf, isFloat);
                if (v == 0 && semi2 >= 0) return FormatCustom(v, rest.Substring(semi2 + 1), isFloat);
                format = pos;
            }
            bool neg = v < 0 || (v == 0 && Rt.F64Bits(v) < 0 && false);
            double a = v < 0 ? -v : v;
            int intZeros = 0, fracZeros = 0;
            bool seenDot = false, group = false, percent = false;
            int firstDigit = -1, lastDigit = -1;
            for (int i = 0; i < format.Length; i++)
            {
                char c = format[i];
                if (c == '0' || c == '#')
                {
                    if (firstDigit < 0) firstDigit = i;
                    lastDigit = i;
                    if (seenDot) fracZeros++;
                    else if (c == '0') intZeros++;
                }
                else if (c == '.' && !seenDot) seenDot = true;
                else if (c == ',' && !seenDot && firstDigit >= 0) group = true;
                else if (c == '%') percent = true;
            }
            int decimals = fracZeros;
            var d = DigitsOf(a, isFloat);
            if (percent) d.Exp += 2;
            if (d.Count > 0) d = Round(d, d.Exp + 1 + decimals);
            string num = FixedDigits(d, decimals, false, false);
            // drop optional trailing fraction digits beyond the required zeros
            int dot = num.IndexOf('.');
            string ip = dot >= 0 ? num.Substring(0, dot) : num;
            string fp = dot >= 0 ? num.Substring(dot + 1) : "";
            int minFrac = 0;
            if (seenDot)
            {
                int k = format.IndexOf('.') + 1, idx = 0;
                for (; k < format.Length; k++) { if (format[k] == '0') minFrac = idx + 1; if (format[k] == '0' || format[k] == '#') idx++; }
            }
            int keepFrac = fp.Length;
            while (keepFrac > minFrac && fp[keepFrac - 1] == '0') keepFrac--;
            fp = fp.Substring(0, keepFrac);
            if (ip == "0" && intZeros == 0) ip = "";
            if (ip.Length < intZeros) ip = ip.PadLeft(intZeros, '0');
            if (group) ip = Group(ip);
            string body = fp.Length > 0 ? string.Concat2(string.Concat2(ip, "."), fp) : ip;
            if (body.Length == 0) body = intZeros == 0 && fp.Length == 0 ? "" : body;
            bool isZero = d.Count == 0;
            var sb = new Text.StringBuilder();
            if (neg && !isZero) sb.Append('-');
            for (int i = 0; i < format.Length; i++)
            {
                char c = format[i];
                if (i == firstDigit) sb.Append(body);
                if (i >= firstDigit && i <= lastDigit && firstDigit >= 0) continue;
                if (c == ',' || (c == '.' && i > firstDigit && i < lastDigit)) continue;
                if (c == '\'' || c == '"')
                {
                    int j = format.IndexOf(c, i + 1);
                    if (j < 0) j = format.Length;
                    sb.Append(format.Substring(i + 1, j - i - 1));
                    i = j;
                    continue;
                }
                if (c == '\\' && i + 1 < format.Length) { sb.Append(format[++i]); continue; }
                sb.Append(c);
            }
            if (firstDigit < 0) return sb.ToString();
            return sb.ToString();
        }

        // ------------------------------------------------------------------ parsing
        static bool IsWhite(char c) => c == ' ' || (c >= '\t' && c <= '\r');

        static FormatException BadFormat(string s) => new FormatException(string.Concat2(string.Concat2("The input string '", s), "' was not in a correct format."));

        public static bool TryParseInt64(string s, long min, ulong max, out long result, out bool overflow)
        {
            result = 0; overflow = false;
            if (s == null) return false;
            int i = 0, n = s.Length;
            while (i < n && IsWhite(s[i])) i++;
            while (n > i && IsWhite(s[n - 1])) n--;
            bool neg = false;
            if (i < n && (s[i] == '-' || s[i] == '+')) { neg = s[i] == '-'; i++; }
            if (i >= n) return false;
            ulong acc = 0;
            for (; i < n; i++)
            {
                char c = s[i];
                if (c < '0' || c > '9') return false;
                ulong next = acc * 10 + (ulong)(c - '0');
                if (acc > 1844674407370955161UL || next < acc) overflow = true;
                acc = next;
            }
            if (overflow) return false;
            if (neg)
            {
                if (min == 0) { if (acc != 0) { overflow = true; return false; } result = 0; return true; }
                if (acc > (ulong)(-(min + 1)) + 1) { overflow = true; return false; }
                result = acc == (ulong)(-(min + 1)) + 1 ? min : -(long)acc;
                return true;
            }
            if (acc > max) { overflow = true; return false; }
            result = (long)acc;
            return true;
        }

        public static long ParseInt64(string s, long min, ulong max)
        {
            if (s == null) throw new ArgumentNullException("s");
            if (TryParseInt64(s, min, max, out long r, out bool ov)) return r;
            if (ov) throw new OverflowException(max == 255 ? "Value was either too large or too small for an unsigned byte." : max <= 0x7FFFFFFF && min < 0 ? "Value was either too large or too small for an Int32." : "Value was either too large or too small for an Int64.");
            throw BadFormat(s);
        }

        public static bool TryParseUInt64(string s, out ulong result)
        {
            result = 0;
            if (s == null) return false;
            int i = 0, n = s.Length;
            while (i < n && IsWhite(s[i])) i++;
            while (n > i && IsWhite(s[n - 1])) n--;
            if (i < n && s[i] == '+') i++;
            if (i >= n) return false;
            ulong acc = 0;
            for (; i < n; i++)
            {
                char c = s[i];
                if (c < '0' || c > '9') return false;
                ulong next = acc * 10 + (ulong)(c - '0');
                if (acc > 1844674407370955161UL || next < acc) return false;
                acc = next;
            }
            result = acc;
            return true;
        }

        public static bool TryParseDouble(string s, out double result)
        {
            result = 0;
            if (s == null) return false;
            int i = 0, n = s.Length;
            while (i < n && IsWhite(s[i])) i++;
            while (n > i && IsWhite(s[n - 1])) n--;
            bool neg = false;
            if (i < n && (s[i] == '-' || s[i] == '+')) { neg = s[i] == '-'; i++; }
            string rest = s.Substring(i, n - i);
            if (rest == "NaN") { result = double.NaN; return true; }
            if (rest == "Infinity" || rest == "∞") { result = neg ? double.NegativeInfinity : double.PositiveInfinity; return true; }
            var digits = new Text.StringBuilder();
            int dexp = 0;
            bool any = false, dot = false;
            for (; i < n; i++)
            {
                char c = s[i];
                if (c >= '0' && c <= '9')
                {
                    any = true;
                    if (digits.Length == 0 && c == '0') { if (dot) dexp--; continue; }
                    if (digits.Length < 800) { digits.Append(c); if (dot) dexp--; }
                    else if (!dot) dexp++;
                }
                else if (c == '.' && !dot) dot = true;
                else break;
            }
            if (!any) return false;
            if (i < n && (s[i] == 'e' || s[i] == 'E'))
            {
                i++;
                bool eneg = false;
                if (i < n && (s[i] == '-' || s[i] == '+')) { eneg = s[i] == '-'; i++; }
                if (i >= n) return false;
                int ev = 0;
                for (; i < n; i++)
                {
                    char c = s[i];
                    if (c < '0' || c > '9') return false;
                    if (ev < 100000) ev = ev * 10 + (c - '0');
                }
                dexp += eneg ? -ev : ev;
            }
            if (i != n) return false;
            result = DecimalToDouble(digits.ToString(), dexp);
            if (neg) result = -result;
            return true;
        }

        /// <summary>Correctly rounded value of digits × 10^exp.</summary>
        static double DecimalToDouble(string digits, int exp)
        {
            if (digits.Length == 0) return 0;
            if (digits.Length + exp > 310) return double.PositiveInfinity;
            if (digits.Length + exp < -330) return 0;
            var num = new BigNum(0);
            for (int i = 0; i < digits.Length; i++) { num.MulSmall(10); num.Add(new BigNum((ulong)(digits[i] - '0'))); }
            BigNum den = new BigNum(1);
            if (exp >= 0) num.MulPow10(exp); else den.MulPow10(-exp);
            // scale so that num/den has 64+ significant bits: q = floor(num * 2^k / den)
            int k = den.BitLength() - num.BitLength() + 66;
            if (k > 0) num.ShiftLeft(k); else if (k < 0) den.ShiftLeft(-k);
            // long division (quotient has ~66 bits)
            var q = new BigNum(0);
            int qbits = num.BitLength() - den.BitLength() + 1;
            var shifted = den.Clone(); shifted.ShiftLeft(qbits > 0 ? qbits - 1 : 0);
            ulong qhi = 0, qlo = 0;
            for (int b = (qbits > 0 ? qbits - 1 : 0); b >= 0; b--)
            {
                bool bit = BigNum.Compare(num, shifted) >= 0;
                if (bit) num.Sub(shifted);
                qhi = (qhi << 1) | (qlo >> 63); qlo = (qlo << 1) | (bit ? 1UL : 0);
                shifted.DivRemSmall(2);
            }
            bool sticky = !num.IsZero;
            // normalize the quotient (qhi:qlo) to 64 bits plus sticky
            int binExp = -k; // value = q * 2^binExp
            while (qhi != 0) { sticky |= (qlo & 1) != 0; qlo = (qlo >> 1) | (qhi << 63); qhi >>= 1; binExp++; }
            if (qlo == 0) return 0;
            int lz = (int)Rt.I64Clz((long)qlo);
            qlo <<= lz; binExp -= lz;
            // qlo has its top bit set; value = qlo * 2^binExp
            int e = binExp + 63; // exponent of the top bit
            int mantBits = 53;
            if (e < -1022) mantBits = 53 - (-1022 - e);
            if (mantBits <= 0)
            {
                if (mantBits == 0 && (qlo > 0x8000000000000000UL || (qlo == 0x8000000000000000UL && sticky))) return double.Epsilon;
                return 0;
            }
            int drop = 64 - mantBits;
            ulong m = qlo >> drop;
            ulong rem = drop == 64 ? qlo : qlo & ((1UL << drop) - 1);
            ulong half = 1UL << (drop - 1);
            if (rem > half || (rem == half && (sticky || (m & 1) == 1))) { m++; if (m == (1UL << mantBits)) { m >>= 1; e++; if (mantBits < 53) mantBits++; } }
            if (e > 1023) return double.PositiveInfinity;
            if (mantBits < 53 || e < -1022) return Rt.F64FromBits((long)m);
            long bits = ((long)(e + 1023) << 52) | (long)(m & 0xFFFFFFFFFFFFFUL);
            return Rt.F64FromBits(bits);
        }

        public static double ParseDouble(string s)
        {
            if (s == null) throw new ArgumentNullException("s");
            if (TryParseDouble(s, out double d)) return d;
            throw BadFormat(s);
        }
    }
}
