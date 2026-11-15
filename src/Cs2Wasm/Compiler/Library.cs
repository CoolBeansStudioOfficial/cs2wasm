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
/// </summary>
internal sealed class Library
{
    /// <summary>The corlib: inside it, `string` and `object` are its own types.</summary>
    public readonly CSharpCompilation Compilation;
    /// <summary>Every library compilation, the corlib first, each after those it references.</summary>
    public readonly IReadOnlyList<CSharpCompilation> All;
    readonly Dictionary<string, INamedTypeSymbol> _types = [];
    readonly HashSet<SyntaxTree> _trees = [];


    static readonly CSharpCompilationOptions s_options = new(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true,
        nullableContextOptions: NullableContextOptions.Disable,
        specificDiagnosticOptions: new Dictionary<string, ReportDiagnostic>
        {
            ["CS0436"] = ReportDiagnostic.Suppress, ["CS0626"] = ReportDiagnostic.Suppress, ["CS8826"] = ReportDiagnostic.Suppress,
            // (Roslyn's experimental APIs, used inside Roslyn)
            ["RSEXPERIMENTAL001"] = ReportDiagnostic.Suppress, ["RSEXPERIMENTAL002"] = ReportDiagnostic.Suppress, ["RSEXPERIMENTAL003"] = ReportDiagnostic.Suppress,
            ["RSEXPERIMENTAL004"] = ReportDiagnostic.Suppress, ["RSEXPERIMENTAL005"] = ReportDiagnostic.Suppress, ["RSEXPERIMENTAL006"] = ReportDiagnostic.Suppress,
            ["RSEXPERIMENTAL007"] = ReportDiagnostic.Suppress, ["RSEXPERIMENTAL008"] = ReportDiagnostic.Suppress, ["RSEXPERIMENTAL009"] = ReportDiagnostic.Suppress,
        });

    public Library(CSharpCompilation program)
    {
        All = Upstream(UpstreamSources.Needed(program));
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
        if (s_substitutions.Count == 0) return null;
        var def = m.OriginalDefinition;
        if (def.ContainingType == null) return null;
        var sig = (def.ReturnsVoid ? "System.Void" : IlName(def.ReturnType)) + " " + def.MetadataName + "(" + string.Join(",", def.Parameters.Select(p => IlName(p.Type) + (p.RefKind != RefKind.None ? "&" : ""))) + ")";
        return s_substitutions.GetValueOrDefault(IlName(def.ContainingType) + "|" + sig);
    }

    public static string? FieldSubstitution(IFieldSymbol f) =>
        f.OriginalDefinition.ContainingType is { } ct ? s_substitutions.GetValueOrDefault(IlName(ct) + "|" + f.Name) : null;

    /// <summary>A type's name as the trimmer's files write it (Outer/Inner, System.Int32).</summary>
    static string IlName(ITypeSymbol t) => t switch
    {
        IArrayTypeSymbol a => IlName(a.ElementType) + "[" + new string(',', a.Rank - 1) + "]",
        IPointerTypeSymbol p => IlName(p.PointedAtType) + "*",
        INamedTypeSymbol { ContainingType: { } outer } n => IlName(outer) + "/" + n.MetadataName,
        INamedTypeSymbol n => (n.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() + "." : "") + n.MetadataName,
        _ => t.Name,
    };

    static readonly object s_lock = new();
    static readonly List<CSharpCompilation> s_built = [];
    static List<(string Name, string Text)>? s_own;

    /// <summary>The first `count` libraries, compiled once per process and extended as programs need more (the
    /// optional ones, built for the programs that use them).</summary>
    static IReadOnlyList<CSharpCompilation> Upstream(int count)
    {
        lock (s_lock)
        {
            s_own ??= Resources("upstream/").ToList();
            while (s_built.Count < count) s_built.Add(BuildOne(UpstreamSources.Libraries[s_built.Count], s_own, s_built));
            return s_built.Take(count).ToList();
        }
    }

