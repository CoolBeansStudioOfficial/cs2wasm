// Invariant globalization (section 7.4): every culture is the invariant culture.
#pragma warning disable CS0436
namespace System.Globalization
{
    public class CultureInfo : IFormatProvider
    {
        static readonly CultureInfo s_invariant = new CultureInfo();
        public static CultureInfo InvariantCulture => s_invariant;
        public static CultureInfo CurrentCulture { get => s_invariant; set { } }
        public static CultureInfo CurrentUICulture { get => s_invariant; set { } }
        public static CultureInfo DefaultThreadCurrentCulture { get => s_invariant; set { } }
        public static CultureInfo GetCultureInfo(string name) => s_invariant;
        public virtual string Name => "";
        public virtual string DisplayName => "Invariant Language (Invariant Country)";
        public virtual NumberFormatInfo NumberFormat => NumberFormatInfo.InvariantInfo;
        public virtual object GetFormat(Type formatType) => null;
        public override string ToString() => "";
    }

    public sealed class NumberFormatInfo : IFormatProvider
    {
        public static NumberFormatInfo InvariantInfo { get; } = new NumberFormatInfo();
        public static NumberFormatInfo CurrentInfo => InvariantInfo;
        public string NumberDecimalSeparator => ".";
        public string NumberGroupSeparator => ",";
        public string NegativeSign => "-";
        public string PositiveSign => "+";
        public object GetFormat(Type formatType) => null;
    }

    [Flags]
    public enum NumberStyles { None = 0, AllowLeadingWhite = 1, AllowTrailingWhite = 2, AllowLeadingSign = 4, Integer = 7, AllowDecimalPoint = 32, AllowExponent = 128, Float = 167, AllowThousands = 64, Number = 111, Any = 511, HexNumber = 515, AllowHexSpecifier = 512 }
}

namespace System
{
    /// <summary>xxHash32 like .NET's HashCode, with a fixed seed so hashes match on every machine (section 7.3).</summary>
    public struct HashCode
    {
        const uint Prime1 = 2654435761U, Prime2 = 2246822519U, Prime3 = 3266489917U, Prime4 = 668265263U, Prime5 = 374761393U;
        const uint Seed = 0;
        uint _v1, _v2, _v3, _v4, _queue1, _queue2, _queue3, _length;

        static uint Rol(uint v, int n) => (v << n) | (v >> (32 - n));
        static uint Round(uint hash, uint input) => Rol(hash + input * Prime2, 13) * Prime1;
        static uint QueueRound(uint hash, uint queued) => Rol(hash + queued * Prime3, 17) * Prime4;
        static uint MixState(uint v1, uint v2, uint v3, uint v4) => Rol(v1, 1) + Rol(v2, 7) + Rol(v3, 12) + Rol(v4, 18);
        static uint MixEmpty() => Seed + Prime5;
        static uint MixFinal(uint hash)
        {
            hash ^= hash >> 15; hash *= Prime2; hash ^= hash >> 13; hash *= Prime3; hash ^= hash >> 16;
            return hash;
        }
        static void Init(out uint v1, out uint v2, out uint v3, out uint v4)
        {
            unchecked { v1 = Seed + Prime1 + Prime2; v2 = Seed + Prime2; v3 = Seed; v4 = Seed - Prime1; }
        }
        static uint H<T>(T value) => value == null ? 0 : (uint)value.GetHashCode();

