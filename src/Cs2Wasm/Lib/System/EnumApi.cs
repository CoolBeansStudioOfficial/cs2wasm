#pragma warning disable CS0436
// Enum's static API over the per-type tables the compiler emits (EnumFormat): names, values, parsing, flags.
using System.Collections.Generic;
using Cs2Wasm.Runtime;

namespace System
{
    public abstract partial class Enum
    {
        // ---------------- values of an enum (boxed or generic) ----------------
        internal long RawValue => EnumFormat.ValueOf(this);

        public bool HasFlag(Enum flag)
        {
            if (flag == null) throw new ArgumentNullException("flag");
            long f = flag.RawValue;
            return (RawValue & f) == f;
        }

        public int CompareTo(object target)
        {
            if (target == null) return 1;
            if (target is not Enum e || Rt.TypeId(e) != Rt.TypeId(this)) throw new ArgumentException("Object must be the same type as the enum. The type passed in was '" + target.GetType() + "'; the enum type was '" + GetType() + "'.");
            int t = EnumFormat.Info(this);
            int size = Mem.LoadI32(t + 8);
            // as the underlying type's CompareTo: 1- and 2-byte types give the difference
            if (size <= 2) return (int)(RawValue - e.RawValue);
            if (Mem.LoadI32(t + 12) != 0) return ((ulong)RawValue).CompareTo((ulong)e.RawValue);
            return RawValue.CompareTo(e.RawValue);
        }

        public string ToString(string format)
        {
            if (string.IsNullOrEmpty(format) || format == "G" || format == "g") return ToString();
            int t = EnumFormat.Info(this);
            long v = RawValue;
            int size = Mem.LoadI32(t + 8);
            switch (format)
            {
                case "D": case "d": return Mem.LoadI32(t + 12) != 0 ? ((ulong)v & Mask(size)).ToString() : v.ToString();
                case "X": case "x":
                    {
                        var s = ((ulong)v & Mask(size)).ToString("X" + (size * 2));
                        return format == "x" ? s.ToLowerInvariant() : s;
                    }
                case "F": case "f": return EnumFormat.FormatFlags(t, v) ?? ToString();
            }
            throw new FormatException("Format string can be only \"G\", \"g\", \"X\", \"x\", \"F\", \"f\", \"D\" or \"d\".");
        }

        static ulong Mask(int size) => size >= 8 ? ulong.MaxValue : (1UL << (size * 8)) - 1;

        // ---------------- static API ----------------
        public static TEnum[] GetValues<TEnum>() where TEnum : struct, Enum
        {
            int t = EnumFormat.InfoOfId(Rt.MarkConstructed<TEnum>());
            int n = Mem.LoadI32(t);
            var r = new TEnum[n];
            for (int i = 0; i < n; i++) r[i] = EnumFormat.From<TEnum>(Mem.LoadI64(t + 16 + i * 16));
            return r;
        }
        public static Array GetValues(Type enumType)
        {
            int t = TableOf(enumType);
            int n = Mem.LoadI32(t);
            var r = new object[n];
            for (int i = 0; i < n; i++) r[i] = EnumFormat.Box(enumType, Mem.LoadI64(t + 16 + i * 16));
            return r;
        }
        public static string[] GetNames<TEnum>() where TEnum : struct, Enum => Names(EnumFormat.InfoOfId(Rt.MarkConstructed<TEnum>()));
        public static string[] GetNames(Type enumType) => Names(TableOf(enumType));
        static string[] Names(int t)
        {
            int n = Mem.LoadI32(t);
            var r = new string[n];
            for (int i = 0; i < n; i++) r[i] = Rt.Borrow<string>(Mem.LoadI32(t + 16 + i * 16 + 8));
            return r;
        }
        public static string GetName<TEnum>(TEnum value) where TEnum : struct, Enum => NameOf(EnumFormat.InfoOfId(Rt.MarkConstructed<TEnum>()), EnumFormat.ToInt64(value));
        public static string GetName(Type enumType, object value) => NameOf(TableOf(enumType), EnumFormat.AnyToInt64(value));
        static string NameOf(int t, long v)
        {
            int n = Mem.LoadI32(t);
            for (int i = 0; i < n; i++) if (Mem.LoadI64(t + 16 + i * 16) == v) return Rt.Borrow<string>(Mem.LoadI32(t + 16 + i * 16 + 8));
            return null;
        }
        public static bool IsDefined<TEnum>(TEnum value) where TEnum : struct, Enum => GetName(value) != null;
        public static bool IsDefined(Type enumType, object value)
        {
            if (value is string s) { int t = TableOf(enumType); foreach (var name in Names(t)) if (name == s) return true; return false; }
            return NameOf(TableOf(enumType), EnumFormat.AnyToInt64(value)) != null;
        }
        public static Type GetUnderlyingType(Type enumType)
        {
            int t = TableOf(enumType);
            int size = Mem.LoadI32(t + 8); bool u = Mem.LoadI32(t + 12) != 0;
            return size switch { 1 => u ? typeof(byte) : typeof(sbyte), 2 => u ? typeof(ushort) : typeof(short), 8 => u ? typeof(ulong) : typeof(long), _ => u ? typeof(uint) : typeof(int) };
        }

