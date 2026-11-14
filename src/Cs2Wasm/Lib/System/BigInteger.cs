#pragma warning disable CS0436, CS0660, CS0661
// System.Numerics.BigInteger: sign and magnitude (little-endian 32-bit limbs), schoolbook arithmetic.
using System.Globalization;
using System.Text;

namespace System.Numerics
{
    public readonly struct BigInteger : IComparable, IComparable<BigInteger>, IEquatable<BigInteger>, IFormattable
    {
        readonly int _sign;      // -1, 0, 1
        readonly uint[] _mag;    // no leading zero limbs; null for zero

        BigInteger(int sign, uint[] mag)
        {
            int n = mag == null ? 0 : mag.Length;
            while (n > 0 && mag[n - 1] == 0) n--;
            if (n == 0) { _sign = 0; _mag = null; return; }
            if (n != mag.Length) { var m = new uint[n]; Array.Copy(mag, m, n); mag = m; }
            _sign = sign; _mag = mag;
        }

        public BigInteger(int value) : this((long)value) { }
        public BigInteger(long value)
        {
            if (value == 0) { _sign = 0; _mag = null; return; }
            ulong u = value < 0 ? (ulong)(-(value + 1)) + 1 : (ulong)value;
            _sign = value < 0 ? -1 : 1;
            _mag = (u >> 32) != 0 ? new[] { (uint)u, (uint)(u >> 32) } : new[] { (uint)u };
        }
        public BigInteger(ulong value)
        {
            if (value == 0) { _sign = 0; _mag = null; return; }
            _sign = 1;
            _mag = (value >> 32) != 0 ? new[] { (uint)value, (uint)(value >> 32) } : new[] { (uint)value };
        }
        public BigInteger(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) throw new OverflowException("Value was either too large or too small for a BigInteger.");
            value = Math.Truncate(value);
            if (value == 0) { _sign = 0; _mag = null; return; }
            bool neg = value < 0; if (neg) value = -value;
            var limbs = new System.Collections.Generic.List<uint>();
            while (value >= 1) { double q = Math.Floor(value / 4294967296.0); limbs.Add((uint)(value - q * 4294967296.0)); value = q; }
            var b = new BigInteger(neg ? -1 : 1, limbs.ToArray());
            _sign = b._sign; _mag = b._mag;
        }

        public static BigInteger Zero => default;
        public static BigInteger One => new BigInteger(1);
        public static BigInteger MinusOne => new BigInteger(-1);
        public int Sign => _sign;
        public bool IsZero => _sign == 0;
        public bool IsOne => _sign == 1 && _mag.Length == 1 && _mag[0] == 1;
        public bool IsEven => _sign == 0 || (_mag[0] & 1) == 0;
        public bool IsPowerOfTwo
        {
            get
            {
                if (_sign != 1) return false;
                for (int i = 0; i < _mag.Length - 1; i++) if (_mag[i] != 0) return false;
                uint top = _mag[_mag.Length - 1];
                return (top & (top - 1)) == 0;
            }
        }

