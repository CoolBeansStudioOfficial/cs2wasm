// il2wasm runs no source generators: the property [GeneratedRegex] would implement constructs the same Regex (pattern,
// options and timeout as declared), which the interpreter runs (System.Text.RegularExpressions without compiled code).
using System.Text.RegularExpressions;

namespace System.Text.Json
{
    internal static partial class JsonHelpers
    {
        private static Regex? s_integerRegex;
        public static partial Regex IntegerRegex => s_integerRegex ??= new Regex(IntegerRegexPattern, RegexOptions.None, TimeSpan.FromMilliseconds(IntegerRegexTimeoutMs));
    }
}