        public static int Combine<T1>(T1 value1)
        {
            uint hc1 = H(value1);
            uint hash = MixEmpty(); hash += 4; hash = QueueRound(hash, hc1);
            return (int)MixFinal(hash);
        }
        public static int Combine<T1, T2>(T1 value1, T2 value2)
        {
            uint hc1 = H(value1), hc2 = H(value2);
            uint hash = MixEmpty(); hash += 8; hash = QueueRound(hash, hc1); hash = QueueRound(hash, hc2);
            return (int)MixFinal(hash);
        }
        public static int Combine<T1, T2, T3>(T1 value1, T2 value2, T3 value3)
        {
            uint hc1 = H(value1), hc2 = H(value2), hc3 = H(value3);
            uint hash = MixEmpty(); hash += 12; hash = QueueRound(hash, hc1); hash = QueueRound(hash, hc2); hash = QueueRound(hash, hc3);
            return (int)MixFinal(hash);
        }
        public static int Combine<T1, T2, T3, T4>(T1 value1, T2 value2, T3 value3, T4 value4)
        {
            uint hc1 = H(value1), hc2 = H(value2), hc3 = H(value3), hc4 = H(value4);
            Init(out uint v1, out uint v2, out uint v3, out uint v4);
            v1 = Round(v1, hc1); v2 = Round(v2, hc2); v3 = Round(v3, hc3); v4 = Round(v4, hc4);
            uint hash = MixState(v1, v2, v3, v4); hash += 16;
            return (int)MixFinal(hash);
        }
        public static int Combine<T1, T2, T3, T4, T5>(T1 value1, T2 value2, T3 value3, T4 value4, T5 value5)
        {
            uint hc1 = H(value1), hc2 = H(value2), hc3 = H(value3), hc4 = H(value4), hc5 = H(value5);
            Init(out uint v1, out uint v2, out uint v3, out uint v4);
            v1 = Round(v1, hc1); v2 = Round(v2, hc2); v3 = Round(v3, hc3); v4 = Round(v4, hc4);
            uint hash = MixState(v1, v2, v3, v4); hash += 20; hash = QueueRound(hash, hc5);
            return (int)MixFinal(hash);
        }
        public static int Combine<T1, T2, T3, T4, T5, T6>(T1 value1, T2 value2, T3 value3, T4 value4, T5 value5, T6 value6)
        {
            uint hc1 = H(value1), hc2 = H(value2), hc3 = H(value3), hc4 = H(value4), hc5 = H(value5), hc6 = H(value6);
            Init(out uint v1, out uint v2, out uint v3, out uint v4);
            v1 = Round(v1, hc1); v2 = Round(v2, hc2); v3 = Round(v3, hc3); v4 = Round(v4, hc4);
            uint hash = MixState(v1, v2, v3, v4); hash += 24; hash = QueueRound(hash, hc5); hash = QueueRound(hash, hc6);
            return (int)MixFinal(hash);
        }
        public static int Combine<T1, T2, T3, T4, T5, T6, T7>(T1 value1, T2 value2, T3 value3, T4 value4, T5 value5, T6 value6, T7 value7)
        {
            uint hc1 = H(value1), hc2 = H(value2), hc3 = H(value3), hc4 = H(value4), hc5 = H(value5), hc6 = H(value6), hc7 = H(value7);
            Init(out uint v1, out uint v2, out uint v3, out uint v4);
            v1 = Round(v1, hc1); v2 = Round(v2, hc2); v3 = Round(v3, hc3); v4 = Round(v4, hc4);
            uint hash = MixState(v1, v2, v3, v4); hash += 28; hash = QueueRound(hash, hc5); hash = QueueRound(hash, hc6); hash = QueueRound(hash, hc7);
            return (int)MixFinal(hash);
        }
        public static int Combine<T1, T2, T3, T4, T5, T6, T7, T8>(T1 value1, T2 value2, T3 value3, T4 value4, T5 value5, T6 value6, T7 value7, T8 value8)
        {
            uint hc1 = H(value1), hc2 = H(value2), hc3 = H(value3), hc4 = H(value4), hc5 = H(value5), hc6 = H(value6), hc7 = H(value7), hc8 = H(value8);
            Init(out uint v1, out uint v2, out uint v3, out uint v4);
            v1 = Round(v1, hc1); v2 = Round(v2, hc2); v3 = Round(v3, hc3); v4 = Round(v4, hc4);
            v1 = Round(v1, hc5); v2 = Round(v2, hc6); v3 = Round(v3, hc7); v4 = Round(v4, hc8);
            uint hash = MixState(v1, v2, v3, v4); hash += 32;
            return (int)MixFinal(hash);
        }

        public void Add<T>(T value) => Add((int)H(value));

        void Add(int value)
        {
            uint val = (uint)value;
            uint previousLength = _length++;
            uint position = previousLength % 4;
            if (position == 0) _queue1 = val;
            else if (position == 1) _queue2 = val;
            else if (position == 2) _queue3 = val;
            else
            {
                if (previousLength == 3) Init(out _v1, out _v2, out _v3, out _v4);
                _v1 = Round(_v1, _queue1); _v2 = Round(_v2, _queue2); _v3 = Round(_v3, _queue3); _v4 = Round(_v4, val);
            }
        }

        public int ToHashCode()
        {
            uint length = _length;
            uint position = length % 4;
            uint hash = length < 4 ? MixEmpty() : MixState(_v1, _v2, _v3, _v4);
            hash += length * 4;
            if (position > 0) { hash = QueueRound(hash, _queue1); if (position > 1) { hash = QueueRound(hash, _queue2); if (position > 2) hash = QueueRound(hash, _queue3); } }
            return (int)MixFinal(hash);
        }
    }
}
