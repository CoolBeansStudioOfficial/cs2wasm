// Primitive types: the compiler stores them as WASM numbers; `this` is the address of the value.
#pragma warning disable CS0436, CS0660, CS0661
using Cs2Wasm.Runtime;

namespace System
{
    public readonly struct SByte : IComparable, IComparable<sbyte>, IEquatable<sbyte>, IFormattable
    {
        readonly sbyte m_value;
        public const sbyte MaxValue = 127;
        public const sbyte MinValue = -128;
        public override string ToString() => Number.Int64ToString(m_value);
        public string ToString(string format) => Number.FormatInteger(m_value, false, 0, 8, format);
        public string ToString(IFormatProvider provider) => ToString();
        public string ToString(string format, IFormatProvider provider) => ToString(format);
        public override bool Equals(object obj) => obj is sbyte v && v == m_value;
        public bool Equals(sbyte obj) => obj == m_value;
        public override int GetHashCode() => (int)m_value;
        public int CompareTo(sbyte value) => m_value < value ? -1 : m_value > value ? 1 : 0;
        public int CompareTo(object value)
        {
            if (value == null) return 1;
            if (value is sbyte v) return CompareTo(v);
            throw new ArgumentException("Object must be of type SByte.");
        }
        public static sbyte Parse(string s) => (sbyte)Number.ParseInt64(s, MinValue, (ulong)MaxValue);
        public static sbyte Parse(string s, IFormatProvider provider) => Parse(s);
        public static bool TryParse(string s, out sbyte result) { if (Number.TryParseInt64(s, MinValue, (ulong)MaxValue, out long r, out _)) { result = (sbyte)r; return true; } result = 0; return false; }
        public static sbyte Max(sbyte x, sbyte y) => x > y ? x : y;
        public static sbyte Min(sbyte x, sbyte y) => x < y ? x : y;
    }

    public readonly struct Byte : IComparable, IComparable<byte>, IEquatable<byte>, IFormattable
    {
        readonly byte m_value;
        public const byte MaxValue = 255;
        public const byte MinValue = 0;
        public override string ToString() => Number.UInt64ToString(m_value);
        public string ToString(string format) => Number.FormatInteger(0, true, m_value, 8, format);
        public string ToString(IFormatProvider provider) => ToString();
        public string ToString(string format, IFormatProvider provider) => ToString(format);
        public override bool Equals(object obj) => obj is byte v && v == m_value;
        public bool Equals(byte obj) => obj == m_value;
        public override int GetHashCode() => (int)m_value;
        public int CompareTo(byte value) => m_value < value ? -1 : m_value > value ? 1 : 0;
        public int CompareTo(object value)
        {
            if (value == null) return 1;
            if (value is byte v) return CompareTo(v);
            throw new ArgumentException("Object must be of type Byte.");
        }
        public static byte Parse(string s) => (byte)Number.ParseInt64(s, 0, MaxValue);
        public static byte Parse(string s, IFormatProvider provider) => Parse(s);
        public static bool TryParse(string s, out byte result) { if (Number.TryParseInt64(s, 0, MaxValue, out long r, out _)) { result = (byte)r; return true; } result = 0; return false; }
        public static byte Max(byte x, byte y) => x > y ? x : y;
        public static byte Min(byte x, byte y) => x < y ? x : y;
    }

    public readonly struct Int16 : IComparable, IComparable<short>, IEquatable<short>, IFormattable
    {
        readonly short m_value;
        public const short MaxValue = 32767;
        public const short MinValue = -32768;
        public override string ToString() => Number.Int64ToString(m_value);
        public string ToString(string format) => Number.FormatInteger(m_value, false, 0, 16, format);
        public string ToString(IFormatProvider provider) => ToString();
        public string ToString(string format, IFormatProvider provider) => ToString(format);
        public override bool Equals(object obj) => obj is short v && v == m_value;
        public bool Equals(short obj) => obj == m_value;
        public override int GetHashCode() => (int)m_value;
        public int CompareTo(short value) => m_value < value ? -1 : m_value > value ? 1 : 0;
        public int CompareTo(object value)
        {
            if (value == null) return 1;
            if (value is short v) return CompareTo(v);
            throw new ArgumentException("Object must be of type Int16.");
        }
        public static short Parse(string s) => (short)Number.ParseInt64(s, MinValue, (ulong)MaxValue);
        public static short Parse(string s, IFormatProvider provider) => Parse(s);
        public static bool TryParse(string s, out short result) { if (Number.TryParseInt64(s, MinValue, (ulong)MaxValue, out long r, out _)) { result = (short)r; return true; } result = 0; return false; }
        public static short Max(short x, short y) => x > y ? x : y;
        public static short Min(short x, short y) => x < y ? x : y;
    }

