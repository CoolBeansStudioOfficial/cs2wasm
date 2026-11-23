using System.Collections.Immutable;
using System.Text.Json;
using Cs2Wasm.Compiler;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Cs2Wasm;

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

public static class Cs2WasmCompiler
{
    public const string CompilerVersion = "0.1.0";

    /// <summary>Roslyn's own emit, for the developer's check of the names of what the C# compiler makes
    /// (CS2WASM_CHECK_NAMES, Compiler/RoslynNamesCheck.cs): set by the .NET-hosted command line and the tests, so
    /// that the self-hosted compiler, which has no use for it, does not carry Roslyn's emitter.</summary>
    public static Func<Compilation, Stream, bool>? EmitForNamesCheck;

    /// <summary>CS2WASM_NO_SECOND_PASS=1: a program with threads is compiled once, taking every object as shared.</summary>
    static readonly bool s_noSecondPass = Environment.GetEnvironmentVariable("CS2WASM_NO_SECOND_PASS") == "1";
    static readonly bool s_times = Environment.GetEnvironmentVariable("CS2WASM_TIMES") == "1";

    public static CompileResult Compile(Compilation compilation, CompileOptions? options = null)
    {
        options ??= new CompileOptions();
        if (compilation is not CSharpCompilation cs)
            return new CompileResult { Diagnostics = [Diagnostic.Create(Diags.NotCSharp, Location.None)] };
        // (compiled with the libraries the program is seen to need; when it meets a type that one left out may
        // hold, once more with all that Roslyn reports as used: Library.NeedsAll. A program of the same name that
        // needed them all the last time in this process is given them at once: the module is the same either way)
        string name = cs.AssemblyName ?? "";
        bool all;
        lock (s_before) all = !s_noBuildHints && s_before.TryGetValue(name, out var b) && b.AllLibraries;
        var result = (all ? null : CompileWith(cs, options, false, name)) ?? CompileWith(cs, options, all = true, name)!;
        if (result.Success)
            lock (s_before) s_before[name] = s_before.GetValueOrDefault(name) with { AllLibraries = all };
        return result;
    }

    /// <summary>What the last build of a program of a name came to, in this process (a compiler that stays loaded:
    /// `--watch`, an embedder's worker, a host's snapshot): whether it started threads, and whether it needed every
    /// reported library. The next build of that name begins there instead of finding it out again by a compilation
    /// that is thrown away; what it is compiled as is decided as before (CompileWith). CS2WASM_NO_BUILD_HINTS=1:
    /// every build begins as the first.</summary>
    static readonly Dictionary<string, (bool? Threads, bool AllLibraries)> s_before = [];
    static readonly bool s_noBuildHints = Environment.GetEnvironmentVariable("CS2WASM_NO_BUILD_HINTS") == "1";
    /// <summary>Whether the program's text starts a thread by name: `new Thread` (qualified or not), `Task.Run`,
    /// `Factory.StartNew`, `Parallel.For`/`ForEach`/`Invoke` (and the async ones), `ThreadPool.`,
    /// `.AsParallel`, or awaits (`await`). Words and dots, whatever space is between them and wherever they are
    /// (a comment's count).
    /// It only chooses which compilation is tried first: a program that reaches a thread's start some other way is
    /// found as before, and one that names a start it never reaches is compiled again without threads.</summary>
    static bool NamesThreadStart(CSharpCompilation cs)
    {
        foreach (var tree in cs.SyntaxTrees)
        {
            var text = tree.GetText().ToString();
            // (the word before the last dot, whether the last token was a dot, and whether a `new` is being
            // followed by a (qualified) name)
            string before = "";
            bool dot = false, afterNew = false;
            for (int i = 0; i < text.Length; )
            {
                char c = text[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }
                if (char.IsLetter(c) || c == '_')
                {
                    int s = i;
                    while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
                    var w = text.AsSpan(s, i - s);
                    if (afterNew && w.SequenceEqual("Thread")) return true;
                    if (dot)
                    {
                        if (before == "Task" && w.SequenceEqual("Run") || before == "Factory" && w.SequenceEqual("StartNew") || before == "ThreadPool"
                            || before == "Parallel" && (w.SequenceEqual("For") || w.SequenceEqual("ForEach") || w.SequenceEqual("Invoke") || w.SequenceEqual("ForAsync") || w.SequenceEqual("ForEachAsync"))
                            || w.SequenceEqual("AsParallel"))
                            return true;
                    }
                    else if (w.SequenceEqual("new")) afterNew = true;
                    // (an awaited task's continuation goes to the thread pool, which starts its workers)
                    else if (w.SequenceEqual("await")) return true;
                    before = w.SequenceEqual("Task") ? "Task" : w.SequenceEqual("Factory") ? "Factory" : w.SequenceEqual("ThreadPool") ? "ThreadPool" : w.SequenceEqual("Parallel") ? "Parallel" : "";
                    dot = false;
                }
                else
                {
                    if (c == '.') dot = true; else { dot = false; before = ""; afterNew = false; }
                    i++;
                }
            }
        }
        return false;
    }