        static int CompareMag(uint[] a, uint[] b)
        {
            int la = a?.Length ?? 0, lb = b?.Length ?? 0;
            if (la != lb) return la < lb ? -1 : 1;
            for (int i = la - 1; i >= 0; i--) if (a[i] != b[i]) return a[i] < b[i] ? -1 : 1;
            return 0;
        }
        static uint[] AddMag(uint[] a, uint[] b)
        {
            if (a == null) return b; if (b == null) return a;
            if (a.Length < b.Length) { var t = a; a = b; b = t; }
            var r = new uint[a.Length + 1];
            ulong carry = 0;
            for (int i = 0; i < a.Length; i++) { ulong s = (ulong)a[i] + (i < b.Length ? b[i] : 0u) + carry; r[i] = (uint)s; carry = s >> 32; }
            r[a.Length] = (uint)carry;
            return r;
        }
        static uint[] SubMag(uint[] a, uint[] b) // a >= b
        {
            if (b == null) return a;
            var r = new uint[a.Length];
            long borrow = 0;
            for (int i = 0; i < a.Length; i++)
            {
                long d = (long)a[i] - (i < b.Length ? b[i] : 0u) - borrow;
                if (d < 0) { d += 4294967296L; borrow = 1; } else borrow = 0;
                r[i] = (uint)d;
            }
            return r;
        }
        static uint[] MulMag(uint[] a, uint[] b)
        {
            if (a == null || b == null) return null;
            var r = new uint[a.Length + b.Length];
            for (int i = 0; i < a.Length; i++)
            {
                ulong carry = 0;
                for (int j = 0; j < b.Length; j++) { ulong t = (ulong)a[i] * b[j] + r[i + j] + carry; r[i + j] = (uint)t; carry = t >> 32; }
                int k = i + b.Length;
                while (carry != 0) { ulong t = (ulong)r[k] + carry; r[k] = (uint)t; carry = t >> 32; k++; }
            }
            return r;
        }
        /// <summary>Long division by bits: quotient and remainder of magnitudes.</summary>
        static void DivMag(uint[] a, uint[] b, out uint[] q, out uint[] rem)
        {
            if (CompareMag(a, b) < 0) { q = null; rem = a; return; }
            if (b.Length == 1)
            {
                var qq = new uint[a.Length]; ulong r = 0;
                for (int i = a.Length - 1; i >= 0; i--) { ulong cur = (r << 32) | a[i]; qq[i] = (uint)(cur / b[0]); r = cur % b[0]; }
                q = qq; rem = r == 0 ? null : new[] { (uint)r };
                return;
            }
            int bits = a.Length * 32;
            var quo = new uint[a.Length];
            uint[] cur2 = null;
            for (int i = bits - 1; i >= 0; i--)
            {
                cur2 = ShiftLeft1(cur2, (a[i >> 5] >> (i & 31)) & 1);
                if (CompareMag(Trim(cur2), b) >= 0) { cur2 = SubMag(Trim(cur2), b); quo[i >> 5] |= 1u << (i & 31); }
            }
            q = quo; rem = Trim(cur2);
        }
        static uint[] Trim(uint[] m)
        {
            if (m == null) return null;
            int n = m.Length; while (n > 0 && m[n - 1] == 0) n--;
            if (n == 0) return null;
            if (n == m.Length) return m;
            var r = new uint[n]; Array.Copy(m, r, n); return r;
        }
        static uint[] ShiftLeft1(uint[] m, uint bit)
        {
            int n = m?.Length ?? 0;
            var r = new uint[n + 1];
            uint carry = bit;
            for (int i = 0; i < n; i++) { r[i] = (m[i] << 1) | carry; carry = m[i] >> 31; }
            r[n] = carry;
            return r;
        }

        public static BigInteger operator +(BigInteger a, BigInteger b)
        {
            if (a._sign == 0) return b; if (b._sign == 0) return a;
            if (a._sign == b._sign) return new BigInteger(a._sign, AddMag(a._mag, b._mag));
            int c = CompareMag(a._mag, b._mag);
            if (c == 0) return Zero;
            return c > 0 ? new BigInteger(a._sign, SubMag(a._mag, b._mag)) : new BigInteger(b._sign, SubMag(b._mag, a._mag));
        }
        public static BigInteger operator -(BigInteger a) => new BigInteger(-a._sign, a._mag);
        public static BigInteger operator +(BigInteger a) => a;
        public static BigInteger operator -(BigInteger a, BigInteger b) => a + -b;
        public static BigInteger operator *(BigInteger a, BigInteger b) => a._sign == 0 || b._sign == 0 ? Zero : new BigInteger(a._sign * b._sign, MulMag(a._mag, b._mag));
        public static BigInteger operator /(BigInteger a, BigInteger b)
        {
            if (b._sign == 0) throw new DivideByZeroException();
            if (a._sign == 0) return Zero;
            DivMag(a._mag, b._mag, out var q, out _);
            return new BigInteger(a._sign * b._sign, q);
        }
        public static BigInteger operator %(BigInteger a, BigInteger b)
        {
            if (b._sign == 0) throw new DivideByZeroException();
            if (a._sign == 0) return Zero;
            DivMag(a._mag, b._mag, out _, out var r);
            return new BigInteger(a._sign, r);
        }
        public static BigInteger operator ++(BigInteger a) => a + One;
        public static BigInteger operator --(BigInteger a) => a - One;
        public static BigInteger operator <<(BigInteger a, int shift)
        {
            if (shift < 0) return a >> -shift;
            if (a._sign == 0 || shift == 0) return a;
            int words = shift >> 5, bits = shift & 31;
            var r = new uint[a._mag.Length + words + 1];
            for (int i = 0; i < a._mag.Length; i++)
            {
                ulong v = (ulong)a._mag[i] << bits;
                r[i + words] |= (uint)v;
                r[i + words + 1] |= (uint)(v >> 32);
            }
            return new BigInteger(a._sign, r);
        }
        public static BigInteger operator >>(BigInteger a, int shift)
        {
            if (shift < 0) return a << -shift;
            if (a._sign == 0 || shift == 0) return a;
            if (a._sign < 0) { var q = DivRem(a, Pow(2, shift), out var rem); return rem.IsZero ? q : q - One; }
            int words = shift >> 5, bits = shift & 31;
            if (words >= a._mag.Length) return Zero;
            var r = new uint[a._mag.Length - words];
            for (int i = 0; i < r.Length; i++)
            {
                ulong v = a._mag[i + words];
                if (i + words + 1 < a._mag.Length) v |= (ulong)a._mag[i + words + 1] << 32;
                r[i] = (uint)(v >> bits);
            }
            return new BigInteger(1, r);
        }

