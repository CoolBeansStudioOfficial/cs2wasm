using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Cs2Wasm.Compiler;

/// <summary>
/// The library the program is compiled with: C# declaring the .NET types the program uses, under their real names, so
/// that a symbol from a reference assembly maps onto a library symbol with the same documentation id.
/// <para>Upstream (section 7.4): dotnet/runtime's System.Private.CoreLib and the libraries above it, each its own
/// compilation (as the assemblies are), from the files the manifests in Upstream/&lt;Library&gt; list plus cs2wasm's own
/// files there (the runtime half, the boundary, the generated SR and unavailable platform invokes). The sources are
/// read from <see cref="UpstreamSources.Root"/>.</para>
/// <para>Otherwise (until the migration is done): the hand-written library in Lib/.</para>
/// </summary>
internal sealed class Library
{
    /// <summary>The corlib: inside it, `string` and `object` are its own types.</summary>
    public readonly CSharpCompilation Compilation;
    /// <summary>Every library compilation, the corlib first, each after those it references.</summary>
    public readonly IReadOnlyList<CSharpCompilation> All;
    readonly Dictionary<string, INamedTypeSymbol> _types = [];
    readonly HashSet<SyntaxTree> _trees = [];

    public static bool UseUpstream => Environment.GetEnvironmentVariable("CS2WASM_UPSTREAM") == "1";

    static readonly CSharpCompilationOptions s_options = new(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true,
        nullableContextOptions: NullableContextOptions.Disable,
        specificDiagnosticOptions: new Dictionary<string, ReportDiagnostic> { ["CS0436"] = ReportDiagnostic.Suppress, ["CS0626"] = ReportDiagnostic.Suppress, ["CS8826"] = ReportDiagnostic.Suppress });

    static readonly Lazy<SyntaxTree[]> s_handWritten = new(() =>
    {
        var opts = new CSharpParseOptions(LanguageVersion.CSharp15);
        return Resources("lib/").Select(r => CSharpSyntaxTree.ParseText(r.Text, opts, path: "cs2wasm/" + r.Name)).ToArray();
    });

    static readonly Lazy<IReadOnlyList<CSharpCompilation>> s_upstream = new(BuildUpstream);

    public Library(IEnumerable<MetadataReference> references)
    {
        if (UseUpstream) All = s_upstream.Value;
        else All = [CSharpCompilation.Create("System.Private.CoreLib", s_handWritten.Value, [], s_options)];
        Compilation = All[0];
        foreach (var c in All)
        {
            foreach (var t in c.SyntaxTrees) _trees.Add(t);
            Index(c.Assembly.GlobalNamespace);
        }
    }

    static IEnumerable<(string Name, string Text)> Resources(string prefix)
    {
        var asm = typeof(Library).Assembly;
        foreach (var n in asm.GetManifestResourceNames().Where(n => n.Replace('\\', '/').StartsWith(prefix)).Order(StringComparer.Ordinal))
        {
            using var s = asm.GetManifestResourceStream(n)!;
            using var r = new StreamReader(s);
            yield return (n.Replace('\\', '/'), r.ReadToEnd());
        }
    }

    /// <summary>The trimmer's substitutions for cs2wasm's features (manifest `substitute` lines): "type|signature" of a
    /// method, or "type|name" of a field, to its value ("" for an empty stub).</summary>
    static readonly Dictionary<string, string> s_substitutions = [];

    /// <summary>The substituted value of a library method (null: not substituted; "": an empty stub).</summary>
    public static string? Substitution(IMethodSymbol m)
    {
        if (!UseUpstream || s_substitutions.Count == 0) return null;
        var def = m.OriginalDefinition;
        if (def.ContainingType == null) return null;
        var sig = (def.ReturnsVoid ? "System.Void" : IlName(def.ReturnType)) + " " + def.MetadataName + "(" + string.Join(",", def.Parameters.Select(p => IlName(p.Type) + (p.RefKind != RefKind.None ? "&" : ""))) + ")";
        return s_substitutions.GetValueOrDefault(IlName(def.ContainingType) + "|" + sig);
    }

    public static string? FieldSubstitution(IFieldSymbol f) =>
        UseUpstream && f.OriginalDefinition.ContainingType is { } ct ? s_substitutions.GetValueOrDefault(IlName(ct) + "|" + f.Name) : null;

    /// <summary>A type's name as the trimmer's files write it (Outer/Inner, System.Int32).</summary>
    static string IlName(ITypeSymbol t) => t switch
    {
        IArrayTypeSymbol a => IlName(a.ElementType) + "[" + new string(',', a.Rank - 1) + "]",
        IPointerTypeSymbol p => IlName(p.PointedAtType) + "*",
        INamedTypeSymbol { ContainingType: { } outer } n => IlName(outer) + "/" + n.MetadataName,
        INamedTypeSymbol n => (n.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() + "." : "") + n.MetadataName,
        _ => t.Name,
    };