    static CSharpCompilation BuildOne(string lib, List<(string Name, string Text)> own, List<CSharpCompilation> built)
    {
        {
            var prefix = "upstream/" + lib + "/";
            var manifest = own.FirstOrDefault(r => r.Name == prefix + "manifest.txt").Text
                ?? throw new CompileException("library manifest missing: " + lib);
            // (not Trim(): an empty substitution value is a trailing tab)
            var lines = manifest.Split('\n').Select(l => l.Trim(' ', '\r')).ToList();
            foreach (var l in lines.Where(l => l.StartsWith("substitute\t")))
            {
                var parts = l.Split('\t');
                if (parts.Length >= 5) s_substitutions[parts[2] + "|" + parts[3]] = parts[4];
            }
            var defines = lines.Where(l => l.StartsWith("define ")).Select(l => l[7..]).ToArray();
            var po = new CSharpParseOptions(LanguageVersion.Preview, preprocessorSymbols: defines);
            var files = lines.Where(l => l.StartsWith("file ")).Select(l => l[5..]).ToList();
            // `root <dir>`: another source tree of the cache (Roslyn's), fetched like the runtime's
            var srcRoot = lines.FirstOrDefault(l => l.StartsWith("root ")) is { } rl ? UpstreamSources.OtherRoot(rl[5..].Trim()) : UpstreamSources.Root;
            if (srcRoot == UpstreamSources.Root) UpstreamSources.Ensure(files); else UpstreamSources.EnsureRoslyn(srcRoot, files);
            var root = srcRoot;
            bool nullable = lines.Contains("nullable enable");
            // edit<TAB>file<TAB>text<TAB>replacement: an exact change to one upstream file (each must still apply)
            var edits = lines.Where(l => l.StartsWith("edit\t")).Select(l => l.Split('\t')).Where(p => p.Length >= 4)
                .GroupBy(p => p[1]).ToDictionary(g => g.Key, g => g.Select(p => (p[2], p[3])).ToList());
            var trees = files.AsParallel().AsOrdered().Select(f =>
            {
                var path = Path.Combine(root, f);
                if (!File.Exists(path)) throw new CompileException($"library source missing: {path}");
                var text = File.ReadAllText(path);
                if (edits.TryGetValue(f, out var es))
                    foreach (var (from, to) in es)
                    {
                        if (!text.Contains(from)) throw new CompileException($"library edit no longer applies: {f}: '{from}'");
                        text = text.Replace(from, to);
                    }
                return CSharpSyntaxTree.ParseText(text, po, path: (root == UpstreamSources.Root ? "dotnet/runtime/" : "dotnet/roslyn/") + f);
            }).ToList();
            trees.AddRange(own.Where(r => r.Name.StartsWith(prefix) && r.Name.EndsWith(".cs"))
                .AsParallel().AsOrdered().Select(r => CSharpSyntaxTree.ParseText(r.Text, po, path: "cs2wasm/" + r.Name)));
            return CSharpCompilation.Create(lib, trees, built.Select(b => (MetadataReference)b.ToMetadataReference()),
                nullable ? s_options.WithNullableContextOptions(NullableContextOptions.Enable) : s_options);
        }
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

    /// <summary>The library's own errors, with CS2WASM_LIB_ERRORS=1 (the libraries are checked when they are generated,
    /// and binding all of them on every build would cost seconds).</summary>
    public IEnumerable<Diagnostic> Errors =>
        Environment.GetEnvironmentVariable("CS2WASM_LIB_ERRORS") == "1" ? All.SelectMany(c => c.GetDiagnostics()).Where(d => d.Severity == DiagnosticSeverity.Error) : [];
}

/// <summary>Where the dotnet/runtime sources the library is built from are.</summary>
internal static class UpstreamSources
{
    public const string Tag = "v11.0.0";