        public static int Compare(BigInteger a, BigInteger b) => a.CompareTo(b);
        public int CompareTo(BigInteger other)
        {
            if (_sign != other._sign) return _sign < other._sign ? -1 : 1;
            int c = CompareMag(_mag, other._mag);
            return _sign >= 0 ? c : -c;
        }
        public int CompareTo(object obj) => obj is BigInteger b ? CompareTo(b) : obj == null ? 1 : throw new ArgumentException("The parameter must be a BigInteger.");
        public bool Equals(BigInteger other) => CompareTo(other) == 0;
        public override bool Equals(object obj) => obj is BigInteger b && Equals(b);
        public override int GetHashCode() { int h = _sign; if (_mag != null) foreach (var w in _mag) h = h * 31 + (int)w; return h; }
        public static bool operator ==(BigInteger a, BigInteger b) => a.CompareTo(b) == 0;
        public static bool operator !=(BigInteger a, BigInteger b) => a.CompareTo(b) != 0;
        public static bool operator <(BigInteger a, BigInteger b) => a.CompareTo(b) < 0;
        public static bool operator >(BigInteger a, BigInteger b) => a.CompareTo(b) > 0;
        public static bool operator <=(BigInteger a, BigInteger b) => a.CompareTo(b) <= 0;
        public static bool operator >=(BigInteger a, BigInteger b) => a.CompareTo(b) >= 0;

        public static implicit operator BigInteger(int v) => new BigInteger(v);
        public static implicit operator BigInteger(long v) => new BigInteger(v);
        public static implicit operator BigInteger(uint v) => new BigInteger((ulong)v);
        public static implicit operator BigInteger(ulong v) => new BigInteger(v);
        public static explicit operator BigInteger(double v) => new BigInteger(v);
        public static explicit operator int(BigInteger v) { long l = (long)v; if (l < int.MinValue || l > int.MaxValue) throw new OverflowException("Value was either too large or too small for an Int32."); return (int)l; }
        public static explicit operator long(BigInteger v)
        {
            if (v._sign == 0) return 0;
            if (v._mag.Length > 2) throw new OverflowException("Value was either too large or too small for an Int64.");
            ulong u = v._mag[0] | (v._mag.Length > 1 ? (ulong)v._mag[1] << 32 : 0);
            if (v._sign > 0) { if (u > long.MaxValue) throw new OverflowException("Value was either too large or too small for an Int64."); return (long)u; }
            if (u > 9223372036854775808UL) throw new OverflowException("Value was either too large or too small for an Int64.");
            return (long)(0 - u);
        }
        public static explicit operator ulong(BigInteger v)
        {
            if (v._sign == 0) return 0;
            if (v._sign < 0 || v._mag.Length > 2) throw new OverflowException("Value was either too large or too small for a UInt64.");
            return v._mag[0] | (v._mag.Length > 1 ? (ulong)v._mag[1] << 32 : 0);
        }
        public static explicit operator double(BigInteger v)
        {
            if (v._sign == 0) return 0;
            double r = 0;
            for (int i = v._mag.Length - 1; i >= 0; i--) r = r * 4294967296.0 + v._mag[i];
            return v._sign * r;
        }

