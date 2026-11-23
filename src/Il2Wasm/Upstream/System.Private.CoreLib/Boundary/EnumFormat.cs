using System;

namespace Il2Wasm.Runtime
{
    /// <summary>
    /// Enum formatting over the compiler's table of each enum (its runtime metadata: [count, flags, size, unsigned],
    /// then (value: i64, name) per member, sorted by unsigned value), for Enum.ToString() / ToString(format) /
    /// ISpanFormattable.TryFormat (manifest substitutions). Upstream's algorithms (Enum.cs: GetNameInlined,
    /// FormatFlagNames, GetSingleFlagsEnumNameForValue, TryFindFlagsNames, FormatNumberAsHex) over one ulong of the
    /// value's bits instead of a generic instantiation per storage type, so a boxed enum does not bring in eight copies
    /// of enum and number formatting. Values compare as the storage type's unsigned bits, as upstream's do.
    /// </summary>
    internal static class EnumFormat
    {
        public static string ToString(Enum e) => Format(e, 'G');

        public static string ToString(Enum e, string? format)
        {
            if (string.IsNullOrEmpty(format)) return Format(e, 'G');
            if (format.Length != 1) throw new FormatException(SR.Format_InvalidEnumFormatSpecification);
            return Format(e, format[0]);
        }

        public static bool TryFormat(Enum e, Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
        {
            char f = 'G';
            if (!format.IsEmpty)
            {
                if (format.Length != 1) throw new FormatException(SR.Format_InvalidEnumFormatSpecification);
                f = format[0];
            }
            string s = Format(e, f);
            if (s.Length > destination.Length) { charsWritten = 0; return false; }
            s.AsSpan().CopyTo(destination);
            charsWritten = s.Length;
            return true;
        }

        static string Format(Enum e, char format)
        {
            int id = Rt.TypeId(e);
            int t = TypeInfo.EnumInfo(id);
            int size = Mem.LoadI32(t + 8);
            bool unsigned = Mem.LoadI32(t + 12) != 0;
            int data = Rt.AddressOf(e) + TypeInfo.BoxOffset(id);
            ulong bits = size switch
            {
                1 => (ulong)Mem.LoadU8(data),
                2 => (ulong)Mem.LoadU16(data),
                8 => (ulong)Mem.LoadI64(data),
                _ => (uint)Mem.LoadI32(data),
            };
            string? result;
            switch (format | 0x20)
            {
                case 'g':
                    result = Mem.LoadI32(t + 4) != 0 ? FlagNames(t, size, bits) : Name(t, size, bits);
                    return result ?? Number(bits, size, unsigned);
                case 'd':
                    return Number(bits, size, unsigned);
                case 'x':
                    return Hex(bits, size);
                case 'f':
                    return FlagNames(t, size, bits) ?? Number(bits, size, unsigned);
                default:
                    throw new FormatException(SR.Format_InvalidEnumFormatSpecification);
            }
        }

        static ulong Mask(int size) => size == 8 ? ulong.MaxValue : (1UL << (size * 8)) - 1;
        static ulong ValueAt(int t, int i, int size) => (ulong)Mem.LoadI64(t + 16 + i * 16) & Mask(size);
        static string NameAt(int t, int i) => Rt.Borrow<string>(Mem.LoadI32(t + 16 + i * 16 + 8));

        /// <summary>The underlying value as a number (signed per the underlying type).</summary>
        static string Number(ulong bits, int size, bool unsigned)
        {
            if (unsigned) return bits.ToString();
            int shift = 64 - size * 8;
            long signed = (long)(bits << shift) >> shift;
            return signed.ToString();
        }

        /// <summary>FormatNumberAsHex: two uppercase digits per byte of the storage.</summary>
        static string Hex(ulong bits, int size)
        {
            Span<char> buf = stackalloc char[16];
            int n = size * 2;
            for (int i = n - 1; i >= 0; i--)
            {
                int d = (int)(bits & 0xF);
                buf[i] = (char)(d < 10 ? '0' + d : 'A' + d - 10);
                bits >>= 4;
            }
            return new string(buf.Slice(0, n));
        }

        /// <summary>GetNameInlined: the name of exactly this value (a binary search over the sorted values), or null.</summary>
        static string? Name(int t, int size, ulong value)
        {
            int lo = 0, hi = Mem.LoadI32(t) - 1;
            while (lo <= hi)
            {
                int i = lo + ((hi - lo) >> 1);
                ulong v = ValueAt(t, i, size);
                if (v == value) return NameAt(t, i);
                if (v < value) lo = i + 1; else hi = i - 1;
            }
            return null;
        }

        /// <summary>FormatFlagNames: one name, or the names whose values or together to the value (", "), or null.</summary>
        static string? FlagNames(int t, int size, ulong value)
        {
            int n = Mem.LoadI32(t);
            if (value == 0) return n > 0 && ValueAt(t, 0, size) == 0 ? NameAt(t, 0) : "0";
            int index;
            for (index = n - 1; (uint)index < (uint)n; index--)
            {
                ulong v = ValueAt(t, index, size);
                if (v <= value)
                {
                    if (v == value) return NameAt(t, index);
                    break;
                }
            }
            Span<int> found = stackalloc int[64];
            int count = 0;
            ulong rest = value;
            while ((uint)index < (uint)n)
            {
                ulong current = ValueAt(t, index, size);
                if (index == 0 && current == 0) break;
                if ((rest & current) == current)
                {
                    rest &= ~current;
                    found[count++] = index;
                    if (rest == 0) break;
                }
                index--;
            }
            if (rest != 0) return null;
            var sb = new System.Text.StringBuilder();
            for (int i = count - 1; i >= 0; i--)
            {
                sb.Append(NameAt(t, found[i]));
                if (i != 0) sb.Append(", ");
            }
            return sb.ToString();
        }
    }
}
