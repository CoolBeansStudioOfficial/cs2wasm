namespace System
{
    // il2wasm: the helpers the compiler's lowering of string concatenation and interpolation calls.
    public partial class String
    {
        internal static string Concat2(string a, string b) => Concat(a, b);
        internal static string Concat3(string a, string b, string c) => Concat(a, b, c);
        internal static string Concat4(string a, string b, string c, string d) => Concat(a, b, c, d);
        /// <summary>Five to eight strings (a null for a place not used: a null part is an empty one), as
        /// `string.Concat(ReadOnlySpan&lt;string&gt;)` joins them: one new string of the whole length, or `Empty` when
        /// every part is empty; never one of the parts itself.</summary>
        internal static string Concat8(string a, string b, string c, string d, string e, string f, string g, string h)
        {
            long total = (long)Len(a) + Len(b) + Len(c) + Len(d) + Len(e) + Len(f) + Len(g) + Len(h);
            if (total > int.MaxValue) throw new OutOfMemoryException();
            if (total == 0) return Empty;
            string r = FastAllocateString((int)total);
            Put(r, Put(r, Put(r, Put(r, Put(r, Put(r, Put(r, Put(r, 0, a), b), c), d), e), f), g), h);
            return r;
        }
        static int Len(string s) => s != null ? s.Length : 0;
        static int Put(string r, int at, string s)
        {
            if (s == null) return at;
            CopyStringContent(r, at, s);
            return at + s.Length;
        }
        internal static string OrEmpty(string s) => s ?? Empty;
        /// <summary>An interpolation hole's alignment: `{x,8}` pads left, `{x,-8}` right.</summary>
        internal static string Align(string s, int width) => width < 0 ? s.PadRight(-width) : s.PadLeft(width);
    }
}
