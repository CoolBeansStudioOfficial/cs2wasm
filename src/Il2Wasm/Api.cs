using System.Collections.Immutable;
using System.Text.Json;
using Il2Wasm.Compiler;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Il2Wasm;

/// <summary>One allowed engine method: the C# method (documentation comment id) and the WASM import it becomes.</summary>
public sealed record BoundaryImport(string Method, string Module, string Name);

/// <summary>The engine boundary (section 6.2), supplied by the embedder.</summary>
public sealed class BoundaryTable
{
    public List<BoundaryImport> Imports { get; init; } = [];
    /// <summary>Full name of an attribute that marks boundary methods (in addition to <c>extern</c>).</summary>
    public string? MarkerAttribute { get; init; }

    public static BoundaryTable FromJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var t = new BoundaryTable
        {
            MarkerAttribute = root.TryGetProperty("markerAttribute", out var ma) ? ma.GetString() : null,
        };
        if (root.TryGetProperty("imports", out var imps))
            foreach (var i in imps.EnumerateArray())
                t.Imports.Add(new BoundaryImport(i.GetProperty("method").GetString()!,
                    i.TryGetProperty("module", out var m) ? m.GetString()! : "env", i.GetProperty("name").GetString()!));
        return t;
    }
}

public sealed class CompileOptions
{
    /// <summary>Other compilations of the program (GodotSharp, the embedder's namespaces); the main one may reference them.</summary>
    public IReadOnlyList<Compilation> AdditionalCompilations { get; init; } = [];
    public BoundaryTable Boundary { get; init; } = new();
    /// <summary>Custom sections written into the module as given.</summary>
    public IReadOnlyList<(string Name, byte[] Data)> CustomSections { get; init; } = [];
    /// <summary>The program's manifest resources (its project's EmbeddedResource items: logical name, contents),
    /// for Assembly.GetManifestResourceStream and ResourceManager.</summary>
    public IReadOnlyList<(string Name, byte[] Data)> Resources { get; init; } = [];
    public IReadOnlyList<ICompilerPlugin> Plugins { get; init; } = [];
    /// <summary>Minimum size of the imported memory, in 64 KiB pages. The engine creates it at least this large.</summary>
    public int MemoryInitialPages { get; init; } = 256;
    public int MemoryMaximumPages { get; init; } = 16384;
    public bool SharedMemory { get; init; } = true;
    /// <summary>The first thread's stack, in bytes.</summary>
    public int StackSize { get; init; } = 1 << 20;
    public bool Optimize { get; init; } = true;
    /// <summary>The tests' build: freed memory is filled with a pattern and never used again, so a use after free
    /// shows (a trap, or output that differs) where it would have read whatever took the block. Costs memory.</summary>
    public bool Checked { get; init; }
    /// <summary>ABI version recorded in the version section; the engine refuses modules it does not understand.</summary>
    public int AbiVersion { get; init; } = 1;
}

public sealed class CompileResult
{
    public byte[]? Module { get; init; }
    public ImmutableArray<Diagnostic> Diagnostics { get; init; }
    /// <summary>Source map v3 mapping module byte offsets to C# files and lines.</summary>
    public string? SourceMap { get; init; }
    /// <summary>The cycle-capable types (section 4.3): their objects live in arenas; every other type's stores are
    /// plain counted stores.</summary>
    public IReadOnlyList<string> CycleCapableTypes { get; init; } = [];
    /// <summary>The functions compiled a second time because a plugin's Run added hooks after the first compile (0 when
    /// everything was added in ICompilerPluginBeforeCompile.RunBeforeCompile).</summary>
    public int RecompiledFunctions { get; init; }
    public bool Success => Module != null;
}

public static class Il2WasmCompiler
{
    public const string CompilerVersion = "0.1.0";

    /// <summary>Roslyn's own emit, for the developer's check of the names of what the C# compiler makes
    /// (IL2WASM_CHECK_NAMES, Compiler/RoslynNamesCheck.cs): set by the .NET-hosted command line and the tests, so
    /// that the self-hosted compiler, which has no use for it, does not carry Roslyn's emitter.</summary>
    public static Func<Compilation, Stream, bool>? EmitForNamesCheck;

