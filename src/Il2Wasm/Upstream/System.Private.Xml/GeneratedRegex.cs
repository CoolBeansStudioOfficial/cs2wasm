// il2wasm runs no source generators: the properties [GeneratedRegex] would implement construct the same Regex (pattern
// and options as declared), which the interpreter runs (System.Text.RegularExpressions without compiled code).
using System.Text.RegularExpressions;

namespace System.Xml.Schema
{
    internal sealed partial class StringFacetsChecker
    {
        private static Regex? s_languageRegex;
        private static partial Regex LanguageRegex => s_languageRegex ??= new Regex("^([a-zA-Z]{1,8})(-[a-zA-Z0-9]{1,8})*$", RegexOptions.ExplicitCapture);
    }
}

namespace System.Xml.Serialization
{
    internal sealed partial class SourceInfo
    {
        private static Regex? s_regex1, s_regex2;
        private static partial Regex Regex1 => s_regex1 ??= new Regex("([(][(](?<t>[^)]+)[)])?(?<a>[^[]+)[[](?<ia>.+)[]][)]?");
        private static partial Regex Regex2 => s_regex2 ??= new Regex("[(][(](?<cast>[^)]+)[)](?<arg>[^)]+)[)]");
    }

    internal sealed partial class XmlSerializationReaderILGen
    {
        private static Regex? s_ensureArrayIndex, s_p0, s_nullAny, s_objectEmpty, s_objectNull, s_object, s_paramsRead;
        private static partial Regex EnsureArrayIndexRegex => s_ensureArrayIndex ??= new Regex("(?<locA1>[^ ]+) = .+EnsureArrayIndex[(](?<locA2>[^,]+), (?<locI1>[^,]+),[^;]+;(?<locA3>[^[]+)[[](?<locI2>[^+]+)[+][+][]]");
        private static partial Regex P0Regex => s_p0 ??= new Regex("(?<a>[^[]+)[[](?<ia>.+)[]]");
        private static partial Regex UnknownNodeNullAnyTypeRegex => s_nullAny ??= new Regex("UnknownNode[(]null, @[\"](?<qnames>[^\"]*)[\"][)];");
        private static partial Regex UnknownNodeObjectEmptyRegex => s_objectEmpty ??= new Regex("UnknownNode[(][(]object[)](?<o>[^,]+), @[\"](?<qnames>[^\"]*)[\"][)];");
        private static partial Regex UnknownNodeObjectNullRegex => s_objectNull ??= new Regex("UnknownNode[(][(]object[)](?<o>[^,]+), null[)];");
        private static partial Regex UnknownNodeObjectRegex => s_object ??= new Regex("UnknownNode[(][(]object[)](?<o>[^)]+)[)];");
        private static partial Regex ParamsReadRegex => s_paramsRead ??= new Regex(@"paramsRead\[(?<index>[0-9]+)\]");
    }
}

namespace System.Xml
{
    public partial class XmlConvert
    {
        private static Regex? s_decodeChar, s_encodeChar;
        private static partial Regex DecodeCharRegex => s_decodeChar ??= new Regex("_[Xx][0-9a-fA-F]{4}(?:_|[0-9a-fA-F]{4}_)");
        private static partial Regex EncodeCharRegex => s_encodeChar ??= new Regex("(?<=_)[Xx][0-9a-fA-F]{4}(?:_|[0-9a-fA-F]{4}_)");
    }
}
