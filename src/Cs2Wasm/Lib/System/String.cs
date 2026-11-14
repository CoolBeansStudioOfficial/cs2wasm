#pragma warning disable CS0626, CS0436, CS0660, CS0661, CS0659, CS0114, CS0108
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Cs2Wasm.Runtime;

namespace System
{
    /// <summary>Layout: header, length at 12, UTF-16 chars from 16, then a terminating zero.</summary>
    public sealed class String : IEnumerable<char>, IComparable, IComparable<string>, IEquatable<string>, ICloneable
    {
        int _length;
        char _firstChar;

        public static readonly string Empty = "";

        // `new string(...)` compiles to the matching static Ctor factory below.
        public extern String(char c, int count);
        public extern String(char[] value);
        public extern String(char[] value, int startIndex, int length);
        public extern String(ReadOnlySpan<char> value);

        public int Length => _length;

        [IndexerName("Chars")]
        public char this[int index]
        {
            get
            {
                if ((uint)index >= (uint)_length) ThrowHelpers.IndexOutOfRange();
                return (char)Mem.LoadU16(Rt.AddressOf(this) + 16 + index * 2);
            }
        }

        internal int Data => Rt.AddressOf(this) + 16;
        internal static string Alloc(int length) => length == 0 ? "" : Rt.Take<string>(Rt.AllocString(length));
        internal char At(int i) => (char)Mem.LoadU16(Rt.AddressOf(this) + 16 + i * 2);

        // ---- construction ----
        public static string Ctor(char c, int count)
        {
            if (count < 0) throw new ArgumentOutOfRangeException("count");
            string s = Alloc(count);
            int d = s.Data;
            for (int i = 0; i < count; i++) Mem.StoreU16(d + i * 2, c);
            return s;
        }
        public static string Ctor(char[] value)
        {
            if (value == null || value.Length == 0) return "";
            return Ctor(value, 0, value.Length);
        }
        public static string Ctor(char[] value, int startIndex, int length)
        {
            if (value == null) throw new ArgumentNullException("value");
            if (startIndex < 0 || length < 0 || startIndex > value.Length - length) throw new ArgumentOutOfRangeException("startIndex");
            string s = Alloc(length);
            if (length > 0) Mem.Copy(s.Data, Rt.ArrayData(value) + startIndex * 2, length * 2);
            return s;
        }

        // ---- concatenation (the compiler lowers + and interpolation to these) ----
        public static string Concat2(string a, string b)
        {
            if (a == null || a._length == 0) return b ?? "";
            if (b == null || b._length == 0) return a;
            string s = Alloc(a._length + b._length);
            Mem.Copy(s.Data, a.Data, a._length * 2);
            Mem.Copy(s.Data + a._length * 2, b.Data, b._length * 2);
            return s;
        }
        public static string OrEmpty(string s) => s ?? "";
        public static string Align(string s, int width)
        {
            int pad = (width < 0 ? -width : width) - s.Length;
            if (pad <= 0) return s;
            return width < 0 ? s.PadRight(-width) : s.PadLeft(width);
        }

