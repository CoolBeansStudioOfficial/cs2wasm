#pragma warning disable CS0436
using Cs2Wasm.Runtime;

namespace System
{
    /// <summary>Console output goes to the engine's write import (section 7.4).</summary>
    public static class Console
    {
        static void Out(string s)
        {
            if (s == null || s.Length == 0) return;
            Engine.Write(Rt.StringData(s), s.Length);
        }

        public static void Write(string value) => Out(value);
        public static void Write(object value) { if (value != null) Out(value.ToString()); }
        public static void Write(bool value) => Out(value ? "True" : "False");
        public static void Write(char value) => Out(string.Ctor(value, 1));
        public static void Write(int value) => Out(Number.Int64ToString(value));
        public static void Write(uint value) => Out(Number.UInt64ToString(value));
        public static void Write(long value) => Out(Number.Int64ToString(value));
        public static void Write(ulong value) => Out(Number.UInt64ToString(value));
        public static void Write(float value) => Out(value.ToString());
        public static void Write(double value) => Out(value.ToString());
        public static void Write(char[] buffer) { if (buffer != null) Out(string.Ctor(buffer)); }
        public static void Write(string format, object arg0) => Out(string.Format(format, arg0));
        public static void Write(string format, object arg0, object arg1) => Out(string.Format(format, arg0, arg1));
        public static void Write(string format, object arg0, object arg1, object arg2) => Out(string.Format(format, arg0, arg1, arg2));
        public static void Write(string format, params object[] arg) => Out(string.Format(format, arg));

        public static void WriteLine() => Out("\n");
        public static void WriteLine(string value) => Out(string.Concat2(value, "\n"));
        public static void WriteLine(object value) => Out(string.Concat2(value == null ? "" : value.ToString(), "\n"));
        public static void WriteLine(bool value) => Out(value ? "True\n" : "False\n");
        public static void WriteLine(char value) => Out(string.Concat2(string.Ctor(value, 1), "\n"));
        public static void WriteLine(int value) => Out(string.Concat2(Number.Int64ToString(value), "\n"));
        public static void WriteLine(uint value) => Out(string.Concat2(Number.UInt64ToString(value), "\n"));
        public static void WriteLine(long value) => Out(string.Concat2(Number.Int64ToString(value), "\n"));
        public static void WriteLine(ulong value) => Out(string.Concat2(Number.UInt64ToString(value), "\n"));
        public static void WriteLine(float value) => Out(string.Concat2(value.ToString(), "\n"));
        public static void WriteLine(double value) => Out(string.Concat2(value.ToString(), "\n"));
        public static void WriteLine(decimal value) => Out(string.Concat2(value.ToString(), "\n"));
        public static void Write(decimal value) => Out(value.ToString());
        public static void WriteLine(char[] buffer) => Out(string.Concat2(buffer == null ? "" : string.Ctor(buffer), "\n"));
        public static void WriteLine(string format, object arg0) => Out(string.Concat2(string.Format(format, arg0), "\n"));
        public static void WriteLine(string format, object arg0, object arg1) => Out(string.Concat2(string.Format(format, arg0, arg1), "\n"));
        public static void WriteLine(string format, object arg0, object arg1, object arg2) => Out(string.Concat2(string.Format(format, arg0, arg1, arg2), "\n"));
        public static void WriteLine(string format, params object[] arg) => Out(string.Concat2(string.Format(format, arg), "\n"));
        public static void WriteLine(string format, ReadOnlySpan<object> arg) => Out(string.Concat2(string.Format(format, arg.ToArray()), "\n"));
        public static void Write(string format, ReadOnlySpan<object> arg) => Out(string.Format(format, arg.ToArray()));
    }
}