    /// <summary>IL2WASM_NO_SECOND_PASS=1: a program with threads is compiled once, taking every object as shared.</summary>
    static readonly bool s_noSecondPass = Environment.GetEnvironmentVariable("IL2WASM_NO_SECOND_PASS") == "1";
    static readonly bool s_times = Environment.GetEnvironmentVariable("IL2WASM_TIMES") == "1";

    public static CompileResult Compile(Compilation compilation, CompileOptions? options = null)
    {
        options ??= new CompileOptions();
        if (compilation is not CSharpCompilation cs)
            return new CompileResult { Diagnostics = [Diagnostic.Create(Diags.NotCSharp, Location.None)] };
        // (compiled with the libraries the program is seen to need; when it meets a type that one left out may
        // hold, once more with all that Roslyn reports as used: Library.NeedsAll)
        return CompileWith(cs, options, false) ?? CompileWith(cs, options, true)!;
    }

    static CompileResult? CompileWith(CSharpCompilation cs, CompileOptions options, bool allLibraries)
    {
        var d = new Driver(cs, options, allLibraries);
        byte[]? bytes = null; string? map = null;
        try
        {
            // a program that starts threads is compiled as one (Driver.ThreadMode): known when the compilation
            // reaches the start of a thread, where it stops and begins again
            // (What that first compilation bound is used by the one that follows, so little of it is lost: compiling
            // for threads at once, when the text names a thread's start or the last build of the name started one,
            // was measured no faster. Test 088 under .NET: 6 to 9.5 s of 26 to 36 s before the thread's start is
            // met, and 47 to 56 s of processor time either way, three runs each.)
            bool threads = false;
            var sw = s_times ? System.Diagnostics.Stopwatch.StartNew() : null;
            try { d.Run(); }
            catch (Driver.ThreadsSeen) { threads = true; }
            if (threads && sw != null) Console.Error.WriteLine($"    {sw.ElapsedMilliseconds,6} ms  compiled without threads until the start of a thread was met ({d.AllMethods.Count(m => m.Compiled)} functions): begun again for threads");
            if (threads || d.UsesThreads && !Driver.s_expNoThreadMode && !d.Diagnostics.Any(x => x.Severity == DiagnosticSeverity.Error))
            {
                d = null!;
                d = new Driver(cs, options, allLibraries) { ThreadMode = true };
                d.Run();
                // Section 4.5: compiled again knowing which types another thread can reach, so that objects no other
                // thread can reach are read and borrowed from as without threads. The new compilation's own answer
                // must name no type the one it used did not (else again, with both), so every decision holds.
                if (!s_noSecondPass && d.Sharing is { Done: true } first && !d.Diagnostics.Any(x => x.Severity == DiagnosticSeverity.Error))
                {
                    var prior = first.PriorMap();
                    // (the types, and the statics a worker's code names: Sharing.SharedKeys)
                    var sharedKeys = first.SharedKeys();
                    for (int round = 0; round < 3; round++)
                    {
                        d = null!;
                        d = new Driver(cs, options, allLibraries) { ThreadMode = true, SharingPrior = prior };
                        d.Run();
                        if (d.Diagnostics.Any(x => x.Severity == DiagnosticSeverity.Error) || d.Sharing is not { Done: true } again) break;
                        var extra = again.SharedKeys().Where(k => !sharedKeys.Contains(k)).ToList();
                        if (Environment.GetEnvironmentVariable("IL2WASM_SHARING_ROUNDS") == "1") Console.Error.WriteLine($"sharing: round {round}: {extra.Count} more than the compilation was given{(extra.Count > 0 ? ": " + string.Join(", ", extra.Take(12)) : "")}");
                        if (extra.Count == 0) break;
                        if (round == 2) { d = new Driver(cs, options, allLibraries) { ThreadMode = true }; d.Run(); break; }
                        prior = again.PriorMap(prior);
                        sharedKeys.UnionWith(extra);
                    }
                }
            }
            if (d.Lib.NeedsAll) return null;
            if (!d.Diagnostics.Any(x => x.Severity == DiagnosticSeverity.Error))
                (bytes, map) = d.Emit();
            if (d.Lib.NeedsAll) return null;
        }
        catch (Exception) when (d.Lib.NeedsAll)
        {
            return null;
        }
        catch (CompileException ex)
        {
            d.Report(Diags.Unsupported, ex.Location, ex.Message);
        }
        catch (Exception ex)
        {
            // a compiler bug: a diagnostic for the embedder, never an exception through its build
            // (the first frame with a file, from the trace's text: "   at M in path:line N")
            var at = (ex.StackTrace ?? "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Contains(" in ") && l.Contains(":line "));
            var where = at == null ? "" : Path.GetFileName(at[(at.LastIndexOf(" in ", StringComparison.Ordinal) + 4)..at.LastIndexOf(":line ", StringComparison.Ordinal)]) + ":" + at[(at.LastIndexOf(":line ", StringComparison.Ordinal) + 6)..];
            d.Report(Diags.Unsupported, null, $"internal error: {ex.GetType().Name}: {ex.Message} at {where}");
            if (Environment.GetEnvironmentVariable("IL2WASM_DEBUG") != null) Console.Error.WriteLine(ex.ToString());
        }
        if (d.Diagnostics.Any(x => x.Severity == DiagnosticSeverity.Error)) bytes = null;
        return new CompileResult
        {
            Module = bytes, SourceMap = bytes == null ? null : map, Diagnostics = [.. d.Diagnostics],
            CycleCapableTypes = bytes == null ? [] : d.Arenas.CycleTypes.Select(id => Types.Key(d.TypeById[id])).ToList(),
            RecompiledFunctions = d.RecompiledFunctions,
        };
    }
}

/// <summary>Compiler diagnostics, reported like Roslyn's (section 6.4).</summary>
public static class Diags
{
    static DiagnosticDescriptor E(string id, string title, string msg) => new(id, title, msg, "il2wasm", DiagnosticSeverity.Error, true);
    static DiagnosticDescriptor W(string id, string title, string msg) => new(id, title, msg, "il2wasm", DiagnosticSeverity.Warning, true);
    static DiagnosticDescriptor I(string id, string title, string msg) => new(id, title, msg, "il2wasm", DiagnosticSeverity.Info, true);

