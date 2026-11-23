using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Il2Wasm.IL.Tests;

/// <summary>Compiles C# to IL assemblies with Roslyn (as `dotnet build` would): the stub CoreLib, and test programs
/// against it or against the running .NET's assemblies (the oracle).</summary>
static class Build
{
    public static readonly string Dir = Path.Combine(Path.GetTempPath(), "il2wasm-il-tests", Environment.ProcessId.ToString());
    static string? s_coreLib;

    static readonly CSharpParseOptions s_parse = new(LanguageVersion.Preview);

    public static string Here(string rel) => Path.Combine(AppContext.BaseDirectory, rel);

    /// <summary>The stub System.Private.CoreLib.dll (built once per run).</summary>
    public static string CoreLib()
    {
        if (s_coreLib != null) return s_coreLib;
        var sources = Directory.GetFiles(Here("CoreLibStub"), "*.cs").OrderBy(f => f, StringComparer.Ordinal).ToArray();
        s_coreLib = Compile("System.Private.CoreLib", sources.Select(File.ReadAllText).ToArray(), [], library: true);
        return s_coreLib;
    }

    /// <summary>Compiles sources to Dir/name.dll (with its portable PDB) and returns the path.</summary>
    public static string Compile(string name, string[] sources, string[] references, bool library = false)
    {
        Directory.CreateDirectory(Dir);
        var trees = sources.Select((s, i) => CSharpSyntaxTree.ParseText(Microsoft.CodeAnalysis.Text.SourceText.From(s, System.Text.Encoding.UTF8), s_parse, path: $"{name}{i}.cs")).ToArray();
        var refs = references.Select(r => (MetadataReference)MetadataReference.CreateFromFile(r)).ToList();
        var opts = new CSharpCompilationOptions(library ? OutputKind.DynamicallyLinkedLibrary : OutputKind.ConsoleApplication,
            allowUnsafe: true, optimizationLevel: OptimizationLevel.Release, deterministic: true, nullableContextOptions: NullableContextOptions.Disable);
        var c = CSharpCompilation.Create(name, trees, refs, opts);
        var dll = Path.Combine(Dir, name + ".dll");
        var pdb = Path.Combine(Dir, name + ".pdb");
        using (var ds = File.Create(dll))
        using (var ps = File.Create(pdb))
        {
            var r = c.Emit(ds, ps, options: new Microsoft.CodeAnalysis.Emit.EmitOptions(debugInformationFormat: Microsoft.CodeAnalysis.Emit.DebugInformationFormat.PortablePdb));
            var errors = r.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            if (errors.Count > 0) throw new Exception($"{name} does not compile:\n" + string.Join("\n", errors.Take(20)));
        }
        return dll;
    }

    /// <summary>A program against the stub CoreLib.</summary>
    public static string Program(string name, params string[] sources) => Compile(name, sources, [CoreLib()]);

    /// <summary>The same program under .NET (the oracle): compiled against the running runtime's assemblies, run in this
    /// process with Console redirected; its output.</summary>
    public static string Oracle(string name, params string[] sources)
    {
        var tpa = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        var dll = Compile(name + ".net", sources, tpa.Where(p => Path.GetFileName(p).StartsWith("System.") || Path.GetFileName(p) == "netstandard.dll" || Path.GetFileName(p) == "mscorlib.dll").ToArray());
        var alc = new System.Runtime.Loader.AssemblyLoadContext(name, isCollectible: true);
        var asm = alc.LoadFromAssemblyPath(dll);
        var old = Console.Out;
        var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            var main = asm.EntryPoint!;
            try { main.Invoke(null, main.GetParameters().Length == 0 ? null : [Array.Empty<string>()]); }
            catch (System.Reflection.TargetInvocationException e) when (e.InnerException != null)
            {
                Console.Out.Flush();
                sw.Write($"Unhandled exception. {e.InnerException.GetType().FullName}: {e.InnerException.Message}\n");
            }
        }
        finally { Console.SetOut(old); alc.Unload(); }
        return sw.ToString().Replace("\r\n", "\n");
    }
}