        public static BigInteger Abs(BigInteger v) => v._sign < 0 ? -v : v;
        public static BigInteger Negate(BigInteger v) => -v;
        public static BigInteger Add(BigInteger a, BigInteger b) => a + b;
        public static BigInteger Subtract(BigInteger a, BigInteger b) => a - b;
        public static BigInteger Multiply(BigInteger a, BigInteger b) => a * b;
        public static BigInteger Divide(BigInteger a, BigInteger b) => a / b;
        public static BigInteger Remainder(BigInteger a, BigInteger b) => a % b;
        public static BigInteger DivRem(BigInteger a, BigInteger b, out BigInteger remainder) { remainder = a % b; return a / b; }
        public static BigInteger Max(BigInteger a, BigInteger b) => a >= b ? a : b;
        public static BigInteger Min(BigInteger a, BigInteger b) => a <= b ? a : b;
        public static BigInteger Pow(BigInteger value, int exponent)
        {
            if (exponent < 0) throw new ArgumentOutOfRangeException("exponent", "The number must be greater than or equal to zero.");
            BigInteger r = One, b = value;
            while (exponent > 0) { if ((exponent & 1) != 0) r *= b; b *= b; exponent >>= 1; }
            return r;
        }
        public static BigInteger ModPow(BigInteger value, BigInteger exponent, BigInteger modulus)
        {
            if (exponent._sign < 0) throw new ArgumentOutOfRangeException("exponent", "The number must be greater than or equal to zero.");
            BigInteger r = One % modulus, b = value % modulus, e = exponent;
            while (!e.IsZero) { if (!e.IsEven) r = r * b % modulus; b = b * b % modulus; e >>= 1; }
            return r;
        }
        public static BigInteger GreatestCommonDivisor(BigInteger a, BigInteger b)
        {
            a = Abs(a); b = Abs(b);
            while (!b.IsZero) { var t = a % b; a = b; b = t; }
            return a;
        }
        public static double Log(BigInteger value) => Math.Log((double)value);
        public static double Log10(BigInteger value) => Math.Log10((double)value);

        public override string ToString()
        {
            if (_sign == 0) return "0";
            var digits = new StringBuilder();
            var mag = (uint[])_mag.Clone();
            int n = mag.Length;
            while (n > 0)
            {
                ulong r = 0;
                for (int i = n - 1; i >= 0; i--) { ulong cur = (r << 32) | mag[i]; mag[i] = (uint)(cur / 1000000000); r = cur % 1000000000; }
                while (n > 0 && mag[n - 1] == 0) n--;
                var chunk = ((uint)r).ToString();
                if (n > 0) chunk = chunk.PadLeft(9, '0');
                digits.Insert(0, chunk);
            }
            if (_sign < 0) digits.Insert(0, '-');
            return digits.ToString();
        }
        public string ToString(string format) => ToString(format, null);
        public string ToString(string format, IFormatProvider provider)
        {
            if (string.IsNullOrEmpty(format) || format == "G" || format == "g" || format == "D" || format == "d" || format == "R" || format == "r") return ToString();
            if (format[0] == 'N' || format[0] == 'n')
            {
                var s = Abs(this).ToString();
                var sb = new StringBuilder();
                for (int i = 0; i < s.Length; i++) { if (i > 0 && (s.Length - i) % 3 == 0) sb.Append(','); sb.Append(s[i]); }
                int dec = format.Length > 1 ? int.Parse(format.Substring(1)) : 2;
                if (dec > 0) sb.Append('.').Append('0', dec);
                return (_sign < 0 ? "-" : "") + sb.ToString();
            }
            if (format[0] == 'X' || format[0] == 'x')
            {
                if (_sign == 0) return "0";
                var sb = new StringBuilder();
                for (int i = _mag.Length - 1; i >= 0; i--) sb.Append(_mag[i].ToString(i == _mag.Length - 1 ? (format[0] == 'X' ? "X" : "x") : (format[0] == 'X' ? "X8" : "x8")));
                var s = sb.ToString();
                if (s[0] >= '8' && !(s[0] >= '0' && s[0] <= '7')) s = "0" + s;
                return s;
            }
            return ToString();
        }

        public static BigInteger Parse(string value) => Parse(value, NumberStyles.Integer, null);
        public static BigInteger Parse(string value, IFormatProvider provider) => Parse(value, NumberStyles.Integer, provider);
        public static BigInteger Parse(string value, NumberStyles style, IFormatProvider provider = null)
        {
            if (!TryParse(value, out var r)) throw new FormatException("The value could not be parsed.");
            return r;
        }
        public static bool TryParse(string value, out BigInteger result)
        {
            result = Zero;
            if (value == null) return false;
            var s = value.Trim();
            if (s.Length == 0) return false;
            int i = 0; bool neg = false;
            if (s[0] == '-' || s[0] == '+') { neg = s[0] == '-'; i = 1; }
            if (i >= s.Length) return false;
            BigInteger acc = Zero, ten9 = new BigInteger(1000000000);
            int start = i;
            while (i < s.Length)
            {
                int len = Math.Min(9, s.Length - i);
                if ((s.Length - start) % 9 != 0 && i == start) len = (s.Length - start) % 9;
                uint chunk = 0;
                for (int k = 0; k < len; k++) { char c = s[i + k]; if (c < '0' || c > '9') return false; chunk = chunk * 10 + (uint)(c - '0'); }
                acc = acc * Pow(10, len) + chunk;
                i += len;
            }
            result = neg ? -acc : acc;
            return true;
        }
    }
}
