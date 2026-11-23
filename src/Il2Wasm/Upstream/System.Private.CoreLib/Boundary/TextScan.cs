// il2wasm: the scans over text that .NET runs sixteen bytes at a time (Vector128), written with the 128-bit
// instructions every WASM engine has (SPEC section 5). Upstream's own vectorized paths are written against
// Vector128<T>, which this compiler does not turn into those instructions; these few loops are where the time is: a
// character looked for, the separators of a Split, the case of ASCII text. Each has the same result as the scalar
// loop it stands in for, which still does the last few characters.

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Il2Wasm.Runtime
{
    internal static unsafe class TextScan
    {
        /// <summary>Eight chars at the address: bit i is set when char i is the value.</summary>
        [Intrinsic] static extern int Eq16(int addr, int value);
        /// <summary>The same for any of three values.</summary>
        [Intrinsic] static extern int Eq16Any3(int addr, int a, int b, int c);
        /// <summary>Eight chars from src to dst with a..z made A..Z (the others as they are); not zero when one of
        /// them is not ASCII.</summary>
        [Intrinsic] static extern int Upper16(int src, int dst);
        /// <summary>The same with A..Z made a..z.</summary>
        [Intrinsic] static extern int Lower16(int src, int dst);

        public static int IndexOfChar(ref char searchSpace, char value, int length)
        {
            int p = (int)Unsafe.AsPointer(ref searchSpace);
            int i = 0;
            for (; i + 8 <= length; i += 8)
            {
                int m = Eq16(p + 2 * i, value);
                if (m != 0) return i + BitOperations.TrailingZeroCount(m);
            }
            for (; i < length; i++) if (Mem.LoadU16(p + 2 * i) == value) return i;
            return -1;
        }

        /// <summary>string.Split's list of the places of up to three separator chars.</summary>
        public static void Separators(ReadOnlySpan<char> source, ref ValueListBuilder<int> list, char c0, char c1, char c2)
        {
            int p = (int)Unsafe.AsPointer(ref MemoryMarshal.GetReference(source));
            int n = source.Length, i = 0;
            for (; i + 8 <= n; i += 8)
            {
                int m = Eq16Any3(p + 2 * i, c0, c1, c2);
                while (m != 0) { list.Append(i + BitOperations.TrailingZeroCount(m)); m &= m - 1; }
            }
            for (; i < n; i++)
            {
                int c = Mem.LoadU16(p + 2 * i);
                if (c == c0 || c == c1 || c == c2) list.Append(i);
            }
        }

        /// <summary>Ascii.ChangeCase from chars to chars: how many chars were done, a multiple of eight, up to the
        /// first eight with a char that is not ASCII (the scalar loop goes on from there, and stops at that char).</summary>
        public static nuint ChangeCase16(int src, int dst, int count, bool upper)
        {
            int i = 0;
            if (upper) { for (; i + 8 <= count; i += 8) if (Upper16(src + 2 * i, dst + 2 * i) != 0) break; }
            else { for (; i + 8 <= count; i += 8) if (Lower16(src + 2 * i, dst + 2 * i) != 0) break; }
            return (nuint)i;
        }
    }
}