    public readonly struct UInt16 : IComparable, IComparable<ushort>, IEquatable<ushort>, IFormattable
    {
        readonly ushort m_value;
        public const ushort MaxValue = 65535;
        public const ushort MinValue = 0;
        public override string ToString() => Number.UInt64ToString(m_value);
        public string ToString(string format) => Number.FormatInteger(0, true, m_value, 16, format);
        public string ToString(IFormatProvider provider) => ToString();
        public string ToString(string format, IFormatProvider provider) => ToString(format);
        public override bool Equals(object obj) => obj is ushort v && v == m_value;
        public bool Equals(ushort obj) => obj == m_value;
        public override int GetHashCode() => (int)m_value;
        public int CompareTo(ushort value) => m_value < value ? -1 : m_value > value ? 1 : 0;
        public int CompareTo(object value)
        {
            if (value == null) return 1;
            if (value is ushort v) return CompareTo(v);
            throw new ArgumentException("Object must be of type UInt16.");
        }
        public static ushort Parse(string s) => (ushort)Number.ParseInt64(s, 0, MaxValue);
        public static ushort Parse(string s, IFormatProvider provider) => Parse(s);
        public static bool TryParse(string s, out ushort result) { if (Number.TryParseInt64(s, 0, MaxValue, out long r, out _)) { result = (ushort)r; return true; } result = 0; return false; }
        public static ushort Max(ushort x, ushort y) => x > y ? x : y;
        public static ushort Min(ushort x, ushort y) => x < y ? x : y;
    }

    public readonly struct Int32 : IComparable, IComparable<int>, IEquatable<int>, IFormattable
    {
        readonly int m_value;
        public const int MaxValue = 2147483647;
        public const int MinValue = -2147483648;
        public override string ToString() => Number.Int64ToString(m_value);
        public string ToString(string format) => Number.FormatInteger(m_value, false, 0, 32, format);
        public string ToString(IFormatProvider provider) => ToString();
        public string ToString(string format, IFormatProvider provider) => ToString(format);
        public override bool Equals(object obj) => obj is int v && v == m_value;
        public bool Equals(int obj) => obj == m_value;
        public override int GetHashCode() => m_value;
        public int CompareTo(int value) => m_value < value ? -1 : m_value > value ? 1 : 0;
        public int CompareTo(object value)
        {
            if (value == null) return 1;
            if (value is int v) return CompareTo(v);
            throw new ArgumentException("Object must be of type Int32.");
        }
        public static int Parse(string s) => (int)Number.ParseInt64(s, MinValue, (ulong)MaxValue);
        public static int Parse(string s, IFormatProvider provider) => Parse(s);
        public static bool TryParse(string s, out int result) { if (Number.TryParseInt64(s, MinValue, (ulong)MaxValue, out long r, out _)) { result = (int)r; return true; } result = 0; return false; }
        public static int Max(int x, int y) => x > y ? x : y;
        public static int Min(int x, int y) => x < y ? x : y;
    }

