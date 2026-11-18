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
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location) ?? "";
        // .../dotnet/shared/Microsoft.NETCore.App/<ver>  ->  .../dotnet/packs/Microsoft.NETCore.App.Ref/<ver>/ref/net11.0
        var dotnetRoot = Path.GetFullPath(Path.Combine(runtimeDir, "..", "..", ".."));
        var packs = Path.Combine(dotnetRoot, "packs", "Microsoft.NETCore.App.Ref");
        string? refDir = null;
        if (Directory.Exists(packs))
            refDir = Directory.GetDirectories(packs).OrderByDescending(d => d, StringComparer.Ordinal)
                .Select(d => Path.Combine(d, "ref", "net11.0")).FirstOrDefault(Directory.Exists);
        // CS2WASM_REFERENCE_DIR: the reference assemblies' directory (the self-hosted compiler has no runtime directory)
        var dir = Environment.GetEnvironmentVariable("CS2WASM_REFERENCE_DIR") is { Length: > 0 } rd ? rd : refDir ?? runtimeDir;
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
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true, deterministic: true, concurrentBuild: Environment.ProcessorCount > 1,
                nullableContextOptions: NullableContextOptions.Enable));
    }

    /// <summary>Runs the source generators of analyzer assemblies over a compilation (as the C# build does), with the
    /// build properties they read (`build_property.Name`); returns the compilation with their output, and their
    /// diagnostics.</summary>
    public static CSharpCompilation RunGenerators(CSharpCompilation compilation, IEnumerable<string> analyzerPaths,
        IReadOnlyDictionary<string, string> buildProperties, out IReadOnlyList<Diagnostic> diagnostics)
    {
        // (an assembly whose generators are compiled into this toolchain is not loaded: KnownGenerators)
        var generators = analyzerPaths.SelectMany(p => KnownGenerators.TryGetValue(Path.GetFileNameWithoutExtension(p), out var known) ? known()
            : new Microsoft.CodeAnalysis.Diagnostics.AnalyzerFileReference(Path.GetFullPath(p), s_loader).GetGenerators(LanguageNames.CSharp)).ToList();
        var driver = CSharpGeneratorDriver.Create(generators, parseOptions: (CSharpParseOptions?)compilation.SyntaxTrees.FirstOrDefault()?.Options,
            optionsProvider: new BuildProperties(buildProperties));
        driver = (CSharpGeneratorDriver)driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diags);
        diagnostics = diags;
        // CS2WASM_GENERATED=<dir>: the generators' output, written out to read
        if (Environment.GetEnvironmentVariable("CS2WASM_GENERATED") is { Length: > 0 } dir)
            foreach (var r in driver.GetRunResult().Results)
                foreach (var g in r.GeneratedSources)
                {
                    var path = Path.Combine(dir, r.Generator.GetGeneratorType().Name, g.HintName);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, g.SourceText.ToString());
                }
        return (CSharpCompilation)output;
    }

    /// <summary>The source generators compiled into this toolchain (SPEC section 7.1), by the name of the assembly
    /// they come in ("Godot.SourceGenerators"): a toolchain that cannot load assemblies (the self-hosted compiler)
    /// runs these when given `-a` with that assembly. tools/selfhost.mjs --generators fills it.</summary>
    public static readonly Dictionary<string, Func<IEnumerable<ISourceGenerator>>> KnownGenerators = new(StringComparer.OrdinalIgnoreCase);

    static readonly AnalyzerLoader s_loader = new();

    sealed class AnalyzerLoader : Microsoft.CodeAnalysis.IAnalyzerAssemblyLoader
    {
        public void AddDependencyLocation(string fullPath) { }
        public System.Reflection.Assembly LoadFromPath(string fullPath) => System.Reflection.Assembly.LoadFrom(fullPath);
    }

    sealed class BuildProperties(IReadOnlyDictionary<string, string> props) : Microsoft.CodeAnalysis.Diagnostics.AnalyzerConfigOptionsProvider
    {
        readonly Options _global = new(props.ToDictionary(kv => "build_property." + kv.Key, kv => kv.Value));
        static readonly Options s_empty = new(new Dictionary<string, string>());
        public override Microsoft.CodeAnalysis.Diagnostics.AnalyzerConfigOptions GlobalOptions => _global;
        public override Microsoft.CodeAnalysis.Diagnostics.AnalyzerConfigOptions GetOptions(SyntaxTree tree) => s_empty;
        public override Microsoft.CodeAnalysis.Diagnostics.AnalyzerConfigOptions GetOptions(AdditionalText textFile) => s_empty;

        sealed class Options(Dictionary<string, string> values) : Microsoft.CodeAnalysis.Diagnostics.AnalyzerConfigOptions
        {
            public override bool TryGetValue(string key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? value) => values.TryGetValue(key, out value);
        }
    }
}
