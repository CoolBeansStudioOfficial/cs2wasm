using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Cs2Wasm.Compiler;

/// <summary>
/// cs2wasm's library sources: C# declaring the .NET types the program uses, under their real names, so that a
/// symbol from a reference assembly maps onto a library symbol with the same documentation id.
/// </summary>
internal sealed class Library
{
    public readonly CSharpCompilation Compilation;
    readonly Dictionary<string, INamedTypeSymbol> _types = [];

    static readonly Lazy<SyntaxTree[]> s_trees = new(() =>
    {
        var asm = typeof(Library).Assembly;
        var opts = new CSharpParseOptions(LanguageVersion.CSharp15);
        return asm.GetManifestResourceNames().Where(n => n.StartsWith("lib/")).Order(StringComparer.Ordinal)
            .Select(n =>
            {
                using var s = asm.GetManifestResourceStream(n)!;
                using var r = new StreamReader(s);
                return CSharpSyntaxTree.ParseText(r.ReadToEnd(), opts, path: "cs2wasm/" + n);
            }).ToArray();
    });

    public Library(IEnumerable<MetadataReference> references)
    {
        // The library is its own corlib (no references): inside it, `string` and `object` are its own types.
        Compilation = CSharpCompilation.Create("System.Private.CoreLib", s_trees.Value, [],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true,
                nullableContextOptions: NullableContextOptions.Disable,
                specificDiagnosticOptions: new Dictionary<string, ReportDiagnostic> { ["CS0436"] = ReportDiagnostic.Suppress, ["CS0626"] = ReportDiagnostic.Suppress }));
        Index(Compilation.Assembly.GlobalNamespace);
    }

    void Index(INamespaceOrTypeSymbol ns)
    {
        foreach (var m in ns.GetMembers())
        {
            if (m is INamedTypeSymbol t)
            {
                if (t.Locations.Any(l => l.IsInSource)) _types[Types.DocId(t)] = t;
                Index(t);
            }
            else if (m is INamespaceSymbol n) Index(n);
        }
    }

    public INamedTypeSymbol? FindType(INamedTypeSymbol def) => _types.GetValueOrDefault(Types.DocId(def));

    public IEnumerable<Diagnostic> Errors => Compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error);
}