    public readonly struct UInt32 : IComparable, IComparable<uint>, IEquatable<uint>, IFormattable
    {
        readonly uint m_value;
        public const uint MaxValue = 4294967295U;
        public const uint MinValue = 0;
        public override string ToString() => Number.UInt64ToString(m_value);
        public string ToString(string format) => Number.FormatInteger(0, true, m_value, 32, format);
        public string ToString(IFormatProvider provider) => ToString();
        public string ToString(string format, IFormatProvider provider) => ToString(format);
        public override bool Equals(object obj) => obj is uint v && v == m_value;
        public bool Equals(uint obj) => obj == m_value;
        public override int GetHashCode() => unchecked((int)m_value);
        public int CompareTo(uint value) => m_value < value ? -1 : m_value > value ? 1 : 0;
        public int CompareTo(object value)
        {
            if (value == null) return 1;
            if (value is uint v) return CompareTo(v);
            throw new ArgumentException("Object must be of type UInt32.");
        }
        public static uint Parse(string s) => (uint)Number.ParseInt64(s, 0, MaxValue);
        public static uint Parse(string s, IFormatProvider provider) => Parse(s);
        public static bool TryParse(string s, out uint result) { if (Number.TryParseInt64(s, 0, MaxValue, out long r, out _)) { result = (uint)r; return true; } result = 0; return false; }
        public static uint Max(uint x, uint y) => x > y ? x : y;
        public static uint Min(uint x, uint y) => x < y ? x : y;
    }

    public readonly struct Int64 : IComparable, IComparable<long>, IEquatable<long>, IFormattable
    {
        readonly long m_value;
        public const long MaxValue = 9223372036854775807L;
        public const long MinValue = -9223372036854775807L - 1;
        public override string ToString() => Number.Int64ToString(m_value);
        public string ToString(string format) => Number.FormatInteger(m_value, false, 0, 64, format);
        public string ToString(IFormatProvider provider) => ToString();
        public string ToString(string format, IFormatProvider provider) => ToString(format);
        public override bool Equals(object obj) => obj is long v && v == m_value;
        public bool Equals(long obj) => obj == m_value;
        public override int GetHashCode() => unchecked((int)m_value ^ (int)(m_value >> 32));
        public int CompareTo(long value) => m_value < value ? -1 : m_value > value ? 1 : 0;
        public int CompareTo(object value)
        {
            if (value == null) return 1;
            if (value is long v) return CompareTo(v);
            throw new ArgumentException("Object must be of type Int64.");
        }
        public static long Parse(string s) => Number.ParseInt64(s, MinValue, (ulong)MaxValue);
        public static long Parse(string s, IFormatProvider provider) => Parse(s);
        public static bool TryParse(string s, out long result) { if (Number.TryParseInt64(s, MinValue, (ulong)MaxValue, out long r, out _)) { result = r; return true; } result = 0; return false; }
        public static long Max(long x, long y) => x > y ? x : y;
        public static long Min(long x, long y) => x < y ? x : y;
    }

    public readonly struct UInt64 : IComparable, IComparable<ulong>, IEquatable<ulong>, IFormattable
    {
        readonly ulong m_value;
        public const ulong MaxValue = 18446744073709551615UL;
        public const ulong MinValue = 0;
        public override string ToString() => Number.UInt64ToString(m_value);
        public string ToString(string format) => Number.FormatInteger(0, true, m_value, 64, format);
        public string ToString(IFormatProvider provider) => ToString();
        public string ToString(string format, IFormatProvider provider) => ToString(format);
        public override bool Equals(object obj) => obj is ulong v && v == m_value;
        public bool Equals(ulong obj) => obj == m_value;
        public override int GetHashCode() => unchecked((int)m_value ^ (int)(m_value >> 32));
        public int CompareTo(ulong value) => m_value < value ? -1 : m_value > value ? 1 : 0;
        public int CompareTo(object value)
        {
            if (value == null) return 1;
            if (value is ulong v) return CompareTo(v);
            throw new ArgumentException("Object must be of type UInt64.");
        }
        public static ulong Parse(string s) => Number.TryParseUInt64(s, out ulong r) ? r : throw new FormatException("The input string '" + s + "' was not in a correct format.");
        public static ulong Parse(string s, IFormatProvider provider) => Parse(s);
        public static bool TryParse(string s, out ulong result) { return Number.TryParseUInt64(s, out result); }
        public static ulong Max(ulong x, ulong y) => x > y ? x : y;
        public static ulong Min(ulong x, ulong y) => x < y ? x : y;
    }