    public static readonly DiagnosticDescriptor NotCSharp = E("CW0001", "Not C#", "il2wasm compiles C# compilations only");
    public static readonly DiagnosticDescriptor MissingBoundary = E("CW0002", "Engine method not in boundary table", "'{0}' has no body and is not in the boundary table");
    public static readonly DiagnosticDescriptor SandboxCapability = W("CW0003", "Capability does not exist in the sandbox", "'{0}' needs {1}, which does not exist in the WASM sandbox: it throws PlatformNotSupportedException when the program reaches it");
    public static readonly DiagnosticDescriptor PlatformInvoke = E("CW0004", "Platform invoke does not exist in the sandbox", "'{0}' uses platform invoke, which does not exist in the WASM sandbox");
    public static readonly DiagnosticDescriptor ExportNeedsName = E("CW0005", "Export needs an entry point name", "'{0}' is [UnmanagedCallersOnly] without EntryPoint; give it EntryPoint = \"name\"");
    public static readonly DiagnosticDescriptor ExportShape = E("CW0006", "Invalid export", "'{0}' must be a static, non-generic method to be exported");
    public static readonly DiagnosticDescriptor DuplicateExport = E("CW0007", "Duplicate export", "Export name '{0}' is used twice");
    public static readonly DiagnosticDescriptor LibraryMissing = W("CW0100", "Library member not implemented", "'{0}' is not implemented in il2wasm's library yet: it throws PlatformNotSupportedException when the program reaches it");
    public static readonly DiagnosticDescriptor Unsupported = E("CW0101", "Not supported yet", "{0}");
    public static readonly DiagnosticDescriptor LibraryError = E("CW0102", "Library error", "il2wasm library failed to compile: {0}");
    public static readonly DiagnosticDescriptor CyclePossible = I("CW0202", "Reference cycle possible", "objects of '{0}' can refer to each other in a cycle; such a group is freed together, when nothing outside it refers to any of its objects (objects nothing refers to go at once; a let-go part whose objects still refer to each other stays until the group goes)");
    public static readonly DiagnosticDescriptor BlockingWait = I("CW0200", "Blocking wait", "'{0}' blocks; on the engine thread it costs frame time");
    public static readonly DiagnosticDescriptor ReflectionUnknown = W("CW0201", "Reflection the compiler cannot follow", "'{0}' on a type the compiler cannot see: metadata it needs may be missing (keep the type with [DynamicallyAccessedMembers])");
    public static readonly DiagnosticDescriptor Plugin = E("CW0300", "Plugin error", "{0}");
}