    /// <summary>The libraries, each after those it references (as tools/CoreLibManifest lists them). The first
    /// <see cref="CoreCount"/> are built for every program; the others for programs that use them (and those before
    /// them in this order, which they may reference).</summary>
    public static readonly string[] Libraries =
    [
        "System.Private.CoreLib", "System.Collections", "System.Collections.NonGeneric", "System.ObjectModel", "System.Memory",
        "System.Threading", "System.Collections.Concurrent", "System.Linq", "System.Console", "System.Runtime.Numerics",
        "System.Threading.Tasks.Parallel", "System.Text.RegularExpressions", "System.Linq.Expressions", "Microsoft.CSharp", "System.Linq.Queryable",
        "System.Collections.Immutable", "System.IO.MemoryMappedFiles", "System.IO.Compression", "System.Reflection.Metadata",
        "System.Private.Uri", "System.Net.Primitives", "System.Runtime.Serialization.Primitives", "System.Private.Xml", "System.Xml.XPath",
        "System.Private.Xml.Linq", "System.Xml.XPath.XDocument", "System.Security.Cryptography", "System.Text.Encoding.CodePages",
        "Microsoft.CodeAnalysis", "Microsoft.CodeAnalysis.CSharp",
    ];

    public const int CoreCount = 15;

    /// <summary>What makes a program need an optional library: a reference to the assembly (Roslyn's are not in the
    /// framework), or its namespace in the source (the framework's reference assemblies all are referenced).</summary>
    static readonly (string Lib, string[] Assemblies, string[] Namespaces)[] Optional =
    [
        ("System.Collections.Immutable", [], ["System.Collections.Immutable", "System.Collections.Frozen"]),
        ("System.IO.MemoryMappedFiles", [], ["System.IO.MemoryMappedFiles"]),
        ("System.IO.Compression", [], ["System.IO.Compression"]),
        ("System.Reflection.Metadata", [], ["System.Reflection.Metadata", "System.Reflection.PortableExecutable"]),
        ("System.Private.Xml", [], ["System.Xml"]),
        ("System.Security.Cryptography", [], ["System.Security.Cryptography"]),
        ("Microsoft.CodeAnalysis.CSharp", ["Microsoft.CodeAnalysis", "Microsoft.CodeAnalysis.CSharp"], []),
    ];

    /// <summary>How many of <see cref="Libraries"/> the program needs.</summary>
    public static int Needed(CSharpCompilation program)
    {
        int count = CoreCount;
        var names = program.ReferencedAssemblyNames.Select(a => a.Name).ToHashSet();
        string? text = null;
        foreach (var (lib, asms, nss) in Optional)
        {
            int i = Array.IndexOf(Libraries, lib);
            if (i < count) continue;
            bool need = asms.Any(names.Contains);
            if (!need && nss.Length > 0)
            {
                text ??= string.Join("\n", program.SyntaxTrees.Select(t => t.GetText().ToString()));
                need = nss.Any(n => text.Contains(n, StringComparison.Ordinal));
            }
            if (need) count = i + 1;
        }
        return count;
    }

    public const string Repository = "https://github.com/dotnet/runtime.git";

    /// <summary>Roslyn's sources (its compiler libraries, for programs that use them: section 9), at the version
    /// cs2wasm itself is built with.</summary>
    public const string RoslynRepository = "https://github.com/dotnet/roslyn.git";
    public const string RoslynCommit = "1324e1d34a0490454837a22370e75bbaeecffb87";

    /// <summary>Another source tree beside the runtime's in the cache (`root` in a manifest).</summary>
    public static string OtherRoot(string name) => Path.Combine(Path.GetDirectoryName(Root.TrimEnd('/', '\\'))!, name);

    /// <summary>As <see cref="Ensure"/>, for Roslyn's tree: a sparse, blobless fetch of the commit.</summary>
    public static void EnsureRoslyn(string root, IEnumerable<string> files)
    {
        var missing = files.Distinct().Where(f => !File.Exists(Path.Combine(root, f))).ToList();
        if (missing.Count == 0) return;
        using var mutex = new Mutex(false, "cs2wasm-roslyn-sources");
        try { mutex.WaitOne(); } catch (AbandonedMutexException) { }
        try
        {
            missing = missing.Where(f => !File.Exists(Path.Combine(root, f))).ToList();
            if (missing.Count == 0) return;
            var patterns = string.Join("\n", missing.Select(f => "/" + f.Replace('\\', '/'))) + "\n";
            if (!Directory.Exists(Path.Combine(root, ".git")))
            {
                Directory.CreateDirectory(root);
                Console.Error.WriteLine($"cs2wasm: fetching Roslyn's compiler sources into {root} (once)");
                Git(root, "init", "-q");
                Git(root, "config", "core.longpaths", "true");
                Git(root, "remote", "add", "origin", RoslynRepository);
                Git(root, "sparse-checkout", "set", "--no-cone", "--stdin", patterns);
                Git(root, "fetch", "-q", "--depth", "1", "--filter=blob:none", "origin", RoslynCommit);
                Git(root, "checkout", "-q", "FETCH_HEAD");
            }
            else Git(root, "sparse-checkout", "add", "--stdin", patterns);
        }
        finally { mutex.ReleaseMutex(); }
    }

