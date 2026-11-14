// Type, BitConverter, Random, Stopwatch.
#pragma warning disable CS0436, CS0660, CS0661
using System.Collections.Generic;
using Cs2Wasm.Runtime;

namespace System
{
    public sealed partial class Type : System.Reflection.MemberInfo
    {
        readonly int _id;
        static Dictionary<int, Type> s_types;
        Type(int id) { _id = id; }

        [Intrinsic] static extern string FullNameOf(int typeId);
        [Intrinsic] static extern string ShortNameOf(int typeId);
        [Intrinsic] static extern int FlagsOf(int typeId);

        /// <summary>One Type object per type, so typeof(A) == typeof(A) holds by reference.</summary>
        internal static Type FromId(int id)
        {
            s_types ??= new Dictionary<int, Type>();
            if (!s_types.TryGetValue(id, out var t)) { t = new Type(id); s_types.Add(id, t); }
            return t;
        }

        public override string Name => ShortNameOf(_id);
        public string FullName => FullNameOf(_id);
        public string Namespace
        {
            get
            {
                string f = FullNameOf(_id);
                int gen = f.IndexOf('[');
                string head = gen >= 0 ? f.Substring(0, gen) : f;
                int plus = head.IndexOf('+');
                if (plus >= 0) head = head.Substring(0, plus);
                int dot = head.LastIndexOf('.');
                return dot < 0 ? null : head.Substring(0, dot);
            }
        }
        public bool IsValueType => (FlagsOf(_id) & 1) != 0;
        public bool IsEnum => (FlagsOf(_id) & 2) != 0;
        public bool IsArray => (FlagsOf(_id) & 4) != 0;
        public bool IsInterface => (FlagsOf(_id) & 8) != 0;
        public bool IsGenericType => (FlagsOf(_id) & 16) != 0;
        public bool IsClass => !IsValueType && !IsInterface;
        public bool IsPrimitive => (FlagsOf(_id) & 32) != 0;
        public override string ToString() => FullNameOf(_id);
        public override bool Equals(object o) => Rt.AddressOf(this) == Rt.AddressOf(o);
        public override int GetHashCode() => _id;
        public static bool operator ==(Type left, Type right) => Rt.AddressOf(left) == Rt.AddressOf(right);
        public static bool operator !=(Type left, Type right) => Rt.AddressOf(left) != Rt.AddressOf(right);
        public static Type GetTypeFromHandle(RuntimeTypeHandle handle) => null;
    }

    public static class BitConverter
    {
        public static readonly bool IsLittleEndian = true;

        // NaN payloads never become observable (section 7.3): reading the bits of a NaN gives the canonical NaN.
        public static long DoubleToInt64Bits(double value) => value != value ? unchecked((long)0xFFF8000000000000UL) : Rt.F64Bits(value);
        public static double Int64BitsToDouble(long value) => Rt.F64FromBits(value);
        public static int SingleToInt32Bits(float value) => value != value ? unchecked((int)0xFFC00000) : Rt.F32Bits(value);
        public static float Int32BitsToSingle(int value) => Rt.F32FromBits(value);
        public static ulong DoubleToUInt64Bits(double value) => (ulong)DoubleToInt64Bits(value);
        public static double UInt64BitsToDouble(ulong value) => Rt.F64FromBits((long)value);

        public static byte[] GetBytes(bool value) => new byte[] { value ? (byte)1 : (byte)0 };
        public static byte[] GetBytes(char value) => GetBytes((short)value);
        public static byte[] GetBytes(short value) => new byte[] { (byte)value, (byte)(value >> 8) };
        public static byte[] GetBytes(ushort value) => GetBytes((short)value);
        public static byte[] GetBytes(int value) => new byte[] { (byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24) };
        public static byte[] GetBytes(uint value) => GetBytes((int)value);
        public static byte[] GetBytes(long value)
        {
            var b = new byte[8];
            for (int i = 0; i < 8; i++) b[i] = (byte)(value >> (8 * i));
            return b;
        }
        public static byte[] GetBytes(ulong value) => GetBytes((long)value);
        public static byte[] GetBytes(float value) => GetBytes(SingleToInt32Bits(value));
        public static byte[] GetBytes(double value) => GetBytes(DoubleToInt64Bits(value));

