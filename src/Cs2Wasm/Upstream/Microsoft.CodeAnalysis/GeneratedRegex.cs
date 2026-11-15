// cs2wasm runs no source generators: the methods [GeneratedRegex] would implement construct the same Regex, which the
// interpreter runs (System.Text.RegularExpressions without compiled code).
using System.Text.RegularExpressions;

namespace Microsoft.CodeAnalysis
{
    public sealed partial class AnalyzerConfig
    {
        private static Regex? s_sectionMatcher, s_propertyMatcher;
        private static partial Regex GetSectionMatcherRegex() => s_sectionMatcher ??= new Regex(s_sectionMatcherPattern);
        private static partial Regex GetPropertyMatcherRegex() => s_propertyMatcher ??= new Regex(s_propertyMatcherPattern);
    }
}