    public readonly struct Double : IComparable, IComparable<double>, IEquatable<double>, IFormattable
    {
        readonly double m_value;
        public const double MinValue = -1.7976931348623157E+308;
        public const double MaxValue = 1.7976931348623157E+308;
        public const double Epsilon = 4.9406564584124654E-324;
        public const double NegativeInfinity = -1.0 / 0.0;
        public const double PositiveInfinity = 1.0 / 0.0;
        public const double NaN = 0.0 / 0.0;
        public const double NegativeZero = -0.0;
        public const double E = 2.7182818284590451;
        public const double Pi = 3.1415926535897931;
        public const double Tau = 6.2831853071795862;
        public static bool IsNaN(double d) => d != d;
        public static bool IsInfinity(double d) => d == PositiveInfinity || d == NegativeInfinity;
        public static bool IsPositiveInfinity(double d) => d == PositiveInfinity;
        public static bool IsNegativeInfinity(double d) => d == NegativeInfinity;
        public static bool IsFinite(double d) => !IsNaN(d) && !IsInfinity(d);
        public static bool IsNegative(double d) => Rt.F64Bits(d) < 0;
        public static bool IsInteger(double d) => IsFinite(d) && d == Rt.Truncate(d);
        public override string ToString() => Number.FormatDouble(m_value, null, false);
        public string ToString(string format) => Number.FormatDouble(m_value, format, false);
        public string ToString(IFormatProvider provider) => ToString();
        public string ToString(string format, IFormatProvider provider) => ToString(format);
        public override bool Equals(object obj) => obj is double v && Equals(v);
        public bool Equals(double obj) => obj == m_value || (IsNaN(obj) && IsNaN(m_value));
        public override int GetHashCode() { long bits = Rt.F64Bits(m_value); if (((bits - 1) & 0x7FFFFFFFFFFFFFFFL) >= 0x7FF0000000000000L) bits &= 0x7FF0000000000000L; return unchecked((int)bits ^ (int)(bits >> 32)); }
        public int CompareTo(double value)
        {
            if (m_value < value) return -1;
            if (m_value > value) return 1;
            if (m_value == value) return 0;
            if (IsNaN(m_value)) return IsNaN(value) ? 0 : -1;
            return 1;
        }
        public int CompareTo(object value)
        {
            if (value == null) return 1;
            if (value is double v) return CompareTo(v);
            throw new ArgumentException("Object must be of type Double.");
        }
        public static double Parse(string s) => Number.ParseDouble(s);
        public static double Parse(string s, IFormatProvider provider) => Parse(s);
        public static bool TryParse(string s, out double result) { return Number.TryParseDouble(s, out result); }
    }

    public readonly struct Single : IComparable, IComparable<float>, IEquatable<float>, IFormattable
    {
        readonly float m_value;
        public const float MinValue = -3.40282347E+38f;
        public const float MaxValue = 3.40282347E+38f;
        public const float Epsilon = 1.401298E-45f;
        public const float NegativeInfinity = -1.0f / 0.0f;
        public const float PositiveInfinity = 1.0f / 0.0f;
        public const float NaN = 0.0f / 0.0f;
        public const float NegativeZero = -0.0f;
        public const float E = 2.71828183f;
        public const float Pi = 3.14159265f;
        public const float Tau = 6.28318531f;
        public static bool IsNaN(float d) => d != d;
        public static bool IsInfinity(float d) => d == PositiveInfinity || d == NegativeInfinity;
        public static bool IsPositiveInfinity(float d) => d == PositiveInfinity;
        public static bool IsNegativeInfinity(float d) => d == NegativeInfinity;
        public static bool IsFinite(float d) => !IsNaN(d) && !IsInfinity(d);
        public static bool IsNegative(float d) => Rt.F32Bits(d) < 0;
        public static bool IsInteger(float d) => IsFinite(d) && d == Rt.TruncateF(d);
        public override string ToString() => Number.FormatDouble(m_value, null, true);
        public string ToString(string format) => Number.FormatDouble(m_value, format, true);
        public string ToString(IFormatProvider provider) => ToString();
        public string ToString(string format, IFormatProvider provider) => ToString(format);
        public override bool Equals(object obj) => obj is float v && Equals(v);
        public bool Equals(float obj) => obj == m_value || (IsNaN(obj) && IsNaN(m_value));
        public override int GetHashCode() { int bits = Rt.F32Bits(m_value); if (((bits - 1) & 0x7FFFFFFF) >= 0x7F800000) bits &= 0x7F800000; return bits; }
        public int CompareTo(float value)
        {
            if (m_value < value) return -1;
            if (m_value > value) return 1;
            if (m_value == value) return 0;
            if (IsNaN(m_value)) return IsNaN(value) ? 0 : -1;
            return 1;
        }
        public int CompareTo(object value)
        {
            if (value == null) return 1;
            if (value is float v) return CompareTo(v);
            throw new ArgumentException("Object must be of type Single.");
        }
        public static float Parse(string s) => (float)Number.ParseDouble(s);
        public static float Parse(string s, IFormatProvider provider) => Parse(s);
        public static bool TryParse(string s, out float result) { bool ok = Number.TryParseDouble(s, out double d); result = (float)d; return ok; }
    }

