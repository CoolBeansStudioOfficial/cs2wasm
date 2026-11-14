#pragma warning disable CS0436
using Cs2Wasm.Runtime;

namespace System.Text
{
    public sealed class StringBuilder
    {
        char[] _chars;
        int _length;

        public StringBuilder() { _chars = new char[16]; }
        public StringBuilder(int capacity) { _chars = new char[capacity < 1 ? 16 : capacity]; }
        public StringBuilder(string value) { _chars = new char[value == null || value.Length < 16 ? 16 : value.Length]; Append(value); }
        public StringBuilder(string value, int capacity) { _chars = new char[capacity < 16 ? 16 : capacity]; Append(value); }

        public int Length
        {
            get => _length;
            set
            {
                if (value < 0) throw new ArgumentOutOfRangeException("value", "Length cannot be less than zero.");
                Ensure(value);
                for (int i = _length; i < value; i++) _chars[i] = '\0';
                _length = value;
            }
        }
        public int Capacity => _chars.Length;

        public char this[int index]
        {
            get { if ((uint)index >= (uint)_length) throw new IndexOutOfRangeException(); return _chars[index]; }
            set { if ((uint)index >= (uint)_length) throw new IndexOutOfRangeException(); _chars[index] = value; }
        }

        void Ensure(int n)
        {
            if (n <= _chars.Length) return;
            int c = _chars.Length * 2;
            if (c < n) c = n;
            var nc = new char[c];
            if (_length > 0) Mem.Copy(Rt.ArrayData(nc), Rt.ArrayData(_chars), _length * 2);
            _chars = nc;
        }

        public StringBuilder Append(string value)
        {
            if (value == null) return this;
            int n = value.Length;
            if (n == 0) return this;
            Ensure(_length + n);
            Mem.Copy(Rt.ArrayData(_chars) + _length * 2, Rt.StringData(value), n * 2);
            _length += n;
            return this;
        }
        public StringBuilder Append(string value, int startIndex, int count) => Append(value.Substring(startIndex, count));
        public StringBuilder Append(char value)
        {
            Ensure(_length + 1);
            _chars[_length++] = value;
            return this;
        }
        public StringBuilder Append(char value, int repeatCount)
        {
            Ensure(_length + repeatCount);
            for (int i = 0; i < repeatCount; i++) _chars[_length++] = value;
            return this;
        }
        public StringBuilder Append(char[] value) { if (value != null) Append(new string(value)); return this; }
        public StringBuilder Append(object value) => value == null ? this : Append(value.ToString());
        public StringBuilder Append(bool value) => Append(value ? "True" : "False");
        public StringBuilder Append(int value) => Append(Number.Int64ToString(value));
        public StringBuilder Append(uint value) => Append(Number.UInt64ToString(value));
        public StringBuilder Append(long value) => Append(Number.Int64ToString(value));
        public StringBuilder Append(ulong value) => Append(Number.UInt64ToString(value));
        public StringBuilder Append(short value) => Append(Number.Int64ToString(value));
        public StringBuilder Append(ushort value) => Append(Number.UInt64ToString(value));
        public StringBuilder Append(byte value) => Append(Number.UInt64ToString(value));
        public StringBuilder Append(sbyte value) => Append(Number.Int64ToString(value));
        public StringBuilder Append(float value) => Append(value.ToString());
        public StringBuilder Append(double value) => Append(value.ToString());
        public StringBuilder Append(StringBuilder value) => value == null ? this : Append(value.ToString());
        public StringBuilder AppendLine() => Append('\n');
        public StringBuilder AppendLine(string value) { Append(value); return Append('\n'); }
        public StringBuilder AppendFormat(string format, object arg0) => Append(string.Format(format, arg0));
        public StringBuilder AppendFormat(string format, object arg0, object arg1) => Append(string.Format(format, arg0, arg1));
        public StringBuilder AppendFormat(string format, object arg0, object arg1, object arg2) => Append(string.Format(format, arg0, arg1, arg2));
        public StringBuilder AppendFormat(string format, params object[] args) => Append(string.Format(format, args));
        public StringBuilder AppendFormat(string format, ReadOnlySpan<object> args) => Append(string.Format(format, args.ToArray()));
        public StringBuilder AppendJoin(string separator, ReadOnlySpan<object> values) => Append(string.Join(separator, values.ToArray()));
        public StringBuilder AppendJoin(string separator, ReadOnlySpan<string> values) => Append(string.Join(separator, values.ToArray()));
        public StringBuilder AppendJoin(string separator, params object[] values) => Append(string.Join(separator, values));
        public StringBuilder AppendJoin(string separator, params string[] values) => Append(string.Join(separator, values));

        public StringBuilder Insert(int index, string value)
        {
            if ((uint)index > (uint)_length) throw new ArgumentOutOfRangeException("index", "Index was out of range. Must be non-negative and less than or equal to the size of the collection.");
            if (value == null || value.Length == 0) return this;
            int n = value.Length;
            Ensure(_length + n);
            for (int i = _length - 1; i >= index; i--) _chars[i + n] = _chars[i];
            for (int i = 0; i < n; i++) _chars[index + i] = value[i];
            _length += n;
            return this;
        }
        public StringBuilder Insert(int index, char value) => Insert(index, string.Ctor(value, 1));
        public StringBuilder Insert(int index, object value) => value == null ? this : Insert(index, value.ToString());
        public StringBuilder Insert(int index, int value) => Insert(index, Number.Int64ToString(value));

        public StringBuilder Remove(int startIndex, int length)
        {
            if (startIndex < 0 || length < 0 || startIndex > _length - length) throw new ArgumentOutOfRangeException("startIndex");
            for (int i = startIndex; i + length < _length; i++) _chars[i] = _chars[i + length];
            _length -= length;
            return this;
        }
        public StringBuilder Replace(char oldChar, char newChar)
        {
            for (int i = 0; i < _length; i++) if (_chars[i] == oldChar) _chars[i] = newChar;
            return this;
        }
        public StringBuilder Replace(string oldValue, string newValue)
        {
            string s = ToString().Replace(oldValue, newValue);
            _length = 0;
            return Append(s);
        }
        public StringBuilder Clear() { _length = 0; return this; }
        public override string ToString() => _length == 0 ? "" : new string(_chars, 0, _length);
        public string ToString(int startIndex, int length) => new string(_chars, startIndex, length);
        public bool Equals(StringBuilder sb) => sb != null && sb.ToString() == ToString();
    }

    public abstract class Encoding { }
}
