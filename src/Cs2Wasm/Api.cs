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
    public IReadOnlyList<ICompilerPlugin> Plugins { get; init; } = [];
    /// <summary>Minimum size of the imported memory, in 64 KiB pages. The engine creates it at least this large.</summary>
    public int MemoryInitialPages { get; init; } = 256;
    public int MemoryMaximumPages { get; init; } = 16384;
    public bool SharedMemory { get; init; } = true;
    public int StackSize { get; init; } = 1 << 20;
    public bool Optimize { get; init; } = true;
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
    public bool Success => Module != null;
}

public static class Cs2WasmCompiler
{
    public const string CompilerVersion = "0.1.0";

    public static CompileResult Compile(Compilation compilation, CompileOptions? options = null)
    {
        options ??= new CompileOptions();
        if (compilation is not CSharpCompilation cs)
            return new CompileResult { Diagnostics = [Diagnostic.Create(Diags.NotCSharp, Location.None)] };
        var d = new Driver(cs, options);
        byte[]? bytes = null; string? map = null;
        try
        {
            d.Run();
            if (!d.Diagnostics.Any(x => x.Severity == DiagnosticSeverity.Error))
                (bytes, map) = d.Emit();
        }
        catch (CompileException ex)
        {
            d.Report(Diags.Unsupported, ex.Location, ex.Message);
        }
        if (d.Diagnostics.Any(x => x.Severity == DiagnosticSeverity.Error)) bytes = null;
        return new CompileResult
        {
            Module = bytes, SourceMap = bytes == null ? null : map, Diagnostics = [.. d.Diagnostics],
            CycleCapableTypes = bytes == null ? [] : d.Arenas.CycleTypes.Select(id => Types.Key(d.TypeById[id])).ToList(),
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
    public static readonly DiagnosticDescriptor SandboxCapability = E("CW0003", "Capability does not exist in the sandbox", "'{0}' needs {1}, which does not exist in the WASM sandbox");
    public static readonly DiagnosticDescriptor PlatformInvoke = E("CW0004", "Platform invoke does not exist in the sandbox", "'{0}' uses platform invoke, which does not exist in the WASM sandbox");
    public static readonly DiagnosticDescriptor ExportNeedsName = E("CW0005", "Export needs an entry point name", "'{0}' is [UnmanagedCallersOnly] without EntryPoint; give it EntryPoint = \"name\"");
    public static readonly DiagnosticDescriptor ExportShape = E("CW0006", "Invalid export", "'{0}' must be a static, non-generic method to be exported");
    public static readonly DiagnosticDescriptor DuplicateExport = E("CW0007", "Duplicate export", "Export name '{0}' is used twice");
    public static readonly DiagnosticDescriptor LibraryMissing = E("CW0100", "Library member not implemented", "'{0}' is not implemented in cs2wasm's library yet");
    public static readonly DiagnosticDescriptor Unsupported = E("CW0101", "Not supported yet", "{0}");
    public static readonly DiagnosticDescriptor LibraryError = E("CW0102", "Library error", "cs2wasm library failed to compile: {0}");
    public static readonly DiagnosticDescriptor BlockingWait = I("CW0200", "Blocking wait", "'{0}' blocks; on the engine thread it costs frame time");
    public static readonly DiagnosticDescriptor ReflectionUnknown = W("CW0201", "Reflection the compiler cannot follow", "'{0}' on a type the compiler cannot see: metadata it needs may be missing (keep the type with [DynamicallyAccessedMembers])");
    public static readonly DiagnosticDescriptor Plugin = E("CW0300", "Plugin error", "{0}");
}
