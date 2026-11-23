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


    static readonly CSharpCompilationOptions s_options = new(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true, concurrentBuild: Environment.ProcessorCount > 1,
        nullableContextOptions: NullableContextOptions.Disable,
        specificDiagnosticOptions: new Dictionary<string, ReportDiagnostic>
        {
            ["CS0436"] = ReportDiagnostic.Suppress, ["CS0626"] = ReportDiagnostic.Suppress, ["CS8826"] = ReportDiagnostic.Suppress,
            // (Roslyn's experimental APIs, used inside Roslyn)
            ["RSEXPERIMENTAL001"] = ReportDiagnostic.Suppress, ["RSEXPERIMENTAL002"] = ReportDiagnostic.Suppress, ["RSEXPERIMENTAL003"] = ReportDiagnostic.Suppress,
            ["RSEXPERIMENTAL004"] = ReportDiagnostic.Suppress, ["RSEXPERIMENTAL005"] = ReportDiagnostic.Suppress, ["RSEXPERIMENTAL006"] = ReportDiagnostic.Suppress,
            ["RSEXPERIMENTAL007"] = ReportDiagnostic.Suppress, ["RSEXPERIMENTAL008"] = ReportDiagnostic.Suppress, ["RSEXPERIMENTAL009"] = ReportDiagnostic.Suppress,
            // (the libraries' experimental APIs, which their projects suppress: ML-KEM, ML-DSA, HPKE...)
            ["SYSLIB5001"] = ReportDiagnostic.Suppress, ["SYSLIB5002"] = ReportDiagnostic.Suppress, ["SYSLIB5003"] = ReportDiagnostic.Suppress, ["SYSLIB5004"] = ReportDiagnostic.Suppress, ["SYSLIB5005"] = ReportDiagnostic.Suppress,
            ["SYSLIB5006"] = ReportDiagnostic.Suppress, ["SYSLIB5007"] = ReportDiagnostic.Suppress, ["SYSLIB5008"] = ReportDiagnostic.Suppress, ["SYSLIB5009"] = ReportDiagnostic.Suppress, ["SYSLIB5010"] = ReportDiagnostic.Suppress,
        });

    /// <summary>The first library (its index in <see cref="UpstreamSources.Libraries"/>) that was left out although
    /// Roslyn reports its reference as used (int.MaxValue: none was): see <see cref="UpstreamSources.Needed"/>.</summary>
    readonly int _firstLeftOut = int.MaxValue;
    /// <summary>Set when the compilation met a type that one of the libraries left out may hold: the program is then
    /// compiled again with all of them (Cs2WasmCompiler.Compile).</summary>
    public volatile bool NeedsAll;
    /// <summary>Thrown where that is found.</summary>
    public sealed class MoreLibraries(string what) : Exception("a library that was left out may be needed: " + what) { }
    /// <summary>CS2WASM_NO_LIB_RESTART=1: every library Roslyn reports as used is read at the start, as before.</summary>
    static readonly bool s_noRestart = Environment.GetEnvironmentVariable("CS2WASM_NO_LIB_RESTART") == "1";
    /// <summary>CS2WASM_BREAK_LIB_RESTART=1 (to prove the rule by breaking it): a type the libraries read do not
    /// settle is taken as they have it, and nothing is compiled again.</summary>
    static readonly bool s_breakRestart = Environment.GetEnvironmentVariable("CS2WASM_BREAK_LIB_RESTART") == "1";

    MoreLibraries More(string what)
    {
        NeedsAll = true;
        return new MoreLibraries(what);
    }

    static int IndexOfLibrary(INamedTypeSymbol t) => Array.IndexOf(UpstreamSources.Libraries, t.ContainingAssembly?.Name ?? "");

    public Library(CSharpCompilation program, bool allReported = false)
    {
        var sw = s_times ? System.Diagnostics.Stopwatch.StartNew() : null;
        var needed = UpstreamSources.Needed(program, allReported || s_noRestart, out _firstLeftOut);
        if (s_breakRestart) _firstLeftOut = int.MaxValue;
        if (sw != null) { Console.Error.WriteLine($"    library: {sw.ElapsedMilliseconds,6} ms  what the program needs ({needed.Count} libraries{UpstreamSources.NeededWhy}{(_firstLeftOut != int.MaxValue ? "; left out until a type of theirs is met: " + UpstreamSources.Libraries[_firstLeftOut] + " and after" : allReported ? "; again, with every library reported" : "")})"); sw.Restart(); }
        All = Upstream(needed);
        if (sw != null) Console.Error.WriteLine($"    library: {sw.ElapsedMilliseconds,6} ms  read and parsed in all");
        Compilation = All[0];
        foreach (var c in All)
        {
            foreach (var t in c.SyntaxTrees) _trees.Add(t);
        }
        if (Environment.GetEnvironmentVariable("CS2WASM_REF_LIBS") == "1") PrintReferenceLibraries(program);
    }

    /// <summary>CS2WASM_REF_LIBS=1 (with CS2WASM_ALL_CORE_LIBS=1, so that every core library is there to look in): for
    /// each reference assembly of the program, the libraries its public types are found in, with how many (the
    /// check of <see cref="UpstreamSources.Needed"/>'s rule: a reference assembly's types are in the core library of
    /// its name or in CoreLib, and in no other core library).</summary>
    void PrintReferenceLibraries(CSharpCompilation program)
    {
        foreach (var r in program.References)
        {
            if (program.GetAssemblyOrModuleSymbol(r) is not IAssemblySymbol asm) continue;
            var by = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var work = new Stack<INamespaceSymbol>(); work.Push(asm.GlobalNamespace);
            while (work.Count > 0)
            {
                var ns = work.Pop();
                foreach (var n in ns.GetNamespaceMembers()) work.Push(n);
                foreach (var t in ns.GetTypeMembers())
                {
                    if (t.DeclaredAccessibility != Accessibility.Public) continue;
                    var lib = Lookup(t)?.ContainingAssembly.Name ?? "(none)";
                    by[lib] = by.GetValueOrDefault(lib) + 1;
                }
            }
            if (by.Count > 0) Console.Error.WriteLine($"reference {asm.Name}: {string.Join(", ", by.Select(kv => kv.Key + " " + kv.Value))}");
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

    /// <summary>Methods a library's manifest declares pure for initializers (`pure-init Namespace.Type.Method`): their
    /// result is their only effect, so an unread static field they initialize is left out (FunctionCompiler.PureInit).</summary>
    public static readonly HashSet<string> PureInitMethods = [];
    /// <summary>Lookups that can fail (`pure-init-checked`): pure for initializers, but a left-out initializer's call
    /// still runs (its failure is the program's), only its result is dropped.</summary>
    public static readonly HashSet<string> CheckedInitMethods = [];
    /// <summary>A lookup's batch form (`pure-init-batch Namespace.Type.Method BatchMethod`): a run of left-out lookups
    /// of (a type field, a name, a constant) is one call of it over tables (FunctionCompiler.FlushLookups).</summary>
    public static readonly Dictionary<string, string> BatchInitMethods = [];
    public static string PureInitKey(IMethodSymbol m) => Types.FullName(m.ContainingType.OriginalDefinition) + "." + m.Name;

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

    static readonly Dictionary<string, List<(string Name, string Path)>> s_resources = [];
    static readonly Dictionary<string, byte[]> s_resourceBytes = [];

    /// <summary>The libraries' manifest resources: assembly, logical name, and the contents (made when asked for).</summary>
    public IEnumerable<(string Asm, string Name, Func<byte[]> Data)> ManifestResources()
    {
        foreach (var c in All)
            if (s_resources.TryGetValue(c.AssemblyName!, out var list))
                foreach (var (name, path) in list)
                    yield return (c.AssemblyName!, name, () => { lock (s_resourceBytes) { return s_resourceBytes.TryGetValue(path, out var b) ? b : s_resourceBytes[path] = ResourceBytes(path); } });
    }

    /// <summary>A resource file's contents; a .resx as the build compiles it (System.Resources' .resources format,
    /// as ResourceWriter writes it).</summary>
    static byte[] ResourceBytes(string path)
    {
        if (!path.EndsWith(".resx", StringComparison.OrdinalIgnoreCase)) return File.ReadAllBytes(path);
        var doc = System.Xml.Linq.XDocument.Load(path);
        using var ms = new MemoryStream();
        using (var w = new System.Resources.ResourceWriter(ms))
        {
            foreach (var d in doc.Root!.Elements("data"))
            {
                if (d.Attribute("type") != null || d.Attribute("mimetype") != null)
                    throw new CompileException($"{path}: resource '{d.Attribute("name")?.Value}' is not a string (only strings are supported)");
                w.AddResource(d.Attribute("name")!.Value, d.Element("value")?.Value ?? "");
            }
        }
        return ms.ToArray();
    }

    static readonly object s_lock = new();
    /// <summary>CS2WASM_TIMES=1: each library's files read and parsed, in milliseconds (summed over the threads that
    /// parse, so on several processors more than the time that passed).</summary>
    static readonly bool s_times = Environment.GetEnvironmentVariable("CS2WASM_TIMES") == "1";
    static long s_readTicks, s_parseTicks, s_chars;
    static readonly bool s_noDocSkip = Environment.GetEnvironmentVariable("CS2WASM_NO_LIB_DOC_SKIP") == "1";
    static readonly Dictionary<string, CSharpCompilation> s_built = [];
    static List<(string Name, string Text)>? s_own;

    /// <summary>The libraries a program needs, with what they are compiled against (in library order), each compiled
    /// once per process when a program first needs it (see <see cref="References"/>).</summary>
    static IReadOnlyList<CSharpCompilation> Upstream(IReadOnlyList<string> needed)
    {
        lock (s_lock)
        {
            s_own ??= Resources("upstream/").ToList();
            var all = new HashSet<string>();
            CSharpCompilation Build(string lib)
            {
                all.Add(lib);
                if (s_built.TryGetValue(lib, out var c)) { foreach (var r in References(lib)) all.Add(r); return c; }
                var refs = References(lib).Select(Build).ToList();
                return s_built[lib] = BuildOne(lib, s_own, refs);
            }
            foreach (var lib in needed) Build(lib);
            return UpstreamSources.Libraries.Where(all.Contains).Select(l => s_built[l]).ToList();
        }
    }

    /// <summary>What a library is compiled against: those before it in library order, or for a standalone one the
    /// core libraries and what its manifest references (with what they reference).</summary>
    static List<string> References(string lib)
    {
        var libs = UpstreamSources.Libraries;
        if (!UpstreamSources.Standalone.Contains(lib))
            return libs.Take(Array.IndexOf(libs, lib)).Where(l => !UpstreamSources.Standalone.Contains(l)).ToList();
        var set = new HashSet<string>(libs.Take(UpstreamSources.CoreCount));
        var work = new Stack<string>(ManifestReferences(lib));
        while (work.Count > 0)
        {
            var r = work.Pop();
            if (!set.Add(r)) continue;
            if (UpstreamSources.Standalone.Contains(r)) foreach (var x in ManifestReferences(r)) work.Push(x);
        }
        return libs.Where(set.Contains).ToList();
    }

    static IEnumerable<string> ManifestReferences(string lib) =>
        (s_own!.FirstOrDefault(r => r.Name == "upstream/" + lib + "/manifest.txt").Text ?? "").Split((char)10)
            .Select(l => l.Trim(' ', (char)13)).Where(l => l.StartsWith("reference ")).Select(l => l[10..].Trim());

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
            foreach (var l in lines.Where(l => l.StartsWith("pure-init "))) PureInitMethods.Add(l[10..].Trim());
            foreach (var l in lines.Where(l => l.StartsWith("pure-init-checked "))) { PureInitMethods.Add(l[18..].Trim()); CheckedInitMethods.Add(l[18..].Trim()); }
            foreach (var l in lines.Where(l => l.StartsWith("pure-init-batch ")))
            {
                var parts = l[16..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2) BatchInitMethods[parts[0]] = parts[1];
            }
            // (the libraries' documentation comments are passed over as plain comments: parsed as XML they are a
            // large part of the libraries' parse, and nothing reads them. The trees' text, positions and symbols are
            // the same. CS2WASM_NO_LIB_DOC_SKIP=1: parsed, as they were)
            var po = new CSharpParseOptions(LanguageVersion.Preview, s_noDocSkip ? DocumentationMode.Parse : DocumentationMode.None, preprocessorSymbols: defines);
            var files = lines.Where(l => l.StartsWith("file ")).Select(l => l[5..]).ToList();
            // `resource <logical name> <file>`: a manifest resource of the library's assembly (a .resx is compiled to
            // .resources when a program reads it)
            var resources = lines.Where(l => l.StartsWith("resource ")).Select(l => l[9..].Split(' ', 2)).Where(p => p.Length == 2).ToList();
            // `root <dir>`: another source tree of the cache (Roslyn's), fetched like the runtime's
            var srcRoot = lines.FirstOrDefault(l => l.StartsWith("root ")) is { } rl ? UpstreamSources.OtherRoot(rl[5..].Trim()) : UpstreamSources.Root;
            var fetch = files.Concat(resources.Select(r => r[1])).ToList();
            if (srcRoot == UpstreamSources.Root) UpstreamSources.Ensure(fetch); else UpstreamSources.EnsureOther(srcRoot, fetch);
            var root = srcRoot;
            s_resources[lib] = resources.Select(r => (r[0], Path.Combine(root, r[1]))).ToList();
            bool nullable = lines.Contains("nullable enable");
            // edit<TAB>file<TAB>text<TAB>replacement: an exact change to one upstream file (each must still apply)
            // (the strings of the numbers below 300 are made once and kept, as CoreCLR and NativeAOT keep them: the library is
            // compiled as Mono's, which keeps ten. `299.ToString()` is the same object every time there, and a number in
            // a concatenation costs no string. CS2WASM_NO_SMALL_NUMBERS=1: ten)
            bool smallNumbers = Environment.GetEnvironmentVariable("CS2WASM_NO_SMALL_NUMBERS") != "1";
            var edits = lines.Where(l => l.StartsWith("edit\t")).Select(l => l.Split('\t')).Where(p => p.Length >= 4 && (smallNumbers || !p[2].Contains("SmallNumberCacheLength")))
                .GroupBy(p => p[1]).ToDictionary(g => g.Key, g => g.Select(p => (p[2], p[3])).ToList());
            // (on one processor the files are parsed one after the other, in the same order: nothing to hand the
            // work to, and an engine with a single thread cannot wait for a task)
            bool many = Environment.ProcessorCount > 1;
            Func<string, SyntaxTree> parse = f =>
            {
                var path = Path.Combine(root, f);
                // (read at once: asking first whether it is there is a second trip to the file system for each of
                // some thousands of files)
                string text;
                long t0 = s_times ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                try { text = File.ReadAllText(path); }
                catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { throw new CompileException($"library source missing: {path}"); }
                if (edits.TryGetValue(f, out var es))
                    foreach (var (from, to) in es)
                    {
                        if (!text.Contains(from)) throw new CompileException($"library edit no longer applies: {f}: '{from}'");
                        text = text.Replace(from, to);
                    }
                if (!s_times) return CSharpSyntaxTree.ParseText(text, po, path: UpstreamSources.DisplayPrefix(root) + f);
                long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                var tree = CSharpSyntaxTree.ParseText(text, po, path: UpstreamSources.DisplayPrefix(root) + f);
                long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
                Interlocked.Add(ref s_readTicks, t1 - t0); Interlocked.Add(ref s_parseTicks, t2 - t1); Interlocked.Add(ref s_chars, text.Length);
                return tree;
            };
            var swLib = s_times ? System.Diagnostics.Stopwatch.StartNew() : null;
            s_readTicks = s_parseTicks = s_chars = 0;
            var trees = (many ? files.AsParallel().AsOrdered().Select(parse) : files.Select(parse)).ToList();
            var ownFiles = own.Where(r => r.Name.StartsWith(prefix) && r.Name.EndsWith(".cs"));
            trees.AddRange(many ? ownFiles.AsParallel().AsOrdered().Select(r => CSharpSyntaxTree.ParseText(r.Text, po, path: "cs2wasm/" + r.Name))
                                : ownFiles.Select(r => CSharpSyntaxTree.ParseText(r.Text, po, path: "cs2wasm/" + r.Name)));
            long tTrees = swLib?.ElapsedMilliseconds ?? 0;
            var made = CSharpCompilation.Create(lib, trees, built.Select(b => (MetadataReference)b.ToMetadataReference()),
                nullable ? s_options.WithNullableContextOptions(NullableContextOptions.Enable) : s_options);
            if (swLib != null)
            {
                long f = System.Diagnostics.Stopwatch.Frequency;
                Console.Error.WriteLine($"    library: {swLib.ElapsedMilliseconds,6} ms  {lib}: {trees.Count} files, {s_chars / 1000000.0:F1}M characters; reading {s_readTicks * 1000 / f} ms, parsing {s_parseTicks * 1000 / f} ms (both summed over threads), all its trees {tTrees} ms, the compilation {swLib.ElapsedMilliseconds - tTrees} ms");
            }
            return made;
        }
    }

    /// <summary>The library's type with a reference type's metadata name, looked up when first asked for (walking all
    /// of the libraries' declarations up front costs seconds on every build): the first public one in library order,
    /// else the first.</summary>
    INamedTypeSymbol? Lookup(INamedTypeSymbol def)
    {
        var name = MetadataName(def);
        lock (_types)
            if (_types.TryGetValue(name, out var known)) return known;
        INamedTypeSymbol? found = null;
        foreach (var c in All)
        {
            if (c.Assembly.GetTypeByMetadataName(name) is not { } t || !t.Locations.Any(l => l.IsInSource)) continue;
            if (found == null) found = t;
            else if (!IsVisible(found) && IsVisible(t)) { found = t; break; }
            if (IsVisible(found)) break;
        }
        // Libraries were left out: the answer is the one all of them would give only when it is a visible type of a
        // library before the first one left out (the search is in library order and stops at the first visible
        // type). Anything else a reference assembly's type comes to (none, or one that is not visible, which a later
        // library's visible type would replace) is not settled: compiled again with all of them.
        if (_firstLeftOut != int.MaxValue && !def.Locations.Any(l => l.IsInSource)
            && !(found != null && IsVisible(found) && IndexOfLibrary(found) < _firstLeftOut))
            throw More(name);
        lock (_types) _types[name] = found!;
        return found;
    }

    static string MetadataName(INamedTypeSymbol t)
    {
        if (t.ContainingType is { } ct) return MetadataName(ct) + "+" + t.MetadataName;
        var ns = t.ContainingNamespace;
        return ns == null || ns.IsGlobalNamespace ? t.MetadataName : ns.ToDisplayString() + "." + t.MetadataName;
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
        return Lookup(def.OriginalDefinition);
    }

    /// <summary>A library type by metadata name, the corlib's first.</summary>
    public INamedTypeSymbol? TypeByMetadataName(string name, bool probe = false)
    {
        foreach (var c in All)
            if (c.Assembly.GetTypeByMetadataName(name) is { } t)
            {
                // (libraries left out: one of them, earlier in the order than where this was found, may have it too)
                if (!probe && _firstLeftOut != int.MaxValue && IndexOfLibrary(t) >= _firstLeftOut) throw More(name);
                return t;
            }
        // (not found: one of the libraries left out may have it. `probe`: a question whose answer the caller uses
        // only for types the program reached, which Lookup settles)
        if (!probe && _firstLeftOut != int.MaxValue) throw More(name);
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
        "System.Private.CoreLib", "System.Collections", "System.Collections.NonGeneric", "System.ObjectModel",
        "System.Memory", "System.Threading", "System.Collections.Concurrent", "System.Linq", "System.Console",
        "System.Runtime.Numerics", "System.Threading.Tasks.Parallel", "System.Text.RegularExpressions",
        "System.Linq.Expressions", "Microsoft.CSharp", "System.Linq.Queryable", "System.Linq.Parallel", "System.Resources.Writer",
        "System.ComponentModel", "System.ComponentModel.Primitives", "System.Collections.Specialized",
        "System.Diagnostics.TraceSource", "System.Collections.Immutable", "System.IO.MemoryMappedFiles",
        "System.IO.Compression", "System.IO.Compression.Brotli", "System.Reflection.Metadata", "System.Private.Uri",
        "System.Diagnostics.DiagnosticSource", "System.Formats.Asn1", "System.Security.Claims",
        "System.Diagnostics.StackTrace", "System.Threading.Channels", "System.Diagnostics.FileVersionInfo", "System.Diagnostics.Process", "System.IO.FileSystem.Watcher", "System.Net.Primitives",
        "System.Security.Cryptography", "System.Text.Encodings.Web", "System.IO.Pipelines", "System.Text.Json", "System.Net.NameResolution", "System.Net.Sockets", "System.Net.Security",
        "System.Net.Quic", "System.Net.NetworkInformation", "System.Net.Http",
        "System.Runtime.Serialization.Primitives", "System.Private.Xml", "System.Xml.XPath",
        "System.Private.Xml.Linq", "System.Xml.XPath.XDocument", "System.Text.Encoding.CodePages",
        "Microsoft.CodeAnalysis", "Microsoft.CodeAnalysis.CSharp", "GodotSharp",
    ];

    /// <summary>Libraries compiled against the core ones and what their manifests reference (with what those are compiled
    /// against), so that needing one brings in only those: GodotSharp (stock Godot's C# API, for Godot scripts: section
    /// 10's proofs 3 and 4), which references System.Diagnostics.TraceSource (right after the core libraries).</summary>
    public static readonly HashSet<string> Standalone = ["GodotSharp"];

    public const int CoreCount = 15;

    /// <summary>What makes a program need an optional library: a reference to the assembly (Roslyn's are not in the
    /// framework), or its namespace in the source (the framework's reference assemblies all are referenced).</summary>
    static readonly (string Lib, string[] Assemblies, string[] Namespaces)[] Optional =
    [
        ("System.Collections.Immutable", [], ["System.Collections.Immutable", "System.Collections.Frozen"]),
        ("System.IO.MemoryMappedFiles", [], ["System.IO.MemoryMappedFiles"]),
        ("System.IO.Compression", [], ["System.IO.Compression"]),
        ("System.Reflection.Metadata", [], ["System.Reflection.Metadata", "System.Reflection.PortableExecutable"]),
        ("System.Linq.Parallel", [], ["AsParallel", "ParallelEnumerable", "ParallelQuery"]),
        ("System.Resources.Writer", [], ["ResourceWriter"]),
        ("System.Diagnostics.FileVersionInfo", [], ["FileVersionInfo"]),
        ("System.Diagnostics.Process", [], ["System.Diagnostics.Process", "Process.", "ProcessStartInfo"]),
        ("System.IO.FileSystem.Watcher", [], ["FileSystemWatcher"]),
        ("System.Text.Encodings.Web", [], ["System.Text.Encodings.Web"]),
        ("System.IO.Pipelines", [], ["System.IO.Pipelines"]),
        ("System.Text.Json", [], ["System.Text.Json"]),
        ("System.Private.Uri", [], ["Uri"]),
        ("System.Diagnostics.DiagnosticSource", [], ["System.Diagnostics.Metrics", "DiagnosticSource", "ActivitySource", "System.Diagnostics.Activity"]),
        ("System.Formats.Asn1", [], ["System.Formats.Asn1"]),
        ("System.Security.Claims", [], ["System.Security.Claims"]),
        ("System.Threading.Channels", [], ["System.Threading.Channels"]),
        ("System.Security.Cryptography", [], ["System.Security.Cryptography"]),
        ("System.Net.Primitives", [], ["System.Net"]),
        ("System.Net.Http", [], ["System.Net.Http"]),
        ("System.Private.Xml", [], ["System.Xml"]),
        ("Microsoft.CodeAnalysis.CSharp", ["Microsoft.CodeAnalysis", "Microsoft.CodeAnalysis.CSharp"], []),
        ("GodotSharp", ["GodotSharp"], []),
    ];

    /// <summary>Reference assemblies whose types a library of another name implements.</summary>
    static readonly Dictionary<string, string> s_referenceLibrary = new()
    {
        ["System.Xml.XDocument"] = "System.Private.Xml.Linq", ["System.Xml.Linq"] = "System.Private.Xml.Linq",
        ["System.Xml.ReaderWriter"] = "System.Private.Xml", ["System.Xml.XmlDocument"] = "System.Private.Xml",
        ["System.Xml.XmlSerializer"] = "System.Private.Xml", ["System.Xml"] = "System.Private.Xml",
        ["System.Runtime.Serialization.Xml"] = "System.Runtime.Serialization.Primitives",
        ["System.IO.Compression.ZipFile"] = "System.IO.Compression",
    };

    /// <summary>For CS2WASM_TIMES: what made the last library of the list needed.</summary>
    public static string NeededWhy = "";

    /// <summary>CS2WASM_ALL_CORE_LIBS=1: the first <see cref="CoreCount"/> libraries for every program, used or not.</summary>
    static readonly bool s_allCore = Environment.GetEnvironmentVariable("CS2WASM_ALL_CORE_LIBS") == "1";

    /// <summary>The libraries the program needs, in library order: CoreLib, a library whose reference assembly the
    /// program's code uses with every one before it (which it may reference), an optional one its text names
    /// likewise, and the standalone ones it uses. A reference assembly's types are in the core library of its name
    /// or in CoreLib (CS2WASM_REF_LIBS=1 lists them), and what the C# compiler itself calls for a construct
    /// (a `dynamic` operation's binders, an expression tree's factories, a query's methods) is among the references
    /// Roslyn reports as used.</summary>
    public static IReadOnlyList<string> Needed(CSharpCompilation program, bool allReported, out int firstLeftOut)
    {
        NeededWhy = "";
        firstLeftOut = int.MaxValue;
        // (a core library is compiled against those before it and no other, so the ones after the last the program
        // uses cannot be reached: their files are not read)
        int count = s_allCore ? CoreCount : 1;
        var standalone = new HashSet<string>();
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
            if (need && Standalone.Contains(lib)) standalone.Add(lib);
            else if (need) { count = i + 1; NeededWhy = "; the last for its name in the text: " + lib; }
        }
        // and the libraries whose reference assemblies the program's code uses, whatever its text says (a type
        // named in full: `new System.Net.Sockets.Socket(...)`); a reference assembly is its library's, or one of these
        var reported = new List<(int Index, string Lib, IAssemblySymbol? Asm)>();
        foreach (var r in program.GetUsedAssemblyReferences())
        {
            var asm = Path.GetFileNameWithoutExtension(r.Display ?? "");
            if (s_referenceLibrary.TryGetValue(asm, out var mapped)) asm = mapped;
            reported.Add((Array.IndexOf(Libraries, asm), asm, program.GetAssemblyOrModuleSymbol(r) as IAssemblySymbol));
        }
        // every library reported (what the program is compiled with when a type of one left out is met)
        int countAll = count;
        var standaloneAll = new HashSet<string>(standalone);
        string whyAll = NeededWhy;
        foreach (var (i, asm, _) in reported)
        {
            if (i < countAll) continue;
            if (Standalone.Contains(asm)) standaloneAll.Add(asm); else { countAll = i + 1; whyAll = "; the last for a reference used: " + asm; }
        }
        if (allReported) { count = countAll; standalone = standaloneAll; NeededWhy = whyAll; }
        else
        {
            // Roslyn reports a reference as used also when only a candidate came from it: a call written as an
            // extension (`xs.Select(...)`) reports every assembly with an extension method of that name
            // (System.Linq.Queryable, System.Linq.Parallel, System.Collections.Immutable for System.Linq's), and
            // with the last of them come all the libraries before it. So a reported assembly that has extension
            // methods is taken at the start only when the program's text has a word that names it: one of its
            // public top-level types, or the last part of a namespace it has public types in, which no reported
            // assembly earlier in library order has too (`Linq` is System.Linq's, though the three others have
            // types in that namespace; `ImmutableArray`, `Frozen`, `Queryable` are their own). One left out
            // wrongly is found when the compilation meets a type of it (Library.Lookup), and the program is
            // compiled again with all that are reported. In library order; a reference that is no library's
            // (System.Runtime: its types are CoreLib's) first.
            var ordered = reported.OrderBy(x => x.Index).ToList();
            // (the assemblies without extension methods first: reported for what they are)
            foreach (var (i, asm, sym) in ordered)
            {
                if (i < count || sym == null || sym.MightContainExtensionMethods) continue;
                if (Standalone.Contains(asm)) standalone.Add(asm); else { count = i + 1; NeededWhy = "; the last for a reference used: " + asm; }
            }
            if (ordered.Any(x => x.Index >= count && x.Asm is { MightContainExtensionMethods: true }))
            {
                var words = Words(program);
                var earlier = new HashSet<string>(StringComparer.Ordinal);
                foreach (var (i, asm, sym) in ordered)
                {
                    if (sym == null) continue;
                    var own = PublicNames(sym);
                    bool named = i >= count && sym.MightContainExtensionMethods && own.Any(n => !earlier.Contains(n) && words.Contains(n));
                    earlier.UnionWith(own);
                    if (!named) continue;
                    if (Standalone.Contains(asm)) standalone.Add(asm); else { count = i + 1; NeededWhy = "; the last for a reference used and named: " + asm; }
                }
            }
        }
        // (a standalone library's references come with it: Library.Upstream builds them)
        var result = Libraries.Where((l, i) => i < count && !Standalone.Contains(l) || standalone.Contains(l)).ToList();
        if (count != countAll || standalone.Count != standaloneAll.Count)
            for (int i = 0; i < Libraries.Length; i++)
                if ((i < countAll && !Standalone.Contains(Libraries[i]) || standaloneAll.Contains(Libraries[i])) && !result.Contains(Libraries[i])) { firstLeftOut = i; break; }
        return result;
    }

    /// <summary>The names of an assembly's public top-level types and the last parts of the namespaces they are in.</summary>
    static HashSet<string> PublicNames(IAssemblySymbol asm)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var work = new Stack<INamespaceSymbol>();
        work.Push(asm.GlobalNamespace);
        while (work.Count > 0)
        {
            var ns = work.Pop();
            foreach (var n in ns.GetNamespaceMembers()) work.Push(n);
            bool any = false;
            foreach (var t in ns.GetTypeMembers())
            {
                if (t.DeclaredAccessibility != Accessibility.Public) continue;
                names.Add(t.Name);
                any = true;
            }
            if (any && !ns.IsGlobalNamespace) names.Add(ns.Name);
        }
        return names;
    }

    /// <summary>The words of the program's text (letters, digits and '_', whatever they are part of: a comment's or a
    /// string's words count, which errs to taking a library).</summary>
    static HashSet<string> Words(CSharpCompilation program)
    {
        var words = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tree in program.SyntaxTrees)
        {
            var text = tree.GetText().ToString();
            for (int i = 0; i < text.Length; )
            {
                if (!(char.IsLetter(text[i]) || text[i] == '_')) { i++; continue; }
                int s = i;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
                words.Add(text[s..i]);
            }
        }
        return words;
    }

    public const string Repository = "https://github.com/dotnet/runtime.git";

    /// <summary>Roslyn's sources (its compiler libraries, for programs that use them: section 9), at the version
    /// cs2wasm itself is built with.</summary>
    public const string RoslynRepository = "https://github.com/dotnet/roslyn.git";
    public const string RoslynCommit = "1324e1d34a0490454837a22370e75bbaeecffb87";

    /// <summary>Another source tree beside the runtime's in the cache (`root` in a manifest).</summary>
    public static string OtherRoot(string name) => Path.Combine(Path.GetDirectoryName(Root.TrimEnd('/', '\\'))!, name);

    /// <summary>Stock Godot's sources (GodotSharp, the Godot C# API: section 10's proofs 3 and 4), at the version the
    /// proofs compare with.</summary>
    public const string GodotRepository = "https://github.com/godotengine/godot.git";
    public const string GodotCommit = "ed1daf0bf001b61586d9930840f2f1394092c079";
    const string GodotGlue = "modules/mono/glue/GodotSharp/GodotSharp/Generated/";

    /// <summary>The other source trees (`root` in a manifest): repository, commit, and how the build names their files.</summary>
    static (string repo, string commit, string label, string prefix) Tree(string root) => Path.GetFileName(root.TrimEnd('/', '\\')) switch
    {
        var n when n.StartsWith("roslyn-") => (RoslynRepository, RoslynCommit, "Roslyn's compiler sources", "dotnet/roslyn/"),
        var n when n.StartsWith("godot-") => (GodotRepository, GodotCommit, "Godot's C# API sources", "godotengine/godot/"),
        var n => throw new CompileException("unknown library source tree: " + n),
    };

    public static string DisplayPrefix(string root) => root == Root ? "dotnet/runtime/" : Tree(root).prefix;

    /// <summary>As <see cref="Ensure"/>, for another tree: a sparse, blobless fetch of its commit. Godot's generated
    /// glue (the C# classes of the engine's API) is not in the repository: the stock editor writes it
    /// (CS2WASM_GODOT_EDITOR, else a standard install).</summary>
    public static void EnsureOther(string root, IEnumerable<string> files)
    {
        var missing = files.Distinct().Where(f => !File.Exists(Path.Combine(root, f))).ToList();
        if (missing.Count == 0) return;
        var (repo, commit, label, _) = Tree(root);
        using var mutex = new Mutex(false, "cs2wasm-sources-" + Path.GetFileName(root.TrimEnd('/', '\\')));
        try { mutex.WaitOne(); } catch (AbandonedMutexException) { }
        try
        {
            missing = missing.Where(f => !File.Exists(Path.Combine(root, f))).ToList();
            if (missing.Count == 0) return;
            var fromGit = missing.Where(f => !f.StartsWith(GodotGlue, StringComparison.Ordinal)).ToList();
            if (fromGit.Count > 0)
            {
                var patterns = string.Join("\n", fromGit.Select(f => "/" + f.Replace('\\', '/'))) + "\n";
                if (!Directory.Exists(Path.Combine(root, ".git")))
                {
                    Directory.CreateDirectory(root);
                    Console.Error.WriteLine($"cs2wasm: fetching {label} into {root} (once)");
                    Git(root, "init", "-q");
                    Git(root, "config", "core.longpaths", "true");
                    Git(root, "remote", "add", "origin", repo);
                    Git(root, "sparse-checkout", "set", "--no-cone", "--stdin", patterns);
                    Git(root, "fetch", "-q", "--depth", "1", "--filter=blob:none", "origin", commit);
                    Git(root, "checkout", "-q", "FETCH_HEAD");
                }
                else Git(root, "sparse-checkout", "add", "--stdin", patterns);
            }
            if (missing.Any(f => f.StartsWith(GodotGlue, StringComparison.Ordinal))) GenerateGodotGlue(root);
        }
        finally { mutex.ReleaseMutex(); }
    }

    static void GenerateGodotGlue(string root)
    {
        const string install = @"C:\Program Files\Godot";
        var editor = Environment.GetEnvironmentVariable("CS2WASM_GODOT_EDITOR") is { Length: > 0 } e ? e
            : Directory.Exists(install) ? Directory.EnumerateFiles(install, "Godot_v*_mono_*_console.exe", SearchOption.AllDirectories).FirstOrDefault() : null;
        if (editor == null || !File.Exists(editor))
            throw new CompileException($"Godot's generated C# API is not in {root}; set CS2WASM_GODOT_EDITOR to a stock Godot .NET editor (it writes it: --headless --generate-mono-glue)");
        Console.Error.WriteLine($"cs2wasm: generating Godot's C# API with {editor} (once)");
        var psi = new System.Diagnostics.ProcessStartInfo(editor) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "--headless", "--generate-mono-glue", Path.Combine(root, "modules", "mono", "glue") }) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync();
        var er = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        if (!File.Exists(Path.Combine(root, GodotGlue, "GeneratedIncludes.props")))
            throw new CompileException("the Godot editor did not write the C# API: " + er.Result + o.Result);
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