    /// <summary>CS2WASM_BREAK_BUILD_HINTS=1 (to prove the rule by breaking it): a program is compiled for threads
    /// when the last of its name was, without asking whether it starts one.</summary>
    static readonly bool s_breakBuildHints = Environment.GetEnvironmentVariable("CS2WASM_BREAK_BUILD_HINTS") == "1";

    static CompileResult? CompileWith(CSharpCompilation cs, CompileOptions options, bool allLibraries, string name)
    {
        Driver d = null!;
        byte[]? bytes = null; string? map = null;
        // The last build of this name started threads, or (a first build) the program's text starts one by name
        // (NamesThreadStart): it is compiled for threads at once, where it would be compiled without them up to the
        // start of a thread and then begun again. That compilation is kept only when it does start one and has no
        // error (the same functions are reached with and without threads, so that is when the compilation without
        // them would have stopped at one); anything else is compiled as before.
        bool threadsFirst = false;
        if (!s_noBuildHints && !Driver.s_expNoThreadMode)
        {
            bool? known = null;
            lock (s_before) if (s_before.TryGetValue(name, out var before)) known = before.Threads;
            threadsFirst = known ?? NamesThreadStart(cs);
        }
        if (threadsFirst)
        {
            var sw0 = s_times ? System.Diagnostics.Stopwatch.StartNew() : null;
            try
            {
                d = new Driver(cs, options, allLibraries) { ThreadMode = true };
                d.Run();
                threadsFirst = s_breakBuildHints || d.UsesThreads && !d.Diagnostics.Any(x => x.Severity == DiagnosticSeverity.Error);
            }
            catch (Exception) { threadsFirst = false; }
            if (d != null && d.Lib.NeedsAll) return null;
            if (!threadsFirst && sw0 != null) Console.Error.WriteLine($"    {sw0.ElapsedMilliseconds,6} ms  compiled for threads first (the last build of '{name}' started one, or its text names a thread's start): it starts none, or failed: begun again");
            else if (sw0 != null) Console.Error.WriteLine("    compiled for threads at once (the last build of its name started one, or its text names a thread's start)");
        }
        if (!threadsFirst) d = new Driver(cs, options, allLibraries);
        bool forThreads = threadsFirst;
        try
        {
            // a program that starts threads is compiled as one (Driver.ThreadMode): known when the compilation
            // reaches the start of a thread, where it stops and begins again
            bool threads = false;
            var sw = s_times ? System.Diagnostics.Stopwatch.StartNew() : null;
            if (!threadsFirst)
            {
            try { d.Run(); }
            catch (Driver.ThreadsSeen) { threads = true; }
            }
            if (threads && sw != null) Console.Error.WriteLine($"    {sw.ElapsedMilliseconds,6} ms  compiled without threads until the start of a thread was met ({d.AllMethods.Count(m => m.Compiled)} functions): begun again for threads");
            if (threadsFirst || threads || d.UsesThreads && !Driver.s_expNoThreadMode && !d.Diagnostics.Any(x => x.Severity == DiagnosticSeverity.Error))
            {
                forThreads = true;
                if (!threadsFirst)
                {
                d = null!;
                d = new Driver(cs, options, allLibraries) { ThreadMode = true };
                d.Run();
                }
                // Section 4.5: compiled again knowing which types another thread can reach, so that objects no other
                // thread can reach are read and borrowed from as without threads. The new compilation's own answer
                // must name no type the one it used did not (else again, with both), so every decision holds.
                if (!s_noSecondPass && d.Sharing is { Done: true } first && !d.Diagnostics.Any(x => x.Severity == DiagnosticSeverity.Error))
                {
                    var prior = first.PriorMap();
                    var sharedKeys = first.SharedTypeSymbols().Select(Types.Key).ToHashSet();
                    for (int round = 0; round < 3; round++)
                    {
                        d = null!;
                        d = new Driver(cs, options, allLibraries) { ThreadMode = true, SharingPrior = prior };
                        d.Run();
                        if (d.Diagnostics.Any(x => x.Severity == DiagnosticSeverity.Error) || d.Sharing is not { Done: true } again) break;
                        var extra = again.SharedTypeSymbols().Select(Types.Key).Where(k => !sharedKeys.Contains(k)).ToList();
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
            if (bytes != null)
                lock (s_before) s_before[name] = s_before.GetValueOrDefault(name) with { Threads = forThreads };
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
            if (Environment.GetEnvironmentVariable("CS2WASM_DEBUG") != null) Console.Error.WriteLine(ex.ToString());
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
    static DiagnosticDescriptor E(string id, string title, string msg) => new(id, title, msg, "cs2wasm", DiagnosticSeverity.Error, true);
    static DiagnosticDescriptor W(string id, string title, string msg) => new(id, title, msg, "cs2wasm", DiagnosticSeverity.Warning, true);
    static DiagnosticDescriptor I(string id, string title, string msg) => new(id, title, msg, "cs2wasm", DiagnosticSeverity.Info, true);

    public static readonly DiagnosticDescriptor NotCSharp = E("CW0001", "Not C#", "cs2wasm compiles C# compilations only");
    public static readonly DiagnosticDescriptor MissingBoundary = E("CW0002", "Engine method not in boundary table", "'{0}' has no body and is not in the boundary table");
    public static readonly DiagnosticDescriptor SandboxCapability = W("CW0003", "Capability does not exist in the sandbox", "'{0}' needs {1}, which does not exist in the WASM sandbox: it throws PlatformNotSupportedException when the program reaches it");
    public static readonly DiagnosticDescriptor PlatformInvoke = E("CW0004", "Platform invoke does not exist in the sandbox", "'{0}' uses platform invoke, which does not exist in the WASM sandbox");
    public static readonly DiagnosticDescriptor ExportNeedsName = E("CW0005", "Export needs an entry point name", "'{0}' is [UnmanagedCallersOnly] without EntryPoint; give it EntryPoint = \"name\"");
    public static readonly DiagnosticDescriptor ExportShape = E("CW0006", "Invalid export", "'{0}' must be a static, non-generic method to be exported");
    public static readonly DiagnosticDescriptor DuplicateExport = E("CW0007", "Duplicate export", "Export name '{0}' is used twice");
    public static readonly DiagnosticDescriptor LibraryMissing = W("CW0100", "Library member not implemented", "'{0}' is not implemented in cs2wasm's library yet: it throws PlatformNotSupportedException when the program reaches it");
    public static readonly DiagnosticDescriptor Unsupported = E("CW0101", "Not supported yet", "{0}");
    public static readonly DiagnosticDescriptor LibraryError = E("CW0102", "Library error", "cs2wasm library failed to compile: {0}");
    public static readonly DiagnosticDescriptor CyclePossible = I("CW0202", "Reference cycle possible", "objects of '{0}' can refer to each other in a cycle; such a group is freed together, when nothing outside it refers to any of its objects (objects nothing refers to go at once; a let-go part whose objects still refer to each other stays until the group goes)");
    public static readonly DiagnosticDescriptor BlockingWait = I("CW0200", "Blocking wait", "'{0}' blocks; on the engine thread it costs frame time");
    public static readonly DiagnosticDescriptor ReflectionUnknown = W("CW0201", "Reflection the compiler cannot follow", "'{0}' on a type the compiler cannot see: metadata it needs may be missing (keep the type with [DynamicallyAccessedMembers])");
    public static readonly DiagnosticDescriptor Plugin = E("CW0300", "Plugin error", "{0}");
}