    public readonly struct Boolean : IComparable, IComparable<bool>, IEquatable<bool>
    {
        readonly bool m_value;
        public static readonly string TrueString = "True";
        public static readonly string FalseString = "False";
        public override string ToString() => m_value ? "True" : "False";
        public string ToString(IFormatProvider provider) => ToString();
        public override bool Equals(object obj) => obj is bool b && b == m_value;
        public bool Equals(bool obj) => obj == m_value;
        public override int GetHashCode() => m_value ? 1 : 0;
        public int CompareTo(bool value) => m_value == value ? 0 : m_value ? 1 : -1;
        public int CompareTo(object obj) { if (obj == null) return 1; if (obj is bool b) return CompareTo(b); throw new ArgumentException("Object must be of type Boolean."); }
        public static bool Parse(string value)
        {
            if (TryParse(value, out bool r)) return r;
            throw new FormatException("String '" + value + "' was not recognized as a valid Boolean.");
        }
        public static bool TryParse(string value, out bool result)
        {
            result = false;
            if (value == null) return false;
            string t = value.Trim();
            if (string.Equals(t, "True", StringComparison.OrdinalIgnoreCase)) { result = true; return true; }
            if (string.Equals(t, "False", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }

    public readonly struct Char : IComparable, IComparable<char>, IEquatable<char>
    {
        readonly char m_value;
        public const char MaxValue = (char)0xFFFF;
        public const char MinValue = '\0';
        public override string ToString() => string.Ctor(m_value, 1);
        public string ToString(IFormatProvider provider) => ToString();
        public static string ToString(char c) => string.Ctor(c, 1);
        public override bool Equals(object obj) => obj is char c && c == m_value;
        public bool Equals(char obj) => obj == m_value;
        public override int GetHashCode() => m_value | (m_value << 16);
        public int CompareTo(char value) => m_value - value;
        public int CompareTo(object value) { if (value == null) return 1; if (value is char c) return CompareTo(c); throw new ArgumentException("Object must be of type Char."); }
        public static bool IsDigit(char c) => c >= '0' && c <= '9';
        public static bool IsAsciiDigit(char c) => c >= '0' && c <= '9';
        public static bool IsAsciiLetter(char c) => (uint)((c | 0x20) - 'a') <= 'z' - 'a';
        public static bool IsAscii(char c) => c <= 0x7F;
        public static bool IsLetter(char c)
        {
            if (c < 0x80) return (uint)((c | 0x20) - 'a') <= 'z' - 'a';
            if (c < 0x100) return c == 0xAA || c == 0xB5 || c == 0xBA || (c >= 0xC0 && c != 0xD7 && c != 0xF7);
            return !IsWhiteSpace(c) && !(c >= 0x2000 && c <= 0x2BFF) && !(c >= 0x3000 && c <= 0x303F) && !(c >= 0xD800 && c <= 0xDFFF) && !(c >= 0xE000 && c <= 0xF8FF) && !(c >= 0xFF00 && c <= 0xFF20);
        }
        public static bool IsLetterOrDigit(char c) => IsLetter(c) || IsDigit(c);
        public static bool IsUpper(char c) => c < 0x80 ? (uint)(c - 'A') <= 'Z' - 'A' : c >= 0xC0 && c <= 0xDE && c != 0xD7;
        public static bool IsLower(char c) => c < 0x80 ? (uint)(c - 'a') <= 'z' - 'a' : (c >= 0xDF && c <= 0xFF && c != 0xF7) || c == 0xB5 || c == 0xAA || c == 0xBA;
        public static bool IsWhiteSpace(char c) =>
            c == ' ' || (c >= '\t' && c <= '\r') || c == '\u0085' || c == (char)0x00A0 || c == (char)0x1680 || (c >= (char)0x2000 && c <= (char)0x200A) ||
            c == (char)0x2028 || c == (char)0x2029 || c == (char)0x202F || c == (char)0x205F || c == (char)0x3000;
        public static bool IsControl(char c) => c < 0x20 || (c >= 0x7F && c < 0xA0);
        public static bool IsPunctuation(char c) =>
            c < 0x80 ? (c >= '!' && c <= '#') || (c >= '%' && c <= '*') || (c >= ',' && c <= '/') || c == ':' || c == ';' || c == '?' || c == '@' || (c >= '[' && c <= ']') || c == '_' || c == '{' || c == '}'
                     : c == 0xA1 || c == 0xA7 || c == 0xAB || c == 0xB6 || c == 0xB7 || c == 0xBB || c == 0xBF;
        public static bool IsSymbol(char c) => c == '$' || c == '+' || c == '<' || c == '=' || c == '>' || c == '^' || c == '`' || c == '|' || c == '~';
        public static bool IsSeparator(char c) => c == ' ' || c == (char)0x00A0 || c == (char)0x2028 || c == (char)0x2029;
        public static bool IsSurrogate(char c) => c >= 0xD800 && c <= 0xDFFF;
        public static bool IsHighSurrogate(char c) => c >= 0xD800 && c <= 0xDBFF;
        public static bool IsLowSurrogate(char c) => c >= 0xDC00 && c <= 0xDFFF;
        public static bool IsNumber(char c) => IsDigit(c) || c == 0xB2 || c == 0xB3 || c == 0xB9 || (c >= 0xBC && c <= 0xBE);
        public static char ToUpper(char c) => ToUpperInvariant(c);
        public static char ToLower(char c) => ToLowerInvariant(c);
        public static char ToUpperInvariant(char c)
        {
            if (c < 0x80) return (uint)(c - 'a') <= 'z' - 'a' ? (char)(c - 32) : c;
            if (c < 0x100)
            {
                if (c >= 0xE0 && c <= 0xFE && c != 0xF7) return (char)(c - 32);
                if (c == 0xFF) return (char)0x178;
                if (c == 0xB5) return (char)0x39C;
                return c;
            }
            if (c >= 0x3B1 && c <= 0x3C9 && c != 0x3C2) return (char)(c - 32);
            if (c >= 0x430 && c <= 0x44F) return (char)(c - 32);
            if (c >= 0x450 && c <= 0x45F) return (char)(c - 80);
            return c;
        }
        public static char ToLowerInvariant(char c)
        {
            if (c < 0x80) return (uint)(c - 'A') <= 'Z' - 'A' ? (char)(c + 32) : c;
            if (c < 0x100) return c >= 0xC0 && c <= 0xDE && c != 0xD7 ? (char)(c + 32) : c;
            if (c == 0x178) return (char)0xFF;
            if (c >= 0x391 && c <= 0x3A9 && c != 0x3A2) return (char)(c + 32);
            if (c >= 0x410 && c <= 0x42F) return (char)(c + 32);
            if (c >= 0x400 && c <= 0x40F) return (char)(c + 80);
            return c;
        }
        public static double GetNumericValue(char c) => IsDigit(c) ? c - '0' : -1;
        public static char Parse(string s)
        {
            if (s == null) throw new ArgumentNullException("s");
            if (s.Length != 1) throw new FormatException("String must be exactly one character long.");
            return s[0];
        }
        public static bool TryParse(string s, out char result)
        {
            if (s == null || s.Length != 1) { result = '\0'; return false; }
            result = s[0]; return true;
        }
    }

}