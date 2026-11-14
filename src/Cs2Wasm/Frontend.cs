using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Cs2Wasm;

/// <summary>Helpers for tools and tests: build a Roslyn compilation from files against the .NET reference assemblies.</summary>
public static class Frontend
{
    public static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.CSharp15);

    /// <summary>Reference assemblies of the .NET 11 targeting pack (falls back to the running runtime's assemblies).</summary>
    public static IReadOnlyList<MetadataReference> DefaultReferences() => s_refs.Value;

    static readonly Lazy<IReadOnlyList<MetadataReference>> s_refs = new(() =>
    {
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        // .../dotnet/shared/Microsoft.NETCore.App/<ver>  ->  .../dotnet/packs/Microsoft.NETCore.App.Ref/<ver>/ref/net11.0
        var dotnetRoot = Path.GetFullPath(Path.Combine(runtimeDir, "..", "..", ".."));
        var packs = Path.Combine(dotnetRoot, "packs", "Microsoft.NETCore.App.Ref");
        string? refDir = null;
        if (Directory.Exists(packs))
            refDir = Directory.GetDirectories(packs).OrderByDescending(d => d, StringComparer.Ordinal)
                .Select(d => Path.Combine(d, "ref", "net11.0")).FirstOrDefault(Directory.Exists);
        var dir = refDir ?? runtimeDir;
        var list = new List<MetadataReference>();
        foreach (var f in Directory.GetFiles(dir, "*.dll").Order(StringComparer.Ordinal))
        {
            try
            {
                using var fs = File.OpenRead(f);
                using var pe = new System.Reflection.PortableExecutable.PEReader(fs);
                if (!pe.HasMetadata) continue;
            }
            catch { continue; }
            list.Add(MetadataReference.CreateFromFile(f));
        }
        return list;
    });

    public static CSharpCompilation CreateCompilation(string assemblyName, IEnumerable<(string path, string text)> sources, IEnumerable<MetadataReference>? extraReferences = null, IEnumerable<string>? preprocessorSymbols = null)
    {
        var opts = preprocessorSymbols == null ? ParseOptions : ParseOptions.WithPreprocessorSymbols(preprocessorSymbols);
        var trees = sources.Select(s => CSharpSyntaxTree.ParseText(Microsoft.CodeAnalysis.Text.SourceText.From(s.text, System.Text.Encoding.UTF8), opts, path: s.path)).ToList();
        return CSharpCompilation.Create(assemblyName, trees, DefaultReferences().Concat(extraReferences ?? []),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true, deterministic: true,
                nullableContextOptions: NullableContextOptions.Enable));
    }
}