        public static string Concat(string str0, string str1) => Concat2(str0, str1);
        public static string Concat(string str0, string str1, string str2) => Concat2(Concat2(str0, str1), str2);
        public static string Concat(string str0, string str1, string str2, string str3) => Concat2(Concat2(Concat2(str0, str1), str2), str3);
        public static string Concat(params string[] values)
        {
            string r = "";
            for (int i = 0; i < values.Length; i++) r = Concat2(r, values[i]);
            return r;
        }
        public static string Concat(object arg0) => arg0 == null ? "" : arg0.ToString() ?? "";
        public static string Concat(object arg0, object arg1) => Concat2(Concat(arg0), Concat(arg1));
        public static string Concat(object arg0, object arg1, object arg2) => Concat2(Concat2(Concat(arg0), Concat(arg1)), Concat(arg2));
        public static string Concat(params object[] args)
        {
            string r = "";
            for (int i = 0; i < args.Length; i++) r = Concat2(r, Concat(args[i]));
            return r;
        }
        public static string Concat(IEnumerable<string> values)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var v in values) sb.Append(v);
            return sb.ToString();
        }

        public static string Join(string separator, params string[] value)
        {
            if (value.Length == 0) return "";
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < value.Length; i++) { if (i > 0) sb.Append(separator); sb.Append(value[i]); }
            return sb.ToString();
        }
        public static string Join(char separator, params string[] value) => Join(Ctor(separator, 1), value);
        public static string Join(string separator, ReadOnlySpan<string> value) => Join(separator, value.ToArray());
        public static string Join(char separator, ReadOnlySpan<string> value) => Join(Ctor(separator, 1), value.ToArray());
        public static string Join(string separator, ReadOnlySpan<object> values) => Join(separator, values.ToArray());
        public static string Join(char separator, ReadOnlySpan<object> values) => Join(Ctor(separator, 1), values.ToArray());
        public static string Concat(ReadOnlySpan<string> values) => Concat(values.ToArray());
        public static string Concat(ReadOnlySpan<object> args) => Concat(args.ToArray());
        public static string Format(string format, ReadOnlySpan<object> args) => Formatting.Format(format, args.ToArray());
        public static string Join(string separator, params object[] values)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < values.Length; i++) { if (i > 0) sb.Append(separator); if (values[i] != null) sb.Append(values[i].ToString()); }
            return sb.ToString();
        }
        public static string Join(string separator, IEnumerable<string> values)
        {
            var sb = new System.Text.StringBuilder();
            bool first = true;
            foreach (var v in values) { if (!first) sb.Append(separator); first = false; sb.Append(v); }
            return sb.ToString();
        }
        public static string Join<T>(string separator, IEnumerable<T> values)
        {
            var sb = new System.Text.StringBuilder();
            bool first = true;
            foreach (var v in values) { if (!first) sb.Append(separator); first = false; if (v != null) sb.Append(v.ToString()); }
            return sb.ToString();
        }
        public static string Join<T>(char separator, IEnumerable<T> values) => Join<T>(Ctor(separator, 1), values);

        // ---- comparison ----
        public static bool Equals(string a, string b)
        {
            if (Rt.AddressOf(a) == Rt.AddressOf(b)) return true;
            if (a == null || b == null) return false;
            int n = a._length;
            if (n != b._length) return false;
            int pa = a.Data, pb = b.Data;
            for (int i = 0; i < n; i++)
                if (Mem.LoadU16(pa + i * 2) != Mem.LoadU16(pb + i * 2)) return false;
            return true;
        }
        public static bool operator ==(string a, string b) => Equals(a, b);
        public static implicit operator ReadOnlySpan<char>(string value) => value == null ? default : new ReadOnlySpan<char>(value.ToCharArray());
        public static string Ctor(ReadOnlySpan<char> value) => value.Length == 0 ? "" : Ctor(value.ToArray());
        public static bool operator !=(string a, string b) => !Equals(a, b);
        public override bool Equals(object obj) => obj is string s && Equals(this, s);
        public bool Equals(string value) => Equals(this, value);
        public bool Equals(string value, StringComparison comparisonType) => Compare(this, value, comparisonType) == 0;
        public static bool Equals(string a, string b, StringComparison comparisonType) => Compare(a, b, comparisonType) == 0;

        /// <summary>Deterministic hash: the same on every machine (section 7.3).</summary>
        public override int GetHashCode()
        {
            uint h = 2166136261;
            int p = Data;
            for (int i = 0; i < _length; i++) { h ^= (uint)Mem.LoadU16(p + i * 2); h *= 16777619; }
            return (int)h;
        }

        public static int CompareOrdinal(string strA, string strB)
        {
            if (Rt.AddressOf(strA) == Rt.AddressOf(strB)) return 0;
            if (strA == null) return -1;
            if (strB == null) return 1;
            int n = Math.Min(strA._length, strB._length);
            for (int i = 0; i < n; i++)
            {
                int d = strA.At(i) - strB.At(i);
                if (d != 0) return d;
            }
            return strA._length - strB._length;
        }

        public static int Compare(string strA, string strB) => Compare(strA, strB, StringComparison.CurrentCulture);
        public static int Compare(string strA, string strB, bool ignoreCase) => Compare(strA, strB, ignoreCase ? StringComparison.CurrentCultureIgnoreCase : StringComparison.CurrentCulture);
        /// <summary>Invariant globalization (section 7.4): culture-sensitive comparison is ordinal, as in .NET's invariant mode.</summary>
        public static int Compare(string strA, string strB, StringComparison comparisonType)
        {
            if (Rt.AddressOf(strA) == Rt.AddressOf(strB)) return 0;
            if (strA == null) return -1;
            if (strB == null) return 1;
            bool ic = comparisonType is StringComparison.OrdinalIgnoreCase or StringComparison.CurrentCultureIgnoreCase or StringComparison.InvariantCultureIgnoreCase;
            int n = Math.Min(strA._length, strB._length);
            for (int i = 0; i < n; i++)
            {
                int a = strA.At(i), b = strB.At(i);
                if (ic) { a = char.ToUpperInvariant((char)a); b = char.ToUpperInvariant((char)b); }
                if (a != b) return a - b;
            }
            return strA._length - strB._length;
        }
        public int CompareTo(string strB) => Compare(this, strB);
        public int CompareTo(object value) => Compare(this, (string)value);

        // ---- queries ----
        public static bool IsNullOrEmpty(string value) => value == null || value._length == 0;
        public static bool IsNullOrWhiteSpace(string value)
        {
            if (value == null) return true;
            for (int i = 0; i < value._length; i++) if (!char.IsWhiteSpace(value.At(i))) return false;
            return true;
        }

        public int IndexOf(char value) => IndexOf(value, 0);
        public int IndexOf(char value, int startIndex)
        {
            if ((uint)startIndex > (uint)_length) throw new ArgumentOutOfRangeException("startIndex");
            for (int i = startIndex; i < _length; i++) if (At(i) == value) return i;
            return -1;
        }
        public int IndexOf(string value) => IndexOf(value, 0, StringComparison.Ordinal);
        public int IndexOf(string value, int startIndex) => IndexOf(value, startIndex, StringComparison.Ordinal);
        public int IndexOf(string value, StringComparison comparisonType) => IndexOf(value, 0, comparisonType);
        public int IndexOf(string value, int startIndex, StringComparison comparisonType)
        {
            if (value == null) throw new ArgumentNullException("value");
            if ((uint)startIndex > (uint)_length) throw new ArgumentOutOfRangeException("startIndex");
            bool ic = comparisonType is StringComparison.OrdinalIgnoreCase or StringComparison.CurrentCultureIgnoreCase or StringComparison.InvariantCultureIgnoreCase;
            for (int i = startIndex; i + value._length <= _length; i++)
            {
                int j = 0;
                for (; j < value._length; j++)
                {
                    char a = At(i + j), b = value.At(j);
                    if (ic) { a = char.ToUpperInvariant(a); b = char.ToUpperInvariant(b); }
                    if (a != b) break;
                }
                if (j == value._length) return i;
            }
            return -1;
        }
        public int IndexOfAny(char[] anyOf)
        {
            for (int i = 0; i < _length; i++) for (int j = 0; j < anyOf.Length; j++) if (At(i) == anyOf[j]) return i;
            return -1;
        }
        public int LastIndexOf(char value)
        {
            for (int i = _length - 1; i >= 0; i--) if (At(i) == value) return i;
            return -1;
        }
        public int LastIndexOf(string value)
        {
            for (int i = _length - value._length; i >= 0; i--)
            {
                int j = 0;
                for (; j < value._length; j++) if (At(i + j) != value.At(j)) break;
                if (j == value._length) return i;
            }
            return -1;
        }
        public bool Contains(string value) => IndexOf(value) >= 0;
        public bool Contains(char value) => IndexOf(value) >= 0;
        public bool Contains(string value, StringComparison comparisonType) => IndexOf(value, comparisonType) >= 0;
        public bool StartsWith(string value) => StartsWith(value, StringComparison.CurrentCulture);
        public bool StartsWith(string value, StringComparison comparisonType)
        {
            if (value == null) throw new ArgumentNullException("value");
            if (value._length > _length) return false;
            return Compare(Substring(0, value._length), value, comparisonType) == 0;
        }
        public bool StartsWith(char value) => _length > 0 && At(0) == value;
        public bool EndsWith(string value) => EndsWith(value, StringComparison.CurrentCulture);
        public bool EndsWith(string value, StringComparison comparisonType)
        {
            if (value == null) throw new ArgumentNullException("value");
            if (value._length > _length) return false;
            return Compare(Substring(_length - value._length), value, comparisonType) == 0;
        }
        public bool EndsWith(char value) => _length > 0 && At(_length - 1) == value;

        // ---- transformation ----
        public string Substring(int startIndex) => Substring(startIndex, _length - startIndex);
        public string Substring(int startIndex, int length)
        {
            if (startIndex < 0 || startIndex > _length) throw new ArgumentOutOfRangeException("startIndex", "startIndex cannot be larger than length of string.");
            if (length < 0 || startIndex > _length - length) throw new ArgumentOutOfRangeException("length", "Index and length must refer to a location within the string.");
            if (length == _length) return this;
            string s = Alloc(length);
            if (length > 0) Mem.Copy(s.Data, Data + startIndex * 2, length * 2);
            return s;
        }
        public string ToUpper() => ToUpperInvariant();
        public string ToLower() => ToLowerInvariant();
        public string ToUpperInvariant()
        {
            string s = Alloc(_length);
            for (int i = 0; i < _length; i++) Mem.StoreU16(s.Data + i * 2, char.ToUpperInvariant(At(i)));
            return s;
        }
        public string ToLowerInvariant()
        {
            string s = Alloc(_length);
            for (int i = 0; i < _length; i++) Mem.StoreU16(s.Data + i * 2, char.ToLowerInvariant(At(i)));
            return s;
        }
        public string Trim() => TrimStart().TrimEnd();
        public string TrimStart()
        {
            int i = 0;
            while (i < _length && char.IsWhiteSpace(At(i))) i++;
            return Substring(i);
        }
        public string TrimEnd()
        {
            int i = _length;
            while (i > 0 && char.IsWhiteSpace(At(i - 1))) i--;
            return Substring(0, i);
        }
        public string Trim(char trimChar)
        {
            int a = 0, b = _length;
            while (a < b && At(a) == trimChar) a++;
            while (b > a && At(b - 1) == trimChar) b--;
            return Substring(a, b - a);
        }
        public string Trim(params char[] trimChars)
        {
            if (trimChars == null || trimChars.Length == 0) return Trim();
            int a = 0, b = _length;
            while (a < b && Array.IndexOf(trimChars, At(a)) >= 0) a++;
            while (b > a && Array.IndexOf(trimChars, At(b - 1)) >= 0) b--;
            return Substring(a, b - a);
        }
        public string TrimStart(char trimChar) { int a = 0; while (a < _length && At(a) == trimChar) a++; return Substring(a); }
        public string TrimEnd(char trimChar) { int b = _length; while (b > 0 && At(b - 1) == trimChar) b--; return Substring(0, b); }
        public string PadLeft(int totalWidth) => PadLeft(totalWidth, ' ');
        public string PadLeft(int totalWidth, char paddingChar)
        {
            if (totalWidth < 0) throw new ArgumentOutOfRangeException("totalWidth");
            int pad = totalWidth - _length;
            if (pad <= 0) return this;
            return Concat2(Ctor(paddingChar, pad), this);
        }
        public string PadRight(int totalWidth) => PadRight(totalWidth, ' ');
        public string PadRight(int totalWidth, char paddingChar)
        {
            if (totalWidth < 0) throw new ArgumentOutOfRangeException("totalWidth");
            int pad = totalWidth - _length;
            if (pad <= 0) return this;
            return Concat2(this, Ctor(paddingChar, pad));
        }
        public string Replace(char oldChar, char newChar)
        {
            string s = Alloc(_length);
            for (int i = 0; i < _length; i++) { char c = At(i); Mem.StoreU16(s.Data + i * 2, c == oldChar ? newChar : c); }
            return s;
        }
        public string Replace(string oldValue, string newValue)
        {
            if (oldValue == null) throw new ArgumentNullException("oldValue");
            if (oldValue._length == 0) throw new ArgumentException("String cannot be of zero length.", "oldValue");
            var sb = new System.Text.StringBuilder();
            int i = 0;
            while (true)
            {
                int j = IndexOf(oldValue, i);
                if (j < 0) break;
                sb.Append(Substring(i, j - i));
                if (newValue != null) sb.Append(newValue);
                i = j + oldValue._length;
            }
            if (i == 0) return this;
            sb.Append(Substring(i));
            return sb.ToString();
        }
        public string Insert(int startIndex, string value) => Concat2(Concat2(Substring(0, startIndex), value), Substring(startIndex));
        public string Remove(int startIndex) => Substring(0, startIndex);
        public string Remove(int startIndex, int count) => Concat2(Substring(0, startIndex), Substring(startIndex + count));
        public string[] Split(char separator) => Split(separator, StringSplitOptions.None);
        public string[] Split(char separator, StringSplitOptions options) => SplitCore(Ctor(separator, 1), options);
        public string[] Split(params char[] separator)
        {
            if (separator == null || separator.Length == 0) return SplitWhite();
            if (separator.Length == 1) return Split(separator[0]);
            var parts = new List<string>();
            int start = 0;
            for (int i = 0; i < _length; i++)
                if (Array.IndexOf(separator, At(i)) >= 0) { parts.Add(Substring(start, i - start)); start = i + 1; }
            parts.Add(Substring(start));
            return parts.ToArray();
        }
        public string[] Split(string separator) => SplitCore(separator, StringSplitOptions.None);
        public string[] Split(string separator, StringSplitOptions options) => SplitCore(separator, options);
        string[] SplitWhite()
        {
            var parts = new List<string>();
            int start = 0;
            for (int i = 0; i < _length; i++)
                if (char.IsWhiteSpace(At(i))) { parts.Add(Substring(start, i - start)); start = i + 1; }
            parts.Add(Substring(start));
            return parts.ToArray();
        }
        string[] SplitCore(string sep, StringSplitOptions options)
        {
            var parts = new List<string>();
            bool removeEmpty = (options & StringSplitOptions.RemoveEmptyEntries) != 0;
            bool trim = (options & StringSplitOptions.TrimEntries) != 0;
            int start = 0;
            if (sep == null || sep._length == 0) { parts.Add(this); return parts.ToArray(); }
            while (true)
            {
                int j = IndexOf(sep, start);
                string part = j < 0 ? Substring(start) : Substring(start, j - start);
                if (trim) part = part.Trim();
                if (!removeEmpty || part.Length > 0) parts.Add(part);
                if (j < 0) break;
                start = j + sep._length;
            }
            return parts.ToArray();
        }
        public char[] ToCharArray()
        {
            var a = new char[_length];
            if (_length > 0) Mem.Copy(Rt.ArrayData(a), Data, _length * 2);
            return a;
        }
        public override string ToString() => this;
        public string ToString(IFormatProvider provider) => this;
        public object Clone() => this;

        public static string Format(string format, object arg0) => Formatting.Format(format, [arg0]);
        public static string Format(string format, object arg0, object arg1) => Formatting.Format(format, [arg0, arg1]);
        public static string Format(string format, object arg0, object arg1, object arg2) => Formatting.Format(format, [arg0, arg1, arg2]);
        public static string Format(string format, params object[] args) => Formatting.Format(format, args);
        public static string Format(IFormatProvider provider, string format, params object[] args) => Formatting.Format(format, args);

        public CharEnumerator GetEnumerator() => new CharEnumerator(this);
        IEnumerator<char> IEnumerable<char>.GetEnumerator() => new CharEnumerator(this);
        IEnumerator IEnumerable.GetEnumerator() => new CharEnumerator(this);
    }

    public sealed class CharEnumerator : IEnumerator<char>
    {
        readonly string _s; int _i = -1;
        internal CharEnumerator(string s) { _s = s; }
        public bool MoveNext() => ++_i < _s.Length;
        public char Current => _s[_i];
        object IEnumerator.Current => Current;
        public void Reset() { _i = -1; }
        public void Dispose() { }
    }

    internal static class Formatting
    {
        public static string Format(string format, object[] args)
        {
            if (format == null) throw new ArgumentNullException("format");
            var sb = new System.Text.StringBuilder();
            int i = 0, n = format.Length;
            while (i < n)
            {
                char c = format[i];
                if (c == '{')
                {
                    if (i + 1 < n && format[i + 1] == '{') { sb.Append('{'); i += 2; continue; }
                    int j = i + 1;
                    int index = 0;
                    while (j < n && format[j] >= '0' && format[j] <= '9') { index = index * 10 + (format[j] - '0'); j++; }
                    int align = 0; bool neg = false;
                    while (j < n && format[j] == ' ') j++;
                    if (j < n && format[j] == ',')
                    {
                        j++;
                        while (j < n && format[j] == ' ') j++;
                        if (j < n && format[j] == '-') { neg = true; j++; }
                        while (j < n && format[j] >= '0' && format[j] <= '9') { align = align * 10 + (format[j] - '0'); j++; }
                        if (neg) align = -align;
                    }
                    string fmt = null;
                    if (j < n && format[j] == ':')
                    {
                        int k = j + 1;
                        while (j < n && format[j] != '}') j++;
                        fmt = format.Substring(k, j - k);
                    }
                    if (j >= n || format[j] != '}') throw new FormatException("Input string was not in a correct format.");
                    if (index >= args.Length) throw new FormatException("Index (zero based) must be greater than or equal to zero and less than the size of the argument list.");
                    object a = args[index];
                    string s = a == null ? "" : fmt != null && a is IFormattable f ? f.ToString(fmt, null) : a.ToString();
                    if (align != 0) s = string.Align(s, align);
                    sb.Append(s);
                    i = j + 1;
                    continue;
                }
                if (c == '}')
                {
                    if (i + 1 < n && format[i + 1] == '}') { sb.Append('}'); i += 2; continue; }
                    throw new FormatException("Input string was not in a correct format.");
                }
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }
    }
}