        public static TEnum Parse<TEnum>(string value) where TEnum : struct => Parse<TEnum>(value, false);
        public static TEnum Parse<TEnum>(string value, bool ignoreCase) where TEnum : struct
        {
            if (value == null) throw new ArgumentNullException("value");
            if (!TryParse<TEnum>(value, ignoreCase, out var r)) throw new ArgumentException("Requested value '" + value + "' was not found.");
            return r;
        }
        public static bool TryParse<TEnum>(string value, out TEnum result) where TEnum : struct => TryParse(value, false, out result);
        public static bool TryParse<TEnum>(string value, bool ignoreCase, out TEnum result) where TEnum : struct
        {
            result = default;
            int t = EnumFormat.InfoOfId(Rt.MarkConstructed<TEnum>());
            if (!ParseCore(t, value, ignoreCase, out long v)) return false;
            result = EnumFormat.From<TEnum>(v);
            return true;
        }
        public static object Parse(Type enumType, string value) => Parse(enumType, value, false);
        public static object Parse(Type enumType, string value, bool ignoreCase)
        {
            if (value == null) throw new ArgumentNullException("value");
            if (!ParseCore(TableOf(enumType), value, ignoreCase, out long v)) throw new ArgumentException("Requested value '" + value + "' was not found.");
            return EnumFormat.Box(enumType, v);
        }
        public static bool TryParse(Type enumType, string value, out object result)
        {
            result = null;
            if (value == null || !ParseCore(TableOf(enumType), value, false, out long v)) return false;
            result = EnumFormat.Box(enumType, v);
            return true;
        }

        static bool ParseCore(int t, string value, bool ignoreCase, out long v)
        {
            v = 0;
            if (value == null) return false;
            var s = value.Trim();
            if (s.Length == 0) return false;
            if (char.IsDigit(s[0]) || s[0] == '-' || s[0] == '+') return long.TryParse(s, out v);
            foreach (var part in s.Split(','))
            {
                var p = part.Trim();
                bool found = false;
                int n = Mem.LoadI32(t);
                for (int i = 0; i < n && !found; i++)
                {
                    var name = Rt.Borrow<string>(Mem.LoadI32(t + 16 + i * 16 + 8));
                    if (ignoreCase ? string.Equals(name, p, StringComparison.OrdinalIgnoreCase) : name == p) { v |= Mem.LoadI64(t + 16 + i * 16); found = true; }
                }
                if (!found) return false;
            }
            return true;
        }

        static int TableOf(Type enumType)
        {
            if (enumType == null) throw new ArgumentNullException("enumType");
            if (!enumType.IsEnum) throw new ArgumentException("Type provided must be an Enum.");
            return EnumFormat.InfoOfId(enumType.Id);
        }
    }

    internal static partial class EnumFormat
    {
        [Intrinsic] public static extern int InfoOfId(int typeId);

        public static long ValueOf(object e) { int t = Info(e); return Value(e, Mem.LoadI32(t + 8)); }

        public static string FormatFlags(int t, long v)
        {
            int count = Mem.LoadI32(t);
            long rest = v; string result = null;
            for (int i = count - 1; i >= 0; i--)
            {
                int entry = t + 16 + i * 16;
                long ev = Mem.LoadI64(entry);
                if (ev != 0 && (rest & ev) == ev)
                {
                    rest &= ~ev;
                    string name = Rt.Borrow<string>(Mem.LoadI32(entry + 8));
                    result = result == null ? name : name + ", " + result;
                }
            }
            return rest == 0 ? result : null;
        }

        public static T From<T>(long v)
        {
            var a = new T[1];
            int addr = Rt.AddressOf(a) + 16;
            int size = Rt.SizeOf<T>();
            if (size == 1) Mem.StoreU8(addr, (int)v); else if (size == 2) Mem.StoreU16(addr, (int)v); else if (size == 8) Mem.StoreI64(addr, v); else Mem.StoreI32(addr, (int)v);
            return a[0];
        }
        public static long ToInt64<T>(T value)
        {
            var a = new T[] { value };
            int addr = Rt.AddressOf(a) + 16;
            int size = Rt.SizeOf<T>();
            return size == 1 ? Mem.LoadI8(addr) : size == 2 ? Mem.LoadI16(addr) : size == 8 ? Mem.LoadI64(addr) : Mem.LoadI32(addr);
        }
        public static long AnyToInt64(object value)
        {
            switch (value)
            {
                case null: throw new ArgumentNullException("value");
                case Enum e: return e.RawValue;
                case int i: return i;
                case long l: return l;
                case short s: return s;
                case sbyte sb: return sb;
                case byte b: return b;
                case ushort us: return us;
                case uint ui: return ui;
                case ulong ul: return (long)ul;
            }
            throw new ArgumentException("The value passed in must be an enum base or an underlying type for an enum, such as an Int32.");
        }
        public static object Box(Type enumType, long v)
        {
            int t = InfoOfId(enumType.Id);
            int size = Mem.LoadI32(t + 8);
            int o = Rt.AllocObject(size == 8 ? 24 : 16, enumType.Id);
            if (size == 1) Mem.StoreU8(o + 12, (int)v); else if (size == 2) Mem.StoreU16(o + 12, (int)v); else if (size == 8) Mem.StoreI64(o + 16, v); else Mem.StoreI32(o + 12, (int)v);
            return Rt.Take<object>(o);
        }
    }
}
