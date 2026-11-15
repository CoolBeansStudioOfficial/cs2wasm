namespace System
{
    // cs2wasm: the helpers the compiler's lowering of string concatenation and interpolation calls.
    public partial class String
    {
        internal static string Concat2(string a, string b) => Concat(a, b);
        internal static string OrEmpty(string s) => s ?? Empty;
        /// <summary>An interpolation hole's alignment: `{x,8}` pads left, `{x,-8}` right.</summary>
        internal static string Align(string s, int width) => width < 0 ? s.PadRight(-width) : s.PadLeft(width);
    }
}