    static IReadOnlyList<CSharpCompilation> BuildUpstream()
    {
        var root = UpstreamSources.Root;
        var own = Resources("upstream/").ToList();
        var built = new List<CSharpCompilation>();
        foreach (var lib in UpstreamSources.Libraries)
        {
            var prefix = "upstream/" + lib + "/";
            var manifest = own.FirstOrDefault(r => r.Name == prefix + "manifest.txt").Text
                ?? throw new CompileException("library manifest missing: " + lib);
            var lines = manifest.Split('\n').Select(l => l.Trim()).ToList();
            foreach (var l in lines.Where(l => l.StartsWith("substitute\t")))
            {
                var parts = l.Split('\t');
                if (parts.Length >= 5) s_substitutions[parts[2] + "|" + parts[3]] = parts[4];
            }
            var defines = lines.Where(l => l.StartsWith("define ")).Select(l => l[7..]).ToArray();
            var po = new CSharpParseOptions(LanguageVersion.Preview, preprocessorSymbols: defines);
            var files = lines.Where(l => l.StartsWith("file ")).Select(l => l[5..]).ToList();
            var trees = files.AsParallel().AsOrdered().Select(f =>
            {
                var path = Path.Combine(root, f);
                if (!File.Exists(path)) throw new CompileException($"library source missing: {path} (dotnet/runtime {UpstreamSources.Tag})");
                return CSharpSyntaxTree.ParseText(File.ReadAllText(path), po, path: "dotnet/runtime/" + f);
            }).ToList();
            trees.AddRange(own.Where(r => r.Name.StartsWith(prefix) && r.Name.EndsWith(".cs"))
                .AsParallel().AsOrdered().Select(r => CSharpSyntaxTree.ParseText(r.Text, po, path: "cs2wasm/" + r.Name)));
            built.Add(CSharpCompilation.Create(lib, trees, built.Select(b => (MetadataReference)b.ToMetadataReference()), s_options));
        }
        return built;
    }

    void Index(INamespaceOrTypeSymbol ns)
    {
        foreach (var m in ns.GetMembers())
        {
            if (m is INamedTypeSymbol t)
            {
                // a public type wins over an internal one with the same name in another library
                if (t.Locations.Any(l => l.IsInSource))
                {
                    var id = Types.DocId(t);
                    if (!_types.TryGetValue(id, out var prev) || (!IsVisible(prev) && IsVisible(t))) _types[id] = t;
                }
                Index(t);
            }
            else if (m is INamespaceSymbol n) Index(n);
        }
    }

    static bool IsVisible(INamedTypeSymbol t)
    {
        for (var x = t; x != null; x = x.ContainingType)
            if (x.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal)) return false;
        return true;
    }

    /// <summary>The library's type for one seen through a reference; a library's own type is itself (CoreLib's
    /// System.Number is not System.Runtime.Numerics' System.Number). Interop is the exception: every library's
    /// Interop.Sys is CoreLib's, whose bodies are the boundary's.</summary>
    public INamedTypeSymbol? FindType(INamedTypeSymbol def)
    {
        if (def.Name != "Interop" || !def.ContainingNamespace.IsGlobalNamespace)
            foreach (var c in All) if (SymbolEqualityComparer.Default.Equals(c.Assembly, def.ContainingAssembly)) return def;
        return _types.GetValueOrDefault(Types.DocId(def));
    }

    /// <summary>A library type by metadata name, the corlib's first.</summary>
    public INamedTypeSymbol? TypeByMetadataName(string name)
    {
        foreach (var c in All) if (c.Assembly.GetTypeByMetadataName(name) is { } t) return t;
        return null;
    }

    public bool Contains(SyntaxTree tree) => _trees.Contains(tree);
    public CSharpCompilation? CompilationOf(SyntaxTree tree) => _trees.Contains(tree) ? All.First(c => c.ContainsSyntaxTree(tree)) : null;

    /// <summary>Errors of the hand-written library (the upstream libraries are checked when they are generated, and
    /// binding all of them on every build would cost seconds).</summary>
    public IEnumerable<Diagnostic> Errors => UseUpstream
        ? Environment.GetEnvironmentVariable("CS2WASM_LIB_ERRORS") == "1" ? All.SelectMany(c => c.GetDiagnostics()).Where(d => d.Severity == DiagnosticSeverity.Error) : []
        : Compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error);
}

/// <summary>Where the dotnet/runtime sources the library is built from are.</summary>
internal static class UpstreamSources
{
    public const string Tag = "v11.0.0";

    /// <summary>The libraries, each after those it references (as tools/CoreLibManifest lists them).</summary>
    public static readonly string[] Libraries =
    [
        "System.Private.CoreLib", "System.Collections", "System.Collections.NonGeneric", "System.ObjectModel", "System.Memory",
        "System.Threading", "System.Collections.Concurrent", "System.Linq", "System.Console", "System.Runtime.Numerics",
        "System.Threading.Tasks.Parallel", "System.Text.RegularExpressions",
    ];

    /// <summary>CS2WASM_RUNTIME_SOURCES, else the cache (%LOCALAPPDATA%/cs2wasm/runtime-v11.0.0).</summary>
    public static string Root =>
        Environment.GetEnvironmentVariable("CS2WASM_RUNTIME_SOURCES") is { Length: > 0 } r ? r
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "cs2wasm", "runtime-" + Tag);
}