    static string? Override => Environment.GetEnvironmentVariable("CS2WASM_RUNTIME_SOURCES") is { Length: > 0 } r ? r : null;

    /// <summary>CS2WASM_RUNTIME_SOURCES, else the cache (CS2WASM_CACHE or %LOCALAPPDATA%/cs2wasm, then runtime-v11.0.0).</summary>
    public static string Root =>
        Override ?? Path.Combine(Environment.GetEnvironmentVariable("CS2WASM_CACHE") is { Length: > 0 } c ? c
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "cs2wasm"), "runtime-" + Tag);

    /// <summary>Makes sure the cache has the files the manifests list: on first use a sparse, blobless clone of
    /// dotnet/runtime at the tag with only those files; later, the files a newer manifest adds. A directory given by
    /// CS2WASM_RUNTIME_SOURCES is used as it is.</summary>
    public static void Ensure(IEnumerable<string> files)
    {
        var root = Root;
        var missing = files.Distinct().Where(f => !File.Exists(Path.Combine(root, f))).ToList();
        if (missing.Count == 0 || Override != null) return;
        using var mutex = new Mutex(false, "cs2wasm-runtime-sources-" + Tag);
        try { mutex.WaitOne(); } catch (AbandonedMutexException) { }
        try
        {
            missing = missing.Where(f => !File.Exists(Path.Combine(root, f))).ToList();
            if (missing.Count == 0) return;
            var patterns = string.Join("\n", missing.Select(f => "/" + f.Replace('\\', '/'))) + "\n";
            if (!Directory.Exists(Path.Combine(root, ".git")))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(root)!);
                Console.Error.WriteLine($"cs2wasm: fetching the dotnet/runtime {Tag} library sources into {root} (once)");
                Git(null, "clone", "--filter=blob:none", "--no-checkout", "--depth", "1", "--branch", Tag, Repository, root);
                Git(root, "config", "core.longpaths", "true");
                Git(root, "sparse-checkout", "set", "--no-cone", "--stdin", patterns);
                Git(root, "checkout", Tag);
            }
            else Git(root, "sparse-checkout", "add", "--stdin", patterns);
        }
        finally { mutex.ReleaseMutex(); }
    }

    /// <summary>Runs git (in `dir`); the last argument after "--stdin" is written to its input.</summary>
    static void Git(string? dir, params string[] args)
    {
        string? input = null;
        int stdin = Array.IndexOf(args, "--stdin");
        if (stdin >= 0 && stdin == args.Length - 2) { input = args[^1]; args = args[..^1]; }
        var psi = new System.Diagnostics.ProcessStartInfo("git") { RedirectStandardInput = input != null, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        if (dir != null) { psi.ArgumentList.Add("-C"); psi.ArgumentList.Add(dir); }
        foreach (var a in args) psi.ArgumentList.Add(a);
        System.Diagnostics.Process p;
        try { p = System.Diagnostics.Process.Start(psi)!; }
        catch (Exception e) { throw new CompileException($"the library sources (dotnet/runtime {Tag}) are not in {Root} and git could not be run to fetch them ({e.Message}); set CS2WASM_RUNTIME_SOURCES to a checkout"); }
        if (input != null) { p.StandardInput.Write(input); p.StandardInput.Close(); }
        var err = p.StandardError.ReadToEndAsync();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new CompileException($"fetching the library sources failed: git {string.Join(" ", args)}: {err.Result.Trim()}");
    }
}
