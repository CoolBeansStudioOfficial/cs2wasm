#pragma warning disable CS0436, CS0660, CS0661
// System.Decimal with .NET's representation (96-bit integer, scale 0..28, sign) and arithmetic rules: results keep
// the larger scale (addition) or the sum of scales (multiplication), are rounded half to even when they do not fit,
// and division gives the exact quotient or 28-29 significant digits. The arithmetic works on BigInteger.
using System.Globalization;
using System.Numerics;
using System.Text;
using Cs2Wasm.Runtime;

namespace System
{
    public readonly struct Decimal : IComparable, IComparable<decimal>, IEquatable<decimal>, IFormattable
    {
        // same layout as .NET: flags (scale in bits 16..23, sign in bit 31), hi, lo, mid
        readonly int _flags;
        readonly uint _hi, _lo, _mid;

        public const decimal Zero = 0m;
        public const decimal One = 1m;
        public const decimal MinusOne = -1m;
        public const decimal MaxValue = 79228162514264337593543950335m;
        public const decimal MinValue = -79228162514264337593543950335m;

        public Decimal(int lo, int mid, int hi, bool isNegative, byte scale)
        {
            if (scale > 28) throw new ArgumentOutOfRangeException("scale", "Decimal's scale value must be between 0 and 28, inclusive.");
            _lo = (uint)lo; _mid = (uint)mid; _hi = (uint)hi;
            _flags = (scale << 16) | (isNegative ? int.MinValue : 0);
        }
        public Decimal(int value) : this((long)value) { }
        public Decimal(uint value) : this((ulong)value) { }
        public Decimal(long value)
        {
            ulong u = value < 0 ? (ulong)(-(value + 1)) + 1 : (ulong)value;
            _lo = (uint)u; _mid = (uint)(u >> 32); _hi = 0;
            _flags = value < 0 ? int.MinValue : 0;
        }
        public Decimal(ulong value) { _lo = (uint)value; _mid = (uint)(value >> 32); _hi = 0; _flags = 0; }
        public Decimal(double value) { var d = FromDouble(value, 15); _lo = d._lo; _mid = d._mid; _hi = d._hi; _flags = d._flags; }
        public Decimal(float value) { var d = FromDouble(value, 7); _lo = d._lo; _mid = d._mid; _hi = d._hi; _flags = d._flags; }
        public Decimal(int[] bits)
        {
            if (bits == null || bits.Length != 4) throw new ArgumentException("Decimal constructor requires an array or span of four valid decimal bytes.");
            _lo = (uint)bits[0]; _mid = (uint)bits[1]; _hi = (uint)bits[2]; _flags = bits[3];
        }

        internal int Scale => (_flags >> 16) & 0xFF;
        internal bool IsNegative => _flags < 0;
        public byte ScaleByte => (byte)Scale;

        // ---------------- BigInteger bridge ----------------
        BigInteger Mantissa => ((BigInteger)_hi << 64) + ((BigInteger)_mid << 32) + _lo;
        static readonly BigInteger s_max96 = (BigInteger.One << 96) - BigInteger.One;

        static decimal Make(BigInteger mantissa, int scale, bool neg)
        {
            // mantissa >= 0; fit into 96 bits and scale 28, rounding half to even
            while (scale > 28 || mantissa > s_max96)
            {
                if (scale == 0) throw new OverflowException("Value was either too large or too small for a Decimal.");
                mantissa = DivRound(mantissa, 10);
                scale--;
            }
            var u = mantissa;
            var two32 = new BigInteger(4294967296L);
            uint lo = (uint)(ulong)(u % two32), mid = (uint)(ulong)((u >> 32) % two32), hi = (uint)(ulong)(u >> 64);
            return new decimal((int)lo, (int)mid, (int)hi, neg && !mantissa.IsZero, (byte)scale);
        }

        /// <summary>a / b rounded half to even (a, b positive).</summary>
        static BigInteger DivRound(BigInteger a, BigInteger b)
        {
            var q = BigInteger.DivRem(a, b, out var r);
            var twice = r * 2;
            if (twice > b || (twice == b && !q.IsEven)) q += 1;
            return q;
        }

        static BigInteger Pow10(int n) => BigInteger.Pow(10, n);

        static void Align(decimal a, decimal b, out BigInteger ma, out BigInteger mb, out int scale)
        {
            int sa = a.Scale, sb = b.Scale;
            scale = Math.Max(sa, sb);
            ma = a.Mantissa * Pow10(scale - sa);
            mb = b.Mantissa * Pow10(scale - sb);
            if (a.IsNegative) ma = -ma;
            if (b.IsNegative) mb = -mb;
        }

        static decimal FromSigned(BigInteger v, int scale) => Make(BigInteger.Abs(v), scale, v.Sign < 0);

        static decimal FromDouble(double value, int digits)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || Math.Abs(value) >= 7.9228162514264338E+28)
                throw new OverflowException("Value was either too large or too small for a Decimal.");
            if (value == 0) return 0m;
            bool neg = value < 0; double a = neg ? -value : value;
            // `digits` significant digits, as .NET's conversion
            int e = (int)Math.Floor(Math.Log10(a));
            int scale = digits - 1 - e;
            BigInteger m;
            if (scale >= 0)
            {
                double scaled = a * Math.Pow(10, Math.Min(scale, 300));
                m = new BigInteger(Math.Round(scaled, MidpointRounding.ToEven));
            }
            else m = new BigInteger(Math.Round(a / Math.Pow(10, -scale), MidpointRounding.ToEven)) * Pow10(-scale);
            if (scale < 0) scale = 0;
            // drop trailing zeros the conversion introduced
            while (scale > 0 && (m % 10).IsZero) { m /= 10; scale--; }
            while (scale > 28) { m = DivRound(m, 10); scale--; }
            return Make(m, scale, neg);
        }

        // ---------------- operators ----------------
        public static decimal operator +(decimal a, decimal b) { Align(a, b, out var x, out var y, out int s); return FromSigned(x + y, s); }
        public static decimal operator -(decimal a, decimal b) { Align(a, b, out var x, out var y, out int s); return FromSigned(x - y, s); }
        public static decimal operator *(decimal a, decimal b) => Make(a.Mantissa * b.Mantissa, a.Scale + b.Scale, a.IsNegative != b.IsNegative);
        public static decimal operator /(decimal a, decimal b)
        {
            var mb = b.Mantissa;
            if (mb.IsZero) throw new DivideByZeroException("Attempted to divide by zero.");
            var ma = a.Mantissa;
            bool neg = a.IsNegative != b.IsNegative;
            if (ma.IsZero) return 0m;
            int k = Math.Max(0, b.Scale - a.Scale);
            while (true)
            {
                var q = BigInteger.DivRem(ma * Pow10(k), mb, out var r);
                int scale = a.Scale - b.Scale + k;
                if (r.IsZero && scale >= 0) return Make(q, scale, neg);
                // one more digit would not fit: round here
                if (scale >= 28 || q * 10 > s_max96)
                {
                    var twice = r * 2;
                    if (twice > mb || (twice == mb && !q.IsEven)) q += 1;
                    if (scale < 0) return Make(q * Pow10(-scale), 0, neg);
                    return Make(q, scale, neg);
                }
                k++;
            }
        }
        public static decimal operator %(decimal a, decimal b)
        {
            if (b.Mantissa.IsZero) throw new DivideByZeroException("Attempted to divide by zero.");
            Align(a, b, out var x, out var y, out int s);
            var r = BigInteger.Remainder(x, y);
            return Make(BigInteger.Abs(r), s, a.IsNegative && !r.IsZero);
        }
        public static decimal operator -(decimal d) => new decimal((int)d._lo, (int)d._mid, (int)d._hi, !d.IsNegative && !d.Mantissa.IsZero, (byte)d.Scale);
        public static decimal operator +(decimal d) => d;
        public static decimal operator ++(decimal d) => d + 1m;
        public static decimal operator --(decimal d) => d - 1m;

        public static int Compare(decimal a, decimal b) { Align(a, b, out var x, out var y, out _); return x.CompareTo(y); }
        public int CompareTo(decimal value) => Compare(this, value);
        public int CompareTo(object value) => value == null ? 1 : value is decimal d ? Compare(this, d) : throw new ArgumentException("Object must be of type Decimal.");
        public bool Equals(decimal value) => Compare(this, value) == 0;
        public override bool Equals(object value) => value is decimal d && Equals(d);
        public static bool Equals(decimal a, decimal b) => Compare(a, b) == 0;
        public override int GetHashCode() => ((double)this).GetHashCode();
        public static bool operator ==(decimal a, decimal b) => Compare(a, b) == 0;
        public static bool operator !=(decimal a, decimal b) => Compare(a, b) != 0;
        public static bool operator <(decimal a, decimal b) => Compare(a, b) < 0;
        public static bool operator <=(decimal a, decimal b) => Compare(a, b) <= 0;
        public static bool operator >(decimal a, decimal b) => Compare(a, b) > 0;
        public static bool operator >=(decimal a, decimal b) => Compare(a, b) >= 0;

        // ---------------- conversions ----------------
        public static implicit operator decimal(byte v) => new decimal((int)v);
        public static implicit operator decimal(sbyte v) => new decimal((int)v);
        public static implicit operator decimal(short v) => new decimal((int)v);
        public static implicit operator decimal(ushort v) => new decimal((int)v);
        public static implicit operator decimal(char v) => new decimal((int)v);
        public static implicit operator decimal(int v) => new decimal(v);
        public static implicit operator decimal(uint v) => new decimal(v);
        public static implicit operator decimal(long v) => new decimal(v);
        public static implicit operator decimal(ulong v) => new decimal(v);
        public static explicit operator decimal(float v) => new decimal(v);
        public static explicit operator decimal(double v) => new decimal(v);

        static BigInteger Truncated(decimal d) => d.Mantissa / Pow10(d.Scale) * (d.IsNegative ? -1 : 1);
        public static explicit operator long(decimal d)
        {
            var t = Truncated(d);
            if (t < long.MinValue || t > long.MaxValue) throw new OverflowException("Value was either too large or too small for an Int64.");
            return (long)t;
        }
        public static explicit operator ulong(decimal d)
        {
            var t = Truncated(d);
            if (t.Sign < 0 || t > ulong.MaxValue) throw new OverflowException("Value was either too large or too small for a UInt64.");
            return (ulong)t;
        }
        public static explicit operator int(decimal d)
        {
            var t = Truncated(d);
            if (t < int.MinValue || t > int.MaxValue) throw new OverflowException("Value was either too large or too small for an Int32.");
            return (int)t;
        }
        public static explicit operator uint(decimal d) { long l = (long)d; if (l < 0 || l > uint.MaxValue) throw new OverflowException("Value was either too large or too small for a UInt32."); return (uint)l; }
        public static explicit operator short(decimal d) { int i = (int)d; if (i < short.MinValue || i > short.MaxValue) throw new OverflowException("Value was either too large or too small for an Int16."); return (short)i; }
        public static explicit operator ushort(decimal d) { int i = (int)d; if (i < 0 || i > ushort.MaxValue) throw new OverflowException("Value was either too large or too small for a UInt16."); return (ushort)i; }
        public static explicit operator byte(decimal d) { int i = (int)d; if (i < 0 || i > 255) throw new OverflowException("Value was either too large or too small for an unsigned byte."); return (byte)i; }
        public static explicit operator sbyte(decimal d) { int i = (int)d; if (i < -128 || i > 127) throw new OverflowException("Value was either too large or too small for a signed byte."); return (sbyte)i; }
        public static explicit operator char(decimal d) => (char)(ushort)d;
        public static explicit operator double(decimal d)
        {
            double m = (double)d._hi * 18446744073709551616.0 + (double)d._mid * 4294967296.0 + d._lo;
            double r = m / Math.Pow(10, d.Scale);
            return d.IsNegative ? -r : r;
        }
        public static explicit operator float(decimal d) => (float)(double)d;

        public static double ToDouble(decimal d) => (double)d;
        public static int ToInt32(decimal d) => (int)d;
        public static long ToInt64(decimal d) => (long)d;
        public static int[] GetBits(decimal d) => new[] { (int)d._lo, (int)d._mid, (int)d._hi, d._flags };

        // ---------------- functions ----------------
        public static decimal Add(decimal a, decimal b) => a + b;
        public static decimal Subtract(decimal a, decimal b) => a - b;
        public static decimal Multiply(decimal a, decimal b) => a * b;
        public static decimal Divide(decimal a, decimal b) => a / b;
        public static decimal Remainder(decimal a, decimal b) => a % b;
        public static decimal Negate(decimal d) => -d;
        public static decimal Abs(decimal d) => d.IsNegative ? -d : d;
        public static decimal Max(decimal a, decimal b) => a >= b ? a : b;
        public static decimal Min(decimal a, decimal b) => a <= b ? a : b;
        public static int Sign(decimal d) => d.Mantissa.IsZero ? 0 : d.IsNegative ? -1 : 1;
        public static decimal Truncate(decimal d) => Make(d.Mantissa / Pow10(d.Scale), 0, d.IsNegative);
        public static decimal Floor(decimal d) { var t = Truncate(d); return d.IsNegative && t != d ? t - 1 : t; }
        public static decimal Ceiling(decimal d) { var t = Truncate(d); return !d.IsNegative && t != d ? t + 1 : t; }
        public static decimal Round(decimal d) => Round(d, 0, MidpointRounding.ToEven);
        public static decimal Round(decimal d, int decimals) => Round(d, decimals, MidpointRounding.ToEven);
        public static decimal Round(decimal d, MidpointRounding mode) => Round(d, 0, mode);
        public static decimal Round(decimal d, int decimals, MidpointRounding mode)
        {
            if (decimals < 0 || decimals > 28) throw new ArgumentOutOfRangeException("decimals", "Decimal can only round to between 0 and 28 digits of precision.");
            int s = d.Scale;
            if (decimals >= s) return d;
            var div = Pow10(s - decimals);
            var q = BigInteger.DivRem(d.Mantissa, div, out var r);
            var twice = r * 2;
            bool up = mode switch
            {
                MidpointRounding.ToEven => twice > div || (twice == div && !q.IsEven),
                MidpointRounding.AwayFromZero => twice >= div,
                MidpointRounding.ToZero => false,
                MidpointRounding.ToNegativeInfinity => d.IsNegative && !r.IsZero,
                _ => !d.IsNegative && !r.IsZero,
            };
            if (up) q += 1;
            return Make(q, decimals, d.IsNegative);
        }

        // ---------------- text ----------------
        public override string ToString()
        {
            var digits = Mantissa.ToString();
            int s = Scale;
            if (s > 0)
            {
                if (digits.Length <= s) digits = new string('0', s - digits.Length + 1) + digits;
                digits = digits.Substring(0, digits.Length - s) + "." + digits.Substring(digits.Length - s);
            }
            return IsNegative && !Mantissa.IsZero ? "-" + digits : digits;
        }
        public string ToString(IFormatProvider provider) => ToString();
        public string ToString(string format) => ToString(format, null);
        public string ToString(string format, IFormatProvider provider)
        {
            if (string.IsNullOrEmpty(format) || format == "G" || format == "g") return ToString();
            char k = format[0];
            int prec = format.Length > 1 && int.TryParse(format.Substring(1), out var p) ? p : -1;
            bool neg = IsNegative && !Mantissa.IsZero;
            var abs = Abs(this);
            switch (k)
            {
                case 'F': case 'f': case 'N': case 'n':
                    {
                        var r = Round(abs, prec < 0 ? 2 : prec, MidpointRounding.AwayFromZero);
                        string s = Pad(r, prec < 0 ? 2 : prec);
                        if (k is 'N' or 'n') s = Group(s);
                        return (neg && r != 0m ? "-" : "") + s;
                    }
                case 'P': case 'p':
                    {
                        var r = Round(abs * 100m, prec < 0 ? 2 : prec, MidpointRounding.AwayFromZero);
                        return (neg && r != 0m ? "-" : "") + Group(Pad(r, prec < 0 ? 2 : prec)) + " %";
                    }
                case 'C': case 'c':
                    {
                        var r = Round(abs, prec < 0 ? 2 : prec, MidpointRounding.AwayFromZero);
                        return (neg && r != 0m ? "-" : "") + "¤" + Group(Pad(r, prec < 0 ? 2 : prec));
                    }
                case 'E': case 'e': case 'R': case 'r':
                    return ((double)this).ToString(format, provider);
            }
            // custom formats ("0.00", "#,##0.##"): digits after the point decide the rounding
            int dot = format.IndexOf('.');
            int frac = 0;
            if (dot >= 0) for (int i = dot + 1; i < format.Length && (format[i] == '0' || format[i] == '#'); i++) frac++;
            var rr = Round(this, Math.Min(frac, 28), MidpointRounding.AwayFromZero);
            return ((double)rr).ToString(format, provider);
        }

        static string Pad(decimal r, int decimals)
        {
            var s = r.ToString();
            int dot = s.IndexOf('.');
            int have = dot < 0 ? 0 : s.Length - dot - 1;
            if (decimals == 0) return dot < 0 ? s : s.Substring(0, dot);
            if (dot < 0) return s + "." + new string('0', decimals);
            return have >= decimals ? s.Substring(0, dot + 1 + decimals) : s + new string('0', decimals - have);
        }

        static string Group(string s)
        {
            int dot = s.IndexOf('.');
            string ip = dot < 0 ? s : s.Substring(0, dot), fp = dot < 0 ? "" : s.Substring(dot);
            var sb = new StringBuilder();
            for (int i = 0; i < ip.Length; i++) { if (i > 0 && (ip.Length - i) % 3 == 0) sb.Append(','); sb.Append(ip[i]); }
            return sb.ToString() + fp;
        }

        public static decimal Parse(string s) => Parse(s, NumberStyles.Number, null);
        public static decimal Parse(string s, IFormatProvider provider) => Parse(s, NumberStyles.Number, provider);
        public static decimal Parse(string s, NumberStyles style, IFormatProvider provider = null)
        {
            if (s == null) throw new ArgumentNullException("s");
            if (!TryParse(s, out var r)) throw new FormatException("The input string '" + s + "' was not in a correct format.");
            return r;
        }
        public static bool TryParse(string s, out decimal result) => TryParse(s, NumberStyles.Number, null, out result);
        public static bool TryParse(string s, NumberStyles style, IFormatProvider provider, out decimal result)
        {
            result = 0m;
            if (s == null) return false;
            var t = s.Trim();
            if (t.Length == 0) return false;
            int i = 0; bool neg = false;
            if (t[0] == '-' || t[0] == '+') { neg = t[0] == '-'; i++; }
            BigInteger m = BigInteger.Zero; int scale = 0; bool dot = false, any = false;
            for (; i < t.Length; i++)
            {
                char c = t[i];
                if (c == '.' && !dot) { dot = true; continue; }
                if (c == ',' && !dot) continue;
                if ((c == 'e' || c == 'E') && (style & NumberStyles.AllowExponent) != 0)
                {
                    if (!any || !int.TryParse(t.Substring(i + 1), out int exp)) return false;
                    scale -= exp;
                    break;
                }
                if (c < '0' || c > '9') return false;
                any = true;
                if (m > s_max96 / 10) { if (dot) continue; return false; }
                m = m * 10 + (c - '0');
                if (dot) scale++;
            }
            if (!any) return false;
            if (scale < 0) { m *= Pow10(-scale); scale = 0; }
            try { result = Make(m, scale, neg); } catch (OverflowException) { return false; }
            return true;
        }
    }
}

namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Parameter)]
    public sealed class DecimalConstantAttribute : Attribute
    {
        public DecimalConstantAttribute(byte scale, byte sign, uint hi, uint mid, uint low) { }
        public DecimalConstantAttribute(byte scale, byte sign, int hi, int mid, int low) { }
    }
}
