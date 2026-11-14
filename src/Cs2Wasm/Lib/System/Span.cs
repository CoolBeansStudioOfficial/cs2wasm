// Span<T> and ReadOnlySpan<T> over arrays (spans over stack or native memory are not supported yet).
#pragma warning disable CS0436, CS0660, CS0661, CS0809
using System.Collections.Generic;

namespace System
{
    public readonly ref struct Span<T>
    {
        internal readonly T[] _array;
        internal readonly int _start;
        readonly int _length;

        public Span(T[] array) { _array = array; _start = 0; _length = array == null ? 0 : array.Length; }
        public Span(T[] array, int start, int length)
        {
            if (array == null) { if (start != 0 || length != 0) throw new ArgumentOutOfRangeException(); _array = null; _start = 0; _length = 0; return; }
            if ((uint)start > (uint)array.Length || (uint)length > (uint)(array.Length - start)) throw new ArgumentOutOfRangeException();
            _array = array; _start = start; _length = length;
        }

        public int Length => _length;
        public bool IsEmpty => _length == 0;
        public static Span<T> Empty => default;

        public ref T this[int index]
        {
            get
            {
                if ((uint)index >= (uint)_length) throw new IndexOutOfRangeException();
                return ref _array[_start + index];
            }
        }

        public ref T GetPinnableReference() { if (_length == 0) return ref Cs2Wasm.Runtime.Rt.NullRef<T>(); return ref _array[_start]; }
        public Span<T> Slice(int start) => Slice(start, _length - start);
        public Span<T> Slice(int start, int length)
        {
            if ((uint)start > (uint)_length || (uint)length > (uint)(_length - start)) throw new ArgumentOutOfRangeException();
            return new Span<T>(_array, _start + start, length);
        }
        public void Fill(T value) { for (int i = 0; i < _length; i++) _array[_start + i] = value; }
        public void Clear() { for (int i = 0; i < _length; i++) _array[_start + i] = default; }
        public void CopyTo(Span<T> destination)
        {
            if (destination.Length < _length) throw new ArgumentException("Destination is too short.", "destination");
            for (int i = 0; i < _length; i++) destination._array[destination._start + i] = _array[_start + i];
        }
        public T[] ToArray()
        {
            var a = new T[_length];
            for (int i = 0; i < _length; i++) a[i] = _array[_start + i];
            return a;
        }
        public Enumerator GetEnumerator() => new Enumerator(this);
        public static implicit operator Span<T>(T[] array) => new Span<T>(array);
        public static implicit operator ReadOnlySpan<T>(Span<T> span) => new ReadOnlySpan<T>(span._array, span._start, span._length);
        public override string ToString() => "System.Span<" + Cs2Wasm.Runtime.Rt.TypeName(_array) + ">[" + _length.ToString() + "]";

        public ref struct Enumerator
        {
            readonly Span<T> _span; int _index;
            internal Enumerator(Span<T> s) { _span = s; _index = -1; }
            public bool MoveNext() { int i = _index + 1; if (i < _span.Length) { _index = i; return true; } return false; }
            public ref T Current => ref _span[_index];
        }
    }

    public readonly ref struct ReadOnlySpan<T>
    {
        internal readonly T[] _array;
        internal readonly int _start;
        readonly int _length;

        public ReadOnlySpan(T[] array) { _array = array; _start = 0; _length = array == null ? 0 : array.Length; }
        public ReadOnlySpan(T[] array, int start, int length)
        {
            if (array == null) { _array = null; _start = 0; _length = 0; return; }
            if ((uint)start > (uint)array.Length || (uint)length > (uint)(array.Length - start)) throw new ArgumentOutOfRangeException();
            _array = array; _start = start; _length = length;
        }

        public ref readonly T GetPinnableReference() { if (_length == 0) return ref Cs2Wasm.Runtime.Rt.NullRef<T>(); return ref _array[_start]; }
        public int Length => _length;
        public bool IsEmpty => _length == 0;
        public static ReadOnlySpan<T> Empty => default;

        public ref readonly T this[int index]
        {
            get
            {
                if ((uint)index >= (uint)_length) throw new IndexOutOfRangeException();
                return ref _array[_start + index];
            }
        }

        public ReadOnlySpan<T> Slice(int start) => Slice(start, _length - start);
        public ReadOnlySpan<T> Slice(int start, int length)
        {
            if ((uint)start > (uint)_length || (uint)length > (uint)(_length - start)) throw new ArgumentOutOfRangeException();
            return new ReadOnlySpan<T>(_array, _start + start, length);
        }
        public T[] ToArray()
        {
            var a = new T[_length];
            for (int i = 0; i < _length; i++) a[i] = _array[_start + i];
            return a;
        }
        public void CopyTo(Span<T> destination)
        {
            if (destination.Length < _length) throw new ArgumentException("Destination is too short.", "destination");
            for (int i = 0; i < _length; i++) destination._array[destination._start + i] = _array[_start + i];
        }
        public Enumerator GetEnumerator() => new Enumerator(this);
        public static implicit operator ReadOnlySpan<T>(T[] array) => new ReadOnlySpan<T>(array);
        public override string ToString() => typeof(T) == typeof(char) ? new string(ToArray() as char[]) : "System.ReadOnlySpan<" + typeof(T).Name + ">[" + _length.ToString() + "]";

        public ref struct Enumerator
        {
            readonly ReadOnlySpan<T> _span; int _index;
            internal Enumerator(ReadOnlySpan<T> s) { _span = s; _index = -1; }
            public bool MoveNext() { int i = _index + 1; if (i < _span.Length) { _index = i; return true; } return false; }
            public ref readonly T Current => ref _span[_index];
        }
    }

    public static class MemoryExtensions
    {
        public static Span<T> AsSpan<T>(this T[] array) => new Span<T>(array);
        public static ReadOnlySpan<char> AsSpan(this string text) => text;
        public static ReadOnlySpan<char> AsSpan(this string text, int start) => ((ReadOnlySpan<char>)text).Slice(start);
        public static ReadOnlySpan<char> AsSpan(this string text, int start, int length) => ((ReadOnlySpan<char>)text).Slice(start, length);
        public static Span<T> AsSpan<T>(this T[] array, int start) => new Span<T>(array, start, array.Length - start);
        public static Span<T> AsSpan<T>(this T[] array, int start, int length) => new Span<T>(array, start, length);
        public static bool SequenceEqual<T>(this ReadOnlySpan<T> span, ReadOnlySpan<T> other) where T : IEquatable<T>
        {
            if (span.Length != other.Length) return false;
            for (int i = 0; i < span.Length; i++) if (!EqualityComparer<T>.Default.Equals(span[i], other[i])) return false;
            return true;
        }
    }
}
