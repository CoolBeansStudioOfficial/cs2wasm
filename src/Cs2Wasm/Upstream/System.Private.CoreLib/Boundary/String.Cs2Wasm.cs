namespace System
{
    // cs2wasm: the helpers the compiler's lowering of string concatenation and interpolation calls.
    public partial class String
    {
        internal static string Concat2(string a, string b) => Concat(a, b);
        internal static string Concat3(string a, string b, string c) => Concat(a, b, c);
        internal static string Concat4(string a, string b, string c, string d) => Concat(a, b, c, d);
        internal static string OrEmpty(string s) => s ?? Empty;
        /// <summary>An interpolation hole's alignment: `{x,8}` pads left, `{x,-8}` right.</summary>
        internal static string Align(string s, int width) => width < 0 ? s.PadRight(-width) : s.PadLeft(width);
    }
}