        static void Check(byte[] value, int startIndex, int size)
        {
            if (value == null) throw new ArgumentNullException("value");
            if ((uint)startIndex >= (uint)value.Length) throw new ArgumentOutOfRangeException("startIndex", "Index was out of range. Must be non-negative and less than the size of the collection.");
            if (startIndex > value.Length - size) throw new ArgumentException("Destination array is not long enough to copy all the items in the collection. Check array index and length.", "value");
        }
        public static short ToInt16(byte[] value, int startIndex) { Check(value, startIndex, 2); return (short)(value[startIndex] | (value[startIndex + 1] << 8)); }
        public static ushort ToUInt16(byte[] value, int startIndex) => (ushort)ToInt16(value, startIndex);
        public static char ToChar(byte[] value, int startIndex) => (char)ToInt16(value, startIndex);
        public static int ToInt32(byte[] value, int startIndex)
        {
            Check(value, startIndex, 4);
            return value[startIndex] | (value[startIndex + 1] << 8) | (value[startIndex + 2] << 16) | (value[startIndex + 3] << 24);
        }
        public static uint ToUInt32(byte[] value, int startIndex) => (uint)ToInt32(value, startIndex);
        public static long ToInt64(byte[] value, int startIndex)
        {
            Check(value, startIndex, 8);
            long r = 0;
            for (int i = 7; i >= 0; i--) r = (r << 8) | value[startIndex + i];
            return r;
        }
        public static ulong ToUInt64(byte[] value, int startIndex) => (ulong)ToInt64(value, startIndex);
        public static float ToSingle(byte[] value, int startIndex) => Int32BitsToSingle(ToInt32(value, startIndex));
        public static double ToDouble(byte[] value, int startIndex) => Int64BitsToDouble(ToInt64(value, startIndex));
        public static bool ToBoolean(byte[] value, int startIndex) { Check(value, startIndex, 1); return value[startIndex] != 0; }
        public static string ToString(byte[] value) => ToString(value, 0, value.Length);
        public static string ToString(byte[] value, int startIndex, int length)
        {
            if (length == 0) return "";
            var sb = new Text.StringBuilder();
            const string hex = "0123456789ABCDEF";
            for (int i = 0; i < length; i++)
            {
                if (i > 0) sb.Append('-');
                byte b = value[startIndex + i];
                sb.Append(hex[b >> 4]).Append(hex[b & 15]);
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// .NET's seeded Random algorithm (Knuth's subtractive generator, as in Net5CompatSeedImpl), so `new Random(seed)`
    /// gives .NET's sequence. The default seed comes from the engine (section 7.4), so peers agree.
    /// </summary>
    public class Random
    {
        readonly int[] _seedArray = new int[56];
        int _inext, _inextp;
        static Random s_shared;

        public Random() : this(Engine.RandomSeed()) { }
        public Random(int Seed)
        {
            int ii;
            int mj, mk;
            int subtraction = (Seed == int.MinValue) ? int.MaxValue : Math.Abs(Seed);
            mj = 161803398 - subtraction;
            _seedArray[55] = mj;
            mk = 1;
            for (int i = 1; i < 55; i++)
            {
                ii = (21 * i) % 55;
                _seedArray[ii] = mk;
                mk = mj - mk;
                if (mk < 0) mk += int.MaxValue;
                mj = _seedArray[ii];
            }
            for (int k = 1; k < 5; k++)
            {
                for (int i = 1; i < 56; i++)
                {
                    int n = i + 30;
                    if (n >= 55) n -= 55;
                    _seedArray[i] -= _seedArray[1 + n];
                    if (_seedArray[i] < 0) _seedArray[i] += int.MaxValue;
                }
            }
            _inext = 0;
            _inextp = 21;
        }

        public static Random Shared => s_shared ??= new Random();

        protected virtual double Sample() => InternalSample() * (1.0 / int.MaxValue);

        int InternalSample()
        {
            int retVal;
            int locINext = _inext, locINextp = _inextp;
            if (++locINext >= 56) locINext = 1;
            if (++locINextp >= 56) locINextp = 1;
            retVal = _seedArray[locINext] - _seedArray[locINextp];
            if (retVal == int.MaxValue) retVal--;
            if (retVal < 0) retVal += int.MaxValue;
            _seedArray[locINext] = retVal;
            _inext = locINext;
            _inextp = locINextp;
            return retVal;
        }

        double GetSampleForLargeRange()
        {
            int result = InternalSample();
            bool negative = InternalSample() % 2 == 0;
            if (negative) result = -result;
            double d = result;
            d += int.MaxValue - 1;
            d /= 2.0 * int.MaxValue - 1;
            return d;
        }

        public virtual int Next() => InternalSample();
        public virtual int Next(int maxValue)
        {
            if (maxValue < 0) throw new ArgumentOutOfRangeException("maxValue", "'maxValue' must be greater than zero.");
            return (int)(Sample() * maxValue);
        }
        public virtual int Next(int minValue, int maxValue)
        {
            if (minValue > maxValue) throw new ArgumentOutOfRangeException("minValue", "'minValue' cannot be greater than maxValue.");
            long range = (long)maxValue - minValue;
            return range <= int.MaxValue ? (int)(Sample() * range) + minValue : (int)((long)(GetSampleForLargeRange() * range) + minValue);
        }
        public virtual double NextDouble() => Sample();
        public virtual float NextSingle() => (float)Sample();
        public virtual void NextBytes(byte[] buffer)
        {
            for (int i = 0; i < buffer.Length; i++) buffer[i] = (byte)InternalSample();
        }
    }
}

namespace System.Reflection
{
    public abstract class MemberInfo
    {
        public abstract string Name { get; }
    }
}

namespace System.Diagnostics
{
    /// <summary>Elapsed time from the engine's monotonic clock (section 7.4).</summary>
    public class Stopwatch
    {
        long _start, _elapsed;
        bool _running;
        public static readonly long Frequency = 1000000000;
        public static readonly bool IsHighResolution = true;
        public static long GetTimestamp() => Engine.MonotonicNs();
        public static Stopwatch StartNew() { var s = new Stopwatch(); s.Start(); return s; }
        public void Start() { if (!_running) { _start = Engine.MonotonicNs(); _running = true; } }
        public void Stop() { if (_running) { _elapsed += Engine.MonotonicNs() - _start; _running = false; } }
        public void Reset() { _elapsed = 0; _running = false; }
        public void Restart() { _elapsed = 0; _start = Engine.MonotonicNs(); _running = true; }
        public bool IsRunning => _running;
        long Raw => _running ? _elapsed + Engine.MonotonicNs() - _start : _elapsed;
        public long ElapsedTicks => Raw;
        public long ElapsedMilliseconds => Raw / 1000000;
        public TimeSpan Elapsed => new TimeSpan(Raw / 100);
    }

    public static class Debug
    {
        public static void Assert(bool condition) { if (!condition) throw new InvalidOperationException("Assertion failed."); }
        public static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        public static void WriteLine(string message) { }
        public static void Write(string message) { }
    }
}
